using System;
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
    /// workset.assign: move elements (listed or filtered) to a user workset, through the element's Workset parameter
    /// (ELEM_PARTITION_PARAM, set to the WorksetId's integer value).
    ///
    /// Refused: a model that is not workshared; a workset name that does not exist unless create is true (then the
    /// name is checked with WorksetTable.IsWorksetNameUnique and Revit's forbidden characters, and the workset is made
    /// with Workset.Create inside the op's transaction); an element whose workset parameter is missing or read-only
    /// (types, view-specific elements, members of groups and hosted elements that follow their host report this).
    /// Elements already on the workset are reported unchanged and not declared; all others are declared as modified,
    /// so the runner's ownership check sees them before anything moves. Moving an element does not move what it hosts.
    /// Readback reads each element's WorksetId.
    /// </summary>
    public sealed class WorksetAssign : OpHandler
    {
        public override string Name => "workset.assign";

        private sealed class Plan
        {
            public string Workset = "";
            public Workset? Existing;
            public List<Element> Move = new List<Element>();
            public List<Element> Already = new List<Element>();
        }

        private static Workset? Find(Document doc, string name, out List<string> names)
        {
            var user = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
            names = user.Select(w => w.Name).ToList();
            return user.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.Ordinal))
                   ?? (user.Count(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) == 1 ? user.First(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)) : null);
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan { Workset = args.GetString("workset").Trim() };
            string at = ctx.FieldPath(args, "workset");
            if (!doc.IsWorkshared) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "the model is not workshared; it has no worksets"); return null; }
            p.Existing = Find(doc, p.Workset, out var names);
            if (p.Existing is null)
            {
                if (!args.GetBool("create")) OpContext.Error(issues, at, RevitIssueCodes.NotFound, "no user workset named '" + p.Workset + "'" + Lookup.Suggest(p.Workset, names) + "; set create:true to make it");
                else if (Kit.BadTypeName(p.Workset) is string bad && (bad.Contains("does not allow") || bad.Contains("empty") || bad.Contains("spaces")))
                    OpContext.Error(issues, at, RevitIssueCodes.Invalid, "workset name: " + bad);
                else if (!WorksetTable.IsWorksetNameUnique(doc, p.Workset))
                    OpContext.Error(issues, at, RevitIssueCodes.Exists, "the name '" + p.Workset + "' is already used by a workset (not a user workset)");
            }
            var elements = ElementSelect.Resolve(ctx, args, "elements", "filter", issues, out bool pending);
            var seen = new HashSet<long>();
            int bad2 = 0;
            foreach (var e in elements)
            {
                if (!seen.Add(ElementIds.ToLong(e.Id))) continue;
                var prm = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (prm is null || prm.IsReadOnly)
                {
                    if (bad2++ < 20)
                        OpContext.Error(issues, ctx.FieldPath(args, args.Has("elements") ? "elements" : "filter"), RevitIssueCodes.ReadOnly,
                            "the workset of element " + Kit.Id(e.Id) + " (" + (e.Category?.Name ?? e.GetType().Name) + ") cannot be changed");
                    continue;
                }
                if (p.Existing != null && e.WorksetId == p.Existing.Id) p.Already.Add(e);
                else { p.Move.Add(e); ctx.DeclareModifies(e.Id); }
            }
            if (bad2 > 20) OpContext.Error(issues, ctx.FieldPath(args, "elements"), RevitIssueCodes.ReadOnly, (bad2 - 20) + " more element(s) cannot change workset");
            if (elements.Count == 0 && !pending && !Kit.HasErrors(issues))
                OpContext.Warning(issues, args.Path, RevitIssueCodes.NotFound, "no elements matched; nothing will move");
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var ws = p!.Existing ?? Workset.Create(doc, p.Workset) ?? throw new OpFailedException("create_failed", "Revit did not create workset '" + p.Workset + "'");
            int value = ws.Id.IntegerValue;
            foreach (var e in p.Move)
            {
                var prm = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                if (prm is null || !prm.Set(value)) throw new OpFailedException("parameter_value", "Revit refused to move element " + Kit.Id(e.Id) + " to workset '" + ws.Name + "'");
                result.Modified.Add(Kit.Id(e.Id));
            }
            foreach (var e in p.Already) result.Unchanged.Add(Kit.Id(e.Id));
            result.Outputs.Add("count", p.Move.Count);
            result.Readback.Add("worksetCreated", p.Existing is null);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var ws = Find(doc, args.GetString("workset").Trim(), out _);
            if (ws is null) { check.Fail("workset '" + args.GetString("workset") + "' does not exist after the op"); check.Apply(result); return; }
            result.Readback.Add("workset", ws.Name);
            result.Readback.Add("worksetId", ws.Id.IntegerValue);
            int ok = 0;
            foreach (var idText in result.Modified.Concat(result.Unchanged))
            {
                if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Element e)) { check.Fail("element " + idText + " no longer exists"); continue; }
                if (e.WorksetId != ws.Id) check.Fail("element " + idText + " is on workset " + e.WorksetId.IntegerValue + ", not '" + ws.Name + "'");
                else ok++;
            }
            result.Readback.Add("moved", result.Modified.Count);
            result.Readback.Add("alreadyThere", result.Unchanged.Count);
            result.Readback.Add("onWorkset", ok);
            check.Apply(result);
        }
    }
}
