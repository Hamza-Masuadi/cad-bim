using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>A straight wall as opening.place sees it, in job units.</summary>
    internal sealed class HostWall
    {
        /// <summary>Stable key for overlap checks inside one job ("op:wallId" or "layer:x0,y0,dx,dy").</summary>
        public string Key = "";
        public Point2 Start;
        /// <summary>Unit direction from start to end.</summary>
        public Point2 Dir;
        public double Length;
        public double Thickness;
        public string Layer = "";
        /// <summary>From the job plan (wall.draw in the same job): face segments of the cleaned outline, before any cut.</summary>
        public List<Segment2>? PlannedSegments;
        public string Source = "";

        public Point2 Normal => Dir.PerpLeft;
        public Point2 At(double s, double side = 0) => Start + Dir * s + Normal * side;
        public double Along(Point2 p) => (p - Start).Dot(Dir);
        public double Across(Point2 p) => (p - Start).Dot(Normal);
    }

    /// <summary>A model-space line on a wall face, with its parameters along the wall.</summary>
    internal sealed class FaceLine
    {
        public ObjectId Id;
        public Point2 A, B;
        public double SA, SB;
        public double Side;
        public double Min => Math.Min(SA, SB);
        public double Max => Math.Max(SA, SB);
    }

    /// <summary>
    /// Finds a host wall: from a wall.draw op of the same job (its centrelines rebuilt exactly as wall.draw builds them,
    /// through Core's WallNetwork), from one line of an existing wall, or from a point between two parallel lines.
    /// Both offset the given lines with Core's ChainOffset. Once the wall.draw op has run, each wall's layer is the one it
    /// reported drawing on.
    /// </summary>
    internal static class WallGeometry
    {
        /// <summary>Thickest wall searched for when only a line or a point is given: 1.5 m.</summary>
        internal static double MaxThickness(OpContext ctx) => Length.Convert(1500, LengthUnit.Millimetre, ctx.JobUnit);

        /// <summary>The op id of a raw "$id" or "$id.x" reference string, else null.</summary>
        internal static string? RefOpId(OpContext ctx, string field)
        {
            var raw = ctx.CurrentOp.Args[field] as SA.Core.Json.JsonString;
            if (raw is null || !Refs.TryParse(raw.Value, out var expr, out _) || expr is null) return null;
            return expr.OpId;
        }

        /// <summary>
        /// Rebuilds the walls of a wall.draw op from its args. Returns null (with the reason) when the op is not a wall.draw
        /// or its args hold references that are only known at execution.
        /// </summary>
        internal static List<HostWall>? FromWallOp(OpContext ctx, JobOp op, out string reason)
        {
            reason = "";
            if (op.Op != "wall.draw") { reason = "op '" + op.Id + "' is " + op.Op + ", not wall.draw"; return null; }
            try
            {
                var args = OpArgs.For(op.Schema, op.Args, ctx.JobUnit, op.ArgsPath);
                string defaultLayer = args.GetStringOrNull("layer") ?? Db.CurrentLayerName(ctx);
                double? defaultThickness = args.GetLengthOrNull("thickness");
                ChainOffset.TryParseJustification(args.GetString("justification"), out var j);
                var drawn = DrawnLayers(ctx, op.Id);
                var inputs = new List<WallInput>();
                var layers = new Dictionary<string, string>(StringComparer.Ordinal);
                int n = 0;
                foreach (var w in args.GetObjectList("walls"))
                {
                    n++;
                    double? t = w.GetLengthOrNull("thickness") ?? defaultThickness;
                    if (!(t > 0)) continue;
                    string id = w.GetStringOrNull("id") ?? "w" + n;
                    var line = ChainOffset.ToCentreline(new List<Point2> { w.GetPoint2("start"), w.GetPoint2("end") }, false, t.Value, j);
                    inputs.Add(new WallInput(id, line[0], line[1], t.Value));
                    layers[id] = w.GetStringOrNull("layer") ?? defaultLayer;
                }
                int c = 0;
                foreach (var ch in args.GetObjectList("chains"))
                {
                    c++;
                    double? t = ch.GetLengthOrNull("thickness") ?? defaultThickness;
                    if (!(t > 0)) continue;
                    string prefix = ch.GetStringOrNull("idPrefix") ?? "c" + c + "_";
                    string layer = ch.GetStringOrNull("layer") ?? defaultLayer;
                    var pts = ch.GetPoint2List("points");
                    bool closed = ch.GetBool("closed");
                    if (closed && pts.Count < 3) continue;
                    var centre = ChainOffset.ToCentreline(pts, closed, t.Value, j);
                    int segs = closed ? centre.Count : centre.Count - 1;
                    for (int i = 0; i < segs; i++)
                    {
                        string id = prefix + (i + 1);
                        inputs.Add(new WallInput(id, centre[i], centre[(i + 1) % centre.Count], t.Value));
                        layers[id] = layer;
                    }
                }
                var options = new WallNetworkOptions
                {
                    Tolerance = args.GetLengthOrNull("tolerance") ?? Length.Convert(1, LengthUnit.Millimetre, ctx.JobUnit),
                    CornerStyle = args.GetString("cornerStyle") == "butt" ? CornerStyle.Butt : CornerStyle.Mitre,
                };
                var net = WallNetwork.Build(inputs, options);
                var result = new List<HostWall>();
                foreach (var w in net.Walls)
                {
                    var d = w.Centreline.Vector;
                    if (d.Length <= 0) continue;
                    result.Add(new HostWall
                    {
                        Key = op.Id + ":" + w.Id,
                        Start = w.Centreline.A,
                        Dir = d.Normalised(),
                        Length = d.Length,
                        Thickness = w.Thickness,
                        Layer = drawn != null && drawn.TryGetValue(w.Id, out var dl) ? dl : layers.TryGetValue(w.Id, out var l) ? l : defaultLayer,
                        PlannedSegments = new List<Segment2>(w.Segments),
                        Source = "wall.draw op '" + op.Id + "', wall '" + w.Id + "'",
                    });
                }
                return result;
            }
            catch (OpArgsException e)
            {
                reason = "the wall.draw args hold values known only at execution (" + e.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// The layer each wall of an already executed wall.draw op was drawn on (its "walls" output), or null before the op
        /// has run. A wall without its own layer takes the current layer at execution, which an op in between may change.
        /// </summary>
        private static Dictionary<string, string>? DrawnLayers(OpContext ctx, string opId)
        {
            var r = ctx.Report.Results.FirstOrDefault(x => x.Id == opId && x.Op == "wall.draw");
            if (r is null || !(r.Outputs["walls"] is SA.Core.Json.JsonArray walls)) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in walls)
                if (v is SA.Core.Json.JsonObject o && o["id"] is SA.Core.Json.JsonString id && o["layer"] is SA.Core.Json.JsonString layer)
                    map[id.Value] = layer.Value;
            return map;
        }

        /// <summary>The wall ids of a rebuilt wall.draw, for error messages.</summary>
        internal static string Ids(List<HostWall> walls) => string.Join(", ", walls.Select(w => w.Key.Substring(w.Key.IndexOf(':') + 1)));

        /// <summary>A wall from one of its face lines: the nearest parallel line on the same layer overlapping it is the other face.</summary>
        internal static HostWall? FromLine(OpContext ctx, Transaction tr, ObjectId lineId, out string reason)
        {
            reason = "";
            if (!(tr.GetObject(lineId, OpenMode.ForRead) is Line line)) { reason = "the host handle is not a LINE"; return null; }
            var a = ctx.Units.ToJob(line.StartPoint);
            var b = ctx.Units.ToJob(line.EndPoint);
            var d = b - a;
            if (d.Length <= ctx.LengthTolerance) { reason = "the host line has no length"; return null; }
            var dir = d.Normalised();
            var n = dir.PerpLeft;
            double best = double.MaxValue;
            double bestSide = 0;
            foreach (var other in LinesOn(ctx, tr, line.Layer))
            {
                if (other.Id == lineId) continue;
                var od = other.B - other.A;
                if (od.Length <= ctx.LengthTolerance || Math.Abs(od.Normalised().Cross(dir)) > 1e-6) continue;
                double side = (other.A - a).Dot(n);
                if (Math.Abs(side) <= ctx.LengthTolerance || Math.Abs(side) > MaxThickness(ctx)) continue;
                double s1 = (other.A - a).Dot(dir), s2 = (other.B - a).Dot(dir);
                if (Math.Max(s1, s2) <= 0 || Math.Min(s1, s2) >= d.Length) continue;
                if (Math.Abs(side) < best) { best = Math.Abs(side); bestSide = side; }
            }
            if (best == double.MaxValue) { reason = "no parallel line on layer '" + line.Layer + "' within " + MaxThickness(ctx) + " of the host line forms the other wall face"; return null; }
            return new HostWall
            {
                Key = "line:" + OpContext.HandleOf(lineId),
                Start = a + n * (bestSide / 2),
                Dir = dir,
                Length = d.Length,
                Thickness = best,
                Layer = line.Layer,
                Source = "line " + OpContext.HandleOf(lineId) + " and its parallel face",
            };
        }

        /// <summary>A wall from a point between two parallel lines on one layer (the closest such pair).</summary>
        internal static HostWall? FromPoint(OpContext ctx, Transaction tr, Point2 p, out string reason)
        {
            reason = "";
            double max = MaxThickness(ctx);
            var near = new List<FaceLine>();
            foreach (var l in LinesOn(ctx, tr, null))
            {
                var seg = new Segment2(l.A, l.B);
                if (seg.Length <= ctx.LengthTolerance) continue;
                double t = seg.Project(p);
                if (t < 0 || t > 1) continue;
                if (seg.LineDistanceTo(p) > max) continue;
                near.Add(l);
            }
            HostWall? best = null;
            for (int i = 0; i < near.Count; i++)
                for (int j = i + 1; j < near.Count; j++)
                {
                    var li = near[i];
                    var lj = near[j];
                    if (!string.Equals(LayerOf(tr, li.Id), LayerOf(tr, lj.Id), StringComparison.OrdinalIgnoreCase)) continue;
                    var di = (li.B - li.A).Normalised();
                    if (Math.Abs(di.Cross((lj.B - lj.A).Normalised())) > 1e-6) continue;
                    var n = di.PerpLeft;
                    double si = (p - li.A).Dot(n);
                    double sj = (p - lj.A).Dot(n);
                    if (si * sj >= 0) continue; // the point must lie between the two lines
                    double t = Math.Abs(si) + Math.Abs(sj);
                    if (t > max || (best != null && t >= best.Thickness)) continue;
                    // Relative to li the point is at offset si and lj at si - sj; the centreline is halfway between the faces.
                    var foot = li.A + di * (p - li.A).Dot(di);
                    var centre = foot + n * ((si - sj) / 2);
                    double s0 = (li.A - centre).Dot(di);
                    best = new HostWall
                    {
                        Key = "pt:" + LayerOf(tr, li.Id) + ":" + Math.Round(centre.X, 3) + "," + Math.Round(centre.Y, 3) + "," + Math.Round(di.X, 6) + "," + Math.Round(di.Y, 6),
                        Start = centre + di * Math.Min(s0, (li.B - centre).Dot(di)),
                        Dir = di,
                        Length = (li.B - li.A).Length,
                        Thickness = t,
                        Layer = LayerOf(tr, li.Id),
                        Source = "lines " + OpContext.HandleOf(li.Id) + " and " + OpContext.HandleOf(lj.Id) + " on either side of the point",
                    };
                }
            if (best is null) reason = "no pair of parallel lines on one layer encloses the point within " + max + " (the thickest wall searched)";
            return best;
        }

        private static string LayerOf(Transaction tr, ObjectId id) => ((Entity)tr.GetObject(id, OpenMode.ForRead)).Layer;

        /// <summary>Model-space lines (optionally on one layer), in job units.</summary>
        internal static List<FaceLine> LinesOn(OpContext ctx, Transaction tr, string? layer)
        {
            var list = new List<FaceLine>();
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.IsErased || !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Line)))) continue;
                var l = (Line)tr.GetObject(id, OpenMode.ForRead);
                if (layer != null && !string.Equals(l.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new FaceLine { Id = id, A = ctx.Units.ToJob(l.StartPoint), B = ctx.Units.ToJob(l.EndPoint) });
            }
            return list;
        }

        /// <summary>Lines on the wall's layer that lie on one of its faces (side = +t/2 or -t/2 from the centreline).</summary>
        internal static List<FaceLine> FaceLines(OpContext ctx, Transaction tr, HostWall w)
        {
            var list = new List<FaceLine>();
            double tol = Math.Max(ctx.LengthTolerance, 1e-9);
            foreach (var l in LinesOn(ctx, tr, w.Layer))
            {
                var d = l.B - l.A;
                if (d.Length <= tol || Math.Abs(d.Normalised().Cross(w.Dir)) > 1e-6) continue;
                double sa = w.Across(l.A), sb = w.Across(l.B);
                foreach (double side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                    if (Math.Abs(sa - side) <= tol && Math.Abs(sb - side) <= tol)
                    {
                        l.Side = side;
                        l.SA = w.Along(l.A);
                        l.SB = w.Along(l.B);
                        list.Add(l);
                    }
            }
            return list;
        }

        /// <summary>Lines on the wall layer that are not parallel to it and touch a face strictly between s1 and s2 (a wall meeting the face).</summary>
        internal static List<FaceLine> CrossingLines(OpContext ctx, Transaction tr, HostWall w, double s1, double s2)
        {
            var list = new List<FaceLine>();
            double tol = Math.Max(ctx.LengthTolerance, 1e-9);
            foreach (var l in LinesOn(ctx, tr, w.Layer))
            {
                var d = l.B - l.A;
                if (d.Length <= tol || Math.Abs(d.Normalised().Cross(w.Dir)) <= 1e-6) continue;
                foreach (var side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                {
                    var face = new Segment2(w.At(s1 + tol, side), w.At(s2 - tol, side));
                    if (face.Length <= 0) continue;
                    var x = face.Intersect(new Segment2(l.A, l.B), tol);
                    if (x.Kind != IntersectionKind.None) { list.Add(l); break; }
                }
            }
            return list;
        }
    }
}
