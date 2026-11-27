using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// layout.clearances: tests clearance zones against model-space obstacles. Every zone is a union of convex pieces
    /// (circle, rectangle, convex polygon, door-swing sector of at most 180 degrees, and for a route one rectangle per
    /// centreline segment plus a disc at each inner vertex). A piece's depth function (how far a point is inside it) is
    /// concave, so the deepest point of an obstacle segment inside a piece is found by ternary search along the segment.
    /// Obstacles are plan segments of entities on the given layers and of references of the given blocks (exploded, or
    /// their extents box); xref references are skipped. One intrusion is reported per obstacle entity and zone, with its
    /// deepest point. The drawing changes only when drawZones is given.
    /// </summary>
    internal sealed class LayoutClearances : OpHandler<LayoutClearances.Plan>
    {
        public override string Name => "layout.clearances";

        internal abstract class Piece
        {
            public abstract double Depth(Point2 p);
        }

        private sealed class Disc : Piece
        {
            public Point2 C; public double R;
            public override double Depth(Point2 p) => R - p.DistanceTo(C);
        }

        private sealed class Convex : Piece
        {
            /// <summary>Counter-clockwise.</summary>
            public List<Point2> Pts = new List<Point2>();
            public override double Depth(Point2 p)
            {
                double d = double.MaxValue;
                for (int i = 0; i < Pts.Count; i++)
                {
                    var a = Pts[i]; var b = Pts[(i + 1) % Pts.Count];
                    var e = b - a;
                    double len = e.Length;
                    if (len < 1e-12) continue;
                    d = Math.Min(d, e.Cross(p - a) / len);
                }
                return d;
            }
        }

        private sealed class Sector : Piece
        {
            public Point2 H; public double R;
            public Point2 U1, U2;
            public override double Depth(Point2 p)
            {
                var v = p - H;
                return Math.Min(R - v.Length, Math.Min(U1.Cross(v), v.Cross(U2)));
            }
        }

        internal sealed class Zone
        {
            public string Id = "", Kind = "";
            public List<Piece> Pieces = new List<Piece>();
            public List<Point2> Outline = new List<Point2>();
            public HashSet<ObjectId> Ignore = new HashSet<ObjectId>();
            public Box Box;
        }

        internal sealed class Intrusion
        {
            public string Zone = "", Handle = "", Type = "", Layer = "";
            public string? Block;
            public double Depth;
            public Point2 Point;
        }

        internal sealed class Plan
        {
            public List<Zone> Zones = new List<Zone>();
            public List<Intrusion> Intrusions = new List<Intrusion>();
            public string? DrawLayer;
            public List<string> Outlines = new List<string>();
            public int Obstacles, XrefsSkipped;
        }

        private static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>
        {
            { "circle", new[] { "centre", "radius" } },
            { "rect", new[] { "min", "max" } },
            { "polygon", new[] { "points" } },
            { "doorSwing", new[] { "hinge", "leafWidth", "closedAngle" } },
            { "route", new[] { "points", "width" } },
        };

        private static readonly string[] Geometry = { "centre", "radius", "min", "max", "points", "width", "hinge", "leafWidth", "closedAngle", "swing", "side" };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            double tol = args.GetLengthOrNull("tolerance") ?? ctx.LengthTolerance;
            var global = Ids(ctx, args, "ignore", issues);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var z in args.GetObjectList("zones"))
            {
                var zone = new Zone { Id = z.GetString("id"), Kind = z.GetString("kind") };
                if (!seen.Add(zone.Id)) OpContext.Error(issues, z, "id", "id_duplicate", "zone id '" + zone.Id + "' is used twice");
                var need = Fields[zone.Kind];
                var missing = need.Where(f => !z.Has(f)).ToList();
                if (missing.Count > 0) { OpContext.Error(issues, z, null, "field_missing", "a " + zone.Kind + " zone needs " + string.Join(", ", missing)); continue; }
                bool bad = false;
                foreach (var f in Geometry)
                    if (z.Has(f) && !need.Contains(f) && !(zone.Kind == "doorSwing" && (f == "swing" || f == "side")))
                    { OpContext.Error(issues, z, f, "field_not_used", f + " does not apply to a " + zone.Kind + " zone"); bad = true; }
                if (bad) continue;
                if (!Shape(ctx, z, zone, issues)) continue;
                zone.Ignore = new HashSet<ObjectId>(global.Concat(Ids(ctx, z, "ignore", issues)));
                zone.Box = Bounds(zone.Outline);
                p.Zones.Add(zone);
            }
            if (args.GetObject("drawZones") is OpArgs dz) p.DrawLayer = DraftDb.Layer(ctx, dz, issues, "layer", "zone outlines");
            var blocks = args.GetStringList("blocks");
            foreach (var b in blocks.Where(n => !SheetDb.HasWildcard(n)))
                if (!DraftDb.BlockExists(ctx, b)) OpContext.Error(issues, args, "blocks", "block_missing", DraftDb.MissingBlock(ctx, b));
            if (issues.Any(i => i.IsError)) return p;

            var layerRx = Targets.Patterns(args, "layers");
            var blockRx = blocks.Select(SheetDb.Wildcard).ToList();
            bool explode = args.GetString("blockGeometry") == "geometry";
            Db.Read(ctx, tr =>
            {
                var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead);
                var all = new Box(p.Zones.Min(z => z.Box.X0), p.Zones.Min(z => z.Box.Y0), p.Zones.Max(z => z.Box.X1), p.Zones.Max(z => z.Box.Y1));
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e is null || e is Viewport || !e.Visible) continue;
                    string? block = null;
                    if (e is BlockReference br)
                    {
                        var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        block = SheetDb.EffectiveName(tr, br);
                        if (def.IsFromExternalReference) { if (Targets.AnyMatch(blockRx, block) || Targets.AnyMatch(layerRx, e.Layer)) p.XrefsSkipped++; continue; }
                    }
                    bool byLayer = Targets.AnyMatch(layerRx, e.Layer);
                    bool byBlock = block != null && Targets.AnyMatch(blockRx, block);
                    if (!byLayer && !byBlock) continue;
                    Box box;
                    try
                    {
                        var ext = e.GeometricExtents;
                        var a = ctx.Units.ToJob(ext.MinPoint); var b = ctx.Units.ToJob(ext.MaxPoint);
                        box = new Box(a.X, a.Y, b.X, b.Y);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                    if (!Overlaps(box, all)) continue;
                    p.Obstacles++;
                    List<Segment2>? segs = null;
                    foreach (var zone in p.Zones)
                    {
                        if (zone.Ignore.Contains(id) || !Overlaps(box, zone.Box)) continue;
                        segs ??= PlanGeometry.Segments(ctx, tr, e, explode && block != null);
                        double best = double.MinValue;
                        Point2 at = default;
                        foreach (var s in segs)
                            foreach (var piece in zone.Pieces)
                            {
                                double d = Deepest(piece, s, out var q);
                                if (d > best) { best = d; at = q; }
                            }
                        if (best > tol)
                            p.Intrusions.Add(new Intrusion { Zone = zone.Id, Handle = OpContext.HandleOf(id), Type = id.ObjectClass.DxfName, Layer = e.Layer, Block = block, Depth = best, Point = at });
                    }
                }
                return 0;
            });
            p.Intrusions = p.Intrusions.OrderByDescending(i => i.Depth).ToList();
            if (ctx.IsValidating && p.XrefsSkipped > 0)
                OpContext.Warning(issues, args, "layers", "xrefs_skipped", p.XrefsSkipped + " xref references are not tested; open the xref or bind it to test its contents");
            return p;
        }

        private static List<ObjectId> Ids(OpContext ctx, OpArgs a, string field, List<Issue> issues)
        {
            var list = new List<ObjectId>();
            if (!a.Has(field) || ctx.IsRef(a, field)) return list;
            foreach (var h in a.GetRefIds(field))
            {
                var id = ctx.IdOf(h);
                if (id.IsNull) OpContext.Warning(issues, a, field, "handle_missing", "ignored handle " + h + " is not in the drawing");
                else list.Add(id);
            }
            return list;
        }

        private static bool Shape(OpContext ctx, OpArgs z, Zone zone, List<Issue> issues)
        {
            switch (zone.Kind)
            {
                case "circle":
                {
                    var c = z.GetPoint2("centre"); double r = z.GetLength("radius");
                    zone.Pieces.Add(new Disc { C = c, R = r });
                    zone.Outline = Circle(c, r, 0, 2 * Math.PI, false);
                    return true;
                }
                case "rect":
                {
                    var a = z.GetPoint2("min"); var b = z.GetPoint2("max");
                    if (b.X <= a.X || b.Y <= a.Y) { OpContext.Error(issues, z, "max", "rect_invalid", "max must be above and to the right of min"); return false; }
                    var pts = new List<Point2> { a, new Point2(b.X, a.Y), b, new Point2(a.X, b.Y) };
                    zone.Pieces.Add(new Convex { Pts = pts });
                    zone.Outline = pts;
                    return true;
                }
                case "polygon":
                {
                    var pts = z.GetPoint2List("points");
                    if (pts.Count < 3) { OpContext.Error(issues, z, "points", "points_count", "a polygon zone needs 3 or more points"); return false; }
                    if (!PlanGeometry.IsConvex(pts)) { OpContext.Error(issues, z, "points", "zone_not_convex", "polygon zones must be convex; split a concave zone into convex ones"); return false; }
                    if (!new Polygon2(pts).IsCounterClockwise) pts.Reverse();
                    zone.Pieces.Add(new Convex { Pts = pts });
                    zone.Outline = pts;
                    return true;
                }
                case "doorSwing":
                {
                    var h = z.GetPoint2("hinge"); double r = z.GetLength("leafWidth");
                    double a0 = Num.Rad(z.GetAngle("closedAngle")), sw = Num.Rad(z.GetAngle("swing"));
                    double start = z.GetString("side") == "left" ? a0 : a0 - sw;
                    var u1 = new Point2(Math.Cos(start), Math.Sin(start));
                    var u2 = new Point2(Math.Cos(start + sw), Math.Sin(start + sw));
                    zone.Pieces.Add(new Sector { H = h, R = r, U1 = u1, U2 = u2 });
                    zone.Outline = Circle(h, r, start, sw, true);
                    return true;
                }
                default:
                {
                    var pts = z.GetPoint2List("points");
                    double w = z.GetLength("width");
                    if (pts.Count < 2) { OpContext.Error(issues, z, "points", "points_count", "a route needs 2 or more centreline points"); return false; }
                    for (int i = 0; i + 1 < pts.Count; i++)
                    {
                        var a = pts[i]; var b = pts[i + 1];
                        if (a.DistanceTo(b) <= ctx.LengthTolerance) { OpContext.Error(issues, z, "points", "duplicate_point", "route points " + i + " and " + (i + 1) + " coincide"); return false; }
                        var n = (b - a).Normalised().PerpLeft * (w / 2);
                        zone.Pieces.Add(new Convex { Pts = new List<Point2> { a - n, b - n, b + n, a + n } });
                        if (i > 0) zone.Pieces.Add(new Disc { C = a, R = w / 2 });
                    }
                    var left = ChainOffset.Offset(pts, false, w / 2);
                    var right = ChainOffset.Offset(pts, false, -w / 2);
                    right.Reverse();
                    zone.Outline = left.Concat(right).ToList();
                    return true;
                }
            }
        }

        private static List<Point2> Circle(Point2 c, double r, double start, double sweep, bool withCentre)
        {
            int n = Math.Max(4, (int)Math.Ceiling(Num.Deg(sweep) / PlanGeometry.ChordDegrees));
            var pts = new List<Point2>();
            if (withCentre) pts.Add(c);
            int last = withCentre ? n : n - 1;
            for (int i = 0; i <= last; i++) pts.Add(new Point2(c.X + r * Math.Cos(start + sweep * i / n), c.Y + r * Math.Sin(start + sweep * i / n)));
            return pts;
        }

        private static Box Bounds(List<Point2> pts) => new Box(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y));

        private static bool Overlaps(Box a, Box b) => a.X0 <= b.X1 && b.X0 <= a.X1 && a.Y0 <= b.Y1 && b.Y0 <= a.Y1;

        internal readonly struct Box
        {
            public Box(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
            public double X0 { get; }
            public double Y0 { get; }
            public double X1 { get; }
            public double Y1 { get; }
        }

        /// <summary>Deepest point of a segment inside a piece (ternary search of a concave function on [0, 1]).</summary>
        private static double Deepest(Piece piece, Segment2 s, out Point2 at)
        {
            var d = s.B - s.A;
            double lo = 0, hi = 1;
            for (int i = 0; i < 80 && hi - lo > 1e-9; i++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (piece.Depth(s.A + d * m1) < piece.Depth(s.A + d * m2)) lo = m1; else hi = m2;
            }
            double t = (lo + hi) / 2;
            at = s.A + d * t;
            double best = piece.Depth(at);
            foreach (var end in new[] { s.A, s.B })
            {
                double de = piece.Depth(end);
                if (de > best) { best = de; at = end; }
            }
            return best;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p.DrawLayer != null)
            {
                var ms = Db.ModelSpace(ctx.Transaction, ctx.Database, OpenMode.ForWrite);
                foreach (var z in p.Zones)
                {
                    var pl = new Polyline();
                    for (int i = 0; i < z.Outline.Count; i++) pl.AddVertexAt(i, ctx.Units.ToDrawing2d(z.Outline[i]), 0, 0, 0);
                    pl.Closed = true;
                    pl.SetDatabaseDefaults(ctx.Database);
                    pl.Layer = p.DrawLayer;
                    string h = OpContext.HandleOf(Db.Append(ctx, ms, pl));
                    p.Outlines.Add(h);
                    result.Created.Add(h);
                }
            }
            var intrusions = new JsonArray(p.Intrusions.Select(i => (JsonValue)new JsonObject().Add("zone", i.Zone).Add("handle", i.Handle).Add("type", i.Type)
                .Add("layer", i.Layer).Add("block", i.Block != null ? (JsonValue)i.Block : JsonValue.Null).Add("depth", AcadValues.Round(i.Depth, 6)).Add("point", DrawingUnits.PointJson(i.Point))));
            var zones = new JsonArray(p.Zones.Select(z =>
            {
                var mine = p.Intrusions.Where(i => i.Zone == z.Id).ToList();
                return (JsonValue)new JsonObject().Add("id", z.Id).Add("kind", z.Kind).Add("intrusions", mine.Count)
                    .Add("maxDepth", mine.Count > 0 ? (JsonValue)AcadValues.Round(mine.Max(i => i.Depth), 6) : 0).Add("outline", Num.Points(z.Outline));
            }));
            result.Outputs.Add("count", p.Intrusions.Count).Add("clear", p.Intrusions.Count == 0).Add("intrusions", intrusions).Add("zones", zones)
                .Add("outlines", JsonArray.FromStrings(p.Outlines));
            if (p.Intrusions.Count > 0 && args.GetBool("failOnIntrusion"))
            {
                var worst = p.Intrusions[0];
                result.Error = new OpError("clearance_intruded", p.Intrusions.Count + " intrusions; the deepest is " + worst.Type + " " + worst.Handle + " " + AcadValues.Round(worst.Depth, 3) + " into zone '" + worst.Zone + "'");
            }
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            int bad = 0;
            foreach (var h in p.Outlines)
            {
                var id = ctx.IdOf(h);
                if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Polyline pl) || !pl.Closed || !string.Equals(pl.Layer, p.DrawLayer, StringComparison.OrdinalIgnoreCase)) bad++;
            }
            result.Readback.Add("zones", p.Zones.Count).Add("obstaclesTested", p.Obstacles).Add("xrefsSkipped", p.XrefsSkipped).Add("outlines", p.Outlines.Count);
            return bad == 0 || Mismatch(result, bad + " zone outlines read back missing, open or on another layer");
        }
    }
}
