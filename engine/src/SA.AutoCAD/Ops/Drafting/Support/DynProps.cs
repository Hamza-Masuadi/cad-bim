using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>A requested dynamic property value converted to what the reference's property accepts.</summary>
    internal sealed class DynValue
    {
        public string Name = "";
        public object Value = 0;
        public DynamicBlockReferencePropertyUnitsType Units;
        /// <summary>The requested value as given, for reports.</summary>
        public JsonValue Requested = JsonValue.Null;
        /// <summary>True for list-valued properties (visibility states, lookups, flips): set first.</summary>
        public bool IsListValue;
    }

    /// <summary>
    /// Dynamic block properties through BlockReference.DynamicBlockReferencePropertyCollection. This API is fragile, so
    /// every request is checked against the reference's own collection before anything is set:
    /// - the name must exist (exact, else case-insensitive) and the property must not be ReadOnly;
    /// - distances are job units converted to drawing units and divided by the reference's X scale, because property
    ///   values are in the block definition's own units; angles are degrees converted to radians; areas use the square
    ///   of the factor;
    /// - when GetAllowedValues returns a list (visibility states, lookups, flip states, value sets) the value must be in
    ///   it; the exact allowed object is used so its CLR type matches;
    /// - otherwise the value is converted to the CLR type of the current value (double, short, int, string).
    /// Range limits (minimum, maximum, increment) are not exposed by the managed API: AutoCAD clamps or snaps the value,
    /// and readback then fails the op because the value read differs from the request.
    /// </summary>
    internal static class DynProps
    {
        internal static List<DynamicBlockReferenceProperty> Properties(BlockReference br)
        {
            var list = new List<DynamicBlockReferenceProperty>();
            if (!br.IsDynamicBlock) return list;
            foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection) list.Add(p);
            return list;
        }

        internal static DynamicBlockReferenceProperty? Find(BlockReference br, string name)
        {
            var props = Properties(br);
            return props.FirstOrDefault(p => p.PropertyName == name) ??
                   props.FirstOrDefault(p => string.Equals(p.PropertyName, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Settable property names, for error messages.</summary>
        internal static string Available(BlockReference br)
        {
            var names = Properties(br).Where(p => !p.ReadOnly && p.PropertyName != "Origin").Select(p => p.PropertyName).Distinct().ToList();
            return names.Count == 0 ? "it has no settable dynamic properties" : "settable properties: " + string.Join(", ", names);
        }

        /// <summary>Checks one request against a reference; returns the converted value, or null with <paramref name="error"/>.</summary>
        internal static DynValue? Convert(OpContext ctx, BlockReference br, string name, JsonValue requested, out string error)
        {
            error = "";
            if (!br.IsDynamicBlock) { error = "the reference is not a dynamic block"; return null; }
            var p = Find(br, name);
            if (p is null) { error = "no dynamic property '" + name + "'; " + Available(br); return null; }
            if (p.ReadOnly) { error = "dynamic property '" + p.PropertyName + "' is read-only"; return null; }
            object current = p.Value;
            var allowed = p.GetAllowedValues() ?? new object[0];
            double scale = Math.Abs(br.ScaleFactors.X);
            if (scale <= 0) scale = 1;
            var v = new DynValue { Name = p.PropertyName, Units = p.UnitsType, Requested = requested };

            if (allowed.Length > 0)
            {
                v.IsListValue = true;
                foreach (var a in allowed)
                {
                    if (Matches(ctx, p.UnitsType, scale, a, requested)) { v.Value = a; return v; }
                }
                error = "value " + requested + " is not allowed for '" + p.PropertyName + "'; allowed: " +
                        string.Join(", ", allowed.Select(a => Describe(ctx, p.UnitsType, scale, a)));
                return null;
            }

            switch (p.UnitsType)
            {
                case DynamicBlockReferencePropertyUnitsType.Distance:
                {
                    if (!TryLength(ctx, requested, out double job)) { error = "'" + p.PropertyName + "' is a distance; give a length"; return null; }
                    v.Value = ToType(ctx.Units.ToDrawing(job) / scale, current);
                    return v;
                }
                case DynamicBlockReferencePropertyUnitsType.Angular:
                {
                    if (!TryNumber(requested, out double deg)) { error = "'" + p.PropertyName + "' is an angle; give degrees"; return null; }
                    v.Value = ToType(deg * Math.PI / 180.0, current);
                    return v;
                }
                case DynamicBlockReferencePropertyUnitsType.Area:
                {
                    if (!TryNumber(requested, out double area)) { error = "'" + p.PropertyName + "' is an area; give a number in job units squared"; return null; }
                    double f = ctx.Units.ToDrawing(1.0);
                    v.Value = ToType(area * f * f / (scale * scale), current);
                    return v;
                }
                default:
                {
                    if (current is string)
                    {
                        if (!(requested is JsonString s)) { error = "'" + p.PropertyName + "' takes text"; return null; }
                        v.Value = s.Value;
                        return v;
                    }
                    if (requested is JsonBool b) { v.Value = ToType(b.Value ? 1.0 : 0.0, current); return v; }
                    if (!TryNumber(requested, out double n)) { error = "'" + p.PropertyName + "' takes a number"; return null; }
                    if (current is short || current is int || current is long)
                    {
                        if (Math.Abs(n - Math.Round(n)) > 1e-9) { error = "'" + p.PropertyName + "' takes a whole number"; return null; }
                    }
                    v.Value = ToType(n, current);
                    return v;
                }
            }
        }

        /// <summary>Sets converted values on a reference opened for write; list values (visibility, lookups, flips) first.</summary>
        internal static void Apply(BlockReference br, IEnumerable<DynValue> values)
        {
            foreach (var v in values.OrderBy(x => x.IsListValue ? 0 : 1))
            {
                var p = Find(br, v.Name) ?? throw new InvalidOperationException("Dynamic property '" + v.Name + "' disappeared from the reference.");
                p.Value = v.Value;
            }
        }

        /// <summary>True when the property's current value equals the converted request.</summary>
        internal static bool Same(DynamicBlockReferenceProperty p, DynValue v)
        {
            object cur = p.Value;
            if (cur is string cs) return string.Equals(cs, v.Value as string ?? System.Convert.ToString(v.Value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!IsNumeric(cur) || !IsNumeric(v.Value)) return Equals(cur, v.Value);
            double a = System.Convert.ToDouble(cur, CultureInfo.InvariantCulture), b = System.Convert.ToDouble(v.Value, CultureInfo.InvariantCulture);
            return Math.Abs(a - b) <= 1e-6 * Math.Max(1.0, Math.Abs(b));
        }

        /// <summary>A property value for reports: distances in job units (times the X scale), angles in degrees.</summary>
        internal static JsonValue ToJson(OpContext ctx, DynamicBlockReferenceProperty p, BlockReference br)
        {
            object v = p.Value;
            if (v is string s) return s;
            if (!IsNumeric(v)) return v?.ToString() ?? "";
            double d = System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
            double scale = Math.Abs(br.ScaleFactors.X);
            if (scale <= 0) scale = 1;
            switch (p.UnitsType)
            {
                case DynamicBlockReferencePropertyUnitsType.Distance: return Math.Round(ctx.Units.ToJob(d * scale), 9);
                case DynamicBlockReferencePropertyUnitsType.Angular: return Math.Round(d * 180.0 / Math.PI, 9);
                case DynamicBlockReferencePropertyUnitsType.Area:
                {
                    double f = ctx.Units.ToJob(1.0);
                    return Math.Round(d * scale * scale * f * f, 9);
                }
                default: return Math.Round(d, 9);
            }
        }

        private static bool Matches(OpContext ctx, DynamicBlockReferencePropertyUnitsType units, double scale, object allowed, JsonValue requested)
        {
            if (allowed is string s)
                return requested is JsonString rs && string.Equals(rs.Value.Trim(), s.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!IsNumeric(allowed)) return requested is JsonString r2 && string.Equals(r2.Value, allowed.ToString(), StringComparison.OrdinalIgnoreCase);
            double a = System.Convert.ToDouble(allowed, CultureInfo.InvariantCulture);
            double want;
            switch (units)
            {
                case DynamicBlockReferencePropertyUnitsType.Distance:
                    if (!TryLength(ctx, requested, out double job)) return false;
                    want = ctx.Units.ToDrawing(job) / scale;
                    break;
                case DynamicBlockReferencePropertyUnitsType.Angular:
                    if (!TryNumber(requested, out double deg)) return false;
                    want = deg * Math.PI / 180.0;
                    break;
                case DynamicBlockReferencePropertyUnitsType.Area:
                    if (!TryNumber(requested, out double area)) return false;
                    double f = ctx.Units.ToDrawing(1.0);
                    want = area * f * f / (scale * scale);
                    break;
                default:
                    if (requested is JsonBool b) want = b.Value ? 1 : 0;
                    else if (!TryNumber(requested, out want)) return false;
                    break;
            }
            return Math.Abs(a - want) <= 1e-6 * Math.Max(1.0, Math.Abs(a));
        }

        private static string Describe(OpContext ctx, DynamicBlockReferencePropertyUnitsType units, double scale, object allowed)
        {
            if (allowed is string s) return "\"" + s + "\"";
            if (!IsNumeric(allowed)) return allowed.ToString() ?? "";
            double d = System.Convert.ToDouble(allowed, CultureInfo.InvariantCulture);
            switch (units)
            {
                case DynamicBlockReferencePropertyUnitsType.Distance: return Math.Round(ctx.Units.ToJob(d * scale), 6).ToString(CultureInfo.InvariantCulture);
                case DynamicBlockReferencePropertyUnitsType.Angular: return Math.Round(d * 180.0 / Math.PI, 6).ToString(CultureInfo.InvariantCulture) + " deg";
                default: return d.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static bool TryNumber(JsonValue v, out double n)
        {
            n = 0;
            if (v is JsonNumber num) { n = num.Value; return true; }
            if (v is JsonString s && double.TryParse(s.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return true;
            return false;
        }

        private static bool TryLength(OpContext ctx, JsonValue v, out double job)
        {
            job = 0;
            if (v is JsonNumber n) { job = n.Value; return true; }
            if (v is JsonString s) return Length.TryParse(s.Value, ctx.JobUnit, out job, out _);
            return false;
        }

        private static bool IsNumeric(object? o) => o is double || o is float || o is short || o is int || o is long || o is ushort || o is uint || o is byte || o is decimal;

        private static object ToType(double value, object current)
        {
            switch (current)
            {
                case short _: return (short)Math.Round(value);
                case int _: return (int)Math.Round(value);
                case long _: return (long)Math.Round(value);
                case float _: return (float)value;
                default: return value;
            }
        }
    }
}
