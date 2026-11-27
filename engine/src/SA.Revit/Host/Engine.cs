using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Validation;
using SA.Revit.Audit;
using SA.Revit.Ops;

namespace SA.Revit.Host
{
    /// <summary>
    /// The engine's state and request routing. Lives for the Revit session.
    /// ping, status and probe are answered on the pipe thread from cached state and reflection; they never call
    /// the Revit API. snapshot, validate, run, audit and capture are queued to the main thread through the
    /// dispatcher; only one of them is in flight at a time and any other API request meanwhile gets busy.
    /// </summary>
    internal sealed class Engine : IDisposable
    {
        private int _apiSlot;
        private volatile RunningJob? _current;

        public Engine(string productVersion, string productBuild, string language)
        {
            ProductVersion = productVersion;
            ProductBuild = productBuild;
            Language = language;
            ProcessId = Process.GetCurrentProcess().Id;
            PipeName = EngineApps.PipeName(EngineApps.Revit, ProcessId);
            Schemas = OpRegistry.Default;
            Handlers = new HandlerRegistry(Schemas);
            Audit = new AuditService(new FactCollectorRegistry());
            Runner = new JobRunner(this);
            Snapshots = new Snapshot.SnapshotBuilder(this);
            Captures = new Capture.CaptureMethod(this);
        }

        public static string EngineVersion =>
            typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Engine).Assembly.GetName().Version?.ToString() ?? "0";

        public string ProductVersion { get; }
        public string ProductBuild { get; }
        public string Language { get; }
        public int ProcessId { get; }
        public string PipeName { get; }
        public DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;
        public OpRegistry Schemas { get; }
        public HandlerRegistry Handlers { get; }
        public AuditService Audit { get; }
        public JobRunner Runner { get; }
        public Snapshot.SnapshotBuilder Snapshots { get; }
        public Capture.CaptureMethod Captures { get; }
        public DocumentTracker Tracker { get; } = new DocumentTracker();
        public JobLedger Ledger { get; } = new JobLedger();
        public ApiDispatcher Dispatcher { get; } = new ApiDispatcher();
        public RunningJob? Current { get => _current; set => _current = value; }
        public string? CurrentMethod { get; private set; }

        // ------------------------------------------------------------------ session file

        public string SessionFile => SaPaths.SessionFile(EngineApps.Revit, ProcessId);

        public void WriteSessionFile()
        {
            var info = new SessionInfo(EngineApps.Revit, ProcessId, PipeName, Compat.ReleaseInfo.Year, ProductVersion + " (" + ProductBuild + ")", StartTime, EngineVersion);
            Directory.CreateDirectory(SaPaths.Sessions());
            string tmp = SessionFile + ".tmp";
            File.WriteAllText(tmp, info.ToJson().ToJson(true), new UTF8Encoding(false));
            if (File.Exists(SessionFile)) File.Delete(SessionFile);
            File.Move(tmp, SessionFile);
        }

        public void DeleteSessionFile()
        {
            try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch (Exception e) { Log.Warn("Could not delete session file: " + e.Message); }
        }

        // ------------------------------------------------------------------ routing (pipe threads)

        public byte[] Handle(byte[] bytes)
        {
            Request req;
            try { req = Request.Parse(bytes); }
            catch (ProtocolException e) { return Responses.Bytes(Response.Failure("", ErrorCodes.BadRequest, e.Message)); }
            Response r;
            try { r = Route(req); }
            catch (Exception e)
            {
                Log.Error("Routing " + req.Method + " failed", e);
                r = Response.Failure(req.Id, ErrorCodes.Internal, e.GetType().Name + ": " + e.Message);
            }
            return Responses.Bytes(r);
        }

        private Response Route(Request req)
        {
            switch (req.Method)
            {
                case Methods.Ping: return Response.Success(req.Id, Ping());
                case Methods.Status: return Status(req);
                case Methods.Probe: return Response.Success(req.Id, Probe.Run(req.Params));
                case Methods.Snapshot:
                    return OnMainThread(req, null, app => Snapshots.Build(app, req));
                case Methods.Audit:
                    return OnMainThread(req, null, app => AuditMethod.Run(this, app, req));
                case Methods.Capture:
                    return OnMainThread(req, null, app => Captures.Run(app, req));
                case Methods.Validate:
                case Methods.Run:
                    return JobRequest(req);
                default:
                    return Response.Failure(req.Id, ErrorCodes.BadRequest, "Unknown method '" + req.Method + "'. Known: " + string.Join(", ", Methods.All));
            }
        }

        private Response JobRequest(Request req)
        {
            var jobValue = req.Params["job"];
            if (jobValue is null || !(jobValue is JsonObject))
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.job must be the job object.");
            var v = JobValidator.Validate(jobValue, Schemas);
            if (!v.Ok || v.Job is null)
                return Response.Failure(req.Id, ErrorCodes.SchemaInvalid, v.Errors.Count() + " schema issue(s); nothing ran.",
                    new JsonObject().Add("issues", Issue.ToJson(v.Issues)));
            var job = v.Job;
            if (job.App != AppKind.Revit)
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "This is the Revit engine; the job is for " + AppKinds.ToWire(job.App) + ".");

            RunMode mode = RunMode.Rehearse;
            if (req.Method == Methods.Run)
            {
                string m = req.Params.GetStringOrNull("mode") ?? "";
                if (m != "commit" && m != "rehearse")
                    return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.mode must be 'rehearse' or 'commit'.");
                mode = m == "commit" ? RunMode.Commit : RunMode.Rehearse;
                if (job.Ops.Any(o => o.Schema.RequiresAllowScript) && !(req.Params["allowScript"] is JsonBool b && b.Value))
                    return Response.Failure(req.Id, ErrorCodes.ValidationFailed, "The job contains a script op and the request does not set allowScript.");
            }

            if (Ledger.IsUsed(job.JobId, out var record)) return DuplicateJob(req.Id, job.JobId, record);
            if (req.Method == Methods.Validate)
                return OnMainThread(req, job.JobId, app => Runner.Validate(app, req.Id, job));
            var queued = new RunningJob(job.JobId, JobStatuses.ToWire(mode), "queued", null, 0, job.Ops.Count, DateTime.UtcNow);
            return OnMainThread(req, job.JobId, app => Runner.Run(app, req.Id, job, mode, req.Params), queued);
        }

        internal static Response DuplicateJob(string reqId, string jobId, JsonObject? record)
        {
            var d = new JsonObject().Add("jobId", jobId);
            if (record != null && record.GetStringOrNull("status") is string st) d.Add("status", st);
            return Response.Failure(reqId, ErrorCodes.DuplicateJob,
                "Job '" + jobId + "' has already been run in commit mode. Job ids are used once; use status to read its report, or give the job a new id.", d);
        }

        /// <summary>
        /// Runs work on the Revit main thread, one request at a time. <paramref name="run"/> is the job a run request
        /// carries: it is current (and status answers running) from before the item is queued until after the runner has
        /// written the final report, so status never falls back to the ledger while the job is still in hand. Work other
        /// than a job runs under its own dialog guard; dismissed dialogs are listed in the response.
        /// </summary>
        private Response OnMainThread(Request req, string? jobId, Func<UIApplication, Response> work, RunningJob? run = null)
        {
            if (Interlocked.CompareExchange(ref _apiSlot, 1, 0) != 0)
            {
                var d = new JsonObject();
                if (CurrentMethod != null) d.Add("method", CurrentMethod);
                var c = _current;
                if (c != null) d.Add("job", c.ToJson());
                return Response.Failure(req.Id, ErrorCodes.Busy, "The engine is busy with another request" + (c != null ? " (job " + c.JobId + ")" : "") + ". Try again shortly.", d);
            }
            try
            {
                CurrentMethod = req.Method;
                if (run != null) _current = run;
                int startSeconds = req.Params["startTimeoutSeconds"] is JsonNumber n && n.TryGetInt64(out long s) ? (int)Math.Max(5, Math.Min(3600, s)) : 60;
                var item = new WorkItem(req.Id, req.Method, jobId, run != null ? work : Guarded(work));
                return Dispatcher.Run(item, TimeSpan.FromSeconds(startSeconds));
            }
            finally
            {
                if (run != null) _current = null;
                CurrentMethod = null;
                Interlocked.Exchange(ref _apiSlot, 0);
            }
        }

        private static Func<UIApplication, Response> Guarded(Func<UIApplication, Response> work) => app =>
        {
            using (var guard = new DialogGuard(app))
                return AttachDialogs(work(app), guard, okToo: true);
        };

        /// <summary>Lists the dialogs the guard dismissed in the response: in error details, and with <paramref name="okToo"/> in a successful result.</summary>
        internal static Response AttachDialogs(Response r, DialogGuard guard, bool okToo)
        {
            if (guard.Dialogs.Count == 0) return r;
            var list = new JsonArray(guard.Dialogs.Select(d => (JsonValue)d.ToReport().ToJson()));
            if (r.Ok)
            {
                if (okToo && r.Result is JsonObject o) o.Set("dialogs", list);
                return r;
            }
            if (r.Error?.Details is JsonObject details) details.Set("dialogs", list);
            else if (r.Error != null)
                return Response.Failure(r.Id, r.Error.Code, r.Error.Message, new JsonObject().Add("dialogs", list));
            return r;
        }

        // ------------------------------------------------------------------ cached answers

        public JsonObject Ping()
        {
            var o = new JsonObject()
                .Add("engine", EngineApps.Revit)
                .Add("engineVersion", EngineVersion)
                .Add("protocol", ProtocolInfo.Version)
                .Add("releaseYear", Compat.ReleaseInfo.Year)
                .Add("product", new JsonObject().Add("version", ProductVersion).Add("build", ProductBuild).Add("language", Language))
                .Add("runtime", new JsonObject().Add("target", Compat.PipeFactory.TargetRuntime).Add("clr", Environment.Version.ToString()))
                .Add("processId", ProcessId)
                .Add("pipe", PipeName)
                .Add("startTime", StartTime.ToString("o", CultureInfo.InvariantCulture))
                .Add("busy", Volatile.Read(ref _apiSlot) != 0);
            if (CurrentMethod != null) o.Add("currentMethod", CurrentMethod);
            var c = _current;
            if (c != null) o.Add("currentJob", c.ToJson());
            var a = Tracker.ActiveIdentity;
            o.Add("activeDocument", a is null ? (JsonValue)JsonValue.Null : a.ToJson());
            o.Add("handlers", JsonArray.FromStrings(Handlers.Names.OrderBy(n => n, StringComparer.Ordinal)));
            o.Add("missingHandlers", JsonArray.FromStrings(Handlers.Missing()));
            o.Add("factTables", JsonArray.FromStrings(Audit.Collectors.Tables));
            if (Log.FilePath != null) o.Add("log", Log.FilePath);
            return o;
        }

        private Response Status(Request req)
        {
            string? jobId = req.Params.GetStringOrNull("jobId");
            if (string.IsNullOrWhiteSpace(jobId)) return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.jobId is required.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(jobId!, JobValidator.JobIdPattern))
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "'" + jobId + "' is not a valid job id.");
            var c = _current;
            if (c != null && c.JobId == jobId) return Running(req.Id, c);
            var rec = Ledger.Lookup(jobId!);
            if (rec?.GetStringOrNull("status") == "running")
            {
                // The job may have started after current was read, or finished after the ledger was read: look again.
                c = _current;
                if (c != null && c.JobId == jobId) return Running(req.Id, c);
                rec = Ledger.Lookup(jobId!);
            }
            if (rec is null) return Response.Success(req.Id, new JsonObject().Add("jobId", jobId!).Add("status", "unknown"));
            if (rec.GetStringOrNull("status") == "running")
            {
                // A running record that is not the current job: the engine stopped mid-job.
                bool thisSession = rec["processId"] is JsonNumber pid && pid.TryGetInt64(out long p) && p == ProcessId;
                var o = new JsonObject().Add("jobId", jobId!).Add("status", "unknown").Add("interrupted", true)
                    .Add("message", thisSession
                        ? "The job started in this Revit session but never wrote its report. Its outcome is unknown: inspect the model " +
                          "(and its undo list) before running the work again. The id stays used."
                        : "The engine stopped while this job was running (Revit closed or crashed). The transaction group never " +
                          "finished, so nothing was committed by the engine. The id stays used.")
                    .Add("record", rec);
                return Response.Success(req.Id, o);
            }
            return Response.Success(req.Id, rec);
        }

        private static Response Running(string reqId, RunningJob c) =>
            Response.Success(reqId, new JsonObject().Add("jobId", c.JobId).Add("status", "running").Add("phase", c.Phase).Add("progress", c.ToJson()));

        public void Dispose()
        {
            Dispatcher.Dispose();
        }
    }
}
