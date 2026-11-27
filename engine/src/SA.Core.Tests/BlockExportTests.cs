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
    public class BlockExportTests
    {
        public static readonly Dictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["block.export"] = @"{ ""blocks"": [""BED-*"", ""CHAIR-*"", ""DESK-1600""], ""layers"": [""I-FURN""], ""expectCount"": 6,
                ""outputDir"": ""C:/Projects/2417/export/furniture"", ""overwrite"": true }",
        };

        /// <summary>The example of reports/FAMILY-CREATE-CONTRACT.md section 1.</summary>
        private const string ContractExample = @"{
  ""format"": ""cadbim.placements/1"",
  ""source"": { ""drawing"": ""GF-R10.dwg"", ""fingerprint"": ""..."", ""units"": ""mm"", ""insunits"": 4 },
  ""blocks"": [
    {
      ""name"": ""BED-DOUBLE"",
      ""effectiveName"": ""BED-DOUBLE"",
      ""file"": ""BED-DOUBLE.dwg"",
      ""isDynamic"": false,
      ""extents"": { ""min"": [-800, 0, 0], ""max"": [800, 2100, 450] },
      ""has3d"": false,
      ""references"": [
        { ""handle"": ""2A1F"", ""space"": ""model"", ""layer"": ""I-FURN"",
          ""position"": [12500.0, 8400.0, 0.0], ""rotationDeg"": 90.0,
          ""scale"": [1, 1, 1], ""mirrored"": false }
      ]
    }
  ]
}";

        private static OpSchema Schema => OpRegistry.Default.Find("block.export", AppKind.AutoCad)!;

        private static List<Issue> Errors(string args) =>
            JobValidator.ValidateArgs(Schema, (JsonObject)JsonValue.Parse(args), LengthUnit.Millimetre).Where(i => i.IsError).ToList();

        private static void Fails(string args, string code)
        {
            var errors = Errors(args);
            Assert.True(errors.Any(i => i.Code == code), args + " expected " + code + ", got: " + string.Join("; ", errors));
        }

        private static PlacementsFile Sample()
        {
            var f = Placements.Parse(ContractExample);
            f.Blocks.Add(new PlacementBlock
            {
                Name = "DOOR-SGL__1", EffectiveName = "DOOR-SGL", File = "DOOR-SGL__1.dwg", IsDynamic = true,
                Min = new double[] { 0, -50, 0 }, Max = new double[] { 900, 50, 2100 }, Has3d = true,
                References =
                {
                    new PlacementReference { Handle = "3B0", Space = "model", Layer = "A-DOOR", Position = new[] { 1.5, -2.25, 0 }, RotationDeg = 270, Scale = new double[] { -1, 1, 1 }, Mirrored = true },
                    new PlacementReference { Handle = "3B1", Space = "Sheet A1", Layer = "A-DOOR", Position = new double[] { 10, 20, 0 }, RotationDeg = 0.000001, Scale = new double[] { -1, -1, 1 }, Mirrored = false },
                },
            });
            return f;
        }

        // ------------------------------------------------------------------ schema

        [Fact]
        public void BlockExportIsDeclaredForAutoCadAndAloneInItsJob()
        {
            Assert.Equal(new[] { "block.export" }, SA.Core.Jobs.Ops.AutoCadOpsBlocks.All().Select(s => s.Name));
            Assert.Equal(Examples.Keys.OrderBy(x => x), SA.Core.Jobs.Ops.AutoCadOpsBlocks.All().Select(s => s.Name).OrderBy(x => x));
            Assert.False(Schema.Destructive);
            Assert.False(Schema.Ensure);
            Assert.Empty(Errors(Examples["block.export"]));

            string Job(params string[] ops) => new JsonObject()
                .Add("jobId", "2026-11-28T09-00-00Z-blocks")
                .Add("app", "autocad")
                .Add("document", new JsonObject().Add("path", "C:/proj/plan.dwg"))
                .Add("units", new JsonObject().Add("length", "mm"))
                .Add("ops", new JsonArray(ops.Select((o, i) => (JsonValue)JsonValue.Parse(o.Replace("ID", "op" + i)))))
                .ToJson();
            string export = @"{ ""id"": ""ID"", ""op"": ""block.export"", ""args"": { ""all"": true } }";
            var alone = JobValidator.Validate(Job(export), OpRegistry.Default);
            Assert.True(alone.Ok, string.Join("\n", alone.Issues));
            var shared = JobValidator.Validate(Job(export, @"{ ""id"": ""ID"", ""op"": ""layer.ensure"", ""args"": { ""layers"": [ { ""name"": ""X"" } ] } }"), OpRegistry.Default);
            Assert.Contains(shared.Issues, i => i.Code == JobValidator.OpNotAlone);
        }

        [Fact]
        public void BlockExportRejectsBadArgs()
        {
            Fails(@"{ }", IssueCodes.Constraint);
            Fails(@"{ ""blocks"": [""A""], ""all"": true }", IssueCodes.Constraint);
            Fails(@"{ ""layers"": [""I-FURN""], ""all"": true }", IssueCodes.Constraint);
            Fails(@"{ ""all"": true, ""expectCount"": 0 }", IssueCodes.Range);
            Fails(@"{ ""all"": true, ""space"": ""model"" }", IssueCodes.UnknownField);
            Fails(@"{ ""blocks"": ""BED"" }", IssueCodes.Type);
            Assert.Empty(Errors(@"{ ""layers"": [""I-FURN*""] }"));
            Assert.Empty(Errors(@"{ ""all"": true, ""excludeLayouts"": false }"));
        }

        // ------------------------------------------------------------------ placements.json

        [Fact]
        public void TheContractExampleReadsAndValidates()
        {
            var f = Placements.Parse(ContractExample);
            Assert.Empty(Placements.Validate(f));
            Assert.Equal("mm", f.Source.Units);
            Assert.Equal(LengthUnit.Millimetre, f.Source.LengthUnit);
            var b = f.Find("bed-double")!;
            Assert.Equal(new double[] { 800, 2100, 450 }, b.Max);
            Assert.Equal(90, b.References[0].RotationDeg);
            Assert.Equal(12500, b.References[0].Position[0]);
        }

        [Fact]
        public void WriteThenReadGivesTheSameFile()
        {
            var f = Sample();
            Assert.Empty(Placements.Validate(f));
            string text = Placements.Write(f);
            var back = Placements.Parse(text);
            Assert.Equal(text, Placements.Write(back));
            Assert.Empty(Placements.Validate(back));
            var door = back.Find("DOOR-SGL__1")!;
            Assert.True(door.IsDynamic && door.Has3d);
            Assert.Equal("Sheet A1", door.References[1].Space);
            Assert.Equal(new[] { 1.5, -2.25, 0 }, door.References[0].Position);
            Assert.Equal(0.000001, door.References[1].RotationDeg);

            var keys = JsonValue.Parse(text).AsObject();
            Assert.Equal(new[] { "format", "source", "blocks" }, keys.Keys);
            Assert.Equal(new[] { "name", "effectiveName", "file", "isDynamic", "extents", "has3d", "references" }, keys["blocks"]!.AsArray()[0].AsObject().Keys);
            Assert.Equal(new[] { "handle", "space", "layer", "position", "rotationDeg", "scale", "mirrored" },
                keys["blocks"]!.AsArray()[0].AsObject()["references"]!.AsArray()[0].AsObject().Keys);
        }

        [Fact]
        public void FilesRoundTripOnDiskAsUtf8WithoutBom()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sa-placements-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var f = Sample();
                f.Blocks[0].Name = f.Blocks[0].EffectiveName = "Bett ÄÖ";
                f.Blocks[0].File = "Bett ÄÖ.dwg";
                string path = Path.Combine(dir, PlacementsFile.DefaultFileName);
                Placements.WriteFile(f, path);
                Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
                var back = Placements.ReadFile(path);
                Assert.Equal("Bett ÄÖ", back.Blocks[0].Name);
                Assert.Contains(Placements.Validate(back, dir), i => i.Path == "$.blocks[0].file");
                foreach (var b in back.Blocks) File.WriteAllBytes(Path.Combine(dir, b.File), new byte[] { 1 });
                Assert.Empty(Placements.Validate(back, dir));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Theory]
        [InlineData(@"""format"": ""cadbim.placements/1""", @"""format"": ""cadbim.placements/2""")]
        [InlineData(@"""position"": [12500.0, 8400.0, 0.0]", @"""position"": [12500.0, 8400.0]")]
        [InlineData(@"""rotationDeg"": 90.0,", @"")]
        [InlineData(@"""insunits"": 4", @"""insunits"": ""mm""")]
        public void ShapeErrorsThrowWithAPath(string from, string to)
        {
            var e = Assert.ThrowsAny<FormatException>(() => Placements.Parse(ContractExample.Replace(from, to)));
            Assert.Contains("$", e.Message);
        }

        [Fact]
        public void TheValidatorEnforcesTheContractRules()
        {
            void Breaks(Action<PlacementsFile> change, string path)
            {
                var f = Sample();
                change(f);
                var issues = Placements.Validate(f);
                Assert.True(issues.Any(i => i.Path == path && i.Code == Placements.InvalidCode), path + ": " + string.Join("; ", issues));
            }
            Breaks(f => f.Source.Units = "millimeters", "$.source.units");
            Breaks(f => f.Source.Insunits = 0, "$.source.insunits");
            Breaks(f => f.Source.Drawing = " ", "$.source.drawing");
            Breaks(f => f.Blocks[1].IsDynamic = false, "$.blocks[1].name");
            Breaks(f => f.Blocks[1].Name = "DOOR-SGL__0", "$.blocks[1].name");
            Breaks(f => f.Blocks[1].Name = "DOOR-SGL_1", "$.blocks[1].name");
            Breaks(f => f.Blocks[1].Name = "bed-double", "$.blocks[1].name");
            Breaks(f => f.Blocks[1].File = "BED-DOUBLE.DWG", "$.blocks[1].file");
            Breaks(f => f.Blocks[1].File = "CON.dwg", "$.blocks[1].file");
            Breaks(f => f.Blocks[1].File = "a/b.dwg", "$.blocks[1].file");
            Breaks(f => f.Blocks[1].File = "door.rfa", "$.blocks[1].file");
            Breaks(f => f.Blocks[1].Min = new double[] { 0, 60, 0 }, "$.blocks[1].extents");
            Breaks(f => f.Blocks[1].Max = new[] { double.NaN, 0, 0 }, "$.blocks[1].extents");
            Breaks(f => f.Blocks[1].References[0].Mirrored = false, "$.blocks[1].references[0].mirrored");
            Breaks(f => f.Blocks[1].References[1].Mirrored = true, "$.blocks[1].references[1].mirrored");
            Breaks(f => f.Blocks[1].References[0].RotationDeg = 360, "$.blocks[1].references[0].rotationDeg");
            Breaks(f => f.Blocks[1].References[0].RotationDeg = -1, "$.blocks[1].references[0].rotationDeg");
            Breaks(f => f.Blocks[1].References[0].Scale = new double[] { 0, 1, 1 }, "$.blocks[1].references[0].scale");
            Breaks(f => f.Blocks[1].References[0].Handle = "2a1f", "$.blocks[1].references[0].handle");
            Breaks(f => f.Blocks[1].References[0].Handle = "ZZ", "$.blocks[1].references[0].handle");
            Breaks(f => f.Blocks[1].References[0].Layer = "", "$.blocks[1].references[0].layer");
            Breaks(f => f.Blocks[1].References[0].Position = new double[] { 0, 0 }, "$.blocks[1].references[0].position");
        }

        [Fact]
        public void OtherUnitsKeepTheirNames()
        {
            var f = Sample();
            f.Source.Insunits = 10;
            f.Source.Units = "yards";
            Assert.Empty(Placements.Validate(f));
            Assert.Null(f.Source.LengthUnit);
            Assert.Equal("ft", Placements.SymbolFor(2));
            Assert.Null(Placements.SymbolFor(21));
        }

        // ------------------------------------------------------------------ names

        [Fact]
        public void FileNamesAreSafeAndUnique()
        {
            var files = Placements.AssignFileNames(new[] { "BED|DOUBLE", "bed_double", "BED_DOUBLE-2", "Chair.", "CON", "Lpt1", "COM10", "Desk 1600 ", "A<>:\"/\\?*B", "placements" });
            Assert.Equal(new[] { "BED_DOUBLE.dwg", "bed_double-2.dwg", "BED_DOUBLE-2-2.dwg", "Chair_.dwg", null, null, "COM10.dwg", "Desk 1600_.dwg", "A________B.dwg", "placements.dwg" }, files);
            Assert.All(files.Where(n => n != null), n => Assert.True(Placements.IsSafeFileName(n!), n));
            Assert.Equal("_", Placements.SafeStem(""));
            Assert.Equal("a_b", Placements.SafeStem("a\tb"));
            Assert.True(Placements.IsReservedDeviceName("nul.dwg"));
            Assert.True(Placements.IsReservedDeviceName("CONOUT$"));
            Assert.False(Placements.IsReservedDeviceName("CONSOLE.dwg"));
        }

        [Fact]
        public void AnonymousRepresentationsAreNumberedFromOne()
        {
            Assert.Equal("DOOR__1", Placements.AnonymousName("DOOR", 1));
            Assert.True(Placements.IsAnonymousName("DOOR__12", "DOOR"));
            Assert.False(Placements.IsAnonymousName("DOOR__0", "DOOR"));
            Assert.False(Placements.IsAnonymousName("DOOR__01", "DOOR"));
            Assert.False(Placements.IsAnonymousName("DOOR__", "DOOR"));
            Assert.False(Placements.IsAnonymousName("DOOR__+1", "DOOR"));
            Assert.True(Placements.IsMirrored(new double[] { -1, 1, 1 }));
            Assert.False(Placements.IsMirrored(new double[] { -1, -1, 1 }));
            Assert.True(Placements.IsMirrored(new double[] { -1, -1, -1 }));
        }
    }
}
