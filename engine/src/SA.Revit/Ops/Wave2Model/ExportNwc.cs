using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Output;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// export.nwc (Revit): Document.Export(folder, name, NavisworksExportOptions), an optional exporter; when
    /// OptionalFunctionalityUtils.IsNavisworksExporterAvailable() is false the op is refused before anything runs.
    ///
    /// The model is not changed and no transaction is opened (ModifiesModel false). The op must not share its job with
    /// an op that changes the model: such a job runs inside a transaction group, and Revit documents that exports are
    /// refused in some application modes. Other exports and read-only ops may share the job.
    ///
    /// Staged like the other exports (see OutputSupport): Revit writes into a scratch folder in the op's staging
    /// folder; a rehearsal checks the file there and deletes it, a commit puts it in place after the job.
    /// Readback: the file exists and is not empty (the NWC format is not documented, so its content is not read).
    /// </summary>
    public sealed class ExportNwc : OpHandler
    {
        public override string Name => "export.nwc";
        public override bool ModifiesModel => false;

        private sealed class Plan
        {
            public string Path = "";
            public View3D? View;
            public List<ElementId> Elements = new List<ElementId>();
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
            {
                OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "the Navisworks exporter is not installed in this Revit; install the Navisworks NWC export utility for this Revit release, then run the op again");
                return null;
            }
            if (doc.IsFamilyDocument) { OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "NWC export is for project documents"); return null; }
            var engine = Host.EngineHost.Current;
            var changing = ctx.Job.Job.Ops.Where(o => o.Index != ctx.Op.Index && (engine?.Handlers.Find(o.Op)?.ModifiesModel ?? true)).Select(o => o.Id + " (" + o.Op + ")").ToList();
            if (changing.Count > 0)
                OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "export.nwc must run in a job without model changes; this job also runs " + string.Join(", ", changing.Take(5)) + ". Run the export as its own job after the change has committed.");

            string? dir = OutputSupport.OutputDir(ctx, args, issues);
            string pattern = args.GetString("fileName");
            var unknown = OutputSupport.UnknownPlaceholders(pattern);
            if (unknown.Count > 0)
                OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid, "unknown placeholder(s) " + string.Join(", ", unknown.Select(u => "{" + u + "}")));

            string scope = args.GetString("scope");
            if (scope == "view")
            {
                if (!args.Has("view")) OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "scope view needs view");
                else
                {
                    var v = Lookup.View(doc, args.Get("view"));
                    if (!v.Ok) OpContext.Error(issues, ctx.FieldPath(args, "view"), v.Code, v.Error!);
                    else if (!(v.Value is View3D v3) || v3.IsTemplate) OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "'" + v.Value!.Name + "' is not a 3D view; NWC export by view takes a 3D view");
                    else p.View = v3;
                }
            }
            else if (args.Has("view")) OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view is used only with scope view");
            if (scope == "elements")
            {
                var listed = Kit.IdList(ctx, args, "elements", out bool pending);
                if (listed.Count == 0 && !pending)
                    OpContext.Error(issues, ctx.FieldPath(args, "elements"), RevitIssueCodes.Invalid, args.Has("elements") ? "elements lists no element to export" : "scope elements needs elements");
                foreach (var kv in listed)
                {
                    if (!ElementIds.TryParse(kv.Value, out var id) || doc.GetElement(id) is null) OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist");
                    else p.Elements.Add(id);
                }
            }
            else if (args.Has("elements")) OpContext.Error(issues, ctx.FieldPath(args, "elements"), RevitIssueCodes.Invalid, "elements is used only with scope elements");
            if (Kit.HasErrors(issues) || dir is null) return null;

            p.Path = System.IO.Path.Combine(dir, OutputSupport.FileName(OutputSupport.Expand(pattern, doc, p.View, ctx.JobId), ".nwc"));
            return OutputSupport.CheckTarget(p.Path, args.GetBool("overwrite"), new HashSet<string>(StringComparer.OrdinalIgnoreCase), issues, ctx.FieldPath(args, "fileName")) ? p : null;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static NavisworksExportOptions Options(OpArgs args, Plan p)
        {
            string scope = args.GetString("scope");
            string prm = args.GetString("parameters");
            var o = new NavisworksExportOptions
            {
                ExportScope = scope == "view" ? NavisworksExportScope.View : scope == "elements" ? NavisworksExportScope.SelectedElements : NavisworksExportScope.Model,
                Coordinates = args.GetString("coordinates") == "internal" ? NavisworksCoordinates.Internal : NavisworksCoordinates.Shared,
                Parameters = prm == "none" ? NavisworksParameters.None : prm == "elements" ? NavisworksParameters.Elements : NavisworksParameters.All,
                ConvertElementProperties = args.GetBool("convertElementProperties"),
                ExportLinks = args.GetBool("exportLinks"),
                ExportParts = args.GetBool("exportParts"),
                DivideFileIntoLevels = args.GetBool("divideByLevels"),
                ExportRoomGeometry = args.GetBool("exportRoomGeometry"),
                ExportRoomAsAttribute = args.GetBool("roomsAsAttribute"),
                ExportElementIds = args.GetBool("elementIds"),
                ExportUrls = args.GetBool("urls"),
                FindMissingMaterials = args.GetBool("findMissingMaterials"),
                ConvertLinkedCADFormats = args.GetBool("convertLinkedCad"),
                ConvertLights = args.GetBool("convertLights"),
                FacetingFactor = args.GetNumber("facetingFactor"),
            };
            if (p.View != null) o.ViewId = p.View.Id;
            if (p.Elements.Count > 0) o.SetSelectedElementIds(p.Elements);
            return o;
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            OutputSupport.RequireNoTransaction(ctx, "NWC export");
            string stage = ctx.StageFolder(System.IO.Path.GetDirectoryName(p!.Path)!);
            string? scratch = null;
            try
            {
                string sc = OutputSupport.NewScratch(stage);
                scratch = sc;
                var opts = Options(args, p);
                OutputSupport.Guard(ctx, "NWC export", () => { ctx.Doc.Export(sc, "export", opts); return true; });
                var produced = Directory.GetFiles(sc, "*.nwc");
                if (produced.Length != 1) throw new OpFailedException(OutputSupport.Failed, "Revit wrote " + produced.Length + " NWC file(s); expected one");
                OutputSupport.Stage(ctx, stage, produced[0], p.Path, args.GetBool("overwrite"));
            }
            finally { OutputSupport.DeleteScratch(scratch); }
            if (!ctx.IsRehearsal) ctx.AddArtifact("nwc", p.Path, p.View != null ? ElementIds.ToWire(p.View.Id) : null);
            result.Outputs.Add("file", p.Path);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string path = result.Outputs.GetStringOrNull("file") ?? "";
            long bytes = OutputSupport.Size(ctx.ActualPath(path));
            result.Readback.Add("path", path);
            result.Readback.Add("bytes", bytes);
            if (bytes <= 0) check.Fail("file " + path + " is missing or empty");
            OutputSupport.NoteStaging(ctx, result);
            check.Apply(result);
        }
    }
}
