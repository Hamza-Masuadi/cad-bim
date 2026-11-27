using System.Collections.Generic;
using System.Linq;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// table.create: an AutoCAD table at position (top-left), in the named table style (default: the current one), with
    /// an optional merged title row, an optional header row and the data rows (see <see cref="TableBuilder"/> for
    /// cell styles and text heights). The column count is the header count, else the longest row; shorter rows are
    /// padded with empty cells. Readback compares every cell's text.
    /// </summary>
    internal sealed class TableCreate : OpHandler<TableSpec>
    {
        public override string Name => "table.create";

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static TableSpec Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var s = new TableSpec { Position = args.GetPoint2("position"), Title = args.GetStringOrNull("title") };
            var headers = args.GetStringList("headers");
            if (headers.Count > 0) s.Headers = headers;
            var rows = args.Get("rows") as JsonArray ?? new JsonArray();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = new List<string>();
                if (rows[i] is JsonArray cells)
                    foreach (var c in cells) row.Add(c is JsonString str ? str.Value : c.ToString());
                s.Rows.Add(row);
            }
            s.Columns = headers.Count > 0 ? headers.Count : s.Rows.Count == 0 ? 0 : s.Rows.Max(r => r.Count);
            if (s.Columns == 0) OpContext.Error(issues, args, "rows", "table_empty", "the table has no columns: give headers or non-empty rows");
            for (int i = 0; i < s.Rows.Count; i++)
                if (s.Rows[i].Count > s.Columns)
                    issues.Add(new Issue(JsonPath.Index(JsonPath.Key(args.Path, "rows"), i), "row_too_long", "row " + i + " has " + s.Rows[i].Count + " cells but there are " + s.Columns + " headers"));
            if (s.Rows.Count == 0) OpContext.Warning(issues, args, "rows", "table_no_rows", "the table has no data rows");
            var widths = args.GetLengthList("columnWidths");
            if (widths.Count > 0)
            {
                if (widths.Count != s.Columns) OpContext.Error(issues, args, "columnWidths", "column_widths_count", widths.Count + " column widths for " + s.Columns + " columns");
                if (widths.Any(w => !(w > 0))) OpContext.Error(issues, args, "columnWidths", "column_width_invalid", "column widths must be positive");
                s.ColumnWidths = widths;
            }
            s.RowHeight = args.GetLengthOrNull("rowHeight");
            TableBuilder.Plan(ctx, args, "style", s, issues);
            s.Layer = DraftDb.Layer(ctx, args, issues, "layer", "tables");
            return s;
        }

        protected override TableSpec Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var s = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            TableBuilder.Build(ctx, s);
            result.Created.Add(s.Handle);
            return s;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, TableSpec s)
        {
            var mismatches = new List<string>();
            bool ok = TableBuilder.Verify(ctx, s, result.Readback, mismatches);
            result.Outputs.Add("rowCount", s.Rows.Count).Add("columnCount", s.Columns);
            foreach (var m in mismatches) Mismatch(result, m);
            return ok && mismatches.Count == 0;
        }
    }
}
