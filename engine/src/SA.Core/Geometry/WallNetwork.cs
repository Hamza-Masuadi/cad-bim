using System;
using System.Collections.Generic;
using System.Linq;

namespace SA.Core.Geometry
{
    /// <summary>One wall centreline. Thickness is the full wall thickness, centred on the line.</summary>
    public sealed class WallInput
    {
        public WallInput(string id, Point2 start, Point2 end, double thickness)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Start = start;
            End = end;
            Thickness = thickness;
        }

        public string Id { get; }
        public Point2 Start { get; }
        public Point2 End { get; }
        public double Thickness { get; }
    }

    public enum CornerStyle
    {
        /// <summary>Two-wall corners meet on the line joining the inner and outer face intersections.</summary>
        Mitre,
        /// <summary>The thicker wall (first in input order on a tie) runs to the other's outer face; the other stops at its inner face.</summary>
        Butt,
    }

    public sealed class WallNetworkOptions
    {
        /// <summary>Endpoints closer than this are joined; an endpoint this close to another wall forms a T. Same units as the input.</summary>
        public double Tolerance { get; set; } = 1.0;

        public CornerStyle CornerStyle { get; set; } = CornerStyle.Mitre;

        /// <summary>An outer mitre point further from the junction than this many half-thicknesses is bevelled.</summary>
        public double MitreLimit { get; set; } = 4.0;

        /// <summary>Directions within this angle (radians) count as parallel or collinear.</summary>
        public double AngleTolerance { get; set; } = 1e-3;
    }

    public enum JunctionKind
    {
        /// <summary>Two walls meeting at an angle.</summary>
        Corner,
        /// <summary>A wall running through, with one or more walls meeting it from one side.</summary>
        Tee,
        /// <summary>Two walls crossing, each running straight through.</summary>
        Cross,
        /// <summary>Two collinear walls end to end.</summary>
        Inline,
        /// <summary>Any other meeting of three or more walls.</summary>
        Other,
    }

    public sealed class Junction
    {
        internal Junction(Point2 point, JunctionKind kind, IReadOnlyList<string> wallIds)
        {
            Point = point; Kind = kind; WallIds = wallIds;
        }

        public Point2 Point { get; }
        public JunctionKind Kind { get; }
        /// <summary>Walls meeting here, in input order, each listed once.</summary>
        public IReadOnlyList<string> WallIds { get; }
    }

    public sealed class OpenEnd
    {
        internal OpenEnd(string wallId, Point2 point, bool atStart)
        {
            WallId = wallId; Point = point; AtStart = atStart;
        }

        public string WallId { get; }
        public Point2 Point { get; }
        /// <summary>True for the wall's start point, false for its end point.</summary>
        public bool AtStart { get; }
    }

    public sealed class WallOutline
    {
        internal WallOutline(string id, double thickness, Segment2 centreline)
        {
            Id = id; Thickness = thickness; Centreline = centreline;
        }

        public string Id { get; }
        public double Thickness { get; }
        public Segment2 Centreline { get; }

        /// <summary>
        /// Closed counter-clockwise regions covered by this wall, one per piece between junctions. Together the pieces
        /// of all walls tile the walls without overlap; use them for hatching.
        /// </summary>
        public List<Polygon2> Pieces { get; } = new List<Polygon2>();

        /// <summary>The cleaned double-line outline owned by this wall: faces and open-end caps, with junction lines removed.</summary>
        public List<Segment2> Segments { get; } = new List<Segment2>();
    }

    public sealed class NetworkIssue
    {
        internal NetworkIssue(string code, string message, IReadOnlyList<string> wallIds)
        {
            Code = code; Message = message; WallIds = wallIds;
        }

        /// <summary>One of: invalid_thickness, zero_length, duplicate_id, duplicate, overlap, folded_junction.</summary>
        public string Code { get; }
        public string Message { get; }
        public IReadOnlyList<string> WallIds { get; }
    }

    public sealed class WallNetworkResult
    {
        internal WallNetworkResult() { }

        /// <summary>Outlines for walls that were processed, in input order. Walls skipped for an issue are absent.</summary>
        public List<WallOutline> Walls { get; } = new List<WallOutline>();
        public List<Junction> Junctions { get; } = new List<Junction>();
        public List<OpenEnd> OpenEnds { get; } = new List<OpenEnd>();
        public List<NetworkIssue> Issues { get; } = new List<NetworkIssue>();

        /// <summary>All outline segments chained into polylines. A room enclosed by walls gives two closed loops (inner and outer faces).</summary>
        public List<Polyline2> Loops { get; } = new List<Polyline2>();

        public WallOutline? Find(string id) => Walls.FirstOrDefault(w => w.Id == id);

        public IEnumerable<Segment2> AllSegments() => Walls.SelectMany(w => w.Segments);
    }

    /// <summary>
    /// Turns wall centrelines into cleaned double-line outlines. Endpoints are snapped, walls are split where another
    /// wall ends on them (T) or crosses them (X), and every junction is resolved so no line is drawn across a joint.
    /// </summary>
    public static class WallNetwork
    {
        private sealed class EndGeom
        {
            public readonly List<Point2> Left = new List<Point2>();
            public readonly List<Point2> Extras = new List<Point2>();
            public readonly List<Point2> Right = new List<Point2>();

            public IEnumerable<Point2> Path() => Left.Concat(Extras).Concat(Right);
        }

        private sealed class Piece
        {
            public int Wall;
            public int NodeA;
            public int NodeB;
            public readonly EndGeom EndA = new EndGeom();
            public readonly EndGeom EndB = new EndGeom();
            public bool IsFirstOfWall;
            public bool IsLastOfWall;
        }

        private sealed class Inc
        {
            public int Piece;
            public bool AtA;
            public Point2 Dir;
            public double Half;
            public int Wall;
            public double Angle;
            public EndGeom End = null!;
        }

        private struct Edge
        {
            public Segment2 Seg;
            public int Piece;
            public int Wall;
            public double MinX, MaxX, MinY, MaxY;
        }

        public static WallNetworkResult Build(IReadOnlyList<WallInput> walls, WallNetworkOptions? options = null)
        {
            if (walls is null) throw new ArgumentNullException(nameof(walls));
            var opt = options ?? new WallNetworkOptions();
            if (!(opt.Tolerance > 0)) throw new ArgumentOutOfRangeException(nameof(options), "Tolerance must be positive.");
            if (!(opt.MitreLimit >= 1)) throw new ArgumentOutOfRangeException(nameof(options), "MitreLimit must be at least 1.");
            double tol = opt.Tolerance;
            double eps = Math.Max(tol * 1e-3, 1e-9);
            double sinTol = Math.Sin(opt.AngleTolerance);
            var result = new WallNetworkResult();

            // 1. Accept walls.
            var accepted = new List<WallInput>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in walls)
            {
                if (!ids.Add(w.Id))
                {
                    result.Issues.Add(new NetworkIssue("duplicate_id", "Wall id '" + w.Id + "' is used more than once; the later wall was skipped.", new[] { w.Id }));
                    continue;
                }
                if (!(w.Thickness > 0) || double.IsInfinity(w.Thickness))
                {
                    result.Issues.Add(new NetworkIssue("invalid_thickness", "Wall '" + w.Id + "' has thickness " + w.Thickness + "; it was skipped.", new[] { w.Id }));
                    continue;
                }
                if (w.Start.DistanceTo(w.End) <= tol)
                {
                    result.Issues.Add(new NetworkIssue("zero_length", "Wall '" + w.Id + "' is shorter than the tolerance; it was skipped.", new[] { w.Id }));
                    continue;
                }
                bool skip = false;
                var seg = new Segment2(w.Start, w.End);
                foreach (var prev in accepted)
                {
                    var ps = new Segment2(prev.Start, prev.End);
                    if (SegmentAnalysis.IsDuplicate(ps, seg, tol))
                    {
                        result.Issues.Add(new NetworkIssue("duplicate", "Wall '" + w.Id + "' duplicates wall '" + prev.Id + "'; it was skipped.", new[] { prev.Id, w.Id }));
                        skip = true;
                        break;
                    }
                    if (SegmentAnalysis.SharedLength(ps, seg, tol) > tol)
                    {
                        result.Issues.Add(new NetworkIssue("overlap", "Wall '" + w.Id + "' overlaps wall '" + prev.Id + "' along its length; it was skipped.", new[] { prev.Id, w.Id }));
                        skip = true;
                        break;
                    }
                }
                if (!skip) accepted.Add(w);
            }

            // 2. Snap endpoints into nodes.
            var nodes = new List<Point2>();
            int NodeFor(Point2 p)
            {
                for (int i = 0; i < nodes.Count; i++)
                    if (nodes[i].DistanceTo(p) <= tol) return i;
                nodes.Add(p);
                return nodes.Count - 1;
            }
            int wc = accepted.Count;
            var startNode = new int[wc];
            var endNode = new int[wc];
            for (int i = 0; i < wc; i++)
            {
                startNode[i] = NodeFor(accepted[i].Start);
                endNode[i] = NodeFor(accepted[i].End);
            }

            // 3. T junctions: a node lying on another wall's interior splits that wall; the node moves onto the wall.
            var splits = new List<List<KeyValuePair<double, int>>>();
            for (int i = 0; i < wc; i++) splits.Add(new List<KeyValuePair<double, int>>());
            Segment2 WallSeg(int i) => new Segment2(nodes[startNode[i]], nodes[endNode[i]]);

            for (int n = 0; n < nodes.Count; n++)
            {
                for (int i = 0; i < wc; i++)
                {
                    if (startNode[i] == n || endNode[i] == n) continue;
                    var s = WallSeg(i);
                    double len = s.Length;
                    double t = s.Project(nodes[n]);
                    double tTol = tol / len;
                    if (t <= tTol || t >= 1 - tTol) continue;
                    var foot = s.PointAt(t);
                    if (foot.DistanceTo(nodes[n]) > tol) continue;
                    if (splits[i].Any(kv => kv.Value == n)) continue;
                    nodes[n] = foot;
                    splits[i].Add(new KeyValuePair<double, int>(t, n));
                }
            }

            // 4. X crossings: interiors crossing each other split both walls at a new node.
            for (int i = 0; i < wc; i++)
            {
                for (int j = i + 1; j < wc; j++)
                {
                    if (startNode[i] == startNode[j] || startNode[i] == endNode[j] || endNode[i] == startNode[j] || endNode[i] == endNode[j]) continue;
                    var a = WallSeg(i);
                    var b = WallSeg(j);
                    var x = a.Intersect(b, tol);
                    if (x.Kind != IntersectionKind.Point) continue;
                    double ta = a.Project(x.Point), tb = b.Project(x.Point);
                    double tolA = tol / a.Length, tolB = tol / b.Length;
                    if (ta <= tolA || ta >= 1 - tolA || tb <= tolB || tb >= 1 - tolB) continue;
                    bool known = splits[i].Any(kv => nodes[kv.Value].DistanceTo(x.Point) <= tol) &&
                                 splits[j].Any(kv => nodes[kv.Value].DistanceTo(x.Point) <= tol);
                    if (known) continue;
                    int n = NodeFor(x.Point);
                    if (!splits[i].Any(kv => kv.Value == n)) splits[i].Add(new KeyValuePair<double, int>(ta, n));
                    if (!splits[j].Any(kv => kv.Value == n)) splits[j].Add(new KeyValuePair<double, int>(tb, n));
                }
            }

            // 5. Pieces between consecutive nodes along each wall.
            var pieces = new List<Piece>();
            var piecePairs = new HashSet<long>();
            var wallPieces = new List<List<int>>();
            for (int i = 0; i < wc; i++)
            {
                wallPieces.Add(new List<int>());
                var seq = new List<int> { startNode[i] };
                foreach (var kv in splits[i].OrderBy(k => k.Key)) if (seq[seq.Count - 1] != kv.Value) seq.Add(kv.Value);
                if (seq[seq.Count - 1] != endNode[i]) seq.Add(endNode[i]);
                if (seq.Count < 2)
                {
                    result.Issues.Add(new NetworkIssue("zero_length", "Wall '" + accepted[i].Id + "' collapsed to a point after snapping; it was skipped.", new[] { accepted[i].Id }));
                    continue;
                }
                for (int k = 0; k + 1 < seq.Count; k++)
                {
                    int na = seq[k], nb = seq[k + 1];
                    long key = Math.Min(na, nb) * 1000003L + Math.Max(na, nb);
                    if (nodes[na].DistanceTo(nodes[nb]) <= eps || !piecePairs.Add(key)) continue;
                    var p = new Piece { Wall = i, NodeA = na, NodeB = nb, IsFirstOfWall = k == 0, IsLastOfWall = k + 2 == seq.Count };
                    wallPieces[i].Add(pieces.Count);
                    pieces.Add(p);
                }
            }

            // 6. Resolve every node.
            var incidence = new List<List<Inc>>();
            for (int n = 0; n < nodes.Count; n++) incidence.Add(new List<Inc>());
            for (int pi = 0; pi < pieces.Count; pi++)
            {
                var p = pieces[pi];
                double half = accepted[p.Wall].Thickness / 2;
                var d = (nodes[p.NodeB] - nodes[p.NodeA]).Normalised();
                incidence[p.NodeA].Add(new Inc { Piece = pi, AtA = true, Dir = d, Half = half, Wall = p.Wall, Angle = Norm(Math.Atan2(d.Y, d.X)), End = p.EndA });
                incidence[p.NodeB].Add(new Inc { Piece = pi, AtA = false, Dir = -d, Half = half, Wall = p.Wall, Angle = Norm(Math.Atan2(-d.Y, -d.X)), End = p.EndB });
            }

            for (int n = 0; n < nodes.Count; n++)
            {
                var inc = incidence[n];
                if (inc.Count == 0) continue;
                inc.Sort((a, b) => a.Angle != b.Angle ? a.Angle.CompareTo(b.Angle) : a.Wall.CompareTo(b.Wall));
                var node = nodes[n];

                if (inc.Count == 1)
                {
                    var e = inc[0];
                    e.End.Left.Add(LeftPoint(node, e));
                    e.End.Right.Add(RightPoint(node, e));
                    var p = pieces[e.Piece];
                    // Pieces run from the wall's start towards its end, so only the first piece's A end is the wall start.
                    result.OpenEnds.Add(new OpenEnd(accepted[e.Wall].Id, node, e.AtA && p.IsFirstOfWall));
                    continue;
                }

                var through = FindThroughPairs(inc, sinTol, tol);
                JunctionKind kind;
                if (inc.Count == 2)
                {
                    bool inline = IsOpposite(inc[0], inc[1], sinTol);
                    if (inline)
                    {
                        foreach (var e in inc) { e.End.Left.Add(LeftPoint(node, e)); e.End.Right.Add(RightPoint(node, e)); }
                        kind = JunctionKind.Inline;
                    }
                    else
                    {
                        bool done = opt.CornerStyle == CornerStyle.Butt && ButtCorner(node, inc[0], inc[1], opt.MitreLimit);
                        if (!done) GeneralNode(node, inc, false, sinTol, opt.MitreLimit, result, accepted);
                        kind = JunctionKind.Corner;
                    }
                }
                else if (through.Count > 0)
                {
                    var t1 = through[0].Key;
                    var t2 = through[0].Value;
                    foreach (var e in new[] { t1, t2 }) { e.End.Left.Add(LeftPoint(node, e)); e.End.Right.Add(RightPoint(node, e)); }
                    var stems = inc.Where(e => e != t1 && e != t2).ToList();
                    TrimStems(node, t1, stems, sinTol, eps);
                    kind = inc.Count == 4 && through.Count >= 2 ? JunctionKind.Cross : inc.Count == 3 ? JunctionKind.Tee : JunctionKind.Other;
                    if (kind == JunctionKind.Other && stems.All(s => Side(t1, s) == Side(t1, stems[0]))) kind = JunctionKind.Tee;
                }
                else
                {
                    GeneralNode(node, inc, true, sinTol, opt.MitreLimit, result, accepted);
                    kind = JunctionKind.Other;
                }

                var wallIds = inc.Select(e => e.Wall).Distinct().OrderBy(w => w).Select(w => accepted[w].Id).ToList();
                result.Junctions.Add(new Junction(node, kind, wallIds));
            }

            // 7. Piece polygons.
            var outlines = new WallOutline[wc];
            var piecePolys = new List<Point2>[pieces.Count];
            for (int i = 0; i < wc; i++)
            {
                if (wallPieces[i].Count == 0) continue;
                outlines[i] = new WallOutline(accepted[i].Id, accepted[i].Thickness, new Segment2(accepted[i].Start, accepted[i].End));
            }
            for (int pi = 0; pi < pieces.Count; pi++)
            {
                var p = pieces[pi];
                var pts = new List<Point2>();
                foreach (var q in p.EndB.Path().Concat(p.EndA.Path()))
                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(q) > eps) pts.Add(q);
                while (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= eps) pts.RemoveAt(pts.Count - 1);
                piecePolys[pi] = pts;
                if (pts.Count >= 3)
                {
                    var poly = new Polygon2(pts);
                    outlines[p.Wall].Pieces.Add(poly.SignedArea < 0 ? poly.Reversed() : poly);
                }
            }

            // 8. Visible outline: polygon edges minus every part shared with another piece.
            var edges = new List<Edge>();
            for (int pi = 0; pi < pieces.Count; pi++)
            {
                var pts = piecePolys[pi];
                if (pts.Count < 2) continue;
                for (int k = 0; k < pts.Count; k++)
                {
                    var s = new Segment2(pts[k], pts[(k + 1) % pts.Count]);
                    if (s.Length <= eps) continue;
                    edges.Add(new Edge
                    {
                        Seg = s, Piece = pi, Wall = pieces[pi].Wall,
                        MinX = Math.Min(s.A.X, s.B.X), MaxX = Math.Max(s.A.X, s.B.X),
                        MinY = Math.Min(s.A.Y, s.B.Y), MaxY = Math.Max(s.A.Y, s.B.Y),
                    });
                }
            }
            var visible = new List<Segment2>[wc];
            for (int i = 0; i < wc; i++) visible[i] = new List<Segment2>();
            for (int ei = 0; ei < edges.Count; ei++)
            {
                var e = edges[ei];
                double len = e.Seg.Length;
                var u = e.Seg.Vector / len;
                var cut = new List<KeyValuePair<double, double>>();
                for (int fj = 0; fj < edges.Count; fj++)
                {
                    var f = edges[fj];
                    if (f.Piece == e.Piece) continue;
                    if (f.MinX > e.MaxX + eps || f.MaxX < e.MinX - eps || f.MinY > e.MaxY + eps || f.MaxY < e.MinY - eps) continue;
                    if (e.Seg.LineDistanceTo(f.Seg.A) > eps || e.Seg.LineDistanceTo(f.Seg.B) > eps) continue;
                    double s0 = (f.Seg.A - e.Seg.A).Dot(u), s1 = (f.Seg.B - e.Seg.A).Dot(u);
                    double lo = Math.Max(0, Math.Min(s0, s1)), hi = Math.Min(len, Math.Max(s0, s1));
                    if (hi - lo > eps) cut.Add(new KeyValuePair<double, double>(lo, hi));
                }
                foreach (var keep in Subtract(len, cut, eps))
                    visible[e.Wall].Add(new Segment2(e.Seg.A + u * keep.Key, e.Seg.A + u * keep.Value));
            }

            for (int i = 0; i < wc; i++)
            {
                if (outlines[i] is null) continue;
                outlines[i].Segments.AddRange(MergeCollinear(visible[i], eps, sinTol));
                result.Walls.Add(outlines[i]);
            }

            result.Loops.AddRange(Chain(result.AllSegments().ToList(), eps * 10));
            return result;
        }

        private static double Norm(double a)
        {
            double twoPi = 2 * Math.PI;
            a %= twoPi;
            if (a < 0) a += twoPi;
            return a;
        }

        private static Point2 LeftPoint(Point2 node, Inc e) => node + e.Dir.PerpLeft * e.Half;
        private static Point2 RightPoint(Point2 node, Inc e) => node - e.Dir.PerpLeft * e.Half;

        private static bool IsOpposite(Inc a, Inc b, double sinTol) => a.Dir.Dot(b.Dir) < 0 && Math.Abs(a.Dir.Cross(b.Dir)) <= sinTol;

        /// <summary>Pairs of collinear, opposite, equal-thickness edges, best first: thickest, then same wall, then input order.</summary>
        private static List<KeyValuePair<Inc, Inc>> FindThroughPairs(List<Inc> inc, double sinTol, double tol)
        {
            var pairs = new List<KeyValuePair<Inc, Inc>>();
            for (int i = 0; i < inc.Count; i++)
                for (int j = i + 1; j < inc.Count; j++)
                    if (IsOpposite(inc[i], inc[j], sinTol) && Math.Abs(inc[i].Half - inc[j].Half) * 2 <= tol)
                        pairs.Add(new KeyValuePair<Inc, Inc>(inc[i], inc[j]));
            return pairs
                .OrderByDescending(p => p.Key.Half)
                .ThenBy(p => p.Key.Wall == p.Value.Wall ? 0 : 1)
                .ThenBy(p => Math.Min(p.Key.Wall, p.Value.Wall))
                .ThenBy(p => Math.Max(p.Key.Wall, p.Value.Wall))
                .ToList();
        }

        /// <summary>+1 when the stem leaves on the counter-clockwise side of the through edge's direction, -1 otherwise.</summary>
        private static int Side(Inc through, Inc stem) => through.Dir.Cross(stem.Dir) > 0 ? 1 : -1;

        private static void TrimStems(Point2 node, Inc through, List<Inc> stems, double sinTol, double eps)
        {
            double h = through.Half;
            var normal = through.Dir.PerpLeft;
            foreach (int side in new[] { 1, -1 })
            {
                var n = normal * side;
                var facePoint = node + n * h;
                var group = stems.Where(s => Side(through, s) == side && Math.Abs(through.Dir.Cross(s.Dir)) > sinTol)
                                 .OrderBy(s => Norm(Math.Atan2(s.Dir.Y, s.Dir.X) - Math.Atan2(through.Dir.Y, through.Dir.X)))
                                 .ToList();
                var leftSet = new HashSet<Inc>();
                var rightSet = new HashSet<Inc>();
                for (int k = 0; k + 1 < group.Count; k++)
                {
                    var sa = group[k];
                    var sb = group[k + 1];
                    if (!Lines.Intersect(LeftPoint(node, sa), sa.Dir, RightPoint(node, sb), sb.Dir, out var g)) continue;
                    if ((g - node).Dot(n) > h + eps)
                    {
                        sa.End.Left.Add(g); sa.End.Left.Add(facePoint); leftSet.Add(sa);
                        sb.End.Right.Add(facePoint); sb.End.Right.Add(g); rightSet.Add(sb);
                    }
                }
                foreach (var s in group)
                {
                    if (!leftSet.Contains(s))
                        s.End.Left.Add(Lines.Intersect(LeftPoint(node, s), s.Dir, facePoint, through.Dir, out var pl) ? pl : LeftPoint(node, s));
                    if (!rightSet.Contains(s))
                        s.End.Right.Add(Lines.Intersect(RightPoint(node, s), s.Dir, facePoint, through.Dir, out var pr) ? pr : RightPoint(node, s));
                }
            }
            // A stem collinear with the through wall cannot be trimmed against it; end it square.
            foreach (var s in stems.Where(s => Math.Abs(through.Dir.Cross(s.Dir)) <= sinTol))
            {
                s.End.Left.Add(LeftPoint(node, s));
                s.End.Right.Add(RightPoint(node, s));
            }
        }

        /// <summary>Two walls at an angle, butt-jointed. Returns false (caller mitres) when the geometry is too acute.</summary>
        private static bool ButtCorner(Point2 node, Inc a, Inc b, double mitreLimit)
        {
            Inc p = a.Half > b.Half || (a.Half == b.Half && a.Wall <= b.Wall) ? a : b;
            Inc s = p == a ? b : a;

            bool innerIsLeft = p.Dir.PerpLeft.Dot(s.Dir) > 0;
            Point2 pInnerPoint = innerIsLeft ? LeftPoint(node, p) : RightPoint(node, p);
            bool sBackIsLeft = s.Dir.PerpLeft.Dot(p.Dir) < 0;
            Point2 sBackPoint = sBackIsLeft ? LeftPoint(node, s) : RightPoint(node, s);

            if (!Lines.Intersect(LeftPoint(node, p), p.Dir, sBackPoint, s.Dir, out var pl)) return false;
            if (!Lines.Intersect(RightPoint(node, p), p.Dir, sBackPoint, s.Dir, out var pr)) return false;
            if (!Lines.Intersect(LeftPoint(node, s), s.Dir, pInnerPoint, p.Dir, out var sl)) return false;
            if (!Lines.Intersect(RightPoint(node, s), s.Dir, pInnerPoint, p.Dir, out var sr)) return false;
            double limit = mitreLimit * Math.Max(p.Half, s.Half);
            foreach (var q in new[] { pl, pr, sl, sr }) if (q.DistanceTo(node) > limit) return false;

            p.End.Left.Add(pl); p.End.Right.Add(pr);
            s.End.Left.Add(sl); s.End.Right.Add(sr);
            return true;
        }

        /// <summary>
        /// Corner points from the gaps between consecutive edges (counter-clockwise). With <paramref name="insertNode"/>
        /// every end also passes through the node, which closes the region between three or more walls.
        /// </summary>
        private static void GeneralNode(Point2 node, List<Inc> sorted, bool insertNode, double sinTol, double mitreLimit,
            WallNetworkResult result, List<WallInput> walls)
        {
            int n = sorted.Count;
            for (int i = 0; i < n; i++)
            {
                var a = sorted[i];
                var b = sorted[(i + 1) % n];
                double gap = Norm(b.Angle - a.Angle);
                if (n == 1 || gap == 0) gap = 2 * Math.PI;

                bool parallel = Math.Abs(Math.Sin(gap)) <= sinTol;
                if (parallel)
                {
                    if (Math.Abs(gap - Math.PI) > Math.PI / 2)
                    {
                        result.Issues.Add(new NetworkIssue("folded_junction",
                            "Walls '" + walls[a.Wall].Id + "' and '" + walls[b.Wall].Id + "' leave a junction in the same direction.",
                            new[] { walls[a.Wall].Id, walls[b.Wall].Id }));
                    }
                    a.End.Left.Add(LeftPoint(node, a));
                    b.End.Right.Add(RightPoint(node, b));
                    continue;
                }

                if (!Lines.Intersect(LeftPoint(node, a), a.Dir, RightPoint(node, b), b.Dir, out var c))
                {
                    a.End.Left.Add(LeftPoint(node, a));
                    b.End.Right.Add(RightPoint(node, b));
                    continue;
                }

                double maxHalf = Math.Max(a.Half, b.Half);
                if (gap > Math.PI && c.DistanceTo(node) > mitreLimit * maxHalf)
                {
                    var bis = a.Dir.Rotate(gap / 2);
                    double dist = mitreLimit * maxHalf;
                    var mid = node + bis * dist;
                    var bevelDir = bis.PerpLeft;
                    var pa = Lines.Intersect(LeftPoint(node, a), a.Dir, mid, bevelDir, out var qa) ? qa : LeftPoint(node, a);
                    var pb = Lines.Intersect(RightPoint(node, b), b.Dir, mid, bevelDir, out var qb) ? qb : RightPoint(node, b);
                    a.End.Left.Add(pa); a.End.Left.Add(mid);
                    b.End.Right.Add(mid); b.End.Right.Add(pb);
                    continue;
                }

                a.End.Left.Add(c);
                b.End.Right.Add(c);
            }
            if (insertNode)
                foreach (var e in sorted) e.End.Extras.Add(node);
        }

        private static IEnumerable<KeyValuePair<double, double>> Subtract(double length, List<KeyValuePair<double, double>> cuts, double eps)
        {
            cuts.Sort((x, y) => x.Key.CompareTo(y.Key));
            double pos = 0;
            foreach (var c in cuts)
            {
                if (c.Key > pos + eps) yield return new KeyValuePair<double, double>(pos, c.Key);
                if (c.Value > pos) pos = c.Value;
            }
            if (length > pos + eps) yield return new KeyValuePair<double, double>(pos, length);
        }

        private static List<Segment2> MergeCollinear(List<Segment2> segs, double eps, double sinTol)
        {
            var list = new List<Segment2>(segs);
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < list.Count && !merged; i++)
                {
                    for (int j = i + 1; j < list.Count && !merged; j++)
                    {
                        var a = list[i];
                        var b = list[j];
                        var da = a.Vector / a.Length;
                        var db = b.Vector / b.Length;
                        if (Math.Abs(da.Cross(db)) > sinTol) continue;
                        if (a.LineDistanceTo(b.A) > eps || a.LineDistanceTo(b.B) > eps) continue;
                        // Join only segments that touch end to end.
                        Point2[] pa = { a.A, a.B };
                        Point2[] pb = { b.A, b.B };
                        for (int x = 0; x < 2 && !merged; x++)
                        {
                            for (int y = 0; y < 2 && !merged; y++)
                            {
                                if (pa[x].DistanceTo(pb[y]) > eps) continue;
                                var far1 = pa[1 - x];
                                var far2 = pb[1 - y];
                                // Must continue on, not fold back.
                                if ((far1 - pa[x]).Dot(far2 - pb[y]) >= 0) continue;
                                list[i] = new Segment2(far1, far2);
                                list.RemoveAt(j);
                                merged = true;
                            }
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>Chains segments sharing endpoints into polylines; open chains first, then closed loops.</summary>
        internal static List<Polyline2> Chain(List<Segment2> segs, double eps)
        {
            var points = new List<Point2>();
            int PointFor(Point2 p)
            {
                for (int i = 0; i < points.Count; i++) if (points[i].DistanceTo(p) <= eps) return i;
                points.Add(p);
                return points.Count - 1;
            }
            var ends = new List<int[]>();
            var adj = new List<List<int>>();
            foreach (var s in segs)
            {
                int a = PointFor(s.A), b = PointFor(s.B);
                while (adj.Count < points.Count) adj.Add(new List<int>());
                ends.Add(new[] { a, b });
                adj[a].Add(ends.Count - 1);
                adj[b].Add(ends.Count - 1);
            }
            var used = new bool[segs.Count];
            var chains = new List<Polyline2>();

            List<int> Walk(int startPoint)
            {
                var path = new List<int> { startPoint };
                int cur = startPoint;
                while (true)
                {
                    int next = -1;
                    foreach (int si in adj[cur]) if (!used[si]) { next = si; break; }
                    if (next < 0) break;
                    used[next] = true;
                    cur = ends[next][0] == cur ? ends[next][1] : ends[next][0];
                    path.Add(cur);
                }
                return path;
            }

            for (int p = 0; p < points.Count; p++)
            {
                if (adj[p].Count % 2 == 1 && adj[p].Any(si => !used[si]))
                {
                    var path = Walk(p);
                    chains.Add(new Polyline2(path.Select(i => points[i]), false));
                }
            }
            for (int si = 0; si < segs.Count; si++)
            {
                if (used[si]) continue;
                var path = Walk(ends[si][0]);
                bool closed = path.Count > 2 && path[0] == path[path.Count - 1];
                if (closed) path.RemoveAt(path.Count - 1);
                chains.Add(new Polyline2(path.Select(i => points[i]), closed));
            }
            return chains;
        }
    }
}
