using System.IO;
using System.Linq;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The static side of family.edit and of the job's ownership mode; the Revit side cannot run here.</summary>
    public class RevitFamilyWorksharingTests
    {
        internal const string FamilyEditJob = @"{
  ""jobId"": ""2026-11-28T09-00-00Z-door-sizes"",
  ""title"": ""Door family sizes"",
  ""app"": ""revit"",
  ""units"": { ""length"": ""mm"" },
  ""ops"": [
    { ""id"": ""f1"", ""op"": ""family.edit"", ""args"": {
        ""family"": ""Single-Flush"", ""category"": ""OST_Doors"",
        ""addParameters"": [ { ""name"": ""Leaf Clearance"", ""spec"": ""length"", ""group"": ""dimensions"" } ],
        ""formulas"": { ""Leaf Clearance"": ""Width - 100 mm"", ""Rough Width"": null },
        ""values"": { ""Fire Rating"": ""FD30"" },
        ""types"": [
          { ""name"": ""915 x 2134mm"", ""values"": { ""Width"": 915, ""Height"": 2134 } },
          { ""name"": ""1010 x 2134mm"", ""create"": true, ""copyFrom"": ""915 x 2134mm"", ""values"": { ""Width"": 1010 } }
        ] } }
  ],
  ""verify"": { ""capture"": [ { ""target"": ""activeView"" } ] }
}";

        private static JobValidation V(string json) => JobValidator.Validate(json, OpRegistry.Default);

        private static JsonObject Job() => (JsonObject)JsonValue.Parse(FamilyEditJob);

        private static JsonArray Ops(JsonObject job) => job["ops"]!.AsArray();

        [Fact]
        public void FamilyEditIsDeclaredForRevitWithItsFields()
        {
            var s = OpRegistry.Default.Find("family.edit", AppKind.Revit);
            Assert.NotNull(s);
            Assert.Equal(AppKind.Revit, s!.App);
            Assert.False(s.Destructive);
            foreach (var f in new[] { "family", "category", "addParameters", "formulas", "values", "types" }) Assert.NotNull(s.FindField(f));
            Assert.True(s.FindField("family")!.Required);
            Assert.Contains("material", RevitOpsFamilyWave2.ParameterSpecs);
            Assert.Contains("dimensions", RevitOpsFamilyWave2.ParameterGroups);
        }

        [Fact]
        public void ExampleJobValidates()
        {
            var v = V(FamilyEditJob);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal(OwnershipMode.Refuse, v.Job!.Ownership);
        }

        [Fact]
        public void FamilyEditMustBeAloneInItsJob()
        {
            var j = Job();
            Ops(j).Add(JsonValue.Parse(@"{ ""id"": ""p1"", ""op"": ""param.set"", ""args"": { ""elements"": [""1""], ""values"": { ""Comments"": ""x"" } } }"));
            var v = V(j.ToJson());
            var i = Assert.Single(v.Errors, e => e.Code == JobValidator.OpNotAlone);
            Assert.Equal("$.ops[0].op", i.Path);
        }

        [Fact]
        public void FamilyEditNeedsSomethingToDo()
        {
            var j = Job();
            var args = (JsonObject)((JsonObject)Ops(j)[0])["args"]!;
            foreach (var k in new[] { "addParameters", "formulas", "values", "types" }) args.Remove(k);
            Assert.False(V(j.ToJson()).Ok);
        }

        [Fact]
        public void BadParameterSpecIsAnEnumError()
        {
            var v = V(FamilyEditJob.Replace(@"""spec"": ""length""", @"""spec"": ""lenght"""));
            Assert.Contains(v.Errors, e => e.Code == IssueCodes.Enum && e.Path == "$.ops[0].args.addParameters[0].spec");
        }

        [Fact]
        public void OwnershipDefaultsToRefuseAndParsesSkip()
        {
            var j = Job();
            j.Add("ownership", "skip");
            var v = V(j.ToJson());
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal(OwnershipMode.Skip, v.Job!.Ownership);
            Assert.Equal("skip", OwnershipModes.ToWire(OwnershipMode.Skip));
            Assert.Equal("refuse", OwnershipModes.ToWire(OwnershipMode.Refuse));
        }

        [Fact]
        public void SkipExampleWithAFilterValidates()
        {
            var v = V(@"{ ""jobId"": ""2026-11-28T10-00-00Z-comments"", ""title"": ""Comments"", ""app"": ""revit"", ""units"": { ""length"": ""mm"" },
  ""ownership"": ""skip"",
  ""ops"": [ { ""id"": ""p1"", ""op"": ""param.set"", ""args"": { ""filter"": { ""category"": ""OST_Doors"" }, ""values"": { ""Comments"": ""checked"" } } } ] }");
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal(OwnershipMode.Skip, v.Job!.Ownership);
        }

        [Fact]
        public void UnknownOwnershipModeIsAnEnumError()
        {
            var j = Job();
            j.Add("ownership", "ignore");
            var v = V(j.ToJson());
            Assert.Contains(v.Errors, e => e.Code == IssueCodes.Enum && e.Path == "$.ownership");
        }

        [Fact]
        public void OwnershipSkipOnAutoCadIsOnlyAWarning()
        {
            var j = (JsonObject)JsonValue.Parse(JobTests.GoodAcadJob);
            j.Add("ownership", "skip");
            var v = V(j.ToJson());
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Contains(v.Issues, i => !i.IsError && i.Path == "$.ownership");
        }

        [Fact]
        public void JobSchemaDeclaresOwnership()
        {
            var s = JsonValue.Parse(File.ReadAllText(Path.Combine(DataFiles.DataDir, "schemas", "job.schema.json"))).AsObject();
            var o = s["properties"]!.AsObject()["ownership"]!.AsObject();
            Assert.Equal(new[] { "refuse", "skip" }, o["enum"]!.AsArray().Select(x => x.AsString()).ToArray());
        }
    }
}
