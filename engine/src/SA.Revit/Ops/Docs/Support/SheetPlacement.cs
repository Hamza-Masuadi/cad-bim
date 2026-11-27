using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Revit.Compat;

namespace SA.Revit.Ops.Docs.Support
{
    /// <summary>
    /// Putting views on sheets. Ordinary views go through Viewport (Viewport.CanAddViewToSheet, Viewport.Create,
    /// GetBoxOutline, GetLabelOutline, SetBoxCenter); schedules go through ScheduleSheetInstance, because the
    /// Viewport API documents that "schedule views are not handled by the Viewport class".
    ///
    /// All rectangles here are sheet coordinates in internal units (paper feet), as Revit reports them.
    /// </summary>
    internal static class SheetPlacement
    {
        /// <summary>
        /// Whether the view can go on the sheet, with the reason when it cannot. A view (other than a legend or a
        /// schedule) can be on one sheet only; a legend can be on many sheets; a schedule can be on many sheets but
        /// once per sheet.
        /// </summary>
        public static bool CanPlace(Document doc, ViewSheet sheet, View view, out string reason)
        {
            reason = "";
            if (view is ViewSheet) { reason = "a sheet cannot be placed on a sheet"; return false; }
            if (view.IsTemplate) { reason = "view templates cannot be placed on sheets"; return false; }
            if (view is ViewSchedule vs)
            {
                if (vs.IsTitleblockRevisionSchedule) { reason = "it is the title block's revision schedule"; return false; }
                if (vs.IsInternalKeynoteSchedule) { reason = "it is an internal keynote schedule"; return false; }
                if (DocsUtil.SheetsShowing(doc, view).Any(s => s.Id == sheet.Id)) { reason = "the schedule is already on sheet " + sheet.SheetNumber; return false; }
                return true;
            }
            if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) return true;
            var on = DocsUtil.SheetsShowing(doc, view);
            if (on.Count > 0)
                reason = view.ViewType == ViewType.Legend
                    ? "the legend is already on sheet " + sheet.SheetNumber
                    : "the view is already on sheet " + string.Join(", ", on.Select(s => s.SheetNumber)) + " (a view can be on one sheet only; duplicate it as a dependent view to show it twice)";
            else if (IsEmpty(view)) reason = "the view is empty (Revit does not place empty views)";
            else reason = "Revit does not allow a " + view.ViewType + " view on a sheet";
            return false;
        }

        public static bool IsEmpty(View view)
        {
            try
            {
                var o = view.Outline;
                return o is null || (Math.Abs(o.Max.U - o.Min.U) < 1e-9 && Math.Abs(o.Max.V - o.Min.V) < 1e-9);
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The view's size on paper before placement, from View.Outline ("the bounds of the view in paper space").
        /// With an active crop this is the crop (or annotation crop) divided by the scale. Null when it cannot be told
        /// (schedules, empty views).
        /// </summary>
        public static Point2? EstimateSize(View view)
        {
            if (view is ViewSchedule) return null;
            try
            {
                var o = view.Outline;
                if (o is null) return null;
                double w = o.Max.U - o.Min.U, h = o.Max.V - o.Min.V;
                return w > 1e-9 && h > 1e-9 ? new Point2(w, h) : (Point2?)null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Places the view so its box (viewport outline, label excluded) is centred on <paramref name="centre"/>.</summary>
        public static Element Place(Document doc, ViewSheet sheet, View view, XYZ centre)
        {
            if (view is ViewSchedule)
            {
                var si = ScheduleSheetInstance.Create(doc, sheet.Id, view.Id, centre);
                doc.Regenerate();
                var r = Box(doc, sheet, si);
                if (r.HasValue)
                {
                    var c = r.Value.Centre;
                    var delta = new XYZ(centre.X - c.X, centre.Y - c.Y, 0);
                    if (delta.GetLength() > 1e-9) { si.Point = si.Point + delta; doc.Regenerate(); }
                }
                return si;
            }
            var vp = Viewport.Create(doc, sheet.Id, view.Id, centre);
            doc.Regenerate();
            var bc = vp.GetBoxCenter();
            if (new XYZ(bc.X - centre.X, bc.Y - centre.Y, 0).GetLength() > 1e-9) { vp.SetBoxCenter(new XYZ(centre.X, centre.Y, bc.Z)); doc.Regenerate(); }
            return vp;
        }

        /// <summary>The placed view's box on the sheet: the viewport outline without its label, or a schedule's extent.</summary>
        public static Rect2? Box(Document doc, ViewSheet sheet, Element placed)
        {
            if (placed is Viewport vp)
            {
                var o = vp.GetBoxOutline();
                return Rect(o.MinimumPoint, o.MaximumPoint);
            }
            var bb = placed.get_BoundingBox(sheet);
            return bb is null ? (Rect2?)null : Rect(bb.Min, bb.Max);
        }

        /// <summary>The viewport label's outline, or null when there is none.</summary>
        public static Rect2? Label(Element placed)
        {
            if (!(placed is Viewport vp)) return null;
            try
            {
                var o = vp.GetLabelOutline();
                if (o is null || o.IsEmpty) return null;
                var r = Rect(o.MinimumPoint, o.MaximumPoint);
                return r.Width > 1e-9 || r.Height > 1e-9 ? r : (Rect2?)null;
            }
            catch (Exception) { return null; }
        }

        public static Rect2 Rect(XYZ a, XYZ b) => Rect2.FromCorners(new Point2(a.X, a.Y), new Point2(b.X, b.Y));

        public static Rect2 Union(Rect2 a, Rect2 b)
        {
            double minX = Math.Min(a.MinX, b.MinX), minY = Math.Min(a.MinY, b.MinY);
            return new Rect2(minX, minY, Math.Max(a.MaxX, b.MaxX) - minX, Math.Max(a.MaxY, b.MaxY) - minY);
        }

        /// <summary>The view shown by a placed viewport or schedule instance.</summary>
        public static ElementId ViewOf(Element placed) =>
            placed is Viewport vp ? vp.ViewId : placed is ScheduleSheetInstance si ? si.ScheduleId : ElementId.InvalidElementId;

        public static ElementId SheetOf(Element placed) =>
            placed is Viewport vp ? vp.SheetId : placed is ScheduleSheetInstance si ? si.OwnerViewId : ElementId.InvalidElementId;

        /// <summary>
        /// Viewport types, from any existing viewport's valid types (Element.GetValidTypes). Null when the model has
        /// no viewport yet, so nothing can be listed before one exists.
        /// </summary>
        public static List<ElementType>? ViewportTypes(Document doc, Viewport? sample = null)
        {
            sample ??= new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault();
            if (sample is null) return null;
            return sample.GetValidTypes().Select(id => doc.GetElement(id) as ElementType).Where(t => t != null).Cast<ElementType>().ToList();
        }

        /// <summary>Paper rectangle in the units given (OpContext.Paper: mm, or inches for an imperial job), for outputs and messages.</summary>
        public static SA.Core.Json.JsonObject Json(Ops.Support.RevitUnits u, Rect2 r) => new SA.Core.Json.JsonObject()
            .Add("min", DocsUtil.Xy(u.LengthFromInternal(r.MinX), u.LengthFromInternal(r.MinY)))
            .Add("max", DocsUtil.Xy(u.LengthFromInternal(r.MaxX), u.LengthFromInternal(r.MaxY)));

        public static string Text(Ops.Support.RevitUnits u, Rect2 r) =>
            "[" + Fmt(u.LengthFromInternal(r.MinX)) + ", " + Fmt(u.LengthFromInternal(r.MinY)) + "] - [" + Fmt(u.LengthFromInternal(r.MaxX)) + ", " + Fmt(u.LengthFromInternal(r.MaxY)) + "]";

        private static string Fmt(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
