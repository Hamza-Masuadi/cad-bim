using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// layer.ensure: creates missing layers; an existing layer is left untouched (reported as unchanged, with any
    /// differences as a warning) unless update is true. Missing simple linetypes are created from the default .lin file.
    /// </summary>
    internal sealed class LayerEnsure : OpHandler<List<LayerEnsure.LayerPlan>>
    {
        public override string Name => "layer.ensure";

        internal sealed class LayerPlan
        {
            public string Name = "";
            public string Path = "";
            public Color Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
            public string Linetype = "Continuous";
            public LinetypeDefinition? LinetypeDef;
            public LineWeight? LineWeight;
            public bool Plot = true;
            public string? Description;
            public int? Transparency;
            public bool Off, Frozen, Locked;
            public bool Exists;
            public string State = "";
            public string Handle = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = Plan(ctx, args, issues);
            string? current = args.GetStringOrNull("setCurrent");
            if (current != null && !ctx.IsRef(args, "setCurrent"))
            {
                var p = plans.Find(x => string.Equals(x.Name, current, StringComparison.OrdinalIgnoreCase));
                if (p is null && !Db.LayerExists(ctx, current))
                    OpContext.Error(issues, args, "setCurrent", IssueCodes.LayerMissing, "setCurrent names layer '" + current + "', which is not in the drawing or in this op");
                else if (p != null && p.Frozen && (!p.Exists || args.GetBool("update")))
                    OpContext.Error(issues, args, "setCurrent", IssueCodes.LayerFrozen, "layer '" + current + "' cannot be both frozen and current");
                else if (p is null || (p.Exists && !args.GetBool("update")))
                {
                    bool frozen = Db.Read(ctx, tr =>
                    {
                        var id = Db.SymbolId(tr, ctx.Database.LayerTableId, current);
                        return !id.IsNull && ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsFrozen;
                    });
                    if (frozen) OpContext.Error(issues, args, "setCurrent", IssueCodes.LayerFrozen, "layer '" + current + "' is frozen and cannot be made current");
                }
            }
            foreach (var p in plans)
                if (!p.Exists) ctx.Plan.Add(PlannedNames.Layer, p.Name);
        }

        private static List<LayerPlan> Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = new List<LayerPlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool fromStandard = args.GetBool("fromStandard");
            bool update = args.GetBool("update");
            string currentLayer = Db.CurrentLayerName(ctx);
            foreach (var item in args.GetObjectList("layers"))
            {
                var p = new LayerPlan { Name = item.GetString("name"), Path = item.Path };
                if (!AcadValues.IsValidSymbolName(p.Name, out string nameError))
                {
                    OpContext.Error(issues, item, "name", IssueCodes.NameInvalid, nameError);
                    continue;
                }
                if (!seen.Add(p.Name))
                {
                    OpContext.Error(issues, item, "name", IssueCodes.DuplicateLayer, "layer '" + p.Name + "' is listed more than once");
                    continue;
                }
                if (ctx.Standard?.LayerScheme != null)
                {
                    var check = ctx.Standard.ParseLayerName(p.Name);
                    if (!check.Valid)
                        OpContext.Error(issues, item, "name", IssueCodes.LayerNameInvalid, "layer '" + p.Name + "' does not follow the standard's naming scheme: " + check.Explain());
                }
                LayerSpec? std = fromStandard ? ctx.Standard?.FindLayer(p.Name) : null;

                if (item.Has("color"))
                {
                    if (AcadValues.TryParseColor(item.Get("color"), false, out var c, out string err)) p.Color = c!;
                    else OpContext.Error(issues, item, "color", IssueCodes.ColorInvalid, err);
                }
                else if (std != null) p.Color = AcadValues.ToColor(std.Color);

                p.Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.LayerTableId, p.Name).IsNull);
                p.Linetype = item.Has("linetype") ? item.GetString("linetype") : std?.Linetype ?? item.GetString("linetype");
                if (!p.Exists || update)
                {
                    // Only a layer that will be written needs its linetype; an existing layer left alone does not.
                    string? ltProblem = LinetypeSupport.Check(ctx, p.Linetype, out p.LinetypeDef);
                    if (ltProblem != null) OpContext.Error(issues, item, "linetype", IssueCodes.LinetypeMissing, ltProblem);
                    if (p.LinetypeDef != null && ctx.IsValidating) ctx.Plan.Add(PlannedNames.Linetype, p.Linetype);
                }

                if (item.Has("lineweight"))
                {
                    if (AcadValues.TryParseLineWeight(item.Get("lineweight"), true, out var lw, out string err)) p.LineWeight = lw;
                    else OpContext.Error(issues, item, "lineweight", IssueCodes.LineweightInvalid, err);
                }
                else if (std?.Lineweight != null)
                {
                    if (AcadValues.TryParseLineWeight(std.Lineweight.Value, true, out var lw, out string err)) p.LineWeight = lw;
                    else OpContext.Error(issues, item, "lineweight", IssueCodes.LineweightInvalid, "the standard's lineweight for this layer: " + err);
                }

                p.Plot = item.Has("plot") ? item.GetBool("plot") : std?.Plot ?? item.GetBool("plot");
                p.Description = item.GetStringOrNull("description") ?? std?.Description;
                p.Transparency = item.Has("transparency") ? item.GetInt("transparency") : (std != null && std.Transparency > 0 ? std.Transparency : (int?)null);
                p.Off = item.GetBool("off");
                p.Frozen = item.GetBool("frozen");
                p.Locked = item.GetBool("locked");
                if (p.Frozen && string.Equals(p.Name, currentLayer, StringComparison.OrdinalIgnoreCase) && (!p.Exists || update))
                    OpContext.Error(issues, item, "frozen", IssueCodes.LayerFrozen, "'" + p.Name + "' is the current layer and cannot be frozen");
                plans.Add(p);
            }
            return plans;
        }

        protected override List<LayerPlan> Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plans = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            bool update = args.GetBool("update");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var createdLinetypes = new List<string>();
            var outputs = new JsonObject();

            foreach (var p in plans)
            {
                var existing = Db.SymbolId(tr, db.LayerTableId, p.Name);
                if (!existing.IsNull && !update)
                {
                    p.State = "unchanged";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Unchanged.Add(p.Handle);
                    var differs = Differences(tr, existing, p);
                    if (differs.Count > 0)
                        ctx.Warn("Layer '" + p.Name + "' exists and differs from the request (" + string.Join(", ", differs) + "); left unchanged because update is false.");
                    outputs.Add(p.Name, p.Handle);
                    continue;
                }
                var ltId = LinetypeSupport.Ensure(ctx, p.Linetype, p.LinetypeDef, out string? ltHandle);
                if (ltHandle != null) createdLinetypes.Add(ltHandle);
                LayerTableRecord ltr;
                if (existing.IsNull)
                {
                    ltr = new LayerTableRecord { Name = p.Name };
                    SetProperties(ltr, p, ltId);
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    var id = lt.Add(ltr);
                    tr.AddNewlyCreatedDBObject(ltr, true);
                    p.State = "created";
                    p.Handle = OpContext.HandleOf(id);
                    result.Created.Add(p.Handle);
                }
                else
                {
                    ltr = (LayerTableRecord)tr.GetObject(existing, OpenMode.ForWrite);
                    SetProperties(ltr, p, ltId);
                    p.State = "updated";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Modified.Add(p.Handle);
                }
                outputs.Add(p.Name, p.Handle);
            }
            result.Created.AddRange(createdLinetypes);

            string? current = args.GetStringOrNull("setCurrent");
            if (current != null)
            {
                var id = Db.SymbolId(tr, db.LayerTableId, current);
                if (id.IsNull) throw new InvalidOperationException("Layer '" + current + "' for setCurrent does not exist.");
                if (db.Clayer != id) db.Clayer = id;
            }
            result.Outputs.Add("layers", outputs);
            return plans;
        }

        private static void SetProperties(LayerTableRecord ltr, LayerPlan p, ObjectId linetypeId)
        {
            ltr.Color = p.Color;
            ltr.LinetypeObjectId = linetypeId;
            if (p.LineWeight.HasValue) ltr.LineWeight = p.LineWeight.Value;
            ltr.IsPlottable = p.Plot;
            if (p.Description != null) ltr.Description = p.Description;
            if (p.Transparency.HasValue) ltr.Transparency = AcadValues.ToTransparency(p.Transparency.Value);
            ltr.IsOff = p.Off;
            ltr.IsFrozen = p.Frozen;
            ltr.IsLocked = p.Locked;
        }

        private static List<string> Differences(Transaction tr, ObjectId id, LayerPlan p)
        {
            var d = new List<string>();
            var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!AcadValues.SameColor(ltr.Color, p.Color)) d.Add("color");
            var lt = (LinetypeTableRecord)tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead);
            if (!string.Equals(lt.Name, p.Linetype, StringComparison.OrdinalIgnoreCase)) d.Add("linetype");
            if (p.LineWeight.HasValue && ltr.LineWeight != p.LineWeight.Value) d.Add("lineweight");
            if (ltr.IsPlottable != p.Plot) d.Add("plot");
            if (ltr.IsOff != p.Off) d.Add("off");
            if (ltr.IsFrozen != p.Frozen) d.Add("frozen");
            if (ltr.IsLocked != p.Locked) d.Add("locked");
            return d;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, List<LayerPlan> plans)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            bool ok = true;
            var list = new JsonArray();
            foreach (var p in plans)
            {
                var id = Db.SymbolId(tr, db.LayerTableId, p.Name);
                if (id.IsNull) { ok = Mismatch(result, "Layer '" + p.Name + "' is not in the drawing after the op."); continue; }
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var lt = (LinetypeTableRecord)tr.GetObject(ltr.LinetypeObjectId, OpenMode.ForRead);
                var o = new JsonObject()
                    .Add("name", ltr.Name)
                    .Add("handle", OpContext.HandleOf(id))
                    .Add("state", p.State)
                    .Add("color", AcadValues.ColorJson(ltr.Color))
                    .Add("linetype", lt.Name)
                    .Add("lineweight", AcadValues.LineWeightJson(ltr.LineWeight))
                    .Add("plot", ltr.IsPlottable)
                    .Add("off", ltr.IsOff)
                    .Add("frozen", ltr.IsFrozen)
                    .Add("locked", ltr.IsLocked);
                var tp = AcadValues.TransparencyPercent(ltr.Transparency);
                o.Add("transparency", tp.HasValue ? (JsonValue)tp.Value : JsonValue.Null);
                o.Add("description", ltr.Description ?? "");
                list.Add(o);
                if (p.State == "unchanged") continue;
                if (!AcadValues.SameColor(ltr.Color, p.Color)) ok = Mismatch(result, "Layer '" + p.Name + "': colour read back differs.");
                if (!string.Equals(lt.Name, p.Linetype, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "Layer '" + p.Name + "': linetype read back is '" + lt.Name + "'.");
                if (p.LineWeight.HasValue && ltr.LineWeight != p.LineWeight.Value) ok = Mismatch(result, "Layer '" + p.Name + "': lineweight read back differs.");
                if (ltr.IsPlottable != p.Plot || ltr.IsOff != p.Off || ltr.IsFrozen != p.Frozen || ltr.IsLocked != p.Locked)
                    ok = Mismatch(result, "Layer '" + p.Name + "': plot/off/frozen/locked read back differs.");
                if (p.Transparency.HasValue && tp != p.Transparency) ok = Mismatch(result, "Layer '" + p.Name + "': transparency read back is " + (tp?.ToString() ?? "none") + ".");
            }
            result.Readback.Add("layers", list);
            string? current = args.GetStringOrNull("setCurrent");
            var cur = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
            result.Readback.Add("currentLayer", cur.Name);
            if (current != null && !string.Equals(cur.Name, current, StringComparison.OrdinalIgnoreCase))
                ok = Mismatch(result, "The current layer is '" + cur.Name + "', not '" + current + "'.");
            return ok;
        }
    }
}
