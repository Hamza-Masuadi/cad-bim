using System.Collections.Generic;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>Core items of the integration pass: skipMissing, incomplete facts, verify.checksFile, standard roles and view naming.</summary>
    public class IntegrationFixTests
    {
        private static readonly CheckRunner Runner = new CheckRunner(EvaluatorRegistry.CreateNeutral());

        private static CheckDefinition Def(string evaluator, string paramsJson, string id = "t.check") =>
            new CheckDefinition(id, CheckApp.Both, "Test", Severity.Warning, evaluator, (JsonObject)JsonValue.Parse(paramsJson));

        // ------------------------------------------------------------------ field_range skipMissing (rv-031)

        private static Facts CleanRevitElements()
        {
            var f = new Facts("revit");
            var e = f.Table("elements");
            e.Add("1").Set("category", "OST_Walls").Set("levelOffsetAbsMm", 0);
            e.Add("2").Set("category", "OST_Furniture").Set("levelOffsetAbsMm", 150);
            e.Add("3").Set("category", "OST_Doors");
            e.Add("4").Set("category", "OST_Rooms");
            e.Add("5").Set("category", "OST_Levels");
            return f;
        }

        [Fact]
        public void FieldRangeCountsAMissingFieldUnlessSkipMissing()
        {
            var f = CleanRevitElements();
            var strict = Runner.Run(Def("field_range", @"{ ""table"": ""elements"", ""field"": ""levelOffsetAbsMm"", ""max"": 200 }"), f, null);
            Assert.Equal(CheckStatus.Fail, strict.Status);
            Assert.Equal(new[] { "3", "4", "5" }, strict.Findings.Select(x => x.RecordId));

            var skip = Runner.Run(Def("field_range", @"{ ""table"": ""elements"", ""field"": ""levelOffsetAbsMm"", ""max"": 200, ""skipMissing"": true }"), f, null);
            Assert.Equal(CheckStatus.Pass, skip.Status);

            f.Table("elements").Add("6").Set("category", "OST_Floors").Set("levelOffsetAbsMm", "high");
            f.Table("elements").Add("7").Set("category", "OST_Floors").Set("levelOffsetAbsMm", 250);
            var mixed = Runner.Run(Def("field_range", @"{ ""table"": ""elements"", ""field"": ""levelOffsetAbsMm"", ""max"": 200, ""skipMissing"": true }"), f, null);
            Assert.Equal(new[] { "6", "7" }, mixed.Findings.Select(x => x.RecordId));
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("field_range", @"{ ""table"": ""elements"", ""field"": ""levelOffsetAbsMm"", ""max"": 200, ""skipMissing"": ""yes"" }"), f, null).Status);
        }

        [Fact]
        public void ShippedOffsetFromLevelCheckPassesOnACleanModel()
        {
            var check = RealData.Revit.Find("rv-031.offset-from-level")!;
            Assert.Equal(CheckStatus.Pass, Runner.Run(check, CleanRevitElements(), RealData.Iso).Status);
        }

        [Fact]
        public void ShippedBlockPropertiesCheckSkipsXrefDependentBlocks()
        {
            var check = RealData.AutoCad.Find("ac-006.block-properties")!;
            Assert.False(((JsonBool)check.Params["where"]!.AsObject()["isXrefDependent"]!).Value);
            var f = new Facts("autocad");
            f.Table("blocks").Add("B1").Set("name", "XA|DOOR").Set("isXref", false).Set("isXrefDependent", true).Set("isAnonymous", false).Set("hardPropertyEntities", 3);
            Assert.Equal(CheckStatus.Pass, Runner.Run(check, f, RealData.Us).Status);
        }

        [Fact]
        public void WhereNotKeepsRecordsThatDifferOrLackTheField()
        {
            var f = new Facts("autocad");
            var x = f.Table("xrefs");
            x.Add("X1").Set("status", "resolved").Set("depth", 1);
            x.Add("X2").Set("status", "unreferenced").Set("depth", 1);
            x.Add("X3").Set("depth", 1);
            x.Add("X4").Set("status", "resolved").Set("depth", 2);
            var r = Runner.Run(Def("field_missing", @"{ ""table"": ""xrefs"", ""field"": ""name"", ""where"": { ""depth"": 1 }, ""whereNot"": { ""status"": ""unreferenced"" } }"), f, null);
            Assert.Equal(new[] { "X1", "X3" }, r.Findings.Select(y => y.RecordId));
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("field_missing", @"{ ""table"": ""xrefs"", ""field"": ""name"", ""whereNot"": ""unreferenced"" }"), f, null).Status);
            Assert.Contains("status", CheckInputs.Fields(Def("field_missing", @"{ ""table"": ""xrefs"", ""field"": ""name"", ""whereNot"": { ""status"": ""x"" } }"), out _));
        }

        [Fact]
        public void ShippedXrefLayerCheckSkipsUnreferencedXrefs()
        {
            var check = RealData.AutoCad.Find("ac-016.xref-layer")!;
            var f = new Facts("autocad");
            var x = f.Table("xrefs");
            x.Add("X1").Set("name", "BASE").Set("depth", 1).Set("status", "resolved").Set("insertLayer", "G-ANNO-REFR");
            x.Add("X2").Set("name", "OLD").Set("depth", 1).Set("status", "unreferenced").Set("insertLayer", "0");
            x.Add("X3").Set("name", "GRID").Set("depth", 1).Set("status", "not_found").Set("insertLayer", "0");
            var r = Runner.Run(check, f, RealData.Us);
            Assert.Equal(new[] { "X3" }, r.Findings.Select(y => y.RecordId));
        }

        // ------------------------------------------------------------------ what a verify step must collect

        [Fact]
        public void NeedsListTheTablesAndFieldsTheChecksRead()
        {
            var checks = new[]
            {
                Def("field_equals", @"{ ""table"": ""layers"", ""field"": ""isUsed"", ""equals"": false, ""where"": { ""isXrefDependent"": false } }", "a"),
                Def("dim_precision_min", @"{ ""table"": ""styles"", ""where"": { ""kind"": ""dim"" }, ""imperialMinIn"": 0.25, ""metricMinMm"": 1 }", "b"),
                Def("count_over_threshold", @"{ ""table"": ""layers"", ""max"": 500 }", "c"),
                Def("manual_review", @"{ ""instructions"": ""look"" }", "d"),
            };
            var n = CheckInputs.Needs(checks);
            Assert.False(n.Everything);
            Assert.Equal(new[] { "layers", "styles" }, n.Tables);
            Assert.Equal(new[] { "isUsed", "isXrefDependent" }, n.FieldsOf("layers"));
            Assert.Equal(new[] { "kind", "stepMm", "unitSystem" }, n.FieldsOf("styles"));
            Assert.True(n.NeedsField("styles", "stepMm"));
            Assert.False(n.NeedsField("layers", "lineweight"));
            Assert.False(n.NeedsTable("entities"));
            Assert.False(n.NeedsAllFields("layers"));
            Assert.Empty(n.UnknownChecks);
            Assert.Equal(@"{""everything"":false,""tables"":{""layers"":[""isUsed"",""isXrefDependent""],""styles"":[""kind"",""stepMm"",""unitSystem""]},""unknownChecks"":[]}", n.ToJson().ToJson(false));
        }

        [Fact]
        public void NeedsAreConservativeForEvaluatorsCoreDoesNotKnow()
        {
            var onTable = CheckInputs.Needs(new[] { Def("engine_only", @"{ ""table"": ""walls"" }", "e1") });
            Assert.False(onTable.Everything);
            Assert.True(onTable.NeedsAllFields("walls"));
            Assert.True(onTable.NeedsField("walls", "anything"));
            Assert.False(onTable.NeedsTable("layers"));
            Assert.Equal(new[] { "e1" }, onTable.UnknownChecks);
            Assert.Contains(@"""walls"":""*""", onTable.ToJson().ToJson(false));

            var anywhere = CheckInputs.Needs(new[] { Def("engine_only", "{}", "e2"), Def("field_missing", @"{ ""table"": ""layers"", ""field"": ""name"" }", "f") });
            Assert.True(anywhere.Everything);
            Assert.True(anywhere.NeedsTable("entities"));
            Assert.True(anywhere.NeedsAllFields("entities"));
            Assert.False(anywhere.NeedsAllFields("layers"));
            Assert.True(anywhere.NeedsField("layers", "name"));
        }

        [Fact]
        public void ShippedChecksNeedOnlyKnownFields()
        {
            foreach (var catalog in new[] { RealData.AutoCad, RealData.Revit })
            {
                var n = CheckInputs.Needs(catalog.Checks);
                Assert.False(n.Everything);
                Assert.Empty(n.UnknownChecks);
                Assert.Contains("insertLayer", CheckInputs.Needs(RealData.AutoCad.Checks.Where(c => c.Id == "ac-016.xref-layer")).FieldsOf("xrefs"));
            }
            var verify = CheckInputs.Needs(RealData.AutoCad.Checks.Where(c => c.Id == "ac-016.xref-layer"));
            Assert.Equal(new[] { "xrefs" }, verify.Tables);
            Assert.Equal(new[] { "depth", "insertLayer", "status" }, verify.FieldsOf("xrefs"));
        }

        // ------------------------------------------------------------------ truncated and omitted facts

        private static Facts Layers(int hard = 0)
        {
            var f = new Facts("autocad");
            var t = f.Table("layers");
            t.Add("L1").Set("name", "A-WALL").Set("isUsed", true).Set("lineweight", 0.35);
            t.Add("L2").Set("name", "A-DOOR").Set("isUsed", hard > 0 ? false : true).Set("lineweight", 0.25);
            return f;
        }

        private const string UnusedLayers = @"{ ""table"": ""layers"", ""field"": ""isUsed"", ""equals"": false }";

        [Fact]
        public void ATruncatedTableNeverPasses()
        {
            var f = Layers();
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("field_equals", UnusedLayers), f, null).Status);

            f.MarkTruncated("layers", Facts.RecordsPart, 2, 5000, "record limit");
            var r = Runner.Run(Def("field_equals", UnusedLayers), f, null);
            Assert.Equal(CheckStatus.Incomplete, r.Status);
            Assert.True(r.Incomplete);
            Assert.Contains("'layers' was cut (2 of 5000, record limit)", r.IncompleteReason);
            Assert.Equal(r.IncompleteReason, r.Message);
            Assert.Empty(r.Findings);

            // Real findings on part of the table still fail, flagged incomplete.
            var g = Layers(hard: 1);
            g.MarkTruncated("layers", Facts.RecordsPart, 2, 5000);
            var bad = Runner.Run(Def("field_equals", UnusedLayers), g, null);
            Assert.Equal(CheckStatus.Fail, bad.Status);
            Assert.True(bad.Incomplete);
            Assert.Single(bad.Findings);
        }

        [Fact]
        public void TheFlatTruncationFlagCountsToo()
        {
            var f = Layers();
            f.Meta.Set("layers.truncated", true).Set("layers.total", 9000);
            var r = Runner.Run(Def("field_equals", UnusedLayers), f, null);
            Assert.Equal(CheckStatus.Incomplete, r.Status);
            Assert.Contains("(2 of 9000)", r.IncompleteReason);

            var off = Layers();
            off.Meta.Set("layers.truncated", false);
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("field_equals", UnusedLayers), off, null).Status);
            Assert.Empty(off.IncompleteParts("layers"));
        }

        [Fact]
        public void ACutFieldMattersOnlyToChecksThatReadIt()
        {
            var f = Layers();
            f.MarkTruncated("layers", "lineweight", 1, 2, "call limit");
            Assert.Equal(CheckStatus.Pass, Runner.Run(Def("field_equals", UnusedLayers), f, null).Status);
            var reads = Runner.Run(Def("field_range", @"{ ""table"": ""layers"", ""field"": ""lineweight"", ""max"": 2, ""skipMissing"": true }"), f, null);
            Assert.Equal(CheckStatus.Incomplete, reads.Status);
            Assert.Contains("'lineweight' (1 of 2, call limit)", reads.IncompleteReason);
            // A field named only in where counts as read.
            Assert.Equal(CheckStatus.Incomplete, Runner.Run(Def("count_over_threshold", @"{ ""table"": ""layers"", ""where"": { ""lineweight"": 9 }, ""max"": 0 }"), f, null).Status);
            // An evaluator's default field counts as read.
            var dims = new Facts("autocad");
            dims.Table("styles").Add("D1").Set("stepMm", 1).Set("unitSystem", "metric");
            dims.MarkOmitted("styles", "stepMm", 3);
            var dp = Runner.Run(Def("dim_precision_min", @"{ ""table"": ""styles"", ""imperialMinIn"": 0.25, ""metricMinMm"": 1 }"), dims, null);
            Assert.Equal(CheckStatus.Incomplete, dp.Status);
            Assert.Contains("omitted from 3 records", dp.IncompleteReason);
        }

        private sealed class EngineEvaluator : ICheckEvaluator
        {
            public string Name => "engine_only";
            public IEnumerable<Finding> Evaluate(CheckContext context) { context.RequireTable("layers"); return new Finding[0]; }
        }

        [Fact]
        public void AnEvaluatorCoreDoesNotKnowIsJudgedConservatively()
        {
            var reg = EvaluatorRegistry.CreateNeutral();
            reg.Register(new EngineEvaluator());
            var f = Layers();
            f.MarkTruncated("layers", "lineweight", 1, 2);
            Assert.Equal(CheckStatus.Incomplete, new CheckRunner(reg).Run(Def("engine_only", @"{ ""table"": ""layers"" }"), f, null).Status);
            Assert.Equal(CheckStatus.Pass, new CheckRunner(reg).Run(Def("engine_only", @"{ ""table"": ""layers"" }"), Layers(), null).Status);
            // A check that names no table, and review items, are not affected.
            Assert.Null(CheckInputs.Incompleteness(Def("manual_review", "{}"), f));
            Assert.True(Runner.Run(Def("manual_review", "{}"), f, null).IsManualReview);
        }

        [Fact]
        public void IncompleteTravelsOnTheWireAndInTheSummary()
        {
            var f = Layers();
            f.MarkTruncated("layers", Facts.RecordsPart, 2, 10);
            var results = new List<CheckResult>
            {
                Runner.Run(Def("field_equals", UnusedLayers, "a.one"), f, null),
                Runner.Run(Def("field_equals", @"{ ""table"": ""layers"", ""field"": ""isUsed"", ""equals"": true }", "a.two"), f, null),
                Runner.Run(Def("field_equals", UnusedLayers, "a.three"), Layers(), null),
            };
            var json = results[0].ToJson();
            Assert.Equal("incomplete", json.GetStringOrNull("status"));
            Assert.True(((JsonBool)json["incomplete"]!).Value);
            var back = CheckResult.Parse(JsonValue.Parse(json.ToJson(false)));
            Assert.Equal(CheckStatus.Incomplete, back.Status);
            Assert.True(back.Incomplete);
            Assert.Equal(results[0].IncompleteReason, back.IncompleteReason);
            Assert.True(CheckResult.Parse(results[1].ToJson()).Incomplete);
            Assert.Equal(CheckStatus.Fail, CheckResult.Parse(results[1].ToJson()).Status);
            Assert.Null(results[2].ToJson()["incomplete"]);
            Assert.Equal(CheckStatus.Incomplete, CheckResult.ParseStatus("incomplete"));
            Assert.Equal("incomplete", CheckResult.StatusToWire(CheckStatus.Incomplete));

            var s = CheckSummary.Of(results);
            Assert.Equal(1, s.Incomplete);
            Assert.Equal(1, s.Fail);
            Assert.Equal(1, s.Pass);
            Assert.Equal(0, s.Error);
            Assert.Equal(1, s.ToJson()["incomplete"]!.AsInt64());
        }

        [Fact]
        public void FactsMetaHelpersWriteTheSharedShape()
        {
            var f = new Facts("revit");
            f.Table("views");
            f.Table("warnings");
            f.MarkTruncated("views", "hiddenElementCount", 10, 20, "call limit");
            f.MarkOmitted("views", "scale");
            f.MarkOmitted("views", "scale");
            Assert.Equal(new[] { "hiddenElementCount", "scale" }, f.IncompleteParts("views"));
            Assert.Equal(new[] { "views" }, f.IncompleteTables());
            var meta = f.Meta;
            Assert.Equal(10, meta["truncated"]!.AsObject()["views"]!.AsObject()["hiddenElementCount"]!.AsObject()["kept"]!.AsInt64());
            Assert.Equal(2, meta["omittedFields"]!.AsObject()["views"]!.AsObject()["scale"]!.AsInt64());
            var parsed = Facts.Parse(JsonValue.Parse(f.ToJson().ToJson(false)));
            Assert.Equal(new[] { "hiddenElementCount", "scale" }, parsed.IncompleteParts("views"));
        }

        // ------------------------------------------------------------------ verify.checksFile

        private static JsonObject JobWithVerify(string verifyJson)
        {
            var job = (JsonObject)JsonValue.Parse(JobTests.GoodAcadJob);
            job.Set("verify", JsonValue.Parse(verifyJson));
            return job;
        }

        [Fact]
        public void VerifyAcceptsChecksFileAndMode()
        {
            var v = JobValidator.Validate(JobWithVerify(@"{ ""checks"": [""p.own""], ""checksFile"": ""project-checks.json"" }"), OpRegistry.Default);
            Assert.True(v.Ok, string.Join("; ", v.Issues));
            Assert.Equal(new[] { "project-checks.json" }, v.Job!.Verify.ChecksFiles);
            Assert.False(v.Job.Verify.AddToBundled);

            v = JobValidator.Validate(JobWithVerify(@"{ ""checks"": [""p.own""], ""checksFile"": [""a.json"", ""dir""], ""checksMode"": ""add"" }"), OpRegistry.Default);
            Assert.True(v.Ok, string.Join("; ", v.Issues));
            Assert.Equal(new[] { "a.json", "dir" }, v.Job!.Verify.ChecksFiles);
            var o = v.Job.Verify.ToAuditOptions();
            Assert.True(o.AddToBundled);
            Assert.Equal(new[] { "p.own" }, o.CheckIds);
            Assert.Equal(new[] { "a.json", "dir" }, o.ChecksFiles);
            Assert.Equal(@"{""checks"":[""p.own""],""checksFile"":[""a.json"",""dir""],""checksMode"":""add""}", o.ToParams().ToJson(false));

            // A job without them is unchanged.
            var plain = JobValidator.Validate(JobTests.GoodAcadJob, OpRegistry.Default).Job!;
            Assert.Empty(plain.Verify.ChecksFiles);
            Assert.False(plain.Verify.AddToBundled);
        }

        [Theory]
        [InlineData(@"{ ""checks"": [""a.b""], ""checksFile"": 3 }", "$.verify.checksFile")]
        [InlineData(@"{ ""checks"": [""a.b""], ""checksFile"": [] }", "$.verify.checksFile")]
        [InlineData(@"{ ""checks"": [""a.b""], ""checksFile"": [""ok.json"", """"] }", "$.verify.checksFile[1]")]
        [InlineData(@"{ ""checks"": [""a.b""], ""checksFile"": ""x.json"", ""checksMode"": ""merge"" }", "$.verify.checksMode")]
        [InlineData(@"{ ""checks"": [""a.b""], ""catalog"": ""x.json"" }", "$.verify.catalog")]
        public void VerifyRejectsBadChecksFileMembers(string verify, string path)
        {
            var v = JobValidator.Validate(JobWithVerify(verify), OpRegistry.Default);
            Assert.False(v.Ok);
            Assert.Contains(v.Errors, i => i.Path == path);
        }

        [Fact]
        public void IdsFromTheJobsOwnChecksFileAreWarnedNotRefused()
        {
            var known = new JobValidationOptions { KnownCheckIds = new HashSet<string> { "walls.open_ends" } };
            var refused = JobValidator.Validate(JobWithVerify(@"{ ""checks"": [""p.own""] }"), OpRegistry.Default, known);
            Assert.Contains(refused.Errors, i => i.Code == IssueCodes.UnknownCheck);

            var warned = JobValidator.Validate(JobWithVerify(@"{ ""checks"": [""p.own""], ""checksFile"": ""project.json"" }"), OpRegistry.Default, known);
            Assert.True(warned.Ok);
            Assert.Contains(warned.Issues, i => !i.IsError && i.Code == IssueCodes.UnknownCheck && i.Path == "$.verify.checks[0]");

            var idle = JobValidator.Validate(JobWithVerify(@"{ ""checksFile"": ""project.json"" }"), OpRegistry.Default);
            Assert.True(idle.Ok);
            Assert.Contains(idle.Issues, i => !i.IsError && i.Path == "$.verify.checksFile");
            var modeOnly = JobValidator.Validate(JobWithVerify(@"{ ""checks"": [""a.b""], ""checksMode"": ""add"" }"), OpRegistry.Default);
            Assert.True(modeOnly.Ok);
            Assert.Contains(modeOnly.Issues, i => !i.IsError && i.Path == "$.verify.checksMode");
        }

        // ------------------------------------------------------------------ standard: roles, title block, purge, view naming

        private const string RolesStandard = @"{
  ""id"": ""t"", ""name"": ""T"",
  ""layers"": [
    { ""name"": ""G-ANNO-NPLT"", ""color"": 4, ""plot"": false, ""linetype"": ""HIDDEN"" },
    { ""name"": ""G-ANNO-REFR"", ""color"": 7 },
    { ""name"": ""G-ANNO-TTLB"", ""color"": 7 }
  ],
  ""layerRoles"": { ""viewport"": ""G-ANNO-NPLT"", ""xref"": ""G-ANNO-REFR"", ""titleBlock"": ""G-ANNO-TTLB"" },
  ""textStyles"": [ { ""name"": ""NOTES"", ""font"": ""Arial"" } ],
  ""titleBlock"": { ""blockName"": ""TB-A1"", ""familyType"": ""A1 Metric : A1"" },
  ""purge"": { ""keep"": [ ""NORTH-ARROW"" ] },
  ""viewNaming"": { ""fields"": [ { ""name"": ""sequence"", ""pattern"": ""[0-9]{2}"" }, { ""name"": ""title"", ""separator"": "" - "", ""pattern"": ""[A-Za-z0-9 ()&-]+"" } ],
                    ""examples"": [ ""01 - Ground Floor"" ] }
}";

        [Fact]
        public void StandardLoadsRolesTitleBlockPurgeAndViewNaming()
        {
            var s = StandardLoader.Parse(RolesStandard);
            Assert.Equal("G-ANNO-NPLT", s.ViewportLayer);
            Assert.Equal("G-ANNO-REFR", s.XrefLayer);
            Assert.Equal("G-ANNO-TTLB", s.TitleBlockLayer);
            Assert.Equal("TB-A1", s.TitleBlockName);
            Assert.Equal("A1 Metric : A1", s.TitleBlockFamilyType);
            Assert.Empty(s.TitleBlockFields);
            Assert.Equal(new[] { "NORTH-ARROW" }, s.PurgeKeep);
            Assert.Empty(s.SelfCheck());
            var keep = s.NamesToKeep();
            foreach (var n in new[] { "north-arrow", "G-ANNO-NPLT", "HIDDEN", "NOTES", "TB-A1", "Continuous" }) Assert.Contains(n, keep);
            Assert.True(s.ParseViewName("02 - First Floor (Demo)").Valid);
            Assert.False(s.ParseViewName("Section 1").Valid);
        }

        [Theory]
        [InlineData(@"""layerRoles"": { ""sheet"": ""X"" }", "$.layerRoles")]
        [InlineData(@"""layerRoles"": { ""viewport"": "" "" }", "$.layerRoles.viewport")]
        [InlineData(@"""purge"": { }", "$.purge.keep")]
        [InlineData(@"""purge"": { ""keep"": [ """" ] }", "$.purge.keep[0]")]
        [InlineData(@"""titleBlock"": { ""familyType"": ""A1"" }", "$.titleBlock.familyType")]
        [InlineData(@"""viewNaming"": { ""fields"": [ { ""name"": ""t"", ""pattern"": ""("" } ] }", "$.viewNaming.fields[0].pattern")]
        public void StandardRejectsBadNewMembers(string member, string path)
        {
            var e = Assert.Throws<StandardFormatException>(() => StandardLoader.Parse(@"{ ""id"": ""t"", ""name"": ""T"", " + member + " }"));
            Assert.Contains(path, e.Message);
        }

        [Fact]
        public void SelfCheckReportsRoleLayersThatAreMissingOrPlotWrongly()
        {
            var s = StandardLoader.Parse(@"{ ""id"": ""t"", ""name"": ""T"",
              ""layers"": [ { ""name"": ""VP"", ""color"": 4 }, { ""name"": ""XR"", ""color"": 7, ""plot"": false } ],
              ""layerRoles"": { ""viewport"": ""VP"", ""xref"": ""XR"", ""titleBlock"": ""TB"" },
              ""viewNaming"": { ""fields"": [ { ""name"": ""n"", ""pattern"": ""[0-9]+"" } ], ""examples"": [ ""A"" ] } }");
            var p = s.SelfCheck();
            Assert.Contains(p, x => x.Contains("layerRoles.viewport 'VP' plots"));
            Assert.Contains(p, x => x.Contains("layerRoles.xref 'XR' does not plot"));
            Assert.Contains(p, x => x.Contains("layerRoles.titleBlock 'TB' is not in the layer list"));
            Assert.Contains(p, x => x.StartsWith("view naming example"));
        }

        [Fact]
        public void BothPresetsNameTheirRoleLayersAndAViewRule()
        {
            foreach (var (name, s) in RealData.Presets())
            {
                Assert.Empty(s.SelfCheck());
                Assert.NotNull(s.ViewNaming);
                Assert.NotNull(s.FindLayer(s.ViewportLayer!));
                Assert.False(s.FindLayer(s.ViewportLayer!)!.Plot);
                Assert.NotNull(s.FindLayer(s.XrefLayer!));
                Assert.NotNull(s.FindLayer(s.TitleBlockLayer!));
                Assert.True(s.Patterns["titleBlockLayer"].IsMatch(s.TitleBlockLayer!), name);
                // The view rule agrees with the preset's viewName pattern.
                foreach (var e in s.ViewNaming!.Examples) Assert.Matches(s.Patterns["viewName"], e);
            }
            Assert.Equal("G-ANNO-NPLT", RealData.Us.ViewportLayer);
            Assert.Equal("Z-Zz3030--ExternalReferences", RealData.Iso.XrefLayer);
            Assert.Contains("G-ANNO-REFR", RealData.Us.CodeLists["xrefLayers"].Select(c => c.Code));
        }

        [Fact]
        public void NamesMatchSchemeChecksViewNames()
        {
            var f = new Facts("revit");
            var v = f.Table("views");
            v.Add("1").Set("name", "01 - Ground Floor");
            v.Add("2").Set("name", "Section 1");
            var r = Runner.Run(Def("names_match_scheme", @"{ ""table"": ""views"", ""field"": ""name"", ""scheme"": ""view"" }"), f, RealData.Iso);
            Assert.Equal(new[] { "2" }, r.Findings.Select(x => x.RecordId));
            var none = StandardLoader.Parse(@"{ ""id"": ""t"", ""name"": ""T"" }");
            Assert.Equal(CheckStatus.Error, Runner.Run(Def("names_match_scheme", @"{ ""table"": ""views"", ""field"": ""name"", ""scheme"": ""view"" }"), f, none).Status);
        }

        // ------------------------------------------------------------------ file name placeholders

        [Fact]
        public void AutoCadFileNamePlaceholdersAreUnchanged()
        {
            var d = OpRegistry.Default.Find("plot.pdf", AppKind.AutoCad)!.FindField("fileName")!.Description;
            Assert.Contains("{drawing}, {layout}, {sheetNumber}, {sheetName}, {view}, {date}, {jobId}", d);
            Assert.DoesNotContain("{revision}", d);
        }
    }
}
