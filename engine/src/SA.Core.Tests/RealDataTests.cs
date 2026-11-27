using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Core.Standards;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>
    /// The shipped data files: data/standards/*.json and data/checks/*.json. The data folder is SA_DATA_DIR when that
    /// environment variable is set, else the data/ folder found above the test binaries.
    /// </summary>
    internal static class RealData
    {
        public static string Dir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SA_DATA_DIR");
                return !string.IsNullOrEmpty(env) ? Path.GetFullPath(env) : DataFiles.DataDir;
            }
        }

        public static string StandardPath(string name) => Path.Combine(Dir, "standards", name);
        public static string ChecksPath(string name) => Path.Combine(Dir, "checks", name);

        private static ProjectStandard? _us, _iso;
        public static ProjectStandard Us => _us ??= StandardLoader.LoadFile(StandardPath("us-ncs-style.json"));
        public static ProjectStandard Iso => _iso ??= StandardLoader.LoadFile(StandardPath("iso19650-uk-style.json"));

        public static IEnumerable<(string Name, ProjectStandard Standard)> Presets()
        {
            yield return ("us-ncs-style", Us);
            yield return ("iso19650-uk-style", Iso);
        }

        private static CheckCatalog? _acad, _revit;
        public static CheckCatalog AutoCad => _acad ??= CheckCatalog.LoadFile(ChecksPath("autocad.json"));
        public static CheckCatalog Revit => _revit ??= CheckCatalog.LoadFile(ChecksPath("revit.json"));
    }

    public class RealDataTests
    {
        public static IEnumerable<object[]> PresetNames() => new[] { new object[] { "us-ncs-style.json" }, new object[] { "iso19650-uk-style.json" } };

        [Theory]
        [MemberData(nameof(PresetNames))]
        public void PresetLoadsAndItsOwnExamplesValidate(string file)
        {
            var std = StandardLoader.LoadFile(RealData.StandardPath(file));
            Assert.Empty(std.SelfCheck());
            Assert.NotNull(std.LayerScheme);
            Assert.NotEmpty(std.Layers);
            foreach (var l in std.Layers) Assert.True(std.ParseLayerName(l.Name).Valid, l.Name + ": " + std.ParseLayerName(l.Name).Explain());

            var layerExamples = std.Raw["layerNaming"]!.AsObject()["examples"]?.AsArray().Select(x => x.AsString()).ToList() ?? new List<string>();
            Assert.NotEmpty(layerExamples);
            foreach (var e in layerExamples) Assert.True(std.ParseLayerName(e).Valid, e + ": " + std.ParseLayerName(e).Explain());

            Assert.NotNull(std.SheetNumbering);
            Assert.NotEmpty(std.SheetNumbering!.Examples);
            foreach (var e in std.SheetNumbering.Examples) Assert.True(std.ParseSheetId(e).Valid, e + ": " + std.ParseSheetId(e).Explain());
            if (std.FileNaming != null)
            {
                Assert.NotEmpty(std.FileNaming.Examples);
                foreach (var e in std.FileNaming.Examples) Assert.True(std.ParseFileName(e).Valid, e + ": " + std.ParseFileName(e).Explain());
            }
            foreach (var f in std.RevisionFormats)
            {
                var raw = std.Raw["revisionCodes"]!.AsObject()["formats"]!.AsArray().First(x => x.AsObject().GetStringOrNull("name") == f.Name).AsObject();
                var ex = raw.GetStringOrNull("example");
                if (ex != null) Assert.Matches(f.Pattern, ex);
            }
            Assert.NotEmpty(std.Scales);
            Assert.NotEmpty(std.TextHeights);
            Assert.NotNull(std.LengthUnit);
        }

        [Fact]
        public void PresetsRejectWhatTheyShould()
        {
            Assert.True(RealData.Us.ParseSheetId("A-101").Valid);
            Assert.False(RealData.Us.ParseSheetId("A-1O1").Valid);
            Assert.False(RealData.Us.ParseLayerName("WALLS").Valid);
            Assert.True(RealData.Iso.ParseFileName("ABCDE-XYZ-ZZ-02-D-A-0001.pdf").Valid);
            Assert.False(RealData.Iso.ParseFileName("ABCDE-XYZ-ZZ-02-D-A-0001.exe").Valid);
            Assert.False(RealData.Iso.ParseLayerName("A-WALL").Valid);
            Assert.Matches(RealData.Iso.Patterns["wallLayer"], "A-Ss2510--FramedWallSystems");
            Assert.Matches(RealData.Us.Patterns["cadLinkWorkset"], "Links - dwg");
        }

        [Fact]
        public void BothChecksFilesLoadWithTheDocumentedCounts()
        {
            Assert.Equal(63, RealData.AutoCad.Checks.Count);
            Assert.Equal(67, RealData.Revit.Checks.Count);
            Assert.All(RealData.AutoCad.Checks, c => Assert.Equal(CheckApp.AutoCad, c.App));
            Assert.All(RealData.Revit.Checks, c => Assert.Equal(CheckApp.Revit, c.App));
            Assert.Equal(14, RealData.AutoCad.Checks.Count(c => c.Evaluator == "manual_review"));
            Assert.Equal(22, RealData.Revit.Checks.Count(c => c.Evaluator == "manual_review"));
            // Review items carry the group "manual", and only they do.
            foreach (var c in RealData.AutoCad.Checks.Concat(RealData.Revit.Checks))
                Assert.True((c.Evaluator == "manual_review") == c.Groups.Contains("manual"), c.Id);
            // The two files load into one catalogue: their ids do not collide.
            Assert.Equal(130, CheckCatalog.LoadFiles(new[] { RealData.ChecksPath("autocad.json"), RealData.ChecksPath("revit.json") }).Checks.Count);
        }

        [Fact]
        public void EveryEvaluatorNamedByAChecksFileIsRegistered()
        {
            var registry = EvaluatorRegistry.CreateNeutral();
            var missing = RealData.AutoCad.Checks.Concat(RealData.Revit.Checks).Where(c => !registry.Contains(c.Evaluator)).Select(c => c.Id + " -> " + c.Evaluator).ToList();
            Assert.Empty(missing);
        }

        [Fact]
        public void EveryStandardReferenceOfEveryCheckExistsInBothPresets()
        {
            var problems = new List<string>();
            foreach (var (name, std) in RealData.Presets())
                foreach (var c in RealData.AutoCad.Checks.Concat(RealData.Revit.Checks))
                {
                    var p = c.Params;
                    foreach (var key in new[] { "standardPattern", "ifStandardPattern" })
                        if (p.GetStringOrNull(key) is string pat && !std.Patterns.ContainsKey(pat)) problems.Add(name + ": " + c.Id + " pattern " + pat);
                    if (p.GetStringOrNull("list") is string list)
                    {
                        if (list.StartsWith("codeList:", StringComparison.Ordinal))
                        {
                            string cl = list.Substring(9);
                            if (!std.CodeLists.TryGetValue(cl, out var codes) || codes.Count == 0) problems.Add(name + ": " + c.Id + " code list " + cl);
                        }
                        else
                        {
                            int count = list == "layers" ? std.Layers.Count : list == "textStyles" ? std.TextStyles.Count : list == "dimStyles" ? std.DimStyles.Count
                                : list == "scales" ? std.Scales.Count : list == "statusCodes" ? std.StatusCodes.Count : -1;
                            if (count <= 0) problems.Add(name + ": " + c.Id + " list " + list);
                        }
                    }
                    if (c.Evaluator == "names_match_scheme")
                    {
                        string scheme = p.GetStringOrNull("scheme") ?? "";
                        bool has = scheme == "layer" ? std.LayerScheme != null : scheme == "sheet" ? std.SheetNumbering != null : std.FileNaming != null;
                        if (!has) problems.Add(name + ": " + c.Id + " scheme " + scheme);
                    }
                    if (c.Evaluator == "scale_in_standard" && std.Scales.Count == 0) problems.Add(name + ": " + c.Id + " scales");
                    if (c.Evaluator == "text_height_standard" && std.TextHeights.Count == 0) problems.Add(name + ": " + c.Id + " text heights");
                    if (c.Evaluator == "units_match_standard" && std.LengthUnit is null) problems.Add(name + ": " + c.Id + " units.length");
                    if (c.Evaluator == "matches_standard_entry" && p["properties"] is JsonObject props)
                        foreach (var kv in props)
                        {
                            string prop = kv.Value.AsString();
                            var raw = std.Raw[p.GetStringOrNull("list")!] as JsonArray;
                            if (raw is null || !raw.Any(e => e.AsObject().ContainsKey(prop.StartsWith("settings.", StringComparison.Ordinal) ? "settings" : prop)))
                                problems.Add(name + ": " + c.Id + " no entry gives " + prop);
                        }
                }
            Assert.Empty(problems);
        }

        [Theory]
        [InlineData("autocad", "us-ncs-style")]
        [InlineData("autocad", "iso19650-uk-style")]
        [InlineData("revit", "us-ncs-style")]
        [InlineData("revit", "iso19650-uk-style")]
        public void SyntheticFactsRunEveryAutomaticCheckToPassOrFail(string app, string preset)
        {
            var std = preset == "us-ncs-style" ? RealData.Us : RealData.Iso;
            var catalog = app == "autocad" ? RealData.AutoCad : RealData.Revit;
            var facts = app == "autocad" ? SyntheticFacts.AutoCad(std) : SyntheticFacts.Revit();
            var runner = new CheckRunner(EvaluatorRegistry.CreateNeutral());
            var results = runner.RunAll(catalog.Checks, facts, std);
            var bad = results.Where(r => !r.IsManualReview && r.Status != CheckStatus.Pass && r.Status != CheckStatus.Fail)
                .Select(r => r.Id + ": " + CheckResult.StatusToWire(r.Status) + " " + r.Message).ToList();
            Assert.Empty(bad);
            // Review items are flagged, automatic ones never are.
            foreach (var r in results)
                Assert.Equal(catalog.Find(r.Id)!.Evaluator == "manual_review", r.IsManualReview);
            var s = CheckSummary.Of(results);
            Assert.Equal(catalog.Checks.Count(c => c.Evaluator == "manual_review"), s.Review);
            Assert.Equal(0, s.Error + s.NotImplemented);
            // The facts are built so that both outcomes occur.
            Assert.True(s.Pass > 0 && s.Fail > 0, "pass " + s.Pass + ", fail " + s.Fail);
            // Every finding of an op fix instantiates.
            foreach (var r in results.Where(x => x.Fixes.Count > 0))
            {
                Assert.NotEmpty(r.Findings);
                foreach (var fx in r.Fixes) Assert.DoesNotContain("{{", fx.ToJson());
            }
        }

        [Fact]
        public void SyntheticAutoCadFactsGiveTheExpectedVerdicts()
        {
            var results = new CheckRunner(EvaluatorRegistry.CreateNeutral()).RunAll(RealData.AutoCad.Checks, SyntheticFacts.AutoCad(RealData.Us), RealData.Us)
                .ToDictionary(r => r.Id);
            Assert.Equal(new[] { "E01", "E08", "E09", "E12", "E14" }, results["ac-001.layer-zero"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "E04" }, results["ac-004.dims-on-dim-layer"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.Pass, results["ac-012.insunits"].Status);
            Assert.Equal(new[] { "S2" }, results["ac-013.annotative-styles"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "E11" }, results["ac-033.off-axis"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "E09" }, results["ac-036.viewport-scale"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "D2" }, results["ac-011.dim-precision"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.Pass, results["ac-048.lineweight-hierarchy"].Status);
            Assert.Equal(new[] { "E13" }, results["ac-040.splines"].Findings.Select(f => f.RecordId));
            // Blocks that came in with an xref (B3) and anonymous blocks (B2) are not judged.
            Assert.Equal(new[] { "B4" }, results["ac-006.block-properties"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "E05" }, results["ac-049.annotation-overlap"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "E02" }, results["ac-050.job-not-in-viewport"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.Pass, results["ac-050.job-cut-by-viewport"].Status);
            Assert.Equal(CheckStatus.Pass, results["ac-051.mixed-viewport-scales"].Status);
            // The ISO preset expects millimetres: an inch drawing fails and the fix carries "mm".
            var iso = new CheckRunner(EvaluatorRegistry.CreateNeutral()).Run(RealData.AutoCad.Find("ac-012.insunits")!, SyntheticFacts.AutoCad(RealData.Us), RealData.Iso);
            Assert.Equal(CheckStatus.Fail, iso.Status);
            Assert.Equal("mm", iso.Fixes[0].AsObject()["args"]!.AsObject().GetStringOrNull("insUnits"));
        }

        [Fact]
        public void SyntheticRevitFactsGiveTheExpectedVerdicts()
        {
            var results = new CheckRunner(EvaluatorRegistry.CreateNeutral()).RunAll(RealData.Revit.Checks, SyntheticFacts.Revit(), RealData.Iso)
                .ToDictionary(r => r.Id);
            Assert.Equal(new[] { "2 x Highlighted walls overlap.", "1 x Wall is slightly off axis and may cause inaccuracies." }, results["rv-003.warning-types"].Findings.Select(f => f.Message));
            Assert.Equal(new[] { "601" }, results["rv-014.cad-link-workset"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "302" }, results["rv-020.ffe-workset"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "501" }, results["rv-045.dim-precision"].Findings.Select(f => f.RecordId));
            Assert.Equal(new[] { "202" }, results["rv-051.ifc-view-fine"].Findings.Select(f => f.RecordId));
            // Rooms, levels, text notes and spaces carry no levelOffsetAbsMm (FACTS.md 2.4); only the desk 300 mm up is a finding.
            Assert.Equal(new[] { "302" }, results["rv-031.offset-from-level"].Findings.Select(f => f.RecordId));
            // The tag with a leader (703) is left out; the text note and the other tag pile up.
            Assert.Equal(new[] { "701" }, results["rv-054.annotation-overlap"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.Pass, results["rv-055.job-outside-crop"].Status);
            Assert.Equal(new[] { "301" }, results["rv-055.job-cut-by-crop"].Findings.Select(f => f.RecordId));
            Assert.Equal(CheckStatus.Pass, results["rv-056.view-scale"].Status);
            Assert.Equal(CheckStatus.Pass, results["rv-057.mixed-scales-on-sheet"].Status);
        }
    }

    /// <summary>Facts built to the field lists of data/checks/FACTS.md.</summary>
    internal static class SyntheticFacts
    {
        public static Facts AutoCad(ProjectStandard std)
        {
            bool us = std.LengthUnit == Units.LengthUnit.Inch;
            string unit = us ? "in" : "mm";
            var f = new Facts("autocad");
            f.Meta.Add("units.length", unit);
            f.Table("document").Add().Set("insUnits", unit).Set("measurement", us ? 0 : 1).Set("ltScale", 1).Set("psLtScale", 1).Set("msLtScale", 1)
                .Set("tileMode", 1).Set("currentLayout", "Model").Set("imageFrame", 2).Set("hasRasterImages", true).Set("purgeableCount", 3)
                .Set("purgeableByKind", new JsonObject().Add("blocks", 1).Add("layers", 2).Add("linetypes", 0).Add("textstyles", 0).Add("dimstyles", 0)
                    .Add("mlinestyles", 0).Add("tablestyles", 0).Add("regapps", 0));

            string wall = us ? "A-WALL" : "A-Ss2510--FramedWallSystems";
            string dims = us ? "A-ANNO-DIMS" : "A-Zz2020--Dimensions";
            var layers = f.Table("layers");
            Layer(layers, "L0", "0", "7", "default", true, 3);
            Layer(layers, "L1", wall, "6", 0.4, true, 5);
            Layer(layers, "L2", dims, "2", 0.18, true, 2);
            Layer(layers, "L3", "Defpoints", "7", "default", false, 0);
            Layer(layers, "L4", "THIN-LINES", "250,0,0", 0.09, true, 1);
            layers.Add("L5").Set("name", "BASE|" + wall).Set("colorKey", "6").Set("colorIndex", 6).Set("linetype", "Continuous").Set("lineweight", 0.4)
                .Set("plottable", true).Set("isFrozen", false).Set("isOff", false).Set("isLocked", false).Set("isXrefDependent", true).Set("entityCount", 0).Set("isUsed", false);

            var e = f.Table("entities");
            Ent(e, "E01", "LINE", "0").Set("x1", 0).Set("y1", 0).Set("x2", 100).Set("y2", 0).Set("offOrthoDeg", 0);
            Ent(e, "E02", "LINE", wall).Set("x1", 0).Set("y1", 0).Set("x2", 100).Set("y2", 0).Set("offOrthoDeg", 0);
            Ent(e, "E03", "DIMENSION", dims).Set("textOverride", "").Set("dimStyle", us ? "ARCH-ANNO" : "MM-ANNO").Set("dimLfac", 1).Set("measurement", 100);
            Ent(e, "E04", "DIMENSION", wall).Set("textOverride", "<> TYP").Set("dimStyle", us ? "ARCH-ANNO" : "MM-ANNO").Set("dimLfac", 1).Set("measurement", 50);
            double ratio = us ? 48 : 100;
            double body = us ? 0.09375 : 2.5;
            Ent(e, "E05", "TEXT", dims, text: true).Set("textHeight", body * ratio).Set("textScaleRatio", ratio).Set("scaleSource", "viewport")
                .Set("textStyle", "NOTES").Set("isAnnotative", false);
            Ent(e, "E06", "MTEXT", dims, text: true).Set("textHeight", 7).Set("textScaleRatio", 1).Set("scaleSource", "annotative")
                .Set("textStyle", "NOTES").Set("isAnnotative", true);
            Ent(e, "E07", "HATCH", wall).Set("isAssociative", false).Set("patternName", "AR-B816").Set("patternScale", 1);
            Ent(e, "E08", "VIEWPORT", "0", space: "paper", layout: "A-101").Set("isLocked", true).Set("vpScaleRatio", ratio);
            Ent(e, "E09", "VIEWPORT", "0", space: "paper", layout: "A-101").Set("isLocked", false).Set("vpScaleRatio", 33);
            Ent(e, "E10", "LWPOLYLINE", wall, polyline: true).Set("isClosed", false).Set("hasWidth", true);
            Ent(e, "E11", "LINE", wall).Set("x1", 0).Set("y1", 0).Set("x2", 100).Set("y2", 0.5).Set("offOrthoDeg", 0.29);
            Ent(e, "E12", "INSERT", "0").Set("blockName", "BASE").Set("isXref", true);
            Ent(e, "E13", "SPLINE", wall);
            Ent(e, "E14", "TEXT", "0", space: "paper", layout: "A-101", text: true).Set("textHeight", body).Set("textScaleRatio", 1).Set("scaleSource", "paper")
                .Set("textStyle", "TAGS").Set("isAnnotative", false);

            var b = f.Table("blocks");
            b.Add("B1").Set("name", "DOOR").Set("isXref", false).Set("isXrefDependent", false).Set("isAnonymous", false).Set("isDynamic", true).Set("entityCount", 5)
                .Set("hardPropertyEntities", 0).Set("nonZeroLayerEntities", 0).Set("referenceCount", 3).Set("units", unit);
            b.Add("B2").Set("name", "*U4").Set("isXref", false).Set("isXrefDependent", false).Set("isAnonymous", true).Set("isDynamic", false).Set("entityCount", 2)
                .Set("hardPropertyEntities", 2).Set("nonZeroLayerEntities", 2).Set("referenceCount", 1).Set("units", unit);
            b.Add("B3").Set("name", "BASE|WINDOW").Set("isXref", false).Set("isXrefDependent", true).Set("isAnonymous", false).Set("isDynamic", false).Set("entityCount", 4)
                .Set("hardPropertyEntities", 4).Set("nonZeroLayerEntities", 4).Set("referenceCount", 2).Set("units", unit);
            b.Add("B4").Set("name", "NORTH").Set("isXref", false).Set("isXrefDependent", false).Set("isAnonymous", false).Set("isDynamic", false).Set("entityCount", 3)
                .Set("hardPropertyEntities", 1).Set("nonZeroLayerEntities", 0).Set("referenceCount", 1).Set("units", unit);

            var x = f.Table("xrefs");
            x.Add("X1").Set("name", "BASE").Set("depth", 1).Set("isOverlay", false).Set("path", "..\\base.dwg").Set("pathType", "relative")
                .Set("status", "resolved").Set("containsReferences", false).Set("insertLayer", us ? "G-ANNO-REFR" : "Z-Zz3030--ExternalReferences").Set("insertCount", 1);
            x.Add().Set("name", "SITE").Set("depth", 2).Set("isOverlay", true).Set("path", "C:\\x\\site.dwg").Set("pathType", "full")
                .Set("status", "not_found").Set("containsReferences", false);

            var s = f.Table("styles");
            s.Add("S1").Set("kind", "text").Set("name", "NOTES").Set("isUsed", true).Set("annotative", true).Set("font", "Arial").Set("isShx", false).Set("height", 0);
            s.Add("S2").Set("kind", "text").Set("name", "TAGS").Set("isUsed", true).Set("annotative", false).Set("font", "romans.shx").Set("isShx", true).Set("height", 0);
            s.Add("S3").Set("kind", "text").Set("name", "Standard").Set("isUsed", false).Set("annotative", false).Set("font", "txt.shx").Set("isShx", true).Set("height", 0);
            s.Add("D1").Set("kind", "dim").Set("name", us ? "ARCH-ANNO" : "MM-ANNO").Set("isUsed", true).Set("annotative", true)
                .Set("dimlunit", us ? 4 : 2).Set("dimdec", us ? 2 : 0).Set("dimrnd", 0).Set("dimlfac", 1).Set("dimtxt", body).Set("dimtxsty", "NOTES")
                .Set("stepMm", us ? 6.35 : 1).Set("unitSystem", us ? "imperial" : "metric");
            s.Add("D2").Set("kind", "dim").Set("name", "Standard").Set("isUsed", true).Set("annotative", false)
                .Set("dimlunit", 2).Set("dimdec", 4).Set("dimrnd", 0).Set("dimlfac", 1).Set("dimtxt", 0.18).Set("dimtxsty", "Standard")
                .Set("stepMm", us ? 0.00254 : 0.0001).Set("unitSystem", us ? "imperial" : "metric");

            var l = f.Table("layouts");
            l.Add("LY1").Set("name", "A-101").Set("tabOrder", 1).Set("viewportCount", 2).Set("paperSize", "ARCH_D").Set("plotStyleTable", "office.ctb");
            l.Add("LY2").Set("name", "Layout2").Set("tabOrder", 2).Set("viewportCount", 0).Set("paperSize", "Letter");

            var a = f.Table("annotations");
            a.Add("E05").Set("type", "TEXT").Set("space", "model").Set("layout", "Model").Set("layer", dims).Set("minX", 0).Set("minY", 0).Set("maxX", 40).Set("maxY", 5);
            a.Add("E06").Set("type", "MTEXT").Set("space", "model").Set("layout", "Model").Set("layer", dims).Set("minX", 30).Set("minY", 1).Set("maxX", 60).Set("maxY", 6);
            a.Add("E14").Set("type", "TEXT").Set("space", "paper").Set("layout", "A-101").Set("layer", "0").Set("minX", 30).Set("minY", 1).Set("maxX", 60).Set("maxY", 6);

            var jp = f.Table("jobPlacement");
            jp.Add("E01").Set("type", "LINE").Set("space", "model").Set("layout", "Model").Set("layer", "0").Set("shown", "full").Set("viewportCount", 2);
            jp.Add("E02").Set("type", "LINE").Set("space", "model").Set("layout", "Model").Set("layer", wall).Set("shown", "none").Set("viewportCount", 2);
            return f;
        }

        private static void Layer(FactTable t, string id, string name, string colorKey, JsonValue lineweight, bool plottable, int count)
        {
            var r = t.Add(id).Set("name", name).Set("colorKey", colorKey).Set("linetype", "Continuous").Set("lineweight", lineweight)
                .Set("plottable", plottable).Set("isFrozen", false).Set("isOff", false).Set("isLocked", false).Set("isXrefDependent", false)
                .Set("entityCount", count).Set("isUsed", count > 0);
            if (!colorKey.Contains(",")) r.Set("colorIndex", int.Parse(colorKey));
        }

        private static FactRecord Ent(FactTable t, string id, string type, string layer, string space = "model", string layout = "Model", bool text = false, bool polyline = false) =>
            t.Add(id).Set("type", type).Set("space", space).Set("layout", layout).Set("layer", layer).Set("color", "bylayer").Set("linetype", "bylayer")
                .Set("lineweight", id == "E02" ? (JsonValue)0.5 : "bylayer").Set("isText", text).Set("isPolyline", polyline).Set("isFlat", true);

        public static Facts Revit()
        {
            var f = new Facts("revit");
            f.Meta.Add("units.length", "mm");
            f.Table("document").Add().Set("title", "Model").Set("isWorkshared", true).Set("extentsMaxDistanceMm", 150000).Set("designOptionSetCount", 0)
                .Set("designOptionCount", 0).Set("groupInstanceCount", 4).Set("unusedGroupTypeCount", 0).Set("unusedFamilyCount", 2).Set("importedImageCount", 0)
                .Set("planRegionCount", 0).Set("lineStyleCount", 40).Set("fillPatternCount", 60).Set("textTypeCount", 8);

            var w = f.Table("warnings");
            for (int i = 0; i < 2; i++)
                w.Add().Set("description", "Highlighted walls overlap.").Set("failureDefinitionId", "f1").Set("severity", "warning")
                    .Set("elementIds", JsonArray.FromStrings(new[] { "301", "302" })).Set("elementCount", 2);
            w.Add().Set("description", "Wall is slightly off axis and may cause inaccuracies.").Set("failureDefinitionId", "f2").Set("severity", "warning")
                .Set("elementIds", JsonArray.FromStrings(new[] { "301" })).Set("elementCount", 1);

            var v = f.Table("views");
            View(v, "201", "01 - Ground Floor", "FloorPlan", onSheet: true, sheet: "A-101", template: true, detail: "medium");
            View(v, "202", "IFC Export 3D", "ThreeD", onSheet: false, sheet: null, template: false, detail: "medium");
            View(v, "203", "Section 1", "Section", onSheet: false, sheet: null, template: false, detail: "fine");
            v.Add("204").Set("name", "Arch Plan Template").Set("viewType", "FloorPlan").Set("isTemplate", true).Set("isSchedule", false).Set("isDependent", false)
                .Set("onSheet", false).Set("hasTemplate", false).Set("templateApplicable", true).Set("hiddenElementCount", 0).Set("detailLevel", "medium")
                .Set("scale", 100).Set("entourageVisible", false);

            var e = f.Table("elements");
            Elem(e, "301", "OST_Walls", "W1").Set("familyName", "Basic Wall").Set("typeName", "Interior").Set("levelName", "Level 0")
                .Set("levelOffsetAbsMm", 0).Set("levelSpan", 1).Set("isAttached", false).Set("worksetName", "Architecture");
            Elem(e, "302", "OST_Furniture", "F1").Set("familyName", "Desk").Set("typeName", "1600").Set("levelName", "Level 0")
                .Set("levelOffsetAbsMm", 300).Set("worksetName", "Architecture");
            Elem(e, "303", "OST_Furniture", "F2").Set("familyName", "Desk").Set("typeName", "1600").Set("levelName", "Level 0")
                .Set("levelOffsetAbsMm", 0).Set("worksetName", "FF&E");
            Elem(e, "304", "OST_Rooms", null).Set("number", "001").Set("levelName", "Level 0").Set("roomState", "ok").Set("worksetName", "Architecture");
            Elem(e, "305", "OST_Rooms", null).Set("number", "001").Set("levelName", "Level 0").Set("roomState", "not_enclosed").Set("worksetName", "Architecture");
            Elem(e, "306", "OST_Levels", null).Set("isDatum", true).Set("isPinned", true).Set("worksetName", "Shared Levels and Grids");
            Elem(e, "307", "OST_TextNotes", null).Set("isViewSpecific", true).Set("ownerViewKind", "view").Set("text", "D-101").Set("worksetName", "Architecture");
            Elem(e, "308", "OST_MEPSpaces", null).Set("number", "S1").Set("roomId", "304").Set("worksetName", "Architecture");

            var t = f.Table("types");
            t.Add("401").Set("name", "Desk 1600").Set("familyName", "Desk").Set("category", "OST_Furniture").Set("isInPlace", false).Set("isLoadable", true)
                .Set("instanceCount", 2).Set("ifcExportAs", "IfcFurniture");
            t.Add("402").Set("name", "Generic Model 1").Set("familyName", "Blob").Set("category", "OST_GenericModel").Set("isInPlace", true)
                .Set("isLoadable", false).Set("instanceCount", 1);
            t.Add("501").Set("name", "Linear - 1/256").Set("familyName", "Linear Dimension Style").Set("category", "OST_Dimensions").Set("isInPlace", false)
                .Set("isLoadable", false).Set("instanceCount", 4).Set("dimensionStyle", "linear").Set("stepMm", 0.0992).Set("unitSystem", "imperial");
            t.Add("502").Set("name", "Linear - mm").Set("familyName", "Linear Dimension Style").Set("category", "OST_Dimensions").Set("isInPlace", false)
                .Set("isLoadable", false).Set("instanceCount", 9).Set("dimensionStyle", "linear").Set("stepMm", 1).Set("unitSystem", "metric");

            var k = f.Table("links");
            k.Add("601").Set("kind", "cad").Set("name", "survey.dwg").Set("isLinked", true).Set("isPinned", true).Set("pathType", "relative")
                .Set("isWorkshared", true).Set("worksetName", "Architecture").Set("isViewSpecific", false);
            k.Add("602").Set("kind", "revit").Set("name", "structure.rvt").Set("isLinked", true).Set("isPinned", true).Set("pathType", "relative")
                .Set("isWorkshared", true).Set("worksetName", "Link - Structure RVT").Set("isViewSpecific", false);

            var a = f.Table("annotations");
            a.Add("701").Set("category", "OST_TextNotes").Set("kind", "text").Set("viewId", "201").Set("viewName", "01 - Ground Floor")
                .Set("minU", 0).Set("minV", 0).Set("maxU", 20).Set("maxV", 3).Set("hasLeader", false).Set("estimated", false);
            a.Add("702").Set("category", "OST_DoorTags").Set("kind", "tag").Set("viewId", "201").Set("viewName", "01 - Ground Floor")
                .Set("minU", 15).Set("minV", 0).Set("maxU", 25).Set("maxV", 3).Set("hasLeader", false).Set("estimated", false);
            a.Add("703").Set("category", "OST_DoorTags").Set("kind", "tag").Set("viewId", "201").Set("viewName", "01 - Ground Floor")
                .Set("minU", 15).Set("minV", 0).Set("maxU", 25).Set("maxV", 3).Set("hasLeader", true).Set("estimated", false);

            var jp = f.Table("jobPlacement");
            jp.Add("301").Set("category", "OST_Walls").Set("viewSpecific", false).Set("shown", "partial").Set("candidateViews", 1);
            jp.Add("307").Set("category", "OST_TextNotes").Set("viewSpecific", true).Set("shown", "notChecked").Set("reason", "annotation").Set("candidateViews", 0);
            return f;
        }

        private static void View(FactTable t, string id, string name, string type, bool onSheet, string? sheet, bool template, string detail)
        {
            var r = t.Add(id).Set("name", name).Set("viewType", type).Set("isTemplate", false).Set("isSchedule", false).Set("isDependent", false)
                .Set("onSheet", onSheet).Set("hasTemplate", template).Set("templateApplicable", true).Set("hiddenElementCount", 0)
                .Set("detailLevel", detail).Set("scale", 100).Set("entourageVisible", false);
            if (sheet != null) r.Set("sheetNumber", sheet);
        }

        private static FactRecord Elem(FactTable t, string id, string category, string? mark)
        {
            var r = t.Add(id).Set("category", category).Set("isPinned", false).Set("isDatum", false).Set("isWorkshared", true).Set("isViewSpecific", false);
            if (mark != null) r.Set("mark", mark);
            return r;
        }
    }
}
