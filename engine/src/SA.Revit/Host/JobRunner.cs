using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using SA.Core.Checks;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Standards;
using SA.Core.Validation;
using SA.Revit.Audit;
using SA.Revit.Capture;
using SA.Revit.Compat;
using SA.Revit.Ops;
using SA.Revit.Ops.Support;

namespace SA.Revit.Host
{
    /// <summary>What ping and status show about the job in progress. Replaced (never mutated) so readers on other threads see a consistent value.</summary>
    internal sealed class RunningJob
    {
        public RunningJob(string jobId, string mode, string phase, string? opId, int opIndex, int opCount, DateTime started)
        {
            JobId = jobId; Mode = mode; Phase = phase; OpId = opId; OpIndex = opIndex; OpCount = opCount; Started = started;
        }

        public string JobId { get; }
        public string Mode { get; }
        public string Phase { get; }
        public string? OpId { get; }
        public int OpIndex { get; }
        public int OpCount { get; }
        public DateTime Started { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("jobId", JobId).Add("mode", Mode).Add("phase", Phase).Add("opIndex", OpIndex).Add("opCount", OpCount)
                .Add("startedAt", Started.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            if (OpId != null) o.Add("opId", OpId);
            return o;
        }
    }

    /// <summary>
    /// Runs validate and run requests on Revit's main thread.
    ///
    /// Order for run: bind the document named in the job (wrong_document / document_changed / read_only_document);
    /// preflight-validate every op with its handler (validation_failed); check worksharing ownership of every
    /// declared element (not_owned); re-check the identity; then open ONE TransactionGroup "SA: &lt;title&gt;".
    /// Each op runs in its own Transaction inside the group, so failures, warnings and deletions are attributed to
    /// the op, and readback after each op reads a committed, regenerated model. Then verify checks and captures run,
    /// still inside the group, and the group is assimilated (one undo step) or rolled back (rehearse, or any failure).
    /// Every exception rolls the group back and is reported as failed with the op id.
    /// </summary>
    internal sealed class JobRunner
    {
        private readonly Engine _engine;

        public JobRunner(Engine engine)
        {
            _engine = engine;
        }

        private sealed class Bound
        {
            public Bound(Document doc, DocumentIdentity identity) { Doc = doc; Identity = identity; }
            public Document Doc;
            public DocumentIdentity Identity;
            public List<Issue> Notes = new List<Issue>();
        }

        private sealed class Preflight
        {
            public List<Issue> Issues = new List<Issue>();
            public HashSet<long> Modifies = new HashSet<long>();
            public HashSet<long> Deletes = new HashSet<long>();
            /// <summary>The first op that declared each element, for the ownership list.</summary>
            public Dictionary<long, string> DeclaredBy = new Dictionary<long, string>();
            public bool Ok => Issues.All(i => !i.IsError);
        }

        // ------------------------------------------------------------------ binding

        private Bound? Bind(UIApplication app, string reqId, Job job, bool commit, out Response? failure)
        {
            failure = null;
            var tracker = _engine.Tracker;
            Document? doc;
            var notes = new List<Issue>();
            string? path = job.Document?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                if (commit)
                {
                    failure = Response.Failure(reqId, ErrorCodes.WrongDocument,
                        "A commit job must name document.path and document.fingerprint, copied from the snapshot.");
                    return null;
                }
                doc = app.ActiveUIDocument?.Document;
                if (doc is null)
                {
                    failure = Response.Failure(reqId, ErrorCodes.WrongDocument, "No document is open.");
                    return null;
                }
                notes.Add(new Issue("$.document", IssueCodes.DocumentMissing, "no document.path; validated against the active document '" + doc.Title + "'", IssueSeverity.Warning));
            }
            else
            {
                var matches = tracker.FindByPath(app.Application, path!);
                if (matches.Count != 1)
                {
                    var open = new JsonArray();
                    foreach (Document d in app.Application.Documents)
                        if (!d.IsLinked) open.Add(DocumentTracker.IdentityPath(d));
                    failure = Response.Failure(reqId, ErrorCodes.WrongDocument,
                        matches.Count == 0 ? "The document '" + path + "' is not open in this Revit session." : "Several open documents match '" + path + "'.",
                        new JsonObject().Add("requested", path!).Add("open", open));
                    return null;
                }
                doc = matches[0];
            }

            var identity = tracker.Compute(doc);
            string? fp = job.Document?.Fingerprint;
            if (string.IsNullOrEmpty(fp))
            {
                if (commit)
                {
                    failure = Response.Failure(reqId, ErrorCodes.DocumentChanged,
                        "A commit job must carry document.fingerprint from a snapshot of the current state.",
                        new JsonObject().Add("current", identity.Fingerprint));
                    return null;
                }
                notes.Add(new Issue("$.document.fingerprint", IssueCodes.DocumentMissing, "no fingerprint; the model may have changed since the snapshot", IssueSeverity.Warning));
            }
            else if (!tracker.FingerprintMatches(doc, identity.Fingerprint, fp!))
            {
                failure = Response.Failure(reqId, ErrorCodes.DocumentChanged,
                    "The document changed since the snapshot (or Revit restarted). Take a new snapshot and rebuild the job.",
                    new JsonObject().Add("expected", fp!).Add("current", identity.Fingerprint));
                return null;
            }
            if (doc.IsReadOnly)
            {
                failure = Response.Failure(reqId, ErrorCodes.ReadOnlyDocument, "The document '" + doc.Title + "' is read-only.");
                return null;
            }
            var b = new Bound(doc, identity);
            b.Notes.AddRange(notes);
            return b;
        }

        private static string OutputDirFor(Job job) =>
            !string.IsNullOrWhiteSpace(job.OutputDir) ? job.OutputDir! : SaPaths.JobOutput(job.JobId);

        // ------------------------------------------------------------------ preflight

        /// <summary>
        /// Loads job.standard into the context, resolving a relative path against the model's folder (as the audit method
        /// does). A standard the job names but that cannot be loaded is a validation error: ops and checks would
        /// otherwise run against no standard, or a different one.
        /// </summary>
        private static void LoadStandard(JobContext jc, List<Issue> issues)
        {
            string? path = jc.Job.Standard;
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = AuditLoader.Resolve(path!, AuditService.DocumentFolder(jc.Doc)); }
            catch (Exception e) { issues.Add(new Issue("$.standard", IssueCodes.StandardInvalid, "standard path '" + path + "' is not usable: " + e.Message)); return; }
            if (!File.Exists(full))
            {
                issues.Add(new Issue("$.standard", IssueCodes.StandardMissing, "standard '" + path + "' was not found (looked for " + full +
                    (Path.IsPathRooted(path!) ? "" : "; relative paths resolve against the model's folder") + ")"));
                return;
            }
            if (StandardLoader.TryLoadFile(full, out var standard, out var error)) { jc.Standard = standard; jc.StandardFile = full; }
            else issues.Add(new Issue("$.standard", IssueCodes.StandardInvalid, error ?? ("standard " + full + " could not be loaded")));
        }

        private Preflight RunPreflight(JobContext jc)
        {
            var pf = new Preflight();
            var job = jc.Job;
            if (!string.IsNullOrWhiteSpace(job.OutputDir) && !Path.IsPathRooted(job.OutputDir))
                pf.Issues.Add(new Issue("$.outputDir", RevitIssueCodes.Invalid, "outputDir must be an absolute path"));
            LoadStandard(jc, pf.Issues);
            foreach (var op in job.Ops)
            {
                var h = _engine.Handlers.Find(op.Op);
                if (h is null)
                {
                    pf.Issues.Add(new Issue(op.Path + ".op", RevitIssueCodes.NoHandler,
                        "op '" + op.Op + "' is declared in Core but this Revit engine (" + Compat.ReleaseInfo.Year + ") has no handler for it yet"));
                    continue;
                }
                if (h is IOutsideTransactionOp && job.Ops.Count > 1)
                {
                    pf.Issues.Add(new Issue(op.Path + ".op", JobValidator.OpNotAlone,
                        "'" + op.Op + "' runs outside the job's transactions and must be the only op in its job (" + job.Ops.Count + " ops found)"));
                    continue;
                }
                if (jc.Doc.IsFamilyDocument && !h.AllowsFamilyDocument)
                {
                    pf.Issues.Add(new Issue(op.Path + ".op", RevitIssueCodes.FamilyDocument,
                        "'" + op.Op + "' cannot run in a family document ('" + jc.Doc.Title + "')"));
                    continue;
                }
                var ctx = new OpContext(jc, op, OpPhase.Preflight);
                try
                {
                    var args = OpArgs.For(op.Schema, (JsonObject)OpContext.MarkPending(op.Args), job.LengthUnit, op.ArgsPath);
                    h.Validate(ctx, args, pf.Issues);
                }
                catch (OpArgsException e) { pf.Issues.Add(new Issue(e.Path, IssueCodes.Type, e.Message)); }
                catch (Exception e)
                {
                    Log.Error("Validate threw for " + op.Op, e);
                    pf.Issues.Add(new Issue(op.Path, RevitIssueCodes.Invalid, "validation of '" + op.Op + "' failed: " + e.GetType().Name + ": " + e.Message));
                }
                if (ctx.Deletes.Count > 0 && !op.Schema.Destructive)
                    pf.Issues.Add(new Issue(op.Path, RevitIssueCodes.NotAllowed, "'" + op.Op + "' would delete existing elements but is not a destructive op"));
                pf.Modifies.UnionWith(ctx.Modifies);
                pf.Deletes.UnionWith(ctx.Deletes);
                foreach (long x in ctx.Modifies.Concat(ctx.Deletes)) if (!pf.DeclaredBy.ContainsKey(x)) pf.DeclaredBy[x] = op.Id;
            }
            return pf;
        }

        /// <summary>Ownership problems of every element the preflight declared, each with the op that declared it.</summary>
        private static List<JsonObject> OwnershipProblems(Document doc, Preflight pf)
        {
            var owners = OwnershipCheck.Problems(doc, pf.Modifies.Concat(pf.Deletes));
            foreach (var o in owners)
                if (pf.DeclaredBy.TryGetValue(OwnershipCheck.IdOf(o), out var opId)) o.Set("opId", opId);
            return owners;
        }

        private static JsonObject OwnershipJson(Job job, List<JsonObject> owners) =>
            new JsonObject().Add("mode", OwnershipModes.ToWire(job.Ownership))
                .Add("problems", new JsonArray(owners.Cast<JsonValue>()))
                .Add("note", owners.Count == 0 ? "every element the job would change is editable"
                    : job.Ownership == OwnershipMode.Skip ? "these elements will be left out; each op's readback.ownershipSkipped gives the final list (an op may reach more once references resolve)"
                    : "the job is refused; set ownership to skip to run it without these elements");

        private static JsonObject IssuesDetails(IEnumerable<Issue> issues, DocumentIdentity? id)
        {
            var d = new JsonObject().Add("issues", Issue.ToJson(issues));
            if (id != null) d.Add("document", id.ToJson());
            return d;
        }

        // ------------------------------------------------------------------ validate method

        public Response Validate(UIApplication app, string reqId, Job job)
        {
            var bound = Bind(app, reqId, job, commit: false, out var failure);
            if (bound is null) return failure!;
            var report = new Report(job.JobId, JobStatus.Unknown, RunMode.Rehearse);
            var jc = new JobContext(bound.Doc, job, RunMode.Rehearse, OutputDirFor(job), report);
            var pf = RunPreflight(jc);
            foreach (var still in OutsideDocuments.CloseLeftOpen())
                pf.Issues.Add(new Issue("$.ops", RevitIssueCodes.Unchecked, "a family copy opened to check the job could not be closed: " + still + ". Close it in Revit without saving.", IssueSeverity.Warning));
            var issues = bound.Notes.Concat(pf.Issues).ToList();
            if (!pf.Ok)
                return Response.Failure(reqId, ErrorCodes.ValidationFailed, pf.Issues.Count(i => i.IsError) + " issue(s) against the live document.",
                    IssuesDetails(issues, bound.Identity));
            var owners = OwnershipProblems(bound.Doc, pf);
            if (owners.Count > 0 && job.Ownership != OwnershipMode.Skip)
                return Response.Failure(reqId, ErrorCodes.NotOwned, owners.Count + " element(s) the job would change are not editable by you.",
                    new JsonObject().Add("elements", new JsonArray(owners.Cast<JsonValue>())).Add("ownership", OwnershipJson(job, owners)).Add("document", bound.Identity.ToJson()));
            return Response.Success(reqId, new JsonObject()
                .Add("ok", true)
                .Add("jobId", job.JobId)
                .Add("issues", Issue.ToJson(issues))
                .Add("document", bound.Identity.ToJson())
                .Add("ownership", OwnershipJson(job, owners))
                .Add("modifiesExisting", JsonArray.FromStrings(pf.Modifies.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                .Add("deletesExisting", JsonArray.FromStrings(pf.Deletes.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }

        // ------------------------------------------------------------------ run method

        private sealed class ChangeSink
        {
            public List<long> Deleted = new List<long>();
            public int Events;
            /// <summary>Set for ownership "skip": modified ids are recorded only then.</summary>
            public bool TrackModified;
            public List<long> Modified = new List<long>();

            public void OnChanged(DocumentChangedEventArgs e)
            {
                Events++;
                try { foreach (var id in e.GetDeletedElementIds()) Deleted.Add(ElementIds.ToLong(id)); } catch (Exception) { }
                if (TrackModified) try { foreach (var id in e.GetModifiedElementIds()) Modified.Add(ElementIds.ToLong(id)); } catch (Exception) { }
            }
        }

        private sealed class JobFailure : Exception
        {
            public JobFailure(string code, string message, bool readback, JsonObject? details = null) : base(message)
            {
                Code = code;
                Readback = readback;
                Details = details;
            }

            public string Code { get; }
            /// <summary>The model did what was asked but the result is wrong (readback or checks): rolled_back.</summary>
            public bool Readback { get; }
            public JsonObject? Details { get; }
        }

        /// <summary>
        /// The dialog guard covers the whole request, preflight included: handlers' Validate can call Revit in ways that
        /// show a dialog (family.edit opens the family), and an unguarded dialog blocks Revit's main thread.
        /// </summary>
        public Response Run(UIApplication app, string reqId, Job job, RunMode mode, JsonObject p)
        {
            using (var dialogs = new DialogGuard(app))
                return Engine.AttachDialogs(Run(app, reqId, job, mode, p, dialogs), dialogs, okToo: false);
        }

        private Response Run(UIApplication app, string reqId, Job job, RunMode mode, JsonObject p, DialogGuard dialogs)
        {
            bool commit = mode == RunMode.Commit;
            string modeWire = JobStatuses.ToWire(mode);
            var total = Stopwatch.StartNew();
            _engine.Current = new RunningJob(job.JobId, modeWire, "validating", null, 0, job.Ops.Count, DateTime.UtcNow);
            // Repeated here on the main thread, where runs are serialised: two identical requests can both pass the
            // check on the pipe threads.
            if (commit && _engine.Ledger.IsUsed(job.JobId, out var usedRecord)) return Engine.DuplicateJob(reqId, job.JobId, usedRecord);
            // Verify checks: the job's verify.checksFile / checksMode, plus any checks file or standard the request names.
            var verifyOptions = job.Verify.ToAuditOptions();
            try
            {
                var fromParams = AuditOptions.FromParams(p);
                verifyOptions.ChecksFiles.AddRange(fromParams.ChecksFiles);
                verifyOptions.AddToBundled |= fromParams.AddToBundled;
                verifyOptions.StandardPath = fromParams.StandardPath;
            }
            catch (FormatException e) { return Response.Failure(reqId, ErrorCodes.BadRequest, e.Message); }

            var bound = Bind(app, reqId, job, commit, out var failure);
            if (bound is null) return failure!;
            var doc = bound.Doc;
            var report = new Report(job.JobId, JobStatus.Running, mode);
            report.Validation.AddRange(bound.Notes);
            var jc = new JobContext(doc, job, mode, OutputDirFor(job), report);
            bool failOnWarnings = p["failOnWarnings"] is JsonBool fw && fw.Value;

            var pf = RunPreflight(jc);
            foreach (var still in OutsideDocuments.CloseLeftOpen())
                pf.Issues.Add(new Issue("$.ops", RevitIssueCodes.Invalid, "a family copy opened to check the job could not be closed: " + still + "; nothing ran. Close it in Revit without saving."));
            if (dialogs.Dialogs.Count > 0)
                pf.Issues.Add(new Issue("$.ops", RevitIssueCodes.Invalid, "Revit showed " + dialogs.Dialogs.Count + " dialog(s) while the ops were being checked; they were dismissed (see dialogs) and the checks are not trusted"));
            AuditSetup? verify = null;
            if (job.Verify.Checks.Count > 0)
            {
                verify = _engine.Audit.Load(verifyOptions, doc, job.Standard, out string source);
                if (verify.Standard is null && (source == "job" || source == "explicit"))
                    pf.Issues.Add(new Issue(source == "job" ? "$.standard" : "$.params.standard", IssueCodes.StandardInvalid,
                        "the verify checks need the named standard and it did not load: " + string.Join("; ", verify.Problems)));
            }
            report.Validation.AddRange(pf.Issues);
            report.TimingMs["validate"] = total.ElapsedMilliseconds;
            if (!pf.Ok)
                return Response.Failure(reqId, ErrorCodes.ValidationFailed, pf.Issues.Count(i => i.IsError) + " issue(s) against the live document; nothing ran.",
                    IssuesDetails(report.Validation, bound.Identity));
            var owners = OwnershipProblems(doc, pf);
            if (owners.Count > 0 && job.Ownership != OwnershipMode.Skip)
                return Response.Failure(reqId, ErrorCodes.NotOwned, owners.Count + " element(s) the job would change are not editable by you; nothing ran.",
                    new JsonObject().Add("elements", new JsonArray(owners.Cast<JsonValue>())).Add("ownership", OwnershipJson(job, owners)).Add("document", bound.Identity.ToJson()));
            foreach (var o in owners) jc.NotEditable[OwnershipCheck.IdOf(o)] = o;
            if (owners.Count > 0)
                report.Validation.Add(new Issue("$.ownership", OwnershipSkip.WarningCode, owners.Count + " element(s) the job would change are not editable by you and will be left out (ownership: skip); " +
                    "each op's readback.ownershipSkipped lists them", IssueSeverity.Warning));
            var checkedOwnership = new HashSet<long>(pf.Modifies.Concat(pf.Deletes));
            if (verify != null) foreach (var e in verify.Problems) report.Warnings.Add(new ReportWarning("engine", e));

            // Identity again, immediately before the first change.
            var before = _engine.Tracker.Compute(doc);
            if (before.Fingerprint != bound.Identity.Fingerprint)
                return Response.Failure(reqId, ErrorCodes.DocumentChanged, "The document changed during validation; nothing ran.",
                    new JsonObject().Add("expected", bound.Identity.Fingerprint).Add("current", before.Fingerprint));

            // A job whose ops only read or export (ModifiesModel false) runs without a transaction group: there is
            // nothing to undo, and Revit's view exports are not run while a group is open.
            bool needsGroup = job.Ops.Any(o => _engine.Handlers.Find(o.Op)?.ModifiesModel ?? true);

            _engine.Ledger.MarkRunning(job.JobId, modeWire);
            var sink = new ChangeSink { TrackModified = job.Ownership == OwnershipMode.Skip && doc.IsWorkshared };
            _engine.Tracker.Suspend(doc, sink.OnChanged);
            TransactionGroup? group = null;
            VisualService.State? visual = null;
            bool committed = false;
            var targets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var exec = Stopwatch.StartNew();
            // Dialogs during the verify.visual captures are reported but do not fail the job: the visual check never does.
            int visualDialogs = 0;
            try
            {
                int d0 = dialogs.Dialogs.Count;
                visual = VisualService.Before(jc, pf.Modifies.Concat(pf.Deletes));
                visualDialogs += dialogs.Dialogs.Count - d0;
                if (needsGroup)
                {
                    group = new TransactionGroup(doc, job.UndoName);
                    if (group.Start() != TransactionStatus.Started) throw new JobFailure(ErrorCodes.OpFailed, "Revit refused to start the transaction group.", false);
                }

                for (int i = 0; i < job.Ops.Count; i++)
                {
                    var op = job.Ops[i];
                    _engine.Current = new RunningJob(job.JobId, modeWire, "execute", op.Id, i, job.Ops.Count, DateTime.UtcNow);
                    RunOp(jc, op, targets, checkedOwnership, sink, dialogs, failOnWarnings);
                }
                report.TimingMs["execute"] = exec.ElapsedMilliseconds;

                var verifyClock = Stopwatch.StartNew();
                _engine.Current = new RunningJob(job.JobId, modeWire, "verify", null, job.Ops.Count, job.Ops.Count, DateTime.UtcNow);
                if (verify != null) RunChecks(jc, verify);
                report.TimingMs["verify"] = verifyClock.ElapsedMilliseconds;
                var cap = Stopwatch.StartNew();
                RunCaptures(jc);
                report.TimingMs["capture"] = cap.ElapsedMilliseconds;
                int d1 = dialogs.Dialogs.Count;
                VisualService.After(jc, visual);
                visualDialogs += dialogs.Dialogs.Count - d1;
                if (visualDialogs > 0)
                    report.Warnings.Add(new ReportWarning("engine", "Revit showed " + visualDialogs + " dialog(s) while exporting the verify.visual images; they were dismissed (see dialogs) and the visual check may be incomplete."));
                if (dialogs.Dialogs.Count - visualDialogs > 0)
                    throw new JobFailure(ErrorCodes.OpFailed, "Revit showed " + (dialogs.Dialogs.Count - visualDialogs) + " dialog(s) during the job; they were dismissed and the job is not trusted.", false);

                if (commit)
                {
                    if (group != null)
                    {
                        var st = group.Assimilate();
                        if (st != TransactionStatus.Committed) throw new JobFailure(ErrorCodes.OpFailed, "Assimilating the transaction group returned " + st + ".", false);
                        committed = true;
                        report.Undo = jc.SkippedOps.Count == job.Ops.Count
                            ? "Nothing to undo: every op was skipped (ownership: skip)."
                            : "One undo step named '" + job.UndoName + "'";
                    }
                    else report.Undo = "Nothing to undo: no op of the job changes the model.";
                    report.Status = JobStatus.Committed;
                }
                else
                {
                    group?.RollBack();
                    report.Status = JobStatus.RolledBack;
                    report.Undo = "Rehearsal: everything was rolled back; the model is unchanged.";
                }
            }
            catch (Exception e)
            {
                var jf = e as JobFailure;
                if (jf is null) Log.Error("Job " + job.JobId + " failed", e);
                SafeRollback(group);
                report.Status = commit && jf != null && jf.Readback ? JobStatus.RolledBack : JobStatus.Failed;
                report.Error = new OpError(jf?.Code ?? ErrorCodes.OpFailed, jf?.Message ?? (e.GetType().Name + ": " + e.Message), jf?.Details);
                if (report.FailedOpId is null && _engine.Current?.Phase == "execute") report.FailedOpId = _engine.Current.OpId;
                report.Undo = "Nothing was changed: the job was rolled back.";
            }
            finally
            {
                SafeRollback(group);
                try { group?.Dispose(); } catch (Exception) { }
                foreach (var still in OutsideDocuments.CloseLeftOpen())
                    report.Warnings.Add(new ReportWarning("engine", "A family copy the job opened for editing could not be closed: " + still + ". Close it in Revit without saving."));
                _engine.Tracker.Resume(doc, committed, before.Fingerprint);
                // Current is cleared by the engine once this method has returned, after the ledger holds the report,
                // so status never sees a gap between "running" and the final record.
                _engine.Current = new RunningJob(job.JobId, modeWire, "finishing", null, job.Ops.Count, job.Ops.Count, DateTime.UtcNow);
            }
            VisualService.Finish(jc, visual);

            if (commit && report.Status == JobStatus.Committed) PublishStaged(jc, committed);
            DiscardStaged(jc);
            report.Committed = committed || report.Status == JobStatus.Committed;
            if (!report.TimingMs.ContainsKey("execute")) report.TimingMs["execute"] = exec.ElapsedMilliseconds;
            report.TimingMs["total"] = total.ElapsedMilliseconds;
            var after = _engine.Tracker.Compute(doc);
            report.Document = new ReportDocument(after.Path, after.Fingerprint, before.Fingerprint);
            if (dialogs != null) foreach (var d in dialogs.Dialogs) report.Dialogs.Add(d.ToReport());
            report.Extra.Set("engine", new JsonObject().Add("release", Compat.ReleaseInfo.Year).Add("version", Engine.EngineVersion));
            var json = report.ToJson();
            _engine.Ledger.Finish(job.JobId, modeWire, json);
            return Response.Success(reqId, json);
        }

        // ------------------------------------------------------------------ files written outside the model

        /// <summary>
        /// After the job has committed: moves every staged file to its final path. All preconditions are checked
        /// before anything is moved (an existing file without overwrite, or a file that changed since it was staged), so
        /// a refusal leaves every final path as it was. A file replaced with overwrite is moved aside first and restored
        /// if the move fails. The model stays committed whatever happens here; a file that could not be published makes
        /// the job failed with committed true, and its staged copy is kept and named in the error.
        /// </summary>
        private static void PublishStaged(JobContext jc, bool modelChanged)
        {
            var report = jc.Report;
            if (jc.Staged.Count == 0) return;
            var problems = new List<KeyValuePair<StagedFile, string>>();
            var earlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refused = new HashSet<StagedFile>();
            foreach (var s in jc.Staged)
            {
                string? why = null;
                bool again = !earlier.Add(s.Final);
                if (!File.Exists(s.Staged)) why = "the staged file is missing";
                else if (again && !s.Overwrite) why = "an earlier op of the job writes the same file and overwrite is false";
                else if (File.Exists(s.Final))
                {
                    if (!s.Overwrite) why = "the file appeared while the job ran and overwrite is false";
                    else if (s.FinalWriteUtc.HasValue || s.FinalLength.HasValue)
                    {
                        var fi = new FileInfo(s.Final);
                        if ((s.FinalWriteUtc.HasValue && fi.LastWriteTimeUtc != s.FinalWriteUtc.Value) || (s.FinalLength.HasValue && fi.Length != s.FinalLength.Value))
                            why = "the file was changed by someone else while the job ran";
                    }
                }
                else if (s.FinalWriteUtc.HasValue) why = "the file was removed while the job ran";
                if (why != null) { problems.Add(new KeyValuePair<StagedFile, string>(s, why)); refused.Add(s); }
            }
            if (problems.Count > 0)
            {
                foreach (var s in jc.Staged.Where(f => !refused.Contains(f))) problems.Add(new KeyValuePair<StagedFile, string>(s, "not put in place because another file of the job was refused"));
            }
            else
            {
                foreach (var s in jc.Staged)
                {
                    try { Publish(s); }
                    catch (Exception e) { problems.Add(new KeyValuePair<StagedFile, string>(s, e.GetType().Name + ": " + e.Message)); }
                }
            }
            if (problems.Count == 0) return;
            foreach (var x in problems) x.Key.Kept = true;
            var unpublished = new HashSet<string>(problems.Select(x => x.Key.Final), StringComparer.OrdinalIgnoreCase);
            report.Artifacts.RemoveAll(a => unpublished.Contains(a.Path));
            report.Status = JobStatus.Failed;
            report.Error = new OpError(ErrorCodes.OpFailed,
                (modelChanged ? "The model change is committed (one undo step), but " : "The model was not changed, but ") + problems.Count + " output file(s) could not be put in place: " +
                string.Join("; ", problems.Take(3).Select(x => x.Key.Final + ": " + x.Value)) +
                ". The files Revit wrote are kept at the staged paths listed in details.",
                new JsonObject().Add("files", new JsonArray(problems.Select(x => (JsonValue)Problem(x.Key, x.Value)))));
            report.FailedOpId ??= problems[0].Key.OpId;
        }

        private static JsonObject Problem(StagedFile s, string why) =>
            new JsonObject().Add("opId", s.OpId).Add("final", s.Final).Add("staged", s.Staged).Add("reason", why);

        private static void Publish(StagedFile s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s.Final)!);
            if (s.CopyOver) { File.Copy(s.Staged, s.Final, true); return; }
            if (!File.Exists(s.Final)) { File.Move(s.Staged, s.Final); return; }
            string aside = s.Final + ".sa-old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.Move(s.Final, aside);
            try { File.Move(s.Staged, s.Final); }
            catch
            {
                try { File.Move(aside, s.Final); } catch (Exception e) { Log.Error("Could not restore " + s.Final + " from " + aside, e); }
                throw;
            }
            try { File.Delete(aside); } catch (Exception e) { Log.Warn("Could not delete " + aside + ": " + e.Message); }
        }

        /// <summary>Deletes the staging folders, except one still holding a file that could not be published.</summary>
        private static void DiscardStaged(JobContext jc)
        {
            var keep = new HashSet<string>(jc.Staged.Where(s => s.Kept).Select(s => s.Staged), StringComparer.OrdinalIgnoreCase);
            foreach (var dir in jc.StageFolders)
            {
                if (keep.Any(k => k.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception e) { jc.Report.Warnings.Add(new ReportWarning("engine", "Could not delete the staging folder '" + dir + "': " + e.Message)); }
            }
            {
                string temp = Path.GetTempPath();
                try
                {
                    var parents = jc.StageFolders.Where(d => d.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                        .Select(Path.GetDirectoryName).Where(d => d != null).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var d in parents)
                        if (Directory.Exists(d!) && !Directory.EnumerateFileSystemEntries(d!).Any()) Directory.Delete(d!);
                }
                catch (Exception) { }
            }
        }

        private static void SafeRollback(TransactionGroup? g)
        {
            if (g is null) return;
            try { if (g.HasStarted() && !g.HasEnded()) g.RollBack(); }
            catch (Exception e) { Log.Error("Transaction group rollback failed", e); }
        }

        private void RunOp(JobContext jc, JobOp op, Dictionary<string, JsonObject> targets, HashSet<long> checkedOwnership,
            ChangeSink sink, DialogGuard dialogs, bool failOnWarnings)
        {
            var doc = jc.Doc;
            var report = jc.Report;
            var h = _engine.Handlers.Find(op.Op) ?? throw Fail(jc, op, "no handler for '" + op.Op + "'", false);
            bool skipMode = jc.Job.Ownership == OwnershipMode.Skip;
            if (skipMode && jc.SkippedOps.Count > 0 &&
                Refs.Find(op.Args, op.ArgsPath).FirstOrDefault(r => Refs.TryParse(r.Value, out var x, out _) && jc.SkippedOps.Contains(x!.OpId)) is var dep && dep.Value != null)
            {
                SkipOp(jc, op, targets, "it uses the result of '" + dep.Value + "', an op that did not run", new JsonArray());
                return;
            }

            JsonObject resolved;
            try { resolved = (JsonObject)Refs.Resolve(op.Args, targets, op.ArgsPath); }
            catch (RefResolutionException e) { throw Fail(jc, op, e.Message, false); }
            var argErrors = JobValidator.ValidateArgs(op.Schema, resolved, jc.Job.LengthUnit, op.ArgsPath).Where(x => x.IsError).ToList();
            if (argErrors.Count > 0) throw Fail(jc, op, "after resolving references: " + string.Join("; ", argErrors.Take(5).Select(x => x.ToString())), false);

            var args = OpArgs.For(op.Schema, resolved, jc.Job.LengthUnit, op.ArgsPath);
            var ctx = new OpContext(jc, op, OpPhase.Execute);
            var issues = new List<Issue>();
            try { h.Validate(ctx, args, issues); }
            catch (OpArgsException e) { issues.Add(new Issue(e.Path, IssueCodes.Type, e.Message)); }
            var errs = issues.Where(x => x.IsError).ToList();
            if (errs.Count > 0) throw Fail(jc, op, "validation with resolved references failed: " + string.Join("; ", errs.Take(5).Select(x => x.Path + ": " + x.Message)), false,
                new JsonObject().Add("issues", Issue.ToJson(errs)));
            foreach (var w in issues.Where(x => !x.IsError)) report.Warnings.Add(new ReportWarning("engine", w.Path + ": " + w.Message, op.Id));

            // Elements first declared now (filters can match more once references resolve): check them too.
            var fresh = ctx.Modifies.Concat(ctx.Deletes).Where(x => !jc.Created.Contains(x) && !checkedOwnership.Contains(x)).ToList();
            if (fresh.Count > 0)
            {
                var owners = OwnershipCheck.Problems(doc, fresh);
                if (owners.Count > 0 && !skipMode) throw new JobFailure(ErrorCodes.NotOwned, owners.Count + " element(s) op '" + op.Id + "' would change are not editable by you.", false,
                    new JsonObject().Add("elements", new JsonArray(owners.Cast<JsonValue>())));
                foreach (var o in owners) jc.NotEditable[OwnershipCheck.IdOf(o)] = o;
                checkedOwnership.UnionWith(fresh);
            }
            JsonArray? leftOut = null;
            var declared = ctx.Modifies.Concat(ctx.Deletes).Where(x => !jc.Created.Contains(x)).ToList();
            if (skipMode && declared.Any(jc.NotEditable.ContainsKey))
            {
                var narrowed = OwnershipSkip.Narrow(jc, op, h, resolved, declared);
                leftOut = OwnershipSkip.Entries(jc, op.Id, narrowed.LeftOut);
                OwnershipSkip.Warn(jc, op.Id, leftOut);
                if (narrowed.SkipReason is null)
                {
                    var again = narrowed.Ctx!.Modifies.Concat(narrowed.Ctx.Deletes).Where(x => !jc.Created.Contains(x) && !checkedOwnership.Contains(x)).ToList();
                    var more = OwnershipCheck.Problems(doc, again);
                    foreach (var o in more) jc.NotEditable[OwnershipCheck.IdOf(o)] = o;
                    checkedOwnership.UnionWith(again);
                    if (more.Count > 0) narrowed.SkipReason = "with elements left out it reaches " + more.Count + " other element(s) that are not editable";
                }
                if (narrowed.SkipReason != null) { SkipOp(jc, op, targets, narrowed.SkipReason, leftOut); return; }
                args = OpArgs.For(op.Schema, narrowed.Args, jc.Job.LengthUnit, op.ArgsPath);
                ctx = narrowed.Ctx!;
            }
            if (!op.Schema.Destructive && ctx.Deletes.Any(x => !jc.Created.Contains(x)))
                throw Fail(jc, op, "'" + op.Op + "' would delete existing elements but is not a destructive op", false);

            int dialogsBefore = dialogs.Dialogs.Count;
            int deletedBefore = sink.Deleted.Count;
            int eventsBefore = sink.Events;
            int modifiedBefore = sink.Modified.Count;
            bool outside = h is IOutsideTransactionOp;
            OpResult result;
            if (h.ModifiesModel && !outside)
            {
                var collector = new FailureCollector();
                using (var t = new Transaction(doc, "SA " + op.Id + ": " + op.Op))
                {
                    if (t.Start() != TransactionStatus.Started) throw Fail(jc, op, "Revit refused to start a transaction", false);
                    collector.Install(t);
                    try
                    {
                        result = h.Execute(ctx, args);
                    }
                    catch (Exception e)
                    {
                        try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch (Exception) { }
                        throw FromHandler(jc, op, e);
                    }
                    var status = t.Commit();
                    foreach (var w in collector.Warnings) report.Warnings.Add(new ReportWarning("app", w.Text(), op.Id));
                    if (status != TransactionStatus.Committed || collector.Errors.Count > 0)
                    {
                        try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch (Exception) { }
                        var details = new JsonObject().Add("transactionStatus", status.ToString())
                            .Add("errors", new JsonArray(collector.Errors.Select(x => (JsonValue)x.ToJson())));
                        throw Fail(jc, op, "Revit rolled the op back (" + status + "): " +
                            (collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Take(3).Select(x => x.Text())) : "no failure message"), false, details);
                    }
                    if (failOnWarnings && collector.Warnings.Count > 0)
                        throw Fail(jc, op, collector.Warnings.Count + " warning(s) and the run asked to fail on warnings: " +
                            string.Join("; ", collector.Warnings.Take(3).Select(x => x.Text())), true);
                }
            }
            else
            {
                if (outside && doc.IsModifiable) throw Fail(jc, op, "the document has an open transaction, and this op runs only outside one", false);
                try { result = h.Execute(ctx, args); }
                catch (Exception e) { throw FromHandler(jc, op, e); }
                if (outside && doc.IsModifiable) throw Fail(jc, op, "the op left a transaction open in the document", false);
            }

            if (dialogs.Dialogs.Count > dialogsBefore)
                throw Fail(jc, op, "Revit showed a dialog during the op: " + string.Join("; ", dialogs.Dialogs.Skip(dialogsBefore).Select(d => d.Kind + " " + d.DialogId + " " + d.Message)), false);

            // Deletion rule (safety rule 7), enforced on what Revit actually deleted, not only on what was declared.
            if (h.ModifiesModel)
            {
                var deleted = sink.Deleted.Skip(deletedBefore).Distinct().ToList();
                var allowed = new HashSet<long>(jc.Created);
                if (op.Schema.Destructive) allowed.UnionWith(ctx.Deletes);
                var illegal = deleted.Where(x => !allowed.Contains(x)).ToList();
                if (illegal.Count > 0)
                    throw Fail(jc, op, "the op deleted " + illegal.Count + " existing element(s) it was not allowed to delete", false,
                        new JsonObject().Add("deleted", JsonArray.FromStrings(illegal.Take(200).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
                if (sink.Events == eventsBefore && (!outside || (jc.Mode == RunMode.Commit && result.Created.Count + result.Modified.Count > 0)))
                    report.Warnings.Add(new ReportWarning("engine", "No DocumentChanged event arrived for this op's transaction; the deletion guard had no data for it.", op.Id));
                if (sink.TrackModified && jc.NotEditable.Count > 0)
                {
                    var touched = sink.Modified.Skip(modifiedBefore).Where(jc.NotEditable.ContainsKey).Distinct().ToList();
                    if (touched.Count > 0)
                        report.Warnings.Add(new ReportWarning("engine", "Revit changed " + touched.Count + " element(s) that were left out as not editable, as a side effect of this op " +
                            "(joins, hosted or dependent elements); Revit's own ownership rules apply to them when the change is saved to central", op.Id)
                        { Code = OwnershipSkip.WarningCode });
                }
            }

            foreach (var c in result.Created) if (ElementIds.TryParse(c, out var cid)) jc.Created.Add(ElementIds.ToLong(cid));

            try { h.Readback(ctx, args, result); }
            catch (Exception e) { throw FromHandler(jc, op, e, readback: true); }
            if (leftOut != null) result.Readback.Set("ownershipSkipped", leftOut);
            report.Results.Add(result);
            jc.Results[op.Id] = result;
            if (!result.Ok)
            {
                report.FailedOpId = op.Id;
                throw new JobFailure(ErrorCodes.RolledBack, "op '" + op.Id + "' (" + op.Op + "): " + (result.Error?.Message ?? "readback did not confirm the change"), true,
                    result.Error?.Details);
            }
            targets[op.Id] = result.ToRefTarget();
        }

        /// <summary>
        /// Ownership "skip": records an op that does not run because it was left with nothing it may change. Its list
        /// and map outputs are empty, so later whole-list references resolve; a later op that refers to it is skipped too.
        /// </summary>
        private static void SkipOp(JobContext jc, JobOp op, Dictionary<string, JsonObject> targets, string why, JsonArray leftOut)
        {
            var r = new OpResult(op.Id, op.Op) { Ok = true };
            r.Readback.Add("skipped", true).Add("reason", why).Add("ownershipSkipped", leftOut);
            foreach (var o in op.Schema.Outputs)
            {
                if (o.Type == FieldType.List) r.Outputs.Add(o.Name, new JsonArray());
                else if (o.Type == FieldType.Map) r.Outputs.Add(o.Name, new JsonObject());
            }
            jc.Report.Results.Add(r);
            jc.Results[op.Id] = r;
            jc.SkippedOps.Add(op.Id);
            targets[op.Id] = r.ToRefTarget();
            jc.Report.Warnings.Add(new ReportWarning("engine", "op '" + op.Id + "' (" + op.Op + ") did not run (ownership: skip): " + why, op.Id)
                { Code = OwnershipSkip.WarningCode, Severity = "warning" });
        }

        private static JobFailure Fail(JobContext jc, JobOp op, string message, bool readback, JsonObject? details = null)
        {
            var r = new OpResult(op.Id, op.Op) { Ok = false, Error = new OpError(ErrorCodes.OpFailed, message, details) };
            jc.Report.Results.Add(r);
            jc.Report.FailedOpId = op.Id;
            return new JobFailure(readback ? ErrorCodes.RolledBack : ErrorCodes.OpFailed, "op '" + op.Id + "' (" + op.Op + "): " + message, readback, details);
        }

        private static JobFailure FromHandler(JobContext jc, JobOp op, Exception e, bool readback = false)
        {
            if (e is OpFailedException of) return Fail(jc, op, of.Message, readback, of.Details);
            if (e is OpArgsException oa) return Fail(jc, op, oa.Message, false);
            Log.Error("Handler " + op.Op + " threw", e);
            return Fail(jc, op, e.GetType().Name + ": " + e.Message, readback);
        }

        // ------------------------------------------------------------------ verify

        /// <summary>
        /// The job's verify checks, with the checks and standard <see cref="AuditService.Load"/> found before anything
        /// ran. Only the fact tables the chosen checks read are collected; a check whose inputs Core cannot list (an
        /// evaluator only this engine defines) makes every table collected.
        /// </summary>
        private void RunChecks(JobContext jc, AuditSetup setup)
        {
            var ids = jc.Job.Verify.Checks;
            if (ids.Count == 0) return;
            var audit = _engine.Audit;
            var needs = CheckInputs.Needs(setup.Catalog.Select(CheckApp.Revit, ids, null, out _));
            HashSet<string>? tables = needs.Everything ? null : new HashSet<string>(needs.Tables, StringComparer.Ordinal);
            // An empty set means "every table" to Collect; checks that read no table get the cheapest one.
            if (tables != null && tables.Count == 0) tables.Add("document");
            var problems = new List<string>();
            var scope = new HashSet<long>(jc.Created);
            foreach (var m in jc.Report.Results.SelectMany(r => r.Modified)) if (ElementIds.TryParse(m, out var mid)) scope.Add(ElementIds.ToLong(mid));
            var facts = audit.Collect(jc.Doc, jc.Units, tables, needs.Everything ? null : needs, scope);
            var results = audit.Run(facts, setup.Catalog, ids, null, setup.Standard, problems);
            foreach (var e in problems) jc.Report.Warnings.Add(new ReportWarning("engine", e));
            jc.Report.Checks.AddRange(results);
            var bad = results.Where(r => r.Status == CheckStatus.Error ||
                                         (r.Status == CheckStatus.Fail && (r.Severity ?? Severity.Error) >= Severity.Error)).ToList();
            if (bad.Count > 0)
                throw new JobFailure(ErrorCodes.RolledBack, bad.Count + " verify check(s) failed: " + string.Join(", ", bad.Select(b => b.Id)), true);
            foreach (var r in results.Where(r => r.Status == CheckStatus.NotImplemented))
                jc.Report.Warnings.Add(new ReportWarning("engine", "check '" + r.Id + "' is not implemented: " + r.Message));
            foreach (var r in results.Where(r => r.Status == CheckStatus.Incomplete))
                jc.Report.Warnings.Add(new ReportWarning("engine", "check '" + r.Id + "' is incomplete, so it cannot pass: " + (r.IncompleteReason ?? r.Message)));
        }

        private static void RunCaptures(JobContext jc)
        {
            int n = 0;
            foreach (var c in jc.Job.Verify.Capture)
            {
                n++;
                string target = c.GetStringOrNull("target") ?? "activeView";
                try
                {
                    View? view = target == "activeView" ? jc.Doc.ActiveView : Lookup.View(jc.Doc, c["target"]).Value;
                    if (view is null) { jc.Report.Warnings.Add(new ReportWarning("engine", "capture target '" + target + "' not found")); continue; }
                    string? why = CaptureService.CannotExport(view);
                    if (why != null) { jc.Report.Warnings.Add(new ReportWarning("engine", "capture skipped: " + why)); continue; }
                    string format = c.GetStringOrNull("format") ?? "png";
                    int width = c["pixelWidth"] is JsonNumber w && w.TryGetInt64(out long wl) ? (int)Math.Max(CaptureService.MinPixels, Math.Min(CaptureService.MaxPixels, wl)) : 2000;
                    string file = Path.Combine(jc.OutputDir, "verify-" + n + "-" + CaptureService.SafeFileName(view.Name) + (format == "pdf" ? ".pdf" : ".png"));
                    var res = format == "pdf"
                        ? CaptureService.ExportPdf(jc.Doc, new List<ElementId> { view.Id }, file, true)
                        : CaptureService.ExportImage(jc.Doc, view, file, false, width, true);
                    if (res.Bytes <= 0) jc.Report.Warnings.Add(new ReportWarning("engine", "capture of '" + view.Name + "' produced an empty file"));
                    else jc.Report.Artifacts.Add(new Artifact(res.Kind, res.Path, target));
                }
                catch (Exception e)
                {
                    jc.Report.Warnings.Add(new ReportWarning("engine", "capture '" + target + "' failed: " + e.Message));
                }
            }
        }
    }
}
