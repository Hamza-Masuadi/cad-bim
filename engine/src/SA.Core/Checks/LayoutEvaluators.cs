using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;
using SA.Core.Standards;

namespace SA.Core.Checks
{
    /// <summary>Evaluators for how a drawing reads on the sheet: overlapping annotation, scales mixed on one sheet.</summary>
    public static class LayoutEvaluators
    {
        /// <summary>Facts meta key: true when the facts were collected for a job's verify step, so records carry <c>byJob</c>.</summary>
        public const string JobScopeKey = "jobScope";

        public static IEnumerable<ICheckEvaluator> All()
        {
            yield return new ExtentsOverlap();
            yield return new SheetScalesUniform();
        }
    }

    /// <summary>
    /// <c>extents_overlap</c> { table, where?, whereNot?, groupBy? = [], box? = ["minX","minY","maxX","maxY"],
    /// minOverlap? = 0.1, jobOnly? = true, labelField? }: within each group (records with equal values of every groupBy
    /// field: a view, a layout), two records whose boxes overlap by more than minOverlap of the smaller box's area are a
    /// finding, one per pair. With jobOnly and facts collected for a job (meta "jobScope" true), only pairs where at least
    /// one record has <c>byJob</c> true count, so a job is not blamed for pile-ups it did not make. Records without four
    /// numeric box fields, or with a box of no area, are skipped.
    /// </summary>
    public sealed class ExtentsOverlap : ICheckEvaluator
    {
        public const string ByJobField = "byJob";
        /// <summary>Box comparisons per check; past it the check reports what it found and one finding saying it stopped.</summary>
        public const long MaxComparisons = 50_000_000;

        public string Name => "extents_overlap";

        private struct Item
        {
            public FactRecord R;
            public double X0, Y0, X1, Y1;
            public bool ByJob;
            public double Area => (X1 - X0) * (Y1 - Y0);
        }

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var groupBy = ctx.ParamStringList("groupBy");
            var box = ctx.ParamStringList("box");
            if (box.Count == 0) box = new List<string> { "minX", "minY", "maxX", "maxY" };
            if (box.Count != 4) throw new CheckParamException("Check '" + ctx.Check.Id + "': param 'box' must name four fields (min x, min y, max x, max y).");
            double minOverlap = ctx.ParamNumberOrNull("minOverlap") ?? 0.1;
            if (!(minOverlap >= 0 && minOverlap <= 1)) throw new CheckParamException("Check '" + ctx.Check.Id + "': param 'minOverlap' is a fraction from 0 to 1.");
            bool jobOnly = ctx.ParamBool("jobOnly", true) && ctx.Facts.Meta[LayoutEvaluators.JobScopeKey] is JsonBool js && js.Value;
            string? labelField = ctx.ParamStringOrNull("labelField");

            var groups = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            string table = "";
            foreach (var r in NeutralEvaluators.Records(ctx, out table))
            {
                if (!(r.Get(box[0]) is JsonNumber a && r.Get(box[1]) is JsonNumber b && r.Get(box[2]) is JsonNumber c && r.Get(box[3]) is JsonNumber d)) continue;
                var it = new Item { R = r, X0 = Math.Min(a.Value, c.Value), Y0 = Math.Min(b.Value, d.Value), X1 = Math.Max(a.Value, c.Value), Y1 = Math.Max(b.Value, d.Value) };
                if (!(it.Area > 0)) continue;
                it.ByJob = r.Get(ByJobField) is JsonBool bj && bj.Value;
                string key = string.Join("\u0001", groupBy.Select(f => r.Get(f)?.ToJson() ?? "?"));
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Item>();
                list.Add(it);
            }

            var findings = new List<Finding>();
            long budget = MaxComparisons;
            foreach (var g in groups.Values)
            {
                if (jobOnly && !g.Any(i => i.ByJob)) continue;
                g.Sort((p, q) => p.X0.CompareTo(q.X0));
                var active = new List<Item>();
                foreach (var it in g)
                {
                    active.RemoveAll(o => o.X1 <= it.X0);
                    foreach (var o in active)
                    {
                        if (--budget < 0)
                        {
                            findings.Add(new Finding("The overlap search stopped after " + MaxComparisons + " box comparisons; pairs past that point were not looked at.",
                                null, table, new JsonObject().Add("review", true)));
                            return findings;
                        }
                        if (jobOnly && !it.ByJob && !o.ByJob) continue;
                        double w = Math.Min(it.X1, o.X1) - Math.Max(it.X0, o.X0);
                        double h = Math.Min(it.Y1, o.Y1) - Math.Max(it.Y0, o.Y0);
                        if (!(w > 0 && h > 0)) continue;
                        double frac = w * h / Math.Min(it.Area, o.Area);
                        if (frac <= minOverlap) continue;
                        if (it.R.Id != null && it.R.Id == o.R.Id) continue;
                        findings.Add(Pair(o, it, frac, table, labelField, groupBy));
                    }
                    active.Add(it);
                }
            }
            return findings;
        }

        private static Finding Pair(Item a, Item b, double frac, string table, string? labelField, List<string> groupBy)
        {
            string Name(Item i) => (labelField != null ? NeutralEvaluators.Text(i.R.Get(labelField)) : "record") + NeutralEvaluators.Label(i.R);
            var group = new JsonObject();
            foreach (var f in groupBy) group.Set(f, a.R.Get(f)?.DeepClone() ?? JsonValue.Null);
            string where = groupBy.Count == 0 ? "" : " (" + string.Join(", ", groupBy.Select(f => f + " " + NeutralEvaluators.Text(a.R.Get(f)))) + ")";
            var extra = new JsonObject()
                .Add("otherId", b.R.Id is null ? (JsonValue)JsonValue.Null : b.R.Id)
                .Add("ids", new JsonArray(a.R.Id is null ? (JsonValue)JsonValue.Null : a.R.Id, b.R.Id is null ? (JsonValue)JsonValue.Null : b.R.Id))
                .Add("box", new JsonArray(a.X0, a.Y0, a.X1, a.Y1))
                .Add("otherBox", new JsonArray(b.X0, b.Y0, b.X1, b.Y1))
                .Add("group", group);
            return NeutralEvaluators.Make(a.R, table,
                Name(a) + " and " + Name(b) + " overlap by " + Math.Round(frac * 100) + "% of the smaller" + where,
                "overlap", Math.Round(frac, 4), extra);
        }
    }

    /// <summary>
    /// <c>sheet_scales_uniform</c> { table, where?, whereNot?, groupField, scaleField, typeField?, tolerance? = 0.001 }:
    /// when the standard's <c>scaleRules.oneScalePerSheet</c> is true, the records of one group (a sheet or layout) must
    /// share one scale. Records of a type in <c>scaleRules.exemptViewTypes</c> (read from typeField) and groups matching
    /// <c>scaleRules.exemptSheets</c> are left out, as are records without a positive scale. One finding per mixed group.
    /// A standard without the rule gives no findings.
    /// </summary>
    public sealed class SheetScalesUniform : ICheckEvaluator
    {
        public string Name => "sheet_scales_uniform";

        public IEnumerable<Finding> Evaluate(CheckContext ctx)
        {
            var std = ctx.RequireStandard();
            string groupField = ctx.ParamString("groupField");
            string scaleField = ctx.ParamString("scaleField");
            string? typeField = ctx.ParamStringOrNull("typeField");
            double tol = ctx.ParamNumberOrNull("tolerance") ?? 0.001;
            if (!(tol >= 0)) throw new CheckParamException("Check '" + ctx.Check.Id + "': param 'tolerance' must not be negative.");
            var rules = std.ScaleRules;
            var records = NeutralEvaluators.Records(ctx, out string table).ToList();
            if (rules is null || !rules.OneScalePerSheet) return new List<Finding>();

            var groups = new Dictionary<string, List<(FactRecord R, double Scale)>>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                if (!(r.Get(groupField) is JsonString g) || g.Value.Length == 0) continue;
                if (rules.ExemptSheets != null && rules.ExemptSheets.IsMatch(g.Value)) continue;
                if (typeField != null && r.Get(typeField) is JsonString t && rules.ExemptViewTypes.Contains(t.Value)) continue;
                if (!(r.Get(scaleField) is JsonNumber n) || !(n.Value > 0)) continue;
                if (!groups.TryGetValue(g.Value, out var list)) groups[g.Value] = list = new List<(FactRecord, double)>();
                list.Add((r, n.Value));
            }

            var findings = new List<Finding>();
            foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var distinct = new List<(double Scale, List<FactRecord> Records)>();
                foreach (var (r, s) in kv.Value)
                {
                    int i = distinct.FindIndex(d => Math.Abs(d.Scale - s) <= tol * Math.Max(d.Scale, s));
                    if (i < 0) distinct.Add((s, new List<FactRecord> { r }));
                    else distinct[i].Records.Add(r);
                }
                if (distinct.Count < 2) continue;
                distinct.Sort((p, q) => q.Records.Count.CompareTo(p.Records.Count));
                var scales = new JsonArray();
                foreach (var d in distinct)
                    scales.Add(new JsonObject().Add("ratio", d.Scale).Add("name", ScaleName(std, d.Scale, tol)).Add("count", d.Records.Count)
                        .Add("ids", JsonArray.FromStrings(d.Records.Where(x => x.Id != null).Select(x => x.Id!))));
                string text = string.Join(", ", distinct.Select(d => ScaleName(std, d.Scale, tol) + " (" + d.Records.Count + ")"));
                var data = new JsonObject().Add("field", scaleField).Add("value", new JsonArray(distinct.Select(d => (JsonValue)d.Scale)))
                    .Add(groupField, kv.Key).Add("scales", scales);
                findings.Add(new Finding(groupField + " " + kv.Key + " mixes scales " + text + "; the standard asks for one scale per sheet",
                    distinct.Last().Records[0].Id, table, data));
            }
            return findings;
        }

        private static string ScaleName(ProjectStandard std, double ratio, double tol)
        {
            var s = std.Scales.FirstOrDefault(x => Math.Abs(x.Ratio - ratio) <= tol * x.Ratio);
            return s != null ? s.Name : "1:" + StandardEvaluators.Num(ratio);
        }
    }
}
