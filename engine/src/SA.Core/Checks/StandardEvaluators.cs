using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;

namespace SA.Core.Checks
{
    /// <summary>
    /// The second set of neutral evaluators (data/checks/FACTS.md section 3). Like <see cref="NeutralEvaluators"/>
    /// they read only facts and the standard, take <c>table</c> and the optional <c>where</c>, and every finding
    /// carries <c>field</c> and <c>value</c> in its data. They are part of <see cref="EvaluatorRegistry.CreateNeutral"/>.
    /// </summary>
    public static class StandardEvaluators
    {
        public static IEnumerable<ICheckEvaluator> All()
        {
            yield return new ConditionalFieldEquals();
            yield return new FieldWithin();
            yield return new DistinctCountMin();
            yield return new ScaleInStandard();
            yield return new UnitsMatchStandard();
            yield return new DimPrecisionMin();
            yield return new MatchesStandardEntry();
            yield return new GroupCountOverThreshold();
            yield return new ManualReview();
        }

        internal static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        internal static JsonValue NumOrNull(double? v) => v.HasValue ? (JsonValue)v.Value : JsonValue.Null;

        /// <summary>A param that must be a JSON array when present.</summary>
        internal static JsonArray? ParamArrayOrNull(CheckContext ctx, string name)
        {
            var v = ctx.Params[name];
            if (v is null || v.IsNull) return null;
            return v as JsonArray ?? throw new CheckParamException("Check '" + ctx.Check.Id + "': param '" + name + "' must be a list.");
        }
    }

    /// <summary>
    /// <c>conditional_field_equals</c> { table, where?, field?, equals | in, negate?, ifField, ifPattern | ifStandardPattern,
    /// ifNegate?, ignoreCase? }: among the records whose <c>ifField</c> matches the pattern (XOR ifNegate), a finding when
    /// <c>field</c> equals the value (XOR negate); with no <c>field</c>, every such record is a finding.
    /// </summary>
    public sealed class ConditionalFieldEquals : ICheckEvaluator
    {
        public string Name => "conditional_field_equals";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string ifField = ctx.ParamString("ifField");
            bool ifNegate = ctx.ParamBool("ifNegate", false);
            bool negate = ctx.ParamBool("negate", false);
            bool ignoreCase = ctx.ParamBool("ignoreCase", false);
            string? field = ctx.ParamStringOrNull("field");
            if (field != null && field.Length == 0) throw new CheckParamException("param 'field' must not be empty.");
            var eq = ctx.Params["equals"];
            var inList = StandardEvaluators.ParamArrayOrNull(ctx, "in");
            List<JsonValue>? values = null;
            if (field != null)
            {
                if ((eq is null) == (inList is null)) throw new CheckParamException("give exactly one of 'equals' or 'in' with 'field'.");
                values = inList != null ? inList.ToList() : new List<JsonValue> { eq! };
            }
            else if (eq != null || inList != null) throw new CheckParamException("'equals' and 'in' need 'field'.");

            string? p = ctx.ParamStringOrNull("ifPattern");
            string? sp = ctx.ParamStringOrNull("ifStandardPattern");
            if ((p is null) == (sp is null)) throw new CheckParamException("give exactly one of 'ifPattern' or 'ifStandardPattern'.");
            var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            Regex rx;
            if (p != null)
            {
                try { rx = new Regex("^(?:" + p + ")$", options); }
                catch (ArgumentException e) { throw new CheckParamException("invalid ifPattern: " + e.Message); }
            }
            else
            {
                var std = ctx.RequireStandard();
                if (!std.Patterns.TryGetValue(sp!, out var named)) throw new CheckParamException("the standard has no pattern '" + sp + "'.");
                rx = ignoreCase ? new Regex(named.ToString(), options) : named;
            }

            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var ifValue = r.Get(ifField);
                bool match = ifValue is JsonString s && rx.IsMatch(s.Value);
                if (match == ifNegate) continue;
                JsonValue? value;
                string reported;
                if (field != null)
                {
                    value = r.Get(field);
                    bool equal = value != null && values!.Any(x => JsonValue.DeepEquals(value, x));
                    if (equal == negate) continue;
                    reported = field;
                }
                else
                {
                    value = ifValue;
                    reported = ifField;
                }
                list.Add(NeutralEvaluators.Make(r, table,
                    reported + " is " + NeutralEvaluators.Text(value) + " on " + ifField + " '" + NeutralEvaluators.Text(ifValue) + "'" + NeutralEvaluators.Label(r),
                    reported, value,
                    new JsonObject().Add("ifField", ifField).Add("ifValue", ifValue?.DeepClone() ?? JsonValue.Null)));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>field_within</c> { table, where?, field, min?, max?, minExclusive?, maxExclusive? }: a finding for each number
    /// inside the interval (the inverse of field_range). Non-numbers and absent values are ignored.
    /// </summary>
    public sealed class FieldWithin : ICheckEvaluator
    {
        public string Name => "field_within";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            double? min = ctx.ParamNumberOrNull("min"), max = ctx.ParamNumberOrNull("max");
            if (min is null && max is null) throw new CheckParamException("give 'min', 'max' or both.");
            bool minEx = ctx.ParamBool("minExclusive", false), maxEx = ctx.ParamBool("maxExclusive", false);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                if (!(r.Get(field) is JsonNumber n)) continue;
                double v = n.Value;
                bool aboveMin = !min.HasValue || (minEx ? v > min.Value : v >= min.Value);
                bool belowMax = !max.HasValue || (maxEx ? v < max.Value : v <= max.Value);
                if (!(aboveMin && belowMax)) continue;
                list.Add(NeutralEvaluators.Make(r, table,
                    field + " = " + n.Raw + NeutralEvaluators.Label(r) + " is within " +
                    (min.HasValue ? (minEx ? "(" : "[") + StandardEvaluators.Num(min.Value) : "(-inf") + " .. " +
                    (max.HasValue ? StandardEvaluators.Num(max.Value) + (maxEx ? ")" : "]") : "inf)"),
                    field, n, new JsonObject().Add("min", StandardEvaluators.NumOrNull(min)).Add("max", StandardEvaluators.NumOrNull(max))));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>distinct_count_min</c> { table, where?, field, min }: one finding when the records passing where hold fewer than
    /// min distinct values of field (absent values are not counted). No finding when no record passes.
    /// </summary>
    public sealed class DistinctCountMin : ICheckEvaluator
    {
        public string Name => "distinct_count_min";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            double min = ctx.ParamNumber("min");
            var distinct = new List<JsonValue>();
            int records = 0;
            foreach (var r in NeutralEvaluators.Records(ctx, out _))
            {
                records++;
                var v = r.Get(field);
                if (v is null) continue;
                if (!distinct.Any(x => JsonValue.DeepEquals(x, v))) distinct.Add(v.DeepClone());
            }
            string table = ctx.ParamString("table");
            if (records == 0 || distinct.Count >= min) return Array.Empty<Finding>();
            return new[]
            {
                new Finding(distinct.Count + " distinct " + field + " value(s) in " + table + ", fewer than " + StandardEvaluators.Num(min) +
                    (distinct.Count > 0 ? ": " + string.Join(", ", distinct.Select(NeutralEvaluators.Text)) : ""),
                    null, table,
                    new JsonObject().Add("field", field).Add("value", new JsonArray(distinct)).Add("count", distinct.Count).Add("min", min)),
            };
        }
    }

    /// <summary>
    /// <c>scale_in_standard</c> { table, where?, field, tolerance? = 0.001, skipMissing? = false }: each scale ratio (model
    /// length per paper length) must be within the relative tolerance of one of the standard's scales. Absent, non-numeric
    /// or non-positive values are findings too, unless skipMissing is true, which skips records without the field (views
    /// that have no scale).
    /// </summary>
    public sealed class ScaleInStandard : ICheckEvaluator
    {
        public string Name => "scale_in_standard";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string field = ctx.ParamString("field");
            double tol = ctx.ParamNumberOrNull("tolerance") ?? 0.001;
            if (!(tol >= 0)) throw new CheckParamException("param 'tolerance' must not be negative.");
            if (std.Scales.Count == 0) throw new CheckParamException("the standard lists no scales.");
            bool skipMissing = ctx.ParamBool("skipMissing", false);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                if (skipMissing && (v is null || v.IsNull)) continue;
                if (!(v is JsonNumber n) || !(n.Value > 0))
                {
                    list.Add(NeutralEvaluators.Make(r, table, field + " is " + NeutralEvaluators.Text(v) + NeutralEvaluators.Label(r) + ", not a scale ratio", field, v,
                        new JsonObject().Add("nearest", JsonValue.Null).Add("nearestRatio", JsonValue.Null)));
                    continue;
                }
                if (std.Scales.Any(s => Math.Abs(n.Value - s.Ratio) <= tol * s.Ratio)) continue;
                var nearest = std.Scales.OrderBy(s => Math.Abs(Math.Log(n.Value / s.Ratio))).First();
                list.Add(NeutralEvaluators.Make(r, table,
                    "Scale ratio " + StandardEvaluators.Num(n.Value) + NeutralEvaluators.Label(r) + " is not a standard scale; nearest is " + nearest.Name,
                    field, v, new JsonObject().Add("nearest", nearest.Name).Add("nearestRatio", nearest.Ratio)));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>units_match_standard</c> { table, where?, field }: the unit symbol in field must equal the standard's units.length;
    /// when the standard has none, only "unitless" is a finding. Data carries "expected" for the fix template.
    /// </summary>
    public sealed class UnitsMatchStandard : ICheckEvaluator
    {
        public string Name => "units_match_standard";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string field = ctx.ParamString("field");
            string? expected = std.LengthUnit.HasValue ? Length.Symbol(std.LengthUnit.Value) : null;
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                string? text = (v as JsonString)?.Value;
                bool bad;
                if (expected != null)
                    bad = !(text != null && (text == expected || (Length.TryParseUnit(text, out var u) && u == std.LengthUnit!.Value)));
                else
                    bad = text != null && string.Equals(text, "unitless", StringComparison.OrdinalIgnoreCase);
                if (!bad) continue;
                list.Add(NeutralEvaluators.Make(r, table,
                    field + " is " + NeutralEvaluators.Text(v) + NeutralEvaluators.Label(r) +
                    (expected != null ? "; the standard uses " + expected : "; the drawing has no units"),
                    field, v, new JsonObject().Add("expected", expected is null ? (JsonValue)JsonValue.Null : expected)));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>dim_precision_min</c> { table, where?, field? = "stepMm", systemField? = "unitSystem", imperialMinIn, metricMinMm,
    /// tolerance? = 1e-6 }: the displayed step must not be finer than the minimum for the record's unit system.
    /// A record whose step or system is absent or unusable is a finding ("cannot judge precision").
    /// </summary>
    public sealed class DimPrecisionMin : ICheckEvaluator
    {
        public string Name => "dim_precision_min";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamStringOrNull("field") ?? "stepMm";
            string systemField = ctx.ParamStringOrNull("systemField") ?? "unitSystem";
            double imperialIn = ctx.ParamNumber("imperialMinIn");
            double metricMm = ctx.ParamNumber("metricMinMm");
            double tol = ctx.ParamNumberOrNull("tolerance") ?? 1e-6;
            if (!(imperialIn > 0) || !(metricMm > 0)) throw new CheckParamException("'imperialMinIn' and 'metricMinMm' must be positive.");
            if (!(tol >= 0) || tol >= 1) throw new CheckParamException("param 'tolerance' must be at least 0 and below 1.");
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                string? system = r.GetString(systemField);
                double? minMm = system == "imperial" ? imperialIn * 25.4 : system == "metric" ? metricMm : (double?)null;
                var extra = new JsonObject().Add("minMm", StandardEvaluators.NumOrNull(minMm)).Add("system", system is null ? (JsonValue)JsonValue.Null : system);
                if (!(v is JsonNumber n) || !(n.Value > 0) || minMm is null)
                {
                    list.Add(NeutralEvaluators.Make(r, table, "Cannot judge precision" + NeutralEvaluators.Label(r) + ": " + field + " is " + NeutralEvaluators.Text(v) +
                        ", " + systemField + " is " + (system ?? "(missing)"), field, v, extra));
                    continue;
                }
                if (n.Value < minMm.Value * (1 - tol))
                    list.Add(NeutralEvaluators.Make(r, table,
                        "Displays steps of " + StandardEvaluators.Num(n.Value) + " mm" + NeutralEvaluators.Label(r) + ", finer than the " + system + " minimum of " + StandardEvaluators.Num(minMm.Value) + " mm",
                        field, v, extra));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>matches_standard_entry</c> { table, where?, list, nameField? = "name", properties, ignoreCase? = true }: records
    /// named in the standard's layers, textStyles or dimStyles must have the property values that entry gives.
    /// Properties the entry does not give are not compared; records not named in the list are skipped.
    /// </summary>
    public sealed class MatchesStandardEntry : ICheckEvaluator
    {
        public string Name => "matches_standard_entry";

        private static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["layers"] = new[] { "color", "linetype", "lineweight", "plot", "transparency" },
            ["textStyles"] = new[] { "font", "height", "widthFactor", "obliqueAngle", "annotative" },
            ["dimStyles"] = new[] { "annotative" },
        };

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string listName = ctx.ParamString("list");
            if (!Allowed.TryGetValue(listName, out var allowed)) throw new CheckParamException("param 'list' must be layers, textStyles or dimStyles.");
            string nameField = ctx.ParamStringOrNull("nameField") ?? "name";
            bool ignoreCase = ctx.ParamBool("ignoreCase", true);
            if (!(ctx.Params["properties"] is JsonObject props) || props.Count == 0)
                throw new CheckParamException("param 'properties' must be a non-empty object of record field to standard property.");
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var kv in props)
            {
                if (!(kv.Value is JsonString sp) || sp.Value.Length == 0) throw new CheckParamException("properties." + kv.Key + " must name a standard property.");
                bool ok = allowed.Contains(sp.Value) || (listName == "dimStyles" && sp.Value.StartsWith("settings.", StringComparison.Ordinal) && sp.Value.Length > 9);
                if (!ok) throw new CheckParamException("'" + sp.Value + "' is not a property of " + listName + " (" + string.Join(", ", allowed) + (listName == "dimStyles" ? ", settings.<NAME>" : "") + ").");
                pairs.Add(new KeyValuePair<string, string>(kv.Key, sp.Value));
            }

            var entries = Entries(std, listName);
            var cmp = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                if (!(r.Get(nameField) is JsonString name)) continue;
                var entry = entries.FirstOrDefault(e => cmp.Equals(e.Key, name.Value));
                if (entry.Value is null) continue;
                var mismatches = new JsonArray();
                foreach (var pair in pairs)
                {
                    var expected = Property(entry.Value, pair.Value);
                    if (expected is null) continue;
                    var actual = r.Get(pair.Key);
                    if (Same(actual, expected, pair.Value == "color")) continue;
                    mismatches.Add(new JsonObject().Add("field", pair.Key).Add("value", actual?.DeepClone() ?? JsonValue.Null).Add("expected", expected.DeepClone()));
                }
                if (mismatches.Count == 0) continue;
                var first = mismatches[0].AsObject();
                list.Add(new Finding(
                    "'" + name.Value + "'" + NeutralEvaluators.Label(r) + " differs from the standard's " + listName + " entry: " +
                    string.Join("; ", mismatches.Select(m => m.AsObject().GetStringOrNull("field") + " is " + NeutralEvaluators.Text(m.AsObject()["value"]) +
                        ", expected " + NeutralEvaluators.Text(m.AsObject()["expected"]))),
                    r.Id, table,
                    new JsonObject().Add("field", first["field"]!.DeepClone()).Add("value", first["value"]!.DeepClone())
                        .Add("expected", first["expected"]!.DeepClone()).Add("mismatches", mismatches).Add("name", name.Value)));
            }
            return list;
        }

        /// <summary>Entry name to the entry as written in the standards file, so only the properties it gives are compared.</summary>
        private static List<KeyValuePair<string, JsonObject>> Entries(ProjectStandard std, string listName)
        {
            var result = new List<KeyValuePair<string, JsonObject>>();
            if (std.Raw[listName] is JsonArray raw)
            {
                foreach (var item in raw)
                    if (item is JsonObject o && o.GetStringOrNull("name") is string n) result.Add(new KeyValuePair<string, JsonObject>(n, o));
                return result;
            }
            // A standard built without its source JSON: describe every loaded value.
            if (listName == "layers")
                foreach (var l in std.Layers)
                    result.Add(new KeyValuePair<string, JsonObject>(l.Name, new JsonObject().Add("color", l.Color.ToString()).Add("linetype", l.Linetype)
                        .Add("lineweight", l.Lineweight.HasValue ? (JsonValue)l.Lineweight.Value : "default").Add("plot", l.Plot).Add("transparency", l.Transparency)));
            else if (listName == "textStyles")
                foreach (var t in std.TextStyles)
                    result.Add(new KeyValuePair<string, JsonObject>(t.Name, new JsonObject().Add("font", t.Font).Add("height", t.Height).Add("widthFactor", t.WidthFactor)
                        .Add("obliqueAngle", t.ObliqueAngle).Add("annotative", t.Annotative)));
            else
                foreach (var d in std.DimStyles)
                    result.Add(new KeyValuePair<string, JsonObject>(d.Name, new JsonObject().Add("annotative", d.Annotative).Add("settings", d.Settings)));
            return result;
        }

        private static JsonValue? Property(JsonObject entry, string property)
        {
            if (property.StartsWith("settings.", StringComparison.Ordinal))
                return entry["settings"] is JsonObject s ? s[property.Substring(9)] : null;
            var v = entry[property];
            return v is null || v.IsNull ? null : v;
        }

        /// <summary>
        /// The comparison matches_standard_entry uses: numbers within 1e-6 relative, strings without case, colours normalised
        /// when <paramref name="colour"/> (ACI 7 equals "7"; "#RRGGBB" equals "r,g,b"); an absent value differs.
        /// </summary>
        public static bool Same(JsonValue? actual, JsonValue expected, bool colour)
        {
            if (actual is null || actual.IsNull) return false;
            if (colour)
            {
                string? a = ColourKey(actual), e = ColourKey(expected);
                if (a != null && e != null) return a == e;
            }
            if (TryNumber(actual, out double x) && TryNumber(expected, out double y))
                return x == y || Math.Abs(x - y) <= 1e-6 * Math.Max(Math.Abs(x), Math.Abs(y));
            if (actual is JsonBool ab && expected is JsonBool eb) return ab.Value == eb.Value;
            if (actual is JsonString sa && expected is JsonString se) return string.Equals(sa.Value.Trim(), se.Value.Trim(), StringComparison.OrdinalIgnoreCase);
            return JsonValue.DeepEquals(actual, expected);
        }

        private static bool TryNumber(JsonValue v, out double d)
        {
            d = 0;
            if (v is JsonNumber n) { d = n.Value; return true; }
            return false;
        }

        private static readonly Regex Digits = new Regex("^\\s*[0-9]{1,3}\\s*$", RegexOptions.CultureInvariant);

        /// <summary>"aci:7" or "rgb:r,g,b"; null when the value is not a colour (bylayer, byblock, text).</summary>
        public static string? ColourKey(JsonValue v)
        {
            if (v is JsonString s && Digits.IsMatch(s.Value)) v = int.Parse(s.Value.Trim(), CultureInfo.InvariantCulture);
            if (!ColorSpec.TryParse(v, out var c) || c is null) return null;
            return c.Aci.HasValue ? "aci:" + c.Aci.Value.ToString(CultureInfo.InvariantCulture) : "rgb:" + c.R + "," + c.G + "," + c.B;
        }
    }

    /// <summary>
    /// <c>group_count_over_threshold</c> { table, where?, key, labelField?, max }: groups records by the key's value and
    /// reports each group larger than max, largest first. Records without the key form no group.
    /// </summary>
    public sealed class GroupCountOverThreshold : ICheckEvaluator
    {
        public string Name => "group_count_over_threshold";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string key = ctx.ParamString("key");
            string? labelField = ctx.ParamStringOrNull("labelField");
            double max = ctx.ParamNumber("max");
            var groups = new List<(string Key, JsonValue Value, FactRecord First, int Count)>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in NeutralEvaluators.Records(ctx, out _))
            {
                var v = r.Get(key);
                if (v is null || v.IsNull) continue;
                string k = v.ToJson();
                if (index.TryGetValue(k, out int i)) groups[i] = (groups[i].Key, groups[i].Value, groups[i].First, groups[i].Count + 1);
                else { index[k] = groups.Count; groups.Add((k, v, r, 1)); }
            }
            string table = ctx.ParamString("table");
            var list = new List<Finding>();
            // OrderByDescending is stable, so equal groups keep their first-seen order.
            foreach (var g in groups.Where(g => g.Count > max).OrderByDescending(g => g.Count))
            {
                var labelValue = labelField != null ? g.First.Get(labelField) : null;
                string label = labelValue is null || labelValue.IsNull ? NeutralEvaluators.Text(g.Value) : NeutralEvaluators.Text(labelValue);
                list.Add(new Finding(g.Count + " x " + label, null, table,
                    new JsonObject().Add("field", key).Add("value", g.Value.DeepClone()).Add("count", g.Count).Add("max", max).Add("label", label)));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>manual_review</c> { instructions? }: reads no facts. Always one finding, "Review by eye: &lt;title&gt;", whose data has
    /// review = true. <see cref="CheckRunner"/> marks the result <see cref="CheckResult.IsManualReview"/>.
    /// </summary>
    public sealed class ManualReview : ICheckEvaluator
    {
        public const string EvaluatorName = "manual_review";

        public string Name => EvaluatorName;

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string? instructions = ctx.ParamStringOrNull("instructions") ?? ctx.Check.Fix?.Instructions;
            return new[]
            {
                new Finding("Review by eye: " + ctx.Check.Title, null, null,
                    new JsonObject().Add("field", "manual").Add("value", "review").Add("review", true)
                        .Add("instructions", instructions is null ? (JsonValue)JsonValue.Null : instructions)),
            };
        }
    }
}
