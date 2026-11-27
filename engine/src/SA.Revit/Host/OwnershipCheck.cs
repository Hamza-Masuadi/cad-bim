using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops;
using SA.Revit.Ops.Modelling.Support;

namespace SA.Revit.Host
{
    /// <summary>
    /// Worksharing safety before any change.
    ///
    /// Checked, for every existing element a handler declares it will modify or delete (elements created earlier
    /// in the same job are skipped):
    /// <list type="bullet">
    /// <item>WorksharingUtils.GetCheckoutStatus: OwnedByOtherUser is refused (not_owned, with the owner);</item>
    /// <item>WorksharingUtils.GetModelUpdatesStatus: UpdatedInCentral and DeletedInCentral are refused, because the
    /// local copy is stale and the edit would fail or overwrite newer work (reload latest first);</item>
    /// <item>the element's workset: a user workset the current user cannot edit and that someone else owns is
    /// reported with the workset owner.</item>
    /// </list>
    /// Not checked: elements Revit itself changes as a side effect (wall joins, hosted elements, rooms, dimensions);
    /// whether central is reachable (borrowing an unowned element needs it and happens at commit); and anything
    /// that changes in central after the check. The API documents both status calls as locally cached values,
    /// "suitable for reporting" but not a guarantee, so the check is a pre-flight filter: Revit's own ownership
    /// enforcement at commit is the final word, and its error rolls the job back through the failure preprocessor.
    /// Nothing is borrowed, relinquished or synchronised.
    /// A problem refuses the job (not_owned) unless the job sets <c>ownership: "skip"</c>; then the element is left out
    /// of every op (<see cref="OwnershipSkip"/>).
    /// </summary>
    internal static class OwnershipCheck
    {
        public static List<JsonObject> Problems(Document doc, IEnumerable<long> ids)
        {
            var problems = new List<JsonObject>();
            if (!doc.IsWorkshared) return problems;
            WorksetTable? table = null;
            try { table = doc.GetWorksetTable(); } catch (Exception) { }
            foreach (long raw in ids.Distinct())
            {
                var id = ElementIds.FromLong(raw);
                var e = doc.GetElement(id);
                if (e is null) continue;
                string reason = "";
                string owner = "";
                try
                {
                    var status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner);
                    if (status == CheckoutStatus.OwnedByOtherUser) reason = "owned_by_other_user";
                }
                catch (Exception ex) { reason = "status_unavailable: " + ex.Message; }
                if (reason.Length == 0)
                {
                    try
                    {
                        var upd = WorksharingUtils.GetModelUpdatesStatus(doc, id);
                        if (upd == ModelUpdatesStatus.UpdatedInCentral) reason = "updated_in_central";
                        else if (upd == ModelUpdatesStatus.DeletedInCentral) reason = "deleted_in_central";
                    }
                    catch (Exception) { }
                }
                string worksetName = "";
                if (table != null)
                {
                    try
                    {
                        var ws = table.GetWorkset(e.WorksetId);
                        if (ws != null)
                        {
                            worksetName = ws.Name;
                            if (reason.Length == 0 && !ws.IsEditable && !string.IsNullOrEmpty(ws.Owner) &&
                                !string.Equals(ws.Owner, doc.Application.Username, StringComparison.OrdinalIgnoreCase))
                            {
                                reason = "workset_owned_by_other_user";
                                owner = ws.Owner;
                            }
                        }
                    }
                    catch (Exception) { }
                }
                if (reason.Length == 0) continue;
                var o = new JsonObject().Add("id", ElementIds.ToWire(id)).Add("reason", reason).Add("owner", owner ?? "")
                    .Add("category", e.Category?.Name ?? "").Add("workset", worksetName);
                problems.Add(o);
            }
            return problems;
        }

        /// <summary>The element id of a problem object from <see cref="Problems"/>.</summary>
        public static long IdOf(JsonObject problem) =>
            ElementIds.TryParse(problem.GetStringOrNull("id") ?? "", out var id) ? ElementIds.ToLong(id) : -1;
    }

    /// <summary>
    /// Job ownership "skip": leaving elements the user cannot edit out of one op, with no code in the handler for
    /// elements the op is given.
    ///
    /// <list type="number">
    /// <item>An element filter (an object field <c>filter</c> next to a list field <c>elements</c>, the shared
    /// selection shape) is replaced by the list it selects (<see cref="ElementSelect"/>, the resolver the handlers use).</item>
    /// <item>Left-out ids this op declared it changes are removed from every top-level id field and id list (ids it only
    /// reads stay).</item>
    /// <item>The op is validated again with <see cref="OpContext.LeftOut"/> set. If the handler still declares a
    /// left-out element (one it found by itself: a type, a host), every item of each top-level list of ids or objects
    /// is validated on its own, and items that lead to a left-out element are removed.</item>
    /// </list>
    /// The op does not run at all (it is skipped, with a warning) when an id field that is not a list names a
    /// left-out element, a list that had items ends up empty, the narrowed args fail validation, or the handler still
    /// declares a left-out element after step 3. Skipping the whole op is the only outcome that never touches a
    /// left-out element.
    /// </summary>
    internal static class OwnershipSkip
    {
        public const string WarningCode = "ownership_skipped";
        /// <summary>Items validated one by one in step 3, per list; a longer list that still needs it skips the op.</summary>
        public const int MaxProbe = 5000;

        internal sealed class Outcome
        {
            public JsonObject Args = new JsonObject();
            public OpContext? Ctx;
            public List<Issue> Issues = new List<Issue>();
            public HashSet<long> LeftOut = new HashSet<long>();
            /// <summary>Set when the op must not run.</summary>
            public string? SkipReason;
        }

        public static Outcome Narrow(JobContext jc, JobOp op, IOpHandler h, JsonObject resolved, IEnumerable<long> declared)
        {
            var bad = new HashSet<long>(jc.NotEditable.Keys);
            var r = new Outcome { Args = (JsonObject)resolved.DeepClone() };
            // Only ids this op would change are taken out of its args. An id left out by an earlier op can still be
            // something this op merely reads (a dimension reference, an ignored element), and removing it would change
            // what the op does.
            var mine = new HashSet<long>(declared.Where(bad.Contains));
            r.LeftOut.UnionWith(mine);
            var args = r.Args;
            var schema = op.Schema;

            if (schema.FindField("filter") is FieldSpec ff && ff.Type == FieldType.Object && IsIdList(schema.FindField("elements")) &&
                args["filter"] is JsonObject && (args["elements"] is null || args["elements"]!.IsNull))
            {
                var probe = new OpContext(jc, op, OpPhase.Execute);
                var issues = new List<Issue>();
                var found = ElementSelect.Resolve(probe, OpArgs.For(schema, args, jc.Job.LengthUnit, op.ArgsPath), "elements", "filter", issues, out bool pending);
                if (issues.Any(i => i.IsError) || pending)
                    return Skip(r, "its filter could not be turned into a list of elements: " + string.Join("; ", issues.Where(i => i.IsError).Take(3).Select(i => i.Message)));
                if (found.Count > 0)
                {
                    args.Remove("filter");
                    args.Set("elements", JsonArray.FromStrings(found.Select(e => ElementIds.ToWire(e.Id))));
                }
            }

            foreach (var f in schema.Fields)
            {
                var v = args[f.Name];
                if (v is null || v.IsNull) continue;
                if (f.Type == FieldType.Ref)
                {
                    string? idText = Refs.ElementId(v);
                    if (idText != null && ElementIds.TryParse(idText, out var id) && mine.Contains(ElementIds.ToLong(id)))
                    {
                        r.LeftOut.Add(ElementIds.ToLong(id));
                        return Skip(r, "'" + f.Name + "' is element " + idText + ", which is not editable");
                    }
                }
                else if (IsIdList(f) && v is JsonArray list)
                {
                    var ids = Refs.ElementIds(list);
                    var kept = new List<string>();
                    foreach (var idText in ids)
                    {
                        if (ElementIds.TryParse(idText, out var id) && mine.Contains(ElementIds.ToLong(id))) r.LeftOut.Add(ElementIds.ToLong(id));
                        else kept.Add(idText);
                    }
                    if (kept.Count == ids.Count) continue;
                    if (kept.Count == 0) return Skip(r, "every element in '" + f.Name + "' is not editable");
                    args.Set(f.Name, JsonArray.FromStrings(kept));
                }
            }

            var final = Validate(jc, op, h, args, bad, out var ctx);
            if (final.Any(i => i.IsError)) return Invalid(r, final);
            if (ctx.DeclaredLeftOut.Count > 0)
            {
                foreach (var f in schema.Fields)
                {
                    if (f.Type != FieldType.List || f.Item is null || (f.Item.Type != FieldType.Ref && f.Item.Type != FieldType.Object)) continue;
                    if (!(args[f.Name] is JsonArray items) || items.Count == 0) continue;
                    if (items.Count > MaxProbe)
                        return Skip(r, "it still reaches elements that are not editable, and '" + f.Name + "' has more than " + MaxProbe + " items to sort out one by one");
                    var keep = new JsonArray();
                    foreach (var item in items)
                    {
                        var trial = (JsonObject)args.DeepClone();
                        trial.Set(f.Name, new JsonArray(item.DeepClone()));
                        var ti = Validate(jc, op, h, trial, bad, out var tctx);
                        if (!ti.Any(i => i.IsError) && tctx.DeclaredLeftOut.Count > 0) r.LeftOut.UnionWith(tctx.DeclaredLeftOut);
                        else keep.Add(item);
                    }
                    if (keep.Count == items.Count) continue;
                    if (keep.Count == 0) return Skip(r, "every item of '" + f.Name + "' changes an element that is not editable");
                    args.Set(f.Name, keep);
                }
                final = Validate(jc, op, h, args, bad, out ctx);
                if (final.Any(i => i.IsError)) return Invalid(r, final);
                if (ctx.DeclaredLeftOut.Count > 0)
                {
                    r.LeftOut.UnionWith(ctx.DeclaredLeftOut);
                    return Skip(r, "it would still change " + ctx.DeclaredLeftOut.Count + " element(s) that are not editable, and they cannot be taken out of its list of elements");
                }
            }
            r.Ctx = ctx;
            r.Issues = final;
            return r;
        }

        private static bool IsIdList(FieldSpec? f) => f != null && f.Type == FieldType.List && f.Item != null && f.Item.Type == FieldType.Ref;

        private static Outcome Skip(Outcome r, string why)
        {
            r.SkipReason = why;
            return r;
        }

        private static Outcome Invalid(Outcome r, List<Issue> issues) =>
            Skip(r, "with the elements that are not editable left out, the op is no longer valid: " +
                    string.Join("; ", issues.Where(i => i.IsError).Take(3).Select(i => i.Path + ": " + i.Message)));

        private static List<Issue> Validate(JobContext jc, JobOp op, IOpHandler h, JsonObject args, HashSet<long> leftOut, out OpContext ctx)
        {
            ctx = new OpContext(jc, op, OpPhase.Execute);
            ctx.LeftOut.UnionWith(leftOut);
            var issues = JobValidator.ValidateArgs(op.Schema, args, jc.Job.LengthUnit, op.ArgsPath).Where(i => i.IsError).ToList();
            if (issues.Count > 0) return issues;
            try { h.Validate(ctx, OpArgs.For(op.Schema, args, jc.Job.LengthUnit, op.ArgsPath), issues); }
            catch (OpArgsException e) { issues.Add(new Issue(e.Path, IssueCodes.Type, e.Message)); }
            catch (Exception e) { issues.Add(new Issue(op.Path, IssueCodes.InvalidValue, "validation of '" + op.Op + "' failed: " + e.GetType().Name + ": " + e.Message)); }
            return issues;
        }

        /// <summary>The report entries for the elements left out of an op: the problem objects with the op id.</summary>
        public static JsonArray Entries(JobContext jc, string opId, IEnumerable<long> ids)
        {
            var a = new JsonArray();
            foreach (long x in ids.Distinct().OrderBy(x => x))
                if (jc.NotEditable.TryGetValue(x, out var p)) a.Add(((JsonObject)p.DeepClone()).Set("opId", opId));
            return a;
        }

        /// <summary>One warning per left-out element (code ownership_skipped, the element in elements), at most 200 per op.</summary>
        public static void Warn(JobContext jc, string opId, JsonArray entries)
        {
            int n = 0;
            foreach (var v in entries)
            {
                if (!(v is JsonObject e)) continue;
                if (++n > 200) break;
                string owner = e.GetStringOrNull("owner") ?? "";
                var w = new ReportWarning("engine", "element " + e.GetStringOrNull("id") + " (" + (e.GetStringOrNull("category") ?? "") + ") left out: " +
                    e.GetStringOrNull("reason") + (owner.Length > 0 ? ", owner " + owner : "") +
                    ((e.GetStringOrNull("workset") ?? "").Length > 0 ? ", workset " + e.GetStringOrNull("workset") : ""), opId)
                { Code = WarningCode, Severity = "warning" };
                w.Elements.Add(e.GetStringOrNull("id") ?? "");
                jc.Report.Warnings.Add(w);
            }
            if (entries.Count > 200)
                jc.Report.Warnings.Add(new ReportWarning("engine", (entries.Count - 200) + " more element(s) left out; the op's readback.ownershipSkipped lists them all", opId) { Code = WarningCode });
        }
    }
}
