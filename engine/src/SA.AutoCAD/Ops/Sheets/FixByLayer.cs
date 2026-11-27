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
    /// fix.bylayer: resets colour, linetype, lineweight and transparency to ByLayer on entities and attribute references
    /// in model and/or paper space, and (includeBlocks) inside named block definitions. Inside a block definition an
    /// entity on layer 0 becomes ByBlock, so the block takes the insertion's properties; one on another layer becomes
    /// ByLayer (a deliberate multi-layer block keeps its layers). Skipped and counted: viewports, entities on locked
    /// layers, xref and xref-dependent blocks, anonymous blocks (dimension, table and dynamic-block representations
    /// that AutoCAD regenerates).
    /// </summary>
    internal sealed class FixByLayer : OpHandler<FixByLayer.Plan>
    {
        public override string Name => "fix.bylayer";

        internal sealed class Target
        {
            public ObjectId Id;
            public bool ByBlock;
            public bool Color, Linetype, Lineweight, Transparency;
        }

        internal sealed class Plan
        {
            public HashSet<string> Props = new HashSet<string>();
            public List<Target> Targets = new List<Target>();
            public Dictionary<string, int> Before = new Dictionary<string, int>();
            public int SkippedLocked;
            public int Scanned;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p.SkippedLocked > 0)
                OpContext.Warning(issues, args, null, "skipped_locked", p.SkippedLocked + " entities with non-ByLayer properties are on locked layers and will be skipped");
        }

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            foreach (var s in args.GetStringList("properties")) p.Props.Add(s);
            foreach (var k in p.Props) p.Before[k] = 0;
            HashSet<string>? layers = args.Has("layers") ? new HashSet<string>(args.GetStringList("layers"), StringComparer.OrdinalIgnoreCase) : null;
            if (layers != null && ctx.IsValidating)
                foreach (var l in layers) if (!Db.LayerExists(ctx, l)) OpContext.Warning(issues, args, "layers", IssueCodes.LayerMissing, "layer '" + l + "' is not in the drawing");
            Db.Read(ctx, tr =>
            {
                foreach (var s in SheetDb.Spaces(tr, ctx.Database, args.GetString("space"), args.GetBool("includeBlocks"), false))
                {
                    var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (e is Viewport) continue;
                        Consider(tr, p, e, s.IsBlock, layers);
                        if (e is BlockReference br)
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (!aid.IsErased) Consider(tr, p, (Entity)tr.GetObject(aid, OpenMode.ForRead), s.IsBlock, layers);
                    }
                }
                return 0;
            });
            return p;
        }

        private static void Consider(Transaction tr, Plan p, Entity e, bool inBlock, HashSet<string>? layers)
        {
            if (layers != null && !layers.Contains(e.Layer)) return;
            p.Scanned++;
            bool byBlock = inBlock && string.Equals(e.Layer, "0", StringComparison.Ordinal);
            var t = new Target { Id = e.ObjectId, ByBlock = byBlock };
            if (p.Props.Contains("color")) t.Color = byBlock ? !e.Color.IsByBlock : !e.Color.IsByLayer;
            if (p.Props.Contains("linetype")) t.Linetype = !string.Equals(e.Linetype, byBlock ? "ByBlock" : "ByLayer", StringComparison.OrdinalIgnoreCase);
            if (p.Props.Contains("lineweight")) t.Lineweight = e.LineWeight != (byBlock ? LineWeight.ByBlock : LineWeight.ByLayer);
            if (p.Props.Contains("transparency")) t.Transparency = byBlock ? !e.Transparency.IsByBlock : !e.Transparency.IsByLayer;
            if (!(t.Color || t.Linetype || t.Lineweight || t.Transparency)) return;
            if (SheetDb.IsOnLockedLayer(tr, e)) { p.SkippedLocked++; return; }
            if (t.Color) p.Before["color"]++;
            if (t.Linetype) p.Before["linetype"]++;
            if (t.Lineweight) p.Before["lineweight"]++;
            if (t.Transparency) p.Before["transparency"]++;
            p.Targets.Add(t);
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var t in p.Targets)
            {
                var e = (Entity)tr.GetObject(t.Id, OpenMode.ForWrite);
                if (t.Color) e.Color = t.ByBlock ? Color.FromColorIndex(ColorMethod.ByBlock, 0) : Color.FromColorIndex(ColorMethod.ByLayer, 256);
                if (t.Linetype) e.LinetypeId = t.ByBlock ? db.ByBlockLinetype : db.ByLayerLinetype;
                if (t.Lineweight) e.LineWeight = t.ByBlock ? LineWeight.ByBlock : LineWeight.ByLayer;
                if (t.Transparency) e.Transparency = new Transparency(t.ByBlock ? TransparencyMethod.ByBlock : TransparencyMethod.ByLayer);
                result.Modified.Add(OpContext.HandleOf(t.Id));
            }
            var counts = new JsonObject();
            foreach (var k in p.Props) counts.Add(k, p.Before[k]);
            result.Outputs.Add("counts", counts);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            // Re-scan: after the fix nothing that was fixable may remain.
            var again = Build(ctx, args, new List<Issue>());
            var before = new JsonObject();
            var after = new JsonObject();
            foreach (var k in p.Props) { before.Add(k, p.Before[k]); after.Add(k, again.Before[k]); }
            result.Readback.Add("before", before).Add("after", after).Add("entitiesChanged", p.Targets.Count)
                .Add("skippedLocked", p.SkippedLocked).Add("scanned", p.Scanned);
            int left = again.Targets.Count;
            return left == 0 || Mismatch(result, left + " entities still have non-ByLayer properties after the fix");
        }
    }
}
