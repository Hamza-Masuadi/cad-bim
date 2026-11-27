using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    public enum OpPhase
    {
        /// <summary>Before anything runs. References to earlier ops are not resolved yet (see <see cref="OpContext.IsPending(JsonValue)"/>).</summary>
        Preflight,
        /// <summary>Just before the op's transaction, with every reference resolved.</summary>
        Execute,
    }

    /// <summary>Thrown by a handler's Execute to fail the op cleanly; the job is rolled back.</summary>
    public sealed class OpFailedException : Exception
    {
        public OpFailedException(string code, string message, JsonObject? details = null) : base(message)
        {
            Code = code;
            Details = details;
        }

        public string Code { get; }
        public JsonObject? Details { get; }
    }

    /// <summary>State shared by every op of one job. Created by the job runner; handlers read it through <see cref="OpContext"/>.</summary>
    internal sealed class JobContext
    {
        public JobContext(Document doc, Job job, RunMode mode, string outputDir, Report report)
        {
            Doc = doc;
            Job = job;
            Mode = mode;
            OutputDir = outputDir;
            Report = report;
            Units = new RevitUnits(doc, job.LengthUnit);
            Paper = new RevitUnits(doc, SA.Core.Units.Length.IsImperial(job.LengthUnit) ? SA.Core.Units.LengthUnit.Inch : SA.Core.Units.LengthUnit.Millimetre);
        }

        /// <summary>The document the job is bound to. Never replaced, never re-read from the UI.</summary>
        public Document Doc { get; }
        public Job Job { get; }
        public RunMode Mode { get; }
        public string OutputDir { get; }
        public Report Report { get; }
        public RevitUnits Units { get; }
        /// <summary>Units of lengths measured on the printed sheet: millimetres for a metric job, inches for an imperial one.</summary>
        public RevitUnits Paper { get; }
        /// <summary>Ids of elements created by earlier ops of this job.</summary>
        public HashSet<long> Created { get; } = new HashSet<long>();
        public Dictionary<string, OpResult> Results { get; } = new Dictionary<string, OpResult>(StringComparer.Ordinal);
        /// <summary>The job's standard (job.standard, relative paths against the model's folder), or null when it names none.</summary>
        public ProjectStandard? Standard { get; set; }
        /// <summary>The full path job.standard resolved to, or null.</summary>
        public string? StandardFile { get; set; }
        public List<StagedFile> Staged { get; } = new List<StagedFile>();
        public List<string> StageFolders { get; } = new List<string>();
        /// <summary>Ownership problems found so far, by element id (the objects OwnershipCheck.Problems returns).</summary>
        public Dictionary<long, JsonObject> NotEditable { get; } = new Dictionary<long, JsonObject>();
        /// <summary>Ops that ownership "skip" left with nothing to do; they did not run.</summary>
        public HashSet<string> SkippedOps { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>What a handler sees: the bound document, the op, units, and ways to declare what it touches.</summary>
    public sealed class OpContext
    {
        internal OpContext(JobContext job, JobOp op, OpPhase phase)
        {
            Job = job;
            Op = op;
            Phase = phase;
        }

        internal JobContext Job { get; }
        public JobOp Op { get; }
        public OpPhase Phase { get; }
        public bool IsPreflight => Phase == OpPhase.Preflight;

        /// <summary>The job's document. Use this, never an active-document lookup.</summary>
        public Document Doc => Job.Doc;
        internal RevitUnits Units => Job.Units;
        /// <summary>Paper units (millimetres, or inches for an imperial job). A plain number in a "paper units" field is in these units; a value with a unit symbol is converted.</summary>
        internal RevitUnits Paper => Job.Paper;
        internal double PaperLength(OpArgs args, string field) =>
            OpArgs.ToLength(args.Get(field) ?? throw new OpArgsException(FieldPath(args, field), "is required"), Paper.JobUnit, FieldPath(args, field));
        internal double? PaperLengthOrNull(OpArgs args, string field) =>
            args.Get(field) is JsonValue v ? OpArgs.ToLength(v, Paper.JobUnit, FieldPath(args, field)) : (double?)null;
        internal SA.Core.Geometry.Point2 PaperPoint(OpArgs args, string field) =>
            OpArgs.ToPoint2(args.Get(field) ?? throw new OpArgsException(FieldPath(args, field), "is required"), Paper.JobUnit, FieldPath(args, field));
        public string JobId => Job.Job.JobId;
        public RunMode Mode => Job.Mode;
        /// <summary>The job's output folder (job.outputDir, else %LOCALAPPDATA%\CadBim\jobs\&lt;jobId&gt;). Not created yet.</summary>
        public string OutputDir => Job.OutputDir;

        internal HashSet<long> Modifies { get; } = new HashSet<long>();
        internal HashSet<long> Deletes { get; } = new HashSet<long>();
        /// <summary>Elements the runner has left out of this op (job ownership "skip"); see <see cref="IsLeftOut"/>.</summary>
        internal HashSet<long> LeftOut { get; } = new HashSet<long>();
        /// <summary>Left-out elements the handler declared anyway, so it would still touch them.</summary>
        internal HashSet<long> DeclaredLeftOut { get; } = new HashSet<long>();

        /// <summary>
        /// True when the job runs with <c>ownership: "skip"</c> and this existing element is not editable by the user, so
        /// the op must leave it alone. The runner already removes such elements from the op's explicit id lists and
        /// expands an element filter into a list first; a handler that finds elements by itself (a type of a listed
        /// element, a host, a search) must skip them, and must not declare them.
        /// </summary>
        public bool IsLeftOut(ElementId id) => ElementIds.IsValid(id) && LeftOut.Contains(ElementIds.ToLong(id));

        /// <summary>
        /// Declares an existing element this op will change. Call it from Validate for every such element: the
        /// runner checks worksharing ownership of declared elements before anything runs.
        /// </summary>
        public void DeclareModifies(ElementId id)
        {
            if (!ElementIds.IsValid(id)) return;
            long x = ElementIds.ToLong(id);
            if (LeftOut.Contains(x)) DeclaredLeftOut.Add(x);
            else Modifies.Add(x);
        }

        /// <summary>
        /// Declares a change to a category's or subcategory's own settings (Category.Material, line weights, colour).
        /// Categories are not elements; in a workshared model their settings belong to the Object Styles workset. The
        /// category's projection graphics style is an element of that workset, so it is declared in the category's place:
        /// the ownership pre-flight checks it, and with ownership "skip" an op whose category is left out does not run.
        /// Returns false when Revit gives the category no graphics style; then only Revit's own check at commit covers it.
        /// </summary>
        public bool DeclareModifiesCategory(Category category)
        {
            GraphicsStyle? style = null;
            try { style = category.GetGraphicsStyle(GraphicsStyleType.Projection); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            if (style is null) return false;
            DeclareModifies(style.Id);
            return true;
        }

        /// <summary>Declares an element this op will delete. Only destructive ops may delete elements the job did not create.</summary>
        public void DeclareDeletes(ElementId id)
        {
            if (!ElementIds.IsValid(id)) return;
            long x = ElementIds.ToLong(id);
            if (LeftOut.Contains(x)) DeclaredLeftOut.Add(x);
            else Deletes.Add(x);
        }

        public bool IsCreatedByJob(ElementId id) => Job.Created.Contains(ElementIds.ToLong(id));

        /// <summary>An engine warning attached to this op in the report.</summary>
        public void Warn(string text) => Job.Report.Warnings.Add(new ReportWarning("engine", text, Op.Id));

        public void AddArtifact(string kind, string path, string? target) => Job.Report.Artifacts.Add(new Artifact(kind, path, target));

        /// <summary>The result of an earlier op of this job, or null.</summary>
        public OpResult? EarlierResult(string opId) => Job.Results.TryGetValue(opId, out var r) ? r : null;

        /// <summary>The job's standard, loaded and checked by the runner before any op runs; null when the job names none.</summary>
        public ProjectStandard? Standard => Job.Standard;

        // ----- Files written outside the model -----

        /// <summary>
        /// A new, empty folder for files this op writes. Rehearse: a private folder under the temp folder, deleted when
        /// the job ends, so a rehearsal never touches the output folder. Commit: a hidden folder inside
        /// <paramref name="finalDir"/> (same volume, so publishing is a rename), or with <paramref name="inTemp"/> a
        /// folder under the temp folder; files registered with <see cref="StageFile"/> are put at their final paths only
        /// after the job has committed, and the folder is deleted.
        /// </summary>
        public string StageFolder(string finalDir, bool inTemp = false)
        {
            string tag = Guid.NewGuid().ToString("N").Substring(0, 12);
            string dir = Mode == RunMode.Rehearse || inTemp
                ? Path.Combine(Path.GetTempPath(), Mode == RunMode.Rehearse ? "SA-rehearse" : "SA-stage", SafeName(JobId), SafeName(Op.Id) + "-" + tag)
                : Path.Combine(finalDir, ".sa-stage-" + tag);
            Directory.CreateDirectory(dir);
            Job.StageFolders.Add(dir);
            return dir;
        }

        /// <summary>Registers a staged file for its final path. Readback reads it at <see cref="ActualPath"/>.</summary>
        internal void StageFile(string staged, string final, bool overwrite, DateTime? finalWriteUtc = null, long? finalLength = null, bool copyOver = false) =>
            Job.Staged.Add(new StagedFile { Staged = staged, Final = final, Overwrite = overwrite, OpId = Op.Id, FinalWriteUtc = finalWriteUtc, FinalLength = finalLength, CopyOver = copyOver });

        /// <summary>Where the file planned for <paramref name="final"/> is now: its staged copy until the job commits.</summary>
        public string ActualPath(string final)
        {
            for (int i = Job.Staged.Count - 1; i >= 0; i--)
                if (string.Equals(Job.Staged[i].Final, final, StringComparison.OrdinalIgnoreCase)) return Job.Staged[i].Staged;
            return final;
        }

        public bool IsRehearsal => Mode == RunMode.Rehearse;

        private static string SafeName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++) if (Array.IndexOf(bad, c[i]) >= 0) c[i] = '_';
            return new string(c);
        }

        // ----- References not yet resolved (preflight) -----

        internal const string PendingKey = "$pending";

        /// <summary>True when the value is, or contains, a reference that is resolved only when the op runs.</summary>
        public static bool IsPending(JsonValue? v)
        {
            switch (v)
            {
                case JsonObject o:
                    if (o.ContainsKey(PendingKey)) return true;
                    foreach (var kv in o) if (IsPending(kv.Value)) return true;
                    return false;
                case JsonArray a:
                    foreach (var i in a) if (IsPending(i)) return true;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>True when the field holds an unresolved reference. Skip document checks for it in preflight.</summary>
        public bool IsPending(OpArgs args, string field) => IsPending(args.Raw[field]);

        /// <summary>Replaces each reference with a { "$pending": "$w1.walls" } marker and unescapes "$$".</summary>
        internal static JsonValue MarkPending(JsonValue value)
        {
            switch (value)
            {
                case JsonString s:
                    if (s.Value.StartsWith("$$", StringComparison.Ordinal)) return new JsonString(s.Value.Substring(1));
                    if (Refs.IsRefText(s.Value)) return new JsonObject().Add(PendingKey, s.Value);
                    return s;
                case JsonArray a:
                {
                    var c = new JsonArray();
                    foreach (var i in a) c.Add(MarkPending(i));
                    return c;
                }
                case JsonObject o:
                {
                    var c = new JsonObject();
                    foreach (var kv in o) c.Add(kv.Key, MarkPending(kv.Value));
                    return c;
                }
                default:
                    return value;
            }
        }

        // ----- Issues -----

        public string FieldPath(OpArgs args, string field) => JsonPath.Key(args.Path, field);

        public static void Error(List<Issue> issues, string path, string code, string message) =>
            issues.Add(new Issue(path, code, message));

        public static void Warning(List<Issue> issues, string path, string code, string message) =>
            issues.Add(new Issue(path, code, message, IssueSeverity.Warning));
    }

    /// <summary>Engine-level issue codes (wire error validation_failed), named for the handlers; the values are Core's <see cref="IssueCodes"/>.</summary>
    public static class RevitIssueCodes
    {
        public const string NoHandler = IssueCodes.NoHandler;
        public const string NotFound = IssueCodes.NotFound;
        public const string Ambiguous = IssueCodes.Ambiguous;
        public const string Mismatch = IssueCodes.ExistingDiffers;
        public const string Invalid = IssueCodes.InvalidValue;
        public const string ReadOnly = IssueCodes.ReadOnly;
        public const string Exists = IssueCodes.AlreadyExists;
        public const string FamilyDocument = IssueCodes.FamilyDocument;
        public const string NotAllowed = IssueCodes.NotAllowed;
        public const string Unchecked = IssueCodes.NotPrechecked;
    }

    /// <summary>
    /// A file an op wrote into a staging folder. In commit mode the runner moves it to <see cref="Final"/> only after the
    /// job has committed; in rehearse mode, and when the job fails, it is deleted with its folder.
    /// </summary>
    internal sealed class StagedFile
    {
        public string Staged = "";
        public string Final = "";
        /// <summary>Set by the runner when the file could not be published: its staging folder is kept.</summary>
        public bool Kept;
        public bool Overwrite;
        public string OpId = "";
        /// <summary>When set, the final file must still have this write time and length, or it is not replaced.</summary>
        public DateTime? FinalWriteUtc;
        public long? FinalLength;
        /// <summary>Publish by copying over the existing file (needs write access to the file only, not to its folder).</summary>
        public bool CopyOver;
    }
}
