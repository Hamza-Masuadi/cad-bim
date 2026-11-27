using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Units;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>Audit options and loaders, report extras, preflight references and the other additions requested by the engines.</summary>
    public class CoreExtensionTests
    {
        // ----- Audit options and loading -----

        [Fact]
        public void AuditOptionsReadAndWriteParams()
        {
            var o = AuditOptions.FromParams((JsonObject)JsonValue.Parse(
                @"{ ""checks"": [""ac-001.layer-zero""], ""group"": ""layers"", ""groups"": [""layers"", ""units""], ""checksFile"": ""proj/checks.json"",
                    ""catalog"": [""more""], ""checksMode"": ""add"", ""standard"": ""proj/std.json"", ""includeFacts"": true, ""units"": ""mm"" }"));
            Assert.Equal(new[] { "ac-001.layer-zero" }, o.CheckIds);
            Assert.Equal(new[] { "layers", "units" }, o.Groups);
            Assert.Equal(new[] { "proj/checks.json", "more" }, o.ChecksFiles);
            Assert.True(o.AddToBundled);
            Assert.Equal("proj/std.json", o.StandardPath);
            Assert.True(o.IncludeFacts);
            var back = AuditOptions.FromParams(o.ToParams());
            Assert.Equal(o.CheckIds, back.CheckIds);
            Assert.Equal(o.Groups, back.Groups);
            Assert.Equal(o.ChecksFiles, back.ChecksFiles);
            Assert.Equal(o.AddToBundled, back.AddToBundled);
            Assert.Equal(o.StandardPath, back.StandardPath);
            Assert.Empty(AuditOptions.FromParams(null).ChecksFiles);
            Assert.Throws<FormatException>(() => AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""checksFile"": 3 }")));
            Assert.Throws<FormatException>(() => AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""checksMode"": ""merge"" }")));
            Assert.Throws<FormatException>(() => AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""standard"": true }")));
        }

        [Fact]
        public void AuditLoaderUsesBundledChecksUnlessTheProjectNamesItsOwn()
        {
            string bundled = Path.Combine(RealData.Dir, "checks");
            var setup = AuditLoader.Load(new AuditOptions(), new[] { bundled, Path.Combine(bundled, "does-not-exist") }, null, RealData.StandardPath("us-ncs-style.json"));
            Assert.True(setup.Ok, string.Join("\n", setup.Problems));
            Assert.Equal(130, setup.Catalog.Checks.Count);
            Assert.Equal(2, setup.CatalogFiles.Count);
            Assert.Equal("us-ncs-style", setup.Standard!.Id);
            Assert.Equal(63, setup.Select(CheckApp.AutoCad, new AuditOptions(), out _).Count);

            var dir = Path.Combine(Path.GetTempPath(), "sa-core-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "project-checks.json"),
                    @"{ ""version"": 1, ""checks"": [ { ""id"": ""proj.layer-zero"", ""app"": ""autocad"", ""title"": ""Nothing on 0"", ""severity"": ""error"",
                        ""evaluator"": ""field_equals"", ""params"": { ""table"": ""entities"", ""field"": ""layer"", ""equals"": ""0"" }, ""groups"": [""layers""] } ] }");
                File.Copy(RealData.StandardPath("iso19650-uk-style.json"), Path.Combine(dir, "standard.json"));

                // Replace: only the project's checks, paths relative to the document folder.
                var opts = AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""checksFile"": ""project-checks.json"", ""standard"": ""standard.json"" }"));
                setup = AuditLoader.Load(opts, new[] { bundled }, dir, RealData.StandardPath("us-ncs-style.json"));
                Assert.True(setup.Ok, string.Join("\n", setup.Problems));
                Assert.Equal(new[] { "proj.layer-zero" }, setup.Catalog.Checks.Select(c => c.Id));
                Assert.Equal("iso19650-uk-style", setup.Standard!.Id);
                Assert.Equal(Path.GetFullPath(Path.Combine(dir, "standard.json")), setup.StandardFile);

                // Add: bundled first, then the project's.
                opts.AddToBundled = true;
                setup = AuditLoader.Load(opts, new[] { bundled }, dir);
                Assert.Equal(131, setup.Catalog.Checks.Count);
                Assert.Equal("proj.layer-zero", setup.Catalog.Checks.Last().Id);

                // A copy of a bundled file added on top collides id for id and adds nothing.
                File.Copy(RealData.ChecksPath("autocad.json"), Path.Combine(dir, "autocad-copy.json"));
                setup = AuditLoader.Load(AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""checksFile"": ""autocad-copy.json"", ""checksMode"": ""add"" }")), new[] { bundled }, dir);
                Assert.False(setup.Ok);
                Assert.Contains("already defined", setup.Problems.Single());
                Assert.Equal(130, setup.Catalog.Checks.Count);

                // Missing and broken files are problems, not exceptions.
                File.WriteAllText(Path.Combine(dir, "broken.json"), @"{ ""id"": ""x"" }");
                setup = AuditLoader.Load(AuditOptions.FromParams((JsonObject)JsonValue.Parse(@"{ ""checksFile"": ""nope.json"", ""standard"": ""broken.json"" }")), null, dir);
                Assert.Equal(2, setup.Problems.Count);
                Assert.Contains("nope.json", setup.Problems[0]);
                Assert.Contains("broken.json", setup.Problems[1]);
                Assert.Null(setup.Standard);
                Assert.Throws<CheckFormatException>(() => CheckCatalog.LoadFiles(new[] { RealData.ChecksPath("autocad.json"), Path.Combine(dir, "autocad-copy.json") }));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ----- Report extras -----

        [Fact]
        public void ReportCarriesDocumentDialogsCommittedAndExtraKeys()
        {
            var r = new Report("j1", JobStatus.Failed, RunMode.Commit)
            {
                Committed = true,
                Document = new ReportDocument("C:/p/m.rvt", "fp-after", "fp-before"),
            };
            r.Dialogs.Add(new ReportDialog("TaskDialog", "TaskDialog_Warning", "Overlap") { DismissedWith = 2, Accepted = false });
            var w = new ReportWarning("app", "Highlighted walls overlap.", "w1") { Code = "b4176cef-6086-45a7-a066-12a3ad8e3a29", Severity = "warning" };
            w.Elements.AddRange(new[] { "101", "102" });
            r.Warnings.Add(w);
            r.Warnings.Add(new ReportWarning("engine", "plain"));
            r.Extra.Add("engine", new JsonObject().Add("release", 2026));
            var json = r.ToJson();
            Assert.True(json["committed"]!.AsBool());
            Assert.Equal("fp-before", json["document"]!.AsObject().GetStringOrNull("fingerprintBefore"));
            Assert.Equal(2026, json["engine"]!.AsObject()["release"]!.AsInt64());
            var plain = json["warnings"]!.AsArray()[1].AsObject();
            Assert.False(plain.ContainsKey("code") || plain.ContainsKey("elements") || plain.ContainsKey("severity"));

            var back = Report.Parse(JsonValue.Parse(json.ToJson()));
            Assert.True(back.Committed);
            Assert.Equal("fp-after", back.Document!.Fingerprint);
            Assert.Equal("C:/p/m.rvt", back.Document.Path);
            Assert.Equal(2, back.Dialogs[0].DismissedWith);
            Assert.False(back.Dialogs[0].Accepted);
            Assert.Equal(new[] { "101", "102" }, back.Warnings[0].Elements);
            Assert.Equal("warning", back.Warnings[0].Severity);
            Assert.StartsWith("b4176cef", back.Warnings[0].Code);
            Assert.True(back.Extra.ContainsKey("engine"));
            Assert.True(JsonValue.DeepEquals(json, back.ToJson()));
        }

        [Fact]
        public void ReportWithoutExtrasSerialisesAsBefore()
        {
            var r = new Report("j2", JobStatus.Committed, RunMode.Commit);
            var json = r.ToJson();
            foreach (var k in new[] { "committed", "document", "dialogs" }) Assert.False(json.ContainsKey(k), k);
            // The engines append document and dialogs to ToJson() output themselves; that must keep working, and parse.
            json.Add("document", new JsonObject().Add("path", "a.rvt").Add("fingerprint", "f").Add("fingerprintBefore", "e"));
            json.Add("dialogs", new JsonArray(new JsonValue[] { new JsonObject().Add("kind", "k").Add("dialogId", "d").Add("message", "m").Add("dismissedWith", 1).Add("accepted", true) }));
            json.Add("engine", new JsonObject().Add("version", "1"));
            var back = Report.Parse(json);
            Assert.Equal("e", back.Document!.FingerprintBefore);
            Assert.True(back.Dialogs.Single().Accepted);
            Assert.Null(back.Committed);
            Assert.Equal("1", back.Extra["engine"]!.AsObject().GetStringOrNull("version"));
        }

        // ----- Preflight references -----

        [Fact]
        public void PendingRefsMarkAndDetectReferences()
        {
            var args = (JsonObject)JsonValue.Parse(@"{ ""host"": ""$w1"", ""walls"": [""$w1.walls.0"", ""123""], ""text"": ""$$5"", ""n"": 3, ""o"": { ""a"": ""$x.y"" } }");
            var marked = PendingRefs.MarkArgs(args);
            Assert.True(PendingRefs.IsMarker(marked["host"]));
            Assert.Equal("$w1", PendingRefs.ReferenceOf(marked["host"]));
            Assert.Equal("$5", marked.GetStringOrNull("text"));
            Assert.True(PendingRefs.IsPending(marked["walls"]));
            Assert.False(PendingRefs.IsMarker(marked["walls"]));
            Assert.True(PendingRefs.IsPending(marked["o"]));
            Assert.False(PendingRefs.IsPending(marked["n"]));
            Assert.Equal("$w1", args.GetStringOrNull("host"));     // the original is untouched

            var schema = OpRegistry.Default.Find("opening.place", AppKind.AutoCad)!;
            var raw = (JsonObject)JsonValue.Parse(@"{ ""kind"": ""door"", ""block"": ""D"", ""host"": ""$w1.walls.0"", ""offset"": 100, ""width"": 900 }");
            var unresolved = OpArgs.For(schema, raw, LengthUnit.Millimetre);
            Assert.True(unresolved.IsRef("host"));
            Assert.False(unresolved.IsRef("block"));
            Assert.False(unresolved.IsPending("host"));
            var pre = OpArgs.For(schema, PendingRefs.MarkArgs(raw), LengthUnit.Millimetre);
            Assert.True(pre.IsPending("host"));
            Assert.True(pre.IsRef("host"));
            Assert.False(pre.IsPending("width"));
            Assert.False(pre.IsRef("notAField"));
        }

        // ----- Error details, issue codes -----

        [Fact]
        public void TimeoutStartedDetail()
        {
            var d = ErrorDetails.Timeout(false, "run");
            Assert.False(d["started"]!.AsBool());
            Assert.False(ErrorDetails.StartedOf(d));
            Assert.False(ErrorDetails.StartedOf(new JsonObject().Add("ran", false)));
            Assert.Null(ErrorDetails.StartedOf(null));
            Assert.Null(ErrorDetails.StartedOf(new JsonObject()));
            var resp = Response.Failure("r1", ErrorCodes.Timeout, "not started", d);
            Assert.False(resp.ToJson()["error"]!.AsObject()["details"]!.AsObject()[ErrorDetails.Started]!.AsBool());
        }

        [Fact]
        public void EngineIssueCodesAreConstants()
        {
            var fields = typeof(IssueCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
            foreach (var c in new[] { "no_handler", "units_unitless", "units_unsupported", "ref_deferred", "script_not_rehearsed", "script_not_allowed",
                "script_not_alone", "layer_missing", "textstyle_missing", "annotation_scale_missing", "capture_invalid", "not_found", "ambiguous",
                "existing_differs", "invalid_value", "already_exists", "family_document", "not_allowed", "not_prechecked", "readback_mismatch" })
                Assert.Contains(c, fields);
            Assert.Equal(fields.Count, fields.Distinct().Count());
        }

        // ----- Schema text and ranges -----

        [Fact]
        public void TightenedRevitSchemasRejectNonsense()
        {
            var wall = OpRegistry.Default.Find("wall.create", AppKind.Revit)!;
            var ok = (JsonObject)JsonValue.Parse(@"{ ""type"": ""Generic - 200mm"", ""level"": ""Level 0"", ""start"": [0,0], ""end"": [1000,0], ""height"": 3000 }");
            Assert.DoesNotContain(JobValidator.ValidateArgs(wall, ok, LengthUnit.Millimetre), i => i.IsError);
            var zero = (JsonObject)ok.DeepClone();
            zero.Set("height", 0);
            Assert.Contains(JobValidator.ValidateArgs(wall, zero, LengthUnit.Millimetre), i => i.IsError && i.Path.EndsWith("height", StringComparison.Ordinal));

            var image = OpRegistry.Default.Find("export.image", AppKind.Revit)!;
            var px = image.FindField("pixelWidth")!;
            Assert.Equal((32.0, 15000.0), (px.Min!.Value, px.Max!.Value));
            Assert.Contains("sheet:<number>", image.FindField("views")!.Description);
            Assert.Contains("view names", OpRegistry.Default.Find("viewports.layout", AppKind.Revit)!.FindField("views")!.Description);
            var titleBlock = OpRegistry.Default.Find("sheet.create", AppKind.Revit)!.FindField("titleBlock")!.Description;
            Assert.Contains("titleBlock.familyType", titleBlock);
            Assert.Contains("none otherwise", titleBlock);

            var script = OpRegistry.Default.Find("script.lisp", AppKind.AutoCad)!;
            Assert.Contains("alone in its job", script.Summary);
            Assert.Contains("does not evaluate", script.Summary);
            Assert.Contains("not rolled back", script.Summary);
            Assert.Contains("baseline", OpRegistry.Default.Find("text.add", AppKind.AutoCad)!.FindField("justify")!.Description);
        }

        // ----- Chain offsetting -----

        [Fact]
        public void ChainOffsetMeetsCornersAtTheOffsetLines()
        {
            var l = new List<Point2> { new Point2(0, 0), new Point2(1000, 0), new Point2(1000, 1000) };
            // Drawn on the left face: the centreline is 100 to the right of each segment.
            var c = ChainOffset.ToCentreline(l, false, 200, WallJustification.Left);
            AssertNear(new Point2(0, -100), c[0]);
            AssertNear(new Point2(1100, -100), c[1]);
            AssertNear(new Point2(1100, 1000), c[2]);
            c = ChainOffset.ToCentreline(l, false, 200, WallJustification.Right);
            AssertNear(new Point2(0, 100), c[0]);
            AssertNear(new Point2(900, 100), c[1]);
            AssertNear(new Point2(900, 1000), c[2]);
            Assert.Equal(l, ChainOffset.ToCentreline(l, false, 200, WallJustification.Centre));

            // Closed CCW square drawn on its outer face (the right side going CCW is outside): centreline moves in.
            var sq = new List<Point2> { new Point2(0, 0), new Point2(1000, 0), new Point2(1000, 1000), new Point2(0, 1000) };
            var inner = ChainOffset.Offset(sq, true, 100);
            AssertNear(new Point2(100, 100), inner[0]);
            AssertNear(new Point2(900, 100), inner[1]);
            AssertNear(new Point2(900, 900), inner[2]);
            AssertNear(new Point2(100, 900), inner[3]);

            // Collinear points keep the offset point; a zero-length segment is not shifted; short input is copied.
            var straight = ChainOffset.Offset(new List<Point2> { new Point2(0, 0), new Point2(500, 0), new Point2(1000, 0) }, false, 50);
            AssertNear(new Point2(500, 50), straight[1]);
            Assert.Single(ChainOffset.Offset(new List<Point2> { new Point2(1, 1) }, false, 50));
            Assert.True(ChainOffset.TryParseJustification("center", out var j) && j == WallJustification.Centre);
            Assert.False(ChainOffset.TryParseJustification("middle", out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => ChainOffset.ToCentreline(l, false, -1, WallJustification.Left));

            // The centreline feeds WallNetwork and gives one closed room.
            var room = ChainOffset.Offset(sq, true, 100);
            var inputs = Enumerable.Range(0, 4).Select(i => new WallInput("w" + i, room[i], room[(i + 1) % 4], 200)).ToList();
            var net = WallNetwork.Build(inputs, new WallNetworkOptions());
            Assert.Empty(net.OpenEnds);
            Assert.Empty(net.Issues);
        }

        private static void AssertNear(Point2 expected, Point2 actual)
        {
            Assert.True(expected.DistanceTo(actual) < 1e-9, "expected " + expected + ", got " + actual);
        }
    }
}
