using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;

namespace SA.Revit.Ops.Docs.Support
{
    /// <summary>
    /// The drawable area of a sheet: where viewports may go, clear of the border and the title strip.
    ///
    /// Revit has no API for a title block's drawable area, so it is derived from the title block's own lines:
    /// <list type="number">
    /// <item><b>Extents.</b> The title block instance's bounding box in the sheet view (get_BoundingBox(sheet)), in
    /// sheet coordinates. No title block on the sheet: no area can be derived and the job must pass <c>area</c>.</item>
    /// <item><b>Lines.</b> The instance's view-specific geometry (get_Geometry with Options.View = the sheet), walked
    /// through GeometryInstance.GetInstanceGeometry(), keeping straight lines that are horizontal or vertical within
    /// 1e-6. "Long" lines are those at least 60% of the extents' height (verticals) or width (horizontals).</item>
    /// <item><b>Frame.</b> Left edge = the innermost long vertical within the left 10% of the extents; right edge =
    /// the innermost long vertical within the right 10%; bottom and top likewise with long horizontals in the bottom
    /// and top 10%. A side with no such line uses the extents edge.</item>
    /// <item><b>Title strip.</b> A right-hand strip is a long vertical strictly inside the frame, between 55% of the
    /// width and 1% short of the frame's right edge; the leftmost such line becomes the drawable right edge. A
    /// bottom strip is a long horizontal between 1% above the frame bottom and 45% of the height; the topmost such
    /// line becomes the drawable bottom edge. Both are checked, so an L-shaped layout keeps the larger clear block
    /// that excludes both.</item>
    /// <item><b>Fallback.</b> When no long line at all is found (a title block drawn with filled regions, nested
    /// annotation without lines, or an empty family), the area is the extents deflated by
    /// <see cref="FallbackMarginMm"/> mm of paper on every side, and the result is marked as a fallback so the op
    /// warns that the title strip was not excluded and the job should pass <c>area</c>.</item>
    /// </list>
    /// The job's <c>margin</c> (packing option) is applied by the packer inside whatever area this returns.
    /// </summary>
    internal static class TitleBlockArea
    {
        public const double FallbackMarginMm = 15;

        public sealed class Result
        {
            public Rect2 Area;
            public Rect2 Extents;
            public bool Fallback;
            public string How = "";
            public FamilyInstance? TitleBlock;
        }

        public static Result? Derive(Document doc, ViewSheet sheet)
        {
            var tb = SheetCreate.TitleBlockOn(doc, sheet);
            if (tb is null) return null;
            var bb = tb.get_BoundingBox(sheet);
            if (bb is null) return null;
            var ext = SheetPlacement.Rect(bb.Min, bb.Max);
            var res = new Result { Extents = ext, TitleBlock = tb };
            var lines = new List<Line>();
            try
            {
                var geo = tb.get_Geometry(new Options { View = sheet });
                if (geo != null) Collect(geo, lines, 0);
            }
            catch (Exception) { }
            double w = ext.Width, h = ext.Height;
            const double axis = 1e-6;
            var verticals = lines.Where(l => Math.Abs(l.Direction.X) < axis && l.Length >= 0.6 * h)
                .Select(l => l.GetEndPoint(0).X).ToList();
            var horizontals = lines.Where(l => Math.Abs(l.Direction.Y) < axis && l.Length >= 0.6 * w)
                .Select(l => l.GetEndPoint(0).Y).ToList();
            if (verticals.Count == 0 && horizontals.Count == 0)
            {
                double m = UnitUtils.ConvertToInternalUnits(FallbackMarginMm, UnitTypeId.Millimeters);
                res.Area = ext.Deflate(m);
                res.Fallback = true;
                res.How = "no border or strip lines found in the title block; extents less " + FallbackMarginMm + " mm";
                return res;
            }
            double left = verticals.Where(x => x <= ext.MinX + 0.10 * w).DefaultIfEmpty(ext.MinX).Max();
            double right = verticals.Where(x => x >= ext.MaxX - 0.10 * w).DefaultIfEmpty(ext.MaxX).Min();
            double bottom = horizontals.Where(y => y <= ext.MinY + 0.10 * h).DefaultIfEmpty(ext.MinY).Max();
            double top = horizontals.Where(y => y >= ext.MaxY - 0.10 * h).DefaultIfEmpty(ext.MaxY).Min();
            var how = new List<string> { "frame from the title block's border lines" };
            var strip = verticals.Where(x => x > ext.MinX + 0.55 * w && x < right - 0.01 * w).ToList();
            if (strip.Count > 0) { right = strip.Min(); how.Add("right-hand title strip excluded"); }
            var bstrip = horizontals.Where(y => y > bottom + 0.01 * h && y < ext.MinY + 0.45 * h).ToList();
            if (bstrip.Count > 0) { bottom = bstrip.Max(); how.Add("bottom title strip excluded"); }
            if (!(right > left) || !(top > bottom))
            {
                double m = UnitUtils.ConvertToInternalUnits(FallbackMarginMm, UnitTypeId.Millimeters);
                res.Area = ext.Deflate(m);
                res.Fallback = true;
                res.How = "the title block's lines gave no usable rectangle; extents less " + FallbackMarginMm + " mm";
                return res;
            }
            res.Area = new Rect2(left, bottom, right - left, top - bottom);
            res.How = string.Join("; ", how);
            return res;
        }

        private static void Collect(GeometryElement geo, List<Line> into, int depth)
        {
            if (depth > 4) return;
            foreach (GeometryObject g in geo)
            {
                if (g is Line l) into.Add(l);
                else if (g is GeometryInstance gi)
                {
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) Collect(inner, into, depth + 1);
                }
            }
        }
    }
}
