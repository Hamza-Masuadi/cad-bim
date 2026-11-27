using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// layout.create (ensure): a new paper space layout built inside the job transaction (a new "*Paper_Space&lt;n&gt;"
    /// block table record, a Layout added with AddToLayoutDictionary, then Layout.Initialize for its paper viewport),
    /// rather than LayoutManager.CreateLayout, whose changes are not known to roll back with the transaction.
    ///
    /// Plot settings come from, in order: a named page setup (this drawing's, else the template's), the template's
    /// layout (read through a side database, never opened in the editor), else defaults; then the job's plotDevice,
    /// paperSize, orientation and plotStyleTable override them. Every value goes through PlotSettingsValidator and is
    /// checked first: the device must be installed, the media offered by it, the style table installed and of the
    /// drawing's plot style kind. Only plot settings are taken from a template; its paper space content is not
    /// cloned (cloning is not known to roll back), and a warning says how many objects were left behind.
    ///
    /// makeCurrent: switching layouts is not part of the transaction, so the runner switches after the job has committed
    /// (still under the job's document lock) and reports it; a failed switch is a warning and the committed work stands.
    /// A rehearsal never switches.
    /// </summary>
    internal sealed class LayoutCreate : OpHandler<LayoutCreate.Plan>
    {
        public override string Name => "layout.create";

        internal sealed class Plan
        {
            public string Name = "";
            public bool Exists;
            public bool Write;
            public string Device = "";
            public string Media = "";
            public string? Orientation;
            public PlotRotation? Rotation;
            public string? StyleSheet;
            public SheetSettings? ScaleFrom;
            public string Source = "defaults";
            public string? TitleBlock;
            public string TitleBlockLayer = "";
            public double TitleBlockScale = 1;
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public ObjectId LayoutId;
            public ObjectId TitleBlockId;
            public string State = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p != null && !p.Exists) ctx.Plan.Add(PlannedNames.Layout, p.Name);
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Name = args.GetString("name") };
            bool update = args.GetBool("update");
            if (!AcadValues.IsValidSymbolName(p.Name, out string nameError))
            {
                OpContext.Error(issues, args, "name", IssueCodes.NameInvalid, nameError);
                return null;
            }
            if (string.Equals(p.Name, "Model", StringComparison.OrdinalIgnoreCase))
            {
                OpContext.Error(issues, args, "name", IssueCodes.NameInvalid, "'Model' is the model space layout");
                return null;
            }
            if (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, p.Name))
            {
                OpContext.Error(issues, args, "name", "duplicate_layout", "an earlier op in this job already creates layout '" + p.Name + "'");
                return null;
            }
            p.Exists = Db.Read(ctx, tr => !SheetDb.LayoutId(tr, ctx.Database, p.Name).IsNull);
            p.Write = !p.Exists || update;
            if (ctx.Standard?.SheetNumbering != null && ctx.IsValidating)
            {
                var sn = ctx.Standard.ParseSheetId(p.Name);
                if (!sn.Valid) OpContext.Warning(issues, args, "name", "sheet_number_invalid", "layout name is not a sheet number under the standard: " + sn.Explain());
            }

            // Source of settings.
            string? template = args.GetStringOrNull("template");
            string? pageSetup = args.GetStringOrNull("pageSetup");
            SheetSettings? src = null;
            if (template != null)
            {
                if (!SheetDb.FileExists(template))
                {
                    OpContext.Error(issues, args, "template", "template_missing", "template '" + template + "' does not exist (give a full path)");
                    return null;
                }
            }
            if (pageSetup != null)
            {
                src = Db.Read(ctx, tr => SideDatabase.PageSetup(tr, ctx.Database, pageSetup));
                if (src != null) p.Source = "pageSetup:" + pageSetup;
                else if (template != null)
                {
                    src = SideDatabase.TemplatePageSetup(template, pageSetup);
                    if (src != null) p.Source = "template pageSetup:" + pageSetup;
                }
                if (src is null)
                {
                    OpContext.Error(issues, args, "pageSetup", "page_setup_missing", "there is no paper space page setup named '" + pageSetup + "' in the drawing" + (template != null ? " or the template" : ""));
                    return null;
                }
            }
            else if (template != null)
            {
                var info = SideDatabase.TemplateLayout(template, args.GetStringOrNull("templateLayout"), out string why);
                if (info is null)
                {
                    OpContext.Error(issues, args, "templateLayout", "template_layout_missing", why);
                    return null;
                }
                src = info.Settings;
                p.Source = "template layout:" + info.LayoutName;
                if (info.PaperEntities > 0 && ctx.IsValidating)
                    OpContext.Warning(issues, args, "template", "template_content_not_copied", "only the plot settings of template layout '" + info.LayoutName + "' are used; its " + info.PaperEntities + " paper space objects are not copied (insert the title block with titleBlock)");
            }

            p.Device = args.Has("plotDevice") || src is null || src.Device.Length == 0 ? args.GetString("plotDevice") : src.Device;
            string? device = PlotSupport.FindDevice(p.Device);
            if (device is null)
            {
                OpContext.Error(issues, args, "plotDevice", "plot_device_missing", "plotter configuration '" + p.Device + "' is not installed");
                return null;
            }
            p.Device = device;
            var media = PlotSupport.MediaFor(p.Device);
            string? wanted = args.GetStringOrNull("paperSize") ?? (src != null && src.Media.Length > 0 ? src.Media : null);
            if (wanted is null)
            {
                OpContext.Error(issues, args, "paperSize", "paper_size_missing", "give paperSize (a canonical media name of " + p.Device + "), a pageSetup or a template");
                return null;
            }
            var hit = PlotSupport.FindMedia(media, wanted);
            if (hit is null)
            {
                OpContext.Error(issues, args, "paperSize", "paper_size_missing", "'" + wanted + "' is not a paper size of " + p.Device + "; similar: " + string.Join(", ", Similar(media, wanted)));
                return null;
            }
            p.Media = hit;
            if (args.Has("orientation") || src is null) p.Orientation = args.GetString("orientation");
            else p.Rotation = src.Rotation;
            p.ScaleFrom = src;
            p.StyleSheet = args.GetStringOrNull("plotStyleTable") ?? (src != null && src.StyleSheet.Length > 0 ? src.StyleSheet : null);
            if (p.StyleSheet != null)
            {
                var problem = PlotSupport.StyleProblem(ctx.Database, p.StyleSheet);
                if (problem != null)
                {
                    OpContext.Error(issues, args, args.Has("plotStyleTable") ? "plotStyleTable" : "pageSetup", "plot_style_invalid", problem);
                    return null;
                }
            }

            // Title block.
            p.TitleBlock = args.GetStringOrNull("titleBlock");
            if (p.TitleBlock != null && !CheckTitleBlock(ctx, args, p, issues)) return null;
            return p;
        }

        private static IEnumerable<string> Similar(List<string> media, string wanted)
        {
            string key = new string(wanted.Where(char.IsLetterOrDigit).Take(8).ToArray());
            var near = media.Where(m => m.IndexOf(key.Length > 4 ? key.Substring(0, 4) : key, StringComparison.OrdinalIgnoreCase) >= 0).Take(8).ToList();
            return near.Count > 0 ? near : media.Take(8);
        }

        private static bool CheckTitleBlock(OpContext ctx, OpArgs args, Plan p, List<Issue> issues)
        {
            string name = p.TitleBlock!;
            var defs = Db.Read(ctx, tr =>
            {
                var id = Db.SymbolId(tr, ctx.Database.BlockTableId, name);
                if (id.IsNull) return null;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout || btr.IsFromExternalReference) return null;
                var tags = new List<string>();
                foreach (ObjectId eid in btr)
                    if (tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad && !ad.Constant) tags.Add(ad.Tag);
                double? mm = DrawingUnits.MillimetresPer((int)btr.Units);
                double? host = DrawingUnits.MillimetresPer((int)ctx.Database.Insunits);
                p.TitleBlockScale = mm.HasValue && host.HasValue ? mm.Value / host.Value : 1.0;
                return tags;
            });
            if (defs is null)
            {
                if (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Block, name)) return true;
                OpContext.Error(issues, args, "titleBlock", "block_missing", "block '" + name + "' is not defined in the drawing (define it first with block.define fromFile)");
                return false;
            }
            if (Math.Abs(p.TitleBlockScale - 1) > 1e-12 && ctx.IsValidating)
                OpContext.Warning(issues, args, "titleBlock", "titleblock_units", "the title block's insertion units differ from the drawing's; it is inserted at scale " + p.TitleBlockScale);
            foreach (var kv in args.GetMap("titleBlockAttributes"))
            {
                string tag = TitleBlocks.TagFor(ctx.Standard, kv.Key);
                if (!defs.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    OpContext.Error(issues, args, "titleBlockAttributes", "attribute_missing", "block '" + name + "' has no attribute '" + tag + "' (it has " + string.Join(", ", defs) + ")");
                    return false;
                }
                p.Attributes[tag] = kv.Value is JsonString s ? s.Value : kv.Value.ToJson(false);
            }
            string? layer = StandardLayers.TitleBlock(ctx.Standard);
            if (layer != null)
            {
                if (!Db.LayerExists(ctx, layer))
                {
                    OpContext.Error(issues, args, "titleBlock", IssueCodes.LayerMissing, "the standard's title block layer '" + layer + "' is not in the drawing; create it with layer.ensure first");
                    return false;
                }
                p.TitleBlockLayer = layer;
            }
            else
            {
                p.TitleBlockLayer = Db.CurrentLayerName(ctx);
                if (ctx.IsValidating)
                    OpContext.Warning(issues, args, "titleBlock", "layer_defaulted", "the standard names no title block layer; the title block goes on the current layer '" + p.TitleBlockLayer + "'");
            }
            return true;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the layout could not be planned.");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            double mmPerUnit = ctx.Units.MmPerUnit ?? throw new InvalidOperationException("The drawing has no defined unit.");

            if (p.Exists)
            {
                p.LayoutId = SheetDb.LayoutId(tr, db, p.Name);
                string h = OpContext.HandleOf(p.LayoutId);
                if (!p.Write)
                {
                    p.State = "unchanged";
                    result.Unchanged.Add(h);
                    var l = (Layout)tr.GetObject(p.LayoutId, OpenMode.ForRead);
                    var diffs = new List<string>();
                    if (!string.Equals(l.PlotConfigurationName, p.Device, StringComparison.OrdinalIgnoreCase)) diffs.Add("plot device " + l.PlotConfigurationName);
                    if (!string.Equals(l.CanonicalMediaName, p.Media, StringComparison.OrdinalIgnoreCase)) diffs.Add("paper " + l.CanonicalMediaName);
                    if (p.StyleSheet != null && !string.Equals(l.CurrentStyleSheet, p.StyleSheet, StringComparison.OrdinalIgnoreCase)) diffs.Add("plot style " + l.CurrentStyleSheet);
                    if (p.Orientation != null && PlotSupport.Orientation(l) != p.Orientation) diffs.Add(PlotSupport.Orientation(l));
                    if (diffs.Count > 0) ctx.Warn("Layout '" + p.Name + "' exists and differs (" + string.Join(", ", diffs) + "); left unchanged because update is false.");
                    if (p.TitleBlock != null && FindTitleBlock(tr, l, p.TitleBlock).IsNull)
                        ctx.Warn("Layout '" + p.Name + "' exists without title block '" + p.TitleBlock + "'; nothing inserted because update is false.");
                    p.TitleBlockId = p.TitleBlock != null ? FindTitleBlock(tr, l, p.TitleBlock) : ObjectId.Null;
                }
                else
                {
                    p.State = "updated";
                    var l = (Layout)tr.GetObject(p.LayoutId, OpenMode.ForWrite);
                    PlotSupport.ApplySheet(l, p.Device, p.Media, p.Orientation, p.Rotation, p.StyleSheet, mmPerUnit, p.ScaleFrom);
                    result.Modified.Add(h);
                    if (p.TitleBlock != null)
                    {
                        p.TitleBlockId = FindTitleBlock(tr, l, p.TitleBlock);
                        if (p.TitleBlockId.IsNull) p.TitleBlockId = InsertTitleBlock(ctx, l, p, result);
                    }
                }
            }
            else
            {
                p.State = "created";
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                var btr = new BlockTableRecord { Name = NextPaperSpaceName(tr, bt) };
                var btrId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
                var layout = new Layout { LayoutName = p.Name, TabOrder = NextTabOrder(tr, db) };
                layout.AddToLayoutDictionary(db, btrId);
                p.LayoutId = layout.ObjectId;
                tr.AddNewlyCreatedDBObject(layout, true);
                if (btr.LayoutId != p.LayoutId) btr.LayoutId = p.LayoutId;
                PlotSupport.ApplySheet(layout, p.Device, p.Media, p.Orientation, p.Rotation, p.StyleSheet, mmPerUnit, p.ScaleFrom);
                layout.Initialize();
                result.Created.Add(OpContext.HandleOf(btrId));
                result.Created.Add(OpContext.HandleOf(p.LayoutId));
                if (p.TitleBlock != null) p.TitleBlockId = InsertTitleBlock(ctx, layout, p, result);
            }
            if (args.GetBool("makeCurrent"))
            {
                if (ctx.Mode == RunMode.Commit) ctx.LayoutAfterCommit = p.Name;
                else ctx.Warn("makeCurrent: a rehearsal does not switch layouts; the commit switches to '" + p.Name + "' after it has committed.");
                result.Readback.Add("makeCurrent", "after commit");
            }
            result.Outputs.Add("layout", OpContext.HandleOf(p.LayoutId));
            result.Outputs.Add("titleBlock", p.TitleBlockId.IsNull ? JsonValue.Null : (JsonValue)OpContext.HandleOf(p.TitleBlockId));
            return p;
        }

        private static string NextPaperSpaceName(Transaction tr, BlockTable bt)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in bt)
            {
                var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                used.Add(b.Name);
            }
            for (int i = 0; ; i++)
            {
                string n = BlockTableRecord.PaperSpace + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Contains(n)) return n;
            }
        }

        private static int NextTabOrder(Transaction tr, Database db) =>
            SheetDb.PaperLayouts(tr, db).Select(l => l.TabOrder).DefaultIfEmpty(0).Max() + 1;

        private static ObjectId FindTitleBlock(Transaction tr, Layout layout, string block)
        {
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (!id.IsErased && id.ObjectClass.DxfName == "INSERT" && tr.GetObject(id, OpenMode.ForRead) is BlockReference br &&
                    string.Equals(SheetDb.EffectiveName(tr, br), block, StringComparison.OrdinalIgnoreCase)) return id;
            return ObjectId.Null;
        }

        /// <summary>Inserts the title block at the paper origin, scale from the block's units, with attribute references from its definitions.</summary>
        private static ObjectId InsertTitleBlock(OpContext ctx, Layout layout, Plan p, OpResult result)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var defId = Db.SymbolId(tr, db.BlockTableId, p.TitleBlock!);
            if (defId.IsNull) throw new InvalidOperationException("Block '" + p.TitleBlock + "' is not defined.");
            var def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            var br = new BlockReference(Point3d.Origin, defId);
            br.SetDatabaseDefaults(db);
            br.Layer = p.TitleBlockLayer;
            br.ScaleFactors = new Scale3d(p.TitleBlockScale);
            var id = Db.Append(ctx, space, br);
            BlockSupport.AddAttributes(ctx, br, def, p.Attributes);
            result.Created.Add(OpContext.HandleOf(id));
            return id;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var l = (Layout)tr.GetObject(p.LayoutId, OpenMode.ForRead);
            var read = SheetSettings.From(l);
            string orientation = PlotSupport.Orientation(l);
            var o = read.ToJson().Add("name", l.LayoutName).Add("state", p.State).Add("source", p.Source).Add("orientation", orientation)
                .Add("paperSizeMm", new JsonArray(AcadValues.Round(l.PlotPaperSize.X, 3), AcadValues.Round(l.PlotPaperSize.Y, 3)))
                .Add("hasPaperViewport", SheetDb.HasAnyViewport(tr, l));
            result.Readback.Add("layout", o);
            bool ok = true;
            if (!string.Equals(l.LayoutName, p.Name, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "layout name reads back '" + l.LayoutName + "'");
            if (p.State == "unchanged") return ok;
            if (!string.Equals(read.Device, p.Device, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "plot device reads back '" + read.Device + "', expected '" + p.Device + "'");
            if (!string.Equals(read.Media, p.Media, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "paper size reads back '" + read.Media + "', expected '" + p.Media + "'");
            if (p.StyleSheet != null && !string.Equals(read.StyleSheet, p.StyleSheet, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "plot style table reads back '" + read.StyleSheet + "'");
            if (p.Orientation != null && orientation != p.Orientation) ok = Mismatch(result, "orientation reads back " + orientation + ", expected " + p.Orientation);
            if (p.Rotation.HasValue && p.Orientation is null && read.Rotation != p.Rotation.Value) ok = Mismatch(result, "plot rotation reads back " + read.Rotation);
            if (l.PlotType != PlotType.Layout) ok = Mismatch(result, "plot type reads back " + l.PlotType + ", expected Layout");
            if (!SheetDb.HasAnyViewport(tr, l)) ok = Mismatch(result, "the layout has no paper space viewport after Initialize");
            if (p.TitleBlock != null)
            {
                if (p.TitleBlockId.IsNull) ok = Mismatch(result, "title block '" + p.TitleBlock + "' is not on the layout");
                else
                {
                    var br = (BlockReference)tr.GetObject(p.TitleBlockId, OpenMode.ForRead);
                    var attrs = new JsonObject();
                    foreach (ObjectId aid in br.AttributeCollection)
                    {
                        var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead);
                        string value = BlockSupport.ValueOf(ar);
                        attrs.Set(ar.Tag, value);
                        if (p.Attributes.TryGetValue(ar.Tag, out var want) && value != want) ok = Mismatch(result, "title block attribute " + ar.Tag + " reads back '" + value + "'");
                    }
                    result.Readback.Add("titleBlock", new JsonObject().Add("handle", OpContext.HandleOf(p.TitleBlockId)).Add("block", SheetDb.EffectiveName(tr, br))
                        .Add("layer", br.Layer).Add("position", DrawingUnits.PointJson(ctx.Units.ToJob(br.Position))).Add("scale", AcadValues.Round(br.ScaleFactors.X)).Add("attributes", attrs));
                    if (br.Position.DistanceTo(Point3d.Origin) > ctx.Units.ToDrawing(ctx.LengthTolerance)) ok = Mismatch(result, "title block is not at the paper origin");
                }
            }
            return ok;
        }
    }
}
