using System.Collections.Generic;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Json;
using Xunit;

namespace SA.Core.Tests
{
    public class ChecksTests
    {
        private static CheckDefinition Def(string evaluator, string paramsJson, CheckFix? fix = null, CheckApp app = CheckApp.Both, string id = "t.check") =>
            new CheckDefinition(id, app, "Test", Severity.Warning, evaluator, (JsonObject)JsonValue.Parse(paramsJson), fix: fix);

        private static readonly CheckRunner Runner = new CheckRunner(EvaluatorRegistry.CreateNeutral());

        private static Facts AcadFacts()
        {
            var f = new Facts("autocad");
            f.Meta.Add("units.length", "mm");
            var layers = f.Table("layers");
            foreach (var n in new[] { "0", "A-WALL", "A-WALX", "Walls", "A-DOOR", "Defpoints" }) layers.Add().Set("name", n);
            var ents = f.Table("entities");
            ents.Add("1A").Set("color", "bylayer").Set("layer", "A-WALL");
            ents.Add("1B").Set("color", 1).Set("layer", "A-WALL");
            ents.Add("1C").Set("layer", "A-DOOR");
            var lines = f.Table("lines");
            lines.Add("L1").Set("layer", "A-WALL").Set("x1", 0).Set("y1", 0).Set("x2", 1000).Set("y2", 0);
            lines.Add("L2").Set("layer", "A-WALL").Set("x1", 0.0002).Set("y1", 0).Set("x2", 1000).Set("y2", 0);
            lines.Add("L3").Set("layer", "A-DOOR").Set("x1", 0).Set("y1", 0).Set("x2", 1000).Set("y2", 0);
            lines.Add("L4").Set("layer", "A-WALL").Set("x1", 0).Set("y1", 0).Set("x2", 1000).Set("y2", 0);
            var text = f.Table("text");
            text.Add("T1").Set("height", 250).Set("scale", 100);
            text.Add("T2").Set("height", 300).Set("scale", 100);
            text.Add("T3").Set("height", 125).Set("scale", 50);
            text.Add("T4").Set("height", 2.5);
            f.Table("sheets").Add("S1").Set("number", "A-101");
            f.Table("sheets").Add("S2").Set("number", "A101");
            var tb = f.Table("titleblocks");
            tb.Add("Layout1").Set("SHEET_TITLE", "Plan");
            tb.Add("Layout2").Set("SHEET_TITLE", " ");
            tb.Add("Layout3");
            var w = f.Table("warnings");
            for (int i = 0; i < 5; i++) w.Add();
            return f;
        }

        [Fact]
        public void ExampleCatalogueLoadsAndSelects()
        {
            var cat = CheckCatalog.LoadFile(DataFiles.Example("checks.example.json"));
            Assert.Equal(9, cat.Checks.Count);
            var standards = cat.Select(CheckApp.AutoCad, null, new[] { "standards" }, out var unknown);
            Assert.Equal(new[] { "layers.naming", "layers.in_standard", "sheets.numbering" }, standards.Select(c => c.Id));
            Assert.Empty(unknown);
            var byId = cat.Select(CheckApp.Revit, new[] { "model.warnings", "layers.naming", "nope.x" }, null, out unknown);
            Assert.Equal(new[] { "model.warnings" }, byId.Select(c => c.Id));
            Assert.Equal(new[] { "layers.naming", "nope.x" }, unknown);
            Assert.Equal(4, cat.Select(CheckApp.Revit, null, null, out _).Count);
            Assert.True(cat.Find("layers.in_standard")!.Fix!.IsManual);
        }

        [Fact]
        public void ExampleChecksRunAgainstFactsWithNeverAFalsePass()
        {
            var cat = CheckCatalog.LoadFile(DataFiles.Example("checks.example.json"));
            var results = Runner.RunAll(cat.Select(CheckApp.AutoCad, null, null, out _), AcadFacts(), DataFiles.Us).ToDictionary(r => r.Id);
            Assert.Equal(CheckStatus.Fail, results["layers.naming"].Status);
            Assert.Equal(new[] { "A-WALX", "Walls" }, results["layers.naming"].Findings.Select(f => f.Data["value"]!.AsString()));
            Assert.Equal(CheckStatus.Fail, results["entities.bylayer_color"].Status);
            Assert.Equal(new[] { "1B", "1C" }, results["entities.bylayer_color"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "S2" }, results["sheets.numbering"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "Layout2", "Layout3" }, results["sheets.title_fields"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "L2", "L4" }, results["geometry.duplicate_lines"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.NotImplemented, results["walls.open_ends"].Status);
            Assert.Contains("wall_open_ends", results["walls.open_ends"].Message);
            // The fix template carries the record id.
            var fix = results["sheets.title_fields"].Fixes[0].AsObject();
            Assert.Equal("titleblock.fill", fix.GetStringOrNull("op"));
            Assert.Equal("Layout2", fix["args"]!.AsObject()["layouts"]!.AsArray()[0].AsString());
        }

        [Fact]
        public void MissingTableIsNotImplementedAndBadParamsAreErrors()
        {
            var f = new Facts("revit");
            Assert.Equal(CheckStatus.NotImplemented, Runner.Run(Def("field_missing", "{\"table\":\"rooms\",\"field\":\"name\"}"), f, null).Status);
            f.Table("rooms");
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("field_missing", "{\"table\":\"rooms\",\"field\":\"name\"}"), f, null).Status);
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("field_missing", "{\"field\":\"name\"}"), f, null).Status);
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("names_match_scheme", "{\"table\":\"rooms\",\"field\":\"name\",\"scheme\":\"layer\"}"), f, null).Status);
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("field_matches_pattern", "{\"table\":\"rooms\",\"field\":\"name\",\"pattern\":\"[\"}"), f, null).Status);
            Assert.Equal(CheckStatus.NotImplemented, Runner.Run(Def("no_such_evaluator", "{}"), f, null).Status);
        }

        [Fact]
        public void FieldMatchesPatternAndNegate()
        {
            var f = AcadFacts();
            var r = Runner.Run(Def("field_matches_pattern", "{\"table\":\"layers\",\"field\":\"name\",\"pattern\":\"[A-Z]-[A-Z]{4}\"}"), f, null);
            Assert.Equal(new[] { "0", "Walls", "Defpoints" }, r.Findings.Select(x => x.Data["value"]!.AsString()));
            var neg = Runner.Run(Def("field_matches_pattern", "{\"table\":\"layers\",\"field\":\"name\",\"pattern\":\"walls\",\"ignoreCase\":true,\"negate\":true}"), f, null);
            Assert.Equal("Walls", neg.Findings.Single().Data["value"]!.AsString());
            var std = Runner.Run(Def("field_matches_pattern", "{\"table\":\"layers\",\"field\":\"name\",\"standardPattern\":\"blockName\"}"), f, DataFiles.Us);
            Assert.Equal(new[] { "0", "Walls", "Defpoints" }, std.Findings.Select(x => x.Data["value"]!.AsString()));
        }

        [Fact]
        public void DimOverrideValueSplitsWrongFromTypedNumbers()
        {
            var f = new Facts("autocad");
            var d = f.Table("entities");
            d.Add("D1").Set("type", "DIMENSION").Set("textOverride", "%%c18000").Set("measurement", 18000.0003).Set("dimLfac", 1);
            d.Add("D2").Set("type", "DIMENSION").Set("textOverride", "{\\H2.5x;\\U+2205}18000").Set("measurement", 18000).Set("dimLfac", 1);
            d.Add("D3").Set("type", "DIMENSION").Set("textOverride", "10'-0\"").Set("measurement", 100).Set("dimLfac", 1);
            d.Add("D4").Set("type", "DIMENSION").Set("textOverride", "<> TYP").Set("measurement", 100).Set("dimLfac", 1);
            d.Add("D5").Set("type", "DIMENSION").Set("textOverride", "").Set("measurement", 100).Set("dimLfac", 1);
            d.Add("D6").Set("type", "DIMENSION").Set("textOverride", "3.25").Set("measurement", 3.26).Set("dimLfac", 1);
            d.Add("D7").Set("type", "DIMENSION").Set("textOverride", "90%%d").Set("measurement", System.Math.PI / 2).Set("dimLfac", 1);
            d.Add("D8").Set("type", "DIMENSION").Set("textOverride", "VARIES");
            var differs = Runner.Run(Def("dim_override_value", "{\"table\":\"entities\",\"where\":{\"type\":\"DIMENSION\"},\"match\":\"differs\"}"), f, null);
            Assert.Equal(new[] { "D3", "D6", "D8" }, differs.Findings.Select(x => x.RecordId));
            var equals = Runner.Run(Def("dim_override_value", "{\"table\":\"entities\",\"where\":{\"type\":\"DIMENSION\"},\"match\":\"equals\"}"), f, null);
            Assert.Equal(new[] { "D1", "D2", "D7" }, equals.Findings.Select(x => x.RecordId));
        }

        [Fact]
        public void CountOverThreshold()
        {
            var f = AcadFacts();
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("count_over_threshold", "{\"table\":\"warnings\",\"max\":5}"), f, null).Status);
            var r = Runner.Run(Def("count_over_threshold", "{\"table\":\"warnings\",\"max\":4}"), f, null);
            Assert.Equal(5, r.Findings.Single().Data["count"]!.AsInt64());
            Assert.Equal(CheckStatus.Fail, Runner.Run(Def("count_over_threshold", "{\"table\":\"entities\",\"max\":1,\"where\":{\"layer\":\"A-WALL\"}}"), f, null).Status);
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("count_over_threshold", "{\"table\":\"entities\",\"max\":1,\"where\":{\"layer\":\"A-DOOR\"}}"), f, null).Status);
        }

        [Fact]
        public void FieldEqualsWithInAndNegate()
        {
            var f = AcadFacts();
            Assert.Equal(new[] { "1B" }, Runner.Run(Def("field_equals", "{\"table\":\"entities\",\"field\":\"color\",\"equals\":1}"), f, null).Findings.Select(x => x.RecordId));
            Assert.Equal(new[] { "1A", "1B" }, Runner.Run(Def("field_equals", "{\"table\":\"entities\",\"field\":\"color\",\"in\":[1,\"bylayer\"]}"), f, null).Findings.Select(x => x.RecordId));
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("field_equals", "{\"table\":\"entities\",\"field\":\"color\"}"), f, null).Status);
        }

        [Fact]
        public void DuplicatesNeedEveryKeyAndRespectPrecision()
        {
            var f = AcadFacts();
            var exact = Runner.Run(Def("duplicates_by_key", "{\"table\":\"lines\",\"keys\":[\"layer\",\"x1\",\"y1\",\"x2\",\"y2\"]}"), f, null);
            Assert.Equal(new[] { "L4" }, exact.Findings.Select(x => x.RecordId));
            Assert.Equal("L1", exact.Findings[0].Data["duplicateOf"]!.AsString());
            var noKey = Runner.Run(Def("duplicates_by_key", "{\"table\":\"entities\",\"keys\":[\"color\"]}"), f, null);
            Assert.Equal(CheckStatus.Pass, noKey.Status);
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("duplicates_by_key", "{\"table\":\"lines\",\"keys\":[]}"), f, null).Status);
        }

        [Fact]
        public void ValueInStandardList()
        {
            var f = AcadFacts();
            var r = Runner.Run(Def("value_in_standard_list", "{\"table\":\"layers\",\"field\":\"name\",\"list\":\"layers\",\"ignore\":[\"0\",\"Defpoints\"]}"), f, DataFiles.Us);
            Assert.Equal(new[] { "A-WALX", "Walls" }, r.Findings.Select(x => x.Data["value"]!.AsString()));
            var codes = Runner.Run(Def("value_in_standard_list", "{\"table\":\"sheets\",\"field\":\"number\",\"list\":\"codeList:disciplines\"}"), f, DataFiles.Us);
            Assert.Equal(2, codes.Findings.Count);
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("value_in_standard_list", "{\"table\":\"layers\",\"field\":\"name\",\"list\":\"codeList:none\"}"), f, DataFiles.Us).Status);
        }

        [Fact]
        public void FieldRange()
        {
            var f = new Facts();
            var t = f.Table("rooms");
            t.Add("R1").Set("area", 12.5);
            t.Add("R2").Set("area", 0);
            t.Add("R3").Set("area", "n/a");
            var r = Runner.Run(Def("field_range", "{\"table\":\"rooms\",\"field\":\"area\",\"min\":0.01}"), f, null);
            Assert.Equal(new[] { "R2", "R3" }, r.Findings.Select(x => x.RecordId));
        }

        [Fact]
        public void TextHeightAgainstTheStandard()
        {
            var f = AcadFacts();
            var r = Runner.Run(Def("text_height_standard", "{\"table\":\"text\"}"), f, DataFiles.Iso);
            Assert.Equal(new[] { "T2", "T4" }, r.Findings.Select(x => x.RecordId));
            var t2 = r.Findings[0];
            Assert.Equal(3.0, t2.Data["plottedMm"]!.AsDouble(), 9);
            Assert.Equal(250, t2.Data["expectedModelHeight"]!.AsDouble(), 9);
            var noUnit = new Facts();
            noUnit.Table("text").Add("x").Set("height", 1).Set("scale", 1);
            Assert.Equal(CheckStatus.NotImplemented, Runner.Run(Def("text_height_standard", "{\"table\":\"text\"}"), noUnit, DataFiles.Iso).Status);
        }

        [Fact]
        public void FindingsAreCapped()
        {
            var f = new Facts();
            var t = f.Table("x");
            for (int i = 0; i < 20; i++) t.Add(i.ToString()).Set("v", 1);
            var runner = new CheckRunner(EvaluatorRegistry.CreateNeutral()) { MaxFindingsPerCheck = 5 };
            var r = runner.Run(Def("field_equals", "{\"table\":\"x\",\"field\":\"v\",\"equals\":1}"), f, null);
            Assert.Equal(5, r.Findings.Count);
            Assert.Contains("20 findings", r.Message);
        }

        [Fact]
        public void FixTemplatesKeepTypes()
        {
            var fix = CheckFix.ForOp("entity.setprops", (JsonObject)JsonValue.Parse("{\"handles\":[\"{{recordId}}\"],\"color\":\"{{value}}\",\"note\":\"was {{value}} on {{layer}}\"}"));
            var args = fix.Instantiate(new Finding("x", "2A", "entities", new JsonObject().Add("value", 3).Add("layer", "A-WALL")))!;
            Assert.Equal("2A", args["handles"]!.AsArray()[0].AsString());
            Assert.Equal(3, args["color"]!.AsInt64());
            Assert.Equal("was 3 on A-WALL", args["note"]!.AsString());
            Assert.Null(CheckFix.Manual("do it").Instantiate(new Finding("x")));
        }

        [Theory]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"Bad Id\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\"}]}", "$.checks[0].id")]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"a.b\",\"app\":\"archicad\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\"}]}", "$.checks[0].app")]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"a.b\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"high\",\"evaluator\":\"e\"}]}", "$.checks[0].severity")]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"a.b\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\"},{\"id\":\"a.b\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\"}]}", "$.checks[1].id")]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"a.b\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\",\"fix\":{\"kind\":\"auto\"}}]}", "$.checks[0].fix.kind")]
        [InlineData("{\"version\":2,\"checks\":[]}", "$.version")]
        [InlineData("{\"version\":1,\"checks\":[{\"id\":\"a.b\",\"app\":\"both\",\"title\":\"t\",\"severity\":\"info\",\"evaluator\":\"e\",\"params\":{},\"extra\":1}]}", "$.checks[0].extra")]
        public void CatalogueErrorsNameThePath(string json, string path)
        {
            var e = Assert.Throws<CheckFormatException>(() => CheckCatalog.Parse(json));
            Assert.StartsWith(path, e.Message);
        }

        [Fact]
        public void FactsAndResultsRoundTrip()
        {
            var f = AcadFacts();
            var back = Facts.Parse(JsonValue.Parse(f.ToJson().ToJson()));
            Assert.Equal(f.Tables.Select(t => t.Name), back.Tables.Select(t => t.Name));
            Assert.Equal("mm", back.Meta.GetStringOrNull("units.length"));
            Assert.Equal(4, back.Table("lines").Records.Count);
            var r = Runner.Run(Def("field_equals", "{\"table\":\"entities\",\"field\":\"color\",\"equals\":1}"), f, null);
            var rb = CheckResult.Parse(JsonValue.Parse(r.ToJson().ToJson()));
            Assert.True(JsonValue.DeepEquals(r.ToJson(), rb.ToJson()));
        }
    }
}
