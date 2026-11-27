using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// textstyle.ensure: creates missing text styles; existing ones are left alone unless update is true.
    /// Font: a name ending in .shx (or .shp) is a shape font file and must be found on the support path; a name ending in
    /// .ttf/.otf is a TrueType file; anything else is a TrueType family name (e.g. "Arial"), set through FontDescriptor.
    /// Height: a non-annotative style stores the model height in drawing units; an annotative style stores the paper
    /// height in paper units (millimetres when MEASUREMENT is metric, inches when imperial), the same convention as
    /// doc.setup applyStandard (see Anno). The readback converts back the same way and reports the height in job units
    /// (the paper height for annotative styles, which a job gives and reads back in the paper unit: mm, or inches for an
    /// imperial job). A height taken from the standard is read in the standard's lengthUnit
    /// (else the job unit).
    /// </summary>
    internal sealed class TextStyleEnsure : OpHandler<List<TextStyleEnsure.StylePlan>>
    {
        public override string Name => "textstyle.ensure";

        internal sealed class StylePlan
        {
            public string Name = "";
            public string Font = "";
            public string? BigFont;
            public bool IsShx;
            public bool IsFontFile;
            /// <summary>Job units.</summary>
            public double Height;
            public double WidthFactor = 1;
            public double ObliqueDegrees;
            public bool Annotative;
            public bool Exists;
            public string State = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            foreach (var p in Plan(ctx, args, issues))
                if (!p.Exists) ctx.Plan.Add(PlannedNames.TextStyle, p.Name);
        }

        private static List<StylePlan> Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = new List<StylePlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool fromStandard = args.GetBool("fromStandard");
            foreach (var item in args.GetObjectList("styles"))
            {
                var p = new StylePlan { Name = item.GetString("name") };
                if (!AcadValues.IsValidSymbolName(p.Name, out string err)) { OpContext.Error(issues, item, "name", IssueCodes.NameInvalid, err); continue; }
                if (!seen.Add(p.Name)) { OpContext.Error(issues, item, "name", IssueCodes.DuplicateStyle, "style '" + p.Name + "' is listed more than once"); continue; }
                TextStyleSpec? std = null;
                if (fromStandard && ctx.Standard != null)
                    std = ctx.Standard.TextStyles.Find(s => string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase));

                p.Font = item.GetString("font");
                p.BigFont = item.GetStringOrNull("bigFont");
                string lower = p.Font.Trim().ToLowerInvariant();
                p.IsShx = lower.EndsWith(".shx", StringComparison.Ordinal) || lower.EndsWith(".shp", StringComparison.Ordinal);
                p.IsFontFile = p.IsShx || lower.EndsWith(".ttf", StringComparison.Ordinal) || lower.EndsWith(".otf", StringComparison.Ordinal) || lower.EndsWith(".ttc", StringComparison.Ordinal);
                if (p.IsShx && Db.FindSupportFile(ctx.Database, p.Font) is null)
                    OpContext.Error(issues, item, "font", IssueCodes.FontMissing, "shape font '" + p.Font + "' was not found on the support path");
                else if (!p.IsFontFile)
                    OpContext.Warning(issues, item, "font", IssueCodes.FontUnverified, "TrueType family '" + p.Font + "' cannot be checked before use; Windows substitutes a default font if it is not installed");
                if (p.BigFont != null && Db.FindSupportFile(ctx.Database, p.BigFont) is null)
                    OpContext.Error(issues, item, "bigFont", IssueCodes.FontMissing, "big font '" + p.BigFont + "' was not found on the support path");
                if (p.BigFont != null && !p.IsShx)
                    OpContext.Error(issues, item, "bigFont", IssueCodes.BigFontNeedsShx, "a big font can only be used with an SHX font");

                p.Annotative = item.Has("annotative") ? item.GetBool("annotative") : std?.Annotative ?? item.GetBool("annotative");
                p.Height = item.Has("height") ? (p.Annotative ? ctx.PaperLength(item, "height") : item.GetLength("height"))
                    : std != null ? Length.Convert(std.Height, ctx.Standard!.LengthUnit ?? ctx.JobUnit, ctx.JobUnit)
                    : item.GetLength("height");
                p.WidthFactor = item.Has("widthFactor") ? item.GetNumber("widthFactor") : std?.WidthFactor ?? item.GetNumber("widthFactor");
                p.ObliqueDegrees = item.Has("obliqueAngle") ? item.GetAngle("obliqueAngle") : std?.ObliqueAngle ?? item.GetAngle("obliqueAngle");
                p.Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.TextStyleTableId, p.Name).IsNull);
                plans.Add(p);
            }
            return plans;
        }

        protected override List<StylePlan> Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plans = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            bool update = args.GetBool("update");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var p in plans)
            {
                var existing = Db.SymbolId(tr, db.TextStyleTableId, p.Name);
                if (!existing.IsNull && !update)
                {
                    p.State = "unchanged";
                    result.Unchanged.Add(OpContext.HandleOf(existing));
                    var t = (TextStyleTableRecord)tr.GetObject(existing, OpenMode.ForRead);
                    bool wasAnnotative = t.Annotative == AnnotativeStates.True;
                    if (!SameFont(t, p) || wasAnnotative != p.Annotative || Math.Abs(Anno.StyleSizeToJob(ctx, t.TextSize, wasAnnotative) - p.Height) > ctx.LengthTolerance)
                        ctx.Warn("Text style '" + p.Name + "' exists with different settings; left unchanged because update is false.");
                    continue;
                }
                TextStyleTableRecord rec;
                if (existing.IsNull)
                {
                    rec = new TextStyleTableRecord { Name = p.Name };
                    var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                    var id = table.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    Set(ctx, rec, p);
                    p.State = "created";
                    result.Created.Add(OpContext.HandleOf(id));
                }
                else
                {
                    rec = (TextStyleTableRecord)tr.GetObject(existing, OpenMode.ForWrite);
                    Set(ctx, rec, p);
                    p.State = "updated";
                    result.Modified.Add(OpContext.HandleOf(existing));
                }
            }
            return plans;
        }

        private static void Set(OpContext ctx, TextStyleTableRecord rec, StylePlan p)
        {
            if (p.IsFontFile)
            {
                rec.FileName = p.Font;
                rec.BigFontFileName = p.BigFont ?? "";
            }
            else
            {
                rec.FileName = "";
                rec.BigFontFileName = "";
                rec.Font = new FontDescriptor(p.Font, false, false, 0, 0);
            }
            rec.TextSize = Anno.StyleSizeFromJob(ctx, p.Height, p.Annotative);
            rec.XScale = p.WidthFactor;
            rec.ObliquingAngle = p.ObliqueDegrees * Math.PI / 180.0;
            // Annotative is an object property that needs the record to be database-resident (it was added above).
            rec.Annotative = p.Annotative ? AnnotativeStates.True : AnnotativeStates.False;
            if (p.Annotative) rec.SetPaperOrientation(true);
        }

        private static bool SameFont(TextStyleTableRecord t, StylePlan p)
        {
            if (p.IsFontFile) return string.Equals(t.FileName ?? "", p.Font, StringComparison.OrdinalIgnoreCase);
            return string.Equals(t.Font.TypeFace ?? "", p.Font, StringComparison.OrdinalIgnoreCase);
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, List<StylePlan> plans)
        {
            var tr = ctx.Transaction;
            bool ok = true;
            var list = new JsonArray();
            foreach (var p in plans)
            {
                var id = Db.SymbolId(tr, ctx.Database.TextStyleTableId, p.Name);
                if (id.IsNull) { ok = Mismatch(result, "Text style '" + p.Name + "' is not in the drawing after the op."); continue; }
                var t = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                bool annotative = t.Annotative == AnnotativeStates.True;
                double height = Anno.StyleSizeToJob(ctx, t.TextSize, annotative);
                double oblique = t.ObliquingAngle * 180.0 / Math.PI;
                list.Add(new JsonObject()
                    .Add("name", t.Name)
                    .Add("handle", OpContext.HandleOf(id))
                    .Add("state", p.State)
                    .Add("fileName", t.FileName ?? "")
                    .Add("bigFontFileName", t.BigFontFileName ?? "")
                    .Add("typeface", t.Font.TypeFace ?? "")
                    .Add("height", annotative ? ctx.PaperOut(height) : AcadValues.Round(height))
                    .Add("widthFactor", AcadValues.Round(t.XScale))
                    .Add("obliqueAngle", AcadValues.Round(oblique))
                    .Add("annotative", annotative));
                if (p.State == "unchanged") continue;
                if (!SameFont(t, p)) ok = Mismatch(result, "Text style '" + p.Name + "': font read back differs.");
                if (Math.Abs(height - p.Height) > ctx.LengthTolerance) ok = Mismatch(result, "Text style '" + p.Name + "': height read back is " + height + ".");
                if (Math.Abs(t.XScale - p.WidthFactor) > 1e-9) ok = Mismatch(result, "Text style '" + p.Name + "': width factor read back differs.");
                if (!Tolerance.AngleEqual(oblique, p.ObliqueDegrees, 1e-6)) ok = Mismatch(result, "Text style '" + p.Name + "': oblique angle read back differs.");
                if (annotative != p.Annotative) ok = Mismatch(result, "Text style '" + p.Name + "': annotative read back is " + annotative + ".");
            }
            result.Readback.Add("styles", list);
            return ok;
        }
    }
}
