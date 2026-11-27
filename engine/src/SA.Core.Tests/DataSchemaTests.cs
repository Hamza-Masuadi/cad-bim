using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Json;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>
    /// Every data file matches its JSON schema in data/schemas: the examples, data/design, data/standards and
    /// data/checks. Implements the parts of JSON Schema draft 2020-12 those schemas use. Design data also needs
    /// min &lt;= max and ids unique across files.
    /// </summary>
    public class DataSchemaTests
    {
        private static string Data => DataFiles.DataDir;

        public static IEnumerable<object[]> Files()
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Data, "schemas", "examples"), "*.json").OrderBy(f => f))
                yield return new object[] { Path.GetFileName(f).Split('.')[0], Rel(f) };
            foreach (var (sub, schema) in new[] { ("design", "design-data"), ("standards", "standard"), ("checks", "checks") })
                foreach (var f in Directory.GetFiles(Path.Combine(Data, sub), "*.json").OrderBy(f => f))
                    yield return new object[] { schema, Rel(f) };
        }

        private static string Rel(string f) => f.Substring(Data.Length + 1).Replace('\\', '/');

        [Fact]
        public void AllDataFilesAreCovered() => Assert.Equal(29, Files().Count());

        [Theory]
        [MemberData(nameof(Files))]
        public void FileMatchesItsSchema(string schema, string file)
        {
            var root = JsonValue.Parse(File.ReadAllText(Path.Combine(Data, "schemas", schema + ".schema.json")));
            var data = JsonValue.Parse(File.ReadAllText(Path.Combine(Data, file)));
            var errors = new List<string>();
            Validate(data, root, root, "$", errors);
            if (schema == "design-data" && data is JsonObject d && d["entries"] is JsonArray entries)
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i] is JsonObject e && e["min"] is JsonNumber lo && e["max"] is JsonNumber hi && lo.Value > hi.Value)
                        errors.Add("$.entries[" + i + "]: min " + lo.Raw + " > max " + hi.Raw);
            Assert.True(errors.Count == 0, file + ":\n" + string.Join("\n", errors.Take(20)));
        }

        [Fact]
        public void ValidatorRejectsBrokenData()
        {
            var root = JsonValue.Parse(File.ReadAllText(Path.Combine(Data, "schemas", "checks.schema.json")));
            var good = JsonValue.Parse(File.ReadAllText(Path.Combine(Data, "checks", "autocad.json"))).AsObject();
            Assert.Empty(Collect(good, root, root, "$"));
            var noChecks = (JsonObject)good.DeepClone();
            noChecks.Remove("checks");
            Assert.Contains(Collect(noChecks, root, root, "$"), e => e.Contains("'checks'"));
            var extra = (JsonObject)good.DeepClone();
            extra.Add("bogus", 1);
            var wrongType = (JsonObject)good.DeepClone();
            wrongType.Set("checks", "not a list");
            Assert.NotEmpty(Collect(extra, root, root, "$").Concat(Collect(wrongType, root, root, "$")));
        }

        [Fact]
        public void DesignDataIdsAreUniqueAcrossFiles()
        {
            var seen = new Dictionary<string, string>();
            var dupes = new List<string>();
            foreach (var f in Directory.GetFiles(Path.Combine(Data, "design"), "*.json"))
                if (JsonValue.Parse(File.ReadAllText(f)) is JsonObject d && d["entries"] is JsonArray entries)
                    foreach (var id in entries.OfType<JsonObject>().Select(e => e.GetStringOrNull("id")).Where(id => id != null))
                    {
                        if (seen.TryGetValue(id!, out var other)) dupes.Add(id + " in " + Path.GetFileName(f) + " and " + other);
                        else seen[id!] = Path.GetFileName(f);
                    }
            Assert.Empty(dupes);
        }

        private static List<string> Collect(JsonValue v, JsonValue s, JsonValue root, string path)
        {
            var e = new List<string>();
            Validate(v, s, root, path, e);
            return e;
        }

        private static void Validate(JsonValue v, JsonValue schema, JsonValue root, string path, List<string> errors)
        {
            if (schema is JsonBool b)
            {
                if (!b.Value) errors.Add(path + ": not allowed");
                return;
            }
            var s = schema.AsObject();
            if (s.GetStringOrNull("$ref") is string r)
            {
                if (!r.StartsWith("#/")) throw new NotSupportedException("only local refs: " + r);
                JsonValue target = root;
                foreach (var part in r.Substring(2).Split('/')) target = target.AsObject()[part] ?? throw new KeyNotFoundException(r);
                Validate(v, target, root, path, errors);
            }
            if (s["type"] is JsonValue t)
            {
                var types = t is JsonArray ta ? ta.Select(x => x.AsString()).ToList() : new List<string> { t.AsString() };
                if (!types.Any(x => TypeOk(v, x))) { errors.Add(path + ": expected " + string.Join("|", types)); return; }
            }
            if (s["enum"] is JsonArray en && !en.Any(x => Same(v, x))) errors.Add(path + ": " + Short(v) + " is not an allowed value");
            if (s["const"] is JsonValue c && !Same(v, c)) errors.Add(path + ": must be " + c.ToJson());
            if (v is JsonString str)
            {
                if (s["minLength"] is JsonNumber mnl && str.Value.Length < mnl.Value) errors.Add(path + ": shorter than " + mnl.Raw);
                if (s["maxLength"] is JsonNumber mxl && str.Value.Length > mxl.Value) errors.Add(path + ": longer than " + mxl.Raw);
                if (s.GetStringOrNull("pattern") is string p && !Regex.IsMatch(str.Value, p)) errors.Add(path + ": " + Short(v) + " does not match " + p);
            }
            if (v is JsonNumber n)
            {
                if (s["minimum"] is JsonNumber mn && n.Value < mn.Value) errors.Add(path + ": below minimum " + mn.Raw);
                if (s["maximum"] is JsonNumber mx && n.Value > mx.Value) errors.Add(path + ": above maximum " + mx.Raw);
                if (s["exclusiveMinimum"] is JsonNumber emn && n.Value <= emn.Value) errors.Add(path + ": not above " + emn.Raw);
                if (s["exclusiveMaximum"] is JsonNumber emx && n.Value >= emx.Value) errors.Add(path + ": not below " + emx.Raw);
            }
            if (v is JsonArray a)
            {
                if (s["minItems"] is JsonNumber mi && a.Count < mi.Value) errors.Add(path + ": fewer than " + mi.Raw + " items");
                if (s["maxItems"] is JsonNumber ma && a.Count > ma.Value) errors.Add(path + ": more than " + ma.Raw + " items");
                if (s["uniqueItems"] is JsonBool u && u.Value && a.Select(x => x.ToJson()).Distinct().Count() != a.Count) errors.Add(path + ": items are not unique");
                if (s["items"] is JsonValue items)
                    for (int i = 0; i < a.Count; i++) Validate(a[i], items, root, path + "[" + i + "]", errors);
            }
            if (v is JsonObject o)
            {
                foreach (var req in s["required"] is JsonArray ra ? ra.Select(x => x.AsString()) : Enumerable.Empty<string>())
                    if (!o.ContainsKey(req)) errors.Add(path + ": missing required member '" + req + "'");
                var props = s["properties"] as JsonObject;
                foreach (var kv in o)
                {
                    if (props != null && props[kv.Key] is JsonValue ps) Validate(kv.Value, ps, root, path + "." + kv.Key, errors);
                    else if (s["additionalProperties"] is JsonBool ab && !ab.Value) errors.Add(path + "." + kv.Key + ": unknown member");
                    else if (s["additionalProperties"] is JsonObject aps) Validate(kv.Value, aps, root, path + "." + kv.Key, errors);
                }
            }
            foreach (var sub in s["allOf"] is JsonArray all ? all : new JsonArray()) Validate(v, sub, root, path, errors);
            if (s["anyOf"] is JsonArray any && !any.Any(x => Collect(v, x, root, path).Count == 0)) errors.Add(path + ": matches none of anyOf");
            if (s["oneOf"] is JsonArray one)
            {
                int matched = one.Count(x => Collect(v, x, root, path).Count == 0);
                if (matched != 1) errors.Add(path + ": matches " + matched + " of oneOf (needs exactly 1)");
            }
            if (s["not"] is JsonValue not && Collect(v, not, root, path).Count == 0) errors.Add(path + ": matches a forbidden form");
            if (s["if"] is JsonValue cond)
            {
                if (Collect(v, cond, root, path).Count == 0) { if (s["then"] is JsonValue th) Validate(v, th, root, path, errors); }
                else if (s["else"] is JsonValue el) Validate(v, el, root, path, errors);
            }
        }

        private static bool TypeOk(JsonValue v, string t)
        {
            switch (t)
            {
                case "null": return v.IsNull;
                case "boolean": return v is JsonBool;
                case "integer": return v is JsonNumber n && Math.Abs(n.Value % 1) == 0;
                case "number": return v is JsonNumber;
                case "string": return v is JsonString;
                case "array": return v is JsonArray;
                case "object": return v is JsonObject;
                default: throw new NotSupportedException("type " + t);
            }
        }

        private static bool Same(JsonValue a, JsonValue b) =>
            a is JsonNumber x && b is JsonNumber y ? x.Value == y.Value : a.ToJson() == b.ToJson();

        private static string Short(JsonValue v)
        {
            var j = v.ToJson();
            return j.Length > 60 ? j.Substring(0, 60) : j;
        }
    }
}
