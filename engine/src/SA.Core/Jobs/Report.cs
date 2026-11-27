using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.Core.Jobs
{
    public enum JobStatus { Unknown, Running, Committed, RolledBack, Failed }

    public enum RunMode { Rehearse, Commit }

    public static class JobStatuses
    {
        public static string ToWire(JobStatus s)
        {
            switch (s)
            {
                case JobStatus.Running: return "running";
                case JobStatus.Committed: return "committed";
                case JobStatus.RolledBack: return "rolled_back";
                case JobStatus.Failed: return "failed";
                default: return "unknown";
            }
        }

        public static JobStatus Parse(string s)
        {
            switch (s)
            {
                case "unknown": return JobStatus.Unknown;
                case "running": return JobStatus.Running;
                case "committed": return JobStatus.Committed;
                case "rolled_back": return JobStatus.RolledBack;
                case "failed": return JobStatus.Failed;
                default: throw new FormatException("Unknown job status '" + s + "'.");
            }
        }

        public static string ToWire(RunMode m) => m == RunMode.Commit ? "commit" : "rehearse";

        public static RunMode ParseMode(string s) =>
            s == "commit" ? RunMode.Commit : s == "rehearse" ? RunMode.Rehearse : throw new FormatException("Unknown run mode '" + s + "'.");
    }

    public sealed class OpError
    {
        public OpError(string code, string message, JsonObject? details = null)
        {
            Code = code; Message = message; Details = details;
        }

        public string Code { get; }
        public string Message { get; }
        public JsonObject? Details { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("code", Code).Add("message", Message);
            if (Details != null) o.Add("details", Details);
            return o;
        }
    }

    /// <summary>
    /// The result of one op. <see cref="Ok"/> may only be true when <see cref="Readback"/> was read from the document after
    /// the op and matched the request within tolerance. Element ids are strings: AutoCAD handles, Revit 64-bit element ids.
    /// </summary>
    public sealed class OpResult
    {
        public OpResult(string id, string op)
        {
            Id = id;
            Op = op;
        }

        public string Id { get; }
        public string Op { get; }
        public bool Ok { get; set; }
        public List<string> Created { get; } = new List<string>();
        public List<string> Modified { get; } = new List<string>();
        public List<string> Deleted { get; } = new List<string>();
        /// <summary>Values read back from the document, in job units.</summary>
        public JsonObject Readback { get; set; } = new JsonObject();
        /// <summary>Named values later ops can reference as <c>$id.name</c> (declared in the op schema's outputs).</summary>
        public JsonObject Outputs { get; set; } = new JsonObject();
        /// <summary>Elements the op found already in the required state (ensure ops), so left unchanged.</summary>
        public List<string> Unchanged { get; } = new List<string>();
        public OpError? Error { get; set; }

        /// <summary>The value "$id" resolves to. See <see cref="Refs"/>.</summary>
        public JsonObject ToRefTarget()
        {
            var ids = Created.Concat(Modified).ToList();
            var o = new JsonObject();
            o.Add("id", ids.Count > 0 ? (JsonValue)ids[0] : (Unchanged.Count > 0 ? (JsonValue)Unchanged[0] : JsonValue.Null));
            o.Add("ids", JsonArray.FromStrings(ids.Count > 0 ? ids : Unchanged));
            o.Add("created", JsonArray.FromStrings(Created));
            o.Add("modified", JsonArray.FromStrings(Modified));
            o.Add("deleted", JsonArray.FromStrings(Deleted));
            o.Add("readback", Readback.DeepClone());
            o.Add("outputs", Outputs.DeepClone());
            return o;
        }

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Add("id", Id).Add("op", Op).Add("ok", Ok)
                .Add("created", JsonArray.FromStrings(Created))
                .Add("modified", JsonArray.FromStrings(Modified))
                .Add("deleted", JsonArray.FromStrings(Deleted));
            if (Unchanged.Count > 0) o.Add("unchanged", JsonArray.FromStrings(Unchanged));
            o.Add("readback", Readback);
            if (Outputs.Count > 0) o.Add("outputs", Outputs);
            if (Error != null) o.Add("error", Error.ToJson());
            return o;
        }

        public static OpResult Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var r = new OpResult(c.GetNonEmptyString("id"), c.GetNonEmptyString("op")) { Ok = c.Required("ok").AsBool() };
            r.Created.AddRange(c.GetStringList("created"));
            r.Modified.AddRange(c.GetStringList("modified"));
            r.Deleted.AddRange(c.GetStringList("deleted"));
            r.Unchanged.AddRange(c.GetStringList("unchanged"));
            r.Readback = c.Optional("readback")?.AsObject() ?? new JsonObject();
            r.Outputs = c.Optional("outputs")?.AsObject() ?? new JsonObject();
            var e = c.Optional("error");
            if (e != null) r.Error = new OpError(e.GetNonEmptyString("code"), e.GetString("message", ""), e.Optional("details")?.AsObject());
            return r;
        }
    }

    public sealed class Artifact
    {
        public Artifact(string kind, string path, string? target = null)
        {
            Kind = kind; Path = path; Target = target;
        }

        /// <summary>png, pdf, csv, dwg, ifc, json...</summary>
        public string Kind { get; }
        public string Path { get; }
        public string? Target { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("kind", Kind).Add("path", Path);
            if (Target != null) o.Add("target", Target);
            return o;
        }
    }

    public sealed class ReportWarning
    {
        public ReportWarning(string source, string text, string? opId = null)
        {
            Source = source; Text = text; OpId = opId;
        }

        /// <summary>"app" for warnings raised by AutoCAD or Revit, "engine" for the engine's own.</summary>
        public string Source { get; }
        public string Text { get; }
        public string? OpId { get; }
        /// <summary>Optional machine-readable code, e.g. the Revit failure definition GUID. Wire: "code".</summary>
        public string? Code { get; set; }
        /// <summary>Optional severity as the application reports it ("warning", "error"...). Wire: "severity".</summary>
        public string? Severity { get; set; }
        /// <summary>Optional ids of the elements the warning is about. Wire: "elements" (written only when not empty).</summary>
        public List<string> Elements { get; } = new List<string>();

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("source", Source).Add("text", Text);
            if (OpId != null) o.Add("opId", OpId);
            if (Code != null) o.Add("code", Code);
            if (Severity != null) o.Add("severity", Severity);
            if (Elements.Count > 0) o.Add("elements", JsonArray.FromStrings(Elements));
            return o;
        }

        public static ReportWarning Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var w = new ReportWarning(c.GetString("source"), c.GetString("text"), c.GetStringOrNull("opId"))
            {
                Code = c.GetStringOrNull("code"),
                Severity = c.GetStringOrNull("severity"),
            };
            w.Elements.AddRange(c.GetStringList("elements"));
            return w;
        }
    }

    /// <summary>The document identity after the job (and the fingerprint before it), as the engine computed them.</summary>
    public sealed class ReportDocument
    {
        public ReportDocument(string path, string fingerprint, string? fingerprintBefore = null)
        {
            Path = path ?? ""; Fingerprint = fingerprint ?? ""; FingerprintBefore = fingerprintBefore;
        }

        public string Path { get; }
        /// <summary>The fingerprint after the job: what a follow-up job must carry.</summary>
        public string Fingerprint { get; }
        public string? FingerprintBefore { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("path", Path).Add("fingerprint", Fingerprint);
            if (FingerprintBefore != null) o.Add("fingerprintBefore", FingerprintBefore);
            return o;
        }

        public static ReportDocument Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            return new ReportDocument(c.GetString("path", ""), c.GetString("fingerprint", ""), c.GetStringOrNull("fingerprintBefore"));
        }
    }

    /// <summary>A dialog the application showed during the job, and how the engine dismissed it.</summary>
    public sealed class ReportDialog
    {
        public ReportDialog(string kind, string dialogId, string message)
        {
            Kind = kind ?? ""; DialogId = dialogId ?? ""; Message = message ?? "";
        }

        public string Kind { get; }
        public string DialogId { get; }
        public string Message { get; }
        /// <summary>The result code the dialog was dismissed with. Wire: "dismissedWith".</summary>
        public long? DismissedWith { get; set; }
        /// <summary>Whether the dismissal accepted (OK, Yes) rather than cancelled. Wire: "accepted".</summary>
        public bool? Accepted { get; set; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("kind", Kind).Add("dialogId", DialogId).Add("message", Message);
            if (DismissedWith.HasValue) o.Add("dismissedWith", DismissedWith.Value);
            if (Accepted.HasValue) o.Add("accepted", Accepted.Value);
            return o;
        }

        public static ReportDialog Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var d = new ReportDialog(c.GetString("kind", ""), c.GetString("dialogId", ""), c.GetString("message", ""));
            if (c.Optional("dismissedWith")?.Value is JsonNumber n && n.TryGetInt64(out long x)) d.DismissedWith = x;
            if (c.Optional("accepted")?.Value is JsonBool b) d.Accepted = b.Value;
            return d;
        }
    }

    /// <summary>The result of <c>run</c>, and what <c>status</c> returns for a finished job.</summary>
    public sealed class Report
    {
        public Report(string jobId, JobStatus status, RunMode mode)
        {
            JobId = jobId; Status = status; Mode = mode;
        }

        public string JobId { get; }
        public JobStatus Status { get; set; }
        public RunMode Mode { get; }
        public List<Issue> Validation { get; } = new List<Issue>();
        public List<OpResult> Results { get; } = new List<OpResult>();
        public List<CheckResult> Checks { get; } = new List<CheckResult>();
        public List<Artifact> Artifacts { get; } = new List<Artifact>();
        public List<ReportWarning> Warnings { get; } = new List<ReportWarning>();
        public string? Undo { get; set; }
        /// <summary>For a failed job: the op that failed and why.</summary>
        public string? FailedOpId { get; set; }
        public OpError? Error { get; set; }
        /// <summary>Phase name to milliseconds: validate, execute, verify, capture.</summary>
        public Dictionary<string, long> TimingMs { get; } = new Dictionary<string, long>();
        /// <summary>
        /// True when the job's changes were committed to the document, even if a later step (post-commit readback, verify)
        /// then failed and <see cref="Status"/> is Failed; false when nothing was changed; null when not stated. Wire: "committed".
        /// </summary>
        public bool? Committed { get; set; }
        /// <summary>The document identity after the job. Wire: "document".</summary>
        public ReportDocument? Document { get; set; }
        /// <summary>Dialogs shown and dismissed during the job. Wire: "dialogs" (written only when not empty).</summary>
        public List<ReportDialog> Dialogs { get; } = new List<ReportDialog>();
        /// <summary>
        /// Other top-level members (for example "engine"), kept by <see cref="Parse"/> and written last by <see cref="ToJson"/>
        /// so a report read back from a ledger loses nothing. Keys of the members above are ignored here.
        /// </summary>
        public JsonObject Extra { get; } = new JsonObject();

        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "jobId", "status", "mode", "validation", "results", "summary", "checks", "artifacts", "warnings", "undo", "failedOpId",
            "error", "timingMs", "committed", "document", "dialogs",
        };

        public int CreatedCount => Results.Sum(r => r.Created.Count);
        public int ModifiedCount => Results.Sum(r => r.Modified.Count);
        public int DeletedCount => Results.Sum(r => r.Deleted.Count);

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Add("jobId", JobId)
                .Add("status", JobStatuses.ToWire(Status))
                .Add("mode", JobStatuses.ToWire(Mode))
                .Add("validation", Issue.ToJson(Validation))
                .Add("results", new JsonArray(Results.Select(r => (JsonValue)r.ToJson())))
                .Add("summary", new JsonObject().Add("created", CreatedCount).Add("modified", ModifiedCount).Add("deleted", DeletedCount))
                .Add("checks", new JsonArray(Checks.Select(c => (JsonValue)c.ToJson())))
                .Add("artifacts", new JsonArray(Artifacts.Select(a => (JsonValue)a.ToJson())))
                .Add("warnings", new JsonArray(Warnings.Select(w => (JsonValue)w.ToJson())));
            if (Undo != null) o.Add("undo", Undo);
            if (FailedOpId != null) o.Add("failedOpId", FailedOpId);
            if (Error != null) o.Add("error", Error.ToJson());
            var t = new JsonObject();
            foreach (var kv in TimingMs) t.Add(kv.Key, kv.Value);
            o.Add("timingMs", t);
            if (Committed.HasValue) o.Add("committed", Committed.Value);
            if (Document != null) o.Add("document", Document.ToJson());
            if (Dialogs.Count > 0) o.Add("dialogs", new JsonArray(Dialogs.Select(d => (JsonValue)d.ToJson())));
            foreach (var kv in Extra)
                if (!KnownKeys.Contains(kv.Key) && !o.ContainsKey(kv.Key)) o.Add(kv.Key, kv.Value);
            return o;
        }

        public static Report Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var r = new Report(c.GetNonEmptyString("jobId"), JobStatuses.Parse(c.GetString("status")), JobStatuses.ParseMode(c.GetString("mode")));
            var v = c.Optional("validation");
            if (v != null) foreach (var i in v.Items()) r.Validation.Add(Issue.Parse(i.Value));
            var res = c.Optional("results");
            if (res != null) foreach (var i in res.Items()) r.Results.Add(OpResult.Parse(i.Value));
            var chk = c.Optional("checks");
            if (chk != null) foreach (var i in chk.Items()) r.Checks.Add(CheckResult.Parse(i.Value));
            var art = c.Optional("artifacts");
            if (art != null) foreach (var i in art.Items()) r.Artifacts.Add(new Artifact(i.GetNonEmptyString("kind"), i.GetNonEmptyString("path"), i.GetStringOrNull("target")));
            var w = c.Optional("warnings");
            if (w != null) foreach (var i in w.Items()) r.Warnings.Add(ReportWarning.Parse(i.Value));
            r.Undo = c.GetStringOrNull("undo");
            r.FailedOpId = c.GetStringOrNull("failedOpId");
            var e = c.Optional("error");
            if (e != null) r.Error = new OpError(e.GetNonEmptyString("code"), e.GetString("message", ""), e.Optional("details")?.AsObject());
            var t = c.Optional("timingMs");
            if (t != null) foreach (var kv in t.Members()) r.TimingMs[kv.Key] = kv.Value.AsInt64();
            var committed = c.Optional("committed");
            if (committed != null && !committed.Value.IsNull) r.Committed = committed.AsBool();
            var doc = c.Optional("document");
            if (doc != null && !doc.Value.IsNull) r.Document = ReportDocument.Parse(doc.Value);
            var dl = c.Optional("dialogs");
            if (dl != null) foreach (var i in dl.Items()) r.Dialogs.Add(ReportDialog.Parse(i.Value));
            foreach (var kv in c.AsObject())
                if (!KnownKeys.Contains(kv.Key) && kv.Key != "$schema") r.Extra.Add(kv.Key, kv.Value.DeepClone());
            return r;
        }
    }
}
