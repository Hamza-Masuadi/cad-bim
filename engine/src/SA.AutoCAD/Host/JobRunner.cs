using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Audit;
using SA.AutoCAD.Capture;
using SA.AutoCAD.Ops;
using SA.Core.Checks;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Standards;
using SA.Core.Validation;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Validates and runs jobs (ARCHITECTURE sections 5 and 7).
    ///
    /// Order for run, all on the main thread inside ONE write lock of the job's document (one lock = one undo step,
    /// labelled with the job's undo name):
    ///  1. find the document named by the job (by path, else by fingerprint); it must be the active drawing, writable,
    ///     and its fingerprint must equal the job's (wrong_document / read_only_document / document_changed);
    ///  2. document-level validation: units rule, a handler for every op, every handler's Validate, script approval;
    ///  3. ledger "running" record (commit), then the fingerprint is checked again immediately before the first write;
    ///  4. one top-level transaction; for each op: resolve refs, re-check args against the schema, Execute (which does
    ///     its own readback through the transaction); any exception or failed readback ends the job;
    ///  5. verify.checks run on the facts read through the same transaction, before commit, so a failing check with
    ///     severity error or critical can still roll the job back;
    ///  6. rehearse aborts the transaction; commit commits it;
    ///  7. after commit: every created or modified handle that no op deleted is re-opened in a fresh transaction
    ///     (post-commit readback), then verify.capture runs;
    ///  8. system variables changed by the job are restored in a finally block; a rehearsal then checks that nothing it
    ///     appended survived the rollback and that the material system variables are unchanged; the ledger record is
    ///     finished.
    ///
    /// Status: committed; rolled_back only when nothing changed (a commit refused by a readback mismatch or a blocking
    /// check, or a clean rehearsal); failed otherwise. A rehearsal with a mismatch, a blocking check or a change that
    /// survived its rollback is failed. A script job whose code ran but did not commit is failed with
    /// error.details.scriptChangesRemain true: AutoLISP changes are outside the transaction and stay in the drawing.
    ///
    /// A commit of a script job evaluates the code with no transaction open; the job transaction starts afterwards.
    /// </summary>
    internal sealed class JobRunner
    {
        private readonly HandlerRegistry _handlers;
        private readonly JobLedger _ledger;
        private readonly OpRegistry _schemas;

        internal JobRunner(HandlerRegistry handlers, JobLedger ledger, OpRegistry schemas)
        {
            _handlers = handlers;
            _ledger = ledger;
            _schemas = schemas;
        }

        internal JobLedger Ledger => _ledger;

        /// <summary>Pipe thread. Static validation through Core; throws schema_invalid with the issues.</summary>
        internal Job StaticValidate(JsonObject p, bool requireDocument, out List<Issue> warnings)
        {
            if (!(p["job"] is JsonObject jobJson))
                throw new EngineException(ErrorCodes.BadRequest, "params.job must be the job object.");
            HashSet<string> known;
            try { known = CheckCatalogs.KnownAutoCadIds(p); }
            catch (Exception e) { Log.Error("Loading check catalogs failed.", e); known = new HashSet<string>(); }
            var v = JobValidator.Validate(jobJson, _schemas, new JobValidationOptions { KnownCheckIds = known, RequireDocument = requireDocument });
            if (!v.Ok || v.Job is null)
                throw new EngineException(ErrorCodes.SchemaInvalid, "The job failed static validation; nothing was run.",
                    new JsonObject().Add("issues", Issue.ToJson(v.Issues)));
            if (v.Job.App != AppKind.AutoCad)
                throw new EngineException(ErrorCodes.SchemaInvalid, "The job is for " + AppKinds.ToWire(v.Job.App) + "; this is the AutoCAD engine.");
            warnings = v.Issues.Where(i => !i.IsError).ToList();
            return v.Job;
        }

        // ---------------------------------------------------------------- document binding

        /// <summary>Main thread. Finds the job's document and checks it is the active, writable drawing in the expected state.</summary>
        private static Document Bind(Job job, bool forWrite)
        {
            var dm = AcApp.DocumentManager;
            Document? doc = null;
            string? path = job.Document?.Path;
            string? fingerprint = job.Document?.Fingerprint;
            if (!string.IsNullOrEmpty(path)) doc = DocumentIdentity.FindOpen(path!);
            else if (!string.IsNullOrEmpty(fingerprint)) doc = DocumentIdentity.FindByFingerprint(fingerprint!);
            else doc = dm.MdiActiveDocument;

            var open = new JsonArray();
            foreach (Document d in dm) open.Add(d.Name ?? "");
            var active = dm.MdiActiveDocument;
            if (doc is null)
                throw new EngineException(ErrorCodes.WrongDocument,
                    "The drawing the job names is not open" + (path != null ? ": " + path : " (no open drawing has that fingerprint)") + ".",
                    new JsonObject().Add("expected", path ?? "").Add("open", open).Add("active", active?.Name ?? ""));
            if (!ReferenceEquals(doc, active))
                throw new EngineException(ErrorCodes.WrongDocument,
                    "The drawing '" + doc.Name + "' is open but not the active drawing. Activate it and run again; the engine only works on the active drawing.",
                    new JsonObject().Add("expected", doc.Name ?? "").Add("active", active?.Name ?? ""));
            if (forWrite && doc.IsReadOnly)
                throw new EngineException(ErrorCodes.ReadOnlyDocument, "The drawing '" + doc.Name + "' is open read-only.");
            CheckFingerprint(job, doc);
            return doc;
        }

        private static void CheckFingerprint(Job job, Document doc)
        {
            string? expected = job.Document?.Fingerprint;
            if (string.IsNullOrEmpty(expected)) return;
            string actual = DocumentIdentity.FingerprintOf(doc);
            if (actual != expected)
                throw new EngineException(ErrorCodes.DocumentChanged,
                    "The drawing changed since the snapshot the job was planned from (fingerprint " + expected + ", now " + actual + "). Take a new snapshot.",
                    new JsonObject().Add("expected", expected!).Add("actual", actual).Add("path", doc.Name ?? ""));
        }

        // ---------------------------------------------------------------- validation

        private ProjectStandard? LoadStandard(Job job, Document doc, List<Issue> issues)
        {
            if (job.Standard is null) return null;
            string? path = StandardResolver.Resolve(job.Standard, doc);
            if (path is null)
            {
                issues.Add(new Issue("$.standard", IssueCodes.StandardMissing, "standard file '" + job.Standard + "' was not found (looked in: " +
                    string.Join("; ", StandardResolver.Candidates(job.Standard, doc)) + ")"));
                return null;
            }
            try { return StandardLoader.LoadFile(path); }
            catch (Exception e)
            {
                issues.Add(new Issue("$.standard", IssueCodes.StandardInvalid, "standard '" + path + "' could not be loaded: " + e.Message));
                return null;
            }
        }

        /// <summary>A copy of the args with "$$" escapes turned into "$" and references left as strings, for Validate.</summary>
        private static JsonValue Unescape(JsonValue v)
        {
            switch (v)
            {
                case JsonString s: return s.Value.StartsWith("$$", StringComparison.Ordinal) ? new JsonString(s.Value.Substring(1)) : s;
                case JsonArray a: return new JsonArray(a.Select(Unescape));
                case JsonObject o:
                {
                    var c = new JsonObject();
                    foreach (var kv in o) c.Add(kv.Key, Unescape(kv.Value));
                    return c;
                }
                default: return v;
            }
        }

        private List<Issue> ValidateDocument(OpContext ctx, Job job, RunMode mode, bool allowScript, string fingerprint, bool forValidate = false)
        {
            var issues = new List<Issue>();
            var handlers = new List<IOpHandler?>();
            foreach (var op in job.Ops)
            {
                var h = _handlers.Find(op.Op);
                if (h is null) issues.Add(new Issue(op.Path + ".op", IssueCodes.NoHandler, "op '" + op.Op + "' has no handler in this engine build; nothing in the job was run"));
                handlers.Add(h);
            }
            if (!ctx.Units.IsSupported && !handlers.All(h => h != null && h.AllowsUnitlessDrawing))
            {
                issues.Add(new Issue("$.units", ctx.Units.IsUnitless ? IssueCodes.UnitsUnitless : IssueCodes.UnitsUnsupported,
                    "the drawing's insertion units are " + ctx.Units.Name + " (INSUNITS " + ctx.Units.Code + "); lengths cannot be converted safely. " +
                    "Set INSUNITS first with fix.units or doc.setup in a job of its own"));
            }
            if (issues.Any(i => i.IsError)) return issues;

            for (int i = 0; i < job.Ops.Count; i++)
            {
                var op = job.Ops[i];
                ctx.CurrentOp = op;
                try
                {
                    var args = OpArgs.For(op.Schema, (JsonObject)Unescape(op.Args), job.LengthUnit, op.ArgsPath);
                    handlers[i]!.Validate(ctx, args, issues);
                }
                catch (OpArgsException e)
                {
                    // A field that still holds a "$ref" cannot be read before the referenced op has run. Its document
                    // checks are deferred: Execute re-runs them on the resolved args and fails the op if they fail.
                    var raw = JsonPath.Select(job.Raw, e.Path);
                    if (raw != null && Refs.Find(raw, e.Path).Count > 0)
                        issues.Add(new Issue(e.Path, IssueCodes.RefDeferred, "holds a reference; the op's document checks run at execution instead", IssueSeverity.Warning));
                    else
                        issues.Add(new Issue(e.Path, IssueCodes.Type, e.Message));
                }
                catch (Exception e)
                {
                    Log.Error("Validate of '" + op.Op + "' (" + op.Id + ") threw.", e);
                    issues.Add(new Issue(op.Path, IssueCodes.ValidationError, "the handler could not validate this op: " + e.GetType().Name + ": " + e.Message));
                }
            }

            if (job.Ops.Any(o => o.Schema.RequiresAllowScript))
            {
                if (!allowScript)
                    issues.Add(new Issue("$.ops", IssueCodes.ScriptNotAllowed, "the job contains a script op; send it with allowScript"));
                else if (mode == RunMode.Commit)
                {
                    var r = _ledger.FindRehearsal(job.JobId);
                    string hash = JobLedger.HashJob(job);
                    string? why = r is null ? "it has not been rehearsed"
                        : r.Status != JobStatus.RolledBack || r.Report?["error"] != null ? "its rehearsal did not succeed"
                        : r.JobHash != hash ? "the job changed after its rehearsal"
                        : !r.AllowScript ? "it was rehearsed without allowScript"
                        : r.FingerprintBefore != fingerprint ? "the drawing changed after the rehearsal"
                        : null;
                    if (why != null)
                        issues.Add(new Issue("$.ops", IssueCodes.ScriptNotRehearsed, "a job with script.lisp commits only after an identical rehearsal on the same drawing state: " + why,
                            forValidate ? IssueSeverity.Warning : IssueSeverity.Error));
                }
            }

            for (int i = 0; i < job.Verify.Capture.Count; i++)
            {
                string? problem = CaptureService.CheckSpec(job.Verify.Capture[i]);
                if (problem != null) issues.Add(new Issue(JsonPath.Index("$.verify.capture", i), IssueCodes.CaptureInvalid, problem));
            }
            return issues;
        }

        private OpContext NewContext(Document doc, Job job, RunMode mode, bool allowScript, SysVarScope sysVars, Report report, List<Issue> issues)
        {
            var units = DrawingUnits.Read(doc.Database, job.LengthUnit);
            var standard = LoadStandard(job, doc, issues);
            return new OpContext(doc, job, units, standard, mode, allowScript, sysVars, report);
        }

        /// <summary>Main thread. The validate method: everything up to execution, without any change.</summary>
        internal JsonValue ValidateOnMain(Job job, List<Issue> staticWarnings, bool allowScript)
        {
            var doc = Bind(job, forWrite: false);
            using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var sysVars = new SysVarScope())
            {
                var report = new Report(job.JobId, JobStatus.Unknown, RunMode.Rehearse);
                var issues = new List<Issue>(staticWarnings);
                var ctx = NewContext(doc, job, RunMode.Rehearse, allowScript, sysVars, report, issues);
                string fp = DocumentIdentity.FingerprintOf(doc);
                if (!issues.Any(i => i.IsError)) issues.AddRange(ValidateDocument(ctx, job, RunMode.Commit, allowScript, fp, forValidate: true));
                if (doc.IsReadOnly) issues.Add(new Issue("$.document", IssueCodes.ReadOnly, "the drawing is open read-only; run would be refused"));
                if (issues.Any(i => i.IsError))
                    throw new EngineException(ErrorCodes.ValidationFailed, "The job does not fit the drawing; nothing was run.",
                        new JsonObject().Add("issues", Issue.ToJson(issues)));
                var dup = _ledger.FindCommit(job.JobId);
                return new JsonObject()
                    .Add("ok", true)
                    .Add("jobId", job.JobId)
                    .Add("ops", job.Ops.Count)
                    .Add("document", new JsonObject().Add("path", doc.Name ?? "").Add("fingerprint", fp))
                    .Add("units", ctx.Units.ToJson())
                    .Add("issues", Issue.ToJson(issues))
                    .Add("alreadyCommitted", dup != null);
            }
        }

        // ---------------------------------------------------------------- run

        /// <summary>Main thread. Runs the job and returns its report. Refusals before any change throw EngineException.</summary>
        internal Report RunOnMain(Job job, RunMode mode, bool allowScript, List<Issue> staticWarnings)
        {
            var total = Stopwatch.StartNew();
            var doc = Bind(job, forWrite: true);
            var tracker = DocumentIdentity.Track(doc);
            string fingerprintBefore = tracker.Fingerprint;
            string undoLabel = mode == RunMode.Commit ? job.UndoName : job.UndoName + " (rehearsal)";

            DocumentLock docLock;
            try
            {
                docLock = doc.LockDocument(DocumentLockMode.Write, "SA_JOB", undoLabel, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception e)
            {
                throw new EngineException(ErrorCodes.Busy, "The drawing could not be locked for writing (" + e.ErrorStatus + "); a command may be active.");
            }

            // A rehearsal is rolled back, including the system variables restored at the end, so change counting is
            // suspended from here until the lock is released; RunLocked checks that the rollback was complete.
            if (mode == RunMode.Rehearse) tracker.BeginRehearsal();
            try
            {
                return RunLocked(job, mode, allowScript, staticWarnings, doc, tracker, fingerprintBefore, undoLabel, docLock, total);
            }
            finally
            {
                tracker.EndRehearsal();
            }
        }

        private Report RunLocked(Job job, RunMode mode, bool allowScript, List<Issue> staticWarnings, Document doc, DocumentTracker tracker,
            string fingerprintBefore, string undoLabel, DocumentLock docLock, Stopwatch total)
        {
            var report = new Report(job.JobId, JobStatus.Running, mode);
            report.Validation.AddRange(staticWarnings);
            var sysVars = new SysVarScope();
            LedgerEntry? entry = null;
            OpContext? ctx = null;
            bool committed = false;
            SA.AutoCAD.Capture.VisualService.State? visual = null;
            var materialBefore = mode == RunMode.Rehearse ? MaterialValues() : null;
            try
            {
                var sw = Stopwatch.StartNew();
                var issues = new List<Issue>();
                ctx = NewContext(doc, job, mode, allowScript, sysVars, report, issues);
                if (!issues.Any(i => i.IsError)) issues.AddRange(ValidateDocument(ctx, job, mode, allowScript, fingerprintBefore));
                report.Validation.AddRange(issues);
                report.TimingMs["validate"] = sw.ElapsedMilliseconds;
                if (issues.Any(i => i.IsError))
                    throw new EngineException(ErrorCodes.ValidationFailed, "The job does not fit the drawing; nothing was run.",
                        new JsonObject().Add("issues", Issue.ToJson(report.Validation)));

                var started = new LedgerEntry
                {
                    JobId = job.JobId, Status = JobStatus.Running, Mode = mode, JobHash = JobLedger.HashJob(job),
                    DocumentPath = doc.Name ?? "", FingerprintBefore = fingerprintBefore, AllowScript = allowScript,
                    ProcessId = Process.GetCurrentProcess().Id, StartedAt = DateTimeOffset.Now,
                };
                _ledger.Begin(started);
                entry = started;
                visual = SA.AutoCAD.Capture.VisualService.Before(doc, job, mode);

                // Identity again, immediately before the first write.
                CheckFingerprint(job, doc);
                if (tracker.Fingerprint != fingerprintBefore)
                    throw new EngineException(ErrorCodes.DocumentChanged, "The drawing changed during validation; nothing was run.");

                committed = Execute(ctx, job, doc, report);

                if (committed)
                {
                    PostCommitReadback(doc, report);
                    if (report.Status == JobStatus.Committed && ctx.LayoutAfterCommit != null) SwitchLayoutAfterCommit(ctx.LayoutAfterCommit, report);
                    if (report.Status == JobStatus.Committed) Capture(doc, job, report);
                }
            }
            catch (EngineException e) when (entry is null)
            {
                // Refused before anything ran: no ledger record, the id is not spent.
                sysVars.Dispose();
                docLock.Dispose();
                throw new EngineException(e.Code, e.Message, e.Details, e);
            }
            catch (Exception e)
            {
                Log.Error("Job '" + job.JobId + "' failed outside an op.", e);
                if (report.Status == JobStatus.Running || report.Status == JobStatus.Committed && report.Error is null)
                {
                    bool wasCommitted = report.Status == JobStatus.Committed;
                    report.Status = JobStatus.Failed;
                    report.Error = new OpError(e is EngineException ee ? ee.Code : ErrorCodes.Internal,
                        (wasCommitted ? "The job was committed, then: " : "") + e.GetType().Name + ": " + e.Message);
                }
            }
            finally
            {
                sysVars.Dispose();
                foreach (var f in sysVars.RestoreFailures) report.Warnings.Add(new ReportWarning("engine", f));
            }
            SA.AutoCAD.Capture.VisualService.After(doc, report, visual, committed);

            bool scriptRan = ctx?.ScriptEvaluated == true;
            bool leaked = materialBefore != null && CheckRehearsal(tracker, materialBefore, report);
            if (scriptRan && !committed) ScriptChangesRemain(report, undoLabel);
            report.Committed = committed ? true : scriptRan || leaked ? (bool?)null : false;

            try
            {
                report.Undo = UndoText(report, committed, scriptRan, undoLabel);
                report.TimingMs["total"] = total.ElapsedMilliseconds;
                if (entry != null)
                {
                    entry.Status = report.Status;
                    entry.FingerprintAfter = tracker.Fingerprint;
                    entry.Report = report.ToJson();
                    try { _ledger.Finish(entry); }
                    catch (Exception e)
                    {
                        report.Extra.Set("ledgerIncomplete", true);
                        report.Warnings.Add(new ReportWarning("engine", "The final ledger record could not be written (" + e.Message + "). This report is the only " +
                            "record of the outcome: after an AutoCAD restart, status reports this job as unknown."));
                    }
                }
            }
            finally
            {
                docLock.Dispose();
            }
            return report;
        }

        private bool Execute(OpContext ctx, Job job, Document doc, Report report)
        {
            var db = doc.Database;
            var sw = Stopwatch.StartNew();
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            // AutoLISP opens objects itself; it is evaluated with no transaction of the engine's open (script jobs hold one op).
            bool lateTransaction = !rehearse && job.Ops.Any(o => o.Schema.RequiresAllowScript);
            Transaction? tr = lateTransaction ? null : db.TransactionManager.StartTransaction();
            ctx.SetTransaction(tr);
            // A failure that changed nothing: rolled_back for a commit; failed for a rehearsal, whose success is rolled_back.
            var notApplied = rehearse ? JobStatus.Failed : JobStatus.RolledBack;
            bool committed = false;
            try
            {
                var targets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var op in job.Ops)
                {
                    ctx.CurrentOp = op;
                    var handler = _handlers.Find(op.Op)!;
                    JsonObject resolved;
                    try
                    {
                        resolved = (JsonObject)Refs.Resolve(op.Args, targets, op.ArgsPath);
                    }
                    catch (RefResolutionException e)
                    {
                        Fail(report, op, JobStatus.Failed, ErrorCodes.OpFailed, "Reference could not be resolved: " + e.Message);
                        return false;
                    }
                    var argIssues = JobValidator.ValidateArgs(op.Schema, resolved, job.LengthUnit, op.ArgsPath);
                    if (argIssues.Any(i => i.IsError))
                    {
                        report.Validation.AddRange(argIssues);
                        Fail(report, op, JobStatus.Failed, ErrorCodes.OpFailed, "After resolving references the args are invalid: " + argIssues.First(i => i.IsError));
                        return false;
                    }
                    OpResult result;
                    try
                    {
                        result = handler.Execute(ctx, OpArgs.For(op.Schema, resolved, job.LengthUnit, op.ArgsPath));
                    }
                    catch (Exception e)
                    {
                        Log.Error("Op '" + op.Id + "' (" + op.Op + ") threw.", e);
                        var failed = new OpResult(op.Id, op.Op) { Ok = false, Error = new OpError(e is EngineException ee ? ee.Code : ErrorCodes.OpFailed, e.GetType().Name + ": " + e.Message) };
                        report.Results.Add(failed);
                        Fail(report, op, JobStatus.Failed, e is EngineException ee2 ? ee2.Code : ErrorCodes.OpFailed, "Op '" + op.Id + "' failed: " + e.Message);
                        return false;
                    }
                    report.Results.Add(result);
                    if (!result.Ok)
                    {
                        Fail(report, op, notApplied, rehearse ? ErrorCodes.OpFailed : ErrorCodes.RolledBack,
                            "Op '" + op.Id + "' did not read back as requested: " + (result.Error?.Message ?? "no detail"));
                        return false;
                    }
                    targets[op.Id] = result.ToRefTarget();
                }
                report.TimingMs["execute"] = sw.ElapsedMilliseconds;
                if (tr is null)
                {
                    tr = db.TransactionManager.StartTransaction();
                    ctx.SetTransaction(tr);
                }

                if (job.Verify.Checks.Count > 0)
                {
                    var vsw = Stopwatch.StartNew();
                    var results = AuditService.Verify(doc, tr, job, ctx.Standard, report.Warnings,
                        new HashSet<string>(report.Results.SelectMany(r => r.Created.Concat(r.Modified)), StringComparer.OrdinalIgnoreCase));
                    report.Checks.AddRange(results);
                    report.TimingMs["verify"] = vsw.ElapsedMilliseconds;
                    var blocking = results.Where(r => r.Status == CheckStatus.Fail && r.Severity.HasValue && r.Severity.Value >= Severity.Error).ToList();
                    foreach (var r in results.Where(r => r.Status == CheckStatus.NotImplemented || r.Status == CheckStatus.Error))
                        report.Warnings.Add(new ReportWarning("engine", "Check '" + r.Id + "' was not evaluated (" + CheckResult.StatusToWire(r.Status) + "): " + (r.Message ?? "")));
                    // Incomplete never blocks (owner decision 1); listed first so it is not missed among other warnings.
                    int at = 0;
                    foreach (var r in results.Where(r => r.Status == CheckStatus.Incomplete || r.Incomplete))
                        report.Warnings.Insert(at++, new ReportWarning("engine", r.Status == CheckStatus.Incomplete
                            ? "INCOMPLETE CHECK '" + r.Id + "': it found nothing but saw only part of the drawing, so it is not a pass. " + (r.IncompleteReason ?? r.Message ?? "")
                            : "INCOMPLETE CHECK '" + r.Id + "': its findings are real, but it saw only part of the drawing and may have missed more. " + (r.IncompleteReason ?? r.Message ?? ""))
                        { Code = "check_incomplete", Severity = "warning" });
                    if (blocking.Count > 0)
                    {
                        report.Status = notApplied;
                        report.Error = new OpError(rehearse ? ErrorCodes.OpFailed : ErrorCodes.RolledBack,
                            "Verify check(s) failed: " + string.Join(", ", blocking.Select(b => b.Id)) + "; the job was rolled back.");
                        return false;
                    }
                }

                if (rehearse)
                {
                    report.Status = JobStatus.RolledBack;
                }
                else
                {
                    tr.Commit();
                    committed = true;
                    report.Status = JobStatus.Committed;
                }
            }
            catch (Exception e)
            {
                Log.Error("Job '" + job.JobId + "' failed.", e);
                report.Status = JobStatus.Failed;
                report.FailedOpId = ctx.CurrentOp?.Id;
                report.Error = new OpError(ErrorCodes.OpFailed, e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (tr != null)
                {
                    try
                    {
                        if (!committed) tr.Abort();
                    }
                    catch (Exception e) { Log.Error("Aborting the job transaction failed.", e); }
                    try { tr.Dispose(); } catch (Exception e) { Log.Error("Disposing the job transaction failed.", e); }
                }
                ctx.SetTransaction(null);
            }
            return committed;
        }

        /// <summary>A script job that evaluated code and did not commit: the drawing holds changes; never report rolled_back.</summary>
        private static void ScriptChangesRemain(Report report, string undoLabel)
        {
            string why = report.Error?.Message ?? "the job did not commit";
            report.Status = JobStatus.Failed;
            report.Error = new OpError(ErrorCodes.OpFailed,
                "script.lisp ran and changed the drawing outside the job transaction, then the job did not commit (" + why + "). " +
                "The script's changes remain in the drawing, in the undo step '" + undoLabel + "'; one U removes them. Do not rerun the job before checking the drawing.",
                new JsonObject().Add("scriptChangesRemain", true).Add("cause", why));
        }

        /// <summary>Main thread. The material system variables as invariant strings (the fingerprint counts their changes).</summary>
        private static Dictionary<string, string> MaterialValues()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in DocumentIdentity.MaterialSysVars)
            {
                try { values[name] = Convert.ToString(AcApp.GetSystemVariable(name), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
                catch (Exception) { }
            }
            return values;
        }

        /// <summary>
        /// Main thread, after the rehearsal's abort and system variable restore. An appended object that is still valid
        /// and not erased, or a material system variable that differs from its value before the job, survived the rollback:
        /// the change counter is bumped so the fingerprint shows it, and the rehearsal fails. Returns true on a leak.
        /// Changes to existing objects made outside the transaction are not detected.
        /// </summary>
        private static bool CheckRehearsal(DocumentTracker tracker, Dictionary<string, string> before, Report report)
        {
            var survivors = new List<string>();
            foreach (var id in tracker.RehearsalAppended)
            {
                try
                {
                    if (!id.IsNull && id.IsValid && !id.IsErased) survivors.Add(id.Handle.ToString() + " (" + id.ObjectClass.DxfName + ")");
                }
                catch (Exception) { }
            }
            var after = MaterialValues();
            var changed = before.Where(kv => after.TryGetValue(kv.Key, out var v) && v != kv.Value).Select(kv => kv.Key + " " + kv.Value + " -> " + after[kv.Key]).ToList();
            if (survivors.Count == 0 && changed.Count == 0) return false;
            tracker.ForceBump();
            var details = new JsonObject().Add("rehearsalLeaked", true)
                .Add("objects", JsonArray.FromStrings(survivors.Take(50))).Add("objectCount", survivors.Count)
                .Add("systemVariables", JsonArray.FromStrings(changed));
            if (report.Error != null) details.Add("cause", report.Error.Message);
            report.Status = JobStatus.Failed;
            report.Error = new OpError(ErrorCodes.OpFailed,
                "The rehearsal was rolled back but changes survived it: " +
                (survivors.Count > 0 ? survivors.Count + " object(s) it created are still in the drawing (" + string.Join(", ", survivors.Take(10)) + ")" : "") +
                (survivors.Count > 0 && changed.Count > 0 ? "; " : "") +
                (changed.Count > 0 ? "system variables changed: " + string.Join(", ", changed) : "") +
                ". The drawing fingerprint has changed; take a new snapshot. This is an engine defect: report the job.", details);
            return true;
        }

        private static void Fail(Report report, JobOp op, JobStatus status, string code, string message)
        {
            report.Status = status;
            report.FailedOpId = op.Id;
            report.Error = new OpError(code, message);
        }

        /// <summary>
        /// After commit: every handle the job created or modified must open, not erased, in a fresh transaction, except
        /// handles an op of the job deleted (a later delete wins).
        /// </summary>
        private static void PostCommitReadback(Document doc, Report report)
        {
            var db = doc.Database;
            var missing = new List<string>();
            var deleted = new HashSet<string>(report.Results.SelectMany(r => r.Deleted), StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var r in report.Results)
                {
                    foreach (var h in r.Created.Concat(r.Modified))
                    {
                        if (deleted.Contains(h)) continue;
                        bool ok = false;
                        try
                        {
                            if (db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out ObjectId id) && !id.IsErased)
                                ok = tr.GetObject(id, OpenMode.ForRead, false) != null;
                        }
                        catch (Exception) { ok = false; }
                        if (!ok) missing.Add(r.Id + ":" + h);
                    }
                }
                tr.Commit();
            }
            if (missing.Count > 0)
            {
                report.Status = JobStatus.Failed;
                report.Error = new OpError(ErrorCodes.OpFailed,
                    "The job was committed but " + missing.Count + " object(s) could not be read back afterwards (" + string.Join(", ", missing.Take(20)) +
                    "). Undo the job with U and investigate.");
            }
        }

        /// <summary>
        /// layout.create makeCurrent, after the commit and under the job's document lock (so in its undo step). The job
        /// document is the active one (checked before the run). A failure is a warning: the committed work stands.
        /// </summary>
        private static void SwitchLayoutAfterCommit(string name, Report report)
        {
            try
            {
                var lm = LayoutManager.Current;
                if (!string.Equals(lm.CurrentLayout, name, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = name;
                report.Extra.Set("currentLayout", lm.CurrentLayout);
            }
            catch (Exception e)
            {
                Log.Error("Switching to layout '" + name + "' after the commit failed.", e);
                report.Warnings.Add(new ReportWarning("engine", "The job is committed, but switching to layout '" + name + "' failed (" + e.Message +
                    "); switch to it by hand.") { Code = "layout_switch_failed", Severity = "warning" });
            }
        }

        private static void Capture(Document doc, Job job, Report report)
        {
            if (job.Verify.Capture.Count == 0) return;
            var sw = Stopwatch.StartNew();
            string outDir = job.OutputDir ?? SaPaths.JobOutput(job.JobId);
            for (int i = 0; i < job.Verify.Capture.Count; i++)
            {
                try
                {
                    var arts = CaptureService.Capture(doc, job.Verify.Capture[i], outDir, job.JobId + "-" + (i + 1));
                    report.Artifacts.AddRange(arts);
                }
                catch (Exception e)
                {
                    Log.Error("Capture " + (i + 1) + " of job '" + job.JobId + "' failed.", e);
                    report.Warnings.Add(new ReportWarning("engine", "Capture " + (i + 1) + " failed (the job stays committed): " + e.Message));
                }
            }
            report.TimingMs["capture"] = sw.ElapsedMilliseconds;
        }

        private static string UndoText(Report report, bool committed, bool scriptRan, string undoLabel)
        {
            if (report.Mode == RunMode.Rehearse)
                return report.Committed is null
                    ? "The rehearsal was rolled back, but changes survived it (see error): they are in the step '" + undoLabel + "'; one U removes them."
                    : "Nothing to undo: the rehearsal was rolled back. AutoCAD may list an empty step '" + undoLabel + "'.";
            if (committed)
                return "One undo step for the whole job, labelled '" + undoLabel + "': a single U (or UNDO 1) removes it.";
            if (scriptRan)
                return "script.lisp ran and its changes remain in the drawing (the job did not commit): they are in the step '" + undoLabel + "'; one U removes them.";
            return "Nothing to undo: the job was rolled back before commit.";
        }
    }
}
