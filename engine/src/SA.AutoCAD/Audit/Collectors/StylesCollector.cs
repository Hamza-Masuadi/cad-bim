using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Checks;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// Table "styles" (data/checks/FACTS.md 1.6): one record per text style and per dimension style; id = style handle.
    /// Left out: xref-dependent styles and shape-file text style records.
    /// Both kinds: kind ("text" or "dim"), name, isUsed, annotative. Text: font, isShx, height. Dim: dimlunit, dimdec,
    /// dimrnd, dimlfac, dimtxt, dimtxsty, stepMm, unitSystem.
    /// isUsed comes from the shared scan of model space, paper space and every block definition: text styles of TEXT,
    /// MTEXT, ATTDEF, attributes, MULTILEADER, table cells and dimensions, plus the DIMTXSTY of every used dimension
    /// style; dimension styles of dimensions, leaders and tolerances.
    /// </summary>
    internal sealed class StylesCollector : IFactCollector
    {
        public string Table => "styles";

        public void Collect(FactContext ctx, FactTable table)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var scan = ctx.Scan;

            var dims = new List<DimStyleTableRecord>();
            var textUsed = new HashSet<ObjectId>(scan.TextStylesUsed);
            var dimTable = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in dimTable)
            {
                if (id.IsErased) continue;
                var d = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (d.IsDependent) continue;
                dims.Add(d);
                if (scan.DimStylesUsed.Contains(id) && !d.Dimtxsty.IsNull) textUsed.Add(d.Dimtxsty);
            }

            var textTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            foreach (ObjectId id in textTable)
            {
                if (id.IsErased) continue;
                var t = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (t.IsDependent || t.IsShapeFile) continue;
                string typeface = t.Font.TypeFace ?? "";
                string file = t.FileName ?? "";
                table.Add(FactValues.Handle(id))
                    .Set("kind", "text")
                    .Set("name", t.Name)
                    .Set("isUsed", textUsed.Contains(id))
                    .Set("annotative", t.Annotative == AnnotativeStates.True)
                    .Set("font", typeface.Length > 0 ? typeface : file)
                    .Set("isShx", IsShx(typeface, file))
                    .Set("height", t.TextSize);
            }

            double? unitMm = FactValues.ModelUnitMm(db);
            string? metaUnit = FactValues.MetaLengthUnit(db, out _);
            bool imperialUnit = metaUnit == "in" || metaUnit == "ft";
            foreach (var d in dims)
            {
                string txsty = "";
                if (!d.Dimtxsty.IsNull && tr.GetObject(d.Dimtxsty, OpenMode.ForRead) is TextStyleTableRecord ts) txsty = ts.Name;
                int lunit = d.Dimlunit;
                var r = table.Add(FactValues.Handle(d.ObjectId))
                    .Set("kind", "dim")
                    .Set("name", d.Name)
                    .Set("isUsed", scan.DimStylesUsed.Contains(d.ObjectId))
                    .Set("annotative", d.Annotative == AnnotativeStates.True)
                    .Set("dimlunit", lunit)
                    .Set("dimdec", d.Dimdec)
                    .Set("dimrnd", d.Dimrnd)
                    .Set("dimlfac", d.Dimlfac)
                    .Set("dimtxt", d.Dimtxt)
                    .Set("dimtxsty", txsty)
                    .Set("unitSystem", lunit == 3 || lunit == 4 || lunit == 5 || imperialUnit ? "imperial" : "metric");
                var step = StepMm(lunit, d.Dimdec, d.Dimrnd, d.Dimlfac, unitMm);
                if (step.HasValue) r.Set("stepMm", step.Value);
            }
        }

        /// <summary>
        /// Smallest displayed increment in mm: 10^-DIMDEC for DIMLUNIT 1, 2, 3, 6 (engineering, 3, counts in inches) and
        /// 1 / 2^DIMDEC for 4 and 5; DIMRND raises it; divided by |DIMLFAC|. Null for other DIMLUNIT values, DIMLFAC 0 or
        /// an unknown drawing unit.
        /// </summary>
        internal static double? StepMm(int dimlunit, int dimdec, double dimrnd, double dimlfac, double? drawingUnitMm)
        {
            double step;
            switch (dimlunit)
            {
                case 1: case 2: case 3: case 6: step = Math.Pow(10, -dimdec); break;
                case 4: case 5: step = 1.0 / Math.Pow(2, dimdec); break;
                default: return null;
            }
            if (dimrnd > 0) step = Math.Max(step, dimrnd);
            if (Math.Abs(dimlfac) < 1e-12) return null;
            double? unitMm = dimlunit == 3 ? 25.4 : drawingUnitMm;
            if (!unitMm.HasValue) return null;
            return step / Math.Abs(dimlfac) * unitMm.Value;
        }

        /// <summary>A TrueType family or a .ttf/.ttc/.otf file is not SHX; a .shx file, or a bare font name, is.</summary>
        private static bool IsShx(string typeface, string file)
        {
            if (typeface.Length > 0) return false;
            string ext = "";
            try { ext = Path.GetExtension(file) ?? ""; } catch (ArgumentException) { }
            if (ext.Length == 0) return file.Length > 0;
            return string.Equals(ext, ".shx", StringComparison.OrdinalIgnoreCase);
        }
    }
}
