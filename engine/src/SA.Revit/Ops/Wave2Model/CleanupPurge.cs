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

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// cleanup.purge (Revit): Document.GetUnusedElements, the list behind the Purge Unused window, limited to the given
    /// categories (an empty set returns every unused element).
    ///
    /// Two steps by design. dryRun lists the unused ids (minus what the standard keeps). Deleting needs the ids from
    /// that list and expectCount equal to their number; every id must still be unused when the op runs, so an id that
    /// came into use since the dry run, or a guessed id, refuses the op. Ids must be literal, not references to
    /// earlier ops. keepStandard skips elements whose name, or family name, the standard names as kept.
    ///
    /// Declared as deleted: the ids and their dependents (a family's types, for example). A dependent that is a placed
    /// or annotation element (not a type) and not itself unused, or one the standard keeps, refuses the op: the runner
    /// allows declared deletes, so this is the guard against a purge taking content. After deleting, anything
    /// Revit deleted beyond those fails the op. Deleting can leave more elements unused (a type's material), so
    /// remainingUnused reports the count after the purge; run the op again to take those.
    /// </summary>
    public sealed class CleanupPurge : OpHandler
    {
        public override string Name => "cleanup.purge";

        private sealed class Plan
        {
            public List<ElementId> Unused = new List<ElementId>();
            public List<ElementId> Ids = new List<ElementId>();
            public HashSet<ElementId> Categories = new HashSet<ElementId>();
            public List<ElementId> Dependents = new List<ElementId>();
            public int Kept;
        }

        private static bool Kept(Element? e, HashSet<string>? keep)
        {
            if (e is null || keep is null || keep.Count == 0) return false;
            if (keep.Contains(e.Name)) return true;
            if (e is FamilySymbol fs && (keep.Contains(fs.FamilyName) || keep.Contains(fs.FamilyName + " : " + fs.Name))) return true;
            if (e is ElementType et && keep.Contains(et.FamilyName + " : " + et.Name)) return true;
            return false;
        }

        private static List<ElementId> Unused(Document doc, HashSet<ElementId> categories, HashSet<string>? keep, out int kept)
        {
            var all = Wave2ModelCompat.UnusedElements(doc, categories).OrderBy(ElementIds.ToLong).ToList();
            var list = all.Where(id => !Kept(doc.GetElement(id), keep)).ToList();
            kept = all.Count - list.Count;
            return list;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var names = args.GetStringList("categories");
            for (int i = 0; i < names.Count; i++)
            {
                var c = Lookup.Category(doc, names[i], out string? err);
                if (c is null) OpContext.Error(issues, ctx.FieldPath(args, "categories") + "[" + i + "]", RevitIssueCodes.NotFound, err!);
                else p.Categories.Add(c.Id);
            }
            if (Kit.HasErrors(issues)) return null;
            var keep = args.GetBool("keepStandard") ? ctx.Standard?.NamesToKeep() : null;
            p.Unused = Unused(doc, p.Categories, keep, out p.Kept);

            bool dry = args.GetBool("dryRun");
            if (dry)
            {
                if (args.Has("ids")) OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.Invalid, "dryRun lists ids; give ids only when deleting");
                return Kit.HasErrors(issues) ? null : p;
            }
            if (!args.Has("ids")) { OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.Invalid, "run with dryRun:true first, then pass the listed ids and expectCount"); return null; }

            var unused = new HashSet<long>(p.Unused.Select(ElementIds.ToLong));
            var listed = Kit.IdList(ctx, args, "ids", out bool pending);
            if (pending) { OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.Invalid, "ids must be literal ids from a dry run, not references to earlier ops"); return null; }
            var seen = new HashSet<long>();
            foreach (var kv in listed)
            {
                if (!ElementIds.TryParse(kv.Value, out var id)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "'" + kv.Value + "' is not an element id"); continue; }
                if (!seen.Add(ElementIds.ToLong(id))) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "id " + kv.Value + " is listed twice"); continue; }
                var e = doc.GetElement(id);
                if (e is null) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist"); continue; }
                if (Kept(e, keep)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, "'" + e.Name + "' (" + kv.Value + ") is kept by the standard"); continue; }
                if (!unused.Contains(ElementIds.ToLong(id))) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, "'" + e.Name + "' (" + kv.Value + ") is not unused" + (p.Categories.Count > 0 ? " in the given categories" : "") + "; run a new dry run"); continue; }
                p.Ids.Add(id);
            }
            int expect = args.GetInt("expectCount");
            if (expect != listed.Count) OpContext.Error(issues, ctx.FieldPath(args, "expectCount"), RevitIssueCodes.Mismatch, "expectCount is " + expect + " but " + listed.Count + " id(s) are listed");
            if (Kit.HasErrors(issues)) return null;

            Deletion.Dependents(doc, p.Ids, out var parts, out var separate);
            // Dependents are declared as deletes, so the runner's deletion guard would allow them: refuse any that are
            // placed or annotation elements Revit does not itself report as unused, or that the standard keeps.
            var allUnused = new HashSet<long>(Wave2ModelCompat.UnusedElements(doc, new HashSet<ElementId>()).Select(ElementIds.ToLong));
            var inUse = separate.Where(d => doc.GetElement(d) is Element de && !(de is ElementType) && !allUnused.Contains(ElementIds.ToLong(d))).ToList();
            if (inUse.Count > 0)
                OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.NotAllowed, "purging these ids would also delete " + inUse.Count + " element(s) that are not unused, e.g. " +
                    string.Join(", ", inUse.Take(5).Select(d => Deletion.Describe(doc, d).ToJson())) + "; leave the ids that own them out");
            var keptDeps = separate.Concat(parts).Where(d => Kept(doc.GetElement(d), keep)).ToList();
            if (keptDeps.Count > 0)
                OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.NotAllowed, "purging these ids would also delete " + keptDeps.Count + " element(s) the standard keeps, e.g. '" + doc.GetElement(keptDeps[0])?.Name + "'");
            if (Kit.HasErrors(issues)) return null;
            p.Dependents = parts.Concat(separate).ToList();
            foreach (var id in p.Ids.Concat(p.Dependents)) ctx.DeclareDeletes(id);
            return p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            if (args.GetBool("dryRun"))
            {
                result.Outputs.Add("wouldDelete", Ids(p!.Unused.Select(ElementIds.ToWire)));
                result.Outputs.Add("items", new JsonArray(p.Unused.Select(id => (JsonValue)Deletion.Describe(doc, id))));
                result.Readback.Add("keptByStandard", p.Kept);
                return;
            }
            var allowed = new HashSet<long>(p!.Ids.Concat(p.Dependents).Select(ElementIds.ToLong));
            ICollection<ElementId> gone;
            try { gone = doc.Delete(p.Ids); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("delete_failed", "Revit refused to purge: " + e.Message); }
            var extra = gone.Where(id => !allowed.Contains(ElementIds.ToLong(id))).ToList();
            if (extra.Count > 0)
                throw new OpFailedException("unexpected_deletions", "purging would also delete " + extra.Count + " element(s) that were not listed or dependent, e.g. " + string.Join(", ", extra.Take(5).Select(ElementIds.ToWire)));
            foreach (var id in gone) result.Deleted.Add(ElementIds.ToWire(id));
            result.Outputs.Add("deleted", Ids(gone.Select(ElementIds.ToWire)));
            doc.Regenerate();
            result.Outputs.Add("remainingUnused", Unused(doc, p.Categories, args.GetBool("keepStandard") ? ctx.Standard?.NamesToKeep() : null, out _).Count);
            result.Readback.Add("listed", Ids(p.Ids.Select(ElementIds.ToWire)));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            if (args.GetBool("dryRun")) { check.Apply(result); return; }
            int left = 0;
            foreach (var s in (result.Readback["listed"] as JsonArray ?? new JsonArray()).OfType<JsonString>())
                if (ElementIds.TryParse(s.Value, out var id) && ctx.Doc.GetElement(id) != null) { left++; check.Fail("element " + s.Value + " still exists"); }
            result.Readback.Add("stillPresent", left);
            check.Apply(result);
        }
    }
}
