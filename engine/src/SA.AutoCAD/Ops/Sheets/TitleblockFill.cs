using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// titleblock.fill: sets attribute values on the title block reference of each layout. Keys are attribute tags or the
    /// standard's field sources ("sheet.number" maps to the standard's SHEET_NO field). Values may use {layout}, {date}
    /// (yyyy-MM-dd, local) and {drawing}. Tags the block does not have are reported, never replaced by loose text.
    /// </summary>
    internal sealed class TitleblockFill : OpHandler<TitleblockFill.Plan>
    {
        public override string Name => "titleblock.fill";

        internal sealed class Change
        {
            public string Layout = "";
            public string Block = "";
            public string Tag = "";
            public ObjectId AttributeId;
            public string Value = "";
            public string Before = "";
        }

        internal sealed class Plan
        {
            public List<Change> Changes = new List<Change>();
            public JsonArray Missing = new JsonArray();
            public JsonArray Layouts = new JsonArray();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            string? block = args.GetStringOrNull("block");
            var common = Values(args.GetMap("values"));
            var per = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in args.GetMap("perLayout"))
                per[kv.Key] = Values(((JsonObject)kv.Value).ToList());
            bool requireAll = args.GetBool("requireAll");

            Db.Read(ctx, tr =>
            {
                var all = SheetDb.PaperLayouts(tr, ctx.Database);
                List<Layout> targets;
                if (args.Has("layouts"))
                {
                    targets = new List<Layout>();
                    foreach (var n in args.GetStringList("layouts"))
                    {
                        var l = all.FirstOrDefault(x => string.Equals(x.LayoutName, n, StringComparison.OrdinalIgnoreCase));
                        if (l is null)
                        {
                            if (!(ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, n)))
                                OpContext.Error(issues, args, "layouts", "layout_missing", "there is no layout named '" + n + "'");
                            continue;
                        }
                        targets.Add(l);
                    }
                }
                else targets = all;
                foreach (var n in per.Keys)
                    if (!targets.Any(t => string.Equals(t.LayoutName, n, StringComparison.OrdinalIgnoreCase)) && !(ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, n)))
                        OpContext.Error(issues, args, "perLayout", "layout_missing", "perLayout names '" + n + "', which is not one of the layouts being filled");

                string drawing = SheetDb.DrawingBaseName(ctx);
                string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                foreach (var layout in targets)
                {
                    var tb = TitleBlocks.Find(tr, ctx, layout, block, out string reason);
                    if (tb is null)
                    {
                        OpContext.Error(issues, args, block != null ? "block" : "layouts", "titleblock_missing", "layout '" + layout.LayoutName + "': " + reason + ". Insert the title block (layout.create titleBlock) rather than adding text.");
                        continue;
                    }
                    var layer = (LayerTableRecord)tr.GetObject(((Entity)tr.GetObject(tb.Id, OpenMode.ForRead)).LayerId, OpenMode.ForRead);
                    if (layer.IsLocked)
                    {
                        OpContext.Error(issues, args, null, "layer_locked", "layout '" + layout.LayoutName + "': the title block is on locked layer '" + layer.Name + "'");
                        continue;
                    }
                    var merged = new List<KeyValuePair<string, string>>(common);
                    if (per.TryGetValue(layout.LayoutName, out var extra)) merged.AddRange(extra);
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in merged) tags[TitleBlocks.TagFor(ctx.Standard, kv.Key)] = kv.Value;
                    var missing = new JsonArray();
                    foreach (var kv in tags)
                    {
                        if (!tb.Attributes.TryGetValue(kv.Key, out var aid)) { missing.Add(kv.Key); continue; }
                        var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead);
                        string value = kv.Value.Replace("{layout}", layout.LayoutName).Replace("{date}", date).Replace("{drawing}", drawing);
                        string before = BlockSupport.ValueOf(ar);
                        if (before != value && SheetDb.IsOnLockedLayer(tr, ar))
                        {
                            OpContext.Error(issues, args, null, "layer_locked", "layout '" + layout.LayoutName + "': attribute " + ar.Tag + " of the title block is on locked layer '" + ar.Layer + "'");
                            continue;
                        }
                        p.Changes.Add(new Change { Layout = layout.LayoutName, Block = tb.Name, Tag = ar.Tag, AttributeId = aid, Value = value, Before = before });
                    }
                    if (missing.Count > 0)
                    {
                        p.Missing.Add(new JsonObject().Add("layout", layout.LayoutName).Add("block", tb.Name).Add("tags", missing));
                        if (ctx.IsValidating)
                            OpContext.Warning(issues, args, null, "attribute_missing", "layout '" + layout.LayoutName + "': block '" + tb.Name + "' has no attribute " + string.Join(", ", missing.Select(m => ((JsonString)m).Value)) + "; those values are not written");
                    }
                    if (requireAll && ctx.Standard != null)
                        foreach (var f in ctx.Standard.TitleBlockFields.Where(f => f.Required))
                        {
                            if (!tb.Attributes.TryGetValue(f.Name, out var aid)) continue;
                            var planned = p.Changes.LastOrDefault(c => c.AttributeId == aid);
                            string final = planned?.Value ?? ((AttributeReference)tr.GetObject(aid, OpenMode.ForRead)).TextString;
                            if (string.IsNullOrWhiteSpace(final))
                                OpContext.Error(issues, args, null, "required_field_empty", "layout '" + layout.LayoutName + "': required title block field " + f.Name + " would stay empty");
                        }
                    p.Layouts.Add(new JsonObject().Add("layout", layout.LayoutName).Add("block", tb.Name).Add("reference", OpContext.HandleOf(tb.Id)));
                }
                return 0;
            });
            return p;
        }

        private static List<KeyValuePair<string, string>> Values(List<KeyValuePair<string, JsonValue>> map) =>
            map.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value is JsonString s ? s.Value : kv.Value.ToJson(false))).ToList();

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var modified = new HashSet<string>();
            foreach (var c in p.Changes)
            {
                if (c.Before == c.Value) continue;
                var ar = (AttributeReference)tr.GetObject(c.AttributeId, OpenMode.ForWrite);
                if (ar.IsMTextAttribute)
                {
                    using (var mt = ar.MTextAttribute)
                    {
                        mt.Contents = c.Value;
                        ar.MTextAttribute = mt;
                    }
                    ar.UpdateMTextAttribute();
                }
                else ar.TextString = c.Value;
                if (modified.Add(OpContext.HandleOf(c.AttributeId))) result.Modified.Add(OpContext.HandleOf(c.AttributeId));
            }
            if (p.Missing.Count > 0) ctx.Warn("Title block attributes not found (values not written): " + p.Missing.ToJson(false));
            var filled = new JsonArray();
            foreach (var c in p.Changes)
                filled.Add(new JsonObject().Add("layout", c.Layout).Add("block", c.Block).Add("tag", c.Tag).Add("value", c.Value));
            result.Outputs.Add("filled", filled);
            result.Outputs.Add("missing", p.Missing.DeepClone());
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            bool ok = true;
            var tr = ctx.Transaction;
            var list = new JsonArray();
            foreach (var c in p.Changes)
            {
                var ar = (AttributeReference)tr.GetObject(c.AttributeId, OpenMode.ForRead);
                string read = BlockSupport.ValueOf(ar);
                list.Add(new JsonObject().Add("layout", c.Layout).Add("block", c.Block).Add("tag", c.Tag).Add("before", c.Before).Add("value", read));
                if (!string.Equals(read, c.Value, StringComparison.Ordinal))
                    ok = Mismatch(result, "layout '" + c.Layout + "': attribute " + c.Tag + " reads back '" + read + "', expected '" + c.Value + "'");
            }
            result.Readback.Add("attributes", list);
            result.Readback.Add("titleBlocks", p.Layouts);
            result.Readback.Add("missing", p.Missing);
            return ok;
        }
    }
}
