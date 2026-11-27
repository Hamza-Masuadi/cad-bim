using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>
    /// The skill's op reference (references/jobs/*.md) is generated from the op schemas plus one example per op, and
    /// every example and every recipe job in the references must pass static validation. Set SA_WRITE_REFS=1 to
    /// rewrite the reference files instead of comparing them.
    /// </summary>
    public class ReferenceDocsTests
    {
        private static string Skill => Path.GetFullPath(Path.Combine(DataFiles.DataDir, ".."));
        private static string JobsDir => Path.Combine(Skill, "references", "jobs");

        private static readonly Dictionary<string, JsonObject> Doc = new Dictionary<string, JsonObject>
        {
            ["autocad"] = new JsonObject().Add("path", "C:/Projects/2417/2417-A-PLAN-L01.dwg").Add("fingerprint", "copy-from-snapshot"),
            ["revit"] = new JsonObject().Add("path", "C:/Projects/2417/2417-ARCH.rvt").Add("fingerprint", "copy-from-snapshot"),
        };

        /// <summary>Lint always warns about the escape hatch; that warning is the rule, not a defect.</summary>
        private static readonly HashSet<string> ExpectedWarnings = new HashSet<string> { "script" };

        /// <summary>app to op name to { args, before? }: wave 1 and family.edit from op-examples.json, wave 2 from the builders' tests.</summary>
        private static Dictionary<string, Dictionary<string, JsonObject>> Examples()
        {
            var file = JsonValue.Parse(File.ReadAllText(Path.Combine(Skill, "engine", "src", "SA.Core.Tests", "op-examples.json"))).AsObject();
            var all = new Dictionary<string, Dictionary<string, JsonObject>>();
            foreach (var app in new[] { "autocad", "revit" })
                all[app] = file[app]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value.AsObject());
            void Add(string app, Dictionary<string, string> args)
            {
                foreach (var kv in args) all[app].Add(kv.Key, new JsonObject().Add("args", JsonValue.Parse(kv.Value)));
            }
            Add("autocad", AutoCadWave2Tests.Examples);
            Add("autocad", BlockExportTests.Examples);
            Add("revit", RevitModelWave2Tests.Examples);
            Add("revit", RevitDocsWave2Tests.Examples);
            Add("revit", CadFamiliesTests.Examples);
            Add("revit", FamilyCreateTests.Examples);
            return all;
        }

        private static JsonObject ExampleJob(string app, string op, JsonObject ex)
        {
            var ops = ex["before"] is JsonArray before ? (JsonArray)before.DeepClone() : new JsonArray();
            ops.Add(new JsonObject().Add("id", "op1").Add("op", op).Add("args", ex["args"]!.DeepClone()));
            return new JsonObject()
                .Add("jobId", "example-" + app + "-" + op.Replace('.', '-'))
                .Add("title", "Example: " + op)
                .Add("app", app)
                .Add("document", Doc[app].DeepClone())
                .Add("units", new JsonObject().Add("length", "mm"))
                .Add("ops", ops);
        }

        private static List<string> Problems(string jobText)
        {
            var v = JobValidator.Validate(jobText, OpRegistry.Default);
            return v.Issues.Where(i => i.IsError || !ExpectedWarnings.Contains(i.Code)).Select(i => i.ToString()).ToList();
        }

        [Fact]
        public void EveryOpHasExactlyOneExampleAndItIsLintClean()
        {
            var examples = Examples();
            var failures = new List<string>();
            foreach (var app in examples.Keys)
            {
                var kind = app == "autocad" ? AppKind.AutoCad : AppKind.Revit;
                var names = OpRegistry.Default.ForApp(kind).Select(s => s.Name).ToList();
                failures.AddRange(names.Except(examples[app].Keys).Select(n => app + " " + n + ": no example"));
                failures.AddRange(examples[app].Keys.Except(names).Select(n => app + " " + n + ": example for an unknown op"));
                foreach (var kv in examples[app].Where(kv => names.Contains(kv.Key)))
                    failures.AddRange(Problems(ExampleJob(app, kv.Key, kv.Value).ToJson()).Select(p => app + " " + kv.Key + ": " + p));
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Theory]
        [InlineData("autocad")]
        [InlineData("revit")]
        public void ReferenceFileIsCurrent(string app)
        {
            var text = Render(app, Examples()[app]);
            var path = Path.Combine(JobsDir, app + ".md");
            if (Environment.GetEnvironmentVariable("SA_WRITE_REFS") == "1")
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return;
            }
            Assert.True(File.Exists(path) && File.ReadAllText(path) == text,
                path + " is out of date: run `SA_WRITE_REFS=1 dotnet test src/SA.Core.Tests --filter ReferenceDocsTests` from engine/.");
        }

        [Fact]
        public void RecipeJobsInTheReferencesAreLintClean()
        {
            var failures = new List<string>();
            int total = 0;
            foreach (var path in new[] { "autocad.md", "revit.md", "workflow.md" }.Select(f => Path.Combine(Skill, "references", f)))
            {
                var text = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(text, "```json\\n(.*?)\\n```", RegexOptions.Singleline))
                {
                    var block = m.Groups[1].Value;
                    if (!block.Contains("\"jobId\"")) continue;
                    total++;
                    int line = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
                    failures.AddRange(Problems(block).Select(p => Path.GetFileName(path) + ":" + line + ": " + p));
                }
            }
            Assert.True(total >= 30, "only " + total + " recipe jobs found");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ------------------------------------------------------------------------------------------- rendering

        private static string Render(string app, Dictionary<string, JsonObject> examples)
        {
            var kind = app == "autocad" ? AppKind.AutoCad : AppKind.Revit;
            var ops = OpRegistry.Default.ToJson(kind).Select(o => o.AsObject()).ToList();
            var title = app == "autocad" ? "AutoCAD" : "Revit";
            var l = new List<string>
            {
                "# " + title + " job ops",
                "",
                "Generated from the op schemas in the engine by `ReferenceDocsTests` in `engine/src/SA.Core.Tests`. Do not edit by hand: change the op schema or its example, then regenerate.",
                "",
                ops.Count + " ops. Every example below passes `sa job lint`. Names in the examples (layers, blocks, types, views, sheets, ids) are placeholders: take the real ones from the snapshot or create them earlier in the same job. `document` is copied from the snapshot's `identity`.",
                "",
                "How to read the field tables:",
                "",
                "- `name*` in `sa ops` is \"yes\" in the Required column. `a.b` is a field of object `a`; `a[].b` is a field of each item of list `a`.",
                "- **length**: a number in the job's `units.length`, or a string with its own unit (`\"3.2m\"`, `\"10'-6\\\"\"`). Fields described as paper sizes take millimetres on the sheet for a metric job and inches for an imperial one, whatever the job unit.",
                "- **degrees**: angles, counter-clockwise.",
                "- **ref**: a `$id` reference to an earlier op's result (`\"$w1\"`, `\"$w1.walls.0\"`), or what the description allows (handle, element id, exact name).",
                "- **ensure** ops leave an existing object alone (differences become a warning) unless `update` is true. **destructive** ops delete; list exactly what will go in the plan.",
                "",
                "## Index",
                "",
                "| Op | Flags | What it does |",
                "|---|---|---|",
            };
            foreach (var o in ops)
                l.Add("| [`" + Name(o) + "`](#" + Anchor(Name(o)) + ") | " + Flags(o) + " | " + Cell(FirstSentence(o.GetStringOrNull("summary") ?? "")) + " |");
            l.Add("");
            foreach (var o in ops)
            {
                l.Add("## " + Name(o));
                l.Add("");
                var fl = Flags(o);
                if (fl.Length > 0) { l.Add("Flags: " + fl + "."); l.Add(""); }
                l.Add(o.GetStringOrNull("summary") ?? "");
                l.Add("");
                var rows = new List<string>();
                var rules = Arr(o, "constraints").Select(c => Constraint(c.AsObject())).ToList();
                FieldRows(Arr(o, "fields"), "", rows, rules);
                if (rows.Count > 0)
                {
                    l.Add("| Field | Type | Required | Default | Limits | Description |");
                    l.Add("|---|---|---|---|---|---|");
                    l.AddRange(rows);
                    l.Add("");
                }
                if (rules.Count > 0) { l.Add("Rules across fields: " + string.Join("; ", rules) + "."); l.Add(""); }
                var outs = Arr(o, "outputs").ToList();
                if (outs.Count > 0)
                {
                    l.Add("Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):");
                    l.Add("");
                    foreach (var f in outs.Select(x => x.AsObject()))
                        l.Add("- `" + Name(f) + "` (" + TypeLabel(f).Replace("\\|", "|") + "): " + (f.GetStringOrNull("description") ?? ""));
                    l.Add("");
                }
                l.Add("Example (lint clean):");
                l.Add("");
                l.Add("```json");
                l.Add(ExampleJob(app, Name(o), examples[Name(o)]).ToJson(indented: true).Replace("\r\n", "\n"));
                l.Add("```");
                l.Add("");
            }
            return string.Join("\n", l);
        }

        private static string Name(JsonObject o) => o.GetStringOrNull("name") ?? "";

        private static IEnumerable<JsonValue> Arr(JsonObject o, string key) => o[key] is JsonArray a ? a : Enumerable.Empty<JsonValue>();

        private static bool Flag(JsonObject o, string key) => o[key] is JsonBool b && b.Value;

        private static string Flags(JsonObject o)
        {
            var f = new List<string>();
            if (Flag(o, "ensure")) f.Add("ensure");
            if (Flag(o, "destructive")) f.Add("destructive");
            if (Flag(o, "requiresAllowScript")) f.Add("needs `--allow-script`, alone in its job");
            if (Name(o) == "plot.pdf") f.Add("alone in its job (only `plot.pdf` ops)");
            if (Name(o) == "family.edit") f.Add("alone in its job");
            if (Name(o) == "block.export") f.Add("alone in its job");
            if (Name(o) == "family.fromcad") f.Add("alone in its job");
            if (Name(o) == "family.create") f.Add("alone in its job");
            return string.Join(", ", f);
        }

        private static string TypeLabel(JsonObject f)
        {
            var t = f.GetStringOrNull("type") ?? "";
            if (t == "enum") return "enum(" + string.Join("\\|", Arr(f, "enum").Select(e => e.AsString())) + ")";
            if ((t == "list" || t == "map") && f["item"] is JsonObject item) return t + "<" + TypeLabel(item) + ">";
            return t;
        }

        private static string Num(JsonValue v) => v is JsonNumber n ? n.Raw : v.ToJson();

        private static string Limits(JsonObject f)
        {
            var o = new List<string>();
            if (f["min"] is JsonValue min) o.Add(">= " + Num(min));
            if (f["exclusiveMin"] is JsonValue emin) o.Add("> " + Num(emin));
            if (f["max"] is JsonValue max) o.Add("<= " + Num(max));
            if (f["minItems"] is JsonNumber mi) o.Add("at least " + mi.Raw + " item" + (mi.Raw == "1" ? "" : "s"));
            if (f["maxItems"] is JsonNumber ma) o.Add("at most " + ma.Raw + " items");
            if (f.GetStringOrNull("pattern") is string p) o.Add("matches `" + p.Replace("|", "\\|") + "`");
            var item = f["item"] as JsonObject;
            var unit = f.GetStringOrNull("unit") ?? item?.GetStringOrNull("unit");
            if (unit == "length") o.Add("length");
            else if (unit == "angle") o.Add("degrees");
            if (item != null && item.GetStringOrNull("type") == "enum")
                o.Add("each one of " + string.Join(", ", Arr(item, "enum").Select(e => "`" + e.AsString() + "`")));
            return string.Join("; ", o);
        }

        private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

        private static string Constraint(JsonObject c)
        {
            var fs = Arr(c, "fields").Select(x => "`" + x.AsString() + "`").ToList();
            var k = c.GetStringOrNull("kind") ?? "";
            switch (k)
            {
                case "exactlyOne": return "exactly one of " + string.Join(", ", fs);
                case "atLeastOne": return "at least one of " + string.Join(", ", fs);
                case "atMostOne": return "at most one of " + string.Join(", ", fs);
                case "requires": return fs[0] + " requires " + string.Join(", ", fs.Skip(1));
                default: return k + " " + string.Join(", ", fs);
            }
        }

        private static void FieldRows(IEnumerable<JsonValue> fields, string prefix, List<string> rows, List<string> rules)
        {
            foreach (var f in fields.Select(x => x.AsObject()))
            {
                var name = prefix + Name(f);
                var def = f["default"] is JsonValue d ? "`" + d.ToJson() + "`" : "";
                rows.Add("| `" + name + "` | " + Cell(TypeLabel(f)) + " | " + (Flag(f, "required") ? "yes" : "") + " | " + Cell(def) + " | "
                         + Cell(Limits(f)) + " | " + Cell(f.GetStringOrNull("description") ?? "") + " |");
                if (f.GetStringOrNull("type") == "object")
                {
                    rules.AddRange(Arr(f, "constraints").Select(c => "in `" + name + "`: " + Constraint(c.AsObject())));
                    FieldRows(Arr(f, "fields"), name + ".", rows, rules);
                }
                if (f["item"] is JsonObject item && item.GetStringOrNull("type") == "object")
                {
                    rules.AddRange(Arr(item, "constraints").Select(c => "in each `" + name + "[]`: " + Constraint(c.AsObject())));
                    FieldRows(Arr(item, "fields"), name + "[].", rows, rules);
                }
            }
        }

        private static string FirstSentence(string s)
        {
            var m = Regex.Match(s, @"^(.+?\.)(\s|$)");
            return m.Success ? m.Groups[1].Value : s;
        }

        private static string Anchor(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9 -]", "").Replace(" ", "-");
    }
}
