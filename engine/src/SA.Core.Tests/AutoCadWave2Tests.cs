using System.Collections.Generic;
using System.Linq;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    public class AutoCadWave2Tests
    {
        private static readonly OpRegistry Reg = OpRegistry.Default;

        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["draw.entities"] = @"{ ""layer"": ""A-ANNO"", ""entities"": [
                { ""id"": ""edge-1"", ""kind"": ""line"", ""points"": [[0,0],[1000,0]] },
                { ""kind"": ""polyline"", ""points"": [[0,0],[1000,0],[1000,1000]], ""bulges"": [0,0.5,0], ""closed"": true },
                { ""kind"": ""arc"", ""center"": [0,0], ""radius"": 500, ""startAngle"": 0, ""endAngle"": 90 },
                { ""kind"": ""arc"", ""points"": [[0,0],[500,200],[1000,0]] },
                { ""kind"": ""circle"", ""center"": [2000,0], ""radius"": 250, ""layer"": ""A-ANNO-SYMB"" },
                { ""kind"": ""point"", ""points"": [[3000,0]] } ] }",
            ["hatch.add"] = @"{ ""boundaries"": [ { ""points"": [[0,0],[4000,0],[4000,3000],[0,3000]], ""islands"": [ { ""handle"": ""2A1"" } ] } ],
                ""pattern"": ""ANSI31"", ""scale"": 10, ""angle"": 45, ""layer"": ""A-HATCH"", ""drawBoundary"": true, ""boundaryLayer"": ""A-HATCH-BDRY"", ""associative"": true }",
            ["entity.transform"] = @"{ ""handles"": [""2A1"", ""2A2""], ""rotate"": { ""base"": [0,0], ""angle"": 90 }, ""copy"": true, ""copies"": 3 }",
            ["entity.delete"] = @"{ ""filter"": { ""layer"": ""TEMP-*"", ""type"": ""LINE"" }, ""expectCount"": 12 }",
            ["block.extract"] = @"{ ""blocks"": [""Door-*""], ""attributes"": [""MARK"", ""WIDTH""], ""rooms"": { ""layers"": [""A-AREA""] }, ""fileName"": ""doors.csv"", ""overwrite"": true }",
            ["area.report"] = @"{ ""filter"": { ""layers"": [""A-AREA""], ""types"": [""polyline"", ""hatch""] }, ""groupBy"": ""layer"", ""areaUnit"": ""m2"",
                ""table"": { ""position"": [0,-5000], ""title"": ""Areas"" }, ""fileName"": ""areas.json"" }",
            ["grid.module"] = @"{ ""boundary"": [[0,0],[6000,0],[6000,4500],[0,4500]], ""module"": 600, ""setOut"": ""centreTile"", ""layer"": ""A-CLNG-GRID"" }",
            ["layout.clearances"] = @"{ ""zones"": [
                { ""id"": ""turn-1"", ""kind"": ""circle"", ""centre"": [1500,1500], ""radius"": 750 },
                { ""id"": ""door-1"", ""kind"": ""doorSwing"", ""hinge"": [0,0], ""leafWidth"": 900, ""closedAngle"": 0, ""swing"": 90, ""side"": ""left"", ""ignore"": [""2A1""] },
                { ""id"": ""route"", ""kind"": ""route"", ""points"": [[0,0],[5000,0]], ""width"": 1200 } ],
                ""layers"": [""A-FURN""], ""blocks"": [""WC-*""], ""drawZones"": { ""layer"": ""A-REVIEW"" } }",
            ["text.replace"] = @"{ ""find"": ""Rev A"", ""replace"": ""Rev B"", ""wholeWord"": true, ""kinds"": [""text"", ""mtext"", ""attributes""], ""expectCount"": 4 }",
            ["revcloud.add"] = @"{ ""rect"": { ""min"": [0,0], ""max"": [3000,2000] }, ""offset"": 200, ""layer"": ""A-REVS"", ""tag"": { ""text"": ""B"", ""block"": ""RevTag"" } }",
            ["layerstate.ensure"] = @"{ ""name"": ""Furniture plan"", ""description"": ""Furniture on"",
                ""layers"": [ { ""name"": ""A-FURN*"", ""on"": true, ""frozen"": false, ""color"": 3, ""lineweight"": 0.25, ""transparency"": 30 } ], ""restore"": true }",
            ["material.ensure"] = @"{ ""materials"": [ { ""name"": ""CT-01 Porcelain 600x600 Light Grey"", ""color"": ""#D0D0D0"",
                ""texture"": { ""path"": ""P:/library/ct01.jpg"", ""width"": 600, ""projection"": ""planar"" } } ] }",
            ["material.assign"] = @"{ ""material"": ""CT-01 Porcelain 600x600 Light Grey"", ""handles"": [""2A1""], ""mapping"": { ""projection"": ""planar"", ""width"": 600 } }",
        };

        private static OpSchema Schema(string op) => Reg.Find(op, AppKind.AutoCad)!;

        private static List<Issue> Errors(string op, string args) =>
            JobValidator.ValidateArgs(Schema(op), (JsonObject)JsonValue.Parse(args), LengthUnit.Millimetre).Where(i => i.Severity == IssueSeverity.Error).ToList();

        private static void Fails(string op, string args, string code, string? path = null)
        {
            var errors = Errors(op, args);
            Assert.True(errors.Any(i => i.Code == code && (path == null || i.Path == path)),
                op + " " + args + " expected " + code + (path != null ? " at " + path : "") + ", got: " + string.Join("; ", errors));
        }

        [Fact]
        public void EveryWaveTwoOpIsRegisteredForAutoCad()
        {
            var names = SA.Core.Jobs.Ops.AutoCadOpsWave2.All().Select(s => s.Name).ToList();
            Assert.Equal(13, names.Count);
            Assert.Equal(names.OrderBy(x => x), Examples.Keys.OrderBy(x => x));
            foreach (var n in names) Assert.Equal(AppKind.AutoCad, Schema(n).App);
            Assert.True(Schema("entity.delete").Destructive);
            Assert.True(Schema("layerstate.ensure").Ensure);
            Assert.True(Schema("material.ensure").Ensure);
            Assert.False(Schema("area.report").Destructive);
            foreach (var op in names.Select(Schema))
            {
                var inConstraint = new HashSet<string>(op.Constraints.SelectMany(c => c.Fields));
                foreach (var f in op.Fields.Where(f => f.Type == FieldType.List && f.MinItems >= 1 && !inConstraint.Contains(f.Name)))
                    Assert.True(f.Required, op.Name + "." + f.Name + " has a minimum item count but is optional");
            }
        }

        [Fact]
        public void EachExampleValidates()
        {
            foreach (var kv in Examples)
            {
                var errors = Errors(kv.Key, kv.Value);
                Assert.True(errors.Count == 0, kv.Key + ": " + string.Join("; ", errors));
            }
        }

        [Fact]
        public void AJobWithEveryExampleValidates()
        {
            var ops = new JsonArray();
            int i = 0;
            foreach (var kv in Examples)
                ops.Add(new JsonObject().Add("id", "op" + i++).Add("op", kv.Key).Add("args", JsonValue.Parse(kv.Value)));
            var job = new JsonObject()
                .Add("jobId", "2026-11-28T09-00-00Z-wave2")
                .Add("app", "autocad")
                .Add("document", new JsonObject().Add("path", "C:/proj/plan.dwg"))
                .Add("units", new JsonObject().Add("length", "mm"))
                .Add("ops", ops);
            var v = JobValidator.Validate(job.ToJson(), Reg);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal(13, v.Job!.Ops.Count);
        }

        [Fact]
        public void DrawEntitiesRejectsBadArgs()
        {
            Fails("draw.entities", @"{ ""entities"": [] }", IssueCodes.TooFew);
            Fails("draw.entities", @"{ }", IssueCodes.Required, "$.args.entities");
            Fails("draw.entities", @"{ ""entities"": [ { ""kind"": ""spline"", ""points"": [[0,0],[1,1]] } ] }", IssueCodes.Enum);
            Fails("draw.entities", @"{ ""entities"": [ { ""points"": [[0,0],[1,1]] } ] }", IssueCodes.Required, "$.args.entities[0].kind");
            Fails("draw.entities", @"{ ""entities"": [ { ""id"": ""bad id!"", ""kind"": ""line"", ""points"": [[0,0],[1,1]] } ] }", IssueCodes.Pattern);
            Fails("draw.entities", @"{ ""entities"": [ { ""kind"": ""circle"", ""center"": [0,0], ""radius"": 0 } ] }", IssueCodes.Range);
            Fails("draw.entities", @"{ ""entities"": [ { ""kind"": ""line"", ""points"": [[0,0],[1,1]], ""colour"": 1 } ] }", IssueCodes.UnknownField);
        }

        [Fact]
        public void HatchAddRejectsBadArgs()
        {
            Fails("hatch.add", @"{ ""boundaries"": [ { ""points"": [[0,0],[1,0],[1,1]], ""handle"": ""2A1"" } ], ""pattern"": ""SOLID"" }", IssueCodes.Constraint);
            Fails("hatch.add", @"{ ""boundaries"": [ { } ], ""pattern"": ""SOLID"" }", IssueCodes.Constraint);
            Fails("hatch.add", @"{ ""boundaries"": [ { ""points"": [[0,0],[1,0]] } ], ""pattern"": ""SOLID"" }", IssueCodes.TooFew);
            Fails("hatch.add", @"{ ""boundaries"": [ { ""handle"": ""2A1"" } ] }", IssueCodes.Required, "$.args.pattern");
            Fails("hatch.add", @"{ ""boundaries"": [ { ""handle"": ""2A1"" } ], ""pattern"": ""SOLID"", ""boundaryLayer"": ""X"" }", IssueCodes.Constraint);
            Fails("hatch.add", @"{ ""boundaries"": [ { ""handle"": ""2A1"" } ], ""pattern"": ""ANSI31"", ""scale"": 0 }", IssueCodes.Range);
            Fails("hatch.add", @"{ ""boundaries"": [ { ""handle"": ""2A1"" } ], ""pattern"": ""SOLID"", ""drawOrder"": ""front"" }", IssueCodes.Enum);
            Fails("hatch.add", @"{ ""boundaries"": [ { ""handle"": ""2A1"", ""islands"": [ { ""handle"": ""2A2"", ""islands"": [] } ] } ], ""pattern"": ""SOLID"" }", IssueCodes.UnknownField);
        }

        [Fact]
        public void EntityTransformRejectsBadArgs()
        {
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""filter"": { ""layer"": ""X"" }, ""move"": { ""from"": [0,0], ""to"": [1,0] } }", IssueCodes.Constraint);
            Fails("entity.transform", @"{ ""handles"": [""2A1""] }", IssueCodes.Constraint);
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""move"": { ""from"": [0,0], ""to"": [1,0] }, ""rotate"": { ""base"": [0,0], ""angle"": 90 } }", IssueCodes.Constraint);
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""scale"": { ""base"": [0,0], ""factor"": -1 } }", IssueCodes.Range);
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""mirror"": { ""axisStart"": [0,0] } }", IssueCodes.Required, "$.args.mirror.axisEnd");
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""move"": { ""from"": [0,0], ""to"": [1,0] }, ""copy"": true, ""copies"": 0 }", IssueCodes.Range);
            Fails("entity.transform", @"{ ""handles"": [""2A1""], ""mirror"": { ""axisStart"": [0,0], ""axisEnd"": [0,1], ""text"": ""flipped"" } }", IssueCodes.Enum);
        }

        [Fact]
        public void EntityDeleteNeedsExpectCount()
        {
            Fails("entity.delete", @"{ ""handles"": [""2A1""] }", IssueCodes.Required, "$.args.expectCount");
            Fails("entity.delete", @"{ ""handles"": [""2A1""], ""expectCount"": 0 }", IssueCodes.Range);
            Fails("entity.delete", @"{ ""expectCount"": 1 }", IssueCodes.Constraint);
            Fails("entity.delete", @"{ ""filter"": { ""space"": ""layout"" }, ""expectCount"": 1 }", IssueCodes.Enum);
        }

        [Fact]
        public void BlockExtractRejectsBadArgs()
        {
            Fails("block.extract", @"{ ""maxInline"": 6000 }", IssueCodes.Range);
            Fails("block.extract", @"{ ""rooms"": { ""tagBlock"": ""RoomTag"" } }", IssueCodes.Required, "$.args.rooms.layers");
            Fails("block.extract", @"{ ""rooms"": { ""layers"": [] } }", IssueCodes.TooFew);
            Fails("block.extract", @"{ ""space"": ""layout"" }", IssueCodes.Enum);
            Assert.Empty(Errors("block.extract", "{ }"));
        }

        [Fact]
        public void AreaReportRejectsBadArgs()
        {
            Fails("area.report", @"{ ""groupBy"": ""layer"" }", IssueCodes.Constraint);
            Fails("area.report", @"{ ""filter"": { ""types"": [""solid""] } }", IssueCodes.Enum);
            Fails("area.report", @"{ ""handles"": [""2A1""], ""precision"": 7 }", IssueCodes.Range);
            Fails("area.report", @"{ ""handles"": [""2A1""], ""table"": { ""title"": ""Areas"" } }", IssueCodes.Required, "$.args.table.position");
            Fails("area.report", @"{ ""handles"": [""2A1""], ""areaUnit"": ""ha"" }", IssueCodes.Enum);
        }

        [Fact]
        public void GridModuleRejectsBadArgs()
        {
            Fails("grid.module", @"{ ""boundary"": [[0,0],[1,0],[1,1]] }", IssueCodes.Required, "$.args.module");
            Fails("grid.module", @"{ ""boundary"": [[0,0],[1,0],[1,1]], ""boundaryHandle"": ""2A1"", ""module"": 600 }", IssueCodes.Constraint);
            Fails("grid.module", @"{ ""boundary"": [[0,0],[1,0],[1,1]], ""module"": 600, ""minBorder"": 1.5 }", IssueCodes.Range);
            Fails("grid.module", @"{ ""boundary"": [[0,0],[1,0],[1,1]], ""module"": 600, ""origin"": [0,0] }", IssueCodes.Constraint);
            Fails("grid.module", @"{ ""boundary"": [[0,0],[1,0]], ""module"": 600 }", IssueCodes.TooFew);
            Fails("grid.module", @"{ ""boundaryHandle"": ""2A1"", ""module"": 600, ""setOut"": ""centre"" }", IssueCodes.Enum);
        }

        [Fact]
        public void LayoutClearancesRejectsBadArgs()
        {
            const string zone = @"{ ""id"": ""z"", ""kind"": ""circle"", ""centre"": [0,0], ""radius"": 750 }";
            Fails("layout.clearances", @"{ ""zones"": [" + zone + "] }", IssueCodes.Constraint);
            Fails("layout.clearances", @"{ ""zones"": [], ""layers"": [""A""] }", IssueCodes.TooFew);
            Fails("layout.clearances", @"{ ""zones"": [ { ""kind"": ""circle"", ""centre"": [0,0], ""radius"": 750 } ], ""layers"": [""A""] }", IssueCodes.Required, "$.args.zones[0].id");
            Fails("layout.clearances", @"{ ""zones"": [ { ""id"": ""z"", ""kind"": ""ellipse"" } ], ""layers"": [""A""] }", IssueCodes.Enum);
            Fails("layout.clearances", @"{ ""zones"": [ { ""id"": ""d"", ""kind"": ""doorSwing"", ""hinge"": [0,0], ""leafWidth"": 900, ""closedAngle"": 0, ""swing"": 200 } ], ""layers"": [""A""] }", IssueCodes.Range);
            Fails("layout.clearances", @"{ ""zones"": [" + zone + @"], ""layers"": [""A""], ""drawZones"": { } }", IssueCodes.Required, "$.args.drawZones.layer");
            Fails("layout.clearances", @"{ ""zones"": [" + zone + @"], ""blocks"": [""B""], ""blockGeometry"": ""hull"" }", IssueCodes.Enum);
            Fails("layout.clearances", @"{ ""zones"": [" + zone + @"], ""layers"": [""A""], ""tolerance"": -5 }", IssueCodes.Range, "$.args.tolerance");
            Assert.Empty(Errors("layout.clearances", @"{ ""zones"": [" + zone + @"], ""layers"": [""A""], ""tolerance"": 0 }"));
        }

        [Fact]
        public void TextReplaceRejectsBadArgs()
        {
            Fails("text.replace", @"{ ""replace"": ""B"" }", IssueCodes.Required, "$.args.find");
            Fails("text.replace", @"{ ""find"": ""A"", ""replace"": ""B"", ""kinds"": [""dims""] }", IssueCodes.Enum);
            Fails("text.replace", @"{ ""find"": ""A"", ""replace"": ""B"", ""space"": ""layout"" }", IssueCodes.Enum);
            Assert.Empty(Errors("text.replace", @"{ ""find"": ""A"", ""dryRun"": true }"));
        }

        [Fact]
        public void RevcloudAddRejectsBadArgs()
        {
            Fails("revcloud.add", @"{ ""rect"": { ""min"": [0,0], ""max"": [1,1] }, ""points"": [[0,0],[1,0],[1,1]] }", IssueCodes.Constraint);
            Fails("revcloud.add", @"{ ""layer"": ""A-REVS"" }", IssueCodes.Constraint);
            Fails("revcloud.add", @"{ ""points"": [[0,0],[1,0]] }", IssueCodes.TooFew);
            Fails("revcloud.add", @"{ ""handle"": ""2A1"", ""offset"": -1 }", IssueCodes.Range);
            Fails("revcloud.add", @"{ ""handle"": ""2A1"", ""tag"": { ""block"": ""RevTag"" } }", IssueCodes.Required, "$.args.tag.text");
            Fails("revcloud.add", @"{ ""rect"": { ""min"": [0,0] } }", IssueCodes.Required, "$.args.rect.max");
            Fails("revcloud.add", @"{ ""handle"": ""2A1"", ""arcLength"": 0 }", IssueCodes.Range, "$.args.arcLength");
        }

        [Fact]
        public void LayerStateEnsureRejectsBadArgs()
        {
            Fails("layerstate.ensure", @"{ ""layers"": [] }", IssueCodes.Required, "$.args.name");
            Fails("layerstate.ensure", @"{ ""name"": ""S"", ""layers"": [ { ""name"": ""A*"", ""transparency"": 95 } ] }", IssueCodes.Range);
            Fails("layerstate.ensure", @"{ ""name"": ""S"", ""properties"": [""visible""] }", IssueCodes.Enum);
            Fails("layerstate.ensure", @"{ ""name"": ""S"", ""layers"": [ { ""on"": false } ] }", IssueCodes.Required, "$.args.layers[0].name");
            Fails("layerstate.ensure", @"{ ""name"": ""S"", ""layers"": [ { ""name"": ""A"", ""frozen"": ""yes"" } ] }", IssueCodes.Type);
        }

        [Fact]
        public void MaterialEnsureRejectsBadArgs()
        {
            Fails("material.ensure", @"{ ""materials"": [] }", IssueCodes.TooFew);
            Fails("material.ensure", @"{ ""materials"": [ { ""description"": ""x"" } ] }", IssueCodes.Required, "$.args.materials[0].name");
            Fails("material.ensure", @"{ ""materials"": [ { ""name"": ""M"", ""texture"": { ""width"": 600 } } ] }", IssueCodes.Required, "$.args.materials[0].texture.path");
            Fails("material.ensure", @"{ ""materials"": [ { ""name"": ""M"", ""texture"": { ""path"": ""P:/a.jpg"", ""projection"": ""cubic"" } } ] }", IssueCodes.Enum);
            Fails("material.ensure", @"{ ""materials"": [ { ""name"": ""M"", ""texture"": { ""path"": ""P:/a.jpg"", ""width"": 0 } } ] }", IssueCodes.Range);
        }

        [Fact]
        public void MaterialAssignRejectsBadArgs()
        {
            Fails("material.assign", @"{ ""layers"": [""A-FLOR""] }", IssueCodes.Required, "$.args.material");
            Fails("material.assign", @"{ ""material"": ""M"", ""layers"": [""A""], ""handles"": [""2A1""] }", IssueCodes.Constraint);
            Fails("material.assign", @"{ ""material"": ""M"" }", IssueCodes.Constraint);
            Fails("material.assign", @"{ ""material"": ""M"", ""handles"": [""2A1""], ""mapping"": { ""projection"": ""box"" } }", IssueCodes.Required, "$.args.mapping.width");
            Fails("material.assign", @"{ ""material"": ""M"", ""filter"": { ""block"": ""B"" }, ""mapping"": { ""width"": 600, ""projection"": ""dome"" } }", IssueCodes.Enum);
        }
    }
}
