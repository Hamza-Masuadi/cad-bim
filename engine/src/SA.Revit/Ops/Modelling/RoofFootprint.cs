using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// roof.footprint: a footprint roof from a sketched outline, with Document.NewFootPrintRoof(curves, level, type,
    /// out mapping). Edge i runs from boundary point i to i+1; by default every edge slopes (a hip roof).
    ///
    /// Slopes: FootPrintRoof.SlopeAngle is documented as a "slope" measurement (rise over run, 0.5 = 26.57 degrees),
    /// so the handler writes tan(angle). A sloping edge with a slope of 0 is refused; mark it definesSlope:false.
    ///
    /// Overhang: FootPrintRoof.Overhang applies only to roofs made with Pick Walls, so for a sketched footprint the
    /// outline is moved outward by the overhang on every edge, and the roof is lowered so that each sloping plane
    /// still passes through the original footprint at level + baseOffset (the plate line), which is what Revit does
    /// for an overhang picked from walls. With one slope the roof's Base Offset becomes baseOffset - overhang x
    /// tan(slope); with mixed slopes the steepest drop sets the base and the other sloping edges are raised by the
    /// difference through their edge Offset. Readback reports both the requested plate height and the base offset.
    ///
    /// Readback: level, type, base offset, and for each edge (matched to the roof's profile curves by end points)
    /// whether it defines slope, its slope and its offset.
    /// </summary>
    public sealed class RoofFootprint : OpHandler
    {
        public override string Name => "roof.footprint";

        private sealed class Edge
        {
            public Point2 A, B;
            public bool Slopes;
            public double Degrees;
            public double Offset;     // internal, relative to the roof base
        }

        private sealed class Plan
        {
            public RoofType Type = null!;
            public Level Level = null!;
            public List<Edge> Edges = new List<Edge>();
            public double RoofBase;    // internal, the roof's Base Offset
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            var t = Lookup.Type<RoofType>(doc, args.GetString("type"), "roof type");
            if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "type"), t.Code, t.Error!);
            else p.Type = t.Value!;
            var lv = Lookup.Level(doc, args.GetString("level"));
            if (!lv.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!);
            else p.Level = lv.Value!;

            var pts = Kit.CleanLoop(args.GetPoint2List("boundary"), u.LengthTolerance);
            string? bad = Kit.CheckLoop(pts, u, doc.Application.ShortCurveTolerance, "footprint");
            if (bad != null) { OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Invalid, bad); return null; }
            int n = pts.Count;
            double slope = args.GetAngle("slope");
            var slopes = Enumerable.Repeat(true, n).ToArray();
            var degrees = Enumerable.Repeat(slope, n).ToArray();
            var seen = new HashSet<int>();
            foreach (var e in args.GetObjectList("edges"))
            {
                int i = e.GetInt("edge");
                if (i >= n) { OpContext.Error(issues, e.Path + ".edge", RevitIssueCodes.Invalid, "edge " + i + " does not exist; the footprint has " + n + " edges (0 to " + (n - 1) + ")"); continue; }
                if (!seen.Add(i)) { OpContext.Error(issues, e.Path + ".edge", RevitIssueCodes.Invalid, "edge " + i + " is listed twice"); continue; }
                slopes[i] = e.GetBool("definesSlope");
                if (e.Has("slope")) degrees[i] = e.GetAngle("slope");
            }
            for (int i = 0; i < n; i++)
                if (slopes[i] && degrees[i] <= 0)
                    OpContext.Error(issues, ctx.FieldPath(args, "edges"), RevitIssueCodes.Invalid, "edge " + i + " slopes at 0 degrees; set definesSlope:false for a level edge");

            double overhang = args.GetLength("overhang");
            var outline = Kit.OffsetOutward(pts, overhang);
            if (overhang > 0)
            {
                string? obad = Kit.CheckLoop(outline, u, doc.Application.ShortCurveTolerance, "footprint with overhang");
                if (obad != null) { OpContext.Error(issues, ctx.FieldPath(args, "overhang"), RevitIssueCodes.Invalid, obad); return null; }
            }
            // Drop of each sloping edge over the overhang, internal units.
            var drops = new double[n];
            for (int i = 0; i < n; i++)
                drops[i] = slopes[i] ? u.LengthToInternal(overhang) * Math.Tan(u.AngleToInternal(degrees[i])) : 0;
            double maxDrop = drops.Length > 0 ? drops.Max() : 0;
            p.RoofBase = u.LengthToInternal(args.GetLength("baseOffset")) - maxDrop;
            for (int i = 0; i < n; i++)
                p.Edges.Add(new Edge { A = outline[i], B = outline[(i + 1) % n], Slopes = slopes[i], Degrees = degrees[i], Offset = slopes[i] ? maxDrop - drops[i] : 0 });
            if (!slopes.Any(s => s)) OpContext.Warning(issues, ctx.FieldPath(args, "edges"), RevitIssueCodes.Unchecked, "no edge slopes: the roof is flat; model falls in the build-up");
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            double z = p!.Level.ProjectElevation;
            var curves = new CurveArray();
            foreach (var e in p.Edges) curves.Append(Line.CreateBound(u.PointToInternal(e.A, z), u.PointToInternal(e.B, z)));
            var roof = doc.Create.NewFootPrintRoof(curves, p.Level, p.Type, out ModelCurveArray mapping)
                       ?? throw new OpFailedException("create_failed", "Revit did not create the roof");
            if (mapping.Size != p.Edges.Count) throw new OpFailedException("create_failed", "Revit returned " + mapping.Size + " footprint curves for " + p.Edges.Count + " edges");
            for (int i = 0; i < p.Edges.Count; i++)
            {
                var mc = mapping.get_Item(i);
                var e = p.Edges[i];
                roof.set_DefinesSlope(mc, e.Slopes);
                if (e.Slopes) roof.set_SlopeAngle(mc, Math.Tan(u.AngleToInternal(e.Degrees)));
                if (Math.Abs(e.Offset) > 1e-9) roof.set_Offset(mc, e.Offset);
            }
            if (!Kit.SetDouble(roof, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM, p.RoofBase))
                throw new OpFailedException("parameter_value", "Revit refused the roof's base offset");
            string id = Kit.Id(roof.Id);
            result.Created.Add(id);
            result.Outputs.Add("roof", id);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("roof");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is FootPrintRoof roof))
            {
                check.Fail("the roof no longer exists after commit");
                check.Apply(result);
                return;
            }
            var p = Build(ctx, args, new List<Issue>());
            var rb = new JsonObject().Add("id", idText);
            check.Text(rb, "type", doc.GetElement(roof.GetTypeId())?.Name, Lookup.Type<RoofType>(doc, args.GetString("type"), "roof type").Value?.Name, "roof type");
            var baseLevel = roof.get_Parameter(BuiltInParameter.ROOF_BASE_LEVEL_PARAM)?.AsElementId() ?? roof.LevelId;
            check.Text(rb, "level", Lookup.LevelName(doc, baseLevel), Lookup.Level(doc, args.GetString("level")).Value?.Name, "roof level");
            var bo = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
            if (bo != null) check.Length(rb, "baseOffset", bo.AsDouble(), p != null ? u.LengthFromInternal(p.RoofBase) : (double?)null, "roof base offset");
            else check.Fail("the roof has no base offset parameter");
            rb.Add("plateHeight", RevitUnits.Num(args.GetLength("baseOffset")));
            rb.Add("overhang", RevitUnits.Num(args.GetLength("overhang")));
            var profile = new List<ModelCurve>();
            foreach (ModelCurveArray loop in roof.GetProfiles()) foreach (ModelCurve mc in loop) profile.Add(mc);
            var edges = new JsonArray();
            if (p != null)
            {
                if (profile.Count != p.Edges.Count) check.Fail("the roof profile has " + profile.Count + " curves, expected " + p.Edges.Count);
                for (int i = 0; i < p.Edges.Count; i++)
                {
                    var e = p.Edges[i];
                    var a = u.PointToInternal(e.A, 0);
                    var b = u.PointToInternal(e.B, 0);
                    var mc = profile.FirstOrDefault(c => Matches(c.GeometryCurve, a, b, u.LengthToleranceInternal));
                    var o = new JsonObject().Add("edge", i);
                    if (mc is null) { check.Fail("edge " + i + " is not in the roof's profile"); edges.Add(o); continue; }
                    check.Flag(o, "definesSlope", roof.get_DefinesSlope(mc), e.Slopes, "edge " + i + " defines slope");
                    if (e.Slopes)
                    {
                        double readDeg = u.AngleFromInternal(Math.Atan(roof.get_SlopeAngle(mc)));
                        o.Add("slope", RevitUnits.Num(readDeg));
                        if (Math.Abs(readDeg - e.Degrees) > 0.01) check.Fail("edge " + i + " slope reads " + Kit.Num(readDeg) + " degrees, requested " + Kit.Num(e.Degrees));
                    }
                    check.Length(o, "offset", roof.get_Offset(mc), u.LengthFromInternal(e.Offset), "edge " + i + " offset");
                    edges.Add(o);
                }
            }
            else check.Fail("the request could not be rebuilt for readback");
            rb.Add("edges", edges);
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            check.Apply(result);
        }

        private static bool Matches(Curve c, XYZ a, XYZ b, double tol)
        {
            XYZ Flat(XYZ q) => new XYZ(q.X, q.Y, 0);
            var s = Flat(c.GetEndPoint(0));
            var t = Flat(c.GetEndPoint(1));
            return (s.DistanceTo(a) <= tol && t.DistanceTo(b) <= tol) || (s.DistanceTo(b) <= tol && t.DistanceTo(a) <= tol);
        }
    }
}
