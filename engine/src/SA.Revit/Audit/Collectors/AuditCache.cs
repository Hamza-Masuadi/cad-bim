using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Values several Revit collectors need, computed once per audit through <see cref="AuditContext.Shared{T}"/>, and
    /// small conversions shared by them. Everything here reads only; nothing regenerates the model.
    /// </summary>
    internal static class AuditCache
    {
        /// <summary>
        /// Generous size limits. A table or field cut by one of them is named in facts meta "truncated"; nothing is
        /// cut silently.
        /// </summary>
        public const int MaxRecordsPerTable = 2000000;

        /// <summary>Upper bound on Element.IsHidden calls for views.hiddenElementCount (views x candidate elements).</summary>
        public const long MaxHiddenChecks = 60000000;

        public static JsonValue Mm(double internalLength) =>
            RevitUnits.Num(UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters));

        public static double MmValue(double internalLength) => UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters);

        /// <summary>The BuiltInCategory name of a category ("OST_Walls"), or null for none or a non-built-in category.</summary>
        public static string? CategoryName(Category? c)
        {
            if (c is null) return null;
            var bic = ElementIds.ToBuiltInCategory(c.Id);
            return bic.HasValue ? bic.Value.ToString() : null;
        }

        public static bool Is(Element e, BuiltInCategory bic) => e.Category != null && ElementIds.ToBuiltInCategory(e.Category.Id) == bic;

        /// <summary>FACTS.md section 0: the project's Length display unit mapped to mm, cm, m, in or ft; null for any other.</summary>
        public static string? ProjectLengthSymbol(Document doc)
        {
            ForgeTypeId u;
            try { u = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId(); }
            catch (Exception) { return null; }
            if (u.Equals(UnitTypeId.Millimeters)) return "mm";
            if (u.Equals(UnitTypeId.Centimeters)) return "cm";
            if (u.Equals(UnitTypeId.Meters) || u.Equals(UnitTypeId.MetersCentimeters)) return "m";
            if (u.Equals(UnitTypeId.Inches) || u.Equals(UnitTypeId.FractionalInches)) return "in";
            if (u.Equals(UnitTypeId.Feet) || u.Equals(UnitTypeId.FeetFractionalInches)) return "ft";
            return null;
        }

        /// <summary>FACTS.md 2.5 unitSystem: imperial for feet, inches, feet-and-fractional-inches and fractional inches.</summary>
        public static bool IsImperialUnit(ForgeTypeId u) =>
            u.Equals(UnitTypeId.Feet) || u.Equals(UnitTypeId.Inches) || u.Equals(UnitTypeId.FeetFractionalInches) || u.Equals(UnitTypeId.FractionalInches);

        /// <summary>True when the unit is one Revit accepts for the Length spec.</summary>
        public static bool IsLengthUnit(ForgeTypeId u)
        {
            try { return UnitUtils.GetValidUnits(SpecTypeId.Length).Any(x => x.Equals(u)); }
            catch (Exception) { return false; }
        }

        /// <summary>Placed instances per type id: one pass over every non-type element, counting GetTypeId().</summary>
        public static Dictionary<long, int> TypeUse(AuditContext ctx) => ctx.Shared("typeUse", () =>
        {
            var map = new Dictionary<long, int>();
            foreach (var e in new FilteredElementCollector(ctx.Doc).WhereElementIsNotElementType())
            {
                ElementId t;
                try { t = e.GetTypeId(); } catch (Exception) { continue; }
                if (!ElementIds.IsValid(t)) continue;
                long k = ElementIds.ToLong(t);
                map[k] = map.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            return map;
        });

        /// <summary>Document.GetWarnings(), read once.</summary>
        public static IList<FailureMessage> Warnings(AuditContext ctx) => ctx.Shared("warnings", () => (IList<FailureMessage>)ctx.Doc.GetWarnings().ToList());

        /// <summary>Workset names by workset id (WorksetTable.GetWorkset), cached; null when the model is not workshared.</summary>
        public static string? WorksetName(AuditContext ctx, Element e)
        {
            if (!ctx.Doc.IsWorkshared) return null;
            var cache = ctx.Shared("worksetNames", () => new Dictionary<int, string>());
            WorksetId id;
            try { id = e.WorksetId; } catch (Exception) { return null; }
            if (id == WorksetId.InvalidWorksetId) return null;
            if (cache.TryGetValue(id.IntegerValue, out var name)) return name;
            try { name = ctx.Doc.GetWorksetTable().GetWorkset(id)?.Name; } catch (Exception) { name = null; }
            if (name != null) cache[id.IntegerValue] = name;
            return name;
        }

        /// <summary>A trimmed string parameter value, or null when absent or empty.</summary>
        public static string? Text(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p is null || !p.HasValue) return null;
                string? s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                s = s?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch (Exception) { return null; }
        }

        /// <summary>A double parameter in internal units, or null.</summary>
        public static double? Double(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p is null || !p.HasValue || p.StorageType != StorageType.Double) return null;
                return p.AsDouble();
            }
            catch (Exception) { return null; }
        }

        /// <summary>An element id parameter, or null.</summary>
        public static ElementId? Id(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p is null || p.StorageType != StorageType.ElementId) return null;
                var id = p.AsElementId();
                return ElementIds.IsValid(id) ? id : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>An integer parameter, or null.</summary>
        public static int? Int(Element e, BuiltInParameter bip)
        {
            try
            {
                var p = e.get_Parameter(bip);
                if (p is null || !p.HasValue || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger();
            }
            catch (Exception) { return null; }
        }
    }
}
