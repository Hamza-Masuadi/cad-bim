using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// Names the job will create before they exist, so a later op's validation can rely on an earlier op
    /// (layer.ensure then wall.draw on that layer). Kinds are free strings; use the constants.
    /// </summary>
    internal sealed class PlannedNames
    {
        public const string Layer = "layer";
        public const string Linetype = "linetype";
        public const string TextStyle = "textstyle";
        public const string DimStyle = "dimstyle";
        public const string Block = "block";
        public const string Layout = "layout";
        public const string AnnotationScale = "annoscale";

        private readonly Dictionary<string, HashSet<string>> _names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>Records that an op earlier in this job will create <paramref name="name"/> (symbol names compare case-insensitively).</summary>
        public void Add(string kind, string name)
        {
            if (!_names.TryGetValue(kind, out var set)) _names[kind] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(name);
        }

        public bool Contains(string kind, string name) => _names.TryGetValue(kind, out var set) && set.Contains(name);
    }

    /// <summary>
    /// Everything a handler may use. The document and database are the job's, found once by path and carried here:
    /// handlers never look up the active document. <see cref="Transaction"/> is the job's single top-level transaction and
    /// is only available during execution (it is null during validation).
    /// </summary>
    internal sealed class OpContext
    {
        private Transaction? _transaction;

        internal OpContext(Document document, Job job, DrawingUnits units, ProjectStandard? standard, RunMode mode, bool allowScript,
            SysVarScope sysVars, Report report)
        {
            Document = document;
            Database = document.Database;
            Job = job;
            Units = units;
            Standard = standard;
            Mode = mode;
            AllowScript = allowScript;
            SysVarScope = sysVars;
            Report = report;
        }

        public Document Document { get; }
        public Database Database { get; }
        public Job Job { get; }
        public LengthUnit JobUnit => Job.LengthUnit;
        public DrawingUnits Units { get; }
        /// <summary>The job's project standard, when the job names one (loaded and checked before validation).</summary>
        public ProjectStandard? Standard { get; }
        public RunMode Mode { get; }
        public bool AllowScript { get; }
        public PlannedNames Plan { get; } = new PlannedNames();
        public bool IsValidating => _transaction is null;
        internal SysVarScope SysVarScope { get; }
        internal Report Report { get; }

        /// <summary>
        /// Set by script.lisp immediately before it evaluates code: from then on the drawing may hold changes the job
        /// transaction cannot roll back.
        /// </summary>
        internal bool ScriptEvaluated { get; set; }

        /// <summary>Files ops wrote for publishing after the commit (see <see cref="StagedFiles"/>).</summary>
        internal StagedFiles Staging { get; } = new StagedFiles();

        /// <summary>A layout to make current once the job has committed (layout.create makeCurrent); the last request wins.</summary>
        internal string? LayoutAfterCommit { get; set; }

        /// <summary>The op being validated or executed.</summary>
        public JobOp CurrentOp { get; internal set; } = null!;

        /// <summary>Readback tolerance in job units (0.5 mm expressed in the job unit).</summary>
        public double LengthTolerance => Tolerance.DefaultLength(Job.LengthUnit);

        /// <summary>
        /// Unit of sizes on the printed sheet (owner decision 12): millimetres, or inches for an imperial job. A plain
        /// number in a paper field is in this unit; a value with a unit symbol is converted.
        /// </summary>
        public LengthUnit PaperUnit => Length.IsImperial(JobUnit) ? LengthUnit.Inch : LengthUnit.Millimetre;

        public double PaperToJob(double paper) => Length.Convert(paper, PaperUnit, JobUnit);

        public double JobToPaper(double job) => Length.Convert(job, JobUnit, PaperUnit);

        /// <summary>A paper field read in <see cref="PaperUnit"/> and returned in job units, as every other length.</summary>
        public double? PaperLengthOrNull(OpArgs args, string field) =>
            args.Get(field) is JsonValue v ? PaperToJob(OpArgs.ToLength(v, PaperUnit, args.Path + "." + field)) : (double?)null;

        public double PaperLength(OpArgs args, string field) =>
            PaperLengthOrNull(args, field) ?? throw new OpArgsException(args.Path + "." + field, "is required");

        public SA.Core.Geometry.Point2 PaperPoint(OpArgs args, string field)
        {
            var v = args.Get(field) ?? throw new OpArgsException(args.Path + "." + field, "is required");
            var p = OpArgs.ToPoint2(v, PaperUnit, args.Path + "." + field);
            return new SA.Core.Geometry.Point2(PaperToJob(p.X), PaperToJob(p.Y));
        }

        /// <summary>A job-unit length on the sheet as a rounded paper value, for outputs and readback.</summary>
        public double PaperOut(double job) => DrawingUnits.Round(JobToPaper(job));

        public JsonArray PaperOut(SA.Core.Geometry.Point2 job) => new JsonArray(PaperOut(job.X), PaperOut(job.Y));

        public Transaction Transaction =>
            _transaction ?? throw new InvalidOperationException("The job transaction exists only during execution; Validate must not write.");

        internal void SetTransaction(Transaction? t) => _transaction = t;

        /// <summary>Sets a system variable for this job only; the runner restores it in a finally block.</summary>
        public void SetSysVar(string name, object value) => SysVarScope.Set(name, value);

        public void Warn(string text) => Report.Warnings.Add(new ReportWarning("engine", text, CurrentOp?.Id));

        public void AppWarning(string text) => Report.Warnings.Add(new ReportWarning("app", text, CurrentOp?.Id));

        /// <summary>
        /// True when the raw (unresolved) value of <paramref name="field"/> contains a "$ref" or a preflight pending marker.
        /// During validation such values are still reference strings; skip document checks on them (they are re-validated
        /// against the schema after resolution, just before execution).
        /// </summary>
        public bool IsRef(OpArgs args, string field) => args.IsRef(field);

        /// <summary>Adds an error issue at args.field (or args when field is null).</summary>
        public static void Error(List<Issue> issues, OpArgs args, string? field, string code, string message) =>
            issues.Add(new Issue(field is null ? args.Path : JsonPath.Key(args.Path, field), code, message));

        public static void Warning(List<Issue> issues, OpArgs args, string? field, string code, string message) =>
            issues.Add(new Issue(field is null ? args.Path : JsonPath.Key(args.Path, field), code, message, IssueSeverity.Warning));

        public static string HandleOf(ObjectId id) => id.Handle.ToString();

        /// <summary>The object id for a handle string, or ObjectId.Null when there is none or it is erased.</summary>
        public ObjectId IdOf(string handle)
        {
            try
            {
                long value = Convert.ToInt64(handle, 16);
                if (Database.TryGetObjectId(new Handle(value), out ObjectId id) && !id.IsErased) return id;
            }
            catch (FormatException) { }
            catch (OverflowException) { }
            return ObjectId.Null;
        }
    }
}
