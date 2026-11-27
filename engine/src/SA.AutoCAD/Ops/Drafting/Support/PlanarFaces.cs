using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Geometry;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>
    /// Finds the smallest closed region around a point in a set of segments (what BOUNDARY does, in pure geometry):
    /// segments are split where they cross, endpoints within the tolerance are merged, dangling edges are pruned, then
    /// the face is traced from the first edge a ray from the point towards +X hits, always taking the first edge
    /// clockwise from the reverse of the arriving edge (so the face stays on the left). A traced loop that does not
    /// contain the point (an island such as a column) is skipped and the next hit along the ray is tried.
    /// Islands inside the found face are not subtracted.
    /// </summary>
    internal static class PlanarFaces
    {
        internal static List<Point2>? Enclosing(IReadOnlyList<Segment2> input, Point2 p, double tol, out string reason)
        {
            reason = "";
            var segs = Split(input, tol);
            var nodes = new List<Point2>();
            var index = new Dictionary<long, List<int>>();
            int Node(Point2 q)
            {
                long kx = (long)Math.Floor(q.X / (tol * 4)), ky = (long)Math.Floor(q.Y / (tol * 4));
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        if (index.TryGetValue(Key(kx + dx, ky + dy), out var cell))
                            foreach (int i in cell) if (nodes[i].DistanceTo(q) <= tol) return i;
                nodes.Add(q);
                long k = Key(kx, ky);
                if (!index.TryGetValue(k, out var list)) index[k] = list = new List<int>();
                list.Add(nodes.Count - 1);
                return nodes.Count - 1;
            }
            var adj = new Dictionary<int, HashSet<int>>();
            foreach (var s in segs)
            {
                int a = Node(s.A), b = Node(s.B);
                if (a == b) continue;
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new HashSet<int>();
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new HashSet<int>();
                la.Add(b); lb.Add(a);
            }
            // Prune dangling edges: they cannot bound a face.
            var queue = new Queue<int>(adj.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key));
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                if (!adj.TryGetValue(v, out var nb) || nb.Count != 1) continue;
                int u = nb.First();
                nb.Clear();
                adj[u].Remove(v);
                if (adj[u].Count == 1) queue.Enqueue(u);
            }

            // Edges hit by the ray p + t (1, 0), t > 0, nearest first.
            var hits = new List<KeyValuePair<double, KeyValuePair<int, int>>>();
            foreach (var kv in adj)
                foreach (int b in kv.Value)
                {
                    int a = kv.Key;
                    if (a > b) continue;
                    var A = nodes[a];
                    var B = nodes[b];
                    if ((A.Y > p.Y) == (B.Y > p.Y)) continue;
                    double x = A.X + (p.Y - A.Y) * (B.X - A.X) / (B.Y - A.Y);
                    if (x > p.X + tol) hits.Add(new KeyValuePair<double, KeyValuePair<int, int>>(x - p.X, new KeyValuePair<int, int>(a, b)));
                }
            if (hits.Count == 0) { reason = "no line lies to the right of the point; the point is not enclosed"; return null; }
            foreach (var h in hits.OrderBy(h => h.Key))
            {
                int a = h.Value.Key, b = h.Value.Value;
                // Orient the first edge so the point is on its left.
                if ((nodes[b] - nodes[a]).Cross(p - nodes[a]) < 0) { int t = a; a = b; b = t; }
                var loop = Trace(nodes, adj, a, b);
                if (loop is null || loop.Count < 3) continue;
                var poly = new Polygon2(loop);
                if (poly.SignedArea <= 0) continue;
                if (poly.Locate(p, tol) != PointLocation.Inside) continue;
                return Simplify(loop, tol);
            }
            reason = "the lines around the point do not close into a region (look for gaps larger than the tolerance)";
            return null;
        }

        private static long Key(long x, long y) => unchecked(x * 73856093L ^ y * 19349663L);

        private static List<Point2>? Trace(List<Point2> nodes, Dictionary<int, HashSet<int>> adj, int a0, int b0)
        {
            var loop = new List<Point2> { nodes[a0] };
            int a = a0, b = b0;
            int guard = 0;
            while (guard++ < 100000)
            {
                if (b == a0 && loop.Count > 1)
                {
                    return loop;
                }
                loop.Add(nodes[b]);
                var back = nodes[a] - nodes[b];
                double backAngle = Math.Atan2(back.Y, back.X);
                int best = -1;
                double bestTurn = double.MaxValue;
                foreach (int c in adj[b])
                {
                    var d = nodes[c] - nodes[b];
                    double ang = Math.Atan2(d.Y, d.X);
                    double turn = backAngle - ang; // clockwise angle from the reverse edge
                    while (turn <= 1e-12) turn += 2 * Math.PI;
                    while (turn > 2 * Math.PI) turn -= 2 * Math.PI;
                    if (c == a && adj[b].Count > 1) turn = 2 * Math.PI; // going back only at a dead end
                    if (turn < bestTurn) { bestTurn = turn; best = c; }
                }
                if (best < 0) return null;
                a = b;
                b = best;
                if (a == a0 && b == b0) return loop.Take(loop.Count - 1).ToList();
            }
            return null;
        }

        private static List<Segment2> Split(IReadOnlyList<Segment2> input, double tol)
        {
            var cuts = new List<List<double>>();
            foreach (var _ in input) cuts.Add(new List<double> { 0, 1 });
            for (int i = 0; i < input.Count; i++)
            {
                var si = input[i];
                double minX = Math.Min(si.A.X, si.B.X) - tol, maxX = Math.Max(si.A.X, si.B.X) + tol;
                double minY = Math.Min(si.A.Y, si.B.Y) - tol, maxY = Math.Max(si.A.Y, si.B.Y) + tol;
                for (int j = i + 1; j < input.Count; j++)
                {
                    var sj = input[j];
                    if (Math.Max(sj.A.X, sj.B.X) < minX || Math.Min(sj.A.X, sj.B.X) > maxX || Math.Max(sj.A.Y, sj.B.Y) < minY || Math.Min(sj.A.Y, sj.B.Y) > maxY) continue;
                    var x = si.Intersect(sj, tol);
                    if (x.Kind == IntersectionKind.Point) { cuts[i].Add(x.T); cuts[j].Add(x.U); }
                    else if (x.Kind == IntersectionKind.Overlap)
                    {
                        cuts[i].Add(Clamp01(si.Project(sj.A))); cuts[i].Add(Clamp01(si.Project(sj.B)));
                        cuts[j].Add(Clamp01(sj.Project(si.A))); cuts[j].Add(Clamp01(sj.Project(si.B)));
                    }
                    // A T-junction within the tolerance: an endpoint of one segment on the other.
                    foreach (var q in new[] { sj.A, sj.B }) if (si.DistanceTo(q) <= tol) cuts[i].Add(Clamp01(si.Project(q)));
                    foreach (var q in new[] { si.A, si.B }) if (sj.DistanceTo(q) <= tol) cuts[j].Add(Clamp01(sj.Project(q)));
                }
            }
            var result = new List<Segment2>();
            for (int i = 0; i < input.Count; i++)
            {
                var ts = cuts[i].Distinct().OrderBy(t => t).ToList();
                for (int k = 1; k < ts.Count; k++)
                {
                    var a = input[i].PointAt(ts[k - 1]);
                    var b = input[i].PointAt(ts[k]);
                    if (a.DistanceTo(b) > tol) result.Add(new Segment2(a, b));
                }
            }
            return result;
        }

        private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t;

        /// <summary>Drops repeated and collinear vertices.</summary>
        internal static List<Point2> Simplify(List<Point2> pts, double tol)
        {
            var list = new List<Point2>();
            foreach (var q in pts) if (list.Count == 0 || list[list.Count - 1].DistanceTo(q) > tol) list.Add(q);
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tol) list.RemoveAt(list.Count - 1);
            bool changed = true;
            while (changed && list.Count > 3)
            {
                changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var prev = list[(i - 1 + list.Count) % list.Count];
                    var cur = list[i];
                    var next = list[(i + 1) % list.Count];
                    if (new Segment2(prev, next).DistanceTo(cur) <= tol) { list.RemoveAt(i); changed = true; break; }
                }
            }
            return list;
        }
    }
}
