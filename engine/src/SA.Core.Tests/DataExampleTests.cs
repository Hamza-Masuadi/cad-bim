using System.IO;
using System.Linq;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The example data files other authors copy from must load with the same code the engines use.</summary>
    public class DataExampleTests
    {
        [Fact]
        public void ExampleJobPassesStaticValidation()
        {
            var v = JobValidator.Validate(File.ReadAllText(DataFiles.Example("job.example.json")), OpRegistry.Default);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Empty(v.Issues);
            Assert.Equal(4, v.Job!.Ops.Count);
        }

        [Fact]
        public void TestPlanJobsPassStaticValidation()
        {
            var root = Path.Combine(DataFiles.DataDir, "..", "scripts", "testdata");
            var files = Directory.GetFiles(Path.Combine(root, "autocad"), "*.json")
                .Concat(Directory.GetFiles(Path.Combine(root, "revit"), "*.json"))
                .Where(f => !f.EndsWith("-check.json")).ToArray();
            Assert.Equal(37, files.Length);
            var failures = files
                .Select(f => (f, v: JobValidator.Validate(File.ReadAllText(f), OpRegistry.Default)))
                .Where(x => !x.v.Ok)
                .Select(x => Path.GetFileName(x.f) + ": " + string.Join("; ", x.v.Errors));
            Assert.Empty(failures);
        }

        [Fact]
        public void ExampleReportParsesAndRoundTrips()
        {
            var json = JsonValue.Parse(File.ReadAllText(DataFiles.Example("report.example.json")));
            var r = Report.Parse(json);
            Assert.Equal(JobStatus.Committed, r.Status);
            Assert.Equal(12, r.CreatedCount);
            Assert.Equal(2, r.ModifiedCount);
            var o = (JsonObject)json;
            Assert.Equal(r.CreatedCount, o["summary"]!.AsObject()["created"]!.AsInt64());
            Assert.Equal(410, r.TimingMs["capture"]);
            Assert.Equal("rm1", r.Results[1].Outputs["walls"]!.AsArray()[0].AsObject().GetStringOrNull("id"));
        }

        [Fact]
        public void DesignDataExampleIsWellFormed()
        {
            var o = JsonValue.Parse(File.ReadAllText(DataFiles.Example("design-data.example.json"))).AsObject();
            var entries = o["entries"]!.AsArray();
            Assert.Equal(4, entries.Count);
            Assert.Contains(entries, e => e.AsObject()["value"]!.IsNull && e.AsObject().GetStringOrNull("formula") != null);
        }

        [Fact]
        public void SchemasAreValidJson()
        {
            var dir = Path.Combine(DataFiles.DataDir, "schemas");
            var files = Directory.GetFiles(dir, "*.schema.json");
            Assert.Equal(5, files.Length);
            foreach (var f in files)
            {
                var s = JsonValue.Parse(File.ReadAllText(f)).AsObject();
                Assert.Equal("https://json-schema.org/draft/2020-12/schema", s.GetStringOrNull("$schema"));
            }
        }
    }
}
