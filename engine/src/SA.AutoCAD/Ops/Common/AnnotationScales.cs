using System;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.Core.Standards;

namespace SA.AutoCAD.Ops.Common
{
    /// <summary>
    /// The drawing's annotation scale list (context collection "ACDB_ANNOTATIONSCALES"). A scale stores
    /// PaperUnits : DrawingUnits, with paper units in millimetres (MEASUREMENT metric) or inches (imperial) and drawing
    /// units in INSUNITS (the convention doc.setup writes; see Drafting/Support/Anno). Its ratio here is model length per
    /// paper length, 100 for 1:100 in any unit: DrawingUnits / PaperUnits x millimetres per drawing unit / millimetres
    /// per paper unit.
    /// </summary>
    internal static class AnnotationScales
    {
        internal const string CollectionName = "ACDB_ANNOTATIONSCALES";

        internal static ObjectContextCollection? Collection(Database db)
        {
            try { return db.ObjectContextManager.GetContextCollection(CollectionName); }
            catch (Exception) { return null; }
        }

        /// <summary>Model per paper ratio of a scale. In a unitless drawing it is DrawingUnits / PaperUnits.</summary>
        internal static double Ratio(Database db, AnnotationScale s)
        {
            if (!(s.PaperUnits > 0)) return double.NaN;
            double dpp = s.DrawingUnits / s.PaperUnits;
            double? mmPerUnit = DrawingUnits.MillimetresPer((int)db.Insunits);
            if (!mmPerUnit.HasValue) return dpp;
            double paperMm = db.Measurement == MeasurementValue.Metric ? 1.0 : 25.4;
            return dpp * mmPerUnit.Value / paperMm;
        }

        /// <summary>
        /// Finds a scale by exact name first, then by model per paper ratio (within 1e-9 relative). <paramref name="scaleText"/>
        /// is a job scale string such as "1:100".
        /// </summary>
        internal static AnnotationScale? Find(Database db, string scaleText)
        {
            var coll = Collection(db);
            if (coll is null) return null;
            foreach (ObjectContext c in coll)
                if (c is AnnotationScale s && string.Equals(s.Name, scaleText.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
            if (!Scale.TryParseRatio(scaleText, out double ratio)) return null;
            foreach (ObjectContext c in coll)
            {
                if (!(c is AnnotationScale s)) continue;
                double r = Ratio(db, s);
                if (!double.IsNaN(r) && Math.Abs(r - ratio) <= 1e-9 * Math.Max(1, ratio)) return s;
            }
            return null;
        }
    }
}
