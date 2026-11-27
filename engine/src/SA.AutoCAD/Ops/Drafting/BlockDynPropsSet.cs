using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// block.dynprops.set: sets dynamic block properties on references chosen by handle or by dynamic block name.
    /// Every property of every target is checked against that reference's own DynamicBlockReferencePropertyCollection
    /// before anything changes: the name must exist and be writable, list values (visibility states, lookups, flip
    /// states) must be allowed, and values are converted from job units and degrees (see <see cref="DynProps"/>). Values
    /// are applied list properties first, then the rest. Readback re-reads every property: a value that AutoCAD
    /// clamped or snapped to a parameter's range or increment fails the op.
    /// </summary>
    internal sealed class BlockDynPropsSet : OpHandler<BlockDynPropsSet.DynPlan>
    {
        public override string Name => "block.dynprops.set";

        internal sealed class DynPlan
        {
            public List<ObjectId> Targets = new List<ObjectId>();
            public List<KeyValuePair<string, JsonValue>> Requested = new List<KeyValuePair<string, JsonValue>>();
            public Dictionary<string, List<DynValue>> Values = new Dictionary<string, List<DynValue>>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static DynPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new DynPlan { Requested = args.GetMap("properties") };
            if (p.Requested.Count == 0) OpContext.Error(issues, args, "properties", "properties_empty", "no properties given");
            p.Targets = RefTargets.Find(ctx, args, issues, needDynamic: true);
            Db.Read(ctx, tr =>
            {
                foreach (var id in p.Targets)
                {
                    var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                    string h = OpContext.HandleOf(id);
                    var list = new List<DynValue>();
                    foreach (var kv in p.Requested)
                    {
                        var v = DynProps.Convert(ctx, br, kv.Key, kv.Value, out string err);
                        if (v is null) OpContext.Error(issues, args, "properties", "dynprop_invalid", "reference " + h + " (" + BlockSupport.EffectiveName(tr, br) + "), '" + kv.Key + "': " + err);
                        else list.Add(v);
                    }
                    p.Values[h] = list;
                }
                return 0;
            });
            return p;
        }

        protected override DynPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            foreach (var id in p.Targets)
            {
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForWrite);
                DynProps.Apply(br, p.Values[OpContext.HandleOf(id)]);
                result.Modified.Add(OpContext.HandleOf(id));
            }
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, DynPlan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var list = new JsonArray();
            foreach (var id in p.Targets)
            {
                string h = OpContext.HandleOf(id);
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var o = new JsonObject();
                foreach (var v in p.Values[h])
                {
                    var prop = DynProps.Find(br, v.Name);
                    if (prop is null) { mismatches.Add("reference " + h + ": property " + v.Name + " is gone"); continue; }
                    o.Set(v.Name, DynProps.ToJson(ctx, prop, br));
                    if (!DynProps.Same(prop, v))
                        mismatches.Add("reference " + h + ": " + v.Name + " reads back as " + DynProps.ToJson(ctx, prop, br) + ", requested " + v.Requested +
                            " (AutoCAD may have clamped it to the parameter's range or increments, or another property overrode it)");
                }
                list.Add(new JsonObject().Add("handle", h).Add("block", BlockSupport.EffectiveName(tr, br)).Add("properties", o));
            }
            result.Readback.Add("references", list).Add("count", p.Targets.Count);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
