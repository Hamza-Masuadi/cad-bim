using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>The dimension style a dimension op uses, as validated.</summary>
    internal sealed class DimStyleChoice
    {
        public string Name = "";
        public bool Annotative;
        /// <summary>Model text height in job units at the current annotation scale (0 when the style is only planned).</summary>
        public double TextHeight;
        public string? Scale;
    }

    /// <summary>One dimension to create: measured points and a point on the dimension line, in job units.</summary>
    internal sealed class DimPlanItem
    {
        public Point2 P1, P2, LinePoint;
        /// <summary>Radians; null for an aligned dimension.</summary>
        public double? Rotation;
        public double Expected;
        public string Handle = "";
        public bool Overall;
    }

    /// <summary>
    /// Shared by dim.linear and dim.string. Dimensions are created with the style's id and no dimension variable
    /// overrides (Dimension.Dimscale, Dimtxt and friends are never set): annotative styles get the annotation scale
    /// through CANNOSCALE and Annotative = True after append; non-annotative styles use their own DIMSCALE.
    /// The managed API has no way to create dimension associativity (AcDbDimAssoc is not wrapped), so dimensions
    /// made from points are not associative; readback reports associative: false.
    /// </summary>
    internal static class DimCommon
    {
        internal static DimStyleChoice Style(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var c = new DimStyleChoice();
            string? given = args.GetStringOrNull("dimStyle");
            var db = ctx.Database;
            c.Name = given ?? Db.Read(ctx, tr => ((DimStyleTableRecord)tr.GetObject(db.Dimstyle, OpenMode.ForRead)).Name);
            var state = DraftState.For(ctx);
            if (given != null && ctx.IsValidating && state.DimStyleAnnotative.TryGetValue(given, out bool plannedAnno))
            {
                c.Annotative = plannedAnno;
            }
            else
            {
                bool found = Db.Read(ctx, tr =>
                {
                    var id = Db.SymbolId(tr, db.DimStyleTableId, c.Name);
                    if (id.IsNull) return false;
                    var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    c.Annotative = Anno.IsAnnotative(rec);
                    c.TextHeight = DimVars.ModelTextHeight(ctx, rec);
                    if (Math.Abs(rec.Dimlfac - 1) > 1e-12)
                        OpContext.Warning(issues, args, "dimStyle", "dimlfac", "style '" + c.Name + "' has DIMLFAC " + rec.Dimlfac + ": its dimensions show a value different from the geometry (AC-034)");
                    if (!c.Annotative)
                    {
                        var s = Anno.Current(db);
                        double dpp = s is null ? 1 : Anno.DrawingPerPaper(s);
                        double scale = rec.Dimscale > 0 ? rec.Dimscale : 1;
                        if (dpp > 1.5 && Math.Abs(scale - dpp) > 1e-6 * dpp)
                            OpContext.Warning(issues, args, "dimStyle", "dimscale_mismatch", "non-annotative style '" + c.Name + "' has DIMSCALE " + scale +
                                " but the annotation scale is " + Anno.CurrentName(db) + "; its text will not plot at the style's size. Use an annotative style or one made for this scale.");
                    }
                    return true;
                });
                if (!found)
                {
                    OpContext.Error(issues, args, "dimStyle", "dimstyle_missing", "dimension style '" + c.Name + "' is not in the drawing; add it with dimstyle.ensure first." +
                        Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.SymbolNames(tr, db.DimStyleTableId), c.Name)));
                }
            }
            if (c.Annotative)
            {
                var s = Anno.Current(db);
                c.Scale = s?.Name;
                if (s is null) OpContext.Error(issues, args, "dimStyle", IssueCodes.AnnotationScaleMissing, "annotative style '" + c.Name + "' needs a current annotation scale (CANNOSCALE)");
            }
            return c;
        }

        /// <summary>The unit direction a dimension measures along: the points' direction, or +X / +Y (signed by the points).</summary>
        internal static bool Direction(Point2 a, Point2 b, string orientation, double tol, out Point2 dir, out string error)
        {
            error = "";
            dir = new Point2(1, 0);
            var d = b - a;
            switch (orientation)
            {
                case "horizontal":
                    if (Math.Abs(d.X) <= tol) { error = "a horizontal dimension of points with the same X measures nothing"; return false; }
                    dir = new Point2(Math.Sign(d.X), 0);
                    return true;
                case "vertical":
                    if (Math.Abs(d.Y) <= tol) { error = "a vertical dimension of points with the same Y measures nothing"; return false; }
                    dir = new Point2(0, Math.Sign(d.Y));
                    return true;
                default:
                    if (d.Length <= tol) { error = "the points coincide"; return false; }
                    dir = d.Normalised();
                    return true;
            }
        }

        /// <summary>Creates one dimension in model space; returns its handle.</summary>
        internal static string Create(OpContext ctx, BlockTableRecord ms, DimPlanItem item, ObjectId styleId, string layer, bool annotative, string? overrideText)
        {
            var db = ctx.Database;
            Dimension dim;
            if (item.Rotation.HasValue)
            {
                var r = new RotatedDimension();
                r.SetDatabaseDefaults(db);
                r.Rotation = item.Rotation.Value;
                r.XLine1Point = ctx.Units.ToDrawing(item.P1);
                r.XLine2Point = ctx.Units.ToDrawing(item.P2);
                r.DimLinePoint = ctx.Units.ToDrawing(item.LinePoint);
                dim = r;
            }
            else
            {
                var a = new AlignedDimension();
                a.SetDatabaseDefaults(db);
                a.XLine1Point = ctx.Units.ToDrawing(item.P1);
                a.XLine2Point = ctx.Units.ToDrawing(item.P2);
                a.DimLinePoint = ctx.Units.ToDrawing(item.LinePoint);
                dim = a;
            }
            dim.DimensionStyle = styleId;
            dim.Layer = layer;
            dim.DimensionText = overrideText ?? "";
            var id = Db.Append(ctx, ms, dim);
            if (annotative) dim.Annotative = AnnotativeStates.True;
            dim.RecomputeDimensionBlock(true);
            item.Handle = OpContext.HandleOf(id);
            return item.Handle;
        }

        /// <summary>Re-reads a dimension; returns the measured value in job units, or null with a mismatch recorded.</summary>
        internal static double? Verify(OpContext ctx, DimPlanItem item, string style, string layer, bool annotative, string? overrideText, List<string> mismatches, JsonArray readback)
        {
            var id = ctx.IdOf(item.Handle);
            if (id.IsNull) { mismatches.Add("dimension " + item.Handle + " is not in the drawing after the op"); return null; }
            var tr = ctx.Transaction;
            if (!(tr.GetObject(id, OpenMode.ForRead) is Dimension dim)) { mismatches.Add("object " + item.Handle + " is not a dimension"); return null; }
            double measured = ctx.Units.ToJob(dim.Measurement);
            string styleName = ((DimStyleTableRecord)tr.GetObject(dim.DimensionStyle, OpenMode.ForRead)).Name;
            Point2 p1, p2, lp;
            double? rot = null;
            if (dim is RotatedDimension r) { p1 = ctx.Units.ToJob(r.XLine1Point); p2 = ctx.Units.ToJob(r.XLine2Point); lp = ctx.Units.ToJob(r.DimLinePoint); rot = r.Rotation; }
            else if (dim is AlignedDimension a) { p1 = ctx.Units.ToJob(a.XLine1Point); p2 = ctx.Units.ToJob(a.XLine2Point); lp = ctx.Units.ToJob(a.DimLinePoint); }
            else { mismatches.Add("dimension " + item.Handle + " is not linear"); return null; }
            bool anno = Anno.IsAnnotative(dim);
            readback.Add(new JsonObject()
                .Add("handle", item.Handle).Add("measurement", AcadValues.Round(measured)).Add("style", styleName).Add("layer", dim.Layer)
                .Add("xLine1", DrawingUnits.PointJson(p1)).Add("xLine2", DrawingUnits.PointJson(p2)).Add("dimLinePoint", DrawingUnits.PointJson(lp))
                .Add("annotative", anno).Add("associative", false).Add("textOverride", dim.DimensionText ?? "").Add("overall", item.Overall));
            double tol = ctx.LengthTolerance;
            string label = "dimension " + item.Handle;
            if (dim.Measurement < 0) mismatches.Add(label + " has no computed measurement");
            else if (Math.Abs(measured - item.Expected) > tol) mismatches.Add(label + " measures " + measured + ", expected " + item.Expected);
            if (p1.DistanceTo(item.P1) > tol || p2.DistanceTo(item.P2) > tol) mismatches.Add(label + ": extension line points read back differently");
            if (rot.HasValue && item.Rotation.HasValue && !Tolerance.AngleEqual(rot.Value * 180 / Math.PI, item.Rotation.Value * 180 / Math.PI, 1e-6)) mismatches.Add(label + ": rotation reads back differently");
            if (!string.Equals(styleName, style, StringComparison.OrdinalIgnoreCase)) mismatches.Add(label + " has style '" + styleName + "', expected '" + style + "'");
            if (!string.Equals(dim.Layer, layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add(label + " is on layer '" + dim.Layer + "', expected '" + layer + "'");
            if (anno != annotative) mismatches.Add(label + ": annotative reads back as " + anno);
            if ((dim.DimensionText ?? "") != (overrideText ?? "")) mismatches.Add(label + ": text override reads back as '" + dim.DimensionText + "'");
            return measured;
        }

        /// <summary>
        /// Dimension text overrides are refused unless they keep the measured value ("&lt;&gt;", e.g. "&lt;&gt; CLR") or are one
        /// of the accepted labels for an equal or variable string (EQ, VARIES, VIF, HOLD); AC-009.
        /// </summary>
        internal static string? CheckOverride(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            string? t = args.GetStringOrNull("textOverride");
            if (t is null || t.Length == 0) return null;
            if (t.Contains("<>")) return t;
            string u = t.Trim().ToUpperInvariant();
            if (u == "EQ" || u == "VARIES" || u == "VIF" || u == "HOLD")
            {
                OpContext.Warning(issues, args, "textOverride", "text_override_label", "the text '" + t + "' replaces the measured value; acceptable only in a string whose overall is fixed");
                return t;
            }
            OpContext.Error(issues, args, "textOverride", "text_override_refused", "overriding the measured value with '" + t + "' hides the real dimension (AC-009). Keep the value with <> (for example \"<> CLR\"), or fix the geometry.");
            return t;
        }

        internal static double Unit(double v) => v;
    }
}
