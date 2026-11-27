using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// entity.delete: erases the listed or filtered entities. The number selected must equal expectCount, and every
    /// selected entity must be on an unlocked layer (a filter that meets locked entities fails rather than skipping
    /// them, so the count the caller expected is the count erased). Erasing inside the job transaction is undone when a
    /// rehearsal aborts. Viewports are never selected.
    /// </summary>
    internal sealed class EntityDelete : OpHandler<EntityDelete.Plan>
    {
        public override string Name => "entity.delete";

        internal sealed class Plan
        {
            public List<ObjectId> Ids = new List<ObjectId>();
            public List<string> Handles = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var s = Targets.Select(ctx, args, issues, lockedIsError: true);
            Targets.CheckCount(ctx, args, issues, s, "entities");
            return new Plan { Ids = s.Ids, Handles = s.Ids.Select(OpContext.HandleOf).ToList() };
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            foreach (var id in p.Ids)
            {
                var e = tr.GetObject(id, OpenMode.ForWrite);
                e.Erase();
                result.Deleted.Add(OpContext.HandleOf(id));
            }
            result.Outputs.Add("deleted", JsonArray.FromStrings(p.Handles)).Add("count", p.Ids.Count);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var left = p.Ids.Where(id => !id.IsErased).Select(OpContext.HandleOf).ToList();
            result.Readback.Add("erased", p.Ids.Count - left.Count).Add("notErased", JsonArray.FromStrings(left));
            return left.Count == 0 || Mismatch(result, left.Count + " entities are not erased (first " + left[0] + ")");
        }
    }
}
