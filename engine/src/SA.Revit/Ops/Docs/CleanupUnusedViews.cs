using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// cleanup.unusedViews (destructive): deletes listed views that nothing uses. Only the ids the job lists can be
    /// deleted; nothing is found and deleted by search. With dryRun the op deletes nothing and lists the views it
    /// would delete in <c>wouldDelete</c> (and <c>skipped</c> with reasons); a rehearsal deletes and rolls back.
    ///
    /// <para><b>Never deleted</b> (skipped, with the reason as a warning):</para>
    /// <list type="bullet">
    /// <item>a view on a sheet (viewport or schedule instance), and sheets themselves;</item>
    /// <item>a view template, unless includeTemplates; and a template still assigned to any view, always;</item>
    /// <item>the document's active view (Document.ActiveView, read only for this safety check);</item>
    /// <item>a view with dependent views: always when any dependent is on a sheet; otherwise when keepWithDependents
    /// (default). When keepWithDependents is false, its dependents are deleted with it and must pass every check too;</item>
    /// <item>a view whose marker shows on a sheeted view: an elevation marker in a sheeted view hosting it
    /// (ElevationMarker.GetViewId), a section or callout viewer in a sheeted view that is one of the view's dependent
    /// elements or carries its name, or a reference section/callout pointing at it
    /// (ReferenceableViewUtils.GetReferencedViewId);</item>
    /// <item>the title block revision schedule and internal keynote schedules.</item>
    /// </list>
    /// <para><b>Deletion guard.</b> For each view to delete, the view, its deletable dependent views and their logical
    /// children (Element.GetDependentElements(null): tags, dimensions, text in the view) are declared as deletes in
    /// Validate. After Document.Delete the op compares Revit's deleted set with the declared set and fails when
    /// Revit deleted anything else.</para>
    /// </summary>
    public sealed class CleanupUnusedViews : OpHandler
    {
        public override string Name => "cleanup.unusedViews";

        private sealed class Plan
        {
            public List<View> Delete = new List<View>();
            public HashSet<long> Declared = new HashSet<long>();
            public List<KeyValuePair<string, string>> Skipped = new List<KeyValuePair<string, string>>();
        }

        private sealed class Context
        {
            public HashSet<long> Sheeted = new HashSet<long>();
            public HashSet<long> MarkerViewsOnSheets = new HashSet<long>();
            public List<Element> ViewersOnSheets = new List<Element>();
            public HashSet<long> TemplatesInUse = new HashSet<long>();
            public long Active = -1;
        }

        private static Context Survey(Document doc)
        {
            var c = new Context();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()) c.Sheeted.Add(ElementIds.ToLong(vp.ViewId));
            foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()) c.Sheeted.Add(ElementIds.ToLong(si.ScheduleId));
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                if (!v.IsTemplate && ElementIds.IsValid(v.ViewTemplateId)) c.TemplatesInUse.Add(ElementIds.ToLong(v.ViewTemplateId));
            try { if (doc.ActiveView != null) c.Active = ElementIds.ToLong(doc.ActiveView.Id); } catch (Exception) { }
            foreach (long sid in c.Sheeted)
            {
                if (!(doc.GetElement(ElementIds.FromLong(sid)) is View sv) || sv is ViewSchedule || sv.ViewType == ViewType.Legend || sv.ViewType == ViewType.DraftingView) continue;
                try
                {
                    foreach (var m in new FilteredElementCollector(doc, sv.Id).OfClass(typeof(ElevationMarker)).Cast<ElevationMarker>())
                        for (int i = 0; i < m.MaximumViewCount; i++)
                        {
                            var id = m.GetViewId(i);
                            if (ElementIds.IsValid(id)) c.MarkerViewsOnSheets.Add(ElementIds.ToLong(id));
                        }
                    c.ViewersOnSheets.AddRange(new FilteredElementCollector(doc, sv.Id).OfCategory(BuiltInCategory.OST_Viewers).WhereElementIsNotElementType());
                }
                catch (Exception) { }
            }
            return c;
        }

        private static string? Blocker(Document doc, View v, Context c, ICollection<ElementId> deps, bool includeTemplates)
        {
            long id = ElementIds.ToLong(v.Id);
            if (v is ViewSheet) return "it is a sheet";
            if (v.IsTemplate)
            {
                if (!includeTemplates) return "it is a view template (set includeTemplates to delete unused templates)";
                if (c.TemplatesInUse.Contains(id)) return "it is a view template assigned to views";
                return null;
            }
            if (c.Sheeted.Contains(id)) return "it is on a sheet";
            if (id == c.Active) return "it is the active view";
            if (v is ViewSchedule vs && (vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule)) return "it is an internal schedule";
            if (c.MarkerViewsOnSheets.Contains(id)) return "its elevation marker shows on a sheeted view";
            var depSet = new HashSet<long>(deps.Select(ElementIds.ToLong));
            foreach (var viewer in c.ViewersOnSheets)
            {
                if (depSet.Contains(ElementIds.ToLong(viewer.Id))) return "its marker shows on a sheeted view";
                if (string.Equals(viewer.Name, v.Name, StringComparison.Ordinal) && (v.ViewType == ViewType.Section || v.ViewType == ViewType.Detail ||
                    v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.Elevation))
                    return "a marker named after it shows on a sheeted view";
                try
                {
                    if (ReferenceableViewUtils.GetReferencedViewId(doc, viewer.Id) == v.Id) return "a reference marker on a sheeted view points at it";
                }
                catch (Exception) { }
            }
            return null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var c = Survey(doc);
            bool includeTemplates = args.GetBool("includeTemplates");
            bool keepWithDependents = args.GetBool("keepWithDependents");
            var raw = args.Get("ids") as JsonArray ?? new JsonArray();
            var seen = new HashSet<long>();
            for (int i = 0; i < raw.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "ids"), i);
                if (OpContext.IsPending(raw[i])) continue;
                foreach (var idText in Refs.ElementIds(raw[i]))
                {
                    if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Element e)) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "element " + idText + " does not exist"); continue; }
                    if (!(e is View v)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "element " + idText + " is a " + (e.Category?.Name ?? e.GetType().Name) + ", not a view"); continue; }
                    if (!seen.Add(ElementIds.ToLong(v.Id))) continue;
                    var deps = SafeDeps(v);
                    string? why = Blocker(doc, v, c, deps, includeTemplates);
                    var dependents = v.IsTemplate ? new List<View>() : v.GetDependentViewIds().Select(x => doc.GetElement(x) as View).Where(x => x != null).Cast<View>().ToList();
                    if (why is null && dependents.Count > 0)
                    {
                        var sheetedDep = dependents.FirstOrDefault(d => c.Sheeted.Contains(ElementIds.ToLong(d.Id)));
                        if (sheetedDep != null) why = "its dependent view '" + sheetedDep.Name + "' is on a sheet";
                        else if (keepWithDependents) why = "it has " + dependents.Count + " dependent view(s) (keepWithDependents)";
                        else
                            foreach (var d in dependents)
                            {
                                var b = Blocker(doc, d, c, SafeDeps(d), includeTemplates);
                                if (b != null) { why = "its dependent view '" + d.Name + "' cannot go: " + b; break; }
                            }
                    }
                    if (why != null) { p.Skipped.Add(new KeyValuePair<string, string>(ElementIds.ToWire(v.Id), "view " + DocsUtil.Describe(v) + " kept: " + why)); continue; }
                    p.Delete.Add(v);
                    p.Declared.Add(ElementIds.ToLong(v.Id));
                    foreach (var x in deps) p.Declared.Add(ElementIds.ToLong(x));
                    foreach (var d in dependents)
                    {
                        p.Declared.Add(ElementIds.ToLong(d.Id));
                        foreach (var x in SafeDeps(d)) p.Declared.Add(ElementIds.ToLong(x));
                    }
                }
            }
            if (declare && !args.GetBool("dryRun")) foreach (var x in p.Declared) ctx.DeclareDeletes(ElementIds.FromLong(x));
            if (!DocsUtil.HasErrors(issues))
                foreach (var s in p.Skipped) OpContext.Warning(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.NotAllowed, s.Value);
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static ICollection<ElementId> SafeDeps(View v)
        {
            try { return v.GetDependentElements(null); } catch (Exception) { return new List<ElementId>(); }
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            foreach (var s in p.Skipped) ctx.Warn(s.Value);
            var deleted = new List<string>();
            if (args.GetBool("dryRun"))
            {
                result.Outputs.Set("deleted", new JsonArray());
                result.Outputs.Set("wouldDelete", JsonArray.FromStrings(p.Delete.Select(v => ElementIds.ToWire(v.Id))));
                result.Outputs.Set("skipped", JsonArray.FromStrings(p.Skipped.Select(s => s.Key)));
                ctx.Warn("dryRun: nothing was deleted; " + p.Delete.Count + " view(s) would be deleted with " + p.Declared.Count + " element(s) in all");
                return;
            }
            if (p.Delete.Count > 0)
            {
                var gone = ctx.Doc.Delete(p.Delete.Select(v => v.Id).ToList());
                var extra = gone.Where(x => !p.Declared.Contains(ElementIds.ToLong(x))).ToList();
                if (extra.Count > 0)
                    throw new OpFailedException("unexpected_deletion", "deleting the views would also delete " + extra.Count + " element(s) that were not declared",
                        new JsonObject().Add("elements", JsonArray.FromStrings(extra.Take(200).Select(ElementIds.ToWire))));
                deleted.AddRange(ElementIds.ToWire(gone));
                result.Deleted.AddRange(deleted);
            }
            result.Outputs.Set("deleted", JsonArray.FromStrings(p.Delete.Select(v => ElementIds.ToWire(v.Id))));
            result.Outputs.Set("skipped", JsonArray.FromStrings(p.Skipped.Select(s => s.Key)));
            result.Outputs.Set("wouldDelete", new JsonArray());
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var deleted = (result.Outputs["deleted"] as JsonArray ?? new JsonArray()).Select(v => v.AsString()).ToList();
            var skipped = (result.Outputs["skipped"] as JsonArray ?? new JsonArray()).Select(v => v.AsString()).ToList();
            foreach (var id in deleted)
                if (ElementIds.TryParse(id, out var eid) && doc.GetElement(eid) != null) check.Fail("view " + id + " still exists after the op");
            var would = (result.Outputs["wouldDelete"] as JsonArray ?? new JsonArray()).Select(v => v.AsString()).ToList();
            foreach (var id in would)
                if (!ElementIds.TryParse(id, out var eid) || doc.GetElement(eid) is null) check.Fail("view " + id + " is gone after a dry run");
            foreach (var id in skipped)
                if (!ElementIds.TryParse(id, out var eid) || doc.GetElement(eid) is null) check.Fail("skipped view " + id + " is gone after the op");
            result.Readback.Set("deleted", JsonArray.FromStrings(deleted));
            result.Readback.Set("skipped", JsonArray.FromStrings(skipped));
            if (args.GetBool("dryRun")) result.Readback.Set("wouldDelete", JsonArray.FromStrings(would));
            result.Readback.Set("elementsDeleted", result.Deleted.Count);
            check.Apply(result);
        }
    }
}
