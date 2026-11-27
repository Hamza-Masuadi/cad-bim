using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// fix.units: sets the drawing's insertion units (Database.Insunits) and MEASUREMENT (Database.Measurement: metric
    /// for mm, cm and m, imperial for in and ft, which picks the default hatch and linetype files). Geometry is NOT
    /// scaled unless scaleContents is true; the result says which happened ("geometryScaled" and "factor").
    ///
    /// With scaleContents: every model space entity (entities on locked layers included) is scaled about basePoint by
    /// (millimetres per old unit) / (millimetres per new unit), so a drawing drawn in the wrong unit becomes true size.
    /// Paper space viewports are adjusted to keep showing the same part of the model at the same plotted size: view
    /// target scaled about the base point and custom scale divided by the factor. Paper space itself is not scaled. A
    /// unitless drawing cannot be scaled (the old unit is unknown); only the units are set.
    ///
    /// Sizes stored as plain numbers in drawing units are scaled by the same factor, so annotation keeps its size relative
    /// to the geometry: LTSCALE, TEXTSIZE, the fixed height of every non-annotative text style, DIMSCALE of every
    /// non-annotative dimension style (when not 0) and the DIMSCALE override of model space dimensions that carry one.
    /// When the paper unit changes (MEASUREMENT metric to imperial or back), the paper height of annotative text styles
    /// is converted. Not changed, and listed under "notScaled" with a warning: the annotation scale list (there is no
    /// documented way to edit an existing scale), the paper sizes of annotative dimension styles on a paper unit change,
    /// multileader and table styles, and paper space content and layout plot settings.
    ///
    /// Header values (INSUNITS, MEASUREMENT, LTSCALE, TEXTSIZE) are written through the Database properties in commit
    /// mode and through the job's system variable scope in a rehearsal, as doc.setup does.
    ///
    /// basePoint is converted with the old units. The units of the job (job lengths) are interpreted with the old
    /// INSUNITS for the whole job, so fix.units should be alone in its job.
    /// </summary>
    internal sealed class FixUnits : OpHandler<FixUnits.Plan>
    {
        public override string Name => "fix.units";

        public override bool AllowsUnitlessDrawing => true;

        internal sealed class Sample
        {
            public ObjectId Id;
            public Point3d Before;
        }

        internal sealed class Plan
        {
            public int OldCode;
            public int NewCode;
            public MeasurementValue Measurement;
            public bool Scale;
            public double Factor = 1;
            public Point3d Base;
            public int Entities;
            public int Viewports;
            public List<Sample> Samples = new List<Sample>();
            /// <summary>Paper millimetres per paper unit before and after (MEASUREMENT).</summary>
            public double PaperMmBefore = 1, PaperMmAfter = 1;
            public double LtScale, TextSize;
            /// <summary>Text style or dimension style id to the value it must read back.</summary>
            public Dictionary<ObjectId, double> StyleSizes = new Dictionary<ObjectId, double>();
            public Dictionary<ObjectId, double> DimScales = new Dictionary<ObjectId, double>();
            public int DimensionOverrides;
            public List<string> NotScaled = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p is null || !ctx.IsValidating) return;
            if (ctx.Job.Ops.Count > 1)
                OpContext.Warning(issues, args, null, "units_not_alone", "fix.units changes INSUNITS; other ops in the same job still use the old units. Run it in a job of its own.");
            if (p.OldCode == p.NewCode && !p.Scale)
                OpContext.Warning(issues, args, "insUnits", "units_unchanged", "INSUNITS is already " + DrawingUnits.NameOf(p.NewCode));
            if (!p.Scale && p.OldCode != 0 && p.OldCode != p.NewCode)
                OpContext.Warning(issues, args, "scaleContents", "units_not_scaled", "only the unit setting changes; geometry is not scaled, so existing objects are now read as " + DrawingUnits.NameOf(p.NewCode) + ". Set scaleContents if the drawing was drawn in " + DrawingUnits.NameOf(p.OldCode) + " and must keep its true size.");
        }

        private static int Code(string u)
        {
            switch (u)
            {
                case "in": return 1;
                case "ft": return 2;
                case "mm": return 4;
                case "cm": return 5;
                case "m": return 6;
                default: throw new ArgumentException("unit " + u);
            }
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { OldCode = (int)ctx.Database.Insunits, NewCode = Code(args.GetString("insUnits")) };
            string u = args.GetString("insUnits");
            p.Measurement = u == "in" || u == "ft" ? MeasurementValue.English : MeasurementValue.Metric;
            p.Scale = args.GetBool("scaleContents");
            if (!p.Scale) return p;
            double? oldMm = DrawingUnits.MillimetresPer(p.OldCode);
            double newMm = DrawingUnits.MillimetresPer(p.NewCode)!.Value;
            if (!oldMm.HasValue)
            {
                OpContext.Error(issues, args, "scaleContents", "units_unknown", "the drawing is " + DrawingUnits.NameOf(p.OldCode) + ", so there is no factor to scale by; set the units without scaling, or give the drawing its true unit first");
                return null;
            }
            p.Factor = oldMm.Value / newMm;
            var b = args.GetPoint2("basePoint");
            double toOld = Length.MillimetresPer(ctx.JobUnit) / oldMm.Value;
            p.Base = new Point3d(b.X * toOld, b.Y * toOld, 0);
            if (Math.Abs(p.Factor - 1) < 1e-15 && ctx.IsValidating)
                OpContext.Warning(issues, args, "scaleContents", "units_factor_one", "old and new units are the same; nothing is scaled");
            p.PaperMmBefore = ctx.Database.Measurement == MeasurementValue.Metric ? 1.0 : 25.4;
            p.PaperMmAfter = p.Measurement == MeasurementValue.Metric ? 1.0 : 25.4;
            NotScaled(ctx, p);
            if (ctx.IsValidating && p.NotScaled.Count > 0)
                OpContext.Warning(issues, args, "scaleContents", "units_not_all_scaled", "these keep their old numbers and must be checked or redone after the job: " + string.Join("; ", p.NotScaled));
            return p;
        }

        private static void NotScaled(OpContext ctx, Plan p)
        {
            var db = ctx.Database;
            var names = new List<string>();
            var coll = AnnotationScales.Collection(db);
            if (coll != null) foreach (ObjectContext c in coll) if (c is AnnotationScale s) names.Add(s.Name);
            if (names.Count > 0)
                p.NotScaled.Add("annotation scale list (" + string.Join(", ", names.Take(10)) + (names.Count > 10 ? ", ..." : "") + "): each entry keeps its drawing units, so annotative objects and viewports using it no longer match; recreate the scales for the new unit");
            Db.Read(ctx, tr =>
            {
                if (p.PaperMmBefore != p.PaperMmAfter)
                {
                    var annoDims = new List<string>();
                    foreach (ObjectId id in (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
                    {
                        var d = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (d.Annotative == AnnotativeStates.True) annoDims.Add(d.Name);
                    }
                    if (annoDims.Count > 0)
                        p.NotScaled.Add("paper sizes of annotative dimension styles (" + string.Join(", ", annoDims) + "): the paper unit changes, so their lengths are now read in the new paper unit");
                }
                if (SheetDb.PaperLayouts(tr, db).Count > 0)
                    p.NotScaled.Add("paper space content and layout plot settings (viewports are adjusted to keep their plotted scale)");
                return 0;
            });
            p.NotScaled.Add("multileader and table styles");
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the change could not be planned.");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            if (p.Scale && Math.Abs(p.Factor - 1) > 1e-15)
            {
                var styleDimScale = new Dictionary<ObjectId, double>();
                foreach (ObjectId id in (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead))
                    styleDimScale[id] = ((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Dimscale;

                var m = Matrix3d.Scaling(p.Factor, p.Base);
                var ms = Db.ModelSpace(tr, db, OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (id.IsErased) continue;
                    var e = SheetDb.OpenForWrite(tr, id, true);
                    if (p.Samples.Count < 25 && SamplePoint(e, out var pt)) p.Samples.Add(new Sample { Id = id, Before = pt });
                    // A dimension whose DIMSCALE differs from its style's carries an override, which the style change below misses.
                    double? overrideScale = null;
                    if (e is Dimension dim && dim.Annotative != AnnotativeStates.True && dim.Dimscale > 0 &&
                        styleDimScale.TryGetValue(dim.DimensionStyle, out double ss) && Math.Abs(dim.Dimscale - ss) > 1e-12 * Math.Max(1, ss))
                        overrideScale = dim.Dimscale;
                    e.TransformBy(m);
                    if (overrideScale.HasValue) { ((Dimension)e).Dimscale = overrideScale.Value * p.Factor; p.DimensionOverrides++; }
                    p.Entities++;
                    result.Modified.Add(OpContext.HandleOf(id));
                }

                foreach (ObjectId id in (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead))
                {
                    var t = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!(t.TextSize > 0)) continue;
                    bool annotative = t.Annotative == AnnotativeStates.True;
                    if (annotative && p.PaperMmBefore == p.PaperMmAfter) continue;
                    double size = annotative ? t.TextSize * p.PaperMmBefore / p.PaperMmAfter : t.TextSize * p.Factor;
                    t.UpgradeOpen();
                    t.TextSize = size;
                    p.StyleSizes[id] = size;
                    result.Modified.Add(OpContext.HandleOf(id));
                }
                foreach (var kv in styleDimScale)
                {
                    var d = (DimStyleTableRecord)tr.GetObject(kv.Key, OpenMode.ForRead);
                    if (d.Annotative == AnnotativeStates.True || !(d.Dimscale > 0)) continue;
                    d.UpgradeOpen();
                    d.Dimscale = kv.Value * p.Factor;
                    p.DimScales[kv.Key] = d.Dimscale;
                    result.Modified.Add(OpContext.HandleOf(kv.Key));
                }
                p.LtScale = db.Ltscale * p.Factor;
                p.TextSize = db.Textsize * p.Factor;
                if (rehearse) { ctx.SetSysVar("LTSCALE", p.LtScale); ctx.SetSysVar("TEXTSIZE", p.TextSize); }
                else { db.Ltscale = p.LtScale; db.Textsize = p.TextSize; }
                foreach (var layout in SheetDb.PaperLayouts(tr, db))
                    foreach (var vid in SheetDb.ModelViewports(tr, layout))
                    {
                        var vp = (Viewport)SheetDb.OpenForWrite(tr, vid, true);
                        vp.ViewTarget = p.Base + (vp.ViewTarget - p.Base) * p.Factor;
                        vp.ViewCenter = new Point2d(vp.ViewCenter.X * p.Factor, vp.ViewCenter.Y * p.Factor);
                        if (vp.CustomScale > 0) vp.CustomScale = vp.CustomScale / p.Factor;
                        p.Viewports++;
                        result.Modified.Add(OpContext.HandleOf(vid));
                    }
            }
            else
            {
                p.Factor = 1;
                p.NotScaled.Clear();
            }
            if ((int)db.Insunits != p.NewCode)
            {
                if (rehearse) ctx.SetSysVar("INSUNITS", (short)p.NewCode);
                else db.Insunits = (UnitsValue)p.NewCode;
            }
            if (db.Measurement != p.Measurement)
            {
                if (rehearse) ctx.SetSysVar("MEASUREMENT", (short)(p.Measurement == MeasurementValue.Metric ? 1 : 0));
                else db.Measurement = p.Measurement;
            }
            if (p.NotScaled.Count > 0) ctx.Warn("fix.units scaled the model but not: " + string.Join("; ", p.NotScaled) + ".");
            result.Outputs.Add("before", DrawingUnits.NameOf(p.OldCode));
            result.Outputs.Add("factor", p.Factor);
            result.Outputs.Add("geometryScaled", p.Factor != 1);
            return p;
        }

        private static bool SamplePoint(Entity e, out Point3d pt)
        {
            pt = default;
            switch (e)
            {
                case Line l: pt = l.StartPoint; return true;
                case Circle c: pt = c.Center; return true;
                case BlockReference br: pt = br.Position; return true;
                case DBText t: pt = t.Position; return true;
                case DBPoint d: pt = d.Position; return true;
                case Polyline pl when pl.NumberOfVertices > 0: pt = pl.GetPoint3dAt(0); return true;
                default: return false;
            }
        }

        private static Point3d CurrentPoint(Entity e)
        {
            switch (e)
            {
                case Line l: return l.StartPoint;
                case Circle c: return c.Center;
                case BlockReference br: return br.Position;
                case DBText t: return t.Position;
                case DBPoint d: return d.Position;
                case Polyline pl: return pl.GetPoint3dAt(0);
                default: throw new InvalidOperationException("Not a sampled type.");
            }
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var db = ctx.Database;
            var tr = ctx.Transaction;
            bool ok = true;
            int code = (int)db.Insunits;
            result.Readback.Add("insUnitsBefore", DrawingUnits.NameOf(p.OldCode)).Add("insUnits", DrawingUnits.NameOf(code))
                .Add("measurement", db.Measurement.ToString()).Add("geometryScaled", p.Scale && p.Factor != 1)
                .Add("factor", p.Factor).Add("entitiesScaled", p.Entities).Add("viewportsAdjusted", p.Viewports)
                .Add("summary", p.Scale && p.Factor != 1
                    ? "Units set to " + DrawingUnits.NameOf(code) + " and model space scaled by " + p.Factor + "."
                    : "Units set to " + DrawingUnits.NameOf(code) + "; geometry was not scaled.");
            if (code != p.NewCode) ok = Mismatch(result, "INSUNITS reads back " + DrawingUnits.NameOf(code));
            if (db.Measurement != p.Measurement) ok = Mismatch(result, "MEASUREMENT reads back " + db.Measurement);
            // Sampled points moved exactly as the scaling says (drawing units, relative tolerance).
            foreach (var s in p.Samples)
            {
                var e = (Entity)tr.GetObject(s.Id, OpenMode.ForRead);
                var want = p.Base + (s.Before - p.Base) * p.Factor;
                var got = CurrentPoint(e);
                double tol = 1e-9 * Math.Max(1, want.GetAsVector().Length);
                if (got.DistanceTo(want) > tol) { ok = Mismatch(result, "entity " + OpContext.HandleOf(s.Id) + " is not where the scaling puts it"); break; }
            }
            result.Readback.Add("samplesChecked", p.Samples.Count);
            if (p.Factor != 1)
            {
                if (Math.Abs(db.Ltscale - p.LtScale) > 1e-9 * Math.Max(1, p.LtScale)) ok = Mismatch(result, "LTSCALE reads back " + db.Ltscale + ", expected " + p.LtScale);
                if (Math.Abs(db.Textsize - p.TextSize) > 1e-9 * Math.Max(1, p.TextSize)) ok = Mismatch(result, "TEXTSIZE reads back " + db.Textsize + ", expected " + p.TextSize);
                foreach (var kv in p.StyleSizes)
                {
                    var t = (TextStyleTableRecord)tr.GetObject(kv.Key, OpenMode.ForRead);
                    if (Math.Abs(t.TextSize - kv.Value) > 1e-9 * Math.Max(1, kv.Value)) ok = Mismatch(result, "text style '" + t.Name + "' height reads back " + t.TextSize);
                }
                foreach (var kv in p.DimScales)
                {
                    var d = (DimStyleTableRecord)tr.GetObject(kv.Key, OpenMode.ForRead);
                    if (Math.Abs(d.Dimscale - kv.Value) > 1e-9 * Math.Max(1, kv.Value)) ok = Mismatch(result, "dimension style '" + d.Name + "' DIMSCALE reads back " + d.Dimscale);
                }
            }
            result.Readback.Add("ltScale", AcadValues.Round(db.Ltscale, 9)).Add("textStylesScaled", p.StyleSizes.Count)
                .Add("dimStylesScaled", p.DimScales.Count).Add("dimensionOverridesScaled", p.DimensionOverrides)
                .Add("notScaled", JsonArray.FromStrings(p.NotScaled));
            return ok;
        }
    }
}
