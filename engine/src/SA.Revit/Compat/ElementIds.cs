using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace SA.Revit.Compat
{
    /// <summary>
    /// The only place element ids are converted. Ids are 64-bit everywhere and travel as decimal strings.
    ///
    /// Release notes: ElementId(Int64) and ElementId.Value were added in 2024, when ElementId(int) and IntegerValue
    /// were deprecated; both 32-bit members were removed in 2026 (the 2024 and 2025 reference assemblies list them,
    /// the 2026 and 2027 ones do not). This file uses only the 64-bit members, so it needs no #if; keep it that way
    /// and never call IntegerValue anywhere in the engine.
    /// </summary>
    internal static class ElementIds
    {
        public static long ToLong(ElementId id) => id.Value;

        public static string ToWire(ElementId id) => id.Value.ToString(CultureInfo.InvariantCulture);

        public static ElementId FromLong(long value) => new ElementId(value);

        public static bool IsValid(ElementId? id) => id != null && id != ElementId.InvalidElementId;

        /// <summary>Parses a wire id ("123456"). Rejects anything but a plain decimal integer.</summary>
        public static bool TryParse(string? text, out ElementId id)
        {
            id = ElementId.InvalidElementId;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!long.TryParse(text!.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return false;
            id = new ElementId(v);
            return true;
        }

        public static List<string> ToWire(IEnumerable<ElementId> ids)
        {
            var list = new List<string>();
            foreach (var id in ids) list.Add(ToWire(id));
            return list;
        }

        /// <summary>The built-in category an id stands for, or null. BuiltInCategory is a 64-bit enum from 2024.</summary>
        public static BuiltInCategory? ToBuiltInCategory(ElementId? id)
        {
            if (id is null || id == ElementId.InvalidElementId) return null;
            long v = id.Value;
            if (v >= 0) return null;
            var bic = (BuiltInCategory)v;
            return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic : (BuiltInCategory?)null;
        }

        public static ElementId FromBuiltInCategory(BuiltInCategory category) => new ElementId(category);

        public static BuiltInParameter? ToBuiltInParameter(ElementId? id)
        {
            if (id is null || id == ElementId.InvalidElementId) return null;
            long v = id.Value;
            if (v >= 0) return null;
            var bip = (BuiltInParameter)v;
            return Enum.IsDefined(typeof(BuiltInParameter), bip) ? bip : (BuiltInParameter?)null;
        }
    }
}
