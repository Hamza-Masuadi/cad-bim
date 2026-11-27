using System.Collections.Generic;
using System.Linq;
using SA.Core.Geometry;
using Xunit;

namespace SA.Core.Tests
{
    public class GeometryTests
    {
        private static Point2 P(double x, double y) => new Point2(x, y);

        [Fact]
        public void PolygonAreaSignedAndAbsolute()
        {
            var ccw = new Polygon2(new[] { P(0, 0), P(4, 0), P(4, 3), P(0, 3) });
            Assert.Equal(12, ccw.SignedArea);
            Assert.True(ccw.IsCounterClockwise);
            var cw = ccw.Reversed();
            Assert.Equal(-12, cw.SignedArea);
            Assert.Equal(12, cw.Area);
            Assert.Equal(14, ccw.Perimeter);
            Assert.Equal(P(2, 1.5), ccw.Centroid);
        }

        [Fact]
        public void ConcaveAreaAndPointInPolygon()
        {
            // L shape: 2x2 square with the top-right 1x1 removed.
            var l = new Polygon2(new[] { P(0, 0), P(2, 0), P(2, 1), P(1, 1), P(1, 2), P(0, 2) });
            Assert.Equal(3, l.Area);
            Assert.Equal(PointLocation.Inside, l.Locate(P(0.5, 1.5), 1e-9));
            Assert.Equal(PointLocation.Outside, l.Locate(P(1.5, 1.5), 1e-9));
            Assert.Equal(PointLocation.OnBoundary, l.Locate(P(1, 1.5), 1e-9));
            Assert.Equal(PointLocation.OnBoundary, l.Locate(P(2.0005, 0.5), 1e-3));
            Assert.True(l.IsSimple(1e-9));
        }

        [Fact]
        public void BowTieIsNotSimpleAndPolygonNeedsThreePoints()
        {
            var bow = new Polygon2(new[] { P(0, 0), P(2, 2), P(2, 0), P(0, 2) });
            Assert.False(bow.IsSimple(1e-9));
            Assert.Throws<System.ArgumentException>(() => new Polygon2(new[] { P(0, 0), P(1, 1) }));
        }

        [Fact]
        public void ClosureTest()
        {
            var open = new Polyline2(new[] { P(0, 0), P(10, 0), P(10, 10), P(0.0004, 0.0003) }, false);
            Assert.True(open.IsClosed(0.001));
            Assert.False(open.IsClosed(0.0001));
            var poly = open.ToPolygon(0.001)!;
            Assert.Equal(3, poly.Vertices.Count);
            Assert.Null(new Polyline2(new[] { P(0, 0), P(1, 0), P(1, 1) }, false).ToPolygon(0.001));
            Assert.Equal(40, new Polyline2(new[] { P(0, 0), P(10, 0), P(10, 10), P(0, 10) }, true).Length);
        }

        [Fact]
        public void SegmentIntersections()
        {
            var a = new Segment2(0, 0, 10, 0);
            var x = a.Intersect(new Segment2(5, -5, 5, 5), 1e-9);
            Assert.Equal(IntersectionKind.Point, x.Kind);
            Assert.Equal(P(5, 0), x.Point);
            Assert.Equal(0.5, x.T, 12);

            Assert.Equal(IntersectionKind.None, a.Intersect(new Segment2(5, 1, 5, 5), 1e-9).Kind);
            // Just short of the line, inside tolerance.
            Assert.Equal(IntersectionKind.Point, a.Intersect(new Segment2(5, 0.4, 5, 5), 0.5).Kind);

            var ov = a.Intersect(new Segment2(4, 0, 20, 0), 1e-9);
            Assert.Equal(IntersectionKind.Overlap, ov.Kind);
            Assert.Equal(P(4, 0), ov.Point);
            Assert.Equal(P(10, 0), ov.OverlapEnd);

            Assert.Equal(IntersectionKind.Point, a.Intersect(new Segment2(10, 0, 20, 0), 1e-9).Kind);
            Assert.Equal(IntersectionKind.None, a.Intersect(new Segment2(0, 1, 10, 1), 1e-9).Kind);
        }

        [Fact]
        public void DistancesAndProjection()
        {
            var s = new Segment2(0, 0, 10, 0);
            Assert.Equal(3, s.DistanceTo(P(5, 3)));
            Assert.Equal(5, s.DistanceTo(P(13, 4)));
            Assert.Equal(4, s.LineDistanceTo(P(13, 4)));
            Assert.Equal(1.3, s.Project(P(13, 4)), 12);
        }

        [Fact]
        public void DuplicateAndOverlapDetection()
        {
            var segs = new List<Segment2>
            {
                new Segment2(0, 0, 10, 0),       // 0
                new Segment2(10, 0, 0, 0.0001),  // 1 duplicate of 0, reversed, within tol
                new Segment2(5, 0, 15, 0),       // 2 overlaps 0 and 1 by 5
                new Segment2(15, 0, 20, 0),      // 3 touches 2 end to end: not an overlap
                new Segment2(0, 5, 10, 5),       // 4 parallel, apart
                new Segment2(3, -1, 3, 1),       // 5 crosses 0: not an overlap
            };
            var pairs = SegmentAnalysis.FindDuplicatesAndOverlaps(segs, 0.001);
            Assert.Contains(pairs, p => p.First == 0 && p.Second == 1 && p.Relation == SegmentRelation.Duplicate);
            var o = pairs.Single(p => p.First == 0 && p.Second == 2);
            Assert.Equal(SegmentRelation.Overlap, o.Relation);
            Assert.Equal(5, o.SharedLength, 6);
            Assert.Contains(pairs, p => p.First == 1 && p.Second == 2 && p.Relation == SegmentRelation.Overlap);
            Assert.DoesNotContain(pairs, p => p.Second == 3 || p.First == 3);
            Assert.DoesNotContain(pairs, p => p.Second == 4 || p.Second == 5);
            Assert.Equal(3, pairs.Count);
        }

        [Fact]
        public void LineIntersectionParallel()
        {
            Assert.False(Lines.Intersect(P(0, 0), P(1, 0), P(0, 1), P(2, 0), out _));
            Assert.True(Lines.Intersect(P(0, 0), P(1, 1), P(2, 0), P(0, 1), out var q));
            Assert.Equal(2, q.X, 12);
            Assert.Equal(2, q.Y, 12);
        }
    }

    public class PackerTests
    {
        private static readonly Rect2 Sheet = new Rect2(0, 0, 100, 100);

        [Fact]
        public void RowsTopToBottomLeftToRight()
        {
            var items = new[] { new PackItem("a", 40, 30), new PackItem("b", 40, 30), new PackItem("c", 40, 30) };
            var r = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 5, GapX = 2, GapY = 2 });
            Assert.True(r.AllPlaced);
            var a = r.Placed[0]; var b = r.Placed[1]; var c = r.Placed[2];
            Assert.Equal((5.0, 65.0), (a.Rect.MinX, a.Rect.MinY));
            Assert.Equal((47.0, 65.0), (b.Rect.MinX, b.Rect.MinY));
            Assert.Equal((5.0, 33.0, 1), (c.Rect.MinX, c.Rect.MinY, c.Row));
            Assert.Equal(0, a.Row);
            foreach (var p in r.Placed) Assert.True(Sheet.Deflate(5).Contains(p.Rect, 1e-9));
            for (int i = 0; i < r.Placed.Count; i++)
                for (int j = i + 1; j < r.Placed.Count; j++)
                    Assert.False(r.Placed[i].Rect.Overlaps(r.Placed[j].Rect));
        }

        [Fact]
        public void CentreAndDistributeAlignment()
        {
            var items = new[] { new PackItem("a", 40, 30), new PackItem("b", 40, 20) };
            var centre = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 5, GapX = 2, RowAlignment = RowAlignment.Centre, ItemAlignment = ItemAlignment.Middle });
            Assert.Equal(9, centre.Placed[0].Rect.MinX, 9);
            Assert.Equal(51, centre.Placed[1].Rect.MinX, 9);
            Assert.Equal(centre.Placed[0].Centre.Y, centre.Placed[1].Centre.Y, 9);
            var dist = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 5, GapX = 2, RowAlignment = RowAlignment.Distribute, ItemAlignment = ItemAlignment.Bottom });
            Assert.Equal(5, dist.Placed[0].Rect.MinX, 9);
            Assert.Equal(55, dist.Placed[1].Rect.MinX, 9);
            Assert.Equal(dist.Placed[0].Rect.MinY, dist.Placed[1].Rect.MinY, 9);
        }

        [Fact]
        public void ReportsWhatDoesNotFitAndKeepsGoing()
        {
            var items = new[]
            {
                new PackItem("huge", 200, 10),
                new PackItem("tall", 90, 60),
                new PackItem("second", 90, 60),
                new PackItem("small", 20, 20),
            };
            var r = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 5 });
            Assert.Equal(new[] { "tall", "small" }, r.Placed.Select(p => p.Id).ToArray());
            Assert.Equal("larger_than_area", r.NotPlaced.Single(n => n.Id == "huge").Reason);
            Assert.Equal("no_space", r.NotPlaced.Single(n => n.Id == "second").Reason);
            Assert.False(r.AllPlaced);
        }

        [Fact]
        public void Deterministic()
        {
            var items = Enumerable.Range(0, 12).Select(i => new PackItem("v" + i, 10 + i % 4 * 5, 8 + i % 3 * 4)).ToArray();
            var r1 = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 2, GapX = 1, GapY = 1 });
            var r2 = RectPacker.Pack(Sheet, items, new PackOptions { Margin = 2, GapX = 1, GapY = 1 });
            Assert.Equal(r1.Placed.Select(p => p.Rect.ToString()), r2.Placed.Select(p => p.Rect.ToString()));
            Assert.Equal(12, r1.Placed.Count + r1.NotPlaced.Count);
        }
    }
}
