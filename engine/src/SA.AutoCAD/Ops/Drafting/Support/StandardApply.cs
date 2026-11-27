using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using SA.AutoCAD.Ops.Common;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>One named object of the standard and what happened to it.</summary>
    internal sealed class StdItem
    {
        public string Kind = "";
        public string Name = "";
        /// <summary>created, matching (exists and equals the standard), different (exists, differs, left alone), updated.</summary>
        public string State = "";
        public List<string> Differences = new List<string>();
        public string Handle = "";
        public LayerSpec? Layer;
        public LinetypeDefinition? LinetypeDef;
        public TextStyleSpec? TextStyle;
        public DimStylePlan? DimStyle;
        public DimStyleSpec? DimSpec;
        public bool Exists;
    }

    /// <summary>
    /// doc.setup's applyStandard: every linetype (needed by the standard's layers), layer, text style and dimension
    /// style the standard defines. Non-destructive: an existing object is compared and reported as matching or
    /// different, and is only written when update is true. Text style heights are read in the standard's length unit
    /// (else the job unit) and written as paper units for annotative styles, drawing units otherwise.
    /// </summary>
    internal static class StandardApply
    {
        internal static List<StdItem> Plan(OpContext ctx, ProjectStandard std, double mmPerDrawingUnit, List<string> errors, List<string> warnings)
        {
            var items = new List<StdItem>();
            LengthUnit stdUnit = std.LengthUnit ?? ctx.JobUnit;
            var linetypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in std.Layers)
            {
                var it = new StdItem { Kind = "layer", Name = l.Name, Layer = l };
                if (!AcadValues.IsValidSymbolName(l.Name, out string err)) { errors.Add("standard layer: " + err); continue; }
                it.Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.LayerTableId, l.Name).IsNull);
                if (l.Lineweight.HasValue && !AcadValues.TryParseLineWeight(l.Lineweight.Value, true, out _, out string lwErr)) errors.Add("standard layer '" + l.Name + "': " + lwErr);
                items.Add(it);
                if (!LinetypeSupport.IsBuiltIn(l.Linetype) && linetypes.Add(l.Linetype))
                {
                    string? problem = LinetypeSupport.Check(ctx, l.Linetype, out var def);
                    if (problem != null) errors.Add("standard layer '" + l.Name + "': " + problem);
                    else if (def != null) items.Insert(0, new StdItem { Kind = "linetype", Name = l.Linetype, LinetypeDef = def });
                }
            }
            foreach (var t in std.TextStyles)
            {
                if (!AcadValues.IsValidSymbolName(t.Name, out string err)) { errors.Add("standard text style: " + err); continue; }
                string lower = t.Font.Trim().ToLowerInvariant();
                if ((lower.EndsWith(".shx", StringComparison.Ordinal) || lower.EndsWith(".shp", StringComparison.Ordinal)) && Db.FindSupportFile(ctx.Database, t.Font) is null)
                    errors.Add("standard text style '" + t.Name + "': shape font '" + t.Font + "' was not found on the support path");
                items.Add(new StdItem { Kind = "textstyle", Name = t.Name, TextStyle = t, Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.TextStyleTableId, t.Name).IsNull) });
            }
            foreach (var d in std.DimStyles)
            {
                var errs = new List<string>();
                var p = DimStyles.Plan(ctx, d.Name, null, d.Annotative, new[] { new DimSettingsSource(d.Settings, stdUnit, "standard dimension style '" + d.Name + "'") }, mmPerDrawingUnit, errs, warnings);
                // A DIMTXSTY naming a text style the standard itself defines is fine: it is created first.
                foreach (var e in errs)
                    if (!(e.Contains("DIMTXSTY") && std.TextStyles.Any(t => e.Contains("'" + t.Name + "'")))) errors.Add(e);
                items.Add(new StdItem { Kind = "dimstyle", Name = d.Name, DimStyle = p, DimSpec = d, Exists = p.Exists });
            }
            return items;
        }

        internal static void Execute(OpContext ctx, List<StdItem> items, bool update, double mmPerDrawingUnit, LengthUnit stdUnit, List<string> created, List<string> modified, List<string> unchanged)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var it in items)
            {
                switch (it.Kind)
                {
                    case "linetype":
                    {
                        var id = LinetypeSupport.Ensure(ctx, it.Name, it.LinetypeDef, out string? h);
                        it.Handle = OpContext.HandleOf(id);
                        if (h != null) { it.State = "created"; created.Add(h); }
                        else { it.State = "matching"; unchanged.Add(it.Handle); }
                        break;
                    }
                    case "layer":
                    {
                        var l = it.Layer!;
                        var existing = Db.SymbolId(tr, db.LayerTableId, l.Name);
                        if (!existing.IsNull)
                        {
                            it.Handle = OpContext.HandleOf(existing);
                            it.Differences = LayerDifferences(tr, existing, l);
                            if (it.Differences.Count == 0) { it.State = "matching"; unchanged.Add(it.Handle); break; }
                            if (!update) { it.State = "different"; unchanged.Add(it.Handle); break; }
                            WriteLayer(ctx, (LayerTableRecord)tr.GetObject(existing, OpenMode.ForWrite), l);
                            it.State = "updated";
                            modified.Add(it.Handle);
                            break;
                        }
                        var rec = new LayerTableRecord { Name = l.Name };
                        WriteLayer(ctx, rec, l);
                        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                        var nid = table.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                        it.Handle = OpContext.HandleOf(nid);
                        it.State = "created";
                        created.Add(it.Handle);
                        break;
                    }
                    case "textstyle":
                    {
                        var t = it.TextStyle!;
                        var existing = Db.SymbolId(tr, db.TextStyleTableId, t.Name);
                        if (!existing.IsNull)
                        {
                            it.Handle = OpContext.HandleOf(existing);
                            it.Differences = TextStyleDifferences(ctx, (TextStyleTableRecord)tr.GetObject(existing, OpenMode.ForRead), t, mmPerDrawingUnit, stdUnit);
                            if (it.Differences.Count == 0) { it.State = "matching"; unchanged.Add(it.Handle); break; }
                            if (!update) { it.State = "different"; unchanged.Add(it.Handle); break; }
                            WriteTextStyle(ctx, (TextStyleTableRecord)tr.GetObject(existing, OpenMode.ForWrite), t, mmPerDrawingUnit, stdUnit);
                            it.State = "updated";
                            modified.Add(it.Handle);
                            break;
                        }
                        var rec = new TextStyleTableRecord { Name = t.Name };
                        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                        var nid = table.Add(rec);
                        tr.AddNewlyCreatedDBObject(rec, true);
                        WriteTextStyle(ctx, rec, t, mmPerDrawingUnit, stdUnit);
                        it.Handle = OpContext.HandleOf(nid);
                        it.State = "created";
                        created.Add(it.Handle);
                        break;
                    }
                    case "dimstyle":
                    {
                        // Re-planned now: text styles the standard defines (DIMTXSTY) exist only from this point on.
                        var errs = new List<string>();
                        var warns = new List<string>();
                        var d = it.DimSpec!;
                        var p = DimStyles.Plan(ctx, d.Name, null, d.Annotative, new[] { new DimSettingsSource(d.Settings, stdUnit, "standard dimension style '" + d.Name + "'") }, mmPerDrawingUnit, errs, warns);
                        if (errs.Count > 0) throw new InvalidOperationException(string.Join("; ", errs));
                        it.DimStyle = p;
                        var existing = Db.SymbolId(tr, db.DimStyleTableId, p.Name);
                        if (!existing.IsNull)
                        {
                            it.Handle = OpContext.HandleOf(existing);
                            it.Differences = DimStyles.Differences(ctx, tr, existing, p);
                            if (it.Differences.Count == 0) { it.State = "matching"; p.State = "unchanged"; unchanged.Add(it.Handle); break; }
                            if (!update) { it.State = "different"; p.State = "unchanged"; unchanged.Add(it.Handle); break; }
                            DimStyles.Update(ctx, p, existing);
                            it.State = "updated";
                            p.State = "updated";
                            modified.Add(it.Handle);
                            break;
                        }
                        var nid = DimStyles.Create(ctx, p);
                        it.Handle = OpContext.HandleOf(nid);
                        it.State = "created";
                        p.State = "created";
                        created.Add(it.Handle);
                        break;
                    }
                }
            }
        }

        private static void WriteLayer(OpContext ctx, LayerTableRecord rec, LayerSpec l)
        {
            rec.Color = AcadValues.ToColor(l.Color);
            var lt = LinetypeSupport.IsBuiltIn(l.Linetype) ? Db.SymbolId(ctx.Transaction, ctx.Database.LinetypeTableId, "Continuous") : Db.SymbolId(ctx.Transaction, ctx.Database.LinetypeTableId, l.Linetype);
            if (lt.IsNull) throw new InvalidOperationException("Linetype '" + l.Linetype + "' for layer '" + l.Name + "' is missing.");
            rec.LinetypeObjectId = lt;
            if (l.Lineweight.HasValue && AcadValues.TryParseLineWeight(l.Lineweight.Value, true, out var lw, out _)) rec.LineWeight = lw;
            else rec.LineWeight = LineWeight.ByLineWeightDefault;
            rec.IsPlottable = l.Plot;
            if (l.Description != null) rec.Description = l.Description;
            if (l.Transparency > 0) rec.Transparency = AcadValues.ToTransparency(l.Transparency);
        }

        internal static List<string> LayerDifferences(Transaction tr, ObjectId id, LayerSpec l)
        {
            var d = new List<string>();
            var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!AcadValues.SameColor(rec.Color, AcadValues.ToColor(l.Color))) d.Add("color " + AcadValues.ColorJson(rec.Color) + " (standard " + l.Color + ")");
            var lt = (LinetypeTableRecord)tr.GetObject(rec.LinetypeObjectId, OpenMode.ForRead);
            if (!string.Equals(lt.Name, l.Linetype, StringComparison.OrdinalIgnoreCase)) d.Add("linetype " + lt.Name + " (standard " + l.Linetype + ")");
            LineWeight want = LineWeight.ByLineWeightDefault;
            if (l.Lineweight.HasValue) AcadValues.TryParseLineWeight(l.Lineweight.Value, true, out want, out _);
            if (rec.LineWeight != want) d.Add("lineweight " + AcadValues.LineWeightJson(rec.LineWeight) + " (standard " + AcadValues.LineWeightJson(want) + ")");
            if (rec.IsPlottable != l.Plot) d.Add("plot " + rec.IsPlottable + " (standard " + l.Plot + ")");
            int tp = AcadValues.TransparencyPercent(rec.Transparency) ?? 0;
            if (tp != l.Transparency) d.Add("transparency " + tp + " (standard " + l.Transparency + ")");
            return d;
        }

        private static bool IsFontFile(string font)
        {
            string lower = font.Trim().ToLowerInvariant();
            return lower.EndsWith(".shx", StringComparison.Ordinal) || lower.EndsWith(".shp", StringComparison.Ordinal) ||
                   lower.EndsWith(".ttf", StringComparison.Ordinal) || lower.EndsWith(".otf", StringComparison.Ordinal) || lower.EndsWith(".ttc", StringComparison.Ordinal);
        }

        /// <summary>The style's TextSize: paper units for annotative styles, drawing units otherwise.</summary>
        private static double TextSize(OpContext ctx, TextStyleSpec t, double mmPerDrawingUnit, LengthUnit stdUnit)
        {
            double mm = Length.Convert(t.Height, stdUnit, LengthUnit.Millimetre);
            return t.Annotative ? mm / Anno.PaperUnitMm(ctx.Database) : mm / mmPerDrawingUnit;
        }

        private static void WriteTextStyle(OpContext ctx, TextStyleTableRecord rec, TextStyleSpec t, double mmPerDrawingUnit, LengthUnit stdUnit)
        {
            if (IsFontFile(t.Font)) { rec.FileName = t.Font; rec.BigFontFileName = ""; }
            else { rec.FileName = ""; rec.BigFontFileName = ""; rec.Font = new FontDescriptor(t.Font, false, false, 0, 0); }
            rec.TextSize = TextSize(ctx, t, mmPerDrawingUnit, stdUnit);
            rec.XScale = t.WidthFactor;
            rec.ObliquingAngle = t.ObliqueAngle * Math.PI / 180.0;
            rec.Annotative = t.Annotative ? AnnotativeStates.True : AnnotativeStates.False;
            if (t.Annotative) rec.SetPaperOrientation(true);
        }

        internal static List<string> TextStyleDifferences(OpContext ctx, TextStyleTableRecord rec, TextStyleSpec t, double mmPerDrawingUnit, LengthUnit stdUnit)
        {
            var d = new List<string>();
            bool sameFont = IsFontFile(t.Font)
                ? string.Equals(rec.FileName ?? "", t.Font, StringComparison.OrdinalIgnoreCase)
                : string.Equals(rec.Font.TypeFace ?? "", t.Font, StringComparison.OrdinalIgnoreCase);
            if (!sameFont) d.Add("font " + (string.IsNullOrEmpty(rec.FileName) ? rec.Font.TypeFace : rec.FileName) + " (standard " + t.Font + ")");
            double size = TextSize(ctx, t, mmPerDrawingUnit, stdUnit);
            if (Math.Abs(rec.TextSize - size) > 1e-6 * Math.Max(1, size)) d.Add("height " + AcadValues.Round(rec.TextSize, 6) + " (standard " + AcadValues.Round(size, 6) + ")");
            if (Math.Abs(rec.XScale - t.WidthFactor) > 1e-9) d.Add("width factor " + rec.XScale + " (standard " + t.WidthFactor + ")");
            if (Math.Abs(rec.ObliquingAngle - t.ObliqueAngle * Math.PI / 180.0) > 1e-9) d.Add("oblique angle");
            if (Anno.IsAnnotative(rec) != t.Annotative) d.Add("annotative " + Anno.IsAnnotative(rec) + " (standard " + t.Annotative + ")");
            return d;
        }

        /// <summary>Re-reads every created or updated item; mismatches are returned as text.</summary>
        internal static JsonObject Verify(OpContext ctx, List<StdItem> items, double mmPerDrawingUnit, LengthUnit stdUnit, List<string> mismatches)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var created = new JsonArray();
            var matching = new JsonArray();
            var updated = new JsonArray();
            var different = new JsonArray();
            foreach (var it in items)
            {
                string label = it.Kind + " '" + it.Name + "'";
                var entry = new JsonObject().Add("kind", it.Kind).Add("name", it.Name).Add("handle", it.Handle);
                switch (it.State)
                {
                    case "created": created.Add(entry); break;
                    case "updated": updated.Add(entry); break;
                    case "matching": matching.Add(entry); break;
                    default: different.Add(entry.Add("differences", JsonArray.FromStrings(it.Differences))); break;
                }
                if (it.State != "created" && it.State != "updated") continue;
                List<string> diff;
                switch (it.Kind)
                {
                    case "layer":
                    {
                        var id = Db.SymbolId(tr, db.LayerTableId, it.Name);
                        diff = id.IsNull ? new List<string> { "missing" } : LayerDifferences(tr, id, it.Layer!);
                        break;
                    }
                    case "textstyle":
                    {
                        var id = Db.SymbolId(tr, db.TextStyleTableId, it.Name);
                        diff = id.IsNull ? new List<string> { "missing" } : TextStyleDifferences(ctx, (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead), it.TextStyle!, mmPerDrawingUnit, stdUnit);
                        break;
                    }
                    case "dimstyle":
                    {
                        var id = Db.SymbolId(tr, db.DimStyleTableId, it.Name);
                        diff = id.IsNull ? new List<string> { "missing" } : DimStyles.Differences(ctx, tr, id, it.DimStyle!);
                        break;
                    }
                    default:
                    {
                        diff = Db.SymbolId(tr, db.LinetypeTableId, it.Name).IsNull ? new List<string> { "missing" } : new List<string>();
                        break;
                    }
                }
                if (diff.Count > 0) mismatches.Add(label + " reads back differently from the standard: " + string.Join("; ", diff));
            }
            return new JsonObject().Add("created", created).Add("matching", matching).Add("different", different).Add("updated", updated);
        }
    }
}
