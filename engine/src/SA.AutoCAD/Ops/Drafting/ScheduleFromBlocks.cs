using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// schedule.fromBlocks: reads attribute values from block references placed directly in model space (space
    /// "model") or in model space and every paper space layout ("all"). References nested inside other blocks are not
    /// included (the schema has no field for them); their number is reported. Dynamic blocks match by their base name
    /// (BlockReference.DynamicBlockTableRecord). Constant attributes are read from the definition. Missing tags give
    /// empty cells. Layers filter with * and ? wildcards. sortBy compares numbers numerically and text ordinally
    /// (case-insensitive). groupIdentical gives one row per distinct combination with a count column.
    /// The table is built like table.create; the CSV is UTF-8 with a byte order mark, RFC 4180 quoting, CRLF, written
    /// into the job's output folder. A rehearsal writes it to a temporary folder instead, reads it back, and deletes the
    /// folder, so the file in the output folder is never touched by a rolled-back run.
    /// </summary>
    internal sealed class ScheduleFromBlocks : OpHandler<ScheduleFromBlocks.SchedulePlan>
    {
        public override string Name => "schedule.fromBlocks";

        internal sealed class SchedulePlan
        {
            public List<string> Blocks = new List<string>();
            public List<string> Tags = new List<string>();
            public List<string> Headers = new List<string>();
            public bool AllSpaces;
            public List<Regex> Layers = new List<Regex>();
            public List<string> SortBy = new List<string>();
            public bool Group;
            public string CountHeader = "QTY";
            public List<List<string>> Rows = new List<List<string>>();
            public int References, Nested;
            public TableSpec? Table;
            public string? CsvName;
            public string? CsvPath;
            /// <summary>Where the CSV is actually written: CsvPath, or a temporary folder in a rehearsal.</summary>
            public string? CsvWritten;
            public string? TempDir;
            public List<string> SourceHandles = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static SchedulePlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new SchedulePlan
            {
                Blocks = args.GetStringList("blocks"),
                Tags = args.GetStringList("attributes").Select(t => t.Trim().ToUpperInvariant()).ToList(),
                AllSpaces = args.GetString("space") == "all",
                SortBy = args.GetStringList("sortBy").Select(t => t.Trim().ToUpperInvariant()).ToList(),
                Group = args.GetBool("groupIdentical"),
                CountHeader = args.GetString("countHeader"),
                CsvName = args.GetStringOrNull("csv"),
            };
            var headers = args.GetStringList("headers");
            if (headers.Count > 0 && headers.Count != p.Tags.Count)
                OpContext.Error(issues, args, "headers", "headers_count", headers.Count + " headers for " + p.Tags.Count + " attributes");
            p.Headers = headers.Count == p.Tags.Count ? headers : p.Tags.ToList();
            foreach (var s in p.SortBy)
                if (!p.Tags.Contains(s)) OpContext.Error(issues, args, "sortBy", "sort_tag_unknown", "sortBy tag '" + s + "' is not one of the attributes (" + string.Join(", ", p.Tags) + ")");
            foreach (var l in args.GetStringList("layers"))
                p.Layers.Add(new Regex("^" + Regex.Escape(l).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            for (int i = 0; i < p.Blocks.Count; i++)
            {
                string b = p.Blocks[i];
                if (!DraftDb.BlockExists(ctx, b))
                {
                    issues.Add(new Issue(JsonPath.Index(JsonPath.Key(args.Path, "blocks"), i), "block_missing", DraftDb.MissingBlock(ctx, b)));
                    continue;
                }
                var tags = BlockSupport.TagsOf(ctx, b);
                if (tags != null)
                {
                    var missing = p.Tags.Where(t => !tags.Contains(t)).ToList();
                    if (missing.Count == p.Tags.Count)
                        OpContext.Warning(issues, args, "attributes", "attributes_absent", "block '" + b + "' has none of the attributes " + string.Join(", ", p.Tags) + (tags.Count > 0 ? " (its tags: " + string.Join(", ", tags) + ")" : " (it has no attributes)"));
                    else if (missing.Count > 0)
                        OpContext.Warning(issues, args, "attributes", "attributes_partial", "block '" + b + "' lacks " + string.Join(", ", missing) + "; those cells stay empty");
                }
            }

            if (p.CsvName != null)
            {
                string n = p.CsvName.Trim();
                if (!n.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) n += ".csv";
                if (!DraftDb.IsPlainFileName(n)) OpContext.Error(issues, args, "csv", "csv_name_invalid", "'" + p.CsvName + "' must be a plain file name (no folders); it is written into the job's output folder");
                p.CsvName = n;
                p.CsvPath = Path.Combine(DraftDb.OutputDir(ctx), n);
            }

            var table = args.GetObject("table");
            if (table != null)
            {
                var t = new TableSpec { Position = table.GetPoint2("position"), Title = table.GetStringOrNull("title") };
                TableBuilder.Plan(ctx, table, "style", t, issues);
                t.Layer = DraftDb.Layer(ctx, table, issues, "layer", "schedules");
                p.Table = t;
            }

            if (!ctx.IsValidating || p.Blocks.All(b => Db.Read(ctx, tr => !DraftDb.BlockId(tr, ctx.Database, b).IsNull)))
            {
                Collect(ctx, p);
                if (p.References == 0)
                    OpContext.Warning(issues, args, "blocks", "schedule_empty", "no references of " + string.Join(", ", p.Blocks) + " were found" + (p.AllSpaces ? "" : " in model space") +
                        (p.Nested > 0 ? " (" + p.Nested + " are nested inside other blocks and are not scheduled)" : ""));
            }
            return p;
        }

        private static void Collect(OpContext ctx, SchedulePlan p)
        {
            Db.Read(ctx, tr =>
            {
                var db = ctx.Database;
                var spaces = DraftDb.LayoutBlocks(tr, db, p.AllSpaces);
                var records = new List<string[]>();
                foreach (var b in p.Blocks)
                {
                    var id = DraftDb.BlockId(tr, db, b);
                    if (id.IsNull) continue;
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    var refs = BlockSupport.References(tr, btr, out int nested);
                    foreach (var rid in refs)
                    {
                        var br = (BlockReference)tr.GetObject(rid, OpenMode.ForRead);
                        if (!spaces.Contains(br.OwnerId)) continue;
                        if (p.Layers.Count > 0 && !p.Layers.Any(r => r.IsMatch(br.Layer))) continue;
                        var values = BlockSupport.Read(tr, br);
                        records.Add(p.Tags.Select(t => values.TryGetValue(t, out var v) ? v : "").ToArray());
                        p.SourceHandles.Add(OpContext.HandleOf(rid));
                    }
                    p.Nested += nested;
                }
                p.References = records.Count;
                IEnumerable<string[]> ordered = records;
                if (p.SortBy.Count > 0)
                {
                    var idx = p.SortBy.Select(s => p.Tags.IndexOf(s)).ToList();
                    ordered = records.OrderBy(r => r, new RowComparer(idx));
                }
                if (p.Group)
                {
                    var groups = new List<KeyValuePair<string[], int>>();
                    foreach (var r in ordered)
                    {
                        int at = groups.FindIndex(g => g.Key.SequenceEqual(r));
                        if (at < 0) groups.Add(new KeyValuePair<string[], int>(r, 1));
                        else groups[at] = new KeyValuePair<string[], int>(groups[at].Key, groups[at].Value + 1);
                    }
                    p.Rows = groups.Select(g => g.Key.Concat(new[] { g.Value.ToString(CultureInfo.InvariantCulture) }).ToList()).ToList();
                }
                else p.Rows = ordered.Select(r => r.ToList()).ToList();
                return 0;
            });
        }

        private sealed class RowComparer : IComparer<string[]>
        {
            private readonly List<int> _idx;
            public RowComparer(List<int> idx) { _idx = idx; }
            public int Compare(string[]? a, string[]? b)
            {
                if (a is null || b is null) return 0;
                foreach (int i in _idx)
                {
                    int c = CompareValues(a[i], b[i]);
                    if (c != 0) return c;
                }
                return 0;
            }
        }

        /// <summary>Numbers (including a leading number such as "101A") compare numerically, then the rest as text.</summary>
        internal static int CompareValues(string a, string b)
        {
            var ma = Regex.Match(a.Trim(), @"^-?\d+(\.\d+)?");
            var mb = Regex.Match(b.Trim(), @"^-?\d+(\.\d+)?");
            if (ma.Success && mb.Success)
            {
                double da = double.Parse(ma.Value, CultureInfo.InvariantCulture), dbl = double.Parse(mb.Value, CultureInfo.InvariantCulture);
                int c = da.CompareTo(dbl);
                if (c != 0) return c;
                return string.Compare(a.Trim().Substring(ma.Length), b.Trim().Substring(mb.Length), StringComparison.OrdinalIgnoreCase);
            }
            if (ma.Success != mb.Success) return ma.Success ? -1 : 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> AllHeaders(SchedulePlan p) => p.Group ? p.Headers.Concat(new[] { p.CountHeader }).ToList() : p.Headers.ToList();

        protected override SchedulePlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p.Table != null)
            {
                var t = p.Table;
                t.Headers = AllHeaders(p);
                t.Rows = p.Rows;
                t.Columns = t.Headers.Count;
                TableBuilder.Build(ctx, t);
                result.Created.Add(t.Handle);
            }
            if (p.CsvPath != null)
            {
                p.CsvWritten = p.CsvPath;
                if (ctx.Mode == RunMode.Rehearse)
                {
                    p.TempDir = Path.Combine(Path.GetTempPath(), "SA-rehearse", Safe(ctx.Job.JobId), Safe(ctx.CurrentOp.Id));
                    if (Directory.Exists(p.TempDir)) Directory.Delete(p.TempDir, true);
                    p.CsvWritten = Path.Combine(p.TempDir, Path.GetFileName(p.CsvPath));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(p.CsvWritten)!);
                var sb = new StringBuilder();
                sb.Append(string.Join(",", AllHeaders(p).Select(Csv))).Append("\r\n");
                foreach (var r in p.Rows) sb.Append(string.Join(",", r.Select(Csv))).Append("\r\n");
                File.WriteAllText(p.CsvWritten, sb.ToString(), new UTF8Encoding(true));
            }
            result.Outputs.Add("rowCount", p.Rows.Count);
            result.Outputs.Add("csvPath", p.CsvPath != null ? (JsonValue)p.CsvPath : JsonValue.Null);
            result.Outputs.Add("table", p.Table != null ? (JsonValue)p.Table.Handle : JsonValue.Null);
            return p;
        }

        private static string Safe(string s) => new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        private static string Csv(string v) =>
            v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v.StartsWith(" ", StringComparison.Ordinal) || v.EndsWith(" ", StringComparison.Ordinal)
                ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, SchedulePlan p)
        {
            try { return Check(ctx, result, p); }
            finally
            {
                if (p.TempDir != null)
                {
                    try { Directory.Delete(p.TempDir, true); }
                    catch (IOException e) { ctx.Warn("Could not remove the rehearsal CSV folder '" + p.TempDir + "': " + e.Message); }
                    catch (UnauthorizedAccessException e) { ctx.Warn("Could not remove the rehearsal CSV folder '" + p.TempDir + "': " + e.Message); }
                }
            }
        }

        private static bool Check(OpContext ctx, OpResult result, SchedulePlan p)
        {
            var mismatches = new List<string>();
            result.Readback.Add("references", p.References).Add("nestedSkipped", p.Nested).Add("rows", p.Rows.Count)
                .Add("sourceHandles", JsonArray.FromStrings(p.SourceHandles.Take(500)));
            if (p.Table != null)
            {
                var tb = new JsonObject();
                TableBuilder.Verify(ctx, p.Table, tb, mismatches);
                result.Readback.Add("table", tb);
            }
            if (p.CsvWritten != null)
            {
                if (!File.Exists(p.CsvWritten)) mismatches.Add("the CSV file " + p.CsvWritten + " was not written");
                else
                {
                    var lines = File.ReadAllLines(p.CsvWritten, Encoding.UTF8).Length;
                    // Embedded line breaks inside quoted values add physical lines; only a shortfall is a mismatch.
                    if (lines < p.Rows.Count + 1) mismatches.Add("the CSV file has " + lines + " lines, expected " + (p.Rows.Count + 1));
                    result.Readback.Add("csvLines", lines).Add("csvBytes", new FileInfo(p.CsvWritten).Length);
                    if (p.TempDir != null) result.Readback.Add("csvRehearsalPath", p.CsvWritten);
                }
            }
            if (!p.Group && p.Rows.Count != p.References) mismatches.Add(p.Rows.Count + " rows for " + p.References + " references");
            if (p.Group && p.Rows.Sum(r => int.Parse(r[r.Count - 1], CultureInfo.InvariantCulture)) != p.References) mismatches.Add("the counts do not add up to the " + p.References + " references");
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
