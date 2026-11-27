using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Core.Imaging;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "annotations" (data/checks/FACTS.md 2.7): one record per text note, tag, room/area/space tag and dimension
    /// text owned by a view; id = element id ("&lt;id&gt;:&lt;n&gt;" for segment n of a multi-segment dimension).
    ///
    /// Views: in a job's verify step, the owner views of the view-specific elements the job created or modified; in an
    /// audit, every non-template view placed on a sheet. At most <see cref="MaxViews"/> views (a safety ceiling); past
    /// it the table is marked cut.
    ///
    /// Fields: category, kind (text, tag, roomTag, dimension), viewId, viewName, minU, minV, maxU, maxV (the box in the
    /// view's plane, paper millimetres from View.Origin along RightDirection and UpDirection, model feet x 304.8 / scale),
    /// hasLeader (TextNote.LeaderCount &gt; 0, IndependentTag.HasLeader, SpatialElementTag.HasLeader), estimated, byJob.
    /// Text and tag boxes are Element.get_BoundingBox(view) projected; that box includes a leader, so checks leave out
    /// hasLeader records. In views whose axes are not model axes the projected box of the model-axis-aligned box is too
    /// large, and estimated is true. Dimension text has no box in the API: it is estimated around TextPosition (per
    /// segment for multi-segment dimensions) from the value string's length x TEXT_SIZE x TEXT_WIDTH_SCALE x 0.7, turned
    /// with the dimension line; estimated is true. Hidden elements (Element.IsHidden, category hidden in the view) are
    /// left out.
    /// </summary>
    internal sealed class AnnotationsCollector : IFactCollector
    {
        public string Table => "annotations";

        public const int MaxViews = 20000;
        private const double GlyphWidth = 0.7;

        private static readonly List<Type> Classes = new List<Type> { typeof(TextNote), typeof(IndependentTag), typeof(SpatialElementTag), typeof(Dimension) };

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            var views = Views(ctx);
            if (views.Count > MaxViews)
            {
                ctx.Truncated(Table, "records", 0, views.Count, "more than " + MaxViews + " views; no view was read");
                return;
            }
            long total = 0;
            foreach (var view in views)
            {
                var plane = new ViewPlane(view);
                string viewId = ElementIds.ToWire(view.Id);
                var hiddenCategories = new Dictionary<long, bool>();
                var elements = new FilteredElementCollector(doc).OwnedByView(view.Id).WherePasses(new ElementMulticlassFilter(Classes)).ToElements();
                foreach (var e in elements)
                {
                    try
                    {
                        if (e.IsHidden(view)) continue;
                        var cat = e.Category;
                        if (cat != null)
                        {
                            long cid = ElementIds.ToLong(cat.Id);
                            if (!hiddenCategories.TryGetValue(cid, out bool hiddenCat)) hiddenCategories[cid] = hiddenCat = view.GetCategoryHidden(cat.Id);
                            if (hiddenCat) continue;
                        }
                        foreach (var (rid, kind, box, leader, estimated) in Boxes(e, view, plane))
                        {
                            total++;
                            if (table.Records.Count >= AuditCache.MaxRecordsPerTable) continue;
                            var r = table.Add(rid);
                            r.Set("category", AuditCache.CategoryName(cat) ?? "").Set("kind", kind).Set("viewId", viewId).Set("viewName", view.Name ?? "")
                                .Set("minU", box.MinX).Set("minV", box.MinY).Set("maxU", box.MaxX).Set("maxV", box.MaxY)
                                .Set("hasLeader", leader).Set("estimated", estimated);
                            if (ctx.JobScope != null) r.Set("byJob", ctx.JobScope.Contains(ElementIds.ToLong(e.Id)));
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException) { ctx.Omitted(Table, "minU"); }
                }
            }
            if (total > table.Records.Count) ctx.Truncated(Table, "records", table.Records.Count, total, "record limit");
        }

        private static List<View> Views(AuditContext ctx)
        {
            var doc = ctx.Doc;
            var list = new List<View>();
            if (ctx.JobScope != null)
            {
                var seen = new HashSet<long>();
                foreach (long id in ctx.JobScope)
                {
                    var e = doc.GetElement(ElementIds.FromLong(id));
                    if (e is null || !e.ViewSpecific || !ElementIds.IsValid(e.OwnerViewId)) continue;
                    if (seen.Add(ElementIds.ToLong(e.OwnerViewId)) && doc.GetElement(e.OwnerViewId) is View v && !v.IsTemplate) list.Add(v);
                }
                return list;
            }
            var placed = SA.Revit.Snapshot.SheetIndex.ViewToSheet(doc);
            foreach (long id in placed.Keys)
                if (doc.GetElement(ElementIds.FromLong(id)) is View v && !v.IsTemplate && !(v is ViewSchedule)) list.Add(v);
            return list;
        }

        private static IEnumerable<(string Id, string Kind, WorldBox Box, bool Leader, bool Estimated)> Boxes(Element e, View view, ViewPlane plane)
        {
            string id = ElementIds.ToWire(e.Id);
            if (e is Dimension d)
            {
                foreach (var x in DimensionBoxes(d, id, plane)) yield return x;
                yield break;
            }
            var bb = e.get_BoundingBox(view);
            if (bb is null) yield break;
            bool leader = e is TextNote tn ? tn.LeaderCount > 0 : e is IndependentTag it ? it.HasLeader : e is SpatialElementTag st && st.HasLeader;
            string kind = e is TextNote ? "text" : e is SpatialElementTag ? "roomTag" : "tag";
            yield return (id, kind, plane.Box(bb), leader, !plane.AxisAligned);
        }

        private static IEnumerable<(string Id, string Kind, WorldBox Box, bool Leader, bool Estimated)> DimensionBoxes(Dimension d, string id, ViewPlane plane)
        {
            var type = d.DimensionType;
            double size = type?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
            if (!(size > 0)) yield break;
            double widthFactor = type?.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)?.AsDouble() ?? 1;
            if (!(widthFactor > 0)) widthFactor = 1;
            bool vertical = false;
            try { if (d.Curve is Line line) vertical = plane.MostlyVertical(line.Direction); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            var parts = new List<(string, XYZ, string)>();
            if (d.NumberOfSegments > 1)
            {
                int n = 0;
                foreach (DimensionSegment s in d.Segments)
                {
                    XYZ? p = null;
                    try { p = s.TextPosition; } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                    if (p != null) parts.Add((id + ":" + n, p, s.ValueString ?? ""));
                    n++;
                }
            }
            else
            {
                XYZ? p = null;
                try { p = d.TextPosition; } catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
                if (p != null) parts.Add((id, p, d.ValueString ?? ""));
            }
            double h = size * 304.8;
            foreach (var (rid, p, text) in parts)
            {
                if (text.Trim().Length == 0) continue;
                double w = text.Length * size * widthFactor * GlyphWidth * 304.8;
                var (u, v) = plane.Point(p);
                double hu = (vertical ? h : w) / 2, hv = (vertical ? w : h) / 2;
                yield return (rid, "dimension", new WorldBox(u - hu, v - hv, u + hu, v + hv), false, true);
            }
        }
    }
}
