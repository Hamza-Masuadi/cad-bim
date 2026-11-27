using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The static side of family.create: schema, cross-field rules and the furniture recipes. Revit cannot run here.</summary>
    public class FamilyCreateTests
    {
        private const string SideTable = @"{
  ""name"": ""Side Table"",
  ""category"": ""Furniture"",
  ""parameters"": [
    { ""name"": ""Width"", ""spec"": ""length"" },
    { ""name"": ""Depth"", ""spec"": ""length"" },
    { ""name"": ""Height"", ""spec"": ""length"" },
    { ""name"": ""Top Thickness"", ""spec"": ""length"", ""value"": 30 },
    { ""name"": ""Top Underside"", ""spec"": ""length"", ""formula"": ""Height - Top Thickness"" },
    { ""name"": ""Top Material"", ""spec"": ""material"", ""value"": ""Oak"" }
  ],
  ""types"": [
    { ""name"": ""500x500"", ""values"": { ""Width"": 500, ""Depth"": 500, ""Height"": 550 } },
    { ""name"": ""600x400"", ""values"": { ""Width"": 600, ""Depth"": 400, ""Height"": 500 } }
  ],
  ""planes"": [
    { ""name"": ""Left"", ""axis"": ""x"", ""at"": -250 },
    { ""name"": ""Right"", ""axis"": ""x"", ""at"": 250 },
    { ""name"": ""Front"", ""axis"": ""y"", ""at"": -500 },
    { ""name"": ""Underside"", ""axis"": ""z"", ""at"": 520 },
    { ""name"": ""Top"", ""axis"": ""z"", ""at"": 550 }
  ],
  ""dimensions"": [
    { ""planes"": [""Left"", ""Right""], ""parameter"": ""Width"" },
    { ""planes"": [""Left"", ""Center (Left/Right)"", ""Right""], ""equal"": true },
    { ""planes"": [""Front"", ""Center (Front/Back)""], ""parameter"": ""Depth"" },
    { ""planes"": [""Ref. Level"", ""Underside""], ""parameter"": ""Top Underside"" },
    { ""planes"": [""Ref. Level"", ""Top""], ""parameter"": ""Height"" }
  ],
  ""forms"": [
    { ""id"": ""top"", ""kind"": ""extrusion"", ""box"": { ""x"": [""Left"", ""Right""], ""y"": [""Front"", ""Center (Front/Back)""], ""z"": [""Underside"", ""Top""] }, ""material"": ""Top Material"" },
    { ""id"": ""leg"", ""kind"": ""extrusion"", ""profile"": [ { ""centre"": [0, -250], ""radius"": 30 } ], ""end"": 520,
      ""lock"": { ""-z"": ""Ref. Level"", ""+z"": ""Underside"" }, ""material"": ""Top Material"" }
  ],
  ""symbols"": [ { ""rect"": { ""x"": [""Left"", ""Right""], ""y"": [""Front"", ""Center (Front/Back)""] } } ],
  ""flex"": { ""x"": ""Width"", ""y"": ""Depth"", ""z"": ""Height"" }
}";

        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["family.create"] = SideTable,
        };

        private static readonly string FamiliesDir = Path.Combine(DataFiles.DataDir, "families");

        private static string Job(JsonValue args, int extraOps = 0)
        {
            var ops = new JsonArray().Add(new JsonObject().Add("id", "fc").Add("op", "family.create").Add("args", args.DeepClone()));
            for (int i = 0; i < extraOps; i++)
                ops.Add(new JsonObject().Add("id", "p" + i).Add("op", "family.place").Add("args", new JsonObject()));
            return new JsonObject()
                .Add("jobId", "family-create-test").Add("title", "family.create test").Add("app", "revit")
                .Add("document", new JsonObject().Add("path", "C:/Projects/2417/2417-ARCH.rvt").Add("fingerprint", "copy-from-snapshot"))
                .Add("units", new JsonObject().Add("length", "mm"))
                .Add("ops", ops).ToJson();
        }

        private static JobValidation V(JsonValue args) => JobValidator.Validate(Job(args), OpRegistry.Default);

        private static JsonObject Args() => JsonValue.Parse(SideTable).AsObject();

        private static JsonObject At(JsonObject root, string field, int index) => root[field]!.AsArray()[index].AsObject();

        private static void HasError(JsonObject args, string code, string pathPart)
        {
            var v = V(args);
            Assert.True(v.Issues.Any(i => i.IsError && i.Code == code && i.Path.Contains(pathPart)),
                "expected error " + code + " at " + pathPart + "; got:\n" + string.Join("\n", v.Issues));
        }

        private static void HasWarning(JsonObject args, string code, string text)
        {
            var v = V(args);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.True(v.Issues.Any(i => !i.IsError && i.Code == code && i.Message.Contains(text)), "expected warning '" + text + "'; got:\n" + string.Join("\n", v.Issues));
        }

        // ------------------------------------------------------------------ schema

        [Fact]
        public void FamilyCreateIsDeclaredForRevitWithItsFields()
        {
            var s = OpRegistry.Default.Find("family.create", AppKind.Revit);
            Assert.NotNull(s);
            Assert.False(s!.Destructive);
            foreach (var f in new[] { "name", "category", "template", "outputDir", "overwrite", "parameterValues", "load", "parameters", "types", "planes", "dimensions", "forms", "symbols", "flex" })
                Assert.NotNull(s.FindField(f));
            Assert.True(s.FindField("name")!.Required);
            Assert.True(s.FindField("types")!.Required);
            Assert.True(s.FindField("forms")!.Required);
            Assert.Equal(new[] { "familyName", "file", "types", "typeIds", "flex" }.OrderBy(x => x), s.Outputs.Select(o => o.Name).Where(n => n != "family").OrderBy(x => x));
        }

        [Fact]
        public void ExampleIsLintCleanWithoutWarnings()
        {
            var v = V(Args());
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Empty(v.Issues);
        }

        [Fact]
        public void MustBeAloneInItsJob()
        {
            var v = JobValidator.Validate(Job(Args(), extraOps: 1), OpRegistry.Default);
            Assert.Contains(v.Issues, i => i.IsError && i.Code == JobValidator.OpNotAlone && i.Path == "$.ops[0].op");
        }

        [Fact]
        public void NeedsCategoryOrTemplate()
        {
            var a = Args();
            a.Remove("category");
            Assert.False(V(a).Ok);
            a.Add("template", "C:/ProgramData/Autodesk/RVT 2025/Family Templates/English/Metric Furniture.rft");
            Assert.True(V(a).Ok, string.Join("\n", V(a).Issues));
        }

        [Fact]
        public void ValidateArgsRunsTheSameRules()
        {
            var a = Args();
            At(a, "types", 0)["values"]!.AsObject().Set("Nope", 1);
            var issues = JobValidator.ValidateArgs(OpRegistry.Default.Find("family.create", AppKind.Revit)!, a, LengthUnit.Millimetre);
            Assert.Contains(issues, i => i.IsError && i.Code == IssueCodes.NotFound && i.Path.Contains("Nope"));
        }

        // ------------------------------------------------------------------ names and paths

        [Theory]
        [InlineData("Bed: Double")]
        [InlineData("CON")]
        [InlineData("Bed.rfa")]
        [InlineData(" Bed")]
        public void BadFamilyNamesAreRefused(string name)
        {
            var a = Args();
            a.Set("name", name);
            HasError(a, IssueCodes.InvalidValue, "$.ops[0].args.name");
        }

        [Fact]
        public void PathsMustBeAbsoluteWindowsPaths()
        {
            var a = Args();
            a.Set("outputDir", "families/out");
            HasError(a, IssueCodes.InvalidValue, "outputDir");
            a = Args();
            a.Set("template", "\\\\server\\templates\\Furniture.txt");
            HasError(a, IssueCodes.InvalidValue, "template");
        }

        [Fact]
        public void DuplicateNamesAreRefusedIgnoringCase()
        {
            var a = Args();
            a["parameters"]!.AsArray().Add(new JsonObject().Add("name", "width").Add("spec", "length"));
            HasError(a, IssueCodes.DuplicateId, "parameters[6].name");
            a = Args();
            a["types"]!.AsArray().Add(new JsonObject().Add("name", "500X500").Add("values", new JsonObject().Add("Width", 1).Add("Depth", 1).Add("Height", 1)));
            HasError(a, IssueCodes.DuplicateId, "types[2].name");
            a = Args();
            a["planes"]!.AsArray().Add(new JsonObject().Add("name", "LEFT").Add("axis", "x").Add("at", 10));
            HasError(a, IssueCodes.DuplicateId, "planes[5].name");
        }

        [Fact]
        public void ReservedPlanesCannotBeDeclared()
        {
            var a = Args();
            a["planes"]!.AsArray().Add(new JsonObject().Add("name", "Ref. Level").Add("axis", "z").Add("at", 0));
            HasError(a, IssueCodes.AlreadyExists, "planes[5].name");
        }

        // ------------------------------------------------------------------ formulas and values

        [Fact]
        public void FormulasNameOnlyDeclaredParameters()
        {
            var a = Args();
            At(a, "parameters", 4).Set("formula", "Height - Top Thicknes");
            HasError(a, IssueCodes.NotFound, "parameters[4].formula");
        }

        [Fact]
        public void FormulaCyclesAreRefused()
        {
            var a = Args();
            At(a, "parameters", 3).Remove("value");
            At(a, "parameters", 3).Set("formula", "Height - Top Underside");
            HasError(a, IssueCodes.Constraint, "formula");
        }

        [Fact]
        public void UnbalancedBracketsAreRefused()
        {
            var a = Args();
            At(a, "parameters", 4).Set("formula", "(Height - Top Thickness");
            HasError(a, IssueCodes.InvalidValue, "parameters[4].formula");
        }

        [Fact]
        public void TypeFormulaCannotUseInstanceParameter()
        {
            var a = Args();
            At(a, "parameters", 3).Set("instance", true);
            HasError(a, IssueCodes.Constraint, "parameters[4].formula");
        }

        [Fact]
        public void FormulaParameterTakesNoValue()
        {
            var a = Args();
            At(a, "types", 0)["values"]!.AsObject().Set("Top Underside", 400);
            HasError(a, IssueCodes.Constraint, "Top Underside");
        }

        [Fact]
        public void TypesGiveValuesOnlyToDeclaredParametersOfTheRightKind()
        {
            var a = Args();
            At(a, "types", 1)["values"]!.AsObject().Set("Widht", 500);
            HasError(a, IssueCodes.NotFound, "Widht");
            a = Args();
            At(a, "types", 1)["values"]!.AsObject().Set("Width", "wide");
            HasError(a, IssueCodes.Type, "types[1].values.Width");
            a = Args();
            At(a, "types", 1)["values"]!.AsObject().Set("Top Material", 3);
            HasError(a, IssueCodes.Type, "Top Material");
        }

        [Fact]
        public void DrivingLengthsMustBePositiveAndGivenForEveryType()
        {
            var a = Args();
            At(a, "types", 1)["values"]!.AsObject().Set("Depth", 0);
            HasError(a, IssueCodes.Range, "types[1].values.Depth");
            a = Args();
            At(a, "types", 1)["values"]!.AsObject().Remove("Height");
            HasError(a, IssueCodes.Required, "types[1]");
        }

        [Fact]
        public void FormulaAnalysisMatchesNamesWithSpacesAndKnowsFunctionsAndUnits()
        {
            var used = FamilyFormulas.Analyse("if(Seat Height > 450 mm, Seat Height - Top, roundup(Seat / 2))", new[] { "Seat", "Seat Height", "Top" }, out var unknown, out var syntax);
            Assert.Null(syntax);
            Assert.Empty(unknown);
            Assert.Equal(new[] { "Seat", "Seat Height", "Top" }, used.OrderBy(x => x));
            FamilyFormulas.Analyse("Width * \"Height\" + Depthh", new[] { "Width" }, out unknown, out _);
            Assert.Equal(new[] { "Depthh" }, unknown);
        }

        // ------------------------------------------------------------------ planes and dimensions

        [Fact]
        public void DimensionPlanesMustExistAndShareAnAxis()
        {
            var a = Args();
            At(a, "dimensions", 0)["planes"]!.AsArray()[1] = "Front";
            HasError(a, IssueCodes.Constraint, "dimensions[0].planes");
            a = Args();
            At(a, "dimensions", 0)["planes"]!.AsArray()[1] = "Rigth";
            HasError(a, IssueCodes.NotFound, "dimensions[0].planes[1]");
        }

        [Fact]
        public void DimensionLabelsAndEqualityAreChecked()
        {
            var a = Args();
            At(a, "dimensions", 0).Remove("parameter");
            HasError(a, IssueCodes.Required, "dimensions[0]");
            a = Args();
            At(a, "dimensions", 0).Set("parameter", "Top Material");
            HasError(a, IssueCodes.Type, "dimensions[0].parameter");
            a = Args();
            At(a, "dimensions", 1).Remove("equal");
            HasError(a, IssueCodes.Constraint, "dimensions[1].equal");
            a = Args();
            a["dimensions"]!.AsArray().Add(new JsonObject().Add("planes", new JsonArray("Right", "Left")).Add("parameter", "Width"));
            HasError(a, IssueCodes.Constraint, "dimensions[5]");
        }

        [Fact]
        public void BothPlanesFixedCannotBeDriven()
        {
            var a = Args();
            At(a, "planes", 2).Set("pinned", true);
            HasError(a, IssueCodes.Constraint, "dimensions[2]");
        }

        [Fact]
        public void UnanchoredPlanesAreWarned()
        {
            var a = Args();
            a["planes"]!.AsArray().Add(new JsonObject().Add("name", "Loose").Add("axis", "x").Add("at", 100));
            HasWarning(a, IssueCodes.Constraint, "'Loose' is not tied");
        }

        // ------------------------------------------------------------------ forms

        [Fact]
        public void BoxPlanesMustBeTheRightAxisAndApart()
        {
            var a = Args();
            At(a, "forms", 0)["box"]!.AsObject()["x"]!.AsArray()[1] = "Front";
            HasError(a, IssueCodes.Constraint, "forms[0].box.x[1]");
            a = Args();
            At(a, "forms", 0)["box"]!.AsObject()["x"]!.AsArray()[1] = "Left";
            HasError(a, IssueCodes.Constraint, "forms[0].box.x");
            a = Args();
            At(a, "forms", 0)["box"]!.AsObject()["z"]!.AsArray()[0] = "Bottom";
            HasError(a, IssueCodes.NotFound, "forms[0].box.z[0]");
        }

        [Fact]
        public void LockedFacesMustStartOnTheirPlanes()
        {
            var a = Args();
            At(a, "forms", 1).Set("end", 500);
            HasError(a, IssueCodes.Constraint, "forms[1].lock[\"+z\"]");
            a = Args();
            At(a, "forms", 1)["lock"]!.AsObject().Set("+z", "Right");
            HasError(a, IssueCodes.Constraint, "forms[1].lock[\"+z\"]");
        }

        [Fact]
        public void FieldsAreCheckedAgainstTheFormKind()
        {
            var a = Args();
            At(a, "forms", 1).Set("axis", new JsonArray(new JsonArray(0, 0), new JsonArray(0, 100)));
            HasError(a, IssueCodes.Constraint, "forms[1].axis");
            a = Args();
            At(a, "forms", 1).Set("kind", "blend");
            HasError(a, IssueCodes.Required, "forms[1].topProfile");
        }

        [Fact]
        public void ProfileLoopsMustBeSimpleAndApart()
        {
            var a = Args();
            At(a, "forms", 1).Set("profile", JsonValue.Parse("[ { \"points\": [[0,0],[100,100],[100,0],[0,100]] } ]"));
            At(a, "forms", 1).Remove("lock");
            HasError(a, IssueCodes.InvalidValue, "forms[1].profile[0]");
            a = Args();
            At(a, "forms", 1).Set("profile", JsonValue.Parse("[ { \"centre\": [0,-250], \"radius\": 30 }, { \"centre\": [20,-250], \"radius\": 30 } ]"));
            HasError(a, IssueCodes.InvalidValue, "forms[1].profile");
        }

        [Fact]
        public void RevolutionProfileStaysOnOneSideOfItsAxis()
        {
            var a = Args();
            a["forms"]!.AsArray().Add(JsonValue.Parse("{ \"kind\": \"revolution\", \"sketch\": \"front\", \"profile\": [ { \"points\": [[-50,0],[50,0],[50,100],[-50,100]] } ], \"axis\": [[0,0],[0,100]] }"));
            HasError(a, IssueCodes.InvalidValue, "forms[2].profile[0]");
        }

        [Fact]
        public void MaterialAndVisibilityParametersAreChecked()
        {
            var a = Args();
            At(a, "forms", 1).Set("void", true);
            HasError(a, IssueCodes.Constraint, "forms[1].material");
            a = Args();
            At(a, "forms", 1).Set("material", "Width");
            HasError(a, IssueCodes.Type, "forms[1].material");
            a = Args();
            At(a, "forms", 1).Set("visibleParameter", "Width");
            HasError(a, IssueCodes.Type, "forms[1].visibleParameter");
            a = Args();
            At(a, "forms", 1).Set("visibility", new JsonObject().Add("coarse", false).Add("medium", false).Add("fine", false));
            HasError(a, IssueCodes.Constraint, "forms[1].visibility");
        }

        [Fact]
        public void SymbolsUsePlanesOfTheRightAxes()
        {
            var a = Args();
            a["symbols"]!.AsArray().Add(JsonValue.Parse("{ \"line\": { \"on\": \"Top\", \"from\": \"Left\", \"to\": \"Right\" } }"));
            HasError(a, IssueCodes.Constraint, "symbols[1].line.on");
            a = Args();
            a["symbols"]!.AsArray().Add(JsonValue.Parse("{ \"line\": { \"on\": \"Front\", \"from\": \"Left\", \"to\": \"Top\" } }"));
            HasError(a, IssueCodes.Constraint, "symbols[1].line.to");
        }

        [Fact]
        public void FlexNamesLengthParameters()
        {
            var a = Args();
            a["flex"]!.AsObject().Set("z", "Top Material");
            HasError(a, IssueCodes.Type, "flex.z");
            a = Args();
            a.Remove("flex");
            HasWarning(a, IssueCodes.Constraint, "no flex sizes");
        }

        // ------------------------------------------------------------------ recipes

        public static readonly string[] RequiredRecipes =
        {
            "hotel-double-bed", "hotel-single-bed", "hotel-nightstand", "hotel-wardrobe", "hotel-desk", "hotel-desk-chair", "hotel-armchair",
            "hotel-sofa-2-seat", "hotel-sofa-3-seat", "hotel-coffee-table", "hotel-dining-table", "hotel-dining-chair", "hotel-tv-unit",
            "hotel-luggage-rack", "wc-close-coupled", "basin-wall-hung", "vanity-unit-with-basin", "shower-tray", "bathtub",
            "kitchen-base-unit", "kitchen-wall-unit", "kitchen-worktop",
        };

        public static IEnumerable<object[]> RecipeFiles() =>
            Directory.GetFiles(Path.Combine(DataFiles.DataDir, "families"), "*.json").OrderBy(f => f).Select(f => new object[] { Path.GetFileName(f) });

        [Theory]
        [MemberData(nameof(RecipeFiles))]
        public void RecipeIsLintCleanWithoutWarnings(string file)
        {
            var args = JsonValue.Parse(File.ReadAllText(Path.Combine(FamiliesDir, file))).AsObject();
            var v = V(args);
            Assert.True(v.Ok && v.Issues.Count == 0, file + ":\n" + string.Join("\n", v.Issues));
        }

        [Theory]
        [MemberData(nameof(RecipeFiles))]
        public void RecipeIsAFurnitureFamilyThatFlexes(string file)
        {
            var args = JsonValue.Parse(File.ReadAllText(Path.Combine(FamiliesDir, file))).AsObject();
            var r = FamilyRecipe.Parse(args, LengthUnit.Millimetre, "$.args", new List<Issue>());
            Assert.Equal(Path.GetFileNameWithoutExtension(file), r.Name.ToLowerInvariant().Replace(' ', '-'));
            Assert.InRange(r.Types.Count, 2, 3);
            foreach (var n in new[] { "Width", "Depth", "Height" }) Assert.Contains(r.Dimensions, d => d.Parameter == n);
            Assert.NotNull(r.Flex);
            Assert.Equal("Width", r.Flex!.X);
            Assert.Equal("Depth", r.Flex.Y);
            Assert.Contains(r.Parameters, p => p.Spec == "material");
            Assert.All(r.Forms.Where(f => !f.Void), f => Assert.NotNull(f.Material));
            Assert.NotEmpty(r.Symbols);
            var source = r.Parameter("Size Source");
            Assert.NotNull(source);
            foreach (var t in r.Types)
            {
                var s = t.ValueOf("Size Source");
                Assert.True(s is JsonString, file + " " + t.Name + ": no Size Source");
                var text = ((JsonString)s!).Value;
                Assert.True(text.Contains("typical, verify") || CitesDesignData(text), file + " " + t.Name + ": '" + text + "' cites no data/design id and is not marked typical, verify");
            }
        }

        private static readonly Lazy<HashSet<string>> DesignIds = new Lazy<HashSet<string>>(() =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.GetFiles(Path.Combine(DataFiles.DataDir, "design"), "*.json"))
                Collect(JsonValue.Parse(File.ReadAllText(f)), ids);
            return ids;
        });

        private static void Collect(JsonValue v, HashSet<string> ids)
        {
            if (v is JsonObject o)
            {
                if (o.GetStringOrNull("id") is string id) ids.Add(id);
                foreach (var kv in o) Collect(kv.Value, ids);
            }
            else if (v is JsonArray a) foreach (var i in a) Collect(i, ids);
        }

        /// <summary>Every id-like word in the text ("furniture.uk.adm1.single_bed.width/.length") names data/design ids.</summary>
        private static bool CitesDesignData(string text)
        {
            var words = text.Split(new[] { ' ', ';', '(', ')', ',' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Count(c => c == '.') >= 2).ToList();
            if (words.Count == 0) return false;
            foreach (var w in words)
            {
                var parts = w.Split('/');
                string first = parts[0];
                if (!DesignIds.Value.Contains(first) && !DesignIds.Value.Any(id => id.StartsWith(first + ".", StringComparison.Ordinal))) return false;
                string stem = first.Substring(0, first.LastIndexOf('.'));
                foreach (var more in parts.Skip(1))
                    if (!DesignIds.Value.Contains(stem + more)) return false;
            }
            return true;
        }

        [Fact]
        public void EveryRequiredRecipeExistsAndTheReadmeListsEachFile()
        {
            var files = Directory.GetFiles(FamiliesDir, "*.json").Select(Path.GetFileNameWithoutExtension).ToList();
            Assert.Empty(RequiredRecipes.Except(files));
            var readme = File.ReadAllText(Path.Combine(FamiliesDir, "README.md"));
            foreach (var f in files) Assert.Contains(f + ".json", readme);
        }
    }
}
