using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// Shared body of view.plan.create and view.ceiling.create: ViewPlan.Create(doc, viewFamilyTypeId, levelId), then
    /// scale, template and name (<see cref="ViewSetup"/>), then the optional crop.
    ///
    /// Crop convention: <c>crop.min</c> / <c>crop.max</c> are plan points in project coordinates (internal origin, job
    /// units). The view's existing CropBox transform is kept; the four plan corners are mapped into the crop box's own
    /// coordinates with Transform.Inverse, so reflected ceiling plans (whose crop box axes are mirrored) get the same
    /// world rectangle as floor plans. The crop's Z range is left as Revit made it. The crop frame is hidden
    /// (CropBoxVisible = false), the professional default for sheet views (views-templates-graphics.md section 8).
    /// </summary>
    public abstract class PlanViewCreateBase : OpHandler
    {
        protected abstract ViewFamily Family { get; }
        protected abstract ViewType Kind { get; }

        private sealed class Plan
        {
            public Level Level = null!;
            public ViewSetup Setup = null!;
            public Rect2? Crop;
        }

        private Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var lv = Lookup.Level(doc, args.GetString("level"));
            if (!lv.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!); return null; }
            p.Level = lv.Value!;
            string? defaultName = null;
            if (!forReadback && args.GetStringOrNull("name") is null)
            {
                var vft = DocsUtil.ViewFamilyType(doc, Family, args.GetStringOrNull("viewFamilyType"), ctx.FieldPath(args, "viewFamilyType"), new List<Issue>());
                foreach (var candidate in new[] { p.Level.Name, p.Level.Name + " - " + (vft?.Name ?? Kind.ToString()) })
                    if (DocsUtil.NameTaken(doc, candidate, Kind) is null) { defaultName = candidate; break; }
                if (defaultName is null)
                    OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid,
                        "level '" + p.Level.Name + "' already has " + Kind + " views named after it; give a name (Revit's \"(1)\" suffixes are RV-028)");
            }
            p.Setup = ViewSetup.Build(ctx, args, Family, Kind, defaultName ?? (forReadback ? null : ""), issues, forReadback: forReadback);
            if (p.Setup.Name == "") p.Setup.Name = null;
            var crop = args.GetObject("crop");
            if (crop != null)
            {
                var a = crop.GetPoint2("min");
                var b = crop.GetPoint2("max");
                if (!(b.X > a.X) || !(b.Y > a.Y))
                    OpContext.Error(issues, ctx.FieldPath(args, "crop"), RevitIssueCodes.Invalid, "crop.max must be above and to the right of crop.min");
                else p.Crop = Rect2.FromCorners(a, b);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var view = ViewPlan.Create(doc, p.Setup.Type!.Id, p.Level.Id);
            result.Created.Add(ElementIds.ToWire(view.Id));
            p.Setup.Apply(view);
            if (p.Crop.HasValue) CropTo(ctx, view, p.Crop.Value);
            ViewSetup.Outputs(result, view);
        }

        internal static void CropTo(OpContext ctx, View view, Rect2 r)
        {
            var box = view.CropBox;
            var inv = box.Transform.Inverse;
            double z = box.Transform.Origin.Z;
            var local = new[]
            {
                inv.OfPoint(ctx.Units.PointToInternal(new Point2(r.MinX, r.MinY), z)), inv.OfPoint(ctx.Units.PointToInternal(new Point2(r.MaxX, r.MinY), z)),
                inv.OfPoint(ctx.Units.PointToInternal(new Point2(r.MaxX, r.MaxY), z)), inv.OfPoint(ctx.Units.PointToInternal(new Point2(r.MinX, r.MaxY), z)),
            };
            var nb = new BoundingBoxXYZ
            {
                Transform = box.Transform,
                Min = new XYZ(local.Min(q => q.X), local.Min(q => q.Y), box.Min.Z),
                Max = new XYZ(local.Max(q => q.X), local.Max(q => q.Y), box.Max.Z),
            };
            view.CropBoxActive = true;
            view.CropBox = nb;
            view.CropBoxVisible = false;
        }

        /// <summary>The crop rectangle in plan (project coordinates, job units) read from the view's CropBox.</summary>
        internal static Rect2 PlanCrop(OpContext ctx, View view)
        {
            var box = view.CropBox;
            var t = box.Transform;
            var pts = new[]
            {
                t.OfPoint(new XYZ(box.Min.X, box.Min.Y, box.Min.Z)), t.OfPoint(new XYZ(box.Max.X, box.Min.Y, box.Min.Z)),
                t.OfPoint(new XYZ(box.Max.X, box.Max.Y, box.Min.Z)), t.OfPoint(new XYZ(box.Min.X, box.Max.Y, box.Min.Z)),
            }.Select(q => ctx.Units.PointFromInternal(q)).ToList();
            return new Rect2(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X) - pts.Min(q => q.X), pts.Max(q => q.Y) - pts.Min(q => q.Y));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result) as ViewPlan;
            if (view is null) { check.Fail("the new view was not found after the op"); check.Apply(result); return; }
            var rb = result.Readback;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            p.Setup.Readback(ctx, view, rb, check);
            check.Flag(rb, "isPlan", view.ViewType == Kind, true, "view type " + Kind);
            check.Text(rb, "level", view.GenLevel?.Name, p.Level.Name);
            bool cropActive = view.CropBoxActive;
            check.Flag(rb, "cropActive", cropActive, p.Crop.HasValue ? true : (bool?)null);
            if (p.Crop.HasValue)
            {
                var c = PlanCrop(ctx, view);
                var want = p.Crop.Value;
                rb.Set("crop", new JsonObject().Add("min", DocsUtil.Xy(c.MinX, c.MinY)).Add("max", DocsUtil.Xy(c.MaxX, c.MaxY)));
                double tol = ctx.Units.LengthTolerance;
                if (Math.Abs(c.MinX - want.MinX) > tol || Math.Abs(c.MinY - want.MinY) > tol || Math.Abs(c.MaxX - want.MaxX) > tol || Math.Abs(c.MaxY - want.MaxY) > tol)
                    check.Fail("crop: read back " + c + ", requested " + want);
                check.Flag(rb, "cropVisible", view.CropBoxVisible, false);
            }
            rb.Set("state", "created");
            check.Apply(result);
        }
    }

    /// <summary>view.plan.create: a floor plan on a level. See <see cref="PlanViewCreateBase"/>.</summary>
    public sealed class ViewPlanCreate : PlanViewCreateBase
    {
        public override string Name => "view.plan.create";
        protected override ViewFamily Family => ViewFamily.FloorPlan;
        protected override ViewType Kind => ViewType.FloorPlan;
    }
}
