using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Standards;
using SA.Core.Units;
using Xunit;

namespace SA.Core.Tests
{
    internal static class DataFiles
    {
        /// <summary>The data/ folder next to engine/, found by walking up from the test binaries.</summary>
        public static string DataDir
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "data", "schemas", "examples");
                    if (Directory.Exists(candidate)) return Path.Combine(dir.FullName, "data");
                    dir = dir.Parent;
                }
                throw new DirectoryNotFoundException("data/schemas/examples not found above " + AppContext.BaseDirectory);
            }
        }

        public static string Example(string name) => Path.Combine(DataDir, "schemas", "examples", name);

        private static ProjectStandard? _us, _iso;
        public static ProjectStandard Us => _us ??= StandardLoader.LoadFile(Example("standard.us.example.json"));
        public static ProjectStandard Iso => _iso ??= StandardLoader.LoadFile(Example("standard.iso.example.json"));
    }

    public class StandardsTests
    {
        private static ProjectStandard Us => DataFiles.Us;
        private static ProjectStandard Iso => DataFiles.Iso;

        [Fact]
        public void ExampleStandardsLoadAndAreSelfConsistent()
        {
            Assert.Equal("us", Us.LayerScheme!.Kind);
            Assert.Equal("iso13567", Iso.LayerScheme!.Kind);
            Assert.Empty(Us.SelfCheck());
            Assert.Empty(Iso.SelfCheck());
            Assert.Equal(LengthUnit.Inch, Us.LengthUnit);
            Assert.Equal(15, Us.Layers.Count);
            Assert.Null(Us.FindLayer("G-ANNO-NPLT")!.Lineweight);
            Assert.False(Us.FindLayer("g-anno-nplt")!.Plot);
            Assert.True(Us.FindLayer("G-ANNO-NPLT")!.Color.IsTrueColor);
            Assert.Equal(0.5, Us.FindLayer("A-WALL")!.Lineweight);
            Assert.Equal(2.5, Iso.DimStyles.Single().Settings["DIMTXT"]!.AsDouble());
        }

        [Theory]
        [InlineData("A-WALL")]
        [InlineData("A-WALL-FULL")]
        [InlineData("A-WALL-FULL-PATT")]
        [InlineData("A-WALL-FULL-PATT-N")]
        [InlineData("A-WALL-D")]
        [InlineData("AD-WALL")]
        [InlineData("S-GRID-IDEN")]
        [InlineData("G-ANNO-TTLB")]
        public void UsLayerNamesValid(string name)
        {
            var r = Us.ParseLayerName(name);
            Assert.True(r.Valid, r.Explain());
        }

        [Theory]
        [InlineData("A-WAL", "major", "bad_length")]
        [InlineData("A-WALX", "major", "unknown_code")]
        [InlineData("X-WALL", "discipline", "unknown_code")]
        [InlineData("AX-WALL", "modifier", "unknown_code")]
        [InlineData("ABC-WALL", "discipline", "bad_length")]
        [InlineData("a-wall", "", "bad_case")]
        [InlineData("A", "major", "missing_field")]
        [InlineData("A--WALL", "", "bad_characters")]
        [InlineData("A-WALL-", "", "bad_characters")]
        [InlineData("A-WALL-FULL-PATT-OTLN", "minor", "too_many_fields")]
        [InlineData("A-WALL-ZZZZ", "minor1", "unknown_code")]
        [InlineData("A-WALL-FUL", "minor1", "bad_length")]
        [InlineData("A-WALL-X", "status", "unknown_code")]
        [InlineData("", "", "empty")]
        public void UsLayerNamesInvalid(string name, string field, string code)
        {
            var r = Us.ParseLayerName(name);
            Assert.False(r.Valid);
            Assert.Contains(r.Problems, p => p.Field == field && p.Code == code);
        }

        [Fact]
        public void UsLayerFieldsAreReported()
        {
            var r = Us.ParseLayerName("AD-WALL-FULL-PATT-N");
            Assert.Equal("A", r["discipline"]);
            Assert.Equal("D", r["modifier"]);
            Assert.Equal("WALL", r["major"]);
            Assert.Equal("FULL", r["minor1"]);
            Assert.Equal("PATT", r["minor2"]);
            Assert.Equal("N", r["status"]);
        }

        [Fact]
        public void IsoNameMayStopAfterTheMandatoryFields()
        {
            Assert.True(Iso.ParseLayerName("A_21____E_").Valid);
            var r = Iso.ParseLayerName("A_21");
            Assert.Contains(r.Problems, p => p.Code == "bad_length" && p.Field == "element");
        }

        [Theory]
        [InlineData("A_22____E_R")]
        [InlineData("A_221___E_")]
        [InlineData("S_G25___E_")]
        [InlineData("A_21____E_N____")]
        [InlineData("A_21____E__ZN01")]
        [InlineData("A_21____E_N____2PXK1MYTAG")]
        public void IsoLayerNamesValid(string name)
        {
            var r = Iso.ParseLayerName(name);
            Assert.True(r.Valid, r.Explain());
        }

        [Theory]
        [InlineData("Q_21____E_", "agent", "unknown_code")]
        [InlineData("A_99____E_", "element", "unknown_code")]
        [InlineData("A_21____Q_", "presentation", "unknown_code")]
        [InlineData("A_21____E_X", "status", "unknown_code")]
        [InlineData("A_21____", "presentation", "missing_field")]
        [InlineData("A_21____E", "presentation", "bad_length")]
        [InlineData("__21____E_", "agent", "missing_field")]
        [InlineData("A_21-___E_", "element", "bad_characters")]
        [InlineData("a_21____e_", "", "bad_case")]
        [InlineData("A_21____E_N____2PXK1THISUSERFIELDISTOOLONG", "userDefined", "bad_length")]
        public void IsoLayerNamesInvalid(string name, string field, string code)
        {
            var r = Iso.ParseLayerName(name);
            Assert.False(r.Valid);
            Assert.Contains(r.Problems, p => p.Field == field && p.Code == code);
        }

        [Fact]
        public void IsoFieldValues()
        {
            var r = Iso.ParseLayerName("A_22____E_R____2");
            Assert.Equal("A_", r["agent"]);
            Assert.Equal("22____", r["element"]);
            Assert.Equal("R", r["status"]);
            Assert.Null(r["sector"]);
            Assert.Equal("2", r["phase"]);
        }

        [Theory]
        [InlineData("A-101", true)]
        [InlineData("G-001", true)]
        [InlineData("A101", false)]
        [InlineData("A-701", false)]
        [InlineData("A-1011", false)]
        [InlineData("X-101", false)]
        [InlineData("a-101", false)]
        public void UsSheetIds(string id, bool valid)
        {
            var r = Us.ParseSheetId(id);
            Assert.True(valid == r.Valid, r.Explain());
            if (valid)
            {
                Assert.Equal(id.Substring(0, 1), r["discipline"]);
                Assert.Equal(id.Substring(3), r["sequence"]);
            }
        }

        [Fact]
        public void SheetIdErrorNamesTheField()
        {
            var r = Us.ParseSheetId("A-701");
            Assert.Equal("type", r.Problems.Single().Field);
            Assert.Contains("'7' is not an allowed code", r.Problems.Single().Message);
        }

        [Theory]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A-1001.dwg", true)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A-1001.PDF", true)]
        [InlineData("PRJ01-ABC-ZZ-XX-M3-A-000001.rvt", true)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A-1001.docx", false)]
        [InlineData("PRJ01-ABC-ZZ-01-XX-A-1001.dwg", false)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-Q-1001.dwg", false)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A-101.dwg", false)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A.dwg", false)]
        [InlineData("PRJ01_ABC-ZZ-01-DR-A-1001.dwg", false)]
        [InlineData("PRJ01-ABC-ZZ-01-DR-A-1001-X.dwg", false)]
        [InlineData("prj01-abc-zz-01-dr-a-1001.dwg", false)]
        public void IsoContainerNames(string name, bool valid)
        {
            var r = Iso.ParseFileName(name);
            Assert.True(valid == r.Valid, r.Explain());
        }

        [Fact]
        public void FileNameFieldsAndFormat()
        {
            var r = Iso.ParseFileName("PRJ01-ABC-ZZ-01-DR-A-1001.dwg");
            Assert.Equal("ABC", r["originator"]);
            Assert.Equal("DR", r["type"]);
            Assert.Equal(".dwg", r["extension"]);
            var built = Iso.FileNaming!.Format(new Dictionary<string, string>
            {
                ["project"] = "PRJ01", ["originator"] = "ABC", ["volume"] = "ZZ", ["level"] = "02", ["type"] = "DR", ["role"] = "S", ["number"] = "2001",
            }, ".pdf");
            Assert.True(built.Valid, built.Explain());
            Assert.Equal("PRJ01-ABC-ZZ-02-DR-S-2001.pdf", built.Name);
            var missing = Iso.FileNaming.Format(new Dictionary<string, string> { ["project"] = "PRJ01" });
            Assert.Equal("missing_field", missing.Problems.Single().Code);
            var bad = Iso.ParseFileName("PRJ01-ABC-ZZ-01-DR-Q-1001.dwg");
            Assert.Equal("role", bad.Problems.Single().Field);
        }

        [Fact]
        public void NamingRuleWithoutSeparatorsAndOptionalFieldsBacktracks()
        {
            var std = StandardLoader.Parse(@"{ ""id"": ""t"", ""name"": ""t"",
              ""sheetNumbering"": { ""fields"": [
                { ""name"": ""disc"", ""codes"": [""A"", ""AD"", ""S""] },
                { ""name"": ""level"", ""pattern"": ""[0-9]"" },
                { ""name"": ""seq"", ""pattern"": ""[0-9]{2}"" },
                { ""name"": ""suffix"", ""separator"": ""."", ""pattern"": ""[A-Z]"", ""optional"": true } ] } }");
            var r = std.ParseSheetId("AD101");
            Assert.True(r.Valid, r.Explain());
            Assert.Equal("AD", r["disc"]);
            Assert.Equal("1", r["level"]);
            Assert.True(std.ParseSheetId("A101.B").Valid);
            Assert.Equal("B", std.ParseSheetId("A101.B")["suffix"]);
            Assert.False(std.ParseSheetId("A101.").Valid);
            Assert.False(std.ParseSheetId("AX101").Valid);
        }

        [Fact]
        public void StatusRevisionScalesAndTextHeights()
        {
            Assert.True(Iso.IsStatusCode("S2"));
            Assert.False(Iso.IsStatusCode("S9"));
            Assert.Equal("preliminary", Iso.MatchRevision("P01.1")!.Name);
            Assert.Equal("contractual", Iso.MatchRevision("C02")!.Name);
            Assert.Null(Iso.MatchRevision("P1"));
            Assert.Equal(48, Us.FindScale("1/4\" = 1'-0\"")!.Ratio, 9);
            Assert.Equal(8, Us.FindScale("1 1/2\" = 1'-0\"")!.Ratio, 9);
            Assert.Equal(1250, Iso.FindScale("1:1250")!.Ratio);
            Assert.Equal(250, Iso.ModelTextHeight("body", 100, LengthUnit.Millimetre), 9);
            Assert.Equal(0.25, Iso.ModelTextHeight("body", 100, LengthUnit.Metre), 9);
            Assert.Equal(4.5, Us.ModelTextHeight("body", 48, LengthUnit.Inch), 9);
            Assert.Throws<KeyNotFoundException>(() => Us.ModelTextHeight("nope", 48, LengthUnit.Inch));
            Assert.Equal("SHEET_NO", Us.TitleBlockFields.First().Name);
            Assert.Matches(Iso.Patterns["viewName"], "01 - Ground Floor");
        }

        [Theory]
        [InlineData(@"{ ""id"": ""x"" }", "$.name")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""layres"": [] }", "$.layres")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""layers"": [ { ""name"": ""A"", ""color"": 300 } ] }", "$.layers[0].color")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""layers"": [ { ""name"": ""A"", ""color"": 1, ""lineweight"": 0.33 } ] }", "$.layers[0].lineweight")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""layerNaming"": { ""scheme"": ""din"" } }", "$.layerNaming.scheme")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""sheetNumbering"": { ""fields"": [ { ""name"": ""a"", ""codeList"": ""missing"" } ] } }", "$.sheetNumbering.fields[0].codeList")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""scales"": [ { ""name"": ""1:100"", ""ratio"": 50 } ] }", "$.scales[0].ratio")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""scales"": [ { ""name"": ""Large"" } ] }", "$.scales[0].name")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""codeLists"": { ""a"": [ ""X"", ""X"" ] } }", "$.codeLists.a[1]")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""revisionCodes"": { ""formats"": [ { ""name"": ""p"", ""pattern"": ""P[0-9"" } ] } }", "$.revisionCodes.formats[0].pattern")]
        [InlineData(@"{ ""id"": ""x"", ""name"": ""x"", ""layerNaming"": { ""scheme"": ""iso13567"", ""fields"": [ { ""name"": ""a"", ""length"": 2 }, { ""name"": ""b"", ""length"": 2, ""required"": true } ] } }", "$.layerNaming.fields[1].required")]
        public void LoaderErrorsNameThePath(string json, string path)
        {
            var e = Assert.Throws<StandardFormatException>(() => StandardLoader.Parse(json));
            Assert.StartsWith(path, e.Message);
        }
    }
}
