using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// entity.setprops: layer, colour, linetype, lineweight and linetype scale on entities named by handle or found by a
    /// filter (layer wildcard, DXF type, block name, space). Listed handles on locked layers are an error; filtered
    /// entities on locked layers are skipped and counted. Moving content to layer 0 or Defpoints is refused (layer 0
    /// loses layer control; Defpoints never plots). Setting an explicit colour, linetype or lineweight is allowed but
    /// warned about, since the standard wants ByLayer.
    /// </summary>
    internal sealed class EntitySetProps : OpHandler<EntitySetProps.Plan>
    {
        public override string Name => "entity.setprops";

        internal sealed class Plan
        {
            public List<ObjectId> Ids = new List<ObjectId>();
            public int SkippedLocked;
            public string? Layer;
            public Color? Color;
            public string? Linetype;
            public LinetypeDefinition? LinetypeDef;
            public LineWeight? LineWeight;
            public double? LtScale;
            public ObjectId LinetypeId;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            p.Layer = args.GetStringOrNull("setLayer");
            if (p.Layer != null)
            {
                if (SheetDb.IsProtectedLayer(p.Layer))
                    OpContext.Error(issues, args, "setLayer", "layer_not_allowed", "content is not moved to layer 0 or Defpoints (layer 0 loses layer control; Defpoints never plots)");
                else if (!ctx.IsRef(args, "setLayer") && !Db.LayerExists(ctx, p.Layer))
                    OpContext.Error(issues, args, "setLayer", IssueCodes.LayerMissing, "layer '" + p.Layer + "' is not in the drawing; create it with layer.ensure first");
            }
            if (args.Has("color"))
            {
                if (AcadValues.TryParseColor(args.Get("color"), true, out var c, out string err)) p.Color = c;
                else OpContext.Error(issues, args, "color", IssueCodes.ColorInvalid, err);
            }
            p.Linetype = args.GetStringOrNull("linetype");
            if (p.Linetype != null)
            {
                string? problem = LinetypeSupport.Check(ctx, p.Linetype, out p.LinetypeDef);
                if (problem != null) OpContext.Error(issues, args, "linetype", IssueCodes.LinetypeMissing, problem);
                else if (p.LinetypeDef != null && ctx.IsValidating) ctx.Plan.Add(PlannedNames.Linetype, p.Linetype);
            }
            if (args.Has("lineweight"))
            {
                if (AcadValues.TryParseLineWeight(args.Get("lineweight"), false, out var lw, out string err)) p.LineWeight = lw;
                else OpContext.Error(issues, args, "lineweight", IssueCodes.LineweightInvalid, err);
            }
            p.LtScale = args.GetNumberOrNull("linetypeScale");
            if (ctx.IsValidating)
            {
                var hard = new List<string>();
                if (p.Color != null && !p.Color.IsByLayer) hard.Add("colour");
                if (p.Linetype != null && !string.Equals(p.Linetype, "bylayer", StringComparison.OrdinalIgnoreCase)) hard.Add("linetype");
                if (p.LineWeight.HasValue && p.LineWeight.Value != LineWeight.ByLayer) hard.Add("lineweight");
                if (hard.Count > 0)
                    OpContext.Warning(issues, args, null, "not_bylayer", "setting " + string.Join(", ", hard) + " on the object rather than ByLayer; the standard expects ByLayer properties");
            }

            if (args.Has("handles"))
            {
                if (ctx.IsRef(args, "handles")) return p;
                var seen = new HashSet<ObjectId>();
                foreach (var h in args.GetRefIds("handles"))
                {
                    var id = ctx.IdOf(h);
                    if (id.IsNull) { OpContext.Error(issues, args, "handles", "handle_missing", "handle " + h + " is not in the drawing"); continue; }
                    bool entity = Db.Read(ctx, tr => tr.GetObject(id, OpenMode.ForRead) is Entity);
                    if (!entity) { OpContext.Error(issues, args, "handles", "not_entity", "handle " + h + " is not an entity"); continue; }
                    bool locked = Db.Read(ctx, tr => SheetDb.IsOnLockedLayer(tr, (Entity)tr.GetObject(id, OpenMode.ForRead)));
                    if (locked) { OpContext.Error(issues, args, "handles", "layer_locked", "entity " + h + " is on a locked layer"); continue; }
                    if (seen.Add(id)) p.Ids.Add(id);
                }
            }
            else
            {
                var f = args.GetObject("filter")!;
                string space = f.GetString("space");
                var layerRx = f.GetStringOrNull("layer") is string lw ? SheetDb.Wildcard(lw) : null;
                string? type = f.GetStringOrNull("type")?.Trim().ToUpperInvariant();
                string? block = f.GetStringOrNull("block");
                if (block != null && type != null && type != "INSERT")
                    OpContext.Error(issues, f, "block", "filter_invalid", "block filters apply to INSERT entities");
                Db.Read(ctx, tr =>
                {
                    foreach (var s in SheetDb.Spaces(tr, ctx.Database, space, false, false))
                    {
                        var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                        {
                            if (id.IsErased) continue;
                            string dxf = id.ObjectClass.DxfName;
                            if (type != null && !string.Equals(dxf, type, StringComparison.OrdinalIgnoreCase)) continue;
                            if (block != null && dxf != "INSERT") continue;
                            var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            if (e is Viewport) continue;
                            if (layerRx != null && !layerRx.IsMatch(e.Layer)) continue;
                            if (block != null && !(e is BlockReference br && string.Equals(SheetDb.EffectiveName(tr, br), block, StringComparison.OrdinalIgnoreCase))) continue;
                            if (SheetDb.IsOnLockedLayer(tr, e)) { p.SkippedLocked++; continue; }
                            p.Ids.Add(id);
                        }
                    }
                    return 0;
                });
                if (ctx.IsValidating && p.Ids.Count == 0)
                    OpContext.Warning(issues, args, "filter", "nothing_selected", "the filter selects no entities");
                if (ctx.IsValidating && p.SkippedLocked > 0)
                    OpContext.Warning(issues, args, "filter", "skipped_locked", p.SkippedLocked + " matching entities are on locked layers and will be skipped");
            }
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            if (p.Linetype != null && !LinetypeSupport.IsBuiltIn(p.Linetype))
            {
                p.LinetypeId = LinetypeSupport.Ensure(ctx, p.Linetype, p.LinetypeDef, out string? created);
                if (created != null) result.Created.Add(created);
            }
            ObjectId layerId = p.Layer != null ? Db.SymbolId(tr, db.LayerTableId, p.Layer) : ObjectId.Null;
            if (p.Layer != null && layerId.IsNull) throw new InvalidOperationException("Layer '" + p.Layer + "' does not exist.");
            foreach (var id in p.Ids)
            {
                var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                if (!layerId.IsNull) e.LayerId = layerId;
                if (p.Color != null) e.Color = p.Color;
                if (p.Linetype != null)
                {
                    if (string.Equals(p.Linetype, "bylayer", StringComparison.OrdinalIgnoreCase)) e.LinetypeId = db.ByLayerLinetype;
                    else if (string.Equals(p.Linetype, "byblock", StringComparison.OrdinalIgnoreCase)) e.LinetypeId = db.ByBlockLinetype;
                    else if (string.Equals(p.Linetype, "continuous", StringComparison.OrdinalIgnoreCase)) e.LinetypeId = db.ContinuousLinetype;
                    else e.LinetypeId = p.LinetypeId;
                }
                if (p.LineWeight.HasValue) e.LineWeight = p.LineWeight.Value;
                if (p.LtScale.HasValue) e.LinetypeScale = p.LtScale.Value;
                result.Modified.Add(OpContext.HandleOf(id));
            }
            result.Outputs.Add("count", p.Ids.Count);
            if (p.SkippedLocked > 0) ctx.Warn(p.SkippedLocked + " entities on locked layers were skipped.");
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            int bad = 0;
            string first = "";
            foreach (var id in p.Ids)
            {
                var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                var why = new List<string>();
                if (p.Layer != null && !string.Equals(e.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) why.Add("layer");
                if (p.Color != null && !AcadValues.SameColor(e.Color, p.Color)) why.Add("colour");
                if (p.Linetype != null && !string.Equals(e.Linetype, p.Linetype, StringComparison.OrdinalIgnoreCase)) why.Add("linetype " + e.Linetype);
                if (p.LineWeight.HasValue && e.LineWeight != p.LineWeight.Value) why.Add("lineweight");
                if (p.LtScale.HasValue && Math.Abs(e.LinetypeScale - p.LtScale.Value) > 1e-9) why.Add("linetype scale");
                if (why.Count > 0) { bad++; if (first.Length == 0) first = OpContext.HandleOf(id) + ": " + string.Join(", ", why); }
            }
            result.Readback.Add("count", p.Ids.Count).Add("matching", p.Ids.Count - bad).Add("skippedLocked", p.SkippedLocked);
            return bad == 0 || Mismatch(result, bad + " entities read back differently (first " + first + ")");
        }
    }
}
