using System;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Standards;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>
    /// Annotation scale arithmetic. An AnnotationScale stores PaperUnits : DrawingUnits, where paper units are
    /// millimetres in a metric drawing (MEASUREMENT 1) and inches in an imperial one, and drawing units are the
    /// drawing's INSUNITS. So DrawingUnits / PaperUnits is "drawing units per paper unit": 100 for 1:100 in a
    /// millimetre drawing, 0.1 for 1:100 in a metre drawing, 48 for 1/4" = 1'-0" in an inch drawing. Annotative sizes
    /// (DIMTXT of an annotative dimension style, the height of an annotative text style) are in paper units.
    /// </summary>
    internal static class Anno
    {
        internal static double PaperUnitMm(Database db) => db.Measurement == MeasurementValue.Metric ? 1.0 : 25.4;

        /// <summary>Drawing units per paper unit of a scale.</summary>
        internal static double DrawingPerPaper(AnnotationScale s) => s.PaperUnits > 0 ? s.DrawingUnits / s.PaperUnits : double.NaN;

        internal static AnnotationScale? Current(Database db)
        {
            try { return db.Cannoscale; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Drawing units per paper unit for a job scale such as "1:100": the drawing's scale entry of that name when it
        /// exists, else computed from the ratio and the drawing's units. Null when the text is not a scale.
        /// </summary>
        internal static double? DrawingPerPaper(OpContext ctx, string scaleText, double mmPerDrawingUnit)
        {
            var s = AnnotationScales.Find(ctx.Database, scaleText);
            if (s != null && string.Equals(s.Name, scaleText.Trim(), StringComparison.OrdinalIgnoreCase)) return DrawingPerPaper(s);
            if (!Scale.TryParseRatio(scaleText, out double ratio)) return null;
            return ratio * PaperUnitMm(ctx.Database) / mmPerDrawingUnit;
        }

        /// <summary>A plotted length (in <paramref name="unit"/>) as a model length in job units at the current annotation scale.</summary>
        internal static double ModelFromPlotted(OpContext ctx, double plotted, LengthUnit unit)
        {
            var s = Current(ctx.Database);
            double dpp = s is null ? 1 : DrawingPerPaper(s);
            if (double.IsNaN(dpp) || dpp <= 0) dpp = 1;
            double paperUnits = Length.Convert(plotted, unit, LengthUnit.Millimetre) / PaperUnitMm(ctx.Database);
            return ctx.Units.ToJob(paperUnits * dpp);
        }

        /// <summary>A paper length in paper units (mm or in) as job units on paper, for reports.</summary>
        internal static double PaperToJob(OpContext ctx, double paperUnits) =>
            Length.Convert(paperUnits * PaperUnitMm(ctx.Database), LengthUnit.Millimetre, ctx.JobUnit);

        /// <summary>A job-unit length meant as a paper size, in paper units (mm or in).</summary>
        internal static double JobToPaper(OpContext ctx, double jobLength) =>
            Length.Convert(jobLength, ctx.JobUnit, LengthUnit.Millimetre) / PaperUnitMm(ctx.Database);

        /// <summary>A text style's TextSize for a height in job units: paper units when annotative, drawing units otherwise.</summary>
        internal static double StyleSizeFromJob(OpContext ctx, double jobHeight, bool annotative) =>
            annotative ? JobToPaper(ctx, jobHeight) : ctx.Units.ToDrawing(jobHeight);

        /// <summary>A text style's TextSize as a height in job units (paper height when annotative).</summary>
        internal static double StyleSizeToJob(OpContext ctx, double size, bool annotative) =>
            annotative ? PaperToJob(ctx, size) : ctx.Units.ToJob(size);

        /// <summary>Name of the current annotation scale, for messages.</summary>
        internal static string CurrentName(Database db) => Current(db)?.Name ?? "(none)";

        /// <summary>
        /// Switches CANNOSCALE for this job to the given scale (restored after the job), so a new annotative object gets
        /// that scale as its only context.
        /// </summary>
        internal static void UseScale(OpContext ctx, string? name)
        {
            if (name is null) return;
            var current = Current(ctx.Database);
            if (current is null || !string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)) ctx.SetSysVar("CANNOSCALE", name);
        }

        internal static bool IsAnnotative(DBObject o) => o.Annotative == AnnotativeStates.True;
    }
}
