using System;
using System.Collections.Generic;

namespace SA.Core.Geometry
{
    public enum SegmentRelation
    {
        /// <summary>Same endpoints (in either direction) within tolerance.</summary>
        Duplicate,
        /// <summary>Collinear and sharing a length longer than the tolerance, but not duplicates.</summary>
        Overlap,
    }

    public readonly struct SegmentPair
    {
        public SegmentPair(int first, int second, SegmentRelation relation, double sharedLength)
        {
            First = first; Second = second; Relation = relation; SharedLength = sharedLength;
        }

        /// <summary>Index of the earlier segment in the input.</summary>
        public int First { get; }
        public int Second { get; }
        public SegmentRelation Relation { get; }
        public double SharedLength { get; }
    }

    /// <summary>Finds duplicate and overlapping segments using a sweep over bounding boxes.</summary>
    public static class SegmentAnalysis
    {
        /// <summary>Returns every duplicate or overlapping pair, ordered by (First, Second).</summary>
        public static List<SegmentPair> FindDuplicatesAndOverlaps(IReadOnlyList<Segment2> segments, double tolerance)
        {
            if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
            int n = segments.Count;
            var order = new int[n];
            var minX = new double[n];
            var maxX = new double[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                minX[i] = Math.Min(segments[i].A.X, segments[i].B.X);
                maxX[i] = Math.Max(segments[i].A.X, segments[i].B.X);
            }
            Array.Sort(order, (a, b) => minX[a] != minX[b] ? minX[a].CompareTo(minX[b]) : a.CompareTo(b));

            var result = new List<SegmentPair>();
            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                var si = segments[i];
                double iMinY = Math.Min(si.A.Y, si.B.Y), iMaxY = Math.Max(si.A.Y, si.B.Y);
                for (int oj = oi + 1; oj < n; oj++)
                {
                    int j = order[oj];
                    if (minX[j] > maxX[i] + tolerance) break;
                    var sj = segments[j];
                    if (Math.Min(sj.A.Y, sj.B.Y) > iMaxY + tolerance || Math.Max(sj.A.Y, sj.B.Y) < iMinY - tolerance) continue;

                    int first = Math.Min(i, j), second = Math.Max(i, j);
                    var a = segments[first];
                    var b = segments[second];
                    if (IsDuplicate(a, b, tolerance))
                    {
                        result.Add(new SegmentPair(first, second, SegmentRelation.Duplicate, Math.Min(a.Length, b.Length)));
                        continue;
                    }
                    double shared = SharedLength(a, b, tolerance);
                    if (shared > tolerance) result.Add(new SegmentPair(first, second, SegmentRelation.Overlap, shared));
                }
            }
            result.Sort((x, y) => x.First != y.First ? x.First.CompareTo(y.First) : x.Second.CompareTo(y.Second));
            return result;
        }

        public static bool IsDuplicate(Segment2 a, Segment2 b, double tolerance) =>
            (a.A.DistanceTo(b.A) <= tolerance && a.B.DistanceTo(b.B) <= tolerance) ||
            (a.A.DistanceTo(b.B) <= tolerance && a.B.DistanceTo(b.A) <= tolerance);

        /// <summary>Length of the collinear shared part of two segments, 0 when they are not collinear.</summary>
        public static double SharedLength(Segment2 a, Segment2 b, double tolerance)
        {
            if (a.Length <= tolerance || b.Length <= tolerance) return 0;
            if (!a.IsCollinearWith(b, tolerance) || !b.IsCollinearWith(a, tolerance)) return 0;
            var d = a.Vector / a.Length;
            double s0 = (b.A - a.A).Dot(d), s1 = (b.B - a.A).Dot(d);
            double lo = Math.Max(0, Math.Min(s0, s1)), hi = Math.Min(a.Length, Math.Max(s0, s1));
            return Math.Max(0, hi - lo);
        }
    }
}
