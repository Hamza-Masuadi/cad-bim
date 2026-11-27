using System;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.Core.Json;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>Value conversions shared by the collectors, written exactly as data/checks/FACTS.md specifies.</summary>
    internal static class FactValues
    {
        internal const double FlatTolerance = 1e-6;
        internal const double NormalTolerance = 1e-9;

        /// <summary>
        /// UnitsValue as the contract's unit word: unitless, in, ft, mm, cm, m; any other value as its enum name with the
        /// leading capitals lower-cased (Yards -> yards, USSurveyFeet -> usSurveyFeet).
        /// </summary>
        internal static string UnitWord(UnitsValue u)
        {
            switch ((int)u)
            {
                case 0: return "unitless";
                case 1: return "in";
                case 2: return "ft";
                case 4: return "mm";
                case 5: return "cm";
                case 6: return "m";
            }
            string name = u.ToString();
            if (name.Length == 0 || char.IsDigit(name[0])) return "code" + ((int)u).ToString(CultureInfo.InvariantCulture);
            int caps = 0;
            while (caps < name.Length && char.IsUpper(name[caps])) caps++;
            if (caps == 0) return name;
            if (caps == name.Length) return name.ToLowerInvariant();
            int lower = caps == 1 ? 1 : caps - 1;
            return name.Substring(0, lower).ToLowerInvariant() + name.Substring(lower);
        }

        /// <summary>
        /// The facts unit (meta "units.length"): mm, cm, m, in, ft; for unitless drawings in (MEASUREMENT 0) or mm
        /// (MEASUREMENT 1) with <paramref name="assumed"/> set; null for every other INSUNITS value.
        /// </summary>
        internal static string? MetaLengthUnit(Database db, out bool assumed)
        {
            assumed = false;
            switch ((int)db.Insunits)
            {
                case 1: return "in";
                case 2: return "ft";
                case 4: return "mm";
                case 5: return "cm";
                case 6: return "m";
                case 0:
                    assumed = true;
                    return (int)db.Measurement == 0 ? "in" : "mm";
                default: return null;
            }
        }

        /// <summary>
        /// Millimetres per drawing unit, with the contract's unitless fallback (25.4 when MEASUREMENT is 0, else 1).
        /// Null only for an INSUNITS code with no defined size.
        /// </summary>
        internal static double? ModelUnitMm(Database db)
        {
            int code = (int)db.Insunits;
            if (code == 0) return (int)db.Measurement == 0 ? 25.4 : 1.0;
            return DrawingUnits.MillimetresPer(code);
        }

        /// <summary>Layer colour key: ACI number as text ("7"), true and colour-book colours as "r,g,b".</summary>
        internal static string LayerColorKey(Color c, out int? aci)
        {
            aci = null;
            if (c.ColorMethod == ColorMethod.ByColor) return Rgb(c);
            int idx = c.ColorMethod == ColorMethod.Foreground ? 7 : c.ColorIndex;
            aci = idx;
            return idx.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Entity colour: bylayer, byblock, the ACI number as text, or "r,g,b" for true and colour-book colours.</summary>
        internal static string EntityColor(Color c)
        {
            switch (c.ColorMethod)
            {
                case ColorMethod.ByLayer: return "bylayer";
                case ColorMethod.ByBlock: return "byblock";
                case ColorMethod.ByColor: return Rgb(c);
                case ColorMethod.Foreground: return "7";
                default: return ((int)c.ColorIndex).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string Rgb(Color c) =>
            c.Red.ToString(CultureInfo.InvariantCulture) + "," + c.Green.ToString(CultureInfo.InvariantCulture) + "," + c.Blue.ToString(CultureInfo.InvariantCulture);

        /// <summary>Entity linetype: bylayer or byblock (compared without case), else the linetype name as stored.</summary>
        internal static string EntityLinetype(string? name)
        {
            if (string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase)) return "bylayer";
            if (string.Equals(name, "ByBlock", StringComparison.OrdinalIgnoreCase)) return "byblock";
            return name ?? "";
        }

        /// <summary>Lineweight: bylayer, byblock, default, else millimetres (enum value / 100).</summary>
        internal static JsonValue LineWeightValue(LineWeight lw)
        {
            switch (lw)
            {
                case LineWeight.ByLayer: return "bylayer";
                case LineWeight.ByBlock: return "byblock";
                case LineWeight.ByLineWeightDefault: return "default";
                default: return Math.Round((int)lw / 100.0, 2);
            }
        }

        /// <summary>True when the colour, linetype or lineweight is not ByBlock or ByLayer (ByLineWeightDefault counts as hard).</summary>
        internal static bool HasHardProperty(Entity e)
        {
            var cm = e.Color.ColorMethod;
            if (cm != ColorMethod.ByLayer && cm != ColorMethod.ByBlock) return true;
            string lt = EntityLinetype(e.Linetype);
            if (lt != "bylayer" && lt != "byblock") return true;
            return e.LineWeight != LineWeight.ByLayer && e.LineWeight != LineWeight.ByBlock;
        }

        /// <summary>"full" for a rooted path (drive letter, UNC or "/"), "relative" for .\ ..\ ./ ../, "none" for a bare name.</summary>
        internal static string PathType(string? path)
        {
            string p = (path ?? "").Trim();
            if (p.Length == 0) return "none";
            if (p.StartsWith("\\\\", StringComparison.Ordinal) || p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("\\", StringComparison.Ordinal)) return "full";
            if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':') return "full";
            if (p.StartsWith(".\\", StringComparison.Ordinal) || p.StartsWith("..\\", StringComparison.Ordinal) ||
                p.StartsWith("./", StringComparison.Ordinal) || p.StartsWith("../", StringComparison.Ordinal)) return "relative";
            if (p.IndexOf('\\') >= 0 || p.IndexOf('/') >= 0) return "relative";
            return "none";
        }

        /// <summary>XrefStatus as the contract's word: resolved, unloaded, unreferenced, not_found, unresolved.</summary>
        internal static string XrefStatusWord(XrefStatus s)
        {
            switch (s)
            {
                case XrefStatus.Resolved: return "resolved";
                case XrefStatus.Unloaded: return "unloaded";
                case XrefStatus.Unreferenced: return "unreferenced";
                case XrefStatus.FileNotFound: return "not_found";
                case XrefStatus.Unresolved: return "unresolved";
                default: return s.ToString().ToLowerInvariant();
            }
        }

        internal static bool ZOk(double z) => Math.Abs(z) <= FlatTolerance;

        internal static bool ZOk(Point3d p) => Math.Abs(p.Z) <= FlatTolerance;

        internal static bool NormalOk(Vector3d n) =>
            Math.Abs(n.X) <= NormalTolerance && Math.Abs(n.Y) <= NormalTolerance && Math.Abs(n.Z - 1.0) <= NormalTolerance;

        /// <summary>(1 / CustomScale) x (model unit mm / paper unit mm); null when CustomScale is not positive.</summary>
        internal static double? ViewportRatio(double customScale, double modelUnitMm, double paperUnitMm)
        {
            if (!(customScale > 0) || !(paperUnitMm > 0)) return null;
            return 1.0 / customScale * (modelUnitMm / paperUnitMm);
        }

        /// <summary>Angle of the XY projection from the X axis folded to [0, 45] degrees; null below 1e-9 length.</summary>
        internal static double? OffOrthoDegrees(double dx, double dy)
        {
            if (Math.Sqrt(dx * dx + dy * dy) < 1e-9) return null;
            double a = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            a = ((a % 90.0) + 90.0) % 90.0;
            return Math.Min(a, 90.0 - a);
        }

        internal static string Handle(ObjectId id) => id.Handle.ToString();
    }
}
