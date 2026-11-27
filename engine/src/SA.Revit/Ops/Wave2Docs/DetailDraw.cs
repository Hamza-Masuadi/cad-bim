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
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// detail.draw: view-specific 2D content in one view.
    ///
    /// <para><b>API.</b> Lines, polyline segments and arcs: Document.Create.NewDetailCurve(view, curve), documented to
    /// need a bound curve in the view's plane; points are put on that plane by the view frame (plans at the level's
    /// elevation, vertical views through View.Origin, drafting views and legends at z 0). Arcs: Arc.Create(centre,
    /// radius, start, end, xAxis, yAxis) with the frame's axes, so angles count counter-clockwise from the frame's x
    /// axis; a circle is two half arcs because a detail curve must be bound. Line styles: the projection
    /// GraphicsStyle of a Lines subcategory, set through CurveElement.LineStyle. Filled regions:
    /// FilledRegion.Create(doc, typeId, viewId, loops), type by name or Document.GetDefaultElementTypeId(
    /// FilledRegionType); masking: FilledRegion.CreateMaskingRegion(doc, viewId, loops); boundary styles checked with
    /// FilledRegion.IsValidLineStyleIdForFilledRegion and set with SetLineStyleId. Detail components:
    /// Document.Create.NewFamilyInstance(point, symbol, view) for view-based families, the symbol activated first,
    /// rotation through ElementTransformUtils.RotateElement about the view direction.</para>
    ///
    /// <para><b>Refused.</b> Sheets (draw in a view; RV-046), 3D views, schedules and templates; zero-length lines,
    /// equal arc angles, self-crossing region loops, holes outside their region, non view-based component families.</para>
    ///
    /// <para><b>Readback.</b> Every element exists in the view; curve end points against the request; line styles;
    /// region loop counts and type; component positions.</para>
    /// </summary>
    public sealed class DetailDraw : OpHandler
    {
        public override string Name => "detail.draw";

        private sealed class CurveItem
        {
            public Curve Curve = null!;
            public GraphicsStyle? Style;
            public string Path = "";
        }

        private sealed class RegionItem
        {
            public List<CurveLoop> Loops = new List<CurveLoop>();
            public FilledRegionType? Type;
            public GraphicsStyle? Style;
            public bool Masking;
            public string Path = "";
        }

        private sealed class ComponentItem
        {
            public FamilySymbol Symbol = null!;
            public XYZ Point = XYZ.Zero;
            public double Rotation;
            public string Path = "";
        }

        private sealed class Plan
        {
            public View View = null!;
            public List<CurveItem> Curves = new List<CurveItem>();
            public List<RegionItem> Regions = new List<RegionItem>();
            public List<ComponentItem> Components = new List<ComponentItem>();
        }

        private static readonly ViewType[] Allowed =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section,
            ViewType.Elevation, ViewType.Detail, ViewType.DraftingView, ViewType.Legend,
        };

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (pending || v is null) return null;
            if (v.IsTemplate || !Allowed.Contains(v.ViewType))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed,
                    "detail items go in plans, sections, elevations, detail and drafting views and legends; not in " + DocsUtil.Describe(v) + (v is ViewSheet ? " (draw in the view, RV-046)" : ""));
                return null;
            }
            var p = new Plan { View = v };
            double tol = ctx.Units.LengthToleranceInternal;
            var styles = new Dictionary<string, GraphicsStyle?>(StringComparer.Ordinal);
            GraphicsStyle? Style(string? name, string path)
            {
                if (name is null) return null;
                if (styles.TryGetValue(name, out var s)) return s;
                s = W2.LineStyle(doc, name, path, issues);
                styles[name] = s;
                return s;
            }
            var opStyle = Style(args.GetStringOrNull("lineStyle"), ctx.FieldPath(args, "lineStyle"));
            GraphicsStyle? ItemStyle(OpArgs a) => a.Has("lineStyle") ? Style(a.GetString("lineStyle"), ctx.FieldPath(a, "lineStyle")) : opStyle;
            void Add(Curve c, OpArgs a) => p.Curves.Add(new CurveItem { Curve = c, Style = ItemStyle(a), Path = a.Path });

            foreach (var a in args.GetObjectList("lines"))
            {
                var s = W2.ToModel(ctx, v, a.GetPoint2("start"));
                var e = W2.ToModel(ctx, v, a.GetPoint2("end"));
                if (s.DistanceTo(e) <= tol) { OpContext.Error(issues, a.Path, RevitIssueCodes.Invalid, "the line has no length"); continue; }
                Add(Line.CreateBound(s, e), a);
            }
            foreach (var a in args.GetObjectList("polylines"))
            {
                var pts = W2.ToModel(ctx, v, a.GetPoint2List("points"));
                if (a.GetBool("closed")) pts.Add(pts[0]);
                for (int i = 1; i < pts.Count; i++)
                {
                    if (pts[i - 1].DistanceTo(pts[i]) <= tol) { OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(a, "points"), i), RevitIssueCodes.Invalid, "repeats the previous point"); continue; }
                    Add(Line.CreateBound(pts[i - 1], pts[i]), a);
                }
            }
            foreach (var a in args.GetObjectList("arcs"))
            {
                double start = a.GetAngle("startAngle"), end = a.GetAngle("endAngle");
                double sweep = ((end - start) % 360 + 360) % 360;
                if (sweep < 1e-6) { OpContext.Error(issues, a.Path, RevitIssueCodes.Invalid, "startAngle and endAngle are the same; use circles for a full circle"); continue; }
                var c = W2.ToModel(ctx, v, a.GetPoint2("centre"));
                double r = ctx.Units.LengthToInternal(a.GetLength("radius"));
                double s0 = ctx.Units.AngleToInternal(start);
                Add(Arc.Create(c, r, s0, s0 + ctx.Units.AngleToInternal(sweep), W2.XAxis(v), W2.YAxis(v)), a);
            }
            foreach (var a in args.GetObjectList("circles"))
            {
                var c = W2.ToModel(ctx, v, a.GetPoint2("centre"));
                double r = ctx.Units.LengthToInternal(a.GetLength("radius"));
                Add(Arc.Create(c, r, 0, Math.PI, W2.XAxis(v), W2.YAxis(v)), a);
                Add(Arc.Create(c, r, Math.PI, 2 * Math.PI, W2.XAxis(v), W2.YAxis(v)), a);
            }
            FilledRegionType? defaultType = null;
            foreach (var pair in new[] { new { Field = "regions", Masking = false }, new { Field = "masking", Masking = true } })
                foreach (var a in args.GetObjectList(pair.Field))
                {
                    var item = new RegionItem { Masking = pair.Masking, Path = a.Path };
                    var outer = a.GetPoint2List("boundary");
                    if (!W2.IsSimplePolygon(outer, ctx.Units.LengthTolerance)) { OpContext.Error(issues, ctx.FieldPath(a, "boundary"), RevitIssueCodes.Invalid, "boundary must be a simple polygon"); continue; }
                    var outerPoly = new Polygon2(outer);
                    var loops = new List<List<Point2>> { outer };
                    var holes = a.Get("holes") as JsonArray ?? new JsonArray();
                    for (int i = 0; i < holes.Count; i++)
                    {
                        string hp = JsonPath.Index(ctx.FieldPath(a, "holes"), i);
                        var h = W2.FramePoints(ctx, v, holes[i], hp);
                        if (!W2.IsSimplePolygon(h, ctx.Units.LengthTolerance)) { OpContext.Error(issues, hp, RevitIssueCodes.Invalid, "hole must be a simple polygon"); continue; }
                        if (h.Any(q => outerPoly.Locate(q, ctx.Units.LengthTolerance) != PointLocation.Inside)) { OpContext.Error(issues, hp, RevitIssueCodes.Invalid, "hole must lie inside the boundary"); continue; }
                        loops.Add(h);
                    }
                    foreach (var l in loops)
                    {
                        var cl = W2.Loop(W2.ToModel(ctx, v, l), tol);
                        if (cl != null) item.Loops.Add(cl);
                    }
                    if (!pair.Masking)
                    {
                        if (a.GetStringOrNull("type") is string tn)
                        {
                            var f = Lookup.Type<FilledRegionType>(doc, tn, "filled region type");
                            if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(a, "type"), f.Code, f.Error!);
                            else item.Type = f.Value;
                        }
                        else
                        {
                            if (defaultType is null)
                            {
                                var id = doc.GetDefaultElementTypeId(ElementTypeGroup.FilledRegionType);
                                defaultType = (ElementIds.IsValid(id) ? doc.GetElement(id) as FilledRegionType : null)
                                    ?? new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().OrderBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
                            }
                            if (defaultType is null) OpContext.Error(issues, ctx.FieldPath(a, "type"), RevitIssueCodes.NotFound, "the model has no filled region type");
                            item.Type = defaultType;
                        }
                    }
                    if (a.GetStringOrNull("lineStyle") is string ls)
                    {
                        item.Style = Style(ls, ctx.FieldPath(a, "lineStyle"));
                        if (item.Style != null && !FilledRegion.IsValidLineStyleIdForFilledRegion(doc, item.Style.Id))
                            OpContext.Error(issues, ctx.FieldPath(a, "lineStyle"), RevitIssueCodes.Invalid, "line style '" + ls + "' cannot be used for a region boundary");
                    }
                    p.Regions.Add(item);
                }
            foreach (var a in args.GetObjectList("components"))
            {
                var f = Lookup.Symbol(doc, BuiltInCategory.OST_DetailComponents, a.GetString("type"), "detail item type");
                if (!f.Ok) { OpContext.Error(issues, ctx.FieldPath(a, "type"), f.Code, f.Error!); continue; }
                if (f.Value!.Family?.FamilyPlacementType != FamilyPlacementType.ViewBased)
                {
                    OpContext.Error(issues, ctx.FieldPath(a, "type"), RevitIssueCodes.Invalid, "'" + Lookup.FamilyTypeName(f.Value) + "' is not a view-based (2D) detail family");
                    continue;
                }
                p.Components.Add(new ComponentItem { Symbol = f.Value, Point = W2.ToModel(ctx, v, a.GetPoint2("point")), Rotation = ctx.Units.AngleToInternal(a.GetAngle("rotation")), Path = a.Path });
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var ids = new List<ElementId>();
            foreach (var c in p.Curves)
            {
                var dc = doc.Create.NewDetailCurve(p.View, c.Curve) ?? throw new OpFailedException("create_failed", c.Path + ": Revit did not create the detail curve");
                if (c.Style != null) dc.LineStyle = c.Style;
                ids.Add(dc.Id);
            }
            foreach (var r in p.Regions)
            {
                var fr = r.Masking ? FilledRegion.CreateMaskingRegion(doc, p.View.Id, r.Loops) : FilledRegion.Create(doc, r.Type!.Id, p.View.Id, r.Loops);
                if (fr is null) throw new OpFailedException("create_failed", r.Path + ": Revit did not create the region");
                if (r.Style != null) fr.SetLineStyleId(r.Style.Id);
                ids.Add(fr.Id);
            }
            foreach (var c in p.Components)
            {
                if (!c.Symbol.IsActive) c.Symbol.Activate();
                var fi = doc.Create.NewFamilyInstance(c.Point, c.Symbol, p.View) ?? throw new OpFailedException("create_failed", c.Path + ": Revit did not place the detail component");
                if (Math.Abs(c.Rotation) > 1e-12)
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(c.Point, c.Point + p.View.ViewDirection), c.Rotation);
                ids.Add(fi.Id);
            }
            result.Created.AddRange(ids.Select(ElementIds.ToWire));
            result.Outputs.Set("elements", W2.Ids(ids));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var ids = result.Created;
            int expected = p.Curves.Count + p.Regions.Count + p.Components.Count;
            if (ids.Count != expected) check.Fail(ids.Count + " element(s) created, " + expected + " requested");
            double tol = ctx.Units.LengthToleranceInternal;
            var items = new JsonArray();
            for (int i = 0; i < ids.Count && i < expected; i++)
            {
                var e = ElementIds.TryParse(ids[i], out var id) ? doc.GetElement(id) : null;
                var rec = new JsonObject().Add("id", ids[i]);
                items.Add(rec);
                if (e is null) { check.Fail("element " + ids[i] + " is missing after the op"); continue; }
                if (e.OwnerViewId != p.View.Id) check.Fail("element " + ids[i] + " is not in " + DocsUtil.Describe(p.View));
                if (i < p.Curves.Count)
                {
                    var want = p.Curves[i];
                    var dc = e as DetailCurve;
                    var got = dc?.GeometryCurve;
                    rec.Add("kind", want.Curve is Arc ? "arc" : "line");
                    if (got is null) { check.Fail("element " + ids[i] + " is not a detail curve"); continue; }
                    if (got.GetEndPoint(0).DistanceTo(want.Curve.GetEndPoint(0)) > tol || got.GetEndPoint(1).DistanceTo(want.Curve.GetEndPoint(1)) > tol)
                        check.Fail(want.Path + ": the detail curve's ends moved");
                    rec.Add("length", ctx.Units.LengthJson(got.Length));
                    check.Text(rec, "lineStyle", dc!.LineStyle?.Name, want.Style?.Name);
                }
                else if (i < p.Curves.Count + p.Regions.Count)
                {
                    var want = p.Regions[i - p.Curves.Count];
                    var fr = e as FilledRegion;
                    rec.Add("kind", want.Masking ? "masking" : "region");
                    if (fr is null) { check.Fail("element " + ids[i] + " is not a filled region"); continue; }
                    int loops = fr.GetBoundaries().Count;
                    rec.Add("loops", loops);
                    if (loops != want.Loops.Count) check.Fail(want.Path + ": " + loops + " boundary loop(s) read back, " + want.Loops.Count + " requested");
                    check.Flag(rec, "masking", fr.IsMasking, want.Masking);
                    if (!want.Masking) check.Text(rec, "type", doc.GetElement(fr.GetTypeId())?.Name, want.Type?.Name);
                }
                else
                {
                    var want = p.Components[i - p.Curves.Count - p.Regions.Count];
                    rec.Add("kind", "component");
                    var at = (e.Location as LocationPoint)?.Point;
                    if (at is null) { check.Fail("element " + ids[i] + " has no location point"); continue; }
                    check.Point(rec, "point", ctx.Units.PointToInternal(W2.FromModel(ctx, p.View, at), 0), W2.FromModel(ctx, p.View, want.Point), want.Path + ".point");
                    check.Text(rec, "type", W2.TypeName(doc, e), Lookup.FamilyTypeName(want.Symbol));
                }
            }
            result.Readback.Set("elements", items);
            check.Apply(result);
        }
    }
}
