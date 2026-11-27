using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>Lookups shared by the wave-2 material and pattern ops.</summary>
    internal static class Wave2Support
    {
        /// <summary>"r,g,b" to a colour, or null when it does not parse or a channel is outside 0-255.</summary>
        public static Color? Rgb(string? text)
        {
            if (text is null) return null;
            var parts = text.Split(',');
            if (parts.Length != 3) return null;
            var b = new byte[3];
            for (int i = 0; i < 3; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0 || v > 255) return null;
                else b[i] = (byte)v;
            return new Color(b[0], b[1], b[2]);
        }

        public static string RgbText(Color? c) => c is null || !c.IsValid ? "" : c.Red + "," + c.Green + "," + c.Blue;

        /// <summary>
        /// True when an op earlier in this job (by name) lists <paramref name="name"/> in <paramref name="listField"/>[].name.
        /// Used in preflight, where objects that an earlier op creates do not exist yet.
        /// </summary>
        public static bool EarlierOpCreates(OpContext ctx, string opName, string listField, string name, Func<JsonObject, bool>? also = null)
        {
            foreach (var o in ctx.Job.Job.Ops)
            {
                if (o.Index >= ctx.Op.Index) break;
                if (o.Op != opName || !(o.Args[listField] is JsonArray a)) continue;
                foreach (var item in a.OfType<JsonObject>())
                    if (item["name"] is JsonString s && string.Equals(s.Value, name, StringComparison.OrdinalIgnoreCase) && (also is null || also(item))) return true;
            }
            return false;
        }

        /// <summary>A drafting or model fill pattern by name; "solid" is the drafting solid fill. Null with an error when missing.</summary>
        public static FillPatternElement? Pattern(Document doc, string name, FillPatternTarget target, out string error)
        {
            error = "";
            if (string.Equals(name, "solid", StringComparison.OrdinalIgnoreCase))
            {
                var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                if (solid is null) error = "the model has no solid fill pattern";
                return solid;
            }
            var hit = FillPatternElement.GetFillPatternElementByName(doc, target, name);
            if (hit != null) return hit;
            var other = FillPatternElement.GetFillPatternElementByName(doc, target == FillPatternTarget.Model ? FillPatternTarget.Drafting : FillPatternTarget.Model, name);
            var names = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Where(f => f.GetFillPattern().Target == target).Select(f => f.Name).ToList();
            error = "no " + (target == FillPatternTarget.Model ? "model" : "drafting") + " fill pattern named '" + name + "'" +
                    (other != null ? " (there is a " + (target == FillPatternTarget.Model ? "drafting" : "model") + " pattern of that name)" : Support.Lookup.Suggest(name, names));
            return null;
        }
    }
}
