using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// layer.translate: moves every entity (and attribute reference) on each source layer to its target, in model space,
    /// paper space and, with includeBlocks, in block definitions (anonymous ones included, so a source layer can become
    /// unreferenced). Xref and xref-dependent layers and blocks are never touched; neither are layer 0 and Defpoints
    /// (as sources or targets). Entities on locked source layers are moved too (forceOpenOnLockedLayer): merging the
    /// layer is what was asked.
    ///
    /// Targets missing from the drawing are created (createTargets) from the standard's layer list when listed there,
    /// else with colour 7, Continuous, default lineweight; a target name that breaks the standard's naming scheme is
    /// refused. Emptied source layers are erased (deleteEmptied) only when Database.Purge confirms nothing references
    /// them any more, and never the current layer. Per-viewport freezes of a source layer are not carried over; the
    /// layer stays (Purge sees the reference) and is reported as kept.
    /// </summary>
    internal sealed class LayerTranslate : OpHandler<LayerTranslate.Plan>
    {
        public override string Name => "layer.translate";

        internal sealed class Move
        {
            public string From = "";
            public string To = "";
            public ObjectId FromId;
            public int Entities;
            public bool Deleted;
            public string Kept = "";
        }

        internal sealed class Target
        {
            public string Name = "";
            public LayerSpec? Spec;
        }

        internal sealed class Plan
        {
            public List<Move> Moves = new List<Move>();
            public List<Target> Create = new List<Target>();
            public int Before;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            foreach (var t in p.Create) ctx.Plan.Add(PlannedNames.Layer, t.Name);
            if (p.Moves.Count == 0) OpContext.Warning(issues, args, "map", "nothing_to_translate", "no layer in the drawing matches the map");
        }

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            bool create = args.GetBool("createTargets");
            var map = args.GetMap("map");
            var layers = Db.Read(ctx, tr =>
            {
                var lt = (LayerTable)tr.GetObject(ctx.Database.LayerTableId, OpenMode.ForRead);
                var list = new List<KeyValuePair<string, ObjectId>>();
                foreach (ObjectId id in lt)
                {
                    if (id.IsErased) continue;
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (l.IsDependent) continue;
                    list.Add(new KeyValuePair<string, ObjectId>(l.Name, id));
                }
                return list;
            });
            var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map)
            {
                string from = kv.Key;
                string to = kv.Value is JsonString s ? s.Value : "";
                if (to.Length == 0) { OpContext.Error(issues, args, "map", "map_invalid", "target for '" + from + "' must be a layer name"); continue; }
                if (SheetDb.IsProtectedLayer(to)) { OpContext.Error(issues, args, "map", "layer_not_allowed", "'" + from + "' would go to " + to + "; content is never merged onto layer 0 or Defpoints"); continue; }
                if (!AcadValues.IsValidSymbolName(to, out string bad)) { OpContext.Error(issues, args, "map", IssueCodes.NameInvalid, bad); continue; }
                var rx = SheetDb.Wildcard(from);
                var sources = layers.Where(l => SheetDb.HasWildcard(from) ? rx.IsMatch(l.Key) : string.Equals(l.Key, from, StringComparison.OrdinalIgnoreCase)).ToList();
                if (sources.Count == 0 && ctx.IsValidating) OpContext.Warning(issues, args, "map", IssueCodes.LayerMissing, "no layer matches '" + from + "'");
                foreach (var src in sources)
                {
                    if (SheetDb.IsProtectedLayer(src.Key)) { if (ctx.IsValidating && !SheetDb.HasWildcard(from)) OpContext.Error(issues, args, "map", "layer_not_allowed", "layer " + src.Key + " is never translated"); continue; }
                    if (string.Equals(src.Key, to, StringComparison.OrdinalIgnoreCase)) continue;
                    if (claimed.TryGetValue(src.Key, out var other))
                    {
                        if (!string.Equals(other, to, StringComparison.OrdinalIgnoreCase))
                            OpContext.Error(issues, args, "map", "map_conflict", "layer '" + src.Key + "' matches two entries (to '" + other + "' and '" + to + "')");
                        continue;
                    }
                    claimed[src.Key] = to;
                    p.Moves.Add(new Move { From = src.Key, To = to, FromId = src.Value });
                }
                bool targetExists = layers.Any(l => string.Equals(l.Key, to, StringComparison.OrdinalIgnoreCase)) || p.Create.Any(t => string.Equals(t.Name, to, StringComparison.OrdinalIgnoreCase));
                if (!targetExists && sources.Count > 0)
                {
                    if (!create) { OpContext.Error(issues, args, "map", IssueCodes.LayerMissing, "target layer '" + to + "' does not exist and createTargets is false"); continue; }
                    if (ctx.Standard?.LayerScheme != null)
                    {
                        var chk = ctx.Standard.ParseLayerName(to);
                        if (!chk.Valid) { OpContext.Error(issues, args, "map", IssueCodes.LayerNameInvalid, "target '" + to + "' does not follow the standard's naming scheme: " + chk.Explain()); continue; }
                    }
                    var spec = ctx.Standard?.FindLayer(to);
                    if (spec != null && spec.Linetype.Length > 0 && !LinetypeSupport.IsBuiltIn(spec.Linetype) && !Db.LinetypeExists(ctx, spec.Linetype))
                        OpContext.Error(issues, args, "map", IssueCodes.LinetypeMissing, "target '" + to + "' needs linetype " + spec.Linetype + "; create the layer with layer.ensure first");
                    p.Create.Add(new Target { Name = to, Spec = spec });
                }
            }
            // Sources that are also targets would chain (A->B, B->C); refuse so the result does not depend on order.
            foreach (var m in p.Moves)
                if (p.Moves.Any(o => string.Equals(o.To, m.From, StringComparison.OrdinalIgnoreCase)))
                    OpContext.Error(issues, args, "map", "map_chain", "layer '" + m.From + "' is both a source and a target; translate in two jobs");
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (var t in p.Create)
            {
                var rec = new LayerTableRecord { Name = t.Name };
                if (t.Spec != null)
                {
                    rec.Color = AcadValues.ToColor(t.Spec.Color);
                    if (!LinetypeSupport.IsBuiltIn(t.Spec.Linetype)) rec.LinetypeObjectId = Db.SymbolId(tr, db.LinetypeTableId, t.Spec.Linetype);
                    if (t.Spec.Lineweight.HasValue && AcadValues.TryParseLineWeight(t.Spec.Lineweight.Value, true, out var lw, out _)) rec.LineWeight = lw;
                    rec.IsPlottable = t.Spec.Plot;
                    if (t.Spec.Description != null) rec.Description = t.Spec.Description;
                    if (t.Spec.Transparency > 0) rec.Transparency = AcadValues.ToTransparency(t.Spec.Transparency);
                }
                else rec.Color = Color.FromColorIndex(ColorMethod.ByAci, 7);
                if (!lt.IsWriteEnabled) lt.UpgradeOpen();
                var id = lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                result.Created.Add(OpContext.HandleOf(id));
                if (t.Spec is null) ctx.Warn("Target layer '" + t.Name + "' is not in the standard's layer list; created with colour 7, Continuous.");
            }

            var targetIds = p.Moves.ToDictionary(m => m.From, m => Db.SymbolId(tr, db.LayerTableId, m.To), StringComparer.OrdinalIgnoreCase);
            var bySource = p.Moves.ToDictionary(m => m.FromId, m => m);
            p.Before = 0;
            foreach (var s in SheetDb.Spaces(tr, db, "all", args.GetBool("includeBlocks"), true))
            {
                var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (bySource.TryGetValue(e.LayerId, out var m)) MoveOne(tr, id, targetIds[m.From], m, result);
                    if (e is BlockReference br)
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            if (aid.IsErased) continue;
                            var ar = (Entity)tr.GetObject(aid, OpenMode.ForRead);
                            if (bySource.TryGetValue(ar.LayerId, out var am)) MoveOne(tr, aid, targetIds[am.From], am, result);
                        }
                }
            }
            p.Before = p.Moves.Sum(m => m.Entities);

            if (args.GetBool("deleteEmptied"))
            {
                foreach (var m in p.Moves)
                {
                    if (m.FromId == db.Clayer) { m.Kept = "current layer"; continue; }
                    var ids = new ObjectIdCollection { m.FromId };
                    db.Purge(ids);
                    if (ids.Count == 0) { m.Kept = "still referenced"; continue; }
                    var rec = (LayerTableRecord)tr.GetObject(m.FromId, OpenMode.ForWrite);
                    rec.Erase();
                    m.Deleted = true;
                    result.Deleted.Add(OpContext.HandleOf(m.FromId));
                }
            }
            var moves = new JsonArray();
            foreach (var m in p.Moves) moves.Add(new JsonObject().Add("from", m.From).Add("to", m.To).Add("entities", m.Entities));
            result.Outputs.Add("moves", moves);
            result.Outputs.Add("deletedLayers", JsonArray.FromStrings(p.Moves.Where(m => m.Deleted).Select(m => m.From)));
            var kept = p.Moves.Where(m => m.Kept.Length > 0).ToList();
            if (kept.Count > 0) ctx.Warn("Emptied layers kept: " + string.Join(", ", kept.Select(m => m.From + " (" + m.Kept + ")")) + ".");
            return p;
        }

        private static void MoveOne(Transaction tr, ObjectId id, ObjectId target, Move m, OpResult result)
        {
            var e = SheetDb.OpenForWrite(tr, id, true);
            e.LayerId = target;
            m.Entities++;
            result.Modified.Add(OpContext.HandleOf(id));
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            bool ok = true;
            var remaining = p.Moves.ToDictionary(m => m.FromId, m => 0);
            foreach (var s in SheetDb.Spaces(tr, db, "all", args.GetBool("includeBlocks"), true))
            {
                var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (remaining.ContainsKey(e.LayerId)) remaining[e.LayerId]++;
                    if (e is BlockReference br)
                        foreach (ObjectId aid in br.AttributeCollection)
                            if (!aid.IsErased && remaining.ContainsKey(((Entity)tr.GetObject(aid, OpenMode.ForRead)).LayerId)) remaining[((Entity)tr.GetObject(aid, OpenMode.ForRead)).LayerId]++;
                }
            }
            var list = new JsonArray();
            foreach (var m in p.Moves)
            {
                int left = m.Deleted ? 0 : remaining[m.FromId];
                bool targetOk = !Db.SymbolId(tr, db.LayerTableId, m.To).IsNull;
                bool erased = m.FromId.IsErased;
                list.Add(new JsonObject().Add("from", m.From).Add("to", m.To).Add("moved", m.Entities).Add("remainingOnSource", left)
                    .Add("sourceDeleted", erased).Add("kept", m.Kept));
                if (left > 0) ok = Mismatch(result, left + " entities are still on '" + m.From + "'");
                if (!targetOk) ok = Mismatch(result, "target layer '" + m.To + "' is missing");
                if (m.Deleted != erased) ok = Mismatch(result, "layer '" + m.From + "' deletion reads back differently");
            }
            result.Readback.Add("moves", list).Add("before", p.Before).Add("after", p.Moves.Sum(m => m.Deleted ? 0 : remaining[m.FromId]));
            return ok;
        }
    }
}
