using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.Core.Geometry;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    /// <summary>A closed outline in plan (job units), with its exact area when the source had arcs.</summary>
    internal sealed class PlanLoop
    {
        public PlanLoop(List<Point2> points, double area)
        {
            Points = points;
            Area = area;
        }

        /// <summary>Tessellated outline, arcs as chords of at most <see cref="PlanGeometry.ChordDegrees"/>.</summary>
        public List<Point2> Points { get; }
        /// <summary>Exact enclosed area (arcs included), job units squared, positive.</summary>
        public double Area { get; }

        public double Perimeter
        {
            get
            {
                double l = 0;
                for (int i = 0; i < Points.Count; i++) l += Points[i].DistanceTo(Points[(i + 1) % Points.Count]);
                return l;
            }
        }

        public Polygon2? Polygon => Points.Count >= 3 ? new Polygon2(Points) : null;
    }

    /// <summary>
    /// Plan geometry of entities in job units. Lightweight polylines and hatch loops are stored in their object coordinate
    /// system (OCS): vertices are tessellated there, then mapped to world with Matrix3d.PlaneToWorld(normal) at the
    /// entity's elevation, and Z is dropped. A bulge is tan(included angle / 4); positive bulges turn counter-clockwise
    /// about the OCS normal, which is clockwise seen from above when the normal points down (mirrored polylines).
    /// </summary>
    internal static class PlanGeometry
    {
        internal const double ChordDegrees = 5;

        /// <summary>True when the normal is parallel to world Z (the entity lies in a plan plane).</summary>
        internal static bool IsPlan(Vector3d normal) => Math.Abs(Math.Abs(normal.GetNormal().Z) - 1) < 1e-9;

        /// <summary>Points of the arc from a to b with bulge b (both ends included), in the same 2D frame.</summary>
        internal static List<Point2> BulgeArc(Point2 a, Point2 b, double bulge, int minChords = 1)
        {
            var pts = new List<Point2> { a };
            if (Math.Abs(bulge) < 1e-12 || a.DistanceTo(b) < 1e-15) { pts.Add(b); return pts; }
            double theta = 4 * Math.Atan(bulge);
            double chord = a.DistanceTo(b);
            double r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var mid = (a + b) * 0.5;
            var dir = (b - a) / chord;
            // h is negative past a half circle, which moves the centre across the chord.
            double h = r * Math.Cos(Math.Abs(theta) / 2);
            var centre = mid + dir.PerpLeft * (h * Math.Sign(bulge));
            double a0 = Math.Atan2(a.Y - centre.Y, a.X - centre.X);
            int n = Math.Max(minChords, (int)Math.Ceiling(Math.Abs(theta) / (ChordDegrees * Math.PI / 180)));
            for (int i = 1; i < n; i++)
            {
                double t = a0 + theta * i / n;
                pts.Add(new Point2(centre.X + r * Math.Cos(t), centre.Y + r * Math.Sin(t)));
            }
            pts.Add(b);
            return pts;
        }

        /// <summary>Signed area (counter-clockwise positive) of a closed loop of vertices with bulges, exact for the arcs.</summary>
        internal static double SignedArea(IReadOnlyList<Point2> v, IReadOnlyList<double> bulges)
        {
            double a = 0;
            int n = v.Count;
            for (int i = 0; i < n; i++)
            {
                var p = v[i];
                var q = v[(i + 1) % n];
                a += (p.X * q.Y - q.X * p.Y) / 2;
                double bu = i < bulges.Count ? bulges[i] : 0;
                if (Math.Abs(bu) < 1e-12) continue;
                double theta = 4 * Math.Atan(Math.Abs(bu));
                double c = p.DistanceTo(q);
                double r = c / (2 * Math.Sin(theta / 2));
                a += Math.Sign(bu) * r * r / 2 * (theta - Math.Sin(theta));
            }
            return a;
        }

        /// <summary>Tessellates a closed loop of vertices with bulges (open: <paramref name="closed"/> false).</summary>
        internal static List<Point2> Tessellate(IReadOnlyList<Point2> v, IReadOnlyList<double> bulges, bool closed)
        {
            var pts = new List<Point2>();
            int segs = closed ? v.Count : v.Count - 1;
            for (int i = 0; i < segs; i++)
            {
                var arc = BulgeArc(v[i], v[(i + 1) % v.Count], i < bulges.Count ? bulges[i] : 0);
                arc.RemoveAt(arc.Count - 1);
                pts.AddRange(arc);
            }
            if (!closed && v.Count > 0) pts.Add(v[v.Count - 1]);
            return pts;
        }

        private static Func<Point2d, Point2> OcsMapper(OpContext ctx, Vector3d normal, double elevation)
        {
            var m = Matrix3d.PlaneToWorld(normal);
            return p => ctx.Units.ToJob(new Point3d(p.X, p.Y, elevation).TransformBy(m));
        }

        /// <summary>Vertices (job units, world XY) and bulges of a lightweight polyline; bulges flipped when its normal points down.</summary>
        internal static void Vertices(OpContext ctx, Polyline pl, out List<Point2> v, out List<double> bulges)
        {
            var map = OcsMapper(ctx, pl.Normal, pl.Elevation);
            double sign = pl.Normal.Z < 0 ? -1 : 1;
            v = new List<Point2>();
            bulges = new List<double>();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                v.Add(map(pl.GetPoint2dAt(i)));
                bulges.Add(sign * pl.GetBulgeAt(i));
            }
        }

        /// <summary>The closed outline of a closed curve, or null (open, not in plan, or a kind not handled).</summary>
        internal static PlanLoop? ClosedLoop(OpContext ctx, Entity e)
        {
            switch (e)
            {
                case Polyline pl:
                {
                    if (!IsPlan(pl.Normal)) return null;
                    Vertices(ctx, pl, out var v, out var b);
                    if (v.Count > 2 && !pl.Closed && v[0].DistanceTo(v[v.Count - 1]) <= ctx.LengthTolerance) { v.RemoveAt(v.Count - 1); b.RemoveAt(b.Count - 1); }
                    else if (!pl.Closed) return null;
                    if (v.Count < 2) return null;
                    var pts = Tessellate(v, b, true);
                    return pts.Count < 3 ? null : new PlanLoop(pts, Math.Abs(SignedArea(v, b)));
                }
                case Circle c:
                {
                    if (!IsPlan(c.Normal)) return null;
                    double r = ctx.Units.ToJob(c.Radius);
                    var centre = ctx.Units.ToJob(c.Center);
                    int n = (int)Math.Ceiling(360 / ChordDegrees);
                    var pts = Enumerable.Range(0, n).Select(i => new Point2(centre.X + r * Math.Cos(2 * Math.PI * i / n), centre.Y + r * Math.Sin(2 * Math.PI * i / n))).ToList();
                    return new PlanLoop(pts, Math.PI * r * r);
                }
                case Curve cv when (cv is Ellipse || cv is Spline):
                {
                    if (!cv.Closed) return null;
                    var pts = Sample(ctx, cv, 144);
                    if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= ctx.LengthTolerance) pts.RemoveAt(pts.Count - 1);
                    if (pts.Count < 3) return null;
                    double f = ctx.Units.ToJob(1.0);
                    double area = cv.Area * f * f;
                    return new PlanLoop(pts, area);
                }
                default:
                    return null;
            }
        }

        /// <summary>Points along any curve by parameter, world XY in job units.</summary>
        internal static List<Point2> Sample(OpContext ctx, Curve cv, int count)
        {
            var pts = new List<Point2>();
            double t0 = cv.StartParam, t1 = cv.EndParam;
            for (int i = 0; i <= count; i++) pts.Add(ctx.Units.ToJob(cv.GetPointAtParameter(t0 + (t1 - t0) * i / count)));
            return pts;
        }

        /// <summary>
        /// Loops of a hatch as plan outlines with exact areas for line and arc edges; edges of other kinds (ellipse arcs,
        /// splines) are sampled. Null when the hatch is not in plan.
        /// </summary>
        internal static List<PlanLoop>? HatchLoops(OpContext ctx, Hatch h)
        {
            if (!IsPlan(h.Normal)) return null;
            var map = OcsMapper(ctx, h.Normal, h.Elevation);
            double sign = h.Normal.Z < 0 ? -1 : 1;
            var loops = new List<PlanLoop>();
            for (int i = 0; i < h.NumberOfLoops; i++)
            {
                var loop = h.GetLoopAt(i);
                var v = new List<Point2>();
                var b = new List<double>();
                if (loop.IsPolyline)
                {
                    foreach (BulgeVertex bv in loop.Polyline) { v.Add(map(bv.Vertex)); b.Add(sign * bv.Bulge); }
                    if (v.Count > 2 && v[0].DistanceTo(v[v.Count - 1]) <= ctx.LengthTolerance) { v.RemoveAt(v.Count - 1); b.RemoveAt(b.Count - 1); }
                    var pts = Tessellate(v, b, true);
                    if (pts.Count >= 3) loops.Add(new PlanLoop(pts, Math.Abs(SignedArea(v, b))));
                    continue;
                }
                var all = new List<Point2>();
                double exact = 0;
                bool exactOk = true;
                foreach (Curve2d c in loop.Curves)
                {
                    if (c is LineSegment2d ls)
                    {
                        all.Add(map(ls.StartPoint));
                        v.Add(map(ls.StartPoint)); b.Add(0);
                    }
                    else if (c is CircularArc2d ca && ArcBulge(ca, map, out var start, out var end, out double bulge))
                    {
                        v.Add(start); b.Add(bulge);
                        var s = BulgeArc(start, end, bulge);
                        s.RemoveAt(s.Count - 1);
                        all.AddRange(s);
                    }
                    else
                    {
                        exactOk = false;
                        var s = c.GetSamplePoints(48);
                        for (int k = 0; k < s.Length - 1; k++) all.Add(map(s[k]));
                    }
                }
                if (all.Count < 3) continue;
                exact = exactOk ? Math.Abs(SignedArea(v, b)) : new Polygon2(all).Area;
                loops.Add(new PlanLoop(all, exact));
            }
            return loops;
        }

        /// <summary>
        /// Bulge of a 2D arc from its start, middle and end samples in world plan (bulge = 2 x sagitta / chord, positive
        /// when the arc lies to the right of start->end), so no angle or direction convention of the arc is relied on.
        /// </summary>
        private static bool ArcBulge(CircularArc2d ca, Func<Point2d, Point2> map, out Point2 start, out Point2 end, out double bulge)
        {
            var s = ca.GetSamplePoints(3);
            start = end = default;
            bulge = 0;
            if (s.Length != 3) return false;
            start = map(s[0]);
            var mid = map(s[1]);
            end = map(s[2]);
            var chord = end - start;
            double c = chord.Length;
            if (c < 1e-12) return false;
            double sag = chord.Cross(mid - start) / c;
            bulge = -2 * sag / c;
            return Math.Abs(mid.DistanceTo(start) - mid.DistanceTo(end)) <= 1e-6 * Math.Max(1, c);
        }

        /// <summary>
        /// Plan segments (job units) of an entity, for clearance tests. Curves are tessellated; texts, dimensions and other
        /// entities without curve geometry give their extents rectangle; block references are exploded recursively (up to
        /// <paramref name="depth"/> levels, the temporary objects disposed) or reduced to their extents.
        /// </summary>
        internal static List<Segment2> Segments(OpContext ctx, Transaction tr, Entity e, bool explodeBlocks, int depth = 6)
        {
            var segs = new List<Segment2>();
            Collect(ctx, tr, e, explodeBlocks, depth, segs);
            return segs;
        }

        private static void Collect(OpContext ctx, Transaction tr, Entity e, bool explode, int depth, List<Segment2> segs)
        {
            switch (e)
            {
                case Line ln:
                    segs.Add(new Segment2(ctx.Units.ToJob(ln.StartPoint), ctx.Units.ToJob(ln.EndPoint)));
                    return;
                case Polyline pl:
                {
                    Vertices(ctx, pl, out var v, out var b);
                    AddPath(segs, Tessellate(v, b, pl.Closed), false);
                    return;
                }
                case Circle c:
                    AddPath(segs, Sample(ctx, c, (int)Math.Ceiling(360 / ChordDegrees)), false);
                    return;
                case Arc a:
                {
                    double sweep = a.EndAngle - a.StartAngle;
                    if (sweep < 0) sweep += 2 * Math.PI;
                    AddPath(segs, Sample(ctx, a, Math.Max(2, (int)Math.Ceiling(sweep * 180 / Math.PI / ChordDegrees))), false);
                    return;
                }
                case Curve cv when !(cv is Ray || cv is Xline):
                    AddPath(segs, Sample(ctx, cv, 96), false);
                    return;
                case DBPoint pt:
                {
                    var p = ctx.Units.ToJob(pt.Position);
                    segs.Add(new Segment2(p, p));
                    return;
                }
                case Hatch h:
                {
                    var loops = HatchLoops(ctx, h);
                    if (loops != null) { foreach (var l in loops) AddPath(segs, l.Points, true); return; }
                    break;
                }
                case BlockReference br when explode && depth > 0 && !(br.BlockTableRecord.IsNull):
                {
                    var parts = new DBObjectCollection();
                    try
                    {
                        br.Explode(parts);
                        foreach (DBObject o in parts)
                            if (o is Entity pe && pe.Visible && !(pe is AttributeDefinition)) Collect(ctx, tr, pe, true, depth - 1, segs);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        AddBox(ctx, br, segs);
                    }
                    finally
                    {
                        foreach (DBObject o in parts) o.Dispose();
                    }
                    // Exploded nested references are not database-resident and have no attribute ids.
                    // ObjectId.GetObject would need a top transaction, which validation's open-close transaction is not.
                    if (br.ObjectId.IsNull) return;
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        if (aid.IsErased) continue;
                        if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar && !ar.Invisible && ar.Visible) AddBox(ctx, ar, segs);
                    }
                    return;
                }
            }
            AddBox(ctx, e, segs);
        }

        private static void AddPath(List<Segment2> segs, List<Point2> pts, bool closed)
        {
            for (int i = 0; i + 1 < pts.Count; i++) segs.Add(new Segment2(pts[i], pts[i + 1]));
            if (closed && pts.Count > 2) segs.Add(new Segment2(pts[pts.Count - 1], pts[0]));
        }

        private static void AddBox(OpContext ctx, Entity e, List<Segment2> segs)
        {
            Extents3d ext;
            try { ext = e.GeometricExtents; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { return; } // entities without extents (empty text) have no geometry to test
            var a = ctx.Units.ToJob(ext.MinPoint);
            var b = ctx.Units.ToJob(ext.MaxPoint);
            AddPath(segs, new List<Point2> { a, new Point2(b.X, a.Y), b, new Point2(a.X, b.Y) }, true);
        }

        /// <summary>A point strictly inside a polygon: the centroid when inside, else the middle of the widest horizontal span through it.</summary>
        internal static Point2 InteriorPoint(Polygon2 poly)
        {
            var c = poly.Centroid;
            if (poly.Locate(c, 0) == PointLocation.Inside) return c;
            var xs = new List<double>();
            foreach (var e in poly.Edges())
            {
                if ((e.A.Y > c.Y) == (e.B.Y > c.Y)) continue;
                xs.Add(e.A.X + (c.Y - e.A.Y) * (e.B.X - e.A.X) / (e.B.Y - e.A.Y));
            }
            xs.Sort();
            double best = -1;
            var pt = c;
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > best) { best = xs[i + 1] - xs[i]; pt = new Point2((xs[i] + xs[i + 1]) / 2, c.Y); }
            return pt;
        }

        /// <summary>True when every vertex of <paramref name="inner"/> lies inside or on <paramref name="outer"/> and its interior point is inside.</summary>
        internal static bool Contains(Polygon2 outer, Polygon2 inner, double tol) =>
            inner.Vertices.All(p => outer.Locate(p, tol) != PointLocation.Outside) && outer.Locate(InteriorPoint(inner), 0) == PointLocation.Inside;

        /// <summary>True when the polygon is convex (collinear vertices allowed).</summary>
        internal static bool IsConvex(IReadOnlyList<Point2> pts)
        {
            int n = pts.Count, sign = 0;
            for (int i = 0; i < n; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % n]; var c = pts[(i + 2) % n];
                double cross = (b - a).Cross(c - b);
                if (Math.Abs(cross) < 1e-12) continue;
                int s = Math.Sign(cross);
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return sign != 0;
        }

        /// <summary>The polygon grown outward by <paramref name="d"/> (mitred corners); counter-clockwise result.</summary>
        internal static List<Point2> Grow(List<Point2> pts, double d)
        {
            var p = new Polygon2(pts).IsCounterClockwise ? pts.ToList() : Enumerable.Reverse(pts).ToList();
            if (d <= 0) return p;
            return ChainOffset.Offset(p, true, -d);
        }
    }
}
