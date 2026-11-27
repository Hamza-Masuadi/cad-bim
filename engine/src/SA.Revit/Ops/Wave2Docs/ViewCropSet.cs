using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// view.crop.set: a view's crop to a rectangle, a polygon (plans) or a room, grown by <c>offset</c>.
    ///
    /// <para><b>How.</b> The request becomes model points on the view's plane. A rectangle that lines up with the crop
    /// box (every view except a rotated plan) is written to View.CropBox in the box's own transform, keeping its depth
    /// range, after RemoveCropRegionShape when a shape was set. Polygons, rooms in plans, and rectangles in rotated
    /// plans go through ViewCropRegionShapeManager.SetCropShape, which the API documents as taking one closed loop of
    /// straight lines in a plane parallel to the view and as refused when the view does not allow a non-rectangular
    /// crop (CanHaveShape); IsCropRegionShapeValid is checked first. A room's outer boundary (finish faces, the loop
    /// with the largest area) is offset outward with CurveLoop.CreateViaOffset (positive offsets go to the right of
    /// the loop, so the sign follows IsCounterclockwise) and arcs are tessellated. In sections and elevations a room
    /// gives a rectangle: its bounding box along the view's right direction and its height.</para>
    ///
    /// <para><b>Refused.</b> Templates, sheets, schedules, 3D, drafting views and legends; views whose crop is held by a
    /// scope box (VIEWER_VOLUME_OF_INTEREST_CROP) or controlled by their template (VIEWER_CROP_REGION). The view is
    /// declared as modified.</para>
    ///
    /// <para><b>Readback.</b> Crop on, frame visibility, annotation crop when given, and the crop box against the
    /// requested points, both measured in the crop box's own frame (Revit keeps the box as the bounds of a shape).
    /// The extent output is the crop box in the view's 2D frame.</para>
    /// </summary>
    public sealed class ViewCropSet : OpHandler
    {
        public override string Name => "view.crop.set";

        private sealed class Plan
        {
            public View View = null!;
            public List<XYZ> Points = new List<XYZ>();
            public bool Shape;
            public bool Visible;
            public bool? Annotation;
        }

        private static readonly ViewType[] Croppable =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section, ViewType.Elevation, ViewType.Detail,
        };

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var p = new Plan { Visible = args.GetBool("visible") };
            if (args.Has("annotationCrop")) p.Annotation = args.GetBool("annotationCrop");
            string at = ctx.FieldPath(args, "view");
            var v = DocsUtil.View(ctx, args.Get("view"), at, issues, out bool pending);
            if (pending || v is null) return null;
            if (v.IsTemplate || v is View3D || v is TableView || !Croppable.Contains(v.ViewType) || !(W2.IsPlanLike(v) || W2.IsVertical(v)))
            {
                OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, DocsUtil.Describe(v) + " has no crop region this op can set");
                return null;
            }
            var scope = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
            if (scope != null && ElementIds.IsValid(scope))
                OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "the crop of " + DocsUtil.Describe(v) + " is held by scope box " + ElementIds.ToWire(scope) + "; change or clear the scope box first");
            if (DocsUtil.TemplateOf(v) is View tpl && DocsUtil.TemplateControls(tpl, BuiltInParameter.VIEWER_CROP_REGION))
                OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "template '" + tpl.Name + "' controls the crop of " + DocsUtil.Describe(v));
            p.View = v;
            double off = args.GetLength("offset");
            double tol = ctx.Units.LengthToleranceInternal;
            bool rotatedPlan = W2.IsPlanLike(v) && Math.Abs((DocsUtil.Flat(v.RightDirection) ?? XYZ.BasisX).DotProduct(XYZ.BasisX)) < 1 - 1e-9;

            if (args.Has("rect"))
            {
                var r = args.GetObject("rect")!;
                var min = r.GetPoint2("min");
                var max = r.GetPoint2("max");
                if (max.X <= min.X || max.Y <= min.Y)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "rect"), RevitIssueCodes.Invalid, "rect max must be above and to the right of min");
                    return null;
                }
                p.Points = RectPoints(ctx, v, Rect2.FromCorners(new Point2(min.X - off, min.Y - off), new Point2(max.X + off, max.Y + off)));
                p.Shape = rotatedPlan;
            }
            else if (args.Has("boundary"))
            {
                if (!W2.IsPlanLike(v))
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.NotAllowed, "a crop polygon is for plans; give rect for " + DocsUtil.Describe(v));
                    return null;
                }
                var pts = args.GetPoint2List("boundary");
                var loop = W2.IsSimplePolygon(pts, ctx.Units.LengthTolerance) ? W2.Loop(W2.ToModel(ctx, v, pts), tol) : null;
                if (loop is null)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Invalid, "boundary must be a simple polygon (no crossing edges, no repeated points)");
                    return null;
                }
                p.Points = Grow(loop, ctx.Units.LengthToInternal(off));
                p.Shape = true;
            }
            else
            {
                string rp = ctx.FieldPath(args, "room");
                var e = W2.Element(ctx, args.Get("room"), rp, issues, out bool roomPending);
                if (roomPending || e is null) return null;
                if (!(e is Room room) || room.Area <= 0)
                {
                    OpContext.Error(issues, rp, RevitIssueCodes.Invalid, "element " + ElementIds.ToWire(e.Id) + " is not a placed, enclosed room");
                    return null;
                }
                if (W2.IsPlanLike(v))
                {
                    var best = OuterLoop(room, W2.PlanZ(v), tol);
                    if (best is null) { OpContext.Error(issues, rp, RevitIssueCodes.Invalid, "room " + ElementIds.ToWire(room.Id) + " has no usable boundary"); return null; }
                    p.Points = Grow(best, ctx.Units.LengthToInternal(off));
                    p.Shape = true;
                }
                else
                {
                    var bb = room.get_BoundingBox(null);
                    if (bb is null) { OpContext.Error(issues, rp, RevitIssueCodes.Invalid, "room " + ElementIds.ToWire(room.Id) + " has no extent"); return null; }
                    var right = W2.XAxis(v);
                    var along = new[] { bb.Min, new XYZ(bb.Max.X, bb.Min.Y, 0), new XYZ(bb.Min.X, bb.Max.Y, 0), bb.Max }
                        .Select(c => ctx.Units.LengthFromInternal((c - v.Origin).DotProduct(right))).ToList();
                    double baseZ = DocsUtil.BaseZ(doc);
                    p.Points = RectPoints(ctx, v, Rect2.FromCorners(
                        new Point2(along.Min() - off, ctx.Units.LengthFromInternal(bb.Min.Z - baseZ) - off),
                        new Point2(along.Max() + off, ctx.Units.LengthFromInternal(bb.Max.Z - baseZ) + off)));
                }
            }

            var mgr = v.GetCropRegionShapeManager();
            if (p.Shape)
            {
                if (mgr is null || !mgr.CanHaveShape)
                {
                    OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, DocsUtil.Describe(v) + " does not allow a non-rectangular crop" + (rotatedPlan ? ", and the plan is rotated so a project-coordinate rect is not a crop box" : ""));
                    return null;
                }
                var loop = W2.Loop(p.Points, tol);
                if (loop is null || !mgr.IsCropRegionShapeValid(loop))
                {
                    OpContext.Error(issues, args.Has("room") ? ctx.FieldPath(args, "room") : args.Has("boundary") ? ctx.FieldPath(args, "boundary") : ctx.FieldPath(args, "rect"),
                        RevitIssueCodes.Invalid, "Revit says this crop shape is not valid for " + DocsUtil.Describe(v));
                    return null;
                }
            }
            if (declare && !DocsUtil.HasErrors(issues)) ctx.DeclareModifies(v.Id);
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static List<XYZ> RectPoints(OpContext ctx, View v, Rect2 r) =>
            new[] { new Point2(r.MinX, r.MinY), new Point2(r.MaxX, r.MinY), new Point2(r.MaxX, r.MaxY), new Point2(r.MinX, r.MaxY) }
                .Select(c => W2.ToModel(ctx, v, c)).ToList();

        /// <summary>The room's finish boundary loop with the largest area, flattened to <paramref name="z"/>, arcs tessellated.</summary>
        private static CurveLoop? OuterLoop(Room room, double z, double tol)
        {
            var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
            CurveLoop? best = null;
            double bestArea = 0;
            if (loops is null) return null;
            foreach (var segs in loops)
            {
                var pts = new List<XYZ>();
                foreach (var s in segs)
                {
                    var tess = s.GetCurve().Tessellate();
                    for (int i = 0; i < tess.Count - 1; i++) pts.Add(new XYZ(tess[i].X, tess[i].Y, z));
                }
                if (pts.Count < 3) continue;
                double a = new Polygon2(pts.Select(q => new Point2(q.X, q.Y))).Area;
                if (a <= bestArea) continue;
                var lp = W2.Loop(pts, tol);
                if (lp is null) continue;
                best = lp;
                bestArea = a;
            }
            return best;
        }

        /// <summary>The loop grown outward by <paramref name="offset"/> (internal units), as straight-line vertices.</summary>
        private static List<XYZ> Grow(CurveLoop loop, double offset)
        {
            var grown = offset > 0 ? CurveLoop.CreateViaOffset(loop, loop.IsCounterclockwise(XYZ.BasisZ) ? offset : -offset, XYZ.BasisZ) : loop;
            var pts = new List<XYZ>();
            foreach (var c in grown)
            {
                var tess = c.Tessellate();
                for (int i = 0; i < tess.Count - 1; i++) pts.Add(tess[i]);
            }
            return pts;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var v = p.View;
            var mgr = v.GetCropRegionShapeManager();
            v.CropBoxActive = true;
            if (p.Shape) mgr.SetCropShape(W2.Loop(p.Points, ctx.Units.LengthToleranceInternal)!);
            else
            {
                if (mgr != null && mgr.ShapeSet) mgr.RemoveCropRegionShape();
                var box = v.CropBox;
                var inv = box.Transform.Inverse;
                var local = p.Points.Select(q => inv.OfPoint(q)).ToList();
                v.CropBox = new BoundingBoxXYZ
                {
                    Transform = box.Transform,
                    Min = new XYZ(local.Min(q => q.X), local.Min(q => q.Y), box.Min.Z),
                    Max = new XYZ(local.Max(q => q.X), local.Max(q => q.Y), box.Max.Z),
                };
            }
            v.CropBoxVisible = p.Visible;
            if (p.Annotation.HasValue)
            {
                var ap = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (ap is null || ap.IsReadOnly) ctx.Warn("the annotation crop of " + DocsUtil.Describe(v) + " cannot be set");
                else ap.Set(p.Annotation.Value ? 1 : 0);
            }
            result.Modified.Add(ElementIds.ToWire(v.Id));
            result.Outputs.Set("view", ElementIds.ToWire(v.Id));
            var flat = p.Points.Select(q => W2.FromModel(ctx, v, q)).ToList();
            result.Outputs.Set("extent", W2.RectJson(Rect2.FromCorners(new Point2(flat.Min(q => q.X), flat.Min(q => q.Y)), new Point2(flat.Max(q => q.X), flat.Max(q => q.Y)))));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var v = p.View;
            var rb = result.Readback;
            rb.Set("view", ElementIds.ToWire(v.Id));
            check.Flag(rb, "cropActive", v.CropBoxActive, true);
            check.Flag(rb, "cropVisible", v.CropBoxVisible, p.Visible);
            if (p.Annotation.HasValue && v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE) is Parameter ap && !ap.IsReadOnly)
                check.Flag(rb, "annotationCrop", ap.AsInteger() != 0, p.Annotation.Value);
            rb.Set("shape", v.GetCropRegionShapeManager()?.ShapeSet ?? false);
            var box = v.CropBox;
            var inv = box.Transform.Inverse;
            var local = p.Points.Select(q => inv.OfPoint(q)).ToList();
            var want = Rect2.FromCorners(new Point2(local.Min(q => q.X), local.Min(q => q.Y)), new Point2(local.Max(q => q.X), local.Max(q => q.Y)));
            var got = Rect2.FromCorners(new Point2(box.Min.X, box.Min.Y), new Point2(box.Max.X, box.Max.Y));
            double tol = ctx.Units.LengthToleranceInternal;
            if (Math.Abs(got.MinX - want.MinX) > tol || Math.Abs(got.MinY - want.MinY) > tol || Math.Abs(got.MaxX - want.MaxX) > tol || Math.Abs(got.MaxY - want.MaxY) > tol)
                check.Fail("crop box: read back " + Size(ctx, got) + " at a different place than the requested " + Size(ctx, want) + " (compared in the crop box's frame)");
            var ext = W2.CropExtent(ctx, v, v);
            rb.Set("extent", W2.RectJson(ext));
            result.Outputs.Set("extent", W2.RectJson(ext));
            check.Apply(result);
        }

        private static string Size(OpContext ctx, Rect2 r) =>
            W2.Fmt(ctx.Units.LengthFromInternal(r.Width)) + " x " + W2.Fmt(ctx.Units.LengthFromInternal(r.Height));
    }
}
