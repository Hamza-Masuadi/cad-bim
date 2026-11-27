using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Snapshot
{
    /// <summary>Which sheet each view is placed on (viewports and schedule instances), and view scales.</summary>
    internal static class SheetIndex
    {
        /// <summary>View id to sheet number.</summary>
        public static Dictionary<long, string> ViewToSheet(Document doc)
        {
            var map = new Dictionary<long, string>();
            var sheets = new Dictionary<long, string>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                sheets[ElementIds.ToLong(s.Id)] = s.SheetNumber ?? "";
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                if (sheets.TryGetValue(ElementIds.ToLong(vp.SheetId), out var num)) map[ElementIds.ToLong(vp.ViewId)] = num;
            }
            foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                if (si.IsTitleblockRevisionSchedule) continue;
                if (sheets.TryGetValue(ElementIds.ToLong(si.OwnerViewId), out var num)) map[ElementIds.ToLong(si.ScheduleId)] = num;
            }
            return map;
        }

        /// <summary>The view's scale ratio (100 for 1:100), or null where the view has no scale.</summary>
        public static JsonValue Scale(View v)
        {
            if (v is ViewSheet || v is ViewSchedule || v.ViewType == ViewType.DrawingSheet) return JsonValue.Null;
            try
            {
                int s = v.Scale;
                return s > 0 ? (JsonValue)s : JsonValue.Null;
            }
            catch (Exception) { return JsonValue.Null; }
        }
    }
}
