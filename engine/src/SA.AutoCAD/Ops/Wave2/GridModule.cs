using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// grid.module: a setting-out grid of module lines inside a closed straight-sided boundary. Work happens in the
    /// grid's frame (the boundary rotated by -angle): per direction, grid lines sit at origin + k x module, where the
    /// origin comes from setOut (centreTile: a whole tile centred on the boundary's extents; centreLine: a line on the
    /// centre; origin: through the given point; balanced: whichever of the first two gives the larger smaller border).
    /// Borders are the cut tiles at the extents in that frame. Lines are clipped to the boundary (even-odd, so concave
    /// boundaries give several pieces) and drawn as Line entities. Cells between lines count as full tiles when they
    /// are whole modules lying wholly inside the boundary, else as cut tiles when they overlap it.
    /// </summary>
    internal sealed class GridModule : OpHandler<GridModule.Plan>
    {
        public override string Name => "grid.module";

        private const int MaxLines = 4000;
        private const int MaxTiles = 250_000;

        internal sealed class Axis
        {
            public double Min, Max, Module, Origin;
            public List<double> Lines = new List<double>();
            public double Low, High;
        }

        internal sealed class Plan
        {
            public List<Point2> Boundary = new List<Point2>();
            public double Angle;
            public Axis X = new Axis(), Y = new Axis();
            public List<Segment2> Segments = new List<Segment2>();
            public List<string> Small = new List<string>();
            public int Full, Cut;
            public string Layer = "";
            public bool Draw;
            public List<string> Handles = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Angle = Num.Rad(args.GetAngle("angle")), Draw = args.GetBool("draw") };
            p.Layer = p.Draw ? DraftDb.Layer(ctx, args, issues, "layer", "grid lines") : "";
            double tol = ctx.LengthTolerance;
            if (args.Has("boundaryHandle"))
            {
                if (ctx.IsRef(args, "boundaryHandle")) return p;
                string h = args.GetRefId("boundaryHandle") ?? "";
                var id = ctx.IdOf(h);
                string? problem = id.IsNull ? "is not in the drawing" : Db.Read(ctx, tr =>
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Polyline pl)) return "is not a lightweight polyline";
                    if (!PlanGeometry.IsPlan(pl.Normal)) return "does not lie flat in plan";
                    PlanGeometry.Vertices(ctx, pl, out var v, out var b);
                    if (b.Any(x => Math.Abs(x) > 1e-9)) return "has arc segments; the grid needs straight sides";
                    if (!pl.Closed && !(v.Count > 2 && v[0].DistanceTo(v[v.Count - 1]) <= tol)) return "is not closed";
                    if (!pl.Closed) v.RemoveAt(v.Count - 1);
                    p.Boundary = v;
                    return null;
                });
                if (problem != null) { OpContext.Error(issues, args, "boundaryHandle", "boundary_invalid", "boundary " + h + " " + problem); return p; }
            }
            else p.Boundary = args.GetPoint2List("boundary");
            if (p.Boundary.Count < 3) { OpContext.Error(issues, args, "boundary", "boundary_invalid", "the boundary needs 3 or more points"); return p; }
            var poly = new Polygon2(p.Boundary);
            if (!poly.IsSimple(tol)) { OpContext.Error(issues, args, args.Has("boundary") ? "boundary" : "boundaryHandle", "boundary_self_intersecting", "the boundary crosses itself"); return p; }

            double mx = args.GetLength("module");
            double my = args.GetLengthOrNull("moduleY") ?? mx;
            var local = p.Boundary.Select(q => q.Rotate(-p.Angle)).ToList();
            p.X = new Axis { Min = local.Min(q => q.X), Max = local.Max(q => q.X), Module = mx };
            p.Y = new Axis { Min = local.Min(q => q.Y), Max = local.Max(q => q.Y), Module = my };
            string setOut = args.GetString("setOut");
            Point2? origin = null;
            if (setOut == "origin")
            {
                if (!args.Has("origin")) { OpContext.Error(issues, args, "origin", "origin_missing", "setOut origin needs origin"); return p; }
                origin = args.GetPoint2("origin").Rotate(-p.Angle);
            }
            else if (args.Has("origin")) OpContext.Error(issues, args, "origin", "field_not_used", "origin applies with setOut origin");
            foreach (var (ax, o) in new[] { (p.X, origin?.X), (p.Y, origin?.Y) })
            {
                if ((ax.Max - ax.Min) / ax.Module > MaxLines)
                {
                    OpContext.Error(issues, args, "module", "grid_too_dense", "the module gives more than " + MaxLines + " lines across the boundary");
                    return p;
                }
                SetOut(ax, setOut, o, tol);
            }
            // Each tile is tested against the boundary on AutoCAD's main thread, once per validate, rehearse and commit.
            if ((double)(p.X.Lines.Count + 1) * (p.Y.Lines.Count + 1) > MaxTiles)
            {
                OpContext.Error(issues, args, "module", "grid_too_dense", "the module gives more than " + MaxTiles + " tiles inside the boundary's extents");
                return p;
            }

            double minFrac = args.GetNumber("minBorder");
            foreach (var (side, v, m) in new[] { ("left", p.X.Low, mx), ("right", p.X.High, mx), ("bottom", p.Y.Low, my), ("top", p.Y.High, my) })
                if (v < minFrac * m - tol) p.Small.Add(side);
            if (ctx.IsValidating && p.Small.Count > 0)
                OpContext.Warning(issues, args, "setOut", "small_border", "border tiles under " + minFrac + " of the module at: " + string.Join(", ", p.Small));

            Clip(p, local);
            Count(p, new Polygon2(local), tol);
            return p;
        }

        /// <summary>Places the lines of one direction and measures its two border tiles.</summary>
        private static void SetOut(Axis ax, string setOut, double? origin, double tol)
        {
            double c = (ax.Min + ax.Max) / 2;
            double Origin(string rule) => rule == "centreTile" ? c - ax.Module / 2 : rule == "centreLine" ? c : origin ?? c;
            (double low, double high) Borders(double o)
            {
                double first = o + Math.Ceiling((ax.Min + tol - o) / ax.Module) * ax.Module;
                double last = o + Math.Floor((ax.Max - tol - o) / ax.Module) * ax.Module;
                if (first > last + tol) return (ax.Max - ax.Min, ax.Max - ax.Min);
                double lo = first - ax.Min, hi = ax.Max - last;
                return (lo, hi);
            }
            if (setOut == "balanced")
            {
                var t = Borders(Origin("centreTile"));
                var l = Borders(Origin("centreLine"));
                ax.Origin = Math.Min(t.low, t.high) >= Math.Min(l.low, l.high) ? Origin("centreTile") : Origin("centreLine");
            }
            else ax.Origin = Origin(setOut);
            (ax.Low, ax.High) = Borders(ax.Origin);
            double x = ax.Origin + Math.Ceiling((ax.Min + tol - ax.Origin) / ax.Module) * ax.Module;
            for (; x < ax.Max - tol; x += ax.Module) ax.Lines.Add(x);
        }

        /// <summary>Pieces of each grid line inside the boundary (grid frame), even-odd with half-open vertex rule.</summary>
        private static void Clip(Plan p, List<Point2> local)
        {
            foreach (var x in p.X.Lines)
                foreach (var (a, b) in Spans(local, x, true)) p.Segments.Add(new Segment2(new Point2(x, a), new Point2(x, b)));
            foreach (var y in p.Y.Lines)
                foreach (var (a, b) in Spans(local, y, false)) p.Segments.Add(new Segment2(new Point2(a, y), new Point2(b, y)));
        }

        private static IEnumerable<(double, double)> Spans(List<Point2> poly, double c, bool vertical)
        {
            var hits = new List<double>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                double ac = vertical ? a.X : a.Y, bc = vertical ? b.X : b.Y;
                if ((ac <= c) == (bc <= c)) continue;
                double t = (c - ac) / (bc - ac);
                hits.Add(vertical ? a.Y + t * (b.Y - a.Y) : a.X + t * (b.X - a.X));
            }
            hits.Sort();
            for (int i = 0; i + 1 < hits.Count; i += 2)
                if (hits[i + 1] - hits[i] > 1e-9) yield return (hits[i], hits[i + 1]);
        }

        private static void Count(Plan p, Polygon2 poly, double tol)
        {
            var xs = new[] { p.X.Min }.Concat(p.X.Lines).Concat(new[] { p.X.Max }).ToList();
            var ys = new[] { p.Y.Min }.Concat(p.Y.Lines).Concat(new[] { p.Y.Max }).ToList();
            var edges = poly.Edges().ToList();
            for (int i = 0; i + 1 < xs.Count; i++)
                for (int j = 0; j + 1 < ys.Count; j++)
                {
                    double x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                    if (x1 - x0 <= tol || y1 - y0 <= tol) continue;
                    var corners = new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) };
                    bool vertexInside = poly.Vertices.Any(v => v.X > x0 + tol && v.X < x1 - tol && v.Y > y0 + tol && v.Y < y1 - tol);
                    bool allIn = corners.All(q => poly.Locate(q, tol) != PointLocation.Outside) && !vertexInside;
                    bool whole = Math.Abs(x1 - x0 - p.X.Module) <= tol && Math.Abs(y1 - y0 - p.Y.Module) <= tol;
                    if (allIn) { if (whole) p.Full++; else p.Cut++; continue; }
                    var centre = new Point2((x0 + x1) / 2, (y0 + y1) / 2);
                    bool overlaps = vertexInside || corners.Any(q => poly.Locate(q, 0) == PointLocation.Inside) || poly.Locate(centre, 0) == PointLocation.Inside;
                    if (!overlaps)
                    {
                        var sides = new[] { new Segment2(corners[0], corners[1]), new Segment2(corners[1], corners[2]), new Segment2(corners[2], corners[3]), new Segment2(corners[3], corners[0]) };
                        overlaps = edges.Any(e => sides.Any(s => s.Intersect(e, 0).Kind == IntersectionKind.Point && s.DistanceTo(e.A) > tol && s.DistanceTo(e.B) > tol));
                    }
                    if (overlaps) p.Cut++;
                }
        }

        private static Point2 World(Plan p, Point2 local) => local.Rotate(p.Angle);

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var handles = new JsonArray();
            if (p.Draw)
            {
                var tr = ctx.Transaction;
                var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForWrite);
                foreach (var s in p.Segments)
                {
                    var ln = new Line(ctx.Units.ToDrawing(World(p, s.A)), ctx.Units.ToDrawing(World(p, s.B)));
                    ln.SetDatabaseDefaults(ctx.Database);
                    ln.Layer = p.Layer;
                    string h = OpContext.HandleOf(Db.Append(ctx, ms, ln));
                    p.Handles.Add(h);
                    result.Created.Add(h);
                    handles.Add(h);
                }
            }
            result.Outputs.Add("handles", handles)
                .Add("borders", new JsonObject().Add("left", R(p.X.Low)).Add("right", R(p.X.High)).Add("bottom", R(p.Y.Low)).Add("top", R(p.Y.High)))
                .Add("smallBorders", JsonArray.FromStrings(p.Small))
                .Add("origin", DrawingUnits.PointJson(World(p, new Point2(p.X.Origin, p.Y.Origin))))
                .Add("fullTiles", p.Full).Add("cutTiles", p.Cut);
            return p;
        }

        private static JsonValue R(double v) => AcadValues.Round(v, 6);

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            double tol = ctx.LengthTolerance;
            int bad = 0;
            string first = "";
            for (int i = 0; i < p.Handles.Count; i++)
            {
                var id = ctx.IdOf(p.Handles[i]);
                var s = p.Segments[i];
                if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Line ln)) { bad++; if (first.Length == 0) first = p.Handles[i] + " is missing"; continue; }
                var a = ctx.Units.ToJob(ln.StartPoint); var b = ctx.Units.ToJob(ln.EndPoint);
                if (a.DistanceTo(World(p, s.A)) > tol || b.DistanceTo(World(p, s.B)) > tol || !string.Equals(ln.Layer, p.Layer, StringComparison.OrdinalIgnoreCase))
                { bad++; if (first.Length == 0) first = p.Handles[i] + " end points or layer differ"; }
            }
            result.Readback.Add("lines", p.Handles.Count).Add("linesX", p.X.Lines.Count).Add("linesY", p.Y.Lines.Count).Add("matching", p.Handles.Count - bad);
            return bad == 0 || Mismatch(result, bad + " grid lines read back differently (first " + first + ")");
        }
    }
}
