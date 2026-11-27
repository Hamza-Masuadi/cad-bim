using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "jobPlacement" (data/checks/FACTS.md 2.8): in a job's verify step, one record per element the job created or
    /// modified that has a category and is neither a type nor a view; id = element id. In an audit there is no job: the
    /// table is empty and marked cut, so its checks report incomplete rather than pass.
    ///
    /// Fields: category, viewSpecific, shown, reason (when shown is notChecked), candidateViews, viewsFull, viewsPartial,
    /// views (string[]: names of the views that show at least part of it).
    /// Candidate views: a view-specific element of a model category (detail lines, detail items, filled regions) is
    /// judged in its owner view; a model element in every floor, ceiling, area and structural plan placed on a sheet whose
    /// GenLevel is the element's level. Only views with an active crop count, and a view where the element or its
    /// category is hidden does not show it. shown: "full" when a candidate's crop holds all of the element's box,
    /// "partial" when crops hold only parts, "none" when no crop holds any of it, "notChecked" with a reason otherwise
    /// (annotation categories, whose clipping depends on the annotation crop; no level; no candidate view; hidden in
    /// every candidate).
    /// The element box is Element.get_BoundingBox (the owner view's for view-specific elements, the model box otherwise)
    /// mapped into crop coordinates; a non-rectangular crop counts as its bounding rectangle, and a plan's view range is
    /// not considered.
    /// </summary>
    internal sealed class JobPlacementCollector : IFactCollector
    {
        public string Table => "jobPlacement";

        private static readonly HashSet<ViewType> PlanTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.EngineeringPlan,
        };

        public void Collect(AuditContext ctx, FactTable table)
        {
            if (ctx.JobScope is null)
            {
                ctx.Truncated(Table, Facts.RecordsPart, 0, 0, "no job: these checks judge what a job created or modified");
                return;
            }
            var doc = ctx.Doc;
            var placed = SA.Revit.Snapshot.SheetIndex.ViewToSheet(doc);
            var plansByLevel = new Dictionary<long, List<(View View, CropFrame Crop)>>();
            foreach (long vid in placed.Keys)
            {
                if (!(doc.GetElement(ElementIds.FromLong(vid)) is ViewPlan vp) || vp.IsTemplate || !PlanTypes.Contains(vp.ViewType)) continue;
                var level = vp.GenLevel;
                var crop = CropFrame.Of(vp);
                if (level is null || crop is null) continue;
                long lid = ElementIds.ToLong(level.Id);
                if (!plansByLevel.TryGetValue(lid, out var list)) plansByLevel[lid] = list = new List<(View, CropFrame)>();
                list.Add((vp, crop));
            }

            foreach (long id in ctx.JobScope.OrderBy(x => x))
            {
                var e = doc.GetElement(ElementIds.FromLong(id));
                if (e is null || e is ElementType || e is View || e.Category is null) continue;
                var r = table.Add(ElementIds.ToWire(e.Id));
                r.Set("category", AuditCache.CategoryName(e.Category) ?? "");
                bool viewSpecific = e.ViewSpecific;
                r.Set("viewSpecific", viewSpecific);
                try
                {
                    var candidates = new List<(View View, CropFrame Crop)>();
                    string? reason = null;
                    if (viewSpecific)
                    {
                        if (e.Category.CategoryType == CategoryType.Annotation) reason = "annotation: its clipping depends on the annotation crop";
                        else if (!(doc.GetElement(e.OwnerViewId) is View owner) || owner is ViewSheet) reason = "drawn on a sheet or in a view without a crop";
                        else
                        {
                            var crop = CropFrame.Of(owner);
                            if (crop is null) reason = "its view '" + owner.Name + "' has no active crop";
                            else candidates.Add((owner, crop));
                        }
                    }
                    else if (!ElementIds.IsValid(e.LevelId)) reason = "no level";
                    else if (!plansByLevel.TryGetValue(ElementIds.ToLong(e.LevelId), out var plans)) reason = "no cropped plan view on a sheet at its level";
                    else candidates.AddRange(plans);

                    r.Set("candidateViews", candidates.Count);
                    if (reason != null) { r.Set("shown", "notChecked").Set("reason", reason); continue; }

                    int full = 0, partial = 0, judged = 0;
                    var names = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var (view, crop) in candidates)
                    {
                        if (e.IsHidden(view) || view.GetCategoryHidden(e.Category.Id)) continue;
                        var bb = e.get_BoundingBox(viewSpecific ? view : null);
                        if (bb is null) continue;
                        judged++;
                        var p = crop.Classify(bb);
                        if (p == Placement.None) continue;
                        if (p == Placement.Full) full++; else partial++;
                        names.Add(view.Name ?? "");
                    }
                    if (judged == 0) { r.Set("shown", "notChecked").Set("reason", "hidden, or without a box, in every candidate view"); continue; }
                    r.Set("shown", full > 0 ? "full" : partial > 0 ? "partial" : "none")
                        .Set("viewsFull", full).Set("viewsPartial", partial).Set("views", JsonArray.FromStrings(names));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { ctx.Omitted(Table, "shown"); }
            }
        }
    }
}
