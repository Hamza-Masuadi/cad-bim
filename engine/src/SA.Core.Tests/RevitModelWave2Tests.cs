using System.Collections.Generic;
using System.Linq;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The schemas of the wave-2 Revit modelling ops; their handlers need Revit and cannot run here.</summary>
    public class RevitModelWave2Tests
    {
        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["family.place"] = @"{ ""type"": ""Desk : 1525 x 762mm"", ""points"": [[1000, 2000], [3000, 2000]], ""level"": ""Level 1"", ""rotation"": 90, ""mark"": ""D"" }",
            ["group.place"] = @"{ ""elements"": [""316001"", ""316002""], ""name"": ""Toilet core A"", ""points"": [[0, 0], [12000, 0]], ""level"": ""Level 2"" }",
            ["element.transform"] = @"{ ""elements"": [""316001""], ""action"": ""rotate"", ""centre"": [0, 0], ""angle"": 45 }",
            ["phase.set"] = @"{ ""elements"": [""316001""], ""created"": ""Existing"", ""demolished"": ""New Construction"" }",
            ["compoundtype.ensure"] = @"{ ""kind"": ""floor"", ""name"": ""FL 250 Concrete + Screed"", ""layers"": [
                { ""function"": ""finish1"", ""material"": ""Screed"", ""thickness"": 50 },
                { ""function"": ""structure"", ""material"": ""Concrete"", ""thickness"": 200, ""core"": true } ] }",
            ["wallsweep.add"] = @"{ ""walls"": [""316010""], ""type"": ""Cornice : 100x100"", ""distance"": 2700, ""side"": ""exterior"" }",
            ["fillpattern.ensure"] = @"{ ""patterns"": [ { ""name"": ""Tile 300"", ""target"": ""model"", ""grids"": [ { ""angle"": 0, ""offset"": 300 }, { ""angle"": 90, ""offset"": 300 } ] } ] }",
            ["material.ensure"] = @"{ ""materials"": [ { ""name"": ""Oak Veneer"", ""identity"": { ""description"": ""Oak veneer panel"", ""keynote"": ""09 64 00"" },
                ""shading"": { ""color"": ""160,112,60"", ""transparency"": 0 }, ""surface"": { ""foreground"": { ""pattern"": ""Tile 300"", ""target"": ""model"" } },
                ""cut"": { ""foreground"": { ""pattern"": ""solid"", ""color"": ""90,58,30"" } }, ""appearance"": { ""color"": ""160,112,60"" } } ] }",
            ["material.assign"] = @"{ ""material"": ""Oak Veneer"", ""layer"": { ""type"": ""Interior - 138mm Partition"", ""kind"": ""wall"", ""function"": ""finish1"", ""side"": ""exterior"" }, ""maxInstances"": 20 }",
            ["link.cad"] = @"{ ""path"": ""C:\\Projects\\Site\\survey.dwg"", ""view"": ""Site"", ""unit"": ""m"", ""hideLayers"": [""TEXT""] }",
            ["link.revit"] = @"{ ""path"": ""C:\\Projects\\A\\structure.rvt"", ""placement"": ""shared"", ""worksets"": ""all"" }",
            ["cleanup.purge"] = @"{ ""ids"": [""400101"", ""400102""], ""expectCount"": 2 }",
            ["export.nwc"] = @"{ ""fileName"": ""{drawing} - coordination"", ""scope"": ""view"", ""view"": ""{3D} Coordination"", ""coordinates"": ""shared"" }",
        };

        public static IEnumerable<object[]> Names() => Examples.Keys.Select(k => new object[] { k });

        private static OpSchema Schema(string name) => OpRegistry.Default.Find(name, AppKind.Revit) ?? throw new Xunit.Sdk.XunitException(name + " is not declared for Revit");

        private static List<Issue> Errors(string op, string args) =>
            JobValidator.ValidateArgs(Schema(op), (JsonObject)JsonValue.Parse(args), LengthUnit.Millimetre).Where(i => i.IsError).ToList();

        [Fact]
        public void AllThirteenAreDeclaredForRevitWithTheirFlags()
        {
            Assert.Equal(13, Examples.Count);
            foreach (var name in Examples.Keys)
            {
                var s = Schema(name);
                Assert.Equal(AppKind.Revit, s.App);
                Assert.Equal(name.EndsWith(".ensure"), s.Ensure);
                Assert.Equal(name == "cleanup.purge", s.Destructive);
            }
        }

        [Fact]
        public void NonEmptyListsOutsideConstraintsAreRequired()
        {
            foreach (var s in Examples.Keys.Select(Schema))
            {
                var inConstraint = new HashSet<string>(s.Constraints.SelectMany(c => c.Fields));
                foreach (var f in s.Fields.Where(f => f.Type == FieldType.List && f.MinItems >= 1 && !inConstraint.Contains(f.Name)))
                    Assert.True(f.Required, s.Name + "." + f.Name + " has a minimum item count but is optional");
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
        public void MaterialOpsAreKeyedPerApp()
        {
            foreach (var name in new[] { "material.ensure", "material.assign" })
            {
                var revit = Schema(name);
                var cad = OpRegistry.Default.Find(name, AppKind.AutoCad);
                if (cad != null)
                {
                    Assert.NotSame(revit, cad);
                    Assert.Equal(AppKind.AutoCad, cad.App);
                }
                Assert.NotNull(revit.FindField(name == "material.ensure" ? "materials" : "layer"));
            }
            Assert.NotNull(Schema("material.assign").FindField("paint"));
        }

        [Theory]
        [InlineData("family.place", @"{ ""type"": ""Lamp : Wall"", ""points"": [[0, 0]], ""host"": ""316001"", ""workPlane"": ""Level 1"" }")]
        [InlineData("family.place", @"{ ""type"": ""Lamp : Wall"", ""points"": [[0, 0]], ""face"": ""top"" }")]
        [InlineData("group.place", @"{ ""elements"": [""316001""], ""points"": [[0, 0]], ""level"": ""Level 1"" }")]
        [InlineData("group.place", @"{ ""groupType"": ""Core"" }")]
        [InlineData("element.transform", @"{ ""elements"": [""316001""], ""action"": ""rotate"", ""centre"": [0, 0] }")]
        [InlineData("element.transform", @"{ ""elements"": [""316001""], ""action"": ""move"", ""vector"": [1, 0], ""centre"": [0, 0], ""angle"": 5 }")]
        [InlineData("element.transform", @"{ ""elements"": [""316001""], ""action"": ""copy"", ""vector"": [1, 0], ""copies"": 0 }")]
        [InlineData("phase.set", @"{ ""created"": ""Existing"" }")]
        [InlineData("compoundtype.ensure", @"{ ""kind"": ""wall"", ""name"": ""X"", ""layers"": [ { ""function"": ""structure"", ""material"": ""Concrete"", ""thickness"": 200 } ] }")]
        [InlineData("compoundtype.ensure", @"{ ""kind"": ""floor"", ""name"": ""X"", ""layers"": [] }")]
        [InlineData("wallsweep.add", @"{ ""type"": ""Cornice"", ""distance"": 100 }")]
        [InlineData("fillpattern.ensure", @"{ ""patterns"": [ { ""name"": ""P"", ""target"": ""model"", ""lines"": { ""spacing"": 100 }, ""grids"": [ { ""offset"": 100 } ] } ] }")]
        [InlineData("fillpattern.ensure", @"{ ""patterns"": [ { ""name"": ""P"", ""target"": ""model"" } ] }")]
        [InlineData("fillpattern.ensure", @"{ ""patterns"": [ { ""name"": ""P"", ""target"": ""model"", ""grids"": [ { ""offset"": 0 } ] } ] }")]
        [InlineData("material.ensure", @"{ ""materials"": [ { ""name"": ""M"", ""shading"": { ""color"": ""red"" } } ] }")]
        [InlineData("material.ensure", @"{ ""materials"": [ { ""name"": ""M"", ""shading"": { ""transparency"": 150 } } ] }")]
        [InlineData("material.assign", @"{ ""material"": ""M"" }")]
        [InlineData("material.assign", @"{ ""material"": ""M"", ""category"": { ""category"": ""Walls"" }, ""paint"": { ""elements"": [""1""], ""faces"": [""top""] } }")]
        [InlineData("material.assign", @"{ ""material"": ""M"", ""layer"": { ""type"": ""T"", ""index"": 0, ""function"": ""structure"", ""side"": ""top"" } }")]
        [InlineData("material.assign", @"{ ""material"": ""M"", ""layer"": { ""type"": ""T"", ""function"": ""structure"" } }")]
        [InlineData("material.assign", @"{ ""material"": ""M"", ""parameter"": { ""target"": ""type"" } }")]
        [InlineData("link.cad", @"{ ""path"": ""C:\\a.dwg"" }")]
        [InlineData("link.cad", @"{ ""path"": ""C:\\a.dwg"", ""view"": ""Site"", ""point"": [0, 0] }")]
        [InlineData("link.cad", @"{ ""path"": ""C:\\a.dwg"", ""view"": ""Site"", ""scale"": 2 }")]
        [InlineData("link.revit", @"{ ""path"": ""C:\\a.rvt"", ""coordinates"": ""share"" }")]
        [InlineData("link.revit", @"{ ""path"": ""C:\\a.rvt"", ""coordinates"": ""publish"" }")]
        [InlineData("cleanup.purge", @"{ ""ids"": [""1""] }")]
        [InlineData("cleanup.purge", @"{ ""expectCount"": 1 }")]
        [InlineData("export.nwc", @"{ ""fileName"": ""x"", ""scope"": ""everything"" }")]
        [InlineData("export.nwc", @"{ ""fileName"": ""x"", ""facetingFactor"": 0 }")]
        public void BadArgsAreRefused(string op, string args)
        {
            Assert.NotEmpty(Errors(op, args));
        }

        private const string Job = @"{
  ""jobId"": ""2026-11-28T10-00-00Z-finishes"",
  ""title"": ""Floor finishes"",
  ""app"": ""revit"",
  ""units"": { ""length"": ""mm"" },
  ""ops"": [
    { ""id"": ""p1"", ""op"": ""fillpattern.ensure"", ""args"": { ""patterns"": [ { ""name"": ""Tile 300"", ""target"": ""model"", ""grids"": [ { ""angle"": 0, ""offset"": 300 }, { ""angle"": 90, ""offset"": 300 } ] } ] } },
    { ""id"": ""m1"", ""op"": ""material.ensure"", ""args"": { ""materials"": [ { ""name"": ""Porcelain 300"", ""surface"": { ""foreground"": { ""pattern"": ""Tile 300"", ""target"": ""model"" } } } ] } },
    { ""id"": ""t1"", ""op"": ""compoundtype.ensure"", ""args"": { ""kind"": ""floor"", ""name"": ""FL Tiled 260"", ""layers"": [
        { ""function"": ""finish1"", ""material"": ""Porcelain 300"", ""thickness"": 10 },
        { ""function"": ""structure"", ""material"": ""Concrete"", ""thickness"": 250, ""core"": true } ] } },
    { ""id"": ""a1"", ""op"": ""material.assign"", ""args"": { ""material"": ""Porcelain 300"", ""paint"": { ""elements"": [""316100""], ""faces"": [""top""] } } }
  ]
}";

        [Fact]
        public void ExampleJobValidates()
        {
            var v = JobValidator.Validate(Job, OpRegistry.Default);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
        }
    }
}
