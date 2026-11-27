using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// area.plan.create: ViewPlan.CreateAreaPlan(doc, areaSchemeId, levelId), then scale, template, name and crop.
    ///
    /// <para>Area schemes are looked up by name (FilteredElementCollector OfClass AreaScheme); the API has no area scheme
    /// constructor, so a missing one is an error naming the schemes there are. The default name is
    /// "&lt;level&gt; - &lt;scheme&gt;", the way the plan ops default to the level name. Another area plan of the same
    /// scheme and level is warned about, not refused: Revit allows it and offices sometimes keep two.</para>
    /// </summary>
    public sealed class AreaPlanCreate : OpHandler
    {
        public override string Name => "area.plan.create";

        private sealed class Plan
        {
            public AreaScheme Scheme = null!;
            public Level Level = null!;
            public ViewSetup Setup = null!;
            public Rect2? Crop;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            var s = Lookup.ByName(schemes, args.GetString("areaScheme"), "area scheme");
            if (!s.Ok) OpContext.Error(issues, ctx.FieldPath(args, "areaScheme"), s.Code, s.Error!);
            else p.Scheme = s.Value!;
            var l = Lookup.Level(doc, args.GetString("level"));
            if (!l.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), l.Code, l.Error!);
            else p.Level = l.Value!;
            string? def = s.Ok && l.Ok ? l.Value!.Name + " - " + s.Value!.Name : null;
            p.Setup = W2.Setup(ctx, args, ViewType.AreaPlan, def, issues, forReadback);
            if (args.Has("crop"))
            {
                var c = args.GetObject("crop")!;
                var min = c.GetPoint2("min");
                var max = c.GetPoint2("max");
                if (max.X <= min.X || max.Y <= min.Y)
                    OpContext.Error(issues, ctx.FieldPath(args, "crop"), RevitIssueCodes.Invalid, "crop max must be above and to the right of min");
                else p.Crop = Rect2.FromCorners(min, max);
            }
            if (!forReadback && s.Ok && l.Ok)
            {
                var same = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan && v.AreaScheme?.Id == p.Scheme.Id && v.GenLevel?.Id == p.Level.Id);
                if (same != null)
                    OpContext.Warning(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Exists, "area plan " + DocsUtil.Describe(same) + " already shows " + p.Scheme.Name + " on " + p.Level.Name);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var view = ViewPlan.CreateAreaPlan(ctx.Doc, p.Scheme.Id, p.Level.Id) ?? throw new OpFailedException("create_failed", "Revit did not create the area plan");
            result.Created.Add(ElementIds.ToWire(view.Id));
            p.Setup.Apply(view);
            if (p.Crop.HasValue) PlanViewCreateBase.CropTo(ctx, view, p.Crop.Value);
            ViewSetup.Outputs(result, view);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result) as ViewPlan;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (view is null || p is null)
            {
                check.Fail(view is null ? "the new area plan was not found after the op" : "could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message)));
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            if (p.Setup.Name is null) p.Setup.Name = p.Level.Name + " - " + p.Scheme.Name;
            p.Setup.Readback(ctx, view, rb, check);
            check.Text(rb, "areaScheme", view.AreaScheme?.Name, p.Scheme.Name);
            check.Text(rb, "level", view.GenLevel?.Name, p.Level.Name);
            check.Text(rb, "viewType", view.ViewType.ToString(), ViewType.AreaPlan.ToString());
            if (p.Crop.HasValue)
            {
                var r = PlanViewCreateBase.PlanCrop(ctx, view);
                var crop = new SA.Core.Json.JsonObject();
                check.Point(crop, "min", ctx.Units.PointToInternal(new Point2(r.MinX, r.MinY), 0), new Point2(p.Crop.Value.MinX, p.Crop.Value.MinY), "crop.min");
                check.Point(crop, "max", ctx.Units.PointToInternal(new Point2(r.MaxX, r.MaxY), 0), new Point2(p.Crop.Value.MaxX, p.Crop.Value.MaxY), "crop.max");
                rb.Set("crop", crop);
                check.Flag(rb, "cropActive", view.CropBoxActive, true);
            }
            check.Apply(result);
        }
    }
}
