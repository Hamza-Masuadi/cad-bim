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
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// link.cad (Revit): Document.Link with DWGImportOptions. The view is always passed: it is required for
    /// ThisViewOnly and for centred placement, and otherwise documented as the source of the link's base level.
    /// placement point is origin placement with ReferencePoint at the point, on the view's level.
    ///
    /// The file's layers are only known once it is linked, so hideLayers is checked after Document.Link; an unknown
    /// layer fails the op (and the job rolls the link back). Layers are hidden in the given view only, through
    /// View.SetCategoryHidden on the import's subcategories, so the view is declared modified when hideLayers is given.
    ///
    /// A file already linked into the view (or model-wide) is refused unless allowDuplicate. Paths are compared as
    /// full paths, case-insensitively.
    /// </summary>
    public sealed class LinkCad : OpHandler
    {
        public override string Name => "link.cad";

        internal static string? LinkedPath(Element? type)
        {
            if (type is null || !type.IsExternalFileReference()) return null;
            try { return ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath()); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        internal static bool SamePath(string? a, string b)
        {
            if (a is null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        internal static string? FileProblem(string path, params string[] extensions)
        {
            if (!Path.IsPathRooted(path)) return "give a full path, not '" + path + "'";
            if (!extensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return "expected a " + string.Join(" or ", extensions) + " file";
            if (!File.Exists(path)) return "the file " + path + " does not exist on this machine";
            return null;
        }

        private static readonly Dictionary<string, ImportUnit> Units = new Dictionary<string, ImportUnit>(StringComparer.Ordinal)
        {
            ["auto"] = ImportUnit.Default, ["mm"] = ImportUnit.Millimeter, ["cm"] = ImportUnit.Centimeter, ["dm"] = ImportUnit.Decimeter,
            ["m"] = ImportUnit.Meter, ["in"] = ImportUnit.Inch, ["ft"] = ImportUnit.Foot, ["us_survey_ft"] = ImportUnit.USSurveyFoot, ["custom"] = ImportUnit.Custom,
        };

        private static ImportInstance[] Existing(Document doc, string path, View view) =>
            new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => i.IsLinked && (i.OwnerViewId == ElementId.InvalidElementId || i.OwnerViewId == view.Id) && SamePath(LinkedPath(doc.GetElement(i.GetTypeId())), path))
                .ToArray();

        private static View? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument) { OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "Revit does not link CAD files into family documents"); return null; }
            if (!OptionalFunctionalityUtils.IsDWGImportLinkAvailable()) { OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "DWG import and link is not available in this Revit installation"); return null; }
            string path = args.GetString("path");
            if (FileProblem(path, ".dwg", ".dxf") is string bad) OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.NotFound, bad);
            var v = Lookup.View(doc, args.Get("view"));
            if (!v.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "view"), v.Code, v.Error!); return null; }
            var view = v.Value!;
            if (view.IsTemplate || !view.CanBePrinted) OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view '" + view.Name + "' cannot take a CAD link (templates and non-printable views are refused)");
            if (args.GetBool("thisViewOnly") && view is View3D) OpContext.Error(issues, ctx.FieldPath(args, "thisViewOnly"), RevitIssueCodes.Invalid, "thisViewOnly is not available in 3D views");
            string placement = args.GetString("placement");
            if (placement == "point" && !args.Has("point")) OpContext.Error(issues, ctx.FieldPath(args, "point"), RevitIssueCodes.Invalid, "placement point needs point");
            string unit = args.GetString("unit");
            if (unit == "custom" && !args.Has("scale")) OpContext.Error(issues, ctx.FieldPath(args, "scale"), RevitIssueCodes.Invalid, "unit custom needs scale");
            if (args.Has("scale") && unit != "custom") OpContext.Error(issues, ctx.FieldPath(args, "unit"), RevitIssueCodes.Invalid, "scale is only used with unit custom");
            if (!args.GetBool("allowDuplicate") && File.Exists(path) && Existing(doc, path, view) is var dup && dup.Length > 0)
                OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.Exists, path + " is already linked into view '" + view.Name + "' (instance " + Kit.Id(dup[0].Id) + "); set allowDuplicate:true to add another");
            if (args.GetStringList("hideLayers").Count > 0) ctx.DeclareModifies(view.Id);
            return Kit.HasErrors(issues) ? null : view;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var view = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            string placement = args.GetString("placement");
            var opts = new DWGImportOptions
            {
                ThisViewOnly = args.GetBool("thisViewOnly"),
                Placement = placement == "centre" ? ImportPlacement.Centered : placement == "shared" ? ImportPlacement.Shared : ImportPlacement.Origin,
                Unit = Units[args.GetString("unit")],
                ColorMode = args.GetString("colorMode") == "black_and_white" ? ImportColorMode.BlackAndWhite : args.GetString("colorMode") == "invert" ? ImportColorMode.Inverted : ImportColorMode.Preserved,
                VisibleLayersOnly = args.GetBool("visibleLayersOnly"),
                AutoCorrectAlmostVHLines = args.GetBool("correctLines"),
                OrientToView = args.GetBool("orientToView"),
            };
            if (args.Has("scale")) opts.CustomScale = args.GetNumber("scale");
            XYZ? at = null;
            if (placement == "point")
            {
                at = ctx.Units.PointToInternal(args.GetPoint2("point"), view!.GenLevel?.ProjectElevation ?? 0);
                opts.ReferencePoint = at;
            }
            bool ok;
            ElementId id;
            try { ok = doc.Link(args.GetString("path"), opts, view, out id); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("link_failed", "Revit refused to link " + args.GetString("path") + ": " + e.Message); }
            if (!ok || !(doc.GetElement(id) is ImportInstance inst)) throw new OpFailedException("link_failed", "Revit did not link " + args.GetString("path"));

            var hidden = new JsonArray();
            var want = args.GetStringList("hideLayers");
            if (want.Count > 0)
            {
                var layers = inst.Category?.SubCategories.Cast<Category>().ToList() ?? new List<Category>();
                foreach (var name in want)
                {
                    var layer = layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal)) ?? layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (layer is null) throw new OpFailedException("layer_not_found", "the linked file has no layer '" + name + "'" + Lookup.Suggest(name, layers.Select(l => l.Name).ToList()));
                    if (!view!.CanCategoryBeHidden(layer.Id)) throw new OpFailedException("layer_not_hidden", "layer '" + layer.Name + "' cannot be hidden in view '" + view.Name + "'");
                    view.SetCategoryHidden(layer.Id, true);
                    hidden.Add(layer.Name);
                }
                result.Modified.Add(Kit.Id(view!.Id));
            }
            if (args.GetBool("pin")) inst.Pinned = true;
            result.Created.Add(Kit.Id(inst.Id));
            result.Outputs.Add("instance", Kit.Id(inst.Id));
            result.Outputs.Add("linkType", Kit.Id(inst.GetTypeId()));
            result.Outputs.Add("hiddenLayers", hidden);
            result.Readback.Add("view", Kit.Id(view!.Id));
            if (at != null) result.Readback.Add("point", ctx.Units.PointJson(at));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            if (!ElementIds.TryParse(result.Outputs.GetStringOrNull("instance"), out var id) || !(doc.GetElement(id) is ImportInstance inst))
            {
                check.Fail("the import instance does not exist after the op");
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            check.Flag(rb, "linked", inst.IsLinked, true);
            string path = args.GetString("path");
            string? have = LinkedPath(doc.GetElement(inst.GetTypeId()));
            check.Text(rb, "path", SamePath(have, path) ? path : have, path);
            ElementIds.TryParse(rb.GetStringOrNull("view"), out var viewId);
            if (args.GetBool("thisViewOnly")) check.Flag(rb, "viewSpecific", inst.OwnerViewId == viewId, true);
            check.Flag(rb, "pinned", inst.Pinned, args.GetBool("pin"));
            if (args.GetString("placement") == "point")
                check.Point(rb, "insertedAt", inst.GetTotalTransform().Origin, args.GetPoint2("point"), "link insertion point");
            if (doc.GetElement(viewId) is View view)
                foreach (var name in (result.Outputs["hiddenLayers"] as JsonArray ?? new JsonArray()).OfType<JsonString>().Select(s => s.Value))
                {
                    var layer = inst.Category?.SubCategories.Cast<Category>().FirstOrDefault(l => l.Name == name);
                    if (layer is null || !view.GetCategoryHidden(layer.Id)) check.Fail("layer '" + name + "' is not hidden in view '" + view.Name + "'");
                }
            check.Apply(result);
        }
    }
}
