using System;
using System.Collections.Generic;
using System.Globalization;

namespace SA.Core.Geometry
{
    /// <summary>A 2D point or vector. Units are whatever the caller uses consistently.</summary>
    public readonly struct Point2 : IEquatable<Point2>
    {
        public Point2(double x, double y) { X = x; Y = y; }

        public double X { get; }
        public double Y { get; }

        public static readonly Point2 Origin = new Point2(0, 0);

        public static Point2 operator +(Point2 a, Point2 b) => new Point2(a.X + b.X, a.Y + b.Y);
        public static Point2 operator -(Point2 a, Point2 b) => new Point2(a.X - b.X, a.Y - b.Y);
        public static Point2 operator -(Point2 a) => new Point2(-a.X, -a.Y);
        public static Point2 operator *(Point2 a, double k) => new Point2(a.X * k, a.Y * k);
        public static Point2 operator *(double k, Point2 a) => new Point2(a.X * k, a.Y * k);
        public static Point2 operator /(Point2 a, double k) => new Point2(a.X / k, a.Y / k);

        public double Length => Math.Sqrt(X * X + Y * Y);
        public double LengthSquared => X * X + Y * Y;

        public double Dot(Point2 o) => X * o.X + Y * o.Y;

        /// <summary>z of the 3D cross product; positive when <paramref name="o"/> is counter-clockwise from this.</summary>
        public double Cross(Point2 o) => X * o.Y - Y * o.X;

        public double DistanceTo(Point2 o) => (o - this).Length;

        /// <summary>Unit vector. Throws for a zero vector.</summary>
        public Point2 Normalised()
        {
            double l = Length;
            if (l == 0) throw new InvalidOperationException("Cannot normalise a zero vector.");
            return new Point2(X / l, Y / l);
        }

        /// <summary>Rotated 90 degrees counter-clockwise.</summary>
        public Point2 PerpLeft => new Point2(-Y, X);

        public Point2 Rotate(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Point2(X * c - Y * s, X * s + Y * c);
        }

        /// <summary>Angle of this vector in radians, in (-pi, pi].</summary>
        public double Angle => Math.Atan2(Y, X);

        public bool AlmostEquals(Point2 o, double tolerance) => DistanceTo(o) <= tolerance;

        public bool Equals(Point2 other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object? obj) => obj is Point2 p && Equals(p);
        public override int GetHashCode() => unchecked(X.GetHashCode() * 397 ^ Y.GetHashCode());
        public static bool operator ==(Point2 a, Point2 b) => a.Equals(b);
        public static bool operator !=(Point2 a, Point2 b) => !a.Equals(b);

        public override string ToString() =>
            "(" + X.ToString("R", CultureInfo.InvariantCulture) + ", " + Y.ToString("R", CultureInfo.InvariantCulture) + ")";
    }

    public readonly struct Point3 : IEquatable<Point3>
    {
        public Point3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public Point2 XY => new Point2(X, Y);
        public bool Equals(Point3 o) => X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z);
        public override bool Equals(object? obj) => obj is Point3 p && Equals(p);
        public override int GetHashCode() => unchecked((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode());
        public override string ToString() =>
            "(" + X.ToString("R", CultureInfo.InvariantCulture) + ", " + Y.ToString("R", CultureInfo.InvariantCulture) + ", " + Z.ToString("R", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>An axis-aligned rectangle. Y grows upwards (sheet and model convention).</summary>
    public readonly struct Rect2
    {
        public Rect2(double minX, double minY, double width, double height)
        {
            if (width < 0 || height < 0) throw new ArgumentException("Rectangle size must not be negative.");
            MinX = minX; MinY = minY; Width = width; Height = height;
        }

        public static Rect2 FromCorners(Point2 a, Point2 b) =>
            new Rect2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        public double MinX { get; }
        public double MinY { get; }
        public double Width { get; }
        public double Height { get; }
        public double MaxX => MinX + Width;
        public double MaxY => MinY + Height;
        public Point2 Centre => new Point2(MinX + Width / 2, MinY + Height / 2);

        public bool Contains(Rect2 r, double tolerance = 0) =>
            r.MinX >= MinX - tolerance && r.MinY >= MinY - tolerance && r.MaxX <= MaxX + tolerance && r.MaxY <= MaxY + tolerance;

        /// <summary>True when the interiors overlap by more than the tolerance (touching edges do not count).</summary>
        public bool Overlaps(Rect2 r, double tolerance = 0) =>
            MinX < r.MaxX - tolerance && r.MinX < MaxX - tolerance && MinY < r.MaxY - tolerance && r.MinY < MaxY - tolerance;

        public Rect2 Deflate(double margin) => new Rect2(MinX + margin, MinY + margin, Math.Max(0, Width - 2 * margin), Math.Max(0, Height - 2 * margin));

        public override string ToString() =>
            "[" + MinX.ToString("R", CultureInfo.InvariantCulture) + ", " + MinY.ToString("R", CultureInfo.InvariantCulture) + ", " +
            Width.ToString("R", CultureInfo.InvariantCulture) + " x " + Height.ToString("R", CultureInfo.InvariantCulture) + "]";
    }

    public enum IntersectionKind { None, Point, Overlap }

    public readonly struct SegmentIntersection
    {
        public SegmentIntersection(IntersectionKind kind, Point2 point, Point2 overlapEnd, double t, double u)
        {
            Kind = kind; Point = point; OverlapEnd = overlapEnd; T = t; U = u;
        }

        public IntersectionKind Kind { get; }
        /// <summary>The crossing point, or the start of the shared part for an overlap.</summary>
        public Point2 Point { get; }
        /// <summary>The end of the shared part for an overlap; equals <see cref="Point"/> otherwise.</summary>
        public Point2 OverlapEnd { get; }
        /// <summary>Parameter of <see cref="Point"/> along the first segment (0 at A, 1 at B).</summary>
        public double T { get; }
        /// <summary>Parameter of <see cref="Point"/> along the second segment.</summary>
        public double U { get; }
    }

    public readonly struct Segment2
    {
        public Segment2(Point2 a, Point2 b) { A = a; B = b; }
        public Segment2(double ax, double ay, double bx, double by) { A = new Point2(ax, ay); B = new Point2(bx, by); }

        public Point2 A { get; }
        public Point2 B { get; }
        public Point2 Vector => B - A;
        public double Length => (B - A).Length;
        public Point2 Midpoint => (A + B) * 0.5;
        public Segment2 Reversed => new Segment2(B, A);

        public Point2 PointAt(double t) => A + (B - A) * t;

        /// <summary>Parameter of the closest point on the infinite line (not clamped).</summary>
        public double Project(Point2 p)
        {
            var d = B - A;
            double l2 = d.LengthSquared;
            return l2 == 0 ? 0 : (p - A).Dot(d) / l2;
        }

        public Point2 ClosestPoint(Point2 p)
        {
            double t = Project(p);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return PointAt(t);
        }

        public double DistanceTo(Point2 p) => ClosestPoint(p).DistanceTo(p);

        /// <summary>Distance from the infinite line through this segment.</summary>
        public double LineDistanceTo(Point2 p)
        {
            double l = Length;
            return l == 0 ? p.DistanceTo(A) : Math.Abs((B - A).Cross(p - A)) / l;
        }

        /// <summary>True when both segments lie on the same line within <paramref name="tolerance"/>.</summary>
        public bool IsCollinearWith(Segment2 o, double tolerance) =>
            LineDistanceTo(o.A) <= tolerance && LineDistanceTo(o.B) <= tolerance;

        /// <summary>
        /// Intersects two segments. Endpoints within <paramref name="tolerance"/> of the other segment count as touching,
        /// and collinear segments sharing more than a point report <see cref="IntersectionKind.Overlap"/>.
        /// </summary>
        public SegmentIntersection Intersect(Segment2 o, double tolerance)
        {
            double len = Length, olen = o.Length;
            if (len <= tolerance || olen <= tolerance)
            {
                // Degenerate: treat as a point.
                Segment2 seg = len <= tolerance ? o : this;
                Point2 pt = len <= tolerance ? A : o.A;
                if (seg.DistanceTo(pt) <= tolerance)
                {
                    double tt = len <= tolerance ? 0 : Project(pt), uu = len <= tolerance ? o.Project(pt) : 0;
                    return new SegmentIntersection(IntersectionKind.Point, pt, pt, tt, uu);
                }
                return default;
            }

            if (IsCollinearWith(o, tolerance))
            {
                var d = (B - A) / len;
                double s0 = (o.A - A).Dot(d), s1 = (o.B - A).Dot(d);
                double lo = Math.Max(0, Math.Min(s0, s1)), hi = Math.Min(len, Math.Max(s0, s1));
                if (hi < lo - tolerance) return default;
                if (hi - lo <= tolerance)
                {
                    double mid = (lo + hi) / 2;
                    var p = A + d * mid;
                    return new SegmentIntersection(IntersectionKind.Point, p, p, mid / len, o.Project(p));
                }
                var p0 = A + d * lo;
                return new SegmentIntersection(IntersectionKind.Overlap, p0, A + d * hi, lo / len, o.Project(p0));
            }

            var r = B - A;
            var q = o.B - o.A;
            double denom = r.Cross(q);
            if (Math.Abs(denom) > 1e-15 * len * olen)
            {
                double t = (o.A - A).Cross(q) / denom;
                double u = (o.A - A).Cross(r) / denom;
                double tTol = tolerance / len, uTol = tolerance / olen;
                if (t >= -tTol && t <= 1 + tTol && u >= -uTol && u <= 1 + uTol)
                {
                    var p = A + r * t;
                    return new SegmentIntersection(IntersectionKind.Point, p, p, t, u);
                }
            }

            // Near misses at endpoints (almost parallel, or just short of each other).
            Point2[] cands = { A, B, o.A, o.B };
            for (int k = 0; k < 4; k++)
            {
                var pt = cands[k];
                var other = k < 2 ? o : this;
                if (other.DistanceTo(pt) <= tolerance)
                    return new SegmentIntersection(IntersectionKind.Point, pt, pt, Project(pt), o.Project(pt));
            }
            return default;
        }

        public override string ToString() => A + " -> " + B;
    }

    public static class Lines
    {
        /// <summary>Intersects the infinite lines p + t*d and q + u*e. Returns false when they are parallel.</summary>
        public static bool Intersect(Point2 p, Point2 d, Point2 q, Point2 e, out Point2 point)
        {
            double denom = d.Cross(e);
            double scale = d.Length * e.Length;
            if (scale == 0 || Math.Abs(denom) <= 1e-12 * scale)
            {
                point = default;
                return false;
            }
            double t = (q - p).Cross(e) / denom;
            point = p + d * t;
            return true;
        }
    }

    /// <summary>An open or closed sequence of points.</summary>
    public sealed class Polyline2
    {
        public Polyline2(IEnumerable<Point2> points, bool closed)
        {
            Points = new List<Point2>(points);
            Closed = closed;
        }

        public IReadOnlyList<Point2> Points { get; }
        public bool Closed { get; }

        public double Length
        {
            get
            {
                double l = 0;
                for (int i = 1; i < Points.Count; i++) l += Points[i - 1].DistanceTo(Points[i]);
                if (Closed && Points.Count > 1) l += Points[Points.Count - 1].DistanceTo(Points[0]);
                return l;
            }
        }

        /// <summary>True when the polyline is flagged closed or its ends coincide within the tolerance.</summary>
        public bool IsClosed(double tolerance) =>
            Closed || (Points.Count > 2 && Points[0].DistanceTo(Points[Points.Count - 1]) <= tolerance);

        public IEnumerable<Segment2> Segments()
        {
            for (int i = 1; i < Points.Count; i++) yield return new Segment2(Points[i - 1], Points[i]);
            if (Closed && Points.Count > 2) yield return new Segment2(Points[Points.Count - 1], Points[0]);
        }

        /// <summary>The polygon this polyline encloses, dropping a duplicated closing point. Null if it is not closed.</summary>
        public Polygon2? ToPolygon(double tolerance)
        {
            if (!IsClosed(tolerance)) return null;
            var pts = new List<Point2>(Points);
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tolerance) pts.RemoveAt(pts.Count - 1);
            return pts.Count >= 3 ? new Polygon2(pts) : null;
        }
    }

    public enum PointLocation { Outside, Inside, OnBoundary }

    /// <summary>A simple polygon given by its vertices (the closing edge is implied).</summary>
    public sealed class Polygon2
    {
        public Polygon2(IEnumerable<Point2> vertices)
        {
            Vertices = new List<Point2>(vertices);
            if (Vertices.Count < 3) throw new ArgumentException("A polygon needs at least three vertices.", nameof(vertices));
        }

        public IReadOnlyList<Point2> Vertices { get; }

        /// <summary>Shoelace area: positive for counter-clockwise vertex order, negative for clockwise.</summary>
        public double SignedArea
        {
            get
            {
                double a = 0;
                int n = Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    var p = Vertices[i];
                    var q = Vertices[(i + 1) % n];
                    a += p.X * q.Y - q.X * p.Y;
                }
                return a / 2;
            }
        }

        public double Area => Math.Abs(SignedArea);
        public bool IsCounterClockwise => SignedArea > 0;

        public double Perimeter
        {
            get
            {
                double l = 0;
                for (int i = 0; i < Vertices.Count; i++) l += Vertices[i].DistanceTo(Vertices[(i + 1) % Vertices.Count]);
                return l;
            }
        }

        public Point2 Centroid
        {
            get
            {
                double a = SignedArea;
                if (a == 0)
                {
                    double sx = 0, sy = 0;
                    foreach (var v in Vertices) { sx += v.X; sy += v.Y; }
                    return new Point2(sx / Vertices.Count, sy / Vertices.Count);
                }
                double cx = 0, cy = 0;
                int n = Vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    var p = Vertices[i];
                    var q = Vertices[(i + 1) % n];
                    double f = p.X * q.Y - q.X * p.Y;
                    cx += (p.X + q.X) * f;
                    cy += (p.Y + q.Y) * f;
                }
                return new Point2(cx / (6 * a), cy / (6 * a));
            }
        }

        public IEnumerable<Segment2> Edges()
        {
            for (int i = 0; i < Vertices.Count; i++) yield return new Segment2(Vertices[i], Vertices[(i + 1) % Vertices.Count]);
        }

        public Polygon2 Reversed()
        {
            var v = new List<Point2>(Vertices);
            v.Reverse();
            return new Polygon2(v);
        }

        /// <summary>Locates a point; points within <paramref name="tolerance"/> of an edge are on the boundary.</summary>
        public PointLocation Locate(Point2 p, double tolerance)
        {
            foreach (var e in Edges())
                if (e.DistanceTo(p) <= tolerance) return PointLocation.OnBoundary;

            bool inside = false;
            int n = Vertices.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = Vertices[i];
                var b = Vertices[j];
                if ((a.Y > p.Y) != (b.Y > p.Y))
                {
                    double x = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
                    if (p.X < x) inside = !inside;
                }
            }
            return inside ? PointLocation.Inside : PointLocation.Outside;
        }

        public bool Contains(Point2 p, double tolerance = 0) => Locate(p, tolerance) != PointLocation.Outside;

        /// <summary>True when no two non-adjacent edges touch and no edge has zero length.</summary>
        public bool IsSimple(double tolerance)
        {
            var edges = new List<Segment2>(Edges());
            int n = edges.Count;
            for (int i = 0; i < n; i++)
            {
                if (edges[i].Length <= tolerance) return false;
                for (int j = i + 1; j < n; j++)
                {
                    bool adjacent = j == i + 1 || (i == 0 && j == n - 1);
                    var x = edges[i].Intersect(edges[j], tolerance);
                    if (x.Kind == IntersectionKind.None) continue;
                    if (!adjacent) return false;
                    if (x.Kind == IntersectionKind.Overlap) return false;
                }
            }
            return true;
        }
    }
}
