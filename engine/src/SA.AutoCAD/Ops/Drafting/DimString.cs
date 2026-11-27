using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// dim.string: a continuous string of RotatedDimensions through the points, all on one dimension line. The string
    /// direction is first-to-last point (aligned), or +/-X (horizontal) or +/-Y (vertical); every segment is a
    /// RotatedDimension at that one rotation, so all segments measure along the same axis and share the same line,
    /// which lies at offset from the first point, positive to the left of the string direction. Points must progress
    /// along the string (no zero or backward segments). The optional overall dimension runs first to last point at
    /// overallOffset (default: offset plus twice the style's model text height, on the same side).
    /// Readback: every segment's measurement, and that every dimension line point lies on its line.
    /// </summary>
    internal sealed class DimString : OpHandler<DimString.StringPlan>
    {
        public override string Name => "dim.string";

        internal sealed class StringPlan
        {
            public DimStyleChoice Style = new DimStyleChoice();
            public string Layer = "";
            public List<DimPlanItem> Items = new List<DimPlanItem>();
            public Point2 Base, Dir;
            public Point2 OverallBase;
            public double Rotation;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static StringPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new StringPlan();
            var pts = args.GetPoint2List("points");
            double offset = args.GetLength("offset");
            string orientation = args.GetString("orientation");
            p.Style = DimCommon.Style(ctx, args, issues);
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "dimensions");
            if (pts.Count < 2) return p;
            if (!DimCommon.Direction(pts[0], pts[pts.Count - 1], orientation, ctx.LengthTolerance, out var dir, out string err))
            {
                OpContext.Error(issues, args, "points", "dimension_zero", "first and last point: " + err);
                return p;
            }
            p.Dir = dir;
            p.Rotation = Math.Atan2(dir.Y, dir.X);
            var n = dir.PerpLeft;
            p.Base = pts[0] + n * offset;
            double last = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double s = (pts[i] - pts[0]).Dot(dir);
                double len = s - last;
                if (len <= ctx.LengthTolerance)
                {
                    issues.Add(new Issue(JsonPath.Index(JsonPath.Key(args.Path, "points"), i), "string_order",
                        len < -ctx.LengthTolerance ? "point " + i + " lies behind the previous one along the string; give points in order" : "point " + i + " adds no length to the string (zero segment)"));
                }
                var mid = (pts[i - 1] + pts[i]) * 0.5;
                p.Items.Add(new DimPlanItem
                {
                    P1 = pts[i - 1], P2 = pts[i], Rotation = p.Rotation, Expected = Math.Abs((pts[i] - pts[i - 1]).Dot(dir)),
                    LinePoint = p.Base + dir * (mid - p.Base).Dot(dir),
                });
                last = s;
            }
            // Measured points on the dimension line side of it make the extension lines run backwards.
            foreach (var q in pts)
                if (Math.Sign((q - p.Base).Dot(n)) == Math.Sign(offset) && Math.Abs((q - p.Base).Dot(n)) > ctx.LengthTolerance)
                {
                    OpContext.Warning(issues, args, "offset", "string_crosses_points", "some points lie beyond the dimension line; the string crosses the geometry. Increase the offset.");
                    break;
                }
            if (args.GetBool("overall") && pts.Count > 2)
            {
                double? oo = args.GetLengthOrNull("overallOffset");
                if (oo is null && p.Style.TextHeight <= 0)
                    OpContext.Warning(issues, args, "overallOffset", "overall_offset_default", "the style's text height is not known yet; the overall offset is computed at execution");
                double gap = 2 * p.Style.TextHeight;
                double overallOffset = oo ?? (offset + Math.Sign(offset == 0 ? 1 : offset) * gap);
                if (oo.HasValue && Math.Abs(oo.Value) <= Math.Abs(offset) + ctx.LengthTolerance)
                    OpContext.Error(issues, args, "overallOffset", "overall_inside", "the overall dimension must lie outside the string (|overallOffset| > |offset|)");
                if (oo.HasValue && Math.Sign(oo.Value) != Math.Sign(offset))
                    OpContext.Error(issues, args, "overallOffset", "overall_side", "the overall dimension must be on the same side as the string");
                p.OverallBase = pts[0] + n * overallOffset;
                var first = pts[0];
                var lastPt = pts[pts.Count - 1];
                var mid = (first + lastPt) * 0.5;
                p.Items.Add(new DimPlanItem
                {
                    P1 = first, P2 = lastPt, Rotation = p.Rotation, Expected = Math.Abs((lastPt - first).Dot(dir)),
                    LinePoint = p.OverallBase + dir * (mid - p.OverallBase).Dot(dir), Overall = true,
                });
            }
            return p;
        }

        protected override StringPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var styleId = Db.SymbolId(tr, ctx.Database.DimStyleTableId, p.Style.Name);
            if (styleId.IsNull) throw new InvalidOperationException("Dimension style '" + p.Style.Name + "' is missing.");
            if (p.Style.Annotative) Anno.UseScale(ctx, p.Style.Scale);
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForWrite);
            foreach (var item in p.Items) result.Created.Add(DimCommon.Create(ctx, ms, item, styleId, p.Layer, p.Style.Annotative, null));
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, StringPlan p)
        {
            var mismatches = new List<string>();
            var list = new JsonArray();
            var measurements = new JsonArray();
            double sum = 0;
            var tr = ctx.Transaction;
            foreach (var item in p.Items)
            {
                double? m = DimCommon.Verify(ctx, item, p.Style.Name, p.Layer, p.Style.Annotative, null, mismatches, list);
                if (!item.Overall && m.HasValue) { measurements.Add(AcadValues.Round(m.Value)); sum += m.Value; }
                var id = ctx.IdOf(item.Handle);
                if (!id.IsNull && tr.GetObject(id, OpenMode.ForRead) is RotatedDimension r)
                {
                    var lp = ctx.Units.ToJob(r.DimLinePoint);
                    var basePt = item.Overall ? p.OverallBase : p.Base;
                    double off = Math.Abs((lp - basePt).Dot(p.Dir.PerpLeft));
                    if (off > ctx.LengthTolerance) mismatches.Add("dimension " + item.Handle + " is " + off + " off the string's dimension line");
                }
            }
            var overall = p.Items.FirstOrDefault(i => i.Overall);
            result.Readback.Add("dimensions", list).Add("segments", measurements.Count).Add("sum", AcadValues.Round(sum))
                .Add("lineBase", DrawingUnits.PointJson(p.Base)).Add("associative", false);
            if (overall != null)
            {
                double expected = overall.Expected;
                if (Math.Abs(sum - expected) > ctx.LengthTolerance * Math.Max(1, measurements.Count)) mismatches.Add("the segments add up to " + sum + ", the overall is " + expected);
            }
            result.Outputs.Add("measurements", measurements).Add("handles", JsonArray.FromStrings(p.Items.Select(i => i.Handle)));
            foreach (var s in mismatches) Mismatch(result, s);
            return mismatches.Count == 0;
        }
    }
}
