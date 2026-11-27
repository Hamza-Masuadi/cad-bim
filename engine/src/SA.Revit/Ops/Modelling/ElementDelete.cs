using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// element.delete (destructive): delete exactly the listed elements.
    ///
    /// Validate: expectCount must equal the number of ids (the schema cannot compare two fields); ids must be distinct,
    /// exist and be deletable (DocumentValidation.CanDeleteElement). Element.GetDependentElements is read for every
    /// listed element: parts of the element itself (its sketch, sketch plane and sketch lines, elements without a
    /// model or annotation category) are always declared with it; separate dependents a user would notice (hosted
    /// doors and windows, tags, instances of a deleted type, a view's annotation) are refused unless allowDependents,
    /// and then declared and listed in a warning. Every declared id goes through the runner's ownership check.
    ///
    /// Apply: Document.Delete(ids). The ids Revit reports as deleted are compared with the declared set; anything
    /// beyond it fails the op (rolled back), so a dependent Revit finds that GetDependentElements did not list is
    /// never deleted silently. Readback confirms every listed id is gone.
    /// </summary>
    public sealed class ElementDelete : OpHandler
    {
        public override string Name => "element.delete";

        private sealed class Plan
        {
            public List<ElementId> Ids = new List<ElementId>();
            public List<ElementId> Parts = new List<ElementId>();
            public List<ElementId> Separate = new List<ElementId>();
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var listed = Kit.IdList(ctx, args, "ids", out bool pending);
            int expect = args.GetInt("expectCount");
            var seen = new HashSet<long>();
            foreach (var kv in listed)
            {
                if (!ElementIds.TryParse(kv.Value, out var id)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "'" + kv.Value + "' is not an element id"); continue; }
                if (!seen.Add(ElementIds.ToLong(id))) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is listed twice"); continue; }
                var e = doc.GetElement(id);
                if (e is null) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist (the snapshot may be stale)"); continue; }
                if (!DocumentValidation.CanDeleteElement(doc, id)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, "Revit does not allow deleting element " + kv.Value + " (" + (e.Category?.Name ?? e.GetType().Name) + ")"); continue; }
                p.Ids.Add(id);
            }
            if (pending) return Kit.HasErrors(issues) ? null : p;
            if (listed.Count != expect)
                OpContext.Error(issues, ctx.FieldPath(args, "expectCount"), RevitIssueCodes.Mismatch, "expectCount is " + expect + " but " + listed.Count + " id(s) are listed; nothing is deleted when they differ");
            if (Kit.HasErrors(issues)) return null;

            Deletion.Dependents(doc, p.Ids, out p.Parts, out p.Separate);
            if (p.Separate.Count > 0)
            {
                string list = string.Join(", ", p.Separate.Take(15).Select(d => Kit.Id(d) + " " + (doc.GetElement(d)?.Category?.Name ?? "")));
                if (!args.GetBool("allowDependents"))
                    OpContext.Error(issues, ctx.FieldPath(args, "allowDependents"), RevitIssueCodes.NotAllowed, "deleting these elements also deletes " + p.Separate.Count + " other element(s): " + list +
                        (p.Separate.Count > 15 ? ", ..." : "") + "; list them too or set allowDependents:true");
                else OpContext.Warning(issues, ctx.FieldPath(args, "allowDependents"), RevitIssueCodes.NotAllowed, p.Separate.Count + " dependent element(s) will be deleted too: " + list + (p.Separate.Count > 15 ? ", ..." : ""));
            }
            foreach (var id in p.Ids) ctx.DeclareDeletes(id);
            foreach (var id in p.Parts) ctx.DeclareDeletes(id);
            if (args.GetBool("allowDependents")) foreach (var id in p.Separate) ctx.DeclareDeletes(id);
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var deleted = doc.Delete(p!.Ids) ?? new List<ElementId>();
            var allowed = new HashSet<long>(ctx.Deletes);
            var extra = deleted.Where(d => !allowed.Contains(ElementIds.ToLong(d)) && !ctx.IsCreatedByJob(d)).ToList();
            if (extra.Count > 0)
                throw new OpFailedException("unexpected_dependents", "Revit would also delete " + extra.Count + " element(s) that were not declared: " + string.Join(", ", extra.Take(20).Select(Kit.Id)) +
                    "; the op was rolled back", new JsonObject().Add("elements", JsonArray.FromStrings(extra.Take(200).Select(Kit.Id))));
            var wire = deleted.Select(Kit.Id).ToList();
            result.Deleted.AddRange(wire);
            result.Outputs.Add("deleted", JsonArray.FromStrings(wire));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var listed = Refs.ElementIds(args.Get("ids"));
            foreach (var idText in listed)
                if (ElementIds.TryParse(idText, out var id) && doc.GetElement(id) != null) check.Fail("element " + idText + " still exists after the op");
            var listedSet = new HashSet<string>(listed);
            result.Readback.Add("listed", listed.Count);
            result.Readback.Add("deletedCount", result.Deleted.Count);
            result.Readback.Add("dependentsDeleted", JsonArray.FromStrings(result.Deleted.Where(d => !listedSet.Contains(d))));
            check.Apply(result);
        }
    }
}
