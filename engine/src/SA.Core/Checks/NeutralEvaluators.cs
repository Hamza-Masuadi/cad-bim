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
    /// Evaluators that need only <see cref="Facts"/> (and sometimes the standard). Every one accepts an optional
    /// <c>where</c> param: an object of field = value pairs a record must all match to be considered.
    /// </summary>
    public static class NeutralEvaluators
    {
        public static IEnumerable<ICheckEvaluator> All()
        {
            yield return new NamesMatchScheme();
            yield return new FieldMatchesPattern();
            yield return new DimOverrideValue();
            yield return new CountOverThreshold();
            yield return new FieldEquals();
            yield return new FieldMissing();
            yield return new DuplicatesByKey();
            yield return new ValueInStandardList();
            yield return new FieldRange();
            yield return new TextHeightStandard();
        }

        internal static IEnumerable<FactRecord> Records(CheckContext ctx, out string tableName)
        {
            tableName = ctx.ParamString("table");
            var table = ctx.RequireTable(tableName);
            IEnumerable<FactRecord> records = table.Records;
            var w = Filter(ctx, "where");
            if (w != null) records = records.Where(r => w.All(kv => JsonValue.DeepEquals(r.Get(kv.Key), kv.Value)));
            // whereNot: every listed field must differ from its value; an absent field never equals, so it passes.
            var n = Filter(ctx, "whereNot");
            if (n != null) records = records.Where(r => n.All(kv => !JsonValue.DeepEquals(r.Get(kv.Key), kv.Value)));
            return records;
        }

        private static JsonObject? Filter(CheckContext ctx, string name)
        {
            var v = ctx.Params[name];
            if (v is null || v.IsNull) return null;
            if (!(v is JsonObject o)) throw new CheckParamException("Check '" + ctx.Check.Id + "': param '" + name + "' must be an object.");
            return o;
        }

        internal static string Text(JsonValue? v) => v is null ? "(missing)" : v is JsonString s ? s.Value : v.ToJson();

        internal static string Label(FactRecord r) => r.Id != null ? " [" + r.Id + "]" : "";

        internal static Finding Make(FactRecord r, string table, string message, string field, JsonValue? value, JsonObject? extra = null)
        {
            var data = new JsonObject().Add("field", field).Add("value", value?.DeepClone() ?? JsonValue.Null);
            if (extra != null) foreach (var kv in extra) data.Set(kv.Key, kv.Value);
            return new Finding(message, r.Id, table, data);
        }
    }

    /// <summary>
    /// <c>names_match_scheme</c> { table, field, scheme: "layer" | "sheet" | "file" | "view" }: every value must parse under the
    /// standard's layer scheme, sheet numbering, file naming or view naming rule.
    /// </summary>
    public sealed class NamesMatchScheme : ICheckEvaluator
    {
        public string Name => "names_match_scheme";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string field = ctx.ParamString("field");
            string scheme = ctx.ParamString("scheme");
            Func<string, NameResult> parse;
            switch (scheme)
            {
                case "layer":
                    if (std.LayerScheme is null) throw new CheckParamException("The standard has no layer naming scheme.");
                    parse = std.LayerScheme.Parse; break;
                case "sheet":
                    if (std.SheetNumbering is null) throw new CheckParamException("The standard has no sheet numbering.");
                    parse = std.SheetNumbering.Parse; break;
                case "file":
                    if (std.FileNaming is null) throw new CheckParamException("The standard has no file naming rule.");
                    parse = std.FileNaming.Parse; break;
                case "view":
                    if (std.ViewNaming is null) throw new CheckParamException("The standard has no view naming rule.");
                    parse = std.ViewNaming.Parse; break;
                default: throw new CheckParamException("param 'scheme' must be layer, sheet, file or view.");
            }
            var ignore = new HashSet<string>(ctx.ParamStringList("ignore"), StringComparer.OrdinalIgnoreCase);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                if (!(v is JsonString s))
                {
                    list.Add(NeutralEvaluators.Make(r, table, "No " + field + " to check" + NeutralEvaluators.Label(r), field, v));
                    continue;
                }
                if (ignore.Contains(s.Value)) continue;
                var res = parse(s.Value);
                if (!res.Valid)
                    list.Add(NeutralEvaluators.Make(r, table, res.Explain(), field, v,
                        new JsonObject().Add("problems", JsonArray.FromStrings(res.Problems.Select(p => p.ToString())))));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>field_matches_pattern</c> { table, field, pattern | standardPattern, ignoreCase?, negate? }: values must match the
    /// regular expression (in full); with negate, values must not match it.
    /// </summary>
    public sealed class FieldMatchesPattern : ICheckEvaluator
    {
        public string Name => "field_matches_pattern";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            bool negate = ctx.ParamBool("negate", false);
            Regex rx;
            string? p = ctx.ParamStringOrNull("pattern");
            string? sp = ctx.ParamStringOrNull("standardPattern");
            if ((p is null) == (sp is null)) throw new CheckParamException("give exactly one of 'pattern' or 'standardPattern'.");
            var options = RegexOptions.CultureInvariant | (ctx.ParamBool("ignoreCase", false) ? RegexOptions.IgnoreCase : RegexOptions.None);
            if (p != null)
            {
                try { rx = new Regex("^(?:" + p + ")$", options); }
                catch (ArgumentException e) { throw new CheckParamException("invalid pattern: " + e.Message); }
            }
            else
            {
                var std = ctx.RequireStandard();
                if (!std.Patterns.TryGetValue(sp!, out var named)) throw new CheckParamException("the standard has no pattern '" + sp + "'.");
                rx = (options & RegexOptions.IgnoreCase) != 0 ? new Regex(named.ToString(), options) : named;
            }
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                string text = v is JsonString s ? s.Value : NeutralEvaluators.Text(v);
                bool match = v is JsonString && rx.IsMatch(text);
                if (match == negate)
                    list.Add(NeutralEvaluators.Make(r, table, "'" + text + "'" + NeutralEvaluators.Label(r) + (negate ? " matches a forbidden pattern" : " does not match the required pattern"), field, v));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>dim_override_value</c> { table, match: "differs" | "equals" }: dimension text overrides (field textOverride) that
    /// do not keep the measured value (empty, contains &lt;&gt;, or EQ are accepted). "differs": a finding when no number in
    /// the text equals the shown value (measurement x dimLfac, or its degrees for angles); "equals": a finding when one does.
    /// shortcut: feet-and-inch text such as 8'-4" is not parsed, so it counts as differing; parse it if imperial projects need it.
    /// </summary>
    public sealed class DimOverrideValue : ICheckEvaluator
    {
        public string Name => "dim_override_value";

        private static readonly Regex Accepted = new Regex(@"^(?:|.*<>.*|EQ\.?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex Codes = new Regex(@"\\U\+[0-9A-Fa-f]{4}|\\[A-OQ-Za-oq-z][^;\\{}]*;|\\P", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"\d+(?:\.(\d+))?", RegexOptions.CultureInvariant);

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string match = ctx.ParamString("match");
            if (match != "differs" && match != "equals") throw new CheckParamException("param 'match' must be differs or equals.");
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get("textOverride");
                string text = v is JsonString s ? s.Value : "";
                if (Accepted.IsMatch(text)) continue;
                bool equal = Matches(text, r.Get("measurement") is JsonNumber m ? m.Value : (double?)null, r.Get("dimLfac") is JsonNumber f ? f.Value : 1);
                if (equal != (match == "equals")) continue;
                list.Add(NeutralEvaluators.Make(r, table, equal
                    ? "'" + text + "'" + NeutralEvaluators.Label(r) + " is typed over the measured value; it is right now but will not update if the geometry changes"
                    : "'" + text + "'" + NeutralEvaluators.Label(r) + " replaces the measured value with a different one", "textOverride", v,
                    new JsonObject().Add("measurement", r.Get("measurement")?.DeepClone() ?? JsonValue.Null)));
            }
            return list;
        }

        internal static bool Matches(string text, double? measurement, double lfac)
        {
            if (measurement is null) return false;
            double shown = measurement.Value * lfac;
            var targets = new[] { shown, measurement.Value * 180 / Math.PI };
            foreach (Match n in Number.Matches(Codes.Replace(text, " ")))
            {
                double x = double.Parse(n.Value, CultureInfo.InvariantCulture);
                double tol = 0.5 * Math.Pow(10, -n.Groups[1].Value.Length);
                if (targets.Any(t => Math.Abs(x - Math.Abs(t)) <= tol)) return true;
            }
            return false;
        }
    }

    /// <summary><c>count_over_threshold</c> { table, max }: one finding when more than max records match.</summary>
    public sealed class CountOverThreshold : ICheckEvaluator
    {
        public string Name => "count_over_threshold";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            double max = ctx.ParamNumber("max");
            int count = NeutralEvaluators.Records(ctx, out string table).Count();
            if (count <= max) return Array.Empty<Finding>();
            return new[]
            {
                new Finding(count + " " + table + " records, more than the limit of " + max.ToString(CultureInfo.InvariantCulture), null, table,
                    new JsonObject().Add("count", count).Add("max", max)),
            };
        }
    }

    /// <summary>
    /// <c>field_equals</c> { table, field, equals | in, negate? }: a finding for every record whose field equals the value
    /// (or one of "in"). With negate, a finding for every record whose field differs. A missing field never equals.
    /// </summary>
    public sealed class FieldEquals : ICheckEvaluator
    {
        public string Name => "field_equals";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            bool negate = ctx.ParamBool("negate", false);
            var eq = ctx.Params["equals"];
            var inList = ctx.Params["in"] as JsonArray;
            if ((eq is null) == (inList is null)) throw new CheckParamException("give exactly one of 'equals' or 'in'.");
            var values = inList != null ? inList.ToList() : new List<JsonValue> { eq! };
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                bool equal = v != null && values.Any(x => JsonValue.DeepEquals(v, x));
                if (equal != negate)
                    list.Add(NeutralEvaluators.Make(r, table, field + " is " + NeutralEvaluators.Text(v) + NeutralEvaluators.Label(r), field, v));
            }
            return list;
        }
    }

    /// <summary><c>field_missing</c> { table, field, emptyIsMissing? = true }: records where the field is absent, null or (by default) an empty string.</summary>
    public sealed class FieldMissing : ICheckEvaluator
    {
        public string Name => "field_missing";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            bool emptyIsMissing = ctx.ParamBool("emptyIsMissing", true);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                bool missing = v is null || v.IsNull || (emptyIsMissing && v is JsonString s && s.Value.Trim().Length == 0);
                if (missing) list.Add(NeutralEvaluators.Make(r, table, field + " is missing" + NeutralEvaluators.Label(r), field, v));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>duplicates_by_key</c> { table, keys: [fields], precision?, ignoreCase? }: records sharing all key values. Every record
    /// after the first of a group is a finding naming the first. Numbers are rounded to "precision" (e.g. 0.001) first.
    /// </summary>
    public sealed class DuplicatesByKey : ICheckEvaluator
    {
        public string Name => "duplicates_by_key";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var keys = ctx.ParamStringList("keys");
            if (keys.Count == 0) throw new CheckParamException("param 'keys' must name at least one field.");
            double? precision = ctx.ParamNumberOrNull("precision");
            if (precision.HasValue && !(precision > 0)) throw new CheckParamException("param 'precision' must be positive.");
            bool ignoreCase = ctx.ParamBool("ignoreCase", false);
            var first = new Dictionary<string, FactRecord>(StringComparer.Ordinal);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var parts = new List<string>();
                bool complete = true;
                foreach (var k in keys)
                {
                    var v = r.Get(k);
                    if (v is null || v.IsNull) { complete = false; break; }
                    parts.Add(KeyText(v, precision, ignoreCase));
                }
                if (!complete) continue;
                string key = string.Join("\u0001", parts);
                if (first.TryGetValue(key, out var orig))
                {
                    var data = new JsonObject().Add("keys", JsonArray.FromStrings(keys)).Add("duplicateOf", orig.Id is null ? (JsonValue)JsonValue.Null : orig.Id);
                    foreach (var k in keys) data.Set(k, r.Get(k)!.DeepClone());
                    list.Add(new Finding("Duplicate of " + (orig.Id ?? "an earlier record") + " by " + string.Join(", ", keys) + NeutralEvaluators.Label(r), r.Id, table, data));
                }
                else first[key] = r;
            }
            return list;
        }

        private static string KeyText(JsonValue v, double? precision, bool ignoreCase)
        {
            switch (v)
            {
                case JsonNumber n when precision.HasValue:
                    double q = Math.Round(n.Value / precision.Value);
                    if (q == 0) q = 0; // fold -0
                    return "n" + q.ToString("R", CultureInfo.InvariantCulture);
                case JsonNumber n: return "n" + n.Value.ToString("R", CultureInfo.InvariantCulture);
                case JsonString s: return "s" + (ignoreCase ? s.Value.ToUpperInvariant() : s.Value);
                case JsonArray a: return "a[" + string.Join(",", a.Select(x => KeyText(x, precision, ignoreCase))) + "]";
                default: return v.ToJson();
            }
        }
    }

    /// <summary>
    /// <c>value_in_standard_list</c> { table, field, list, ignoreCase? = true }: values must be names in a list of the
    /// standard: "layers", "textStyles", "dimStyles", "scales", "statusCodes", or "codeList:&lt;name&gt;".
    /// </summary>
    public sealed class ValueInStandardList : ICheckEvaluator
    {
        public string Name => "value_in_standard_list";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string field = ctx.ParamString("field");
            string listName = ctx.ParamString("list");
            bool ignoreCase = ctx.ParamBool("ignoreCase", true);
            IEnumerable<string> names;
            if (listName == "layers") names = std.Layers.Select(l => l.Name);
            else if (listName == "textStyles") names = std.TextStyles.Select(t => t.Name);
            else if (listName == "dimStyles") names = std.DimStyles.Select(d => d.Name);
            else if (listName == "scales") names = std.Scales.Select(s => s.Name);
            else if (listName == "statusCodes") names = std.StatusCodes.Select(s => s.Code);
            else if (listName.StartsWith("codeList:", StringComparison.Ordinal))
            {
                string n = listName.Substring("codeList:".Length);
                if (!std.CodeLists.TryGetValue(n, out var cl)) throw new CheckParamException("the standard has no code list '" + n + "'.");
                names = cl.Select(c => c.Code);
            }
            else throw new CheckParamException("param 'list' must be layers, textStyles, dimStyles, scales, statusCodes or codeList:<name>.");
            var set = new HashSet<string>(names, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var ignore = new HashSet<string>(ctx.ParamStringList("ignore"), StringComparer.OrdinalIgnoreCase);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                if (v is JsonString s && (set.Contains(s.Value) || ignore.Contains(s.Value))) continue;
                list.Add(NeutralEvaluators.Make(r, table, "'" + NeutralEvaluators.Text(v) + "'" + NeutralEvaluators.Label(r) + " is not in the standard's " + listName, field, v));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>field_range</c> { table, field, min?, max?, skipMissing? = false }: numeric values outside [min, max]; non-numeric
    /// values are findings too. With skipMissing, records where the field is absent are skipped (for fields that apply to
    /// some records only); a value that is present but not a number is still a finding.
    /// </summary>
    public sealed class FieldRange : ICheckEvaluator
    {
        public string Name => "field_range";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            string field = ctx.ParamString("field");
            double? min = ctx.ParamNumberOrNull("min"), max = ctx.ParamNumberOrNull("max");
            if (min is null && max is null) throw new CheckParamException("give 'min', 'max' or both.");
            bool skipMissing = ctx.ParamBool("skipMissing", false);
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                var v = r.Get(field);
                if (v is null && skipMissing) continue;
                if (!(v is JsonNumber n))
                {
                    list.Add(NeutralEvaluators.Make(r, table, field + " is not a number" + NeutralEvaluators.Label(r), field, v));
                    continue;
                }
                if ((min.HasValue && n.Value < min.Value) || (max.HasValue && n.Value > max.Value))
                    list.Add(NeutralEvaluators.Make(r, table, field + " = " + n.Raw + NeutralEvaluators.Label(r) + " is outside " +
                        (min.HasValue ? min.Value.ToString(CultureInfo.InvariantCulture) : "-inf") + " .. " + (max.HasValue ? max.Value.ToString(CultureInfo.InvariantCulture) : "inf"), field, v));
            }
            return list;
        }
    }

    /// <summary>
    /// <c>text_height_standard</c> { table, heightField = "height", scaleField = "scale", unit?, tolerance = 0.02 }:
    /// each text's plotted height (model height / scale ratio) must equal one of the standard's text heights within the
    /// relative tolerance. The model unit is param "unit" or facts meta "units.length". A record with no scale is a finding.
    /// </summary>
    public sealed class TextHeightStandard : ICheckEvaluator
    {
        public string Name => "text_height_standard";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            if (std.TextHeights.Count == 0) throw new CheckParamException("the standard lists no text heights.");
            string hf = ctx.ParamStringOrNull("heightField") ?? "height";
            string sf = ctx.ParamStringOrNull("scaleField") ?? "scale";
            double tol = ctx.ParamNumberOrNull("tolerance") ?? 0.02;
            string? unitText = ctx.ParamStringOrNull("unit") ?? ctx.Facts.Meta.GetStringOrNull("units.length");
            if (unitText is null) throw new FactsMissingException("The model length unit is unknown (param 'unit' or facts meta 'units.length').");
            if (!Length.TryParseUnit(unitText, out var modelUnit)) throw new CheckParamException("unknown unit '" + unitText + "'.");
            var allowedMm = std.TextHeights.Select(t => Length.Convert(t.Plotted, t.Unit, LengthUnit.Millimetre)).ToList();
            var list = new List<Finding>();
            foreach (var r in NeutralEvaluators.Records(ctx, out string table))
            {
                double? h = r.GetNumber(hf);
                double? ratio = r.GetNumber(sf);
                if (h is null || ratio is null || !(ratio > 0))
                {
                    list.Add(NeutralEvaluators.Make(r, table, "Text" + NeutralEvaluators.Label(r) + " has no height or scale to check", hf, r.Get(hf)));
                    continue;
                }
                double plottedMm = Scale.PlottedTextHeight(h.Value, modelUnit, ratio.Value, LengthUnit.Millimetre);
                if (!allowedMm.Any(a => Math.Abs(plottedMm - a) <= tol * a))
                {
                    var nearest = allowedMm.OrderBy(a => Math.Abs(a - plottedMm)).First();
                    list.Add(NeutralEvaluators.Make(r, table,
                        "Text" + NeutralEvaluators.Label(r) + " plots at " + plottedMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm; nearest standard height is " +
                        nearest.ToString("0.###", CultureInfo.InvariantCulture) + " mm", hf, r.Get(hf),
                        new JsonObject().Add("plottedMm", plottedMm).Add("nearestMm", nearest)
                            .Add("expectedModelHeight", Scale.ModelTextHeight(nearest, LengthUnit.Millimetre, ratio.Value, modelUnit))));
                }
            }
            return list;
        }
    }
}
