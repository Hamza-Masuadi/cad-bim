using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    /// <summary>Entities picked by a handles list or by the entity.setprops filter.</summary>
    internal sealed class Selection
    {
        public List<ObjectId> Ids = new List<ObjectId>();
        public int SkippedLocked;
        /// <summary>True when the handles are still references during validation (nothing could be checked).</summary>
        public bool Deferred;
    }

    internal static class Targets
    {
        /// <summary>
        /// Resolves "handles" or "filter". Listed handles that are missing, not entities, or on locked layers are errors.
        /// Filtered entities on locked layers are skipped and counted, or are errors when <paramref name="lockedIsError"/>.
        /// Viewports are never selected.
        /// </summary>
        internal static Selection Select(OpContext ctx, OpArgs args, List<Issue> issues, bool lockedIsError)
        {
            var s = new Selection();
            if (args.Has("handles"))
            {
                if (ctx.IsRef(args, "handles")) { s.Deferred = true; return s; }
                var seen = new HashSet<ObjectId>();
                foreach (var h in args.GetRefIds("handles"))
                {
                    var id = ctx.IdOf(h);
                    if (id.IsNull) { OpContext.Error(issues, args, "handles", "handle_missing", "handle " + h + " is not in the drawing"); continue; }
                    string? problem = Db.Read(ctx, tr =>
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || e is Viewport) return "is not a drawing entity";
                        // Attributes, polyline vertices and block-definition entities belong to another object;
                        // copying or erasing them on their own corrupts or throws.
                        if (!(tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord owner) || !owner.IsLayout) return "is not in model space or a layout (it belongs to a block, attribute owner or polyline)";
                        if (SheetDb.IsOnLockedLayer(tr, e)) return "is on a locked layer (" + e.Layer + ")";
                        return null;
                    });
                    if (problem != null) { OpContext.Error(issues, args, "handles", problem.StartsWith("is on", StringComparison.Ordinal) ? "layer_locked" : "not_entity", "entity " + h + " " + problem); continue; }
                    if (seen.Add(id)) s.Ids.Add(id);
                }
                return s;
            }
            var f = args.GetObject("filter");
            if (f is null) return s;
            string space = f.GetString("space");
            var layerRx = f.GetStringOrNull("layer") is string lw ? SheetDb.Wildcard(lw) : null;
            string? type = f.GetStringOrNull("type")?.Trim().ToUpperInvariant();
            string? block = f.GetStringOrNull("block");
            if (block != null && type != null && type != "INSERT")
                OpContext.Error(issues, f, "block", "filter_invalid", "block filters apply to INSERT entities");
            var lockedHandles = new List<string>();
            Db.Read(ctx, tr =>
            {
                foreach (var sp in SheetDb.Spaces(tr, ctx.Database, space, false, false))
                {
                    var btr = (BlockTableRecord)tr.GetObject(sp.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        string dxf = id.ObjectClass.DxfName;
                        if (type != null && !string.Equals(dxf, type, StringComparison.OrdinalIgnoreCase)) continue;
                        if (block != null && dxf != "INSERT") continue;
                        var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (e is Viewport) continue;
                        if (layerRx != null && !layerRx.IsMatch(e.Layer)) continue;
                        if (block != null && !(e is BlockReference br && SheetDb.Wildcard(block).IsMatch(SheetDb.EffectiveName(tr, br)))) continue;
                        if (SheetDb.IsOnLockedLayer(tr, e)) { s.SkippedLocked++; lockedHandles.Add(OpContext.HandleOf(id)); continue; }
                        s.Ids.Add(id);
                    }
                }
                return 0;
            });
            if (lockedIsError && s.SkippedLocked > 0)
                OpContext.Error(issues, args, "filter", "layer_locked", s.SkippedLocked + " matching entities are on locked layers (" + string.Join(", ", lockedHandles.Take(10)) + (lockedHandles.Count > 10 ? ", ..." : "") + "); unlock the layers or narrow the filter");
            else if (ctx.IsValidating && s.SkippedLocked > 0)
                OpContext.Warning(issues, args, "filter", "skipped_locked", s.SkippedLocked + " matching entities are on locked layers and will be skipped");
            if (ctx.IsValidating && s.Ids.Count == 0 && s.SkippedLocked == 0)
                OpContext.Warning(issues, args, "filter", "nothing_selected", "the filter selects no entities");
            return s;
        }

        /// <summary>The expectCount guard: an error when given and different from the selection (skipped when deferred).</summary>
        internal static void CheckCount(OpContext ctx, OpArgs args, List<Issue> issues, Selection s, string what)
        {
            if (!args.Has("expectCount") || s.Deferred) return;
            int expect = args.GetInt("expectCount");
            if (s.Ids.Count != expect)
                OpContext.Error(issues, args, "expectCount", "count_mismatch", "expected " + expect + " " + what + " but the " + (args.Has("handles") ? "handles name " : "filter selects ") + s.Ids.Count + (s.SkippedLocked > 0 ? " (plus " + s.SkippedLocked + " on locked layers)" : ""));
        }

        /// <summary>Wildcard patterns from a string list field (empty when absent).</summary>
        internal static List<Regex> Patterns(OpArgs args, string field) =>
            args.Has(field) ? args.GetStringList(field).Select(SheetDb.Wildcard).ToList() : new List<Regex>();

        internal static bool AnyMatch(List<Regex> patterns, string value) => patterns.Any(r => r.IsMatch(value));
    }
}
