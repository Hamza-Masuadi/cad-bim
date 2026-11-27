using System.Linq;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Core.Standards;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The nine evaluators of data/checks/FACTS.md section 3.</summary>
    public class StandardEvaluatorTests
    {
        private static readonly CheckRunner Runner = new CheckRunner(EvaluatorRegistry.CreateNeutral());

        private static CheckDefinition Def(string evaluator, string paramsJson, CheckFix? fix = null, string title = "Test") =>
            new CheckDefinition("t.check", CheckApp.Both, title, Severity.Warning, evaluator, (JsonObject)JsonValue.Parse(paramsJson), fix: fix);

        private static CheckResult Run(string evaluator, string paramsJson, Facts facts, ProjectStandard? std = null, CheckFix? fix = null) =>
            Runner.Run(Def(evaluator, paramsJson, fix), facts, std ?? RealData.Us);

        private static Facts Entities()
        {
            var f = new Facts("autocad");
            f.Meta.Add("units.length", "mm");
            var e = f.Table("entities");
            e.Add("E1").Set("type", "DIMENSION").Set("layer", "A-ANNO-DIMS").Set("isText", false);
            e.Add("E2").Set("type", "DIMENSION").Set("layer", "A-WALL").Set("isText", false);
            e.Add("E3").Set("type", "TEXT").Set("layer", "A-WALL").Set("isText", true);
            e.Add("E4").Set("type", "LINE").Set("layer", "A-WALL").Set("isText", false).Set("offOrthoDeg", 0.3);
            e.Add("E5").Set("type", "LINE").Set("layer", "A-DOOR").Set("isText", false).Set("offOrthoDeg", 0);
            e.Add("E6").Set("type", "LINE").Set("layer", "A-DOOR").Set("isText", false).Set("offOrthoDeg", 12);
            e.Add("E7").Set("type", "SPLINE").Set("isText", false);
            return f;
        }

        [Fact]
        public void EveryNewEvaluatorIsInTheDefaultRegistry()
        {
            var names = EvaluatorRegistry.CreateNeutral().Names.ToList();
            foreach (var n in new[] { "conditional_field_equals", "field_within", "distinct_count_min", "scale_in_standard", "units_match_standard",
                "dim_precision_min", "matches_standard_entry", "group_count_over_threshold", "manual_review" })
                Assert.Contains(n, names);
            Assert.Equal(21, names.Count);
            var r = EvaluatorRegistry.CreateNeutral();
            Assert.False(r.TryRegister(new ManualReview()));
            Assert.True(r.Contains("manual_review"));
        }

        [Fact]
        public void ConditionalFieldEquals()
        {
            var f = Entities();
            // Dimensions not on a dimension layer.
            var r = Run("conditional_field_equals", @"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""DIMENSION"", ""ifField"": ""layer"", ""ifStandardPattern"": ""dimensionLayer"", ""ifNegate"": true }", f);
            Assert.Equal(CheckStatus.Fail, r.Status);
            // E7 has no layer: with ifNegate the condition holds, but it is not a dimension.
            Assert.Equal(new[] { "E2" }, r.Findings.Select(x => x.RecordId));
            var d = r.Findings[0].Data;
            Assert.Equal("type", d.GetStringOrNull("field"));
            Assert.Equal("DIMENSION", d.GetStringOrNull("value"));
            Assert.Equal("layer", d.GetStringOrNull("ifField"));
            Assert.Equal("A-WALL", d.GetStringOrNull("ifValue"));
            Assert.Equal("type is DIMENSION on layer 'A-WALL' [E2]", r.Findings[0].Message);

            // Text on wall layers, with "in" and an inline pattern, case-insensitive.
            r = Run("conditional_field_equals", @"{ ""table"": ""entities"", ""field"": ""isText"", ""in"": [true], ""ifField"": ""layer"", ""ifPattern"": ""a-wall.*"", ""ignoreCase"": true }", f);
            Assert.Equal(new[] { "E3" }, r.Findings.Select(x => x.RecordId));

            // No field: every record meeting the condition is a finding.
            r = Run("conditional_field_equals", @"{ ""table"": ""entities"", ""where"": { ""type"": ""LINE"" }, ""ifField"": ""layer"", ""ifPattern"": ""A-DOOR"" }", f);
            Assert.Equal(new[] { "E5", "E6" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal("layer", r.Findings[0].Data.GetStringOrNull("field"));

            // Negate: a finding when the field differs.
            r = Run("conditional_field_equals", @"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""LINE"", ""negate"": true, ""ifField"": ""layer"", ""ifPattern"": ""A-DOOR"" }", f);
            Assert.Equal(CheckStatus.Pass, r.Status);
        }

        [Theory]
        [InlineData(@"{ ""table"": ""nope"", ""field"": ""type"", ""equals"": ""X"", ""ifField"": ""layer"", ""ifPattern"": "".*"" }", CheckStatus.NotImplemented)]
        [InlineData(@"{ ""table"": ""entities"", ""field"": ""type"", ""ifField"": ""layer"", ""ifPattern"": "".*"" }", CheckStatus.Error)]
        [InlineData(@"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""X"", ""ifField"": ""layer"" }", CheckStatus.Error)]
        [InlineData(@"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""X"", ""ifField"": ""layer"", ""ifPattern"": ""a"", ""ifStandardPattern"": ""wallLayer"" }", CheckStatus.Error)]
        [InlineData(@"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""X"", ""ifField"": ""layer"", ""ifStandardPattern"": ""noSuchPattern"" }", CheckStatus.Error)]
        [InlineData(@"{ ""table"": ""entities"", ""field"": ""type"", ""equals"": ""X"", ""ifField"": ""layer"", ""ifPattern"": ""(unclosed"" }", CheckStatus.Error)]
        [InlineData(@"{ ""table"": ""entities"", ""equals"": ""X"", ""ifField"": ""layer"", ""ifPattern"": "".*"" }", CheckStatus.Error)]
        public void ConditionalFieldEqualsMissingTableAndBadParams(string p, CheckStatus expected) =>
            Assert.Equal(expected, Run("conditional_field_equals", p, Entities()).Status);

        [Fact]
        public void FieldWithin()
        {
            var f = Entities();
            var r = Run("field_within", @"{ ""table"": ""entities"", ""where"": { ""type"": ""LINE"" }, ""field"": ""offOrthoDeg"", ""min"": 1e-6, ""minExclusive"": true, ""max"": 0.5 }", f);
            Assert.Equal(new[] { "E4" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal(0.5, r.Findings[0].Data["max"]!.AsDouble());
            Assert.Equal(0.3, r.Findings[0].Data["value"]!.AsDouble());
            // Inclusive bounds catch the 0; non-numbers and absent values are ignored.
            r = Run("field_within", @"{ ""table"": ""entities"", ""field"": ""offOrthoDeg"", ""max"": 0.5 }", f);
            Assert.Equal(new[] { "E4", "E5" }, r.Findings.Select(x => x.RecordId));
            Assert.True(r.Findings[0].Data["min"]!.IsNull);
            r = Run("field_within", @"{ ""table"": ""entities"", ""field"": ""offOrthoDeg"", ""min"": 0, ""max"": 12, ""maxExclusive"": true, ""minExclusive"": true }", f);
            Assert.Equal(new[] { "E4" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal(CheckStatus.Pass, Run("field_within", @"{ ""table"": ""entities"", ""field"": ""offOrthoDeg"", ""min"": 50 }", f).Status);
            Assert.Equal(CheckStatus.NotImplemented, Run("field_within", @"{ ""table"": ""lines"", ""field"": ""x"", ""max"": 1 }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("field_within", @"{ ""table"": ""entities"", ""field"": ""offOrthoDeg"" }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("field_within", @"{ ""table"": ""entities"", ""field"": ""offOrthoDeg"", ""max"": ""1"" }", f).Status);
        }

        [Fact]
        public void DistinctCountMin()
        {
            var f = new Facts("autocad");
            var l = f.Table("layers");
            l.Add("1").Set("lineweight", 0.25).Set("isUsed", true);
            l.Add("2").Set("lineweight", 0.25).Set("isUsed", true);
            l.Add("3").Set("lineweight", "default").Set("isUsed", false);
            l.Add("4").Set("isUsed", true);
            var r = Run("distinct_count_min", @"{ ""table"": ""layers"", ""where"": { ""isUsed"": true }, ""field"": ""lineweight"", ""min"": 2 }", f);
            Assert.Equal(CheckStatus.Fail, r.Status);
            var d = r.Findings.Single().Data;
            Assert.Equal(1, d["count"]!.AsInt64());
            Assert.Equal(0.25, d["value"]!.AsArray()[0].AsDouble());
            Assert.Null(r.Findings[0].RecordId);
            Assert.Equal(CheckStatus.Pass, Run("distinct_count_min", @"{ ""table"": ""layers"", ""field"": ""lineweight"", ""min"": 2 }", f).Status);
            // No record passes where: no finding.
            Assert.Equal(CheckStatus.Pass, Run("distinct_count_min", @"{ ""table"": ""layers"", ""where"": { ""isUsed"": ""x"" }, ""field"": ""lineweight"", ""min"": 2 }", f).Status);
            Assert.Equal(CheckStatus.NotImplemented, Run("distinct_count_min", @"{ ""table"": ""nope"", ""field"": ""lineweight"", ""min"": 2 }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("distinct_count_min", @"{ ""table"": ""layers"", ""field"": ""lineweight"" }", f).Status);
        }

        [Fact]
        public void ScaleInStandard()
        {
            var f = new Facts("autocad");
            var e = f.Table("entities");
            e.Add("V1").Set("type", "VIEWPORT").Set("vpScaleRatio", 48);
            e.Add("V2").Set("type", "VIEWPORT").Set("vpScaleRatio", 48.02);
            e.Add("V3").Set("type", "VIEWPORT").Set("vpScaleRatio", 50);
            e.Add("V4").Set("type", "VIEWPORT");
            e.Add("V5").Set("type", "VIEWPORT").Set("vpScaleRatio", -1);
            var p = @"{ ""table"": ""entities"", ""where"": { ""type"": ""VIEWPORT"" }, ""field"": ""vpScaleRatio"", ""tolerance"": 0.001 }";
            var r = Run("scale_in_standard", p, f, RealData.Us);
            Assert.Equal(new[] { "V3", "V4", "V5" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal("1/4\" = 1'-0\"", r.Findings[0].Data.GetStringOrNull("nearest"));
            Assert.Equal(48, r.Findings[0].Data["nearestRatio"]!.AsDouble());
            Assert.True(r.Findings[1].Data["nearest"]!.IsNull);
            // 1:50 is an ISO scale.
            r = Run("scale_in_standard", p, f, RealData.Iso);
            Assert.DoesNotContain("V3", r.Findings.Select(x => x.RecordId));
            Assert.Equal(CheckStatus.NotImplemented, Run("scale_in_standard", @"{ ""table"": ""vp"", ""field"": ""r"" }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("scale_in_standard", @"{ ""table"": ""entities"", ""field"": ""vpScaleRatio"", ""tolerance"": -1 }", f).Status);
            Assert.Equal(CheckStatus.Error, new CheckRunner(EvaluatorRegistry.CreateNeutral()).Run(Def("scale_in_standard", p), f, null).Status);
        }

        [Fact]
        public void UnitsMatchStandard()
        {
            var f = new Facts("autocad");
            f.Table("document").Add().Set("insUnits", "mm");
            var p = @"{ ""table"": ""document"", ""field"": ""insUnits"" }";
            var fix = CheckFix.ForOp("doc.setup", (JsonObject)JsonValue.Parse(@"{ ""insUnits"": ""{{expected}}"" }"));
            var r = Run("units_match_standard", p, f, RealData.Us, fix);
            Assert.Equal(CheckStatus.Fail, r.Status);
            Assert.Equal("in", r.Findings[0].Data.GetStringOrNull("expected"));
            Assert.Equal("in", r.Fixes[0].AsObject()["args"]!.AsObject().GetStringOrNull("insUnits"));
            Assert.Equal(CheckStatus.Pass, Run("units_match_standard", p, f, RealData.Iso).Status);
            // A standard without units: only unitless is a finding.
            var noUnits = StandardLoader.Parse(@"{ ""id"": ""x"", ""name"": ""x"" }");
            Assert.Equal(CheckStatus.Pass, Run("units_match_standard", p, f, noUnits).Status);
            var g = new Facts("autocad");
            g.Table("document").Add().Set("insUnits", "unitless");
            r = Run("units_match_standard", p, g, noUnits);
            Assert.Equal(CheckStatus.Fail, r.Status);
            Assert.True(r.Findings[0].Data["expected"]!.IsNull);
            Assert.Equal(CheckStatus.NotImplemented, Run("units_match_standard", @"{ ""table"": ""doc"", ""field"": ""insUnits"" }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("units_match_standard", @"{ ""table"": ""document"" }", f).Status);
        }

        [Fact]
        public void DimPrecisionMin()
        {
            var f = new Facts("autocad");
            var s = f.Table("styles");
            s.Add("D1").Set("kind", "dim").Set("stepMm", 6.35).Set("unitSystem", "imperial");
            s.Add("D2").Set("kind", "dim").Set("stepMm", 0.0992).Set("unitSystem", "imperial");
            s.Add("D3").Set("kind", "dim").Set("stepMm", 1).Set("unitSystem", "metric");
            s.Add("D4").Set("kind", "dim").Set("stepMm", 0.1).Set("unitSystem", "metric");
            s.Add("D5").Set("kind", "dim").Set("unitSystem", "metric");
            s.Add("D6").Set("kind", "dim").Set("stepMm", 1);
            s.Add("T1").Set("kind", "text");
            var p = @"{ ""table"": ""styles"", ""where"": { ""kind"": ""dim"" }, ""imperialMinIn"": 0.25, ""metricMinMm"": 1 }";
            var r = Run("dim_precision_min", p, f);
            Assert.Equal(new[] { "D2", "D4", "D5", "D6" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal(6.35, r.Findings[0].Data["minMm"]!.AsDouble(), 9);
            Assert.Equal("imperial", r.Findings[0].Data.GetStringOrNull("system"));
            Assert.StartsWith("Cannot judge precision", r.Findings[2].Message);
            Assert.True(r.Findings[3].Data["minMm"]!.IsNull);
            Assert.Equal(CheckStatus.NotImplemented, Run("dim_precision_min", @"{ ""table"": ""types"", ""imperialMinIn"": 0.25, ""metricMinMm"": 1 }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("dim_precision_min", @"{ ""table"": ""styles"", ""imperialMinIn"": 0.25 }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("dim_precision_min", @"{ ""table"": ""styles"", ""imperialMinIn"": 0, ""metricMinMm"": 1 }", f).Status);
        }

        [Fact]
        public void MatchesStandardEntry()
        {
            var f = new Facts("autocad");
            var s = f.Table("styles");
            s.Add("S1").Set("kind", "text").Set("name", "notes").Set("annotative", true).Set("font", "ARIAL");
            s.Add("S2").Set("kind", "text").Set("name", "TAGS").Set("annotative", false).Set("font", "romans.shx");
            s.Add("S3").Set("kind", "text").Set("name", "Standard").Set("annotative", false);
            s.Add("S4").Set("kind", "text").Set("name", "TITLES");
            var p = @"{ ""table"": ""styles"", ""where"": { ""kind"": ""text"" }, ""list"": ""textStyles"", ""properties"": { ""annotative"": ""annotative"", ""font"": ""font"" } }";
            var r = Run("matches_standard_entry", p, f);
            Assert.Equal(new[] { "S2", "S4" }, r.Findings.Select(x => x.RecordId));
            var d = r.Findings[0].Data;
            Assert.Equal("annotative", d.GetStringOrNull("field"));
            Assert.False(d["value"]!.AsBool());
            Assert.True(d["expected"]!.AsBool());
            Assert.Equal(2, d["mismatches"]!.AsArray().Count);
            Assert.True(r.Findings[1].Data["value"]!.IsNull);
            // Case-sensitive names: "notes" no longer names NOTES.
            r = Run("matches_standard_entry", @"{ ""table"": ""styles"", ""list"": ""textStyles"", ""ignoreCase"": false, ""properties"": { ""annotative"": ""annotative"" } }", f);
            Assert.Equal(new[] { "S2", "S4" }, r.Findings.Select(x => x.RecordId));

            // Layers: colours normalised, lineweight numbers; dimension style settings.
            var g = new Facts("autocad");
            var l = g.Table("layers");
            l.Add("L1").Set("name", "A-WALL").Set("colorKey", "6").Set("lineweight", 0.4);
            l.Add("L2").Set("name", "A-WALL-FIRE").Set("colorKey", "255,0,0").Set("lineweight", 0.2);
            l.Add("L3").Set("name", "X-NOT-LISTED").Set("colorKey", "1");
            r = Run("matches_standard_entry", @"{ ""table"": ""layers"", ""list"": ""layers"", ""properties"": { ""colorKey"": ""color"", ""lineweight"": ""lineweight"" } }", g);
            Assert.Equal(new[] { "L2" }, r.Findings.Select(x => x.RecordId));
            Assert.Equal("colorKey", r.Findings[0].Data.GetStringOrNull("field"));
            var h = new Facts("autocad");
            h.Table("styles").Add("D1").Set("kind", "dim").Set("name", "ARCH-ANNO").Set("dimlunit", 4).Set("dimdec", 3);
            r = Run("matches_standard_entry", @"{ ""table"": ""styles"", ""list"": ""dimStyles"", ""properties"": { ""dimlunit"": ""settings.DIMLUNIT"", ""dimdec"": ""settings.DIMDEC"", ""x"": ""settings.NOT_GIVEN"" } }", h);
            Assert.Equal("dimdec", r.Findings.Single().Data.GetStringOrNull("field"));
            Assert.Single(r.Findings[0].Data["mismatches"]!.AsArray());

            Assert.Equal(CheckStatus.NotImplemented, Run("matches_standard_entry", @"{ ""table"": ""nope"", ""list"": ""layers"", ""properties"": { ""a"": ""color"" } }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("matches_standard_entry", @"{ ""table"": ""styles"", ""list"": ""blocks"", ""properties"": { ""a"": ""color"" } }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("matches_standard_entry", @"{ ""table"": ""styles"", ""list"": ""layers"", ""properties"": { ""a"": ""font"" } }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("matches_standard_entry", @"{ ""table"": ""styles"", ""list"": ""layers"" }", f).Status);
        }

        [Theory]
        [InlineData("7", 7, true)]
        [InlineData("255,0,0", "#FF0000", true)]
        [InlineData(" 1 ", 1, true)]
        [InlineData("2", 1, false)]
        [InlineData("bylayer", 7, false)]
        public void ColoursCompareNormalised(string actual, object expected, bool same)
        {
            JsonValue e = expected is int i ? i : (JsonValue)(string)expected;
            Assert.Equal(same, SA.Core.Checks.MatchesStandardEntry.Same(actual, e, true));
        }

        [Fact]
        public void GroupCountOverThreshold()
        {
            var f = new Facts("revit");
            var w = f.Table("warnings");
            for (int i = 0; i < 3; i++) w.Add().Set("failureDefinitionId", "g1").Set("description", "Wall overlap " + i);
            for (int i = 0; i < 5; i++) w.Add().Set("failureDefinitionId", "g2").Set("description", "Room not enclosed");
            w.Add().Set("failureDefinitionId", "g3");
            w.Add().Set("description", "no id");
            var r = Run("group_count_over_threshold", @"{ ""table"": ""warnings"", ""key"": ""failureDefinitionId"", ""labelField"": ""description"", ""max"": 1 }", f);
            Assert.Equal(new[] { "5 x Room not enclosed", "3 x Wall overlap 0" }, r.Findings.Select(x => x.Message));
            var d = r.Findings[0].Data;
            Assert.Equal("failureDefinitionId", d.GetStringOrNull("field"));
            Assert.Equal("g2", d.GetStringOrNull("value"));
            Assert.Equal(5, d["count"]!.AsInt64());
            // Without a label field the key value is the label; max 0 reports every group.
            r = Run("group_count_over_threshold", @"{ ""table"": ""warnings"", ""key"": ""failureDefinitionId"", ""max"": 0 }", f);
            Assert.Equal(new[] { "5 x g2", "3 x g1", "1 x g3" }, r.Findings.Select(x => x.Message));
            Assert.Equal(CheckStatus.Pass, Run("group_count_over_threshold", @"{ ""table"": ""warnings"", ""key"": ""failureDefinitionId"", ""max"": 5 }", f).Status);
            Assert.Equal(CheckStatus.NotImplemented, Run("group_count_over_threshold", @"{ ""table"": ""x"", ""key"": ""k"", ""max"": 0 }", f).Status);
            Assert.Equal(CheckStatus.Error, Run("group_count_over_threshold", @"{ ""table"": ""warnings"", ""max"": 0 }", f).Status);
        }

        [Fact]
        public void ManualReviewIsAFlaggedFailByDefaultAndAReviewWhenAsked()
        {
            var empty = new Facts("revit");
            var def = Def("manual_review", @"{ ""instructions"": ""Look at it."" }", title: "Nothing exploded");
            var r = Runner.Run(def, empty, null);
            Assert.Equal(CheckStatus.Fail, r.Status);
            Assert.True(r.IsManualReview);
            Assert.Equal("Look at it.", r.ReviewInstructions);
            var f = r.Findings.Single();
            Assert.Null(f.RecordId);
            Assert.Equal("Review by eye: Nothing exploded", f.Message);
            Assert.True(f.Data["review"]!.AsBool());
            Assert.Equal("manual", f.Data.GetStringOrNull("field"));
            var json = r.ToJson();
            Assert.Equal("fail", json.GetStringOrNull("status"));
            Assert.True(json["review"]!.AsBool());
            var back = CheckResult.Parse(json);
            Assert.True(back.IsManualReview);
            Assert.Equal(CheckStatus.Fail, back.Status);

            // Instructions fall back to the manual fix's.
            r = Runner.Run(Def("manual_review", "{}", CheckFix.Manual("Ask the user.")), empty, null);
            Assert.Equal("Ask the user.", r.Findings[0].Data.GetStringOrNull("instructions"));
            Assert.Equal("Ask the user.", r.Fixes[0].AsObject().GetStringOrNull("manual"));

            var reviewRunner = new CheckRunner(EvaluatorRegistry.CreateNeutral()) { ReviewStatus = true };
            r = reviewRunner.Run(def, empty, null);
            Assert.Equal(CheckStatus.Review, r.Status);
            Assert.Equal("Nothing exploded", r.Title);
            Assert.Equal("review", r.ToJson().GetStringOrNull("status"));
            Assert.Equal(CheckStatus.Review, CheckResult.Parse(r.ToJson()).Status);
            Assert.Equal(CheckStatus.Review, CheckResult.ParseStatus("review"));
            // Automatic checks are untouched by the option.
            Assert.Equal(CheckStatus.NotImplemented, reviewRunner.Run(Def("field_equals", @"{ ""table"": ""x"", ""field"": ""a"", ""equals"": 1 }"), empty, null).Status);

            Assert.Equal(CheckStatus.Error, Runner.Run(Def("manual_review", @"{ ""instructions"": 3 }"), empty, null).Status);
        }

        [Fact]
        public void SummaryCountsReviewItemsApart()
        {
            var facts = new Facts("autocad");
            facts.Table("t").Add("1").Set("a", 1);
            var results = Runner.RunAll(new[]
            {
                Def("manual_review", "{}"),
                Def("field_equals", @"{ ""table"": ""t"", ""field"": ""a"", ""equals"": 1 }"),
                Def("field_equals", @"{ ""table"": ""t"", ""field"": ""a"", ""equals"": 2 }"),
                Def("field_equals", @"{ ""table"": ""missing"", ""field"": ""a"", ""equals"": 2 }"),
                Def("field_equals", @"{ ""table"": ""t"", ""field"": ""a"" }"),
            }, facts, null);
            var s = CheckSummary.Of(results);
            Assert.Equal((5, 1, 1, 1, 1, 1), (s.Total, s.Pass, s.Fail, s.Review, s.NotImplemented, s.Error));
            Assert.Equal(1, s.ToJson()["review"]!.AsInt64());
            CheckSummary.Split(results, out var auto, out var review);
            Assert.Equal(4, auto.Count);
            Assert.Single(review);
        }
    }
}
