using System;
using System.Collections.Generic;

namespace SA.Core.Geometry
{
    /// <summary>Which line of a wall a drawn chain describes.</summary>
    public enum WallJustification
    {
        /// <summary>The points are the centreline.</summary>
        Centre,
        /// <summary>The points are the left face (looking from each point to the next); the wall lies to their right.</summary>
        Left,
        /// <summary>The points are the right face; the wall lies to their left.</summary>
        Right,
    }

    /// <summary>
    /// Offsets an open or closed chain of points sideways, meeting adjacent offset segments at the intersection of their
    /// lines, so a chain drawn on a wall face becomes the centreline <see cref="WallNetwork"/> expects.
    /// </summary>
    public static class ChainOffset
    {
        /// <summary>
        /// Offsets each segment by <paramref name="distance"/> to its left (negative: to its right) and joins neighbours
        /// at the intersection of the offset lines. Open chains keep their end points' offsets; a vertex between parallel
        /// segments takes the start of the next offset segment. A zero-length segment is not offset. The result has one
        /// point per input point. Fewer than two points, or a zero distance, returns a copy.
        /// </summary>
        public static List<Point2> Offset(IReadOnlyList<Point2> points, bool closed, double distance)
        {
            if (points is null) throw new ArgumentNullException(nameof(points));
            if (double.IsNaN(distance) || double.IsInfinity(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
            var copy = new List<Point2>(points);
            if (distance == 0 || points.Count < 2) return copy;
            int segs = closed ? points.Count : points.Count - 1;
            var basePts = new Point2[segs];
            var dirs = new Point2[segs];
            for (int i = 0; i < segs; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var d = b - a;
                var shift = d.Length == 0 ? Point2.Origin : d.Normalised().PerpLeft * distance;
                basePts[i] = a + shift;
                dirs[i] = d;
            }
            var result = new List<Point2>(points.Count);
            for (int v = 0; v < points.Count; v++)
            {
                if (!closed && v == 0) { result.Add(basePts[0]); continue; }
                if (!closed && v == points.Count - 1) { result.Add(basePts[segs - 1] + dirs[segs - 1]); continue; }
                int next = v < segs ? v : segs - 1;
                int prev = closed ? (v - 1 + segs) % segs : v - 1;
                result.Add(Lines.Intersect(basePts[prev], dirs[prev], basePts[next], dirs[next], out var x) ? x : basePts[next]);
            }
            return result;
        }

        /// <summary>
        /// The centreline of a wall of <paramref name="thickness"/> drawn along a face: Left shifts the chain by half the
        /// thickness to its right, Right to its left, Centre returns a copy.
        /// </summary>
        public static List<Point2> ToCentreline(IReadOnlyList<Point2> points, bool closed, double thickness, WallJustification justification)
        {
            if (!(thickness >= 0)) throw new ArgumentOutOfRangeException(nameof(thickness), "Thickness must not be negative.");
            switch (justification)
            {
                case WallJustification.Left: return Offset(points, closed, -thickness / 2);
                case WallJustification.Right: return Offset(points, closed, thickness / 2);
                default: return new List<Point2>(points);
            }
        }

        /// <summary>Parses "centre" (or "center"), "left" or "right", as used by wall.draw's justification.</summary>
        public static bool TryParseJustification(string? text, out WallJustification justification)
        {
            switch (text)
            {
                case "centre": case "center": justification = WallJustification.Centre; return true;
                case "left": justification = WallJustification.Left; return true;
                case "right": justification = WallJustification.Right; return true;
                default: justification = WallJustification.Centre; return false;
            }
        }
    }
}
