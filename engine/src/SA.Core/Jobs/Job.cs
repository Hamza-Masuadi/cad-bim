using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.Core.Jobs
{
    public sealed class JobDocument
    {
        public JobDocument(string? path, string? fingerprint)
        {
            Path = path;
            Fingerprint = fingerprint;
        }

        /// <summary>The document path from the snapshot. Engines compare it with the active document.</summary>
        public string? Path { get; }
        /// <summary>The engine-defined fingerprint from the snapshot.</summary>
        public string? Fingerprint { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            if (Path != null) o.Add("path", Path);
            if (Fingerprint != null) o.Add("fingerprint", Fingerprint);
            return o;
        }
    }

    public sealed class JobOp
    {
        internal JobOp(int index, string id, string op, JsonObject args, OpSchema schema, string? note)
        {
            Index = index; Id = id; Op = op; Args = args; Schema = schema; Note = note;
        }

        public int Index { get; }
        public string Id { get; }
        public string Op { get; }
        /// <summary>The args as written (references unresolved). Resolve with <see cref="Refs.Resolve"/> before reading.</summary>
        public JsonObject Args { get; }
        public OpSchema Schema { get; }
        public string? Note { get; }
        /// <summary>JSON path of this op inside the job, for example <c>$.ops[3]</c>.</summary>
        public string Path => JsonPath.Index("$.ops", Index);
        public string ArgsPath => Path + ".args";
    }

    public sealed class JobVerify
    {
        internal JobVerify(List<string> checks, List<JsonObject> capture)
            : this(checks, capture, new List<string>(), false) { }

        internal JobVerify(List<string> checks, List<JsonObject> capture, List<string> checksFiles, bool addToBundled)
        {
            Checks = checks;
            Capture = capture;
            ChecksFiles = checksFiles;
            AddToBundled = addToBundled;
        }

        public IReadOnlyList<string> Checks { get; }
        /// <summary>Capture requests, each at least <c>{ "target": "..." }</c>; engines interpret the rest.</summary>
        public IReadOnlyList<JsonObject> Capture { get; }
        /// <summary>
        /// <c>verify.checksFile</c>: checks files or folders to take the check ids from, as written (resolve relative paths
        /// the way the audit method does). Empty: the engine's bundled checks.
        /// </summary>
        public IReadOnlyList<string> ChecksFiles { get; }
        /// <summary><c>verify.checksMode</c> "add": load <see cref="ChecksFiles"/> after the bundled checks instead of replacing them.</summary>
        public bool AddToBundled { get; }
        /// <summary><c>verify.visual</c>: before/after capture and pixel comparison, or null when not asked for.</summary>
        public SA.Core.Imaging.VisualSpec? Visual { get; internal set; }

        /// <summary>
        /// The verify step as audit options (check ids, checks files, checks mode), for <see cref="SA.Core.Checks.AuditLoader.Load"/>.
        /// The standard is the job's own (<see cref="Job.Standard"/>); pass it as the loader's default standard.
        /// </summary>
        public SA.Core.Checks.AuditOptions ToAuditOptions()
        {
            var o = new SA.Core.Checks.AuditOptions { AddToBundled = AddToBundled };
            o.CheckIds.AddRange(Checks);
            o.ChecksFiles.AddRange(ChecksFiles);
            return o;
        }

        public static readonly JobVerify Empty = new JobVerify(new List<string>(), new List<JsonObject>());
    }

    /// <summary>Thrown by <see cref="Job.Parse"/> when static validation finds errors.</summary>
    public sealed class JobValidationException : Exception
    {
        public JobValidationException(IReadOnlyList<Issue> issues)
            : base("Job is invalid: " + string.Join("; ", issues.Where(i => i.IsError).Take(5).Select(i => i.ToString())))
        {
            Issues = issues;
        }

        public IReadOnlyList<Issue> Issues { get; }
    }

    /// <summary>
    /// <c>ownership</c>: what a Revit job does with existing elements it would change that the user cannot edit in a
    /// workshared model (owned by someone else, or changed in central since the local copy was updated).
    /// Refuse (default): the whole job is refused with not_owned. Skip: those elements are left out of every op and
    /// each is reported; the rest of the job runs.
    /// </summary>
    public enum OwnershipMode { Refuse, Skip }

    public static class OwnershipModes
    {
        public static string ToWire(OwnershipMode m) => m == OwnershipMode.Skip ? "skip" : "refuse";

        public static bool TryParse(string? s, out OwnershipMode mode)
        {
            mode = s == "skip" ? OwnershipMode.Skip : OwnershipMode.Refuse;
            return s == "skip" || s == "refuse";
        }
    }

    /// <summary>A statically validated job. Create one with <see cref="Job.Parse"/> or <see cref="JobValidator.Validate"/>.</summary>
    public sealed class Job
    {
        internal Job(JsonObject raw, string jobId, string? title, AppKind app, JobDocument? document, LengthUnit unit,
            string? standard, string? outputDir, string? description, List<JobOp> ops, JobVerify verify)
        {
            Raw = raw; JobId = jobId; Title = title; App = app; Document = document; LengthUnit = unit;
            Standard = standard; OutputDir = outputDir; Description = description; Ops = ops; Verify = verify;
        }

        public JsonObject Raw { get; }
        public string JobId { get; }
        public string? Title { get; }
        public AppKind App { get; }
        public JobDocument? Document { get; }
        public LengthUnit LengthUnit { get; }
        public string? Standard { get; }
        public string? OutputDir { get; }
        public string? Description { get; }
        public IReadOnlyList<JobOp> Ops { get; }
        public JobVerify Verify { get; }
        /// <summary>Job member <c>ownership</c> (Revit worksharing); <see cref="OwnershipMode.Refuse"/> when absent.</summary>
        public OwnershipMode Ownership { get; internal set; }

        /// <summary>The undo step name: "SA: &lt;title&gt;" (falls back to the job id).</summary>
        public string UndoName => "SA: " + (string.IsNullOrWhiteSpace(Title) ? JobId : Title);

        public JobOp? FindOp(string id) => Ops.FirstOrDefault(o => o.Id == id);

        /// <summary>Validates against <paramref name="registry"/> and returns the job, or throws <see cref="JobValidationException"/>.</summary>
        public static Job Parse(JsonValue json, OpRegistry registry)
        {
            var v = JobValidator.Validate(json, registry);
            if (!v.Ok || v.Job is null) throw new JobValidationException(v.Issues);
            return v.Job;
        }

        public static Job Parse(string text, OpRegistry registry)
        {
            JsonValue json;
            try { json = JsonValue.Parse(text); }
            catch (JsonParseException e) { throw new JobValidationException(new[] { new Issue(JsonPath.Root, IssueCodes.Json, e.Message) }); }
            return Parse(json, registry);
        }

        public JsonObject ToJson() => (JsonObject)Raw.DeepClone();
    }
}
