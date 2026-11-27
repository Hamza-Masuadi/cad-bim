using System.Collections.Generic;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    public class JobTests
    {
        private static readonly OpRegistry Reg = OpRegistry.Default;

        internal const string GoodAcadJob = @"{
  ""jobId"": ""2026-11-27T10-15-00Z-walls-01"",
  ""title"": ""Ground floor partitions"",
  ""app"": ""autocad"",
  ""document"": { ""path"": ""C:/proj/plan.dwg"", ""fingerprint"": ""abc"" },
  ""units"": { ""length"": ""mm"" },
  ""ops"": [
    { ""id"": ""l1"", ""op"": ""layer.ensure"", ""args"": { ""layers"": [ { ""name"": ""A-WALL"", ""color"": 7 } ] } },
    { ""id"": ""w1"", ""op"": ""wall.draw"", ""args"": {
        ""thickness"": 100, ""layer"": ""A-WALL"",
        ""walls"": [ { ""id"": ""south"", ""start"": [0, 0], ""end"": [""6m"", 0] } ] } },
    { ""id"": ""d1"", ""op"": ""opening.place"", ""args"": {
        ""kind"": ""door"", ""block"": ""Door-900"", ""host"": ""$w1"", ""wallId"": ""south"", ""offset"": 1200, ""width"": 900 } },
    { ""id"": ""t1"", ""op"": ""text.add"", ""args"": { ""text"": ""$$ cost note"", ""position"": [0, -500], ""heightName"": ""body"", ""scale"": ""1:100"" } }
  ],
  ""verify"": { ""checks"": [""walls.open_ends""], ""capture"": [ { ""target"": ""activeView"" } ] }
}";

        private static JobValidation V(string json, JobValidationOptions? o = null) => JobValidator.Validate(json, Reg, o);

        private static JsonObject Mutable() => (JsonObject)JsonValue.Parse(GoodAcadJob);

        private static JsonObject OpArgsOf(JsonObject job, int i) => (JsonObject)((JsonObject)job["ops"]!.AsArray()[i])["args"]!;

        private static Issue Single(JobValidation v, string code) => Assert.Single(v.Issues, i => i.Code == code);

        [Fact]
        public void GoodJobParses()
        {
            var v = V(GoodAcadJob);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            var job = v.Job!;
            Assert.Equal(AppKind.AutoCad, job.App);
            Assert.Equal(LengthUnit.Millimetre, job.LengthUnit);
            Assert.Equal(4, job.Ops.Count);
            Assert.Equal("SA: Ground floor partitions", job.UndoName);
            Assert.Equal("$.ops[2]", job.FindOp("d1")!.Path);
            Assert.Equal(new[] { "walls.open_ends" }, job.Verify.Checks);
            Assert.Equal("abc", job.Document!.Fingerprint);
        }

        [Fact]
        public void MissingUnitsAndJobIdAreErrors()
        {
            var j = Mutable();
            j.Remove("units");
            j.Set("jobId", "bad id:1");
            var v = V(j.ToJson());
            Assert.False(v.Ok);
            Assert.Equal("$.units", Single(v, IssueCodes.Required).Path);
            Assert.Equal("$.jobId", Single(v, IssueCodes.BadId).Path);
            Assert.Null(v.Job);
        }

        [Fact]
        public void UnknownOpSuggestsTheNearestName()
        {
            var j = Mutable();
            ((JsonObject)j["ops"]!.AsArray()[1]).Set("op", "wall.drwa");
            var i = Single(V(j.ToJson()), IssueCodes.UnknownOp);
            Assert.Equal("$.ops[1].op", i.Path);
            Assert.Contains("did you mean 'wall.draw'", i.Message);
        }

        [Fact]
        public void WrongAppOp()
        {
            var j = Mutable();
            ((JsonObject)j["ops"]!.AsArray()[0]).Set("op", "level.ensure");
            Assert.Equal("$.ops[0].op", Single(V(j.ToJson()), IssueCodes.WrongApp).Path);
        }

        [Fact]
        public void DuplicateOpIdAndBadOpId()
        {
            var j = Mutable();
            ((JsonObject)j["ops"]!.AsArray()[3]).Set("id", "w1");
            Assert.Equal("$.ops[3].id", Single(V(j.ToJson()), IssueCodes.DuplicateId).Path);
            ((JsonObject)j["ops"]!.AsArray()[3]).Set("id", "1st");
            Assert.Equal("$.ops[3].id", Single(V(j.ToJson()), IssueCodes.BadId).Path);
        }

        [Fact]
        public void UnknownFieldsAreRejectedWithSuggestions()
        {
            var j = Mutable();
            OpArgsOf(j, 1).Set("thikness", 100);
            var i = Single(V(j.ToJson()), IssueCodes.UnknownField);
            Assert.Equal("$.ops[1].args.thikness", i.Path);
            Assert.Contains("thickness", i.Message);
            var j2 = Mutable();
            ((JsonObject)j2["ops"]!.AsArray()[0]).Set("layers", new JsonArray());
            Assert.Contains("inside 'args'", Single(V(j2.ToJson()), IssueCodes.UnknownField).Message);
            var j3 = Mutable();
            j3.Set("colour", 1);
            Assert.Equal("$.colour", Single(V(j3.ToJson()), IssueCodes.UnknownField).Path);
        }

        [Fact]
        public void TypeAndLengthErrorsPointIntoNestedLists()
        {
            var j = Mutable();
            var wall = (JsonObject)OpArgsOf(j, 1)["walls"]!.AsArray()[0];
            wall.Set("start", new JsonArray("abc", 0));
            wall.Set("end", new JsonArray(1, 2, 3));
            var v = V(j.ToJson());
            Assert.Contains(v.Issues, i => i.Path == "$.ops[1].args.walls[0].start[0]" && i.Code == IssueCodes.Type);
            Assert.Contains(v.Issues, i => i.Path == "$.ops[1].args.walls[0].end" && i.Message.Contains("[x, y]"));
        }

        [Fact]
        public void EnumAndRangeErrors()
        {
            var j = Mutable();
            OpArgsOf(j, 1).Set("cornerStyle", "round");
            OpArgsOf(j, 1).Set("thickness", 0);
            OpArgsOf(j, 2).Set("width", "-3'");
            var v = V(j.ToJson());
            Assert.Equal("$.ops[1].args.cornerStyle", Single(v, IssueCodes.Enum).Path);
            var ranges = v.Issues.Where(i => i.Code == IssueCodes.Range).Select(i => i.Path).ToList();
            Assert.Contains("$.ops[1].args.thickness", ranges);
            Assert.Contains("$.ops[2].args.width", ranges);
        }

        [Fact]
        public void LengthRangesAreConvertedToJobUnits()
        {
            var j = Mutable();
            j.Set("units", new JsonObject().Add("length", "m"));
            OpArgsOf(j, 0)["layers"]!.AsArray()[0].AsObject().Set("transparency", 95);
            var v = V(j.ToJson());
            Assert.Equal("$.ops[0].args.layers[0].transparency", Single(v, IssueCodes.Range).Path);
            Assert.True(V(Mutable().ToJson().Replace("\"thickness\": 100", "\"thickness\": \"4\\\"\"")).Ok);
        }

        [Fact]
        public void ConstraintsAcrossFields()
        {
            var j = Mutable();
            OpArgsOf(j, 1).Remove("walls");
            OpArgsOf(j, 2).Set("point", new JsonArray(10, 0));
            OpArgsOf(j, 3).Remove("scale");
            var v = V(j.ToJson());
            var paths = v.Issues.Where(i => i.Code == IssueCodes.Constraint).Select(i => i.Path).ToList();
            Assert.Contains("$.ops[1].args", paths);
            Assert.Contains("$.ops[2].args", paths);
            Assert.Contains("$.ops[3].args", paths);
        }

        [Fact]
        public void ReferenceErrors()
        {
            var j = Mutable();
            OpArgsOf(j, 2).Set("host", "$t1");
            Assert.Equal(IssueCodes.RefForward, V(j.ToJson()).Errors.Single().Code);
            OpArgsOf(j, 2).Set("host", "$nope");
            Assert.Equal(IssueCodes.RefUnknown, V(j.ToJson()).Errors.Single().Code);
            OpArgsOf(j, 2).Set("host", "$w1..walls");
            Assert.Equal(IssueCodes.RefInvalid, V(j.ToJson()).Errors.Single().Code);
            OpArgsOf(j, 2).Set("host", "$d1");
            Assert.Equal(IssueCodes.RefForward, V(j.ToJson()).Errors.Single().Code);
            OpArgsOf(j, 2).Set("host", "$w1.walls.0");
            Assert.True(V(j.ToJson()).Ok);
        }

        [Fact]
        public void ReferencesInsidePointsAreAllowed()
        {
            var j = Mutable();
            OpArgsOf(j, 3).Set("position", new JsonArray("$w1.outputs.loops.0.0.0", 0));
            Assert.True(V(j.ToJson()).Ok);
        }

        [Fact]
        public void DocumentIdentityIsAWarningUnlessRequired()
        {
            var j = Mutable();
            j.Remove("document");
            var v = V(j.ToJson());
            Assert.True(v.Ok);
            Assert.Equal(IssueSeverity.Warning, Single(v, IssueCodes.DocumentMissing).Severity);
            Assert.False(V(j.ToJson(), new JobValidationOptions { RequireDocument = true }).Ok);
        }

        [Fact]
        public void UnknownCheckIdsWhenCatalogueGiven()
        {
            var v = V(GoodAcadJob, new JobValidationOptions { KnownCheckIds = new HashSet<string> { "walls.open_endz" } });
            Assert.Contains("did you mean 'walls.open_endz'", Single(v, IssueCodes.UnknownCheck).Message);
        }

        [Fact]
        public void ScriptOpWarnsAndInvalidJsonIsReported()
        {
            var j = Mutable();
            j["ops"]!.AsArray().Add(new JsonObject().Add("id", "s1").Add("op", "script.lisp")
                .Add("args", new JsonObject().Add("code", "(princ)").Add("purpose", "noop")));
            var v = V(j.ToJson());
            Assert.True(v.Ok);
            Assert.Equal(IssueSeverity.Warning, Single(v, IssueCodes.Script).Severity);
            Assert.True(Reg.Find("script.lisp", AppKind.AutoCad)!.RequiresAllowScript);
            var bad = V("{\"jobId\": ");
            Assert.Equal(IssueCodes.Json, bad.Issues.Single().Code);
            Assert.Equal(IssueCodes.Type, V("[]").Issues.Single().Code);
            Assert.Throws<JobValidationException>(() => Job.Parse("{}", Reg));
        }

        [Fact]
        public void EmptyOpsAndRequiredStrings()
        {
            var j = Mutable();
            j.Set("ops", new JsonArray());
            Assert.Equal(IssueCodes.TooFew, V(j.ToJson()).Errors.Single().Code);
            var j2 = Mutable();
            OpArgsOf(j2, 2).Set("block", "  ");
            Assert.Equal("$.ops[2].args.block", Single(V(j2.ToJson()), IssueCodes.Required).Path);
        }

        [Fact]
        public void RevitJobWithFamilyTypePattern()
        {
            string job = @"{ ""jobId"": ""r1"", ""app"": ""revit"", ""units"": { ""length"": ""ft"" },
              ""ops"": [
                { ""id"": ""w1"", ""op"": ""wall.create"", ""args"": { ""type"": ""Generic - 8\"""", ""level"": ""Level 1"", ""start"": [0,0], ""end"": [""20'-6\"""", 0], ""height"": 10 } },
                { ""id"": ""d1"", ""op"": ""door.place"", ""args"": { ""host"": ""$w1.walls.0"", ""type"": ""Single-Flush : 36\"" x 84\"""", ""offset"": ""4'"" } },
                { ""id"": ""d2"", ""op"": ""door.place"", ""args"": { ""host"": ""$w1"", ""type"": ""Single-Flush"", ""offset"": 4 } },
                { ""id"": ""r1"", ""op"": ""room.place"", ""args"": { ""level"": ""Level 1"", ""point"": [5,5], ""tagType"": ""Room Tag"" } }
              ] }";
            var v = V(job);
            var e = Assert.Single(v.Errors);
            Assert.Equal("$.ops[3].args.tagType", e.Path);
            Assert.Equal(IssueCodes.Pattern, e.Code);
        }

        [Fact]
        public void RefsResolveAgainstResults()
        {
            var w = new OpResult("w1", "wall.draw") { Ok = true };
            w.Created.AddRange(new[] { "2A1", "2A2" });
            w.Readback.Add("length", 6000);
            w.Outputs.Add("walls", new JsonArray(new JsonObject().Add("id", "south").Add("handles", JsonArray.FromStrings(new[] { "2A1" }))));
            var targets = new Dictionary<string, JsonObject> { { "w1", w.ToRefTarget() } };

            var args = (JsonObject)JsonValue.Parse("{\"host\":\"$w1\",\"len\":\"$w1.length\",\"first\":\"$w1.walls.0.id\",\"ids\":\"$w1.created\",\"lit\":\"$$w1\",\"list\":[\"$w1.id\"]}");
            var r = (JsonObject)Refs.Resolve(args, targets, "$.ops[1].args");
            Assert.Equal("2A1", Refs.ElementId(r["host"]));
            Assert.Equal(new[] { "2A1", "2A2" }, Refs.ElementIds(r["host"]));
            Assert.Equal(6000, r["len"]!.AsInt64());
            Assert.Equal("south", r["first"]!.AsString());
            Assert.Equal(new[] { "2A1", "2A2" }, Refs.ElementIds(r["ids"]));
            Assert.Equal("$w1", r["lit"]!.AsString());
            Assert.Equal("2A1", r["list"]!.AsArray()[0].AsString());

            var e = Assert.Throws<RefResolutionException>(() => Refs.Resolve(JsonValue.Parse("{\"x\":\"$w1.nothing\"}"), targets, "$.a"));
            Assert.Equal("$.a.x", e.Path);
            Assert.Throws<RefResolutionException>(() => Refs.Resolve(JsonValue.Parse("[\"$w2\"]"), targets));
            Assert.Throws<RefResolutionException>(() => Refs.Resolve(JsonValue.Parse("[\"$w1.walls.5\"]"), targets));
            Assert.Equal(new[] { "$.b[0]" }, Refs.Find(JsonValue.Parse("{\"a\":\"$$x\",\"b\":[\"$w1\"]}"), "$").Select(k => k.Key));
        }

        [Fact]
        public void OpArgsReadsTypedValuesWithDefaults()
        {
            var job = V(GoodAcadJob).Job!;
            var op = job.FindOp("w1")!;
            var a = OpArgs.For(op.Schema, op.Args, job.LengthUnit, op.ArgsPath);
            Assert.Equal("mitre", a.GetString("cornerStyle"));
            Assert.Equal("centre", a.GetString("justification"));
            Assert.True(a.GetBool("capOpenEnds"));
            Assert.False(a.Has("cornerStyle"));
            Assert.Equal(100, a.GetLength("thickness"));
            Assert.Null(a.GetLengthOrNull("tolerance"));
            var wall = a.GetObjectList("walls").Single();
            Assert.Equal(new Point2(6000, 0), wall.GetPoint2("end"));
            Assert.Equal("$.ops[1].args.walls[0]", wall.Path);
            Assert.Null(wall.GetLengthOrNull("thickness"));
            Assert.Throws<System.ArgumentException>(() => a.Get("nonexistent"));
            Assert.Equal(1.5, OpArgs.ToLength(new JsonString("1'-6\""), LengthUnit.Foot), 12);
            var e = Assert.Throws<OpArgsException>(() => OpArgs.ToPoint2(JsonValue.Parse("[1]"), LengthUnit.Millimetre, "$.p"));
            Assert.Equal("$.p", e.Path);
        }

        [Fact]
        public void ResolvedArgsRevalidate()
        {
            var schema = Reg.Find("opening.place", AppKind.AutoCad)!;
            var args = (JsonObject)JsonValue.Parse("{\"kind\":\"door\",\"block\":\"D\",\"host\":{\"id\":\"2A1\"},\"wallId\":\"south\",\"offset\":1200,\"width\":900}");
            Assert.Empty(JobValidator.ValidateArgs(schema, args, LengthUnit.Millimetre));
            args.Set("host", true);
            Assert.Equal("$.args.host", JobValidator.ValidateArgs(schema, args, LengthUnit.Millimetre).Single().Path);
        }

        [Fact]
        public void ReportRoundTrip()
        {
            var r = new Report("j1", JobStatus.Committed, RunMode.Commit) { Undo = "One undo step named 'SA: x'" };
            var op = new OpResult("w1", "wall.draw") { Ok = true };
            op.Created.Add("2A1");
            op.Readback.Add("length", 6000);
            r.Results.Add(op);
            r.Validation.Add(new Issue("$.document", IssueCodes.DocumentMissing, "x", IssueSeverity.Warning));
            var cr = new CheckResult("walls.open_ends", CheckStatus.Pass) { Title = "Open ends", Severity = Severity.Warning };
            r.Checks.Add(cr);
            r.Artifacts.Add(new Artifact("png", "C:/out/view.png", "activeView"));
            r.Warnings.Add(new ReportWarning("app", "Wall overlaps", "w1"));
            r.TimingMs["execute"] = 340;
            var json = r.ToJson();
            Assert.Equal(1, json["summary"]!.AsObject()["created"]!.AsInt64());
            var back = Report.Parse(JsonValue.Parse(json.ToJson()));
            Assert.True(JsonValue.DeepEquals(json, back.ToJson()));
            Assert.Equal("committed", json.GetStringOrNull("status"));
        }
    }

    public class OpSchemaTests
    {
        private static readonly string[] AutoCad =
        {
            "doc.setup", "layer.ensure", "linetype.ensure", "textstyle.ensure", "dimstyle.ensure", "wall.draw", "opening.place", "grid.draw",
            "room.create", "dim.linear", "dim.string", "text.add", "mleader.add", "table.create", "schedule.fromBlocks", "block.define",
            "block.insert", "block.attrs.set", "block.dynprops.set", "layout.create", "viewport.create", "viewports.layout", "titleblock.fill",
            "plot.pdf", "xref.attach", "xref.repath", "entity.setprops", "layer.translate", "fix.bylayer", "fix.flattenZ", "fix.purge",
            "fix.units", "script.lisp",
        };

        private static readonly string[] Revit =
        {
            "level.ensure", "grid.create", "walltype.ensure", "wall.create", "door.place", "window.place", "floor.create", "ceiling.create",
            "roof.footprint", "room.place", "room.separator", "family.load", "type.duplicate", "param.set", "param.shared.ensure",
            "view.plan.create", "view.ceiling.create", "view.section.create", "view.elevation.create", "view.3d.create", "view.rename",
            "viewtemplate.apply", "view.filter.ensure", "sheet.create", "viewport.place", "viewports.layout", "schedule.create", "tag.all",
            "dim.grids", "text.note", "revision.ensure", "export.pdf", "export.dwg", "export.ifc", "export.image", "element.delete",
            "cleanup.unplacedRooms", "cleanup.unusedViews", "workset.assign",
        };

        [Fact]
        public void EveryWaveOneOpIsDeclaredExactlyOnce()
        {
            var reg = OpRegistry.CreateDefault();
            var acadWave2 = SA.Core.Jobs.Ops.AutoCadOpsWave2.All().Concat(SA.Core.Jobs.Ops.AutoCadOpsBlocks.All()).Select(s => s.Name);
            var revitWave2 = SA.Core.Jobs.Ops.RevitOpsModelWave2.All().Concat(SA.Core.Jobs.Ops.RevitOpsDocsWave2.All())
                .Concat(SA.Core.Jobs.Ops.RevitOpsFamilyWave2.All()).Concat(SA.Core.Jobs.Ops.RevitOpsCadFamilies.All()).Concat(SA.Core.Jobs.Ops.RevitOpsFamilyCreate.All()).Select(s => s.Name);
            Assert.Equal(AutoCad.Concat(acadWave2).OrderBy(x => x), reg.ForApp(AppKind.AutoCad).Select(s => s.Name).OrderBy(x => x));
            Assert.Equal(Revit.Concat(revitWave2).OrderBy(x => x), reg.ForApp(AppKind.Revit).Select(s => s.Name).OrderBy(x => x));
            Assert.Equal(AppKind.Revit, reg.Find("viewports.layout", AppKind.Revit)!.App);
            Assert.Equal(AppKind.AutoCad, reg.Find("viewports.layout", AppKind.AutoCad)!.App);
        }

        [Fact]
        public void DefaultsAreValidForTheirOwnFields()
        {
            foreach (var op in OpRegistry.Default.All)
                foreach (var f in Walk(op.Args))
                {
                    if (f.Default is null) continue;
                    var issues = JobValidator.ValidateArgs(Wrap(f), new JsonObject().Add(f.Name, f.Default), LengthUnit.Millimetre);
                    Assert.True(issues.Count == 0, op.Name + "." + f.Name + ": " + string.Join("; ", issues));
                }
        }

        [Fact]
        public void NonEmptyListsOutsideConstraintsAreRequired()
        {
            foreach (var op in OpRegistry.Default.All)
            {
                var inConstraint = new HashSet<string>(op.Constraints.SelectMany(c => c.Fields));
                foreach (var f in op.Fields.Where(f => f.Type == FieldType.List && f.MinItems >= 1 && !inConstraint.Contains(f.Name)))
                    Assert.True(f.Required, op.Name + "." + f.Name + " has a minimum item count but is optional");
            }
        }

        [Fact]
        public void EnumDefaultsAndDestructiveFlags()
        {
            var reg = OpRegistry.Default;
            foreach (var n in new[] { "element.delete", "cleanup.unplacedRooms", "cleanup.unusedViews", "fix.purge", "layer.translate" })
                Assert.True(reg.Find(n)!.Destructive, n);
            Assert.True(reg.Find("layer.ensure")!.Ensure);
            Assert.True(reg.Find("layer.ensure")!.FindField("update")!.Default is JsonBool b && !b.Value);
        }

        [Fact]
        public void SchemasSerialise()
        {
            var json = OpRegistry.Default.ToJson();
            var back = JsonValue.Parse(json.ToJson());
            Assert.Equal(json.Count, back.AsArray().Count);
            var wall = back.AsArray().First(o => o.AsObject().GetStringOrNull("name") == "wall.draw").AsObject();
            Assert.Equal("autocad", wall.GetStringOrNull("app"));
            var thickness = wall["fields"]!.AsArray().First(f => f.AsObject().GetStringOrNull("name") == "thickness").AsObject();
            Assert.Equal("length", thickness.GetStringOrNull("unit"));
            Assert.Equal(0, thickness["exclusiveMin"]!.AsInt64());
        }

        [Fact]
        public void BadDeclarationsThrow()
        {
            Assert.Throws<System.ArgumentException>(() => new OpSchema("x", AppKind.Revit, "", new[] { Field.String("a", ""), Field.String("a", "") }));
            Assert.Throws<System.ArgumentException>(() => new OpSchema("x", AppKind.Revit, "", new[] { Field.String("a", "") }, new[] { FieldConstraint.ExactlyOne("a", "b") }));
            var reg = new OpRegistry();
            reg.Register(new OpSchema("x", AppKind.Revit, "", new FieldSpec[0]));
            Assert.Throws<System.ArgumentException>(() => reg.Register(new OpSchema("x", AppKind.Revit, "", new FieldSpec[0])));
        }

        private static OpSchema Wrap(FieldSpec f) => new OpSchema("probe", AppKind.AutoCad, "", new[] { f });

        private static IEnumerable<FieldSpec> Walk(FieldSpec obj)
        {
            foreach (var f in obj.Fields)
            {
                yield return f;
                foreach (var g in Walk(f)) yield return g;
                if (f.Item != null && f.Item.Type == FieldType.Object)
                    foreach (var g in Walk(f.Item)) yield return g;
            }
        }
    }
}
