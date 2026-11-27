using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Geometry;
using Xunit;

namespace SA.Core.Tests
{
    public class WallNetworkTests
    {
        private static WallInput W(string id, double ax, double ay, double bx, double by, double t) =>
            new WallInput(id, new Point2(ax, ay), new Point2(bx, by), t);

        private static WallNetworkResult Build(params WallInput[] walls) => WallNetwork.Build(walls);

        private static double TotalLength(WallNetworkResult r) => r.AllSegments().Sum(s => s.Length);

        private static double PieceArea(WallNetworkResult r) => r.Walls.SelectMany(w => w.Pieces).Sum(p => p.SignedArea);

        private static bool HasSegment(WallNetworkResult r, double ax, double ay, double bx, double by, double tol = 1e-6) =>
            r.AllSegments().Any(s =>
                (s.A.DistanceTo(new Point2(ax, ay)) <= tol && s.B.DistanceTo(new Point2(bx, by)) <= tol) ||
                (s.B.DistanceTo(new Point2(ax, ay)) <= tol && s.A.DistanceTo(new Point2(bx, by)) <= tol));

        /// <summary>No visible segment may pass through the interior of the wall region (that would be an uncleaned joint).</summary>
        private static void AssertNoLinesInside(WallNetworkResult r)
        {
            var pieces = r.Walls.SelectMany(w => w.Pieces).ToList();
            foreach (var s in r.AllSegments())
            {
                var mid = s.Midpoint;
                var n = s.Vector.PerpLeft.Normalised() * 0.5;
                bool leftCovered = pieces.Any(p => p.Locate(mid + n, 1e-9) == PointLocation.Inside);
                bool rightCovered = pieces.Any(p => p.Locate(mid - n, 1e-9) == PointLocation.Inside);
                Assert.False(leftCovered && rightCovered, "segment " + s + " lies inside the walls");
            }
        }

        [Fact]
        public void SingleWall()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 100));
            Assert.Empty(r.Issues);
            Assert.Empty(r.Junctions);
            Assert.Equal(2, r.OpenEnds.Count);
            Assert.Contains(r.OpenEnds, e => e.AtStart && e.Point == new Point2(0, 0));
            Assert.Contains(r.OpenEnds, e => !e.AtStart && e.Point == new Point2(1000, 0));
            Assert.Equal(4, r.Walls[0].Segments.Count);
            Assert.Equal(2200, TotalLength(r), 6);
            Assert.True(HasSegment(r, 0, 50, 1000, 50));
            Assert.True(HasSegment(r, 0, -50, 1000, -50));
            Assert.Single(r.Loops);
            Assert.True(r.Loops[0].Closed);
            Assert.Equal(100000, r.Loops[0].ToPolygon(1e-6)!.Area, 6);
            Assert.Equal(100000, PieceArea(r), 6);
        }

        [Fact]
        public void LCornerMitre()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 100), W("b", 0, 0, 0, 1000, 100));
            var j = Assert.Single(r.Junctions);
            Assert.Equal(JunctionKind.Corner, j.Kind);
            Assert.Equal(new[] { "a", "b" }, j.WallIds);
            Assert.Equal(2, r.OpenEnds.Count);
            Assert.True(HasSegment(r, -50, -50, 1000, -50));
            Assert.True(HasSegment(r, 50, 50, 1000, 50));
            Assert.True(HasSegment(r, -50, -50, -50, 1000));
            Assert.True(HasSegment(r, 50, 50, 50, 1000));
            Assert.False(HasSegment(r, -50, -50, 50, 50), "the mitre joint must not be drawn");
            Assert.Equal(4200, TotalLength(r), 6);
            Assert.Equal(200000, PieceArea(r), 6);
            var loop = Assert.Single(r.Loops);
            Assert.True(loop.Closed);
            Assert.Equal(200000, loop.ToPolygon(1e-6)!.Area, 6);
            AssertNoLinesInside(r);
        }

        [Fact]
        public void LCornerUnequalThicknessMitreAndButt()
        {
            var a = W("a", 0, 0, 1000, 0, 200);
            var b = W("b", 0, 0, 0, 1000, 100);
            var mitre = WallNetwork.Build(new[] { a, b });
            var butt = WallNetwork.Build(new[] { a, b }, new WallNetworkOptions { CornerStyle = CornerStyle.Butt });

            foreach (var r in new[] { mitre, butt })
            {
                Assert.Equal(300000, PieceArea(r), 6);
                Assert.True(HasSegment(r, -50, -100, 1000, -100) || r.AllSegments().Any(s => s.A.Y == -100 && s.B.Y == -100 && Math.Abs(s.Length - 1050) < 1e-6));
                Assert.Equal(1, r.Loops.Count(l => l.Closed));
                Assert.Equal(300000, r.Loops.Single().ToPolygon(1e-6)!.Area, 6);
                AssertNoLinesInside(r);
            }
            // Mitre: the joint runs from the outer corner (-50,-100) to the inner corner (50,100).
            Assert.NotEqual(210000, mitre.Find("a")!.Pieces.Single().Area, 3);
            // Butt: the thicker wall runs through to the thin wall's outer face.
            Assert.Equal(210000, butt.Find("a")!.Pieces.Single().Area, 6);
            Assert.Equal(90000, butt.Find("b")!.Pieces.Single().Area, 6);
        }

        [Fact]
        public void TJunctionThroughWallStaysContinuous()
        {
            var r = Build(W("a", 0, 0, 2000, 0, 200), W("b", 1000, 0, 1000, 1000, 100));
            var j = Assert.Single(r.Junctions);
            Assert.Equal(JunctionKind.Tee, j.Kind);
            Assert.Equal(3, r.OpenEnds.Count);
            Assert.True(HasSegment(r, 0, -100, 2000, -100), "far face of the through wall is one line");
            Assert.True(HasSegment(r, 0, 100, 950, 100));
            Assert.True(HasSegment(r, 1050, 100, 2000, 100));
            Assert.True(HasSegment(r, 950, 100, 950, 1000));
            Assert.True(HasSegment(r, 1050, 100, 1050, 1000));
            Assert.False(r.AllSegments().Any(s => s.A.Y < 99 && s.B.Y < 99 && s.A.X > 900 && s.A.X < 1100 && s.B.X > 900 && s.B.X < 1100), "no line inside the junction");
            Assert.Equal(490000, PieceArea(r), 6);
            Assert.Equal(490000, r.Loops.Single().ToPolygon(1e-6)!.Area, 6);
            AssertNoLinesInside(r);
            // The through wall keeps one id with two pieces.
            Assert.Equal(2, r.Find("a")!.Pieces.Count);
        }

        [Fact]
        public void TJunctionWithThickerStem()
        {
            var r = Build(W("a", 0, 0, 2000, 0, 100), W("b", 1000, 0, 1000, 1000, 200));
            Assert.Equal(JunctionKind.Tee, r.Junctions.Single().Kind);
            Assert.True(HasSegment(r, 0, 50, 900, 50));
            Assert.True(HasSegment(r, 1100, 50, 2000, 50));
            Assert.True(HasSegment(r, 900, 50, 900, 1000));
            Assert.True(HasSegment(r, 0, -50, 2000, -50));
            Assert.Equal(390000, PieceArea(r), 6);
            AssertNoLinesInside(r);
        }

        [Fact]
        public void TJunctionWhenStemIsDrawnFirstAndThroughWallIsTwoWalls()
        {
            var r = Build(W("stem", 1000, 1000, 1000, 0, 100), W("left", 0, 0, 1000, 0, 200), W("right", 1000, 0, 2000, 0, 200));
            Assert.Equal(JunctionKind.Tee, r.Junctions.Single().Kind);
            Assert.True(HasSegment(r, 0, -100, 2000, -100) || (HasSegment(r, 0, -100, 1000, -100) && HasSegment(r, 1000, -100, 2000, -100)));
            Assert.Equal(490000, PieceArea(r), 6);
            AssertNoLinesInside(r);
        }

        [Fact]
        public void XCrossing()
        {
            var r = Build(W("a", -1000, 0, 1000, 0, 100), W("b", 0, -1000, 0, 1000, 100));
            var j = Assert.Single(r.Junctions);
            Assert.Equal(JunctionKind.Cross, j.Kind);
            Assert.Equal(new Point2(0, 0), j.Point);
            Assert.Equal(4, r.OpenEnds.Count);
            Assert.Equal(8 * 950 + 4 * 100, TotalLength(r), 6);
            Assert.True(HasSegment(r, -1000, 50, -50, 50));
            Assert.True(HasSegment(r, 50, 50, 1000, 50));
            Assert.True(HasSegment(r, 50, 50, 50, 1000));
            Assert.Equal(390000, PieceArea(r), 6);
            var loop = Assert.Single(r.Loops);
            Assert.True(loop.Closed);
            Assert.Equal(12, loop.ToPolygon(1e-6)!.Vertices.Count);
            AssertNoLinesInside(r);
        }

        [Fact]
        public void CollinearWallsEndToEnd()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 100), W("b", 1000, 0, 2000, 0, 100));
            Assert.Equal(JunctionKind.Inline, r.Junctions.Single().Kind);
            Assert.Equal(2, r.OpenEnds.Count);
            Assert.DoesNotContain(r.AllSegments(), s => Math.Abs(s.A.X - 1000) < 1e-6 && Math.Abs(s.B.X - 1000) < 1e-6);
            Assert.Equal(4200, TotalLength(r), 6);
            Assert.Equal(200000, r.Loops.Single().ToPolygon(1e-6)!.Area, 6);
        }

        [Fact]
        public void CollinearWallsOfUnequalThicknessGetSteps()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 200), W("b", 1000, 0, 2000, 0, 100));
            Assert.Equal(JunctionKind.Inline, r.Junctions.Single().Kind);
            Assert.True(HasSegment(r, 1000, 100, 1000, 50));
            Assert.True(HasSegment(r, 1000, -100, 1000, -50));
            Assert.False(HasSegment(r, 1000, 50, 1000, -50));
            Assert.Equal(300000, r.Loops.Single().ToPolygon(1e-6)!.Area, 6);
        }

        [Fact]
        public void NearMissesWithinToleranceAreJoined()
        {
            var opt = new WallNetworkOptions { Tolerance = 1.0 };
            var l = WallNetwork.Build(new[] { W("a", 0, 0, 1000, 0, 100), W("b", 0.4, 0.3, 0, 1000, 100) }, opt);
            Assert.Equal(JunctionKind.Corner, l.Junctions.Single().Kind);
            Assert.Equal(2, l.OpenEnds.Count);

            var t = WallNetwork.Build(new[] { W("a", 0, 0, 2000, 0, 200), W("b", 1000, 0.6, 1000, 1000, 100) }, opt);
            Assert.Equal(JunctionKind.Tee, t.Junctions.Single().Kind);
            Assert.Equal(3, t.OpenEnds.Count);

            var miss = WallNetwork.Build(new[] { W("a", 0, 0, 1000, 0, 100), W("b", 5, 5, 5, 1000, 100) }, opt);
            Assert.Empty(miss.Junctions);
            Assert.Equal(4, miss.OpenEnds.Count);
        }

        [Fact]
        public void RectangularRoomGivesClosedInnerAndOuterLoops()
        {
            var walls = new[]
            {
                W("s", 0, 0, 4000, 0, 200), W("e", 4000, 0, 4000, 3000, 200),
                W("n", 4000, 3000, 0, 3000, 200), W("w", 0, 3000, 0, 0, 200),
            };
            foreach (var style in new[] { CornerStyle.Mitre, CornerStyle.Butt })
            {
                var r = WallNetwork.Build(walls, new WallNetworkOptions { CornerStyle = style });
                Assert.Empty(r.OpenEnds);
                Assert.Equal(4, r.Junctions.Count);
                Assert.All(r.Junctions, j => Assert.Equal(JunctionKind.Corner, j.Kind));
                Assert.Equal(2, r.Loops.Count);
                Assert.All(r.Loops, l => Assert.True(l.Closed));
                var areas = r.Loops.Select(l => l.ToPolygon(1e-6)!.Area).OrderBy(a => a).ToArray();
                Assert.Equal(3800 * 2800, areas[0], 3);
                Assert.Equal(4200 * 3200, areas[1], 3);
                Assert.Equal(4200 * 3200 - 3800 * 2800, PieceArea(r), 3);
                AssertNoLinesInside(r);
            }
        }

        [Fact]
        public void RoomWithAPartitionTee()
        {
            var r = Build(
                W("s", 0, 0, 4000, 0, 200), W("e", 4000, 0, 4000, 3000, 200),
                W("n", 4000, 3000, 0, 3000, 200), W("w", 0, 3000, 0, 0, 200),
                W("p", 2000, 0, 2000, 3000, 100));
            Assert.Equal(2, r.Junctions.Count(j => j.Kind == JunctionKind.Tee));
            Assert.Empty(r.OpenEnds);
            Assert.Equal(3, r.Loops.Count);
            var areas = r.Loops.Select(l => l.ToPolygon(1e-6)!.Area).OrderBy(a => a).ToArray();
            Assert.Equal(1850 * 2800, areas[0], 3);
            Assert.Equal(1850 * 2800, areas[1], 3);
            Assert.Equal(4200 * 3200, areas[2], 3);
            AssertNoLinesInside(r);
        }

        [Fact]
        public void AcuteCornerIsBevelledWithinMitreLimit()
        {
            double ang = 10 * Math.PI / 180;
            var r = Build(W("a", 0, 0, 1000, 0, 100), W("b", 0, 0, 1000 * Math.Cos(ang), 1000 * Math.Sin(ang), 100));
            double limit = 4 * 50;
            foreach (var s in r.AllSegments())
            {
                Assert.True(s.A.Length < 1100 && s.B.Length < 1100);
                // Without the bevel the outer point would be 50 / sin(5 deg) = 574 behind the junction.
                Assert.True(s.A.X > -1.5 * limit && s.B.X > -1.5 * limit, "outer mitre point must be cut back");
            }
            Assert.Single(r.Loops);
            Assert.True(r.Loops[0].Closed);
        }

        [Fact]
        public void IssuesForBadInput()
        {
            var r = Build(
                W("a", 0, 0, 1000, 0, 100),
                W("dup", 1000, 0, 0, 0, 100),
                W("over", 500, 0, 1500, 0, 100),
                W("zero", 5, 5, 5.2, 5, 100),
                W("thin", 0, 500, 100, 500, 0),
                W("a", 0, 900, 100, 900, 100));
            Assert.Contains(r.Issues, i => i.Code == "duplicate" && i.WallIds.Contains("dup"));
            Assert.Contains(r.Issues, i => i.Code == "overlap" && i.WallIds.Contains("over"));
            Assert.Contains(r.Issues, i => i.Code == "zero_length");
            Assert.Contains(r.Issues, i => i.Code == "invalid_thickness");
            Assert.Contains(r.Issues, i => i.Code == "duplicate_id");
            Assert.Single(r.Walls);
        }

        [Fact]
        public void OpenEndsAreReportedPerWallEnd()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 100), W("b", 1000, 0, 1000, 800, 100), W("c", 3000, 0, 3000, 500, 100));
            Assert.Equal(4, r.OpenEnds.Count);
            Assert.Contains(r.OpenEnds, e => e.WallId == "a" && e.AtStart);
            Assert.Contains(r.OpenEnds, e => e.WallId == "b" && !e.AtStart && e.Point == new Point2(1000, 800));
            Assert.Equal(2, r.OpenEnds.Count(e => e.WallId == "c"));
            Assert.DoesNotContain(r.OpenEnds, e => e.WallId == "a" && !e.AtStart);
        }

        [Fact]
        public void ThreeWayYJunctionIsClosed()
        {
            var r = Build(W("a", 0, 0, 1000, 0, 100), W("b", 0, 0, -500, 866.0254, 100), W("c", 0, 0, -500, -866.0254, 100));
            Assert.Equal(JunctionKind.Other, r.Junctions.Single().Kind);
            Assert.Single(r.Loops);
            Assert.True(r.Loops[0].Closed);
            AssertNoLinesInside(r);
        }
    }
}
