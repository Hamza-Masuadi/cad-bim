using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// block.attrs.set: sets attribute values on block references chosen by handle, or by block name with an optional
    /// attribute filter (see <see cref="RefTargets"/>). Tags match case-insensitively. A reference without one of the
    /// tags fails the op (failOnMissingTag) or skips that tag and reports it. Constant attributes cannot be set.
    /// Multiline attributes are set through MTextAttribute; justified single-line ones are re-aligned.
    /// </summary>
    internal sealed class BlockAttrsSet : OpHandler<BlockAttrsSet.AttrPlan>
    {
        public override string Name => "block.attrs.set";

        internal sealed class AttrPlan
        {
            public List<ObjectId> Targets = new List<ObjectId>();
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>Handle to the tags actually set on it.</summary>
            public Dictionary<string, List<string>> Set = new Dictionary<string, List<string>>();
            public List<string> Skipped = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static AttrPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new AttrPlan { Values = BlockSupport.StringMap(args, "values") };
            if (p.Values.Count == 0) OpContext.Error(issues, args, "values", "values_empty", "no attribute values given");
            p.Targets = RefTargets.Find(ctx, args, issues, needDynamic: false);
            bool failOnMissing = args.GetBool("failOnMissingTag");
            Db.Read(ctx, tr =>
            {
                foreach (var id in p.Targets)
                {
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    var have = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId aid in br.AttributeCollection)
                        if (!aid.IsErased) { var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead); have[ar.Tag] = ar; }
                    var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    var constants = BlockSupport.AttDefs(tr, btr, true).Where(a => a.Constant).Select(a => a.Tag.ToUpperInvariant()).ToList();
                    string h = OpContext.HandleOf(id);
                    var setTags = new List<string>();
                    foreach (var tag in p.Values.Keys)
                    {
                        if (constants.Contains(tag.ToUpperInvariant())) { OpContext.Error(issues, args, "values", "attribute_constant", "attribute '" + tag + "' of " + h + " is constant"); continue; }
                        if (have.TryGetValue(tag, out var att))
                        {
                            if (BlockSupport.ValueOf(att) != p.Values[tag] && DraftDb.IsOnLockedLayer(tr, att)) { OpContext.Error(issues, args, "values", "layer_locked", "attribute '" + tag + "' of " + h + " is on locked layer '" + att.Layer + "'"); continue; }
                            setTags.Add(tag);
                            continue;
                        }
                        string msg = "reference " + h + " (" + BlockSupport.EffectiveName(tr, br) + ") has no attribute '" + tag + "'" + (have.Count > 0 ? "; its tags: " + string.Join(", ", have.Keys) : "; it has no attributes");
                        if (failOnMissing) OpContext.Error(issues, args, "values", "attribute_missing", msg);
                        else { OpContext.Warning(issues, args, "values", "attribute_skipped", msg); p.Skipped.Add(h + ":" + tag); }
                    }
                    p.Set[h] = setTags;
                }
                return 0;
            });
            return p;
        }

        protected override AttrPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            foreach (var id in p.Targets)
            {
                string h = OpContext.HandleOf(id);
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                bool changed = false;
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    if (aid.IsErased) continue;
                    var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead);
                    var tag = p.Set[h].FirstOrDefault(t => string.Equals(t, ar.Tag, StringComparison.OrdinalIgnoreCase));
                    if (tag is null) continue;
                    string value = p.Values[tag];
                    if (BlockSupport.ValueOf(ar) == value) continue;
                    ar.UpgradeOpen();
                    BlockSupport.SetValue(ctx, ar, value);
                    changed = true;
                }
                if (changed) result.Modified.Add(h);
                else result.Unchanged.Add(h);
            }
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, AttrPlan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var list = new JsonArray();
            foreach (var id in p.Targets)
            {
                string h = OpContext.HandleOf(id);
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var values = BlockSupport.Read(tr, br);
                var o = new JsonObject();
                foreach (var tag in p.Set[h])
                {
                    values.TryGetValue(tag.ToUpperInvariant(), out var got);
                    o.Set(tag.ToUpperInvariant(), got ?? "");
                    if (got != p.Values[tag]) mismatches.Add("reference " + h + ": " + tag + " reads back as '" + got + "'");
                }
                list.Add(new JsonObject().Add("handle", h).Add("block", BlockSupport.EffectiveName(tr, br)).Add("values", o));
            }
            result.Readback.Add("references", list).Add("count", p.Targets.Count).Add("skipped", JsonArray.FromStrings(p.Skipped));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
