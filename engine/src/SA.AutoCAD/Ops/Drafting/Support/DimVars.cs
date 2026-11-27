using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>One dimension variable setting, checked and converted, ready to write to a DimStyleTableRecord.</summary>
    internal sealed class DimVarSetting
    {
        public string Name = "";
        public PropertyInfo Property = null!;
        public JsonValue Requested = JsonValue.Null;
        /// <summary>The value to write, except for ObjectId properties, which are resolved by <see cref="SymbolName"/> at execution.</summary>
        public object? Value;
        /// <summary>For ObjectId properties: the text style, block or linetype name ("" = none/default arrowhead).</summary>
        public string? SymbolName;
        /// <summary>For arrowhead properties: the built-in arrowhead block name (e.g. "_ARCHTICK") AutoCAD creates on demand.</summary>
        public string? BuiltInArrow;
    }

    /// <summary>
    /// Dimension variables by their system variable name (DIMTXT, DIMASZ, DIMBLK...) mapped onto the
    /// DimStyleTableRecord property of the same name (Dimtxt, Dimasz, Dimblk; DIMFXL is Dimfxlen and DIMFXLON is
    /// DimfxlenOn). The property list is the same in the 2024-2027 reference assemblies.
    ///
    /// Length variables (DIMTXT, DIMASZ, DIMGAP, DIMEXE, DIMEXO, DIMDLI, DIMDLE, DIMCEN, DIMTSZ, DIMTM, DIMTP, DIMRND,
    /// DIMFXL) are given in the source unit (the job unit, or the standard's unit) and written as paper units (mm in a
    /// metric drawing, inches in an imperial one) for annotative styles, and as drawing units for non-annotative ones
    /// (where DIMSCALE multiplies them). DIMJOGANG is given in degrees. Everything else is written as given.
    /// Refused: DIMLFAC other than 1 (faking a scale, AC-034). Warned: DIMSCALE on an annotative style (ignored),
    /// decimal precision on metric millimetre drawings (AC-011).
    /// </summary>
    internal static class DimVars
    {
        private static readonly Dictionary<string, PropertyInfo> Props = BuildMap();

        private static readonly HashSet<string> LengthVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DIMTXT", "DIMASZ", "DIMGAP", "DIMEXE", "DIMEXO", "DIMDLI", "DIMDLE", "DIMCEN", "DIMTSZ", "DIMTM", "DIMTP", "DIMRND", "DIMFXL",
        };

        private static readonly string[] BuiltInArrows =
        {
            "_CLOSEDBLANK", "_CLOSED", "_DOT", "_ARCHTICK", "_OBLIQUE", "_OPEN", "_ORIGIN", "_ORIGIN2", "_OPEN90", "_OPEN30",
            "_DOTSMALL", "_DOTBLANK", "_SMALL", "_BOXBLANK", "_BOXFILLED", "_DATUMBLANK", "_DATUMFILLED", "_INTEGRAL", "_NONE",
        };

        private static Dictionary<string, PropertyInfo> BuildMap()
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in typeof(DimStyleTableRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || !p.Name.StartsWith("Dim", StringComparison.Ordinal) || p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "Dimfxlen") map["DIMFXL"] = p;
                else if (p.Name == "DimfxlenOn") map["DIMFXLON"] = p;
                else map[p.Name.ToUpperInvariant()] = p;
            }
            return map;
        }

        internal static IEnumerable<string> Names => Props.Keys;

        internal static bool IsLength(string name) => LengthVars.Contains(name);

        /// <summary>
        /// Checks and converts one setting. <paramref name="sourceUnit"/> is the unit length values are given in;
        /// <paramref name="mmPerDrawingUnit"/> converts to drawing units for non-annotative styles.
        /// </summary>
        internal static DimVarSetting? Convert(OpContext ctx, string name, JsonValue v, bool annotative, LengthUnit sourceUnit, double mmPerDrawingUnit,
            List<string> warnings, out string error)
        {
            error = "";
            string key = name.Trim().ToUpperInvariant();
            if (!key.StartsWith("DIM", StringComparison.Ordinal)) key = "DIM" + key;
            if (!Props.TryGetValue(key, out var prop))
            {
                error = "'" + name + "' is not a dimension variable." + DraftDb.Suggest(Props.Keys, key);
                return null;
            }
            var s = new DimVarSetting { Name = key, Property = prop, Requested = v };
            var t = prop.PropertyType;
            if (t == typeof(double))
            {
                if (!TryNumber(v, out double d)) { error = key + " needs a number"; return null; }
                if (LengthVars.Contains(key))
                {
                    double mm = Length.Convert(d, sourceUnit, LengthUnit.Millimetre);
                    d = annotative ? mm / Anno.PaperUnitMm(ctx.Database) : mm / mmPerDrawingUnit;
                }
                else if (key == "DIMJOGANG") d = d * Math.PI / 180.0;
                if (key == "DIMLFAC" && Math.Abs(d - 1) > 1e-12) { error = "DIMLFAC must stay 1: a linear scale factor fakes the drawing scale and every dimension then lies (AC-034)"; return null; }
                if (key == "DIMTXT" && !(d > 0)) { error = "DIMTXT must be positive"; return null; }
                if (key == "DIMSCALE" && annotative) warnings.Add("DIMSCALE is ignored by annotative dimension styles; the annotation scale sizes them");
                if (key == "DIMSCALE" && d < 0) { error = "DIMSCALE cannot be negative"; return null; }
                s.Value = d;
            }
            else if (t == typeof(int))
            {
                if (!TryInt(v, out int i)) { error = key + " needs a whole number"; return null; }
                s.Value = i;
            }
            else if (t == typeof(bool))
            {
                if (v is JsonBool b) s.Value = b.Value;
                else if (TryInt(v, out int i) && (i == 0 || i == 1)) s.Value = i == 1;
                else { error = key + " needs true/false (or 0/1)"; return null; }
            }
            else if (t == typeof(string))
            {
                if (!(v is JsonString str)) { error = key + " needs text"; return null; }
                s.Value = str.Value;
            }
            else if (t == typeof(char))
            {
                if (!(v is JsonString str) || str.Value.Length != 1) { error = key + " needs a single character"; return null; }
                s.Value = str.Value[0];
            }
            else if (t == typeof(Color))
            {
                if (!AcadValues.TryParseColor(v, true, out var c, out string err)) { error = key + ": " + err; return null; }
                s.Value = c;
            }
            else if (t == typeof(LineWeight))
            {
                if (!AcadValues.TryParseLineWeight(v, false, out var lw, out string err)) { error = key + ": " + err; return null; }
                s.Value = lw;
            }
            else if (t == typeof(ObjectId))
            {
                if (!(v is JsonString str)) { error = key + " needs a name"; return null; }
                string n = str.Value.Trim();
                if (IsArrowVar(key))
                {
                    if (n == "" || n == "." || string.Equals(n, "ClosedFilled", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "_ClosedFilled", StringComparison.OrdinalIgnoreCase))
                    {
                        s.SymbolName = "";
                        return s;
                    }
                    string u = "_" + n.TrimStart('_').ToUpperInvariant();
                    if (BuiltInArrows.Contains(u)) { s.BuiltInArrow = u; s.SymbolName = u; return s; }
                    if (!DraftDb.BlockExists(ctx, n)) { error = key + ": arrowhead block '" + n + "' is not in the drawing and is not a built-in arrowhead (" + string.Join(", ", BuiltInArrows.Select(a => a.Substring(1))) + ")"; return null; }
                    s.SymbolName = n;
                    return s;
                }
                if (key == "DIMTXSTY")
                {
                    if (!Db.TextStyleExists(ctx, n)) { error = "DIMTXSTY: text style '" + n + "' is not in the drawing; add it with textstyle.ensure first." + Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.SymbolNames(tr, ctx.Database.TextStyleTableId), n)); return null; }
                    double fixedHeight = Db.Read(ctx, tr =>
                    {
                        var id = Db.SymbolId(tr, ctx.Database.TextStyleTableId, n);
                        return id.IsNull ? 0 : ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).TextSize;
                    });
                    if (fixedHeight > 0) warnings.Add("text style '" + n + "' has a fixed height, which overrides DIMTXT in every dimension of this style");
                    s.SymbolName = n;
                    return s;
                }
                // DIMLTYPE, DIMLTEX1, DIMLTEX2
                if (!LinetypeSupport.IsBuiltIn(n) && !Db.LinetypeExists(ctx, n)) { error = key + ": linetype '" + n + "' is not in the drawing; load it with linetype.ensure first"; return null; }
                s.SymbolName = n;
                return s;
            }
            else
            {
                error = key + " has a type (" + t.Name + ") this op cannot set";
                return null;
            }
            return s;
        }

        private static bool IsArrowVar(string key) => key == "DIMBLK" || key == "DIMBLK1" || key == "DIMBLK2" || key == "DIMLDRBLK";

        /// <summary>Precision warnings for a whole style (AC-011): decimals of a millimetre on building drawings.</summary>
        internal static void PrecisionWarnings(OpContext ctx, IEnumerable<DimVarSetting> settings, List<string> warnings)
        {
            var dec = settings.FirstOrDefault(x => x.Name == "DIMDEC");
            var lunit = settings.FirstOrDefault(x => x.Name == "DIMLUNIT");
            if (dec?.Value is int d && d > 0 && ctx.Database.Insunits == UnitsValue.Millimeters && (lunit is null || (lunit.Value is int u && u == 2)))
                warnings.Add("DIMDEC " + d + " shows decimals of a millimetre; building drawings dimension in whole millimetres (AC-011)");
        }

        /// <summary>Resolves an ObjectId setting at execution (creating a built-in arrowhead block when needed).</summary>
        internal static ObjectId ResolveId(OpContext ctx, DimVarSetting s)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            string n = s.SymbolName ?? "";
            if (IsArrowVar(s.Name))
            {
                if (n == "") return ObjectId.Null;
                var id = DraftDb.BlockId(tr, db, n);
                if (id.IsNull && s.BuiltInArrow != null)
                {
                    // AutoCAD creates a built-in arrowhead block the first time DIMBLK names it. The variable is restored
                    // after the job; the block stays (it is what the style references).
                    ctx.SetSysVar("DIMBLK", s.BuiltInArrow);
                    id = DraftDb.BlockId(tr, db, n);
                }
                if (id.IsNull) throw new InvalidOperationException(s.Name + ": arrowhead block '" + n + "' could not be found or created.");
                return id;
            }
            if (s.Name == "DIMTXSTY")
            {
                var id = Db.SymbolId(tr, db.TextStyleTableId, n);
                if (id.IsNull) throw new InvalidOperationException("DIMTXSTY: text style '" + n + "' is missing.");
                return id;
            }
            var lt = Db.SymbolId(tr, db.LinetypeTableId, n);
            if (lt.IsNull) throw new InvalidOperationException(s.Name + ": linetype '" + n + "' is missing.");
            return lt;
        }

        internal static void Write(OpContext ctx, DimStyleTableRecord rec, DimVarSetting s)
        {
            object? value = s.Property.PropertyType == typeof(ObjectId) ? ResolveId(ctx, s) : s.Value;
            s.Property.SetValue(rec, value, null);
        }

        /// <summary>True when the record holds the setting; <paramref name="got"/> describes the value read.</summary>
        internal static bool Holds(OpContext ctx, Transaction tr, DimStyleTableRecord rec, DimVarSetting s, out string got)
        {
            object? cur = s.Property.GetValue(rec, null);
            got = Describe(tr, cur);
            if (s.Property.PropertyType == typeof(ObjectId))
            {
                var id = cur is ObjectId oid ? oid : ObjectId.Null;
                string want = s.SymbolName ?? "";
                if (want == "") return id.IsNull;
                if (id.IsNull) return false;
                var o = tr.GetObject(id, OpenMode.ForRead) as SymbolTableRecord;
                return o != null && string.Equals(o.Name, want, StringComparison.OrdinalIgnoreCase);
            }
            if (cur is double d && s.Value is double w) return Math.Abs(d - w) <= 1e-9 * Math.Max(1, Math.Abs(w));
            if (cur is Color c && s.Value is Color wc) return AcadValues.SameColor(c, wc);
            return Equals(cur, s.Value);
        }

        internal static string Describe(Transaction tr, object? v)
        {
            switch (v)
            {
                case null: return "null";
                case ObjectId id:
                    if (id.IsNull) return "(none)";
                    return tr.GetObject(id, OpenMode.ForRead) is SymbolTableRecord r ? r.Name : id.Handle.ToString();
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case Color c: return AcadValues.ColorJson(c).ToString();
                default: return System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }

        /// <summary>Copies every dimension variable of <paramref name="from"/> onto <paramref name="to"/> (same database).</summary>
        internal static void CopyAll(DimStyleTableRecord from, DimStyleTableRecord to)
        {
            foreach (var p in Props.Values) p.SetValue(to, p.GetValue(from, null), null);
        }

        /// <summary>The model-space text height (job units) dimensions of this style get at the current annotation scale.</summary>
        internal static double ModelTextHeight(OpContext ctx, DimStyleTableRecord rec)
        {
            if (Anno.IsAnnotative(rec))
            {
                var s = Anno.Current(ctx.Database);
                double dpp = s is null ? 1 : Anno.DrawingPerPaper(s);
                return ctx.Units.ToJob(rec.Dimtxt * dpp);
            }
            double scale = rec.Dimscale > 0 ? rec.Dimscale : 1;
            return ctx.Units.ToJob(rec.Dimtxt * scale);
        }

        private static bool TryNumber(JsonValue v, out double d)
        {
            d = 0;
            if (v is JsonNumber n) { d = n.Value; return true; }
            return v is JsonString s && double.TryParse(s.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        private static bool TryInt(JsonValue v, out int i)
        {
            i = 0;
            if (v is JsonNumber n && n.TryGetInt64(out long l) && l >= int.MinValue && l <= int.MaxValue) { i = (int)l; return true; }
            return v is JsonString s && int.TryParse(s.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        }
    }
}
