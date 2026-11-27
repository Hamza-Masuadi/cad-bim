using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "views" (FACTS.md 2.3): one record per View, id = view id. Left out: sheets (ViewSheet), title block
    /// revision schedules, and views of type ProjectBrowser, SystemBrowser, Internal and Undefined. Templates are
    /// included with isTemplate true.
    ///
    /// Fields: name, viewType (ViewType enum name), isTemplate, isSchedule, isDependent, onSheet (bool: a Viewport or a
    /// ScheduleSheetInstance shows it), sheetNumber (absent when not on a sheet), hasTemplate (ViewTemplateId valid;
    /// for a dependent view that of its primary view), templateApplicable, hiddenElementCount, detailLevel (coarse,
    /// medium, fine, undefined), scale (View.Scale; absent when Revit gives none), entourageVisible.
    ///
    /// hiddenElementCount: Element.IsHidden(view) over the elements that can be hidden in the view one by one: every
    /// non-type, view-independent element with a category, plus the elements owned by the view. Templates: 0. The
    /// total number of IsHidden calls is bounded by AuditCache.MaxHiddenChecks; views past the bound get no
    /// hiddenElementCount and facts meta "truncated" says how many. The bound is a safety ceiling against freezing
    /// Revit, far above real projects; in verify the field is worked out only when a chosen check reads it
    /// (AuditContext.Wants), and the same holds for entourageVisible.
    ///
    /// entourageVisible: OST_Entourage or OST_Planting is not hidden in the view (GetCategoryHidden) and a view collector
    /// (FilteredElementCollector(doc, viewId)) of that category finds an element. The view collector is only built
    /// when the document holds an element of that category at all, and never for templates or schedules.
    /// </summary>
    internal sealed class ViewsCollector : IFactCollector
    {
        public string Table => "views";

        private static readonly HashSet<ViewType> TemplateTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.Detail, ViewType.ThreeD,
            ViewType.AreaPlan, ViewType.EngineeringPlan, ViewType.Walkthrough,
        };

        private static readonly BuiltInCategory[] Entourage = { BuiltInCategory.OST_Entourage, BuiltInCategory.OST_Planting };

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            var placed = Snapshot.SheetIndex.ViewToSheet(doc);
            var views = new List<View>();
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v is ViewSheet) continue;
                if (v.ViewType == ViewType.Internal || v.ViewType == ViewType.ProjectBrowser || v.ViewType == ViewType.SystemBrowser || v.ViewType == ViewType.Undefined) continue;
                if (v is ViewSchedule vs && vs.IsTitleblockRevisionSchedule) continue;
                views.Add(v);
            }
            if (views.Count > AuditCache.MaxRecordsPerTable)
            {
                ctx.Truncated(Table, "records", AuditCache.MaxRecordsPerTable, views.Count, "record limit");
                views = views.Take(AuditCache.MaxRecordsPerTable).ToList();
            }

            // The two costly fields are worked out only when a check reads them (always in a plain audit).
            bool wantHidden = ctx.Wants(Table, "hiddenElementCount");
            bool wantEntourage = ctx.Wants(Table, "entourageVisible");
            // Candidates for "hidden one by one": view-independent elements with a category (shared by every view).
            var independent = !wantHidden ? new List<Element>() : new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent()
                .Where(e => e.Category != null).ToList();
            var ownedByView = new Dictionary<long, List<Element>>();
            foreach (var e in wantHidden ? new FilteredElementCollector(doc).WhereElementIsNotElementType() : Enumerable.Empty<Element>())
            {
                ElementId owner;
                try { if (!e.ViewSpecific) continue; owner = e.OwnerViewId; } catch (Exception) { continue; }
                if (!ElementIds.IsValid(owner)) continue;
                long k = ElementIds.ToLong(owner);
                if (!ownedByView.TryGetValue(k, out var list)) ownedByView[k] = list = new List<Element>();
                list.Add(e);
            }
            var entourageInDoc = !wantEntourage ? new List<BuiltInCategory>() : Entourage.Where(c => new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount() > 0).ToList();

            long budget = AuditCache.MaxHiddenChecks;
            int hiddenSkipped = 0;
            foreach (var v in views)
            {
                var r = table.Add(ElementIds.ToWire(v.Id));
                long id = ElementIds.ToLong(v.Id);
                bool isTemplate = v.IsTemplate;
                bool isSchedule = v is ViewSchedule;
                r.Set("name", v.Name ?? "");
                r.Set("viewType", v.ViewType.ToString());
                r.Set("isTemplate", isTemplate);
                r.Set("isSchedule", isSchedule);
                ElementId primary = ElementId.InvalidElementId;
                try { primary = v.GetPrimaryViewId(); } catch (Exception) { }
                r.Set("isDependent", ElementIds.IsValid(primary));
                bool onSheet = placed.TryGetValue(id, out var sheet);
                r.Set("onSheet", onSheet);
                if (onSheet) r.Set("sheetNumber", sheet!);
                var templateOf = ElementIds.IsValid(primary) && doc.GetElement(primary) is View pv ? pv : v;
                r.Set("hasTemplate", ElementIds.IsValid(templateOf.ViewTemplateId));
                r.Set("templateApplicable", TemplateTypes.Contains(v.ViewType));

                if (!wantHidden) { }
                else if (isTemplate) r.Set("hiddenElementCount", 0);
                else
                {
                    ownedByView.TryGetValue(id, out var owned);
                    long cost = independent.Count + (owned?.Count ?? 0);
                    if (cost > budget) hiddenSkipped++;
                    else
                    {
                        budget -= cost;
                        r.Set("hiddenElementCount", CountHidden(v, independent) + (owned is null ? 0 : CountHidden(v, owned)));
                    }
                }

                ViewDetailLevel dl = ViewDetailLevel.Undefined;
                try { dl = v.DetailLevel; } catch (Exception) { }
                r.Set("detailLevel", dl.ToString().ToLowerInvariant());
                try
                {
                    int s = v.Scale;
                    if (s > 0) r.Set("scale", s);
                }
                catch (Exception) { }
                if (wantEntourage) r.Set("entourageVisible", !isTemplate && !isSchedule && EntourageVisible(ctx, v, entourageInDoc));
            }
            if (hiddenSkipped > 0)
                ctx.Truncated(Table, "hiddenElementCount", views.Count - hiddenSkipped, views.Count,
                    "more than " + AuditCache.MaxHiddenChecks + " Element.IsHidden checks would be needed; the views past the bound have no hiddenElementCount");
        }

        private static int CountHidden(View v, List<Element> candidates)
        {
            int n = 0;
            foreach (var e in candidates)
            {
                try { if (e.IsHidden(v)) n++; } catch (Exception) { }
            }
            return n;
        }

        private static bool EntourageVisible(AuditContext ctx, View v, List<BuiltInCategory> present)
        {
            foreach (var bic in present)
            {
                try
                {
                    var cid = ElementIds.FromBuiltInCategory(bic);
                    if (v.GetCategoryHidden(cid)) continue;
                    if (new FilteredElementCollector(ctx.Doc, v.Id).OfCategory(bic).WhereElementIsNotElementType().GetElementCount() > 0) return true;
                }
                catch (Exception) { ctx.Omitted("views", "entourageVisible(" + bic + ")"); }
            }
            return false;
        }
    }
}
