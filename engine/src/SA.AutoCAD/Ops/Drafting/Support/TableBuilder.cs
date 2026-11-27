using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>What a table holds, in job units.</summary>
    internal sealed class TableSpec
    {
        public Point2 Position;
        public string? Title;
        public List<string>? Headers;
        public List<List<string>> Rows = new List<List<string>>();
        public int Columns;
        public List<double>? ColumnWidths;
        public double? RowHeight;
        public string Style = "";
        public string Layer = "";
        /// <summary>Model text heights from the standard at the current annotation scale (job units); null = the table style's.</summary>
        public double? TitleHeight, BodyHeight;
        public string HeightSource = "table style";
        public string Handle = "";

        public int TitleRows => Title != null ? 1 : 0;
        public int HeaderRows => Headers != null && Headers.Count > 0 ? 1 : 0;
        public int TotalRows => TitleRows + HeaderRows + Rows.Count;

        /// <summary>The text expected in a cell (row and column of the whole table).</summary>
        public string Cell(int r, int c)
        {
            if (Title != null && r == 0) return c == 0 ? Title : "";
            r -= TitleRows;
            if (HeaderRows == 1 && r == 0) return c < Headers!.Count ? Headers[c] : "";
            r -= HeaderRows;
            var row = Rows[r];
            return c < row.Count ? row[c] : "";
        }
    }

    /// <summary>
    /// Builds an AutoCAD table: SetSize, row cell styles "_TITLE" (merged across), "_HEADER", "_DATA" of the table style,
    /// cell texts, then row heights, column widths and GenerateLayout. Tables are not annotative in AutoCAD, so text
    /// heights are model heights: when the job's standard has text heights, the plotted height ("table", else "body",
    /// else "notes"; title "title", else 1.4 x body) times the current annotation scale is set on the rows; otherwise the
    /// table style's heights apply. Only non-obsolete members (Cells, Rows, Columns) are used.
    /// </summary>
    internal static class TableBuilder
    {
        internal static ObjectId StyleId(Transaction tr, Database db, string name) => DraftDb.DictionaryEntry(tr, db.TableStyleDictionaryId, name);

        /// <summary>Checks the style (given or current) and fills Style; plans text heights from the standard.</summary>
        internal static void Plan(OpContext ctx, OpArgs args, string styleField, TableSpec spec, List<Issue> issues)
        {
            string? given = args.GetStringOrNull(styleField);
            if (given != null)
            {
                spec.Style = given;
                bool exists = Db.Read(ctx, tr => !StyleId(tr, ctx.Database, given).IsNull);
                if (!exists)
                    OpContext.Error(issues, args, styleField, "tablestyle_missing", "table style '" + given + "' is not in the drawing." +
                        Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.DictionaryNames(tr, ctx.Database.TableStyleDictionaryId), given)));
            }
            else
            {
                spec.Style = Db.Read(ctx, tr =>
                {
                    var id = ctx.Database.Tablestyle;
                    return id.IsNull ? "Standard" : ((TableStyle)tr.GetObject(id, OpenMode.ForRead)).Name;
                });
            }
            var std = ctx.Standard;
            if (std != null && std.TextHeights.Count > 0)
            {
                TextHeight? body = std.FindTextHeight("table") ?? std.FindTextHeight("body") ?? std.FindTextHeight("notes");
                TextHeight? title = std.FindTextHeight("title");
                if (body != null)
                {
                    spec.BodyHeight = Anno.ModelFromPlotted(ctx, body.Plotted, body.Unit);
                    spec.TitleHeight = title != null ? Anno.ModelFromPlotted(ctx, title.Plotted, title.Unit) : spec.BodyHeight * 1.4;
                    spec.HeightSource = "standard '" + body.Name + "' " + body.Plotted + " " + SA.Core.Units.Length.Symbol(body.Unit) + " at " + Anno.CurrentName(ctx.Database);
                }
            }
        }

        internal static ObjectId Build(OpContext ctx, TableSpec s)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var styleId = StyleId(tr, db, s.Style);
            if (styleId.IsNull) throw new InvalidOperationException("Table style '" + s.Style + "' is missing.");
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var t = new Table();
            t.SetDatabaseDefaults(db);
            t.TableStyle = styleId;
            t.Layer = s.Layer;
            t.Position = ctx.Units.ToDrawing(s.Position);
            t.SetSize(s.TotalRows, s.Columns);
            int r0 = 0;
            if (s.Title != null)
            {
                t.Rows[0].Style = "_TITLE";
                if (s.Columns > 1) t.MergeCells(CellRange.Create(t, 0, 0, 0, s.Columns - 1));
                r0 = 1;
            }
            if (s.HeaderRows == 1) { t.Rows[r0].Style = "_HEADER"; r0++; }
            for (int r = r0; r < s.TotalRows; r++) t.Rows[r].Style = "_DATA";
            for (int r = 0; r < s.TotalRows; r++)
                for (int c = 0; c < s.Columns; c++)
                {
                    if (s.Title != null && r == 0 && c > 0) continue;
                    t.Cells[r, c].TextString = s.Cell(r, c);
                }
            if (s.BodyHeight.HasValue)
                for (int r = 0; r < s.TotalRows; r++)
                    t.Rows[r].TextHeight = ctx.Units.ToDrawing(s.Title != null && r == 0 ? s.TitleHeight ?? s.BodyHeight.Value : s.BodyHeight.Value);
            if (s.RowHeight.HasValue)
                for (int r = 0; r < s.TotalRows; r++) t.Rows[r].Height = ctx.Units.ToDrawing(s.RowHeight.Value);
            var widths = s.ColumnWidths ?? EstimateWidths(ctx, t, s, tr);
            for (int c = 0; c < s.Columns; c++) t.Columns[c].Width = ctx.Units.ToDrawing(widths[c]);
            var id = Db.Append(ctx, ms, t);
            t.GenerateLayout();
            s.Handle = OpContext.HandleOf(id);
            return id;
        }

        /// <summary>Column widths to fit the longest text: 0.8 x text height per character plus a margin of 0.6 x text height each side.</summary>
        private static List<double> EstimateWidths(OpContext ctx, Table t, TableSpec s, Transaction tr)
        {
            double h = s.BodyHeight ?? StyleHeight(ctx, tr, t.TableStyle);
            double margin = 0.6 * h; // the default cell margin is about a third of the default text height; this errs wide
            var list = new List<double>();
            for (int c = 0; c < s.Columns; c++)
            {
                int chars = 1;
                for (int r = s.TitleRows; r < s.TotalRows; r++) chars = Math.Max(chars, s.Cell(r, c).Length);
                list.Add(chars * h * 0.8 + 2 * margin);
            }
            if (s.Title != null)
            {
                double need = s.Title.Length * (s.TitleHeight ?? h * 1.4) * 0.8 + 2 * margin;
                double have = list.Sum();
                if (need > have) for (int c = 0; c < list.Count; c++) list[c] *= need / have;
            }
            return list;
        }

        private static double StyleHeight(OpContext ctx, Transaction tr, ObjectId styleId)
        {
            var ts = (TableStyle)tr.GetObject(styleId, OpenMode.ForRead);
            double h = ts.TextHeight(RowType.DataRow);
            return ctx.Units.ToJob(h > 0 ? h : 2.5);
        }

        /// <summary>Re-reads the table and compares size, position, layer, style and every cell's text.</summary>
        internal static bool Verify(OpContext ctx, TableSpec s, JsonObject readback, List<string> mismatches)
        {
            var id = ctx.IdOf(s.Handle);
            if (id.IsNull) { mismatches.Add("the table " + s.Handle + " is not in the drawing after the op"); return false; }
            var tr = ctx.Transaction;
            if (!(tr.GetObject(id, OpenMode.ForRead) is Table t)) { mismatches.Add("object " + s.Handle + " is not a table"); return false; }
            int rows = t.Rows.Count, cols = t.Columns.Count;
            var pos = ctx.Units.ToJob(t.Position);
            string style = ((TableStyle)tr.GetObject(t.TableStyle, OpenMode.ForRead)).Name;
            var widths = new JsonArray();
            for (int c = 0; c < cols; c++) widths.Add(AcadValues.Round(ctx.Units.ToJob(t.Columns[c].Width)));
            readback.Set("handle", s.Handle).Set("rows", rows).Set("columns", cols).Set("position", DrawingUnits.PointJson(pos))
                .Set("style", style).Set("layer", t.Layer).Set("columnWidths", widths).Set("textHeightSource", s.HeightSource)
                .Set("width", AcadValues.Round(ctx.Units.ToJob(t.Width))).Set("height", AcadValues.Round(ctx.Units.ToJob(t.Height)));
            bool ok = true;
            if (rows != s.TotalRows || cols != s.Columns) { mismatches.Add("the table has " + rows + " x " + cols + " cells, expected " + s.TotalRows + " x " + s.Columns); return false; }
            if (pos.DistanceTo(s.Position) > ctx.LengthTolerance) { mismatches.Add("the table position reads back as " + pos); ok = false; }
            if (!string.Equals(t.Layer, s.Layer, StringComparison.OrdinalIgnoreCase)) { mismatches.Add("the table is on layer '" + t.Layer + "'"); ok = false; }
            if (!string.Equals(style, s.Style, StringComparison.OrdinalIgnoreCase)) { mismatches.Add("the table style reads back as '" + style + "'"); ok = false; }
            int bad = 0;
            string first = "";
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (s.Title != null && r == 0 && c > 0) continue;
                    string got = t.Cells[r, c].TextString ?? "";
                    if (got != s.Cell(r, c)) { if (bad == 0) first = "cell (" + r + ", " + c + ") reads '" + got + "', expected '" + s.Cell(r, c) + "'"; bad++; }
                }
            if (bad > 0) { mismatches.Add(bad + " cells differ; " + first); ok = false; }
            return ok;
        }
    }
}
