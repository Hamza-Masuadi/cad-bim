using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>
    /// The Core side of family.fromcad and family.place fromPlacements: loading placements.json for Revit, family and
    /// template names, the block-reference-to-instance maths and the schemas. The Revit handlers cannot run here.
    /// </summary>
    public class CadFamiliesTests
    {
        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["family.fromcad"] = @"{ ""file"": ""C:\\Projects\\GF\\blocks\\placements.json"", ""blocks"": [""BED-DOUBLE"", ""CHAIR-01""], ""familyPrefix"": ""CAD-"",
                ""category"": ""OST_Furniture"", ""height"": 450, ""symbol"": ""lines"",
                ""overrides"": { ""CHAIR-01"": { ""height"": 820, ""typeName"": ""Oak"" } }, ""outputDir"": ""C:\\Projects\\GF\\families"" }",
        };

        private const string PlaceArgs = @"{ ""fromPlacements"": { ""file"": ""C:\\Projects\\GF\\blocks\\placements.json"", ""align"": { ""cadLink"": ""GF-R10.dwg"" } }, ""level"": ""Level 1"" }";

        private static readonly string SamplePath = Path.Combine(DataFiles.DataDir, "..", "scripts", "testdata", "revit", "cad-furniture", "placements.json");

        private static OpSchema Schema(string name) => OpRegistry.Default.Find(name, AppKind.Revit) ?? throw new Xunit.Sdk.XunitException(name + " is not declared for Revit");

        private static List<Issue> Errors(string op, string args) =>
            JobValidator.ValidateArgs(Schema(op), (JsonObject)JsonValue.Parse(args), LengthUnit.Millimetre).Where(i => i.IsError).ToList();

        private static PlacementReference Ref(double x, double y, double rot = 0, double sx = 1, double sy = 1, string space = "model") =>
            new PlacementReference { Handle = "1A", Space = space, Position = new[] { x, y, 0 }, RotationDeg = rot, Scale = new[] { sx, sy, 1 }, Mirrored = sx * sy < 0 };

        private static PlannedInstance Plan(PlacementReference r, PlanFrame f, double factor = 1)
        {
            var p = CadPlacement.Plan(r, f, factor, out var why);
            Assert.True(p != null, why);
            return p!;
        }

        private static readonly PlanFrame Identity = new PlanFrame(0, 0, 1, 0, 0, 1);

        private static void Basis(PlannedInstance p, double xx, double xy, double yx, double yy)
        {
            Assert.Equal(xx, p.BasisXx, 9);
            Assert.Equal(xy, p.BasisXy, 9);
            Assert.Equal(yx, p.BasisYx, 9);
            Assert.Equal(yy, p.BasisYy, 9);
        }

        // ------------------------------------------------------------------ schemas

        [Fact]
        public void FromCadIsDeclaredForRevitAndMustBeAlone()
        {
            var s = Schema("family.fromcad");
            Assert.False(s.Ensure);
            Assert.False(s.Destructive);
            Assert.True(s.FindField("file")!.Required);
            Assert.Empty(Errors("family.fromcad", Examples["family.fromcad"]));

            string Job(params string[] ops) => new JsonObject()
                .Add("jobId", "2026-11-28T09-00-00Z-cad-families").Add("app", "revit")
                .Add("units", new JsonObject().Add("length", "mm"))
                .Add("ops", new JsonArray(ops.Select((o, i) => (JsonValue)JsonValue.Parse(o.Replace("ID", "op" + i)))))
                .ToJson();
            string fromcad = @"{ ""id"": ""ID"", ""op"": ""family.fromcad"", ""args"": { ""file"": ""C:\\b\\placements.json"" } }";
            var alone = JobValidator.Validate(Job(fromcad), OpRegistry.Default);
            Assert.True(alone.Ok, string.Join("\n", alone.Issues));
            var shared = JobValidator.Validate(Job(fromcad, @"{ ""id"": ""ID"", ""op"": ""param.set"", ""args"": { ""elements"": [""1""], ""values"": { ""Comments"": ""x"" } } }"), OpRegistry.Default);
            Assert.Contains(shared.Issues, i => i.Code == JobValidator.OpNotAlone);
        }

        [Fact]
        public void FromCadRefusesBadEnumsAndHeights()
        {
            Assert.NotEmpty(Errors("family.fromcad", @"{ ""file"": ""C:\\b\\p.json"", ""body"": ""mesh"" }"));
            Assert.NotEmpty(Errors("family.fromcad", @"{ ""file"": ""C:\\b\\p.json"", ""symbol"": ""hatch"" }"));
            Assert.NotEmpty(Errors("family.fromcad", @"{ ""file"": ""C:\\b\\p.json"", ""height"": -10 }"));
            Assert.NotEmpty(Errors("family.fromcad", @"{ ""file"": ""C:\\b\\p.json"", ""overrides"": { ""X"": { ""body"": ""mesh"" } } }"));
            Assert.NotEmpty(Errors("family.fromcad", @"{ ""blocks"": [""X""] }"));
        }

        [Fact]
        public void FamilyPlaceTakesPointsOrFromPlacementsNotBoth()
        {
            Assert.Empty(Errors("family.place", RevitModelWave2Tests.Examples["family.place"]));
            Assert.Empty(Errors("family.place", PlaceArgs));
            Assert.Empty(Errors("family.place", @"{ ""fromPlacements"": { ""file"": ""C:\\b\\p.json"", ""blocks"": [""CHAIR-01""], ""familyMap"": { ""CHAIR-01"": ""Chair : Oak"" },
                ""align"": { ""translate"": [1000, 2000], ""rotationDeg"": 30 }, ""failOnRefused"": true }, ""level"": ""Level 1"", ""offset"": 15, ""mark"": ""CH"" }"));

            var both = PlaceArgs.Replace(@"""level""", @"""points"": [[0, 0]], ""type"": ""Desk : A"", ""level""");
            Assert.NotEmpty(Errors("family.place", both));
            Assert.NotEmpty(Errors("family.place", @"{ ""level"": ""Level 1"" }"));
            Assert.NotEmpty(Errors("family.place", @"{ ""points"": [[0, 0]], ""level"": ""Level 1"" }"));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@", ""level"": ""Level 1""", "")));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@"""level""", @"""type"": ""Desk : A"", ""level""")));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@"""level""", @"""rotation"": 90, ""level""")));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@"""cadLink"": ""GF-R10.dwg""", @"""cadLink"": ""GF-R10.dwg"", ""translate"": [0, 0]")));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@"""cadLink"": ""GF-R10.dwg""", @"""cadLink"": ""GF-R10.dwg"", ""rotationDeg"": 10")));
            Assert.NotEmpty(Errors("family.place", PlaceArgs.Replace(@"""cadLink"": ""GF-R10.dwg""", "")));
        }

        [Fact]
        public void FamilyPlaceDeclaresTheRefusedOutput()
        {
            var s = Schema("family.place");
            Assert.Contains(s.Outputs, o => o.Name == "refused");
            Assert.Contains(s.Outputs, o => o.Name == "elements");
        }

        // ------------------------------------------------------------------ loading placements.json

        [Fact]
        public void TheSampleFileIsAValidPlacementsFile()
        {
            var f = Placements.ReadFile(SamplePath);
            Assert.Empty(Placements.Validate(f));
            Assert.Empty(CadFamilies.RevitRules(f));
            Assert.Equal(3, f.Blocks.Count);
            Assert.True(f.Find("chair-01")!.References[1].Mirrored);
        }

        [Fact]
        public void LoadNeedsAFullPathToAnExistingFile()
        {
            var issues = new List<Issue>();
            Assert.Null(CadFamilies.Load("blocks/placements.json", "$.ops[0].args.file", issues));
            Assert.Contains(issues, i => i.Code == IssueCodes.InvalidValue && i.Path == "$.ops[0].args.file");
            issues.Clear();
            Assert.Null(CadFamilies.Load(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"), "placements.json"), "$.f", issues));
            Assert.Contains(issues, i => i.Code == IssueCodes.NotFound);
        }

        [Fact]
        public void LoadChecksTheFileAndTheDrawingsBesideIt()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sa-cadfam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "placements.json");
                File.Copy(SamplePath, path);
                var issues = new List<Issue>();
                Assert.Null(CadFamilies.Load(path, "$.f", issues));
                Assert.Contains(issues, i => i.IsError && i.Path == "$.f" && i.Message.StartsWith("placements.json $.blocks[0].file"));

                foreach (var b in new[] { "BED-DOUBLE", "CHAIR-01", "TABLE-1200" }) File.WriteAllBytes(Path.Combine(dir, b + ".dwg"), new byte[0]);
                issues.Clear();
                var f = CadFamilies.Load(path, "$.f", issues);
                Assert.True(f != null, string.Join("; ", issues));
                Assert.Empty(issues);

                File.WriteAllText(path, File.ReadAllText(SamplePath).Replace(@"""name"": ""CHAIR-01""", @"""name"": ""BED-DOUBLE"""));
                issues.Clear();
                Assert.Null(CadFamilies.Load(path, "$.f", issues));
                Assert.Contains(issues, i => i.IsError && i.Code == Placements.InvalidCode);

                File.WriteAllText(path, "{ not json");
                issues.Clear();
                Assert.Null(CadFamilies.Load(path, "$.f", issues));
                Assert.Contains(issues, i => i.Code == Placements.InvalidCode);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void RevitRulesNeedAnImportUnitAndWarnAboutLayouts()
        {
            var f = Placements.ReadFile(SamplePath);
            f.Source.Units = "yd";
            Assert.Contains(CadFamilies.RevitRules(f), i => i.IsError && i.Code == IssueCodes.UnitsUnsupported && i.Path == "$.source.units");
            f = Placements.ReadFile(SamplePath);
            f.Blocks[1].References[0].Space = "Layout1";
            var w = Assert.Single(CadFamilies.RevitRules(f));
            Assert.False(w.IsError);
            Assert.Equal("$.blocks[1].references[0].space", w.Path);
        }

        // ------------------------------------------------------------------ names

        [Fact]
        public void FamilyNamesComeFromPrefixOrOverride()
        {
            Assert.Equal("CAD-BED", CadFamilies.FamilyName("BED", "CAD-", null));
            Assert.Equal("BED", CadFamilies.FamilyName("BED", null, "  "));
            Assert.Equal("Double Bed", CadFamilies.FamilyName("BED", "CAD-", " Double Bed "));
            Assert.Null(CadFamilies.FamilyNameProblem("CAD-BED 1600"));
            foreach (var bad in new[] { "", " BED", "BED:1", "A[1]", "CON", "a/b" })
                Assert.NotNull(CadFamilies.FamilyNameProblem(bad));
        }

        [Fact]
        public void TemplateNamesPreferTheJobsUnitSystem()
        {
            Assert.Equal(new[] { "Metric Furniture.rft", "Furniture.rft" }, CadFamilies.TemplateNames(new[] { "Furniture", "furniture" }, true));
            Assert.Equal(new[] { "Furniture.rft", "Metric Furniture.rft", "Mobilier.rft", "Metric Mobilier.rft" },
                CadFamilies.TemplateNames(new[] { "Furniture", "Mobilier" }, false));
            Assert.Equal("Plumbing Fixture", CadFamilies.EnglishTemplateStem("OST_PlumbingFixtures"));
            Assert.Null(CadFamilies.EnglishTemplateStem("OST_Walls"));
        }

        // ------------------------------------------------------------------ maths

        [Fact]
        public void IdentityFramePlacesAtThePointUnrotated()
        {
            var p = Plan(Ref(100, 200), Identity);
            Assert.Equal(100, p.X, 9);
            Assert.Equal(200, p.Y, 9);
            Assert.False(p.Mirrored);
            Assert.Equal(0, p.Rotation, 9);
            Basis(p, 1, 0, 0, 1);
        }

        [Fact]
        public void RotationIsCounterClockwise()
        {
            var p = Plan(Ref(0, 0, 90), Identity);
            Assert.Equal(Math.PI / 2, p.Rotation, 9);
            Basis(p, 0, 1, -1, 0);
            Assert.Equal(90, p.RotationDegrees, 9);
            Assert.Equal(270, Plan(Ref(0, 0, -90 + 360), Identity).RotationDegrees, 9);
        }

        [Fact]
        public void MirrorInXKeepsTheRotation()
        {
            var p = Plan(Ref(0, 0, 0, -1, 1), Identity);
            Assert.True(p.Mirrored);
            Assert.Equal(0, p.Rotation, 9);
            Basis(p, -1, 0, 0, 1);
            var q = Plan(Ref(0, 0, 30, -1, 1), Identity);
            Assert.True(q.Mirrored);
            Assert.Equal(30, q.RotationDegrees, 9);
        }

        [Fact]
        public void MirrorInYIsMirrorInXTurnedHalfway()
        {
            var p = Plan(Ref(0, 0, 0, 1, -1), Identity);
            Assert.True(p.Mirrored);
            Assert.Equal(180, p.RotationDegrees, 9);
            Basis(p, 1, 0, 0, -1);
        }

        [Fact]
        public void BothScalesNegativeIsAHalfTurnNotAMirror()
        {
            var p = Plan(Ref(0, 0, 0, -1, -1), Identity);
            Assert.False(p.Mirrored);
            Assert.Equal(180, p.RotationDegrees, 9);
        }

        [Fact]
        public void AReflectedFrameMirrorsAnUnmirroredReference()
        {
            var frame = new PlanFrame(0, 0, -1, 0, 0, 1);
            var p = Plan(Ref(100, 50, 0), frame);
            Assert.Equal(-100, p.X, 9);
            Assert.Equal(50, p.Y, 9);
            Assert.True(p.Mirrored);
            Assert.Equal(0, p.Rotation, 9);
            var q = Plan(Ref(0, 0, 0, -1, 1), frame);
            Assert.False(q.Mirrored);
            Basis(q, 1, 0, 0, 1);
        }

        [Fact]
        public void RotateThenTranslateFrameAndUnitFactor()
        {
            var frame = PlanFrame.RotateThenTranslate(1000, 0, 90);
            var p = Plan(Ref(100, 0, 0), frame);
            Assert.Equal(1000, p.X, 9);
            Assert.Equal(100, p.Y, 9);
            Assert.Equal(90, p.RotationDegrees, 9);
            var m = Plan(Ref(12500, 8400, 90), Identity, 0.001);
            Assert.Equal(12.5, m.X, 9);
            Assert.Equal(8.4, m.Y, 9);
        }

        [Fact]
        public void ALinkTransformCarryingTheUnitScaleIsUsedAsIs()
        {
            double k = 1 / 304.8;
            Assert.True(CadPlacement.InputScale(new PlanFrame(5, 5, 1, 0, 0, 1), k, out double a, out _));
            Assert.Equal(k, a, 12);
            double c = Math.Cos(0.3) * k, s = Math.Sin(0.3) * k;
            Assert.True(CadPlacement.InputScale(new PlanFrame(5, 5, c, s, -s, c), k, out a, out _));
            Assert.Equal(1, a, 12);
            var p = Plan(Ref(304.8, 0), new PlanFrame(0, 0, k, 0, 0, k), a);
            Assert.Equal(1, p.X, 9);
            Assert.False(p.Mirrored);
            Basis(p, 1, 0, 0, 1);
        }

        [Fact]
        public void OtherScalesAndShearsAreRefused()
        {
            Assert.False(CadPlacement.InputScale(new PlanFrame(0, 0, 2, 0, 0, 2), 1 / 304.8, out _, out var e1));
            Assert.Contains("scales by 2", e1);
            Assert.False(CadPlacement.InputScale(new PlanFrame(0, 0, 1, 0, 0.5, 1), 1 / 304.8, out _, out var e2));
            Assert.Contains("shears", e2);
        }

        [Fact]
        public void ScaledLayoutAndUpsideDownReferencesAreRefused()
        {
            Assert.Null(CadPlacement.Plan(Ref(0, 0, 0, 2, 2), Identity, 1, out var a));
            Assert.Contains("scaled", a);
            Assert.Null(CadPlacement.Plan(Ref(0, 0, space: "Layout1"), Identity, 1, out var b));
            Assert.Contains("Layout1", b);
            var r = Ref(0, 0);
            r.Scale = new[] { 1.0, 1, -1 };
            Assert.Null(CadPlacement.Plan(r, Identity, 1, out var c));
            Assert.Contains("Z scale", c);
        }
    }
}
