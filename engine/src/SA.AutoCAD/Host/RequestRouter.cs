using System;
using System.Threading;
using SA.AutoCAD.Audit;
using SA.AutoCAD.Capture;
using SA.AutoCAD.Ops;
using SA.AutoCAD.Snapshot;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Answers one request (called on a pipe connection thread).
    /// - ping, status and probe never touch the AutoCAD API: they read cached state, the ledger and reflection.
    /// - snapshot, validate, run, audit and capture take the engine's single work slot; a second one arriving while the
    ///   slot is held gets busy. They run on AutoCAD's main thread through <see cref="MainThread"/>.
    /// - A work item that cannot start (AutoCAD never idle) within the start timeout is cancelled and answered with
    ///   timeout; it is guaranteed never to run afterwards.
    /// </summary>
    internal sealed class RequestRouter
    {
        private static readonly TimeSpan RunStartTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan OtherStartTimeout = TimeSpan.FromSeconds(45);

        private readonly JobRunner _runner;
        private readonly HandlerRegistry _handlers;
        private readonly string _pipeName;
        private int _busy;
        private string? _busyWhat;
        private string? _busyJobId;
        private DateTimeOffset? _busySince;

        internal RequestRouter(JobRunner runner, HandlerRegistry handlers, string pipeName)
        {
            _runner = runner;
            _handlers = handlers;
            _pipeName = pipeName;
        }

        internal Response Handle(Request r)
        {
            try
            {
                switch (r.Method)
                {
                    case Methods.Ping: return Response.Success(r.Id, Ping());
                    case Methods.Status: return Response.Success(r.Id, Status(r.Params));
                    case Methods.Probe: return Response.Success(r.Id, ProbeService.Probe(r.Params));
                    case Methods.Snapshot:
                    case Methods.Validate:
                    case Methods.Run:
                    case Methods.Audit:
                    case Methods.Capture:
                        return Response.Success(r.Id, WithSlot(r));
                    default:
                        return Response.Failure(r.Id, ErrorCodes.BadRequest, "Unknown method '" + r.Method + "'. Methods: " + string.Join(", ", Methods.All));
                }
            }
            catch (EngineException e)
            {
                if (e.Code == ErrorCodes.Internal) Log.Error("Request '" + r.Method + "' failed.", e.InnerException ?? e);
                return Response.Failure(r.Id, e.Code, e.Message, e.Details);
            }
            catch (Exception e)
            {
                Log.Error("Request '" + r.Method + "' failed.", e);
                return Response.Failure(r.Id, ErrorCodes.Internal, e.GetType().Name + ": " + e.Message);
            }
        }

        private JsonValue WithSlot(Request r)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                var d = new JsonObject().Add("running", _busyWhat ?? "").Add("since", _busySince?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "");
                if (_busyJobId != null) d.Add("jobId", _busyJobId);
                throw new EngineException(ErrorCodes.Busy, "The engine is busy with " + (_busyWhat ?? "another request") + "; try again, or follow a job with status.", d);
            }
            try
            {
                _busyWhat = r.Method;
                _busySince = DateTimeOffset.Now;
                var p = r.Params;
                switch (r.Method)
                {
                    case Methods.Snapshot: return MainThread.Invoke("snapshot", () => SnapshotBuilder.Build(p), OtherStartTimeout);
                    case Methods.Audit: return MainThread.Invoke("audit", () => AuditService.Audit(p), OtherStartTimeout);
                    case Methods.Capture: return MainThread.Invoke("capture", () => CaptureService.CaptureMethod(p), OtherStartTimeout);
                    case Methods.Validate:
                    {
                        var job = _runner.StaticValidate(p, false, out var warnings);
                        bool allowScript = p["allowScript"] is JsonBool b && b.Value;
                        return MainThread.Invoke("validate", () => _runner.ValidateOnMain(job, warnings, allowScript), OtherStartTimeout);
                    }
                    case Methods.Run: return Run(p);
                    default: throw new EngineException(ErrorCodes.BadRequest, "Unknown method.");
                }
            }
            finally
            {
                _busyWhat = null;
                _busyJobId = null;
                _busySince = null;
                Volatile.Write(ref _busy, 0);
            }
        }

        private JsonValue Run(JsonObject p)
        {
            string? modeText = (p["mode"] as JsonString)?.Value;
            if (modeText != "commit" && modeText != "rehearse")
                throw new EngineException(ErrorCodes.BadRequest, "params.mode must be \"rehearse\" or \"commit\".");
            var mode = JobStatuses.ParseMode(modeText);
            bool allowScript = p["allowScript"] is JsonBool b && b.Value;
            var job = _runner.StaticValidate(p, true, out var warnings);
            var done = _runner.Ledger.FindCommit(job.JobId);
            if (done != null)
            {
                var d = new JsonObject().Add("jobId", job.JobId).Add("status", JobStatuses.ToWire(done.Status));
                if (done.Report != null) d.Add("report", done.Report);
                throw new EngineException(ErrorCodes.DuplicateJob,
                    "Job '" + job.JobId + "' was already run in commit mode (" + JobStatuses.ToWire(done.Status) + "). Job ids are used once; use status to see its report.", d);
            }
            _busyJobId = job.JobId;
            return MainThread.Invoke("run " + job.JobId, () => _runner.RunOnMain(job, mode, allowScript, warnings).ToJson(), RunStartTimeout);
        }

        private JsonObject Ping()
        {
            bool busy = Volatile.Read(ref _busy) != 0;
            var o = new JsonObject()
                .Add("engine", EngineInfo.Describe())
                .Add("pipe", _pipeName)
                .Add("startTime", EngineInfo.StartTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
                .Add("busy", busy);
            if (busy)
            {
                o.Add("running", _busyWhat ?? "");
                if (_busyJobId != null) o.Add("jobId", _busyJobId);
            }
            o.Add("activeDocument", DocumentIdentity.CachedActiveJson());
            o.Add("handlers", new JsonObject().Add("count", _handlers.Names.Count).Add("missing", _handlers.Missing.Count));
            o.Add("methods", JsonArray.FromStrings(Methods.All));
            return o;
        }

        private JsonObject Status(JsonObject p)
        {
            string? jobId = (p["jobId"] as JsonString)?.Value;
            if (!JobLedger.IsSafeId(jobId))
                throw new EngineException(ErrorCodes.BadRequest, "params.jobId must be a job id (letters, digits, '.', '_', '-').");
            var e = _runner.Ledger.Lookup(jobId!);
            if (e is null)
            {
                var u = new JsonObject().Add("jobId", jobId!).Add("status", "unknown");
                if (_busyJobId == jobId) u.Add("queued", true).Add("note", "The job is waiting for AutoCAD to become idle; it has not started.");
                return u;
            }
            var o = new JsonObject()
                .Add("jobId", e.JobId)
                .Add("status", JobStatuses.ToWire(e.Status))
                .Add("mode", JobStatuses.ToWire(e.Mode))
                .Add("startedAt", e.StartedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            if (e.FinishedAt.HasValue) o.Add("finishedAt", e.FinishedAt.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            if (e.Report != null) o.Add("report", e.Report);
            return o;
        }
    }
}
