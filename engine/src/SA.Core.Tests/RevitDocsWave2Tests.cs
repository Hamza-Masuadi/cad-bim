using System.Collections.Generic;
using System.Linq;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The schemas and pure helpers of the wave-2 Revit documentation ops; the handlers need Revit and cannot run here.</summary>
    public class RevitDocsWave2Tests
    {
        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["data.export"] = @"{ ""categories"": [""Doors""], ""parameters"": [""Fire Rating"", ""Mark""], ""include"": [""id"", ""type"", ""level"", ""room""], ""level"": ""Level 1"", ""format"": ""csv"", ""fileName"": ""{drawing} - doors"" }",
            ["schedule.export"] = @"{ ""schedule"": ""Door Schedule"", ""fileName"": ""{view}"", ""delimiter"": ""semicolon"", ""columnHeaders"": ""one_row"" }",
            ["schedule.key.create"] = @"{ ""name"": ""Room Style Key"", ""category"": ""Rooms"", ""keyParameter"": ""Room Style"", ""fields"": [""Floor Finish"", ""Wall Finish""],
                ""keys"": [ { ""name"": ""Office"", ""values"": { ""Floor Finish"": ""Carpet"", ""Wall Finish"": ""Paint"" } } ] }",
            ["area.plan.create"] = @"{ ""areaScheme"": ""Gross Building"", ""level"": ""Level 1"", ""scale"": ""1:100"", ""crop"": { ""min"": [0, 0], ""max"": [30000, 20000] } }",
            ["area.place"] = @"{ ""view"": ""Area Plan - Level 1"", ""boundaries"": [ [[0, 0], [20000, 0], [20000, 15000], [0, 15000]] ], ""areas"": [ { ""point"": [10000, 7500], ""name"": ""GBA"", ""number"": ""A1"" } ] }",
            ["view.drafting.create"] = @"{ ""name"": ""Typical Wall Details"", ""scale"": ""1:5"" }",
            ["detail.draw"] = @"{ ""view"": ""Typical Wall Details"", ""lines"": [ { ""start"": [0, 0], ""end"": [1000, 0] } ], ""circles"": [ { ""centre"": [500, 500], ""radius"": 100 } ],
                ""regions"": [ { ""boundary"": [[0, 0], [200, 0], [200, 200]], ""type"": ""Solid Black"" } ] }",
            ["view.crop.set"] = @"{ ""view"": ""Level 1"", ""rect"": { ""min"": [0, 0], ""max"": [30000, 20000] }, ""offset"": 500 }",
            ["view.callout.create"] = @"{ ""parentView"": ""Level 1"", ""rect"": { ""min"": [1000, 1000], ""max"": [5000, 4000] }, ""name"": ""Toilet Core Callout"", ""scale"": ""1:20"" }",
            ["colorfill.apply"] = @"{ ""view"": ""Level 1"", ""scheme"": ""Department"", ""parameter"": ""Department"", ""colors"": { ""Office"": ""#88CCEE"", ""Lab"": ""200,120,90"" }, ""legendAt"": [32000, 20000] }",
            ["tag.place"] = @"{ ""view"": ""Level 1"", ""tags"": [ { ""element"": ""316001"", ""offset"": [10, 10], ""leader"": true }, { ""element"": ""316002"", ""point"": [5000, 3000] } ] }",
            ["revision.cloud"] = @"{ ""view"": ""Level 1"", ""revision"": ""Rev 1"", ""rect"": { ""min"": [1000, 1000], ""max"": [4000, 3000] }, ""comments"": ""Door moved"" }",
            ["layout.clearances"] = @"{ ""view"": ""Level 1"", ""against"": [""Furniture"", ""Casework"", ""Walls""], ""zones"": [
                { ""id"": ""WC turning"", ""kind"": ""circle"", ""centre"": [2000, 1500], ""radius"": 750 },
                { ""id"": ""D1 swing"", ""kind"": ""doorSwing"", ""hinge"": [0, 0], ""leafWidth"": 900, ""closedAngle"": 0, ""swing"": 90, ""side"": ""left"", ""ignore"": [""316050""] },
                { ""id"": ""D1 clear"", ""kind"": ""doorClearance"", ""door"": ""316050"", ""depth"": 1200, ""faces"": ""facing"" },
                { ""id"": ""Corridor"", ""kind"": ""route"", ""points"": [[0, 0], [10000, 0], [10000, 5000]], ""width"": 1200 } ] }",
            ["dim.string"] = @"{ ""view"": ""Level 1"", ""start"": [0, 5000], ""end"": [20000, 5000], ""offset"": 1500, ""pick"": [""walls"", ""grids""], ""wallFaces"": ""both"", ""overall"": true }",
            ["text.replace"] = @"{ ""find"": ""PRELIMINARY"", ""replace"": ""FOR CONSTRUCTION"", ""matchCase"": true, ""wholeWord"": true, ""parameters"": [""Comments""], ""categories"": [""Doors""], ""dryRun"": true }",
        };

        public static IEnumerable<object[]> Names() => Examples.Keys.Select(k => new object[] { k });

        private static OpSchema Schema(string name) => OpRegistry.Default.Find(name, AppKind.Revit) ?? throw new Xunit.Sdk.XunitException(name + " is not declared for Revit");

        private static List<Issue> Errors(string op, string args) =>
            JobValidator.ValidateArgs(Schema(op), (JsonObject)JsonValue.Parse(args), LengthUnit.Millimetre).Where(i => i.IsError).ToList();

        [Fact]
        public void AllFifteenAreDeclaredForRevitWithTheirFlags()
        {
            Assert.Equal(15, Examples.Count);
            Assert.Equal(Examples.Keys.OrderBy(x => x), RevitOpsDocsWave2.All().Select(s => s.Name).OrderBy(x => x));
            foreach (var name in Examples.Keys)
            {
                var s = Schema(name);
                Assert.Equal(AppKind.Revit, s.App);
                Assert.Equal(name == "schedule.key.create" || name == "colorfill.apply", s.Ensure);
                Assert.False(s.Destructive, name);
            }
        }

        [Theory]
        [MemberData(nameof(Names))]
        public void ExampleArgsValidate(string op)
        {
            var errors = Errors(op, Examples[op]);
            Assert.True(errors.Count == 0, op + ": " + string.Join("; ", errors));
        }

        [Fact]
        public void ConstraintsAreEnforced()
        {
            Assert.NotEmpty(Errors("text.replace", @"{ ""find"": ""A"", ""replace"": ""B"", ""parameters"": [""Comments""] }"));
            Assert.NotEmpty(Errors("view.crop.set", @"{ ""view"": ""Level 1"", ""rect"": { ""min"": [0, 0], ""max"": [1, 1] }, ""room"": ""1234"" }"));
            Assert.NotEmpty(Errors("view.crop.set", @"{ ""view"": ""Level 1"" }"));
            Assert.NotEmpty(Errors("tag.place", @"{ ""view"": ""Level 1"", ""tags"": [ { ""element"": ""1"", ""point"": [0, 0], ""offset"": [1, 1] } ] }"));
            Assert.NotEmpty(Errors("layout.clearances", @"{ ""view"": ""Level 1"", ""against"": [""Walls""], ""zones"": [ { ""id"": ""z"", ""kind"": ""ellipse"" } ] }"));
            Assert.NotEmpty(Errors("layout.clearances", @"{ ""view"": ""Level 1"", ""against"": [""Walls""], ""zones"": [ { ""id"": ""z"", ""kind"": ""doorSwing"", ""swing"": 200 } ] }"));
            Assert.NotEmpty(Errors("detail.draw", @"{ ""view"": ""Typical Wall Details"" }"));
            Assert.NotEmpty(Errors("revision.cloud", @"{ ""view"": ""Level 1"", ""revision"": ""Rev 1"" }"));
            Assert.NotEmpty(Errors("data.export", @"{ ""categories"": [] }"));
        }

        [Theory]
        [InlineData("text.replace")]
        [InlineData("layout.clearances")]
        [InlineData("dim.string")]
        public void SharedOpsAgreeWithAutoCadWhereTheMeaningIsTheSame(string op)
        {
            var revit = Schema(op);
            var cad = OpRegistry.Default.Find(op, AppKind.AutoCad)!;
            Assert.NotNull(cad);
            var shared = new Dictionary<string, string[]>
            {
                ["text.replace"] = new[] { "find", "replace", "matchCase", "wholeWord", "dryRun", "expectCount" },
                ["layout.clearances"] = new[] { "zones", "ignore", "tolerance", "failOnIntrusion" },
                ["dim.string"] = new[] { "offset", "orientation", "overall", "overallOffset", "dimStyle" },
            }[op];
            foreach (var name in shared)
            {
                var r = revit.FindField(name)!;
                var c = cad.FindField(name)!;
                Assert.True(r != null && c != null, op + "." + name);
                Assert.Equal(c.Type, r.Type);
                Assert.Equal(c.Default?.ToJson(), r.Default?.ToJson());
            }
            foreach (var o in cad.Outputs.Where(o => revit.Outputs.Any(x => x.Name == o.Name)))
                Assert.Equal(o.Type, revit.Outputs.First(x => x.Name == o.Name).Type);
            if (op == "layout.clearances")
            {
                var rz = revit.FindField("zones")!.Item!;
                var cz = cad.FindField("zones")!.Item!;
                foreach (var f in new[] { "id", "kind", "centre", "radius", "min", "max", "points", "width", "hinge", "leafWidth", "closedAngle", "swing", "side", "ignore" })
                {
                    var a = rz.Fields.FirstOrDefault(x => x.Name == f);
                    var b = cz.Fields.FirstOrDefault(x => x.Name == f);
                    Assert.True(a != null && b != null, "zone." + f);
                    Assert.Equal(b!.Type, a!.Type);
                    Assert.Equal(b.Default?.ToJson(), a.Default?.ToJson());
                }
                Assert.Contains(revit.Outputs, o => o.Name == "count");
                Assert.Contains(revit.Outputs, o => o.Name == "clear");
            }
            if (op == "text.replace")
                foreach (var o in new[] { "count", "matches", "changes" }) Assert.Contains(revit.Outputs, x => x.Name == o);
        }

        // ----- ClearanceGeometry -----

        private static double SignedArea(IReadOnlyList<Point2> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) a += p[i].Cross(p[(i + 1) % p.Count]);
            return a / 2;
        }

        private static List<Point2> Square(double x0, double y0, double x1, double y1) => ClearanceGeometry.Rect(new Point2(x0, y0), new Point2(x1, y1));

        [Fact]
        public void PolygonDepthIsTheMinimumTranslation()
        {
            Assert.Equal(0.25, ClearanceGeometry.PolygonDepth(Square(0, 0, 1, 1), Square(0.75, 0, 1.75, 1)), 9);
            Assert.Equal(0.0, ClearanceGeometry.PolygonDepth(Square(0, 0, 1, 1), Square(1, 0, 2, 1)), 9);
            Assert.Equal(0.0, ClearanceGeometry.PolygonDepth(Square(0, 0, 1, 1), Square(3, 3, 4, 4)), 9);
            Assert.Equal(0.5, ClearanceGeometry.PolygonDepth(Square(0, 0, 10, 10), Square(4, 9.5, 6, 20)), 9);
        }

        [Fact]
        public void CircleDepthCountsTheCentreInside()
        {
            Assert.Equal(0.5, ClearanceGeometry.CircleDepth(new Point2(0, 0), 1, Square(0.5, -1, 2, 1)), 9);
            Assert.Equal(2.0, ClearanceGeometry.CircleDepth(new Point2(0, 0), 1, Square(-1, -1, 1, 1)), 9);
            Assert.Equal(0.0, ClearanceGeometry.CircleDepth(new Point2(0, 0), 1, Square(2, 2, 3, 3)), 9);
        }

        [Fact]
        public void HullAndConvexity()
        {
            var hull = ClearanceGeometry.ConvexHull(new[] { new Point2(0, 0), new Point2(2, 0), new Point2(1, 1), new Point2(2, 2), new Point2(0, 2), new Point2(1, 0) });
            Assert.Equal(4, hull.Count);
            Assert.True(SignedArea(hull) > 0);
            Assert.Equal(4.0, SignedArea(hull), 9);
            Assert.True(ClearanceGeometry.IsConvex(Square(0, 0, 1, 1), 1e-9));
            var l = new List<Point2> { new Point2(0, 0), new Point2(2, 0), new Point2(2, 1), new Point2(1, 1), new Point2(1, 2), new Point2(0, 2) };
            Assert.False(ClearanceGeometry.IsConvex(l, 1e-9));
        }

        [Fact]
        public void SectorsAreCounterClockwiseBothWays()
        {
            var ccw = ClearanceGeometry.Sector(new Point2(0, 0), 1, 0, 90);
            Assert.True(SignedArea(ccw) > 0);
            Assert.Contains(ccw, p => p.DistanceTo(new Point2(1, 0)) < 1e-9);
            Assert.Contains(ccw, p => p.DistanceTo(new Point2(0, 1)) < 1e-9);
            var cw = ClearanceGeometry.Sector(new Point2(0, 0), 1, 0, -90);
            Assert.True(SignedArea(cw) > 0);
            Assert.Contains(cw, p => p.DistanceTo(new Point2(0, -1)) < 1e-9);
            Assert.Equal(0.0, ClearanceGeometry.PolygonDepth(ccw, Square(-2, -2, -0.1, -0.1)), 9);
            Assert.True(ClearanceGeometry.PolygonDepth(ccw, Square(0.5, 0.5, 2, 2)) > 0);
        }

        [Fact]
        public void RouteCoversItsCorners()
        {
            var parts = ClearanceGeometry.Route(new[] { new Point2(0, 0), new Point2(10, 0), new Point2(10, 5) }, 2);
            Assert.Equal(2, parts.Count);
            Assert.Equal(11.0, parts[0].Max(p => p.X), 9);
            Assert.Equal(0.0, parts[0].Min(p => p.X), 9);
            Assert.Equal(-1.0, parts[1].Min(p => p.Y), 9);
            Assert.Equal(5.0, parts[1].Max(p => p.Y), 9);
            Assert.Empty(ClearanceGeometry.Route(new[] { new Point2(1, 1), new Point2(1, 1) }, 2));
            var strip = ClearanceGeometry.Strip(new Point2(0, 0), new Point2(4, 0), 0.2);
            Assert.Equal(0.8, System.Math.Abs(SignedArea(strip)), 9);
        }

        // ----- TextMatch -----

        [Fact]
        public void TextMatchFindsNonOverlappingWholeWords()
        {
            Assert.Equal(new[] { 0, 11 }, TextMatch.FindAll("cat concat cat", "cat", true, true));
            Assert.Equal(new[] { 0, 7, 11 }, TextMatch.FindAll("cat concat cat", "cat", true, false));
            Assert.Equal(new[] { 0, 2 }, TextMatch.FindAll("aaaa", "aa", true, false));
            Assert.Equal(new[] { 4 }, TextMatch.FindAll("Rev PRELIMINARY_1 preliminary", "PRELIMINARY_1", true, true));
            Assert.Empty(TextMatch.FindAll("Cat", "cat", true, false));
            Assert.Equal(new[] { 0 }, TextMatch.FindAll("Cat", "cat", false, false));
            Assert.Empty(TextMatch.FindAll("x", "", true, false));
            Assert.Equal("dog concat dog", TextMatch.ReplaceAll("cat concat cat", "cat", "dog", true, true));
            Assert.Equal("con", TextMatch.ReplaceAll("concat", "cat", "", true, false));
        }

        // ----- Delimited -----

        [Fact]
        public void DelimitedRoundTrips()
        {
            var rows = new List<List<string>>
            {
                new List<string> { "id", "name", "note" },
                new List<string> { "1", "Door, single", "says \"hi\"" },
                new List<string> { "2", "two\r\nlines", " padded " },
                new List<string> { "3", "", "x" },
            };
            foreach (var sep in new[] { ',', ';', '\t' })
            {
                string text = Delimited.Write(rows, sep);
                Assert.EndsWith("\r\n", text);
                var back = Delimited.Parse(text, sep);
                Assert.Equal(rows.Count, back.Count);
                for (int i = 0; i < rows.Count; i++) Assert.Equal(rows[i], back[i]);
            }
            Assert.Equal(',', Delimited.Separator("comma"));
            Assert.Equal(';', Delimited.Separator("semicolon"));
            Assert.Equal('\t', Delimited.Separator("tab"));
            Assert.Equal("1.5", Delimited.Number(1.5));
            Assert.Equal("0", Delimited.Number(1e-12));
            Assert.Equal("-1234.25", Delimited.Number(-1234.25));
            Assert.Equal(new List<string> { "a", "b'c" }, Delimited.Parse("'a','b''c'\r\n", ',', '\'')[0]);
            Assert.Equal(new List<string> { "\"a\"", "b" }, Delimited.Parse("\"a\",b", ',', null)[0]);
        }
    }
}
