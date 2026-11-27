using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Output
{
    /// <summary>
    /// export.ifc: the model to one IFC file with the built-in exporter options (IFCExportOptions).
    ///
    /// Transactions: Revit documents that "Exporting to IFC requires that document is modifiable, therefore there must
    /// be a transaction already open when this method is called" (RevitAPI.xml, Document.Export(String, String,
    /// IFCExportOptions)). So, unlike the view exports, this op keeps ModifiesModel true: the runner opens the op's
    /// Transaction, and Apply runs the export inside a SubTransaction that is always rolled back. Whatever the
    /// exporter writes into the document (IFC GUID parameters and the like) is undone, and the op's transaction commits
    /// with no change. The guide allows a SubTransaction "only to try something and undo it"; this is that use.
    ///
    /// Options (schema): version ifc2x3 -> IFCVersion.IFC2x3CV2, ifc4 -> IFC4RV, ifc4x3 -> IFC4x3 (2024-2026) or
    /// IFC4x3RV (2027) (Compat/OutputCompat); view -> FilterViewId (a 3D view; only what it shows is exported);
    /// baseQuantities -> ExportBaseQuantities; spaceBoundaries none/first/second -> SpaceBoundaryLevel 0/1/2.
    /// <c>setup</c> (a named IFC export configuration) is refused: those configurations are kept by the IFC exporter
    /// add-in, and the built-in IFCExportOptions has no member that loads one by name.
    ///
    /// ifc4x3 differs by release: 2024-2026 have only IFCVersion.IFC4x3 ("IFC4x3 file format"), while 2027 documents that
    /// member as "Coordination View ... Obsolete" and adds IFC4x3RV ("Reference View"), which 2027 uses. The same job can
    /// therefore give a different model view definition per release; readback reports the file's own (modelView).
    ///
    /// File: fileName ({drawing}, {jobId}, {date}, {view} = the filter view) with ".ifc" added when missing, exported into
    /// a scratch folder in the op's staging folder and put in place when the job commits (see OutputSupport); an
    /// existing file is refused unless overwrite.
    ///
    /// Readback: the file exists, is not empty, is a STEP file (ISO-10303-21), and its FILE_SCHEMA matches the
    /// requested version family (IFC2X3, IFC4, IFC4X3...). The size in bytes and the schema are reported.
    /// </summary>
    public sealed class ExportIfc : OpHandler
    {
        public override string Name => "export.ifc";

        private sealed class Plan
        {
            public string Path = "";
            public View? FilterView;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            bool ok = true;
            string? dir = OutputSupport.OutputDir(ctx, args, issues);
            if (dir is null) ok = false;
            if (args.GetStringOrNull("setup") is string setup)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "setup"), RevitIssueCodes.NotAllowed,
                    "IFC export setups ('" + setup + "') belong to the IFC exporter add-in and cannot be loaded through the built-in export options; give version, view, baseQuantities and spaceBoundaries instead");
                ok = false;
            }
            View? filter = null;
            if (args.Raw["view"] is JsonValue vv && !vv.IsNull && !ctx.IsPending(args, "view"))
            {
                var f = Lookup.View(ctx.Doc, vv);
                if (!f.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "view"), f.Code, f.Error!); ok = false; }
                else if (!(f.Value is View3D) || f.Value.IsTemplate)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "'" + f.Value!.Name + "' is not a 3D view; the IFC filter view must be a 3D view that is not a template");
                    ok = false;
                }
                else filter = f.Value;
            }
            string pattern = args.GetString("fileName");
            var unknown = OutputSupport.UnknownPlaceholders(pattern);
            if (unknown.Count > 0)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid,
                    "unknown placeholder(s) " + string.Join(", ", unknown.Select(u => "{" + u + "}")) + "; use {drawing}, {jobId}, {date} or {view}");
                ok = false;
            }
            if (!ok || dir is null) return null;
            string path = System.IO.Path.Combine(dir, OutputSupport.FileName(OutputSupport.Expand(pattern, ctx.Doc, filter, ctx.JobId), ".ifc"));
            if (!OutputSupport.CheckTarget(path, args.GetBool("overwrite"), new HashSet<string>(StringComparer.OrdinalIgnoreCase), issues, ctx.FieldPath(args, "fileName"))) return null;
            return new Plan { Path = path, FilterView = filter };
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plan = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            var doc = ctx.Doc;
            if (!doc.IsModifiable)
                throw new OpFailedException(OutputSupport.Refused, "IFC export needs an open transaction and the document is not modifiable; nothing was exported");
            string version = args.GetString("version");
            string boundaries = args.GetString("spaceBoundaries");
            var opts = new IFCExportOptions
            {
                FileVersion = OutputCompat.IfcVersion(version),
                ExportBaseQuantities = args.GetBool("baseQuantities"),
                SpaceBoundaryLevel = boundaries == "second" ? 2 : boundaries == "first" ? 1 : 0,
                FilterViewId = plan.FilterView?.Id ?? ElementId.InvalidElementId,
            };
            string stage = ctx.StageFolder(System.IO.Path.GetDirectoryName(plan.Path)!);
            string? scratch = null;
            try
            {
                string sc = OutputSupport.NewScratch(stage);
                scratch = sc;
                string baseName = System.IO.Path.GetFileNameWithoutExtension(plan.Path);
                bool exported;
                using (var sub = new SubTransaction(doc))
                {
                    if (sub.Start() != TransactionStatus.Started)
                        throw new OpFailedException(OutputSupport.Refused, "Revit refused to start the sub-transaction the IFC export runs in; nothing was exported");
                    try { exported = OutputSupport.Guard(ctx, "IFC export", () => doc.Export(sc, baseName, opts)); }
                    finally
                    {
                        // Always undo whatever the exporter wrote into the document.
                        if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    }
                }
                if (!exported) throw new OpFailedException(OutputSupport.Failed, "Revit reported that the IFC export failed");
                var produced = Directory.GetFiles(sc, "*.ifc");
                if (produced.Length != 1)
                    throw new OpFailedException(OutputSupport.Failed, "the IFC export wrote " + produced.Length + " .ifc file(s); expected one",
                        new JsonObject().Add("produced", JsonArray.FromStrings(Directory.GetFiles(sc).Select(p => System.IO.Path.GetFileName(p) ?? ""))));
                OutputSupport.Stage(ctx, stage, produced[0], plan.Path, args.GetBool("overwrite"));
            }
            finally { OutputSupport.DeleteScratch(scratch); }
            result.Outputs.Add("file", plan.Path);
            result.Readback.Add("ifcVersion", OutputCompat.IfcVersionName(version));
            if (plan.FilterView != null) result.Readback.Add("filterView", ElementIds.ToWire(plan.FilterView.Id));
            if (!ctx.IsRehearsal) ctx.AddArtifact("ifc", plan.Path, plan.FilterView != null ? ElementIds.ToWire(plan.FilterView.Id) : null);
        }

        /// <summary>The FILE_SCHEMA family a version argument must produce.</summary>
        private static bool SchemaMatches(string version, string schema)
        {
            string s = schema.ToUpperInvariant();
            switch (version)
            {
                case "ifc2x3": return s.StartsWith("IFC2X3", StringComparison.Ordinal);
                case "ifc4x3": return s.StartsWith("IFC4X3", StringComparison.Ordinal);
                default: return s.StartsWith("IFC4", StringComparison.Ordinal) && !s.StartsWith("IFC4X3", StringComparison.Ordinal);
            }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string path = result.Outputs.GetStringOrNull("file") ?? "";
            string actual = ctx.ActualPath(path);
            long bytes = OutputSupport.Size(actual);
            result.Readback.Add("path", path);
            result.Readback.Add("bytes", bytes);
            if (bytes <= 0) check.Fail("file " + path + " is missing or empty");
            else
            {
                var head = OutputSupport.Head(actual, 4096);
                string? schema = OutputSupport.IfcSchema(head);
                if (schema is null) check.Fail("file " + path + " is not an IFC STEP file (no ISO-10303-21 header)");
                else
                {
                    result.Readback.Add("schema", schema);
                    string? view = OutputSupport.IfcModelView(head);
                    result.Readback.Add("modelView", view is null ? (JsonValue)JsonValue.Null : view);
                    string version = args.GetString("version");
                    if (!SchemaMatches(version, schema)) check.Fail("file " + path + " declares schema '" + schema + "'; requested " + version);
                }
            }
            OutputSupport.NoteStaging(ctx, result);
            check.Apply(result);
        }
    }
}
