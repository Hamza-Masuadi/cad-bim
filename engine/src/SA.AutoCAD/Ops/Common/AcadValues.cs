using System;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Json;
using SA.Core.Standards;

namespace SA.AutoCAD.Ops.Common
{
    /// <summary>Conversions between job values (colour, lineweight, transparency, names) and AutoCAD values.</summary>
    internal static class AcadValues
    {
        /// <summary>
        /// Parses "ACI number 1-255, bylayer, byblock, #RRGGBB or r,g,b". <paramref name="allowByLayer"/> is false for
        /// layer colours (a layer cannot be ByLayer or ByBlock).
        /// </summary>
        internal static bool TryParseColor(JsonValue? v, bool allowByLayer, out Color? color, out string error)
        {
            color = null;
            error = "";
            if (v is null || v.IsNull) { error = "no colour given"; return false; }
            if (v is JsonString s)
            {
                string t = s.Value.Trim().ToLowerInvariant();
                if (t == "bylayer" || t == "byblock")
                {
                    if (!allowByLayer) { error = "a layer colour cannot be " + t; return false; }
                    color = Color.FromColorIndex(t == "bylayer" ? ColorMethod.ByLayer : ColorMethod.ByBlock, t == "bylayer" ? (short)256 : (short)0);
                    return true;
                }
                if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int aciText)) v = aciText;
            }
            if (!ColorSpec.TryParse(v, out var spec) || spec is null)
            {
                error = "expected an ACI number 1-255, \"#RRGGBB\", \"r,g,b\"" + (allowByLayer ? ", \"bylayer\" or \"byblock\"" : "");
                return false;
            }
            color = ToColor(spec);
            return true;
        }

        internal static Color ToColor(ColorSpec spec) =>
            spec.Aci.HasValue ? Color.FromColorIndex(ColorMethod.ByAci, (short)spec.Aci.Value) : Color.FromRgb(spec.R, spec.G, spec.B);

        /// <summary>Same colour for readback: ACI index, or RGB for true colours; ByLayer and ByBlock by method.</summary>
        internal static bool SameColor(Color a, Color b)
        {
            if (a.ColorMethod != b.ColorMethod)
            {
                // ACI 1-255 may come back as ByAci or as ByColor holding an ACI; compare by index when both have one.
                if (a.IsByAci && b.IsByAci) return a.ColorIndex == b.ColorIndex;
                return false;
            }
            if (a.ColorMethod == ColorMethod.ByColor) return a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;
            return a.ColorIndex == b.ColorIndex;
        }

        internal static JsonValue ColorJson(Color c)
        {
            switch (c.ColorMethod)
            {
                case ColorMethod.ByLayer: return "bylayer";
                case ColorMethod.ByBlock: return "byblock";
                case ColorMethod.ByColor:
                    return "#" + c.Red.ToString("X2", CultureInfo.InvariantCulture) + c.Green.ToString("X2", CultureInfo.InvariantCulture) + c.Blue.ToString("X2", CultureInfo.InvariantCulture);
                case ColorMethod.ByAci: return (int)c.ColorIndex;
                default: return c.ColorIndex;
            }
        }

        /// <summary>
        /// Parses a lineweight: millimetres from the standard set, or "bylayer", "byblock", "default". Layers accept only
        /// millimetres and "default".
        /// </summary>
        internal static bool TryParseLineWeight(JsonValue? v, bool forLayer, out LineWeight lw, out string error)
        {
            lw = LineWeight.ByLineWeightDefault;
            error = "";
            if (v is JsonString s)
            {
                string t = s.Value.Trim().ToLowerInvariant();
                if (t == "default") { lw = LineWeight.ByLineWeightDefault; return true; }
                if (t == "bylayer" || t == "byblock")
                {
                    if (forLayer) { error = "a layer lineweight cannot be " + t; return false; }
                    lw = t == "bylayer" ? LineWeight.ByLayer : LineWeight.ByBlock;
                    return true;
                }
                if (!double.TryParse(t.Replace("mm", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mmText))
                {
                    error = "expected millimetres such as 0.25, or \"default\"" + (forLayer ? "" : ", \"bylayer\", \"byblock\"");
                    return false;
                }
                v = mmText;
            }
            if (!(v is JsonNumber n)) { error = "expected millimetres such as 0.25"; return false; }
            int hundredths = (int)Math.Round(n.Value * 100.0);
            if (Math.Abs(n.Value * 100.0 - hundredths) > 1e-6 || !Enum.IsDefined(typeof(LineWeight), hundredths) || hundredths < 0)
            {
                error = n.Value.ToString(CultureInfo.InvariantCulture) + " mm is not an AutoCAD lineweight (0.00, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11)";
                return false;
            }
            lw = (LineWeight)hundredths;
            return true;
        }

        internal static JsonValue LineWeightJson(LineWeight lw)
        {
            switch (lw)
            {
                case LineWeight.ByLayer: return "bylayer";
                case LineWeight.ByBlock: return "byblock";
                case LineWeight.ByLineWeightDefault: return "default";
                default: return Math.Round((int)lw / 100.0, 2);
            }
        }

        /// <summary>Percent transparency 0-90 to an AutoCAD Transparency (alpha 255 = opaque).</summary>
        internal static Transparency ToTransparency(int percent) =>
            new Transparency((byte)Math.Round(255.0 * (100 - percent) / 100.0));

        /// <summary>Percent transparency of a Transparency, or null for ByLayer/ByBlock.</summary>
        internal static int? TransparencyPercent(Transparency t)
        {
            if (!t.IsByAlpha) return null;
            return (int)Math.Round(100.0 - t.Alpha * 100.0 / 255.0);
        }

        /// <summary>True when <paramref name="name"/> is a valid symbol table name with EXTNAMES = 1 rules.</summary>
        internal static bool IsValidSymbolName(string name, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(name)) { error = "the name is empty"; return false; }
            try
            {
                SymbolUtilityServices.ValidateSymbolName(name, false);
                return true;
            }
            catch (Exception e)
            {
                error = "'" + name + "' is not a valid AutoCAD name (" + e.Message + "); names cannot contain < > / \\ \" : ; ? * | , = `";
                return false;
            }
        }

        internal static double Round(double v, int digits = 9) => Math.Round(v, digits);
    }
}
