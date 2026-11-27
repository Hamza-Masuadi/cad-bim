using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>
    /// The block references block.attrs.set and block.dynprops.set act on: the listed handles (each must be a block
    /// reference), or every reference of a block placed directly in a layout (dynamic anonymous copies included),
    /// optionally filtered by attribute values ("where": tags case-insensitive, values exact after trimming).
    /// References on locked layers are refused (they cannot be opened for write).
    /// </summary>
    internal static class RefTargets
    {
        internal static List<ObjectId> Find(OpContext ctx, OpArgs args, List<Issue> issues, bool needDynamic)
        {
            var result = new List<ObjectId>();
            if (args.Has("handles"))
            {
                if (ctx.IsRef(args, "handles")) return result;
                var handles = args.GetRefIds("handles");
                for (int i = 0; i < handles.Count; i++)
                {
                    string path = JsonPath.Index(JsonPath.Key(args.Path, "handles"), i);
                    var id = ctx.IdOf(handles[i]);
                    if (id.IsNull) { issues.Add(new Issue(path, "handle_missing", "no object with handle " + handles[i] + " in the drawing")); continue; }
                    bool ok = Db.Read(ctx, tr =>
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br) || br is Table) { issues.Add(new Issue(path, "not_block_reference", handles[i] + " is not a block reference")); return false; }
                        if (needDynamic && !br.IsDynamicBlock) { issues.Add(new Issue(path, "block_not_dynamic", handles[i] + " is a reference of '" + BlockSupport.EffectiveName(tr, br) + "', which is not a dynamic block")); return false; }
                        if (DraftDb.IsOnLockedLayer(tr, br)) { issues.Add(new Issue(path, "layer_locked", handles[i] + " is on locked layer '" + br.Layer + "'")); return false; }
                        return true;
                    });
                    if (ok && !result.Contains(id)) result.Add(id);
                }
                return result;
            }
            string? block = args.GetStringOrNull("block");
            if (block is null) return result;
            if (!DraftDb.BlockExists(ctx, block)) { OpContext.Error(issues, args, "block", "block_missing", DraftDb.MissingBlock(ctx, block)); return result; }
            var where = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args.Raw["where"] != null) foreach (var kv in args.GetMap("where")) where[kv.Key] = kv.Value is JsonString s ? s.Value : kv.Value.ToString();
            int locked = 0, nestedCount = 0;
            Db.Read(ctx, tr =>
            {
                var bid = DraftDb.BlockId(tr, ctx.Database, block);
                if (bid.IsNull) return 0;
                var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                if (needDynamic && !btr.IsDynamicBlock) { OpContext.Error(issues, args, "block", "block_not_dynamic", "block '" + block + "' is not a dynamic block"); return 0; }
                var refs = BlockSupport.References(tr, btr, out int nested);
                foreach (var id in refs)
                {
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    if (where.Count > 0)
                    {
                        var values = BlockSupport.Read(tr, br);
                        bool match = true;
                        foreach (var kv in where)
                            if (!values.TryGetValue(kv.Key.ToUpperInvariant(), out var v) || v.Trim() != kv.Value.Trim()) { match = false; break; }
                        if (!match) continue;
                    }
                    if (DraftDb.IsOnLockedLayer(tr, br)) { locked++; continue; }
                    result.Add(id);
                }
                nestedCount = nested;
                return 0;
            });
            if (locked > 0) OpContext.Error(issues, args, "block", "layer_locked", locked + " matching references are on locked layers; unlock them or list handles");
            // While validating, earlier ops of the job may still insert references, so "none yet" is only a warning.
            if (result.Count == 0 && locked == 0)
                (ctx.IsValidating ? (Action<List<Issue>, OpArgs, string?, string, string>)OpContext.Warning : OpContext.Error)(issues, args, where.Count > 0 ? "where" : "block", "no_match", "no reference of '" + block + "'" + (where.Count > 0 ? " matches the where filter" : " is placed in a layout") +
                    (nestedCount > 0 ? " (" + nestedCount + " are nested inside other blocks and are not changed)" : ""));
            return result;
        }
    }
}
