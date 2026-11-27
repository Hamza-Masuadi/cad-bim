using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// dim.grids: grid-to-grid strings and an overall dimension in a plan view. Only the straightforward case is
    /// implemented; everything else is refused with a message.
    ///
    /// <para><b>Supported.</b> A plan view (ViewPlan, not a template). Straight grids visible in the view
    /// (FilteredElementCollector(doc, viewId).OfClass(Grid), IsCurved false, not part of a multi-segment grid) that
    /// are parallel to the view's up direction ("vertical" grids) or right direction ("horizontal" grids) within
    /// 0.01 degree. Each set needs at least two grids. Grids at any other angle are skipped with a warning; a
    /// request that leaves nothing to dimension fails validation.</para>
    ///
    /// <para><b>Geometry.</b> Grid lines are read in the view (DatumPlane.GetCurvesInView(ViewSpecific, view), else
    /// Grid.Curve). Grids are ordered by their position across the set. <c>side</c> top-right (default, the usual
    /// side in dimensions-annotation.md section 2) puts the strings at the top of vertical grids and on the right of
    /// horizontal ones, in the view's up and right directions; bottom-left at the other ends; both at both. With E =
    /// the outermost grid end on that side, the overall string is offset inside E and the grid string a further
    /// stringSpacing (default 8 mm paper) inside, so the overall string is the outer one and both sit inside the grid
    /// bubbles. Paper distances are multiplied by the view scale. Default offset: 12 mm paper. The dimension line is a
    /// Line.CreateBound across the set, 5 mm (paper) past the outer grids, at the grids' Z.</para>
    ///
    /// <para><b>References.</b> new Reference(grid) for each grid, in order, passed to
    /// Document.Create.NewDimension(view, line, references[, dimensionType]). The overall string (first and last
    /// grid) is added only when the set has three or more grids. A set already dimensioned in the view with exactly
    /// the same grids is refused (an element is dimensioned only once, section 1).</para>
    ///
    /// <para><b>Readback.</b> Each dimension exists in the view, references the expected grids in order, has n-1
    /// segments, and each segment value equals the perpendicular distance between its two grids within tolerance.</para>
    /// </summary>
    public sealed class DimGrids : OpHandler
    {
        public override string Name => "dim.grids";

        private const double StringSpacingMm = 8;
        private const double DefaultOffsetMm = 12;
        private const double OverrunMm = 5;
        private static readonly double AngleTol = Math.Cos(0.01 * Math.PI / 180);

        private sealed class GridLine
        {
            public Grid Grid = null!;
            public double Pos;   // position across the set (along the set's normal)
            public double End;   // outermost end along the set's direction
            public double Start; // the other end
            public double Z;
        }

        private sealed class Set
        {
            public string Kind = ""; // "vertical" or "horizontal"
            public XYZ Across = XYZ.BasisX; // direction the string runs
            public XYZ Along = XYZ.BasisY;  // direction the grids run (strings are placed at the + end)
            public List<GridLine> Grids = new List<GridLine>();
        }

        private sealed class Plan
        {
            public ViewPlan View = null!;
            public List<Set> Sets = new List<Set>();
            public DimensionType? Type;
            public double Offset; // internal model units
            public double Spacing, Overrun;
            public bool Overall;
            public bool TopRight = true, BottomLeft;
        }

        private static Line? CurveInView(Grid g, View v)
        {
            try
            {
                foreach (var c in g.GetCurvesInView(DatumExtentType.ViewSpecific, v)) if (c is Line l) return l;
            }
            catch (Exception) { }
            return g.Curve as Line;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan { Overall = args.GetBool("overall") };
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (args.GetStringOrNull("dimensionType") is string dt)
            {
                var linear = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>().Where(t => t.StyleType == DimensionStyleType.Linear);
                var f = Lookup.ByName(linear, dt, "linear dimension type");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "dimensionType"), f.Code, f.Error!);
                else p.Type = f.Value;
            }
            if (pending || v is null) return DocsUtil.HasErrors(issues) ? null : p;
            if (!(v is ViewPlan vp) || v.IsTemplate)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "dim.grids works in plan views only; " + DocsUtil.Describe(v) + " is not one");
                return null;
            }
            p.View = vp;
            double scale = Math.Max(1, vp.Scale);
            double mm = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
            p.Offset = (ctx.PaperLengthOrNull(args, "offset") is double o ? ctx.Paper.LengthToInternal(o) : DefaultOffsetMm * mm) * scale;
            p.Spacing = (ctx.PaperLengthOrNull(args, "stringSpacing") is double sp ? ctx.Paper.LengthToInternal(sp) : StringSpacingMm * mm) * scale;
            string side = args.GetString("side");
            p.TopRight = side != "bottom-left";
            p.BottomLeft = side != "top-right";
            p.Overrun = OverrunMm * mm * scale;

            var right = DocsUtil.Flat(vp.RightDirection) ?? XYZ.BasisX;
            var up = DocsUtil.Flat(vp.UpDirection) ?? XYZ.BasisY;
            var vertical = new Set { Kind = "vertical", Across = right, Along = up };
            var horizontal = new Set { Kind = "horizontal", Across = up, Along = right };
            var skipped = new List<string>();
            foreach (var g in new FilteredElementCollector(doc, vp.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (g.IsCurved) { skipped.Add("'" + g.Name + "' (curved)"); continue; }
                if (ElementIds.IsValid(MultiSegmentGrid.GetMultiSegementGridId(g))) { skipped.Add("'" + g.Name + "' (multi-segment)"); continue; }
                var line = CurveInView(g, vp);
                if (line is null) { skipped.Add("'" + g.Name + "' (no straight line in the view)"); continue; }
                var dir = DocsUtil.Flat(line.Direction);
                if (dir is null) { skipped.Add("'" + g.Name + "' (not horizontal in plan)"); continue; }
                Set? set = Math.Abs(dir.DotProduct(up)) >= AngleTol ? vertical : Math.Abs(dir.DotProduct(right)) >= AngleTol ? horizontal : null;
                if (set is null) { skipped.Add("'" + g.Name + "' (not parallel to the view's axes)"); continue; }
                var a = line.GetEndPoint(0);
                var b = line.GetEndPoint(1);
                set.Grids.Add(new GridLine { Grid = g, Pos = a.DotProduct(set.Across), End = Math.Max(a.DotProduct(set.Along), b.DotProduct(set.Along)), Start = Math.Min(a.DotProduct(set.Along), b.DotProduct(set.Along)), Z = a.Z });
            }
            if (skipped.Count > 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "grids not dimensioned (only straight grids parallel to the view axes are handled): " + string.Join(", ", skipped.Take(12)));
            string direction = args.GetString("direction");
            foreach (var set in new[] { vertical, horizontal })
            {
                if (direction != "both" && direction != set.Kind) continue;
                set.Grids = set.Grids.OrderBy(x => x.Pos).ToList();
                double tol = ctx.Units.LengthToleranceInternal;
                for (int i = 1; i < set.Grids.Count; i++)
                    if (set.Grids[i].Pos - set.Grids[i - 1].Pos < tol)
                    {
                        OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "grids '" + set.Grids[i - 1].Grid.Name + "' and '" + set.Grids[i].Grid.Name + "' lie on the same line");
                    }
                if (set.Grids.Count < 2)
                {
                    OpContext.Warning(issues, ctx.FieldPath(args, "direction"), RevitIssueCodes.NotFound, "fewer than two " + set.Kind + " grids are visible in the view; that string is skipped");
                    continue;
                }
                var ids = new HashSet<long>(set.Grids.Select(x => ElementIds.ToLong(x.Grid.Id)));
                foreach (var d in new FilteredElementCollector(doc, vp.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
                {
                    var refs = new HashSet<long>();
                    foreach (Reference r in d.References) refs.Add(ElementIds.ToLong(r.ElementId));
                    if (refs.SetEquals(ids))
                        OpContext.Error(issues, ctx.FieldPath(args, "direction"), RevitIssueCodes.Exists,
                            "the " + set.Kind + " grids are already dimensioned in this view (dimension " + ElementIds.ToWire(d.Id) + "); dimension elements once");
                }
                p.Sets.Add(set);
            }
            if (p.Sets.Count == 0 && !DocsUtil.HasErrors(issues))
                OpContext.Error(issues, ctx.FieldPath(args, "direction"), RevitIssueCodes.NotFound, "no set of two or more parallel straight grids to dimension in " + DocsUtil.Describe(vp));
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static Line StringLine(Set set, double atAlong, double z, double overrun)
        {
            double a0 = set.Grids.First().Pos - overrun, a1 = set.Grids.Last().Pos + overrun;
            var lift = new XYZ(0, 0, z);
            return Line.CreateBound(set.Across * a0 + set.Along * atAlong + lift, set.Across * a1 + set.Along * atAlong + lift);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var dims = new List<string>();
            foreach (var set in p.Sets)
            {
                double z = set.Grids[0].Z;
                foreach (bool plus in new[] { true, false })
                {
                    if (plus ? !p.TopRight : !p.BottomLeft) continue;
                    // E, and the direction from E into the grids.
                    double edge = plus ? set.Grids.Max(x => x.End) : set.Grids.Min(x => x.Start);
                    double inward = plus ? -1 : 1;
                    var all = new ReferenceArray();
                    foreach (var g in set.Grids) all.Append(new Reference(g.Grid));
                    var gridString = StringLine(set, edge + inward * (p.Offset + p.Spacing), z, p.Overrun);
                    var d1 = p.Type != null ? doc.Create.NewDimension(p.View, gridString, all, p.Type) : doc.Create.NewDimension(p.View, gridString, all);
                    if (d1 is null) throw new OpFailedException("dimension_failed", "Revit did not create the " + set.Kind + " grid string");
                    dims.Add(ElementIds.ToWire(d1.Id));
                    if (p.Overall && set.Grids.Count >= 3)
                    {
                        var ends = new ReferenceArray();
                        ends.Append(new Reference(set.Grids.First().Grid));
                        ends.Append(new Reference(set.Grids.Last().Grid));
                        var overall = StringLine(set, edge + inward * p.Offset, z, p.Overrun);
                        var d2 = p.Type != null ? doc.Create.NewDimension(p.View, overall, ends, p.Type) : doc.Create.NewDimension(p.View, overall, ends);
                        if (d2 is null) throw new OpFailedException("dimension_failed", "Revit did not create the " + set.Kind + " overall dimension");
                        dims.Add(ElementIds.ToWire(d2.Id));
                    }
                }
                if (p.Overall && set.Grids.Count < 3) ctx.Warn("the " + set.Kind + " set has two grids, so the grid string is already the overall dimension");
            }
            result.Created.AddRange(dims);
            result.Outputs.Set("dimensions", JsonArray.FromStrings(dims));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var arr = new JsonArray();
            double tol = ctx.Units.LengthToleranceInternal;
            foreach (var idText in result.Created)
            {
                var d = ElementIds.TryParse(idText, out var id) ? doc.GetElement(id) as Dimension : null;
                if (d is null) { check.Fail("dimension " + idText + " is missing after the op"); continue; }
                var grids = new List<Grid>();
                foreach (Reference r in d.References) if (doc.GetElement(r.ElementId) is Grid g) grids.Add(g);
                var rec = new JsonObject().Add("id", idText).Add("grids", JsonArray.FromStrings(grids.Select(g => g.Name)));
                if (grids.Count != d.References.Size) check.Fail("dimension " + idText + " references something other than grids");
                if (d.View?.Id != (ViewSetupView(ctx, args)?.Id ?? d.View?.Id)) check.Fail("dimension " + idText + " is not in the requested view");
                var values = new List<double>();
                if (d.NumberOfSegments <= 1) values.Add(d.Value ?? 0);
                else foreach (DimensionSegment s in d.Segments) values.Add(s.Value ?? 0);
                rec.Add("values", new JsonArray(values.Select(x => ctx.Units.LengthJson(x)).ToArray()));
                if (grids.Count >= 2 && values.Count == grids.Count - 1)
                {
                    for (int i = 0; i < values.Count; i++)
                    {
                        var a = grids[i].Curve as Line;
                        var b = grids[i + 1].Curve as Line;
                        if (a is null || b is null) continue;
                        var n = DocsUtil.Flat(a.Direction);
                        if (n is null) continue;
                        var normal = new XYZ(-n.Y, n.X, 0);
                        double dist = Math.Abs((b.GetEndPoint(0) - a.GetEndPoint(0)).DotProduct(normal));
                        if (Math.Abs(dist - values[i]) > tol)
                            check.Fail("dimension " + idText + " segment " + i + " reads " + ctx.Units.LengthFromInternal(values[i]) + ", grids '" + grids[i].Name + "' and '" + grids[i + 1].Name + "' are " + ctx.Units.LengthFromInternal(dist) + " apart");
                    }
                }
                else check.Fail("dimension " + idText + " has " + values.Count + " segment(s) for " + grids.Count + " grids");
                arr.Add(rec);
            }
            if (result.Created.Count == 0) check.Fail("no dimension was created");
            result.Readback.Set("dimensions", arr);
            check.Apply(result);
        }

        private static View? ViewSetupView(OpContext ctx, OpArgs args)
        {
            var f = Lookup.View(ctx.Doc, args.Get("view"));
            return f.Ok ? f.Value : null;
        }
    }
}
