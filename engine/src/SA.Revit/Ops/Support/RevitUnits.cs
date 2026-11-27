using System;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Json;
using SA.Core.Units;
using SA.Revit.Compat;

namespace SA.Revit.Ops.Support
{
    /// <summary>
    /// Conversion between job units and Revit internal units, always through UnitUtils with ForgeTypeId units.
    /// No conversion factor is written anywhere in the engine.
    ///
    /// Job conventions:
    /// <list type="bullet">
    /// <item>lengths, coordinates: the job's <c>units.length</c> (mm, cm, m, in, ft);</item>
    /// <item>areas and volumes: square / cubic metres for metric jobs (mm, cm, m), square / cubic feet for imperial;</item>
    /// <item>angles: degrees;</item>
    /// <item>any other measurable spec (slope, currency, electrical...): the project's display unit for that spec;</item>
    /// <item>numbers without a spec: as stored.</item>
    /// </list>
    /// </summary>
    internal sealed class RevitUnits
    {
        public RevitUnits(Document doc, LengthUnit jobUnit)
        {
            Doc = doc;
            JobUnit = jobUnit;
            LengthUnitId = ForJobUnit(jobUnit);
            bool imperial = Length.IsImperial(jobUnit);
            AreaUnitId = imperial ? UnitTypeId.SquareFeet : UnitTypeId.SquareMeters;
            VolumeUnitId = imperial ? UnitTypeId.CubicFeet : UnitTypeId.CubicMeters;
        }

        public Document Doc { get; }
        public LengthUnit JobUnit { get; }
        public ForgeTypeId LengthUnitId { get; }
        public ForgeTypeId AreaUnitId { get; }
        public ForgeTypeId VolumeUnitId { get; }
        public string AreaSymbol => Length.IsImperial(JobUnit) ? "ft2" : "m2";
        public string VolumeSymbol => Length.IsImperial(JobUnit) ? "ft3" : "m3";

        /// <summary>Length tolerance (0.5 mm) in job units, for readback comparisons.</summary>
        public double LengthTolerance => Tolerance.DefaultLength(JobUnit);

        /// <summary>The same tolerance in internal units.</summary>
        public double LengthToleranceInternal => LengthToInternal(LengthTolerance);

        public static ForgeTypeId ForJobUnit(LengthUnit u)
        {
            switch (u)
            {
                case LengthUnit.Millimetre: return UnitTypeId.Millimeters;
                case LengthUnit.Centimetre: return UnitTypeId.Centimeters;
                case LengthUnit.Metre: return UnitTypeId.Meters;
                case LengthUnit.Inch: return UnitTypeId.Inches;
                default: return UnitTypeId.Feet;
            }
        }

        public double LengthToInternal(double jobValue) => UnitUtils.ConvertToInternalUnits(jobValue, LengthUnitId);
        public double LengthFromInternal(double internalValue) => UnitUtils.ConvertFromInternalUnits(internalValue, LengthUnitId);
        public double AreaToInternal(double v) => UnitUtils.ConvertToInternalUnits(v, AreaUnitId);
        public double AreaFromInternal(double v) => UnitUtils.ConvertFromInternalUnits(v, AreaUnitId);
        public double VolumeToInternal(double v) => UnitUtils.ConvertToInternalUnits(v, VolumeUnitId);
        public double VolumeFromInternal(double v) => UnitUtils.ConvertFromInternalUnits(v, VolumeUnitId);
        public double AngleToInternal(double degrees) => UnitUtils.ConvertToInternalUnits(degrees, UnitTypeId.Degrees);
        public double AngleFromInternal(double radians) => UnitUtils.ConvertFromInternalUnits(radians, UnitTypeId.Degrees);

        /// <summary>Metres, for the job-neutral values in the snapshot.</summary>
        public static double ToMetres(double internalLength) => UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Meters);

        public XYZ PointToInternal(Point2 p, double zInternal) => new XYZ(LengthToInternal(p.X), LengthToInternal(p.Y), zInternal);

        public Point2 PointFromInternal(XYZ p) => new Point2(LengthFromInternal(p.X), LengthFromInternal(p.Y));

        public JsonArray PointJson(XYZ p) => new JsonArray(Num(LengthFromInternal(p.X)), Num(LengthFromInternal(p.Y)));

        /// <summary>A length read back, in job units, rounded to remove floating-point noise.</summary>
        public JsonValue LengthJson(double internalValue) => Num(LengthFromInternal(internalValue));

        /// <summary>The value as the project shows it, e.g. "3000 mm" or "9' 10\"".</summary>
        public string Display(ForgeTypeId spec, double internalValue)
        {
            try { return UnitFormatUtils.Format(Doc.GetUnits(), spec, internalValue, false); }
            catch (Exception) { return internalValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
        }

        public static JsonValue Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return JsonValue.Null;
            double r = Math.Round(v, 6);
            return r == 0 ? (JsonValue)0.0 : r;
        }

        /// <summary>The unit a job value of this spec is written in, or null for "store as is".</summary>
        public ForgeTypeId? JobUnitFor(ForgeTypeId spec)
        {
            if (ParameterApi.SameSpec(spec, SpecTypeId.Length)) return LengthUnitId;
            if (ParameterApi.SameSpec(spec, SpecTypeId.Area)) return AreaUnitId;
            if (ParameterApi.SameSpec(spec, SpecTypeId.Volume)) return VolumeUnitId;
            if (ParameterApi.SameSpec(spec, SpecTypeId.Angle)) return UnitTypeId.Degrees;
            if (!ParameterApi.IsMeasurable(spec)) return null;
            try { return Doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId(); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Converts a job value for a parameter of this spec to internal units. Numbers are in the job convention;
        /// strings are parsed: lengths by Core (so "3.2m" and 10'-6" work whatever the job unit), anything else by
        /// Revit's own parser with the project's units.
        /// </summary>
        public bool TryToInternal(ForgeTypeId spec, JsonValue value, out double result, out string error)
        {
            result = 0;
            error = "";
            var unit = JobUnitFor(spec);
            if (value is JsonNumber n)
            {
                result = unit is null ? n.Value : UnitUtils.ConvertToInternalUnits(n.Value, unit);
                return true;
            }
            if (value is JsonString s)
            {
                if (ParameterApi.SameSpec(spec, SpecTypeId.Length))
                {
                    if (Length.TryParse(s.Value, JobUnit, out double jobLen, out string err))
                    {
                        result = LengthToInternal(jobLen);
                        return true;
                    }
                    error = err;
                    return false;
                }
                if (ParameterApi.IsMeasurable(spec))
                {
                    try
                    {
                        if (UnitFormatUtils.TryParse(Doc.GetUnits(), spec, s.Value, out double parsed)) { result = parsed; return true; }
                    }
                    catch (Exception e) { error = e.Message; return false; }
                    error = "'" + s.Value + "' is not a valid " + ParameterApi.SpecLabel(spec) + " value in this project's units";
                    return false;
                }
                if (double.TryParse(s.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double plain))
                {
                    result = plain;
                    return true;
                }
                error = "'" + s.Value + "' is not a number";
                return false;
            }
            error = "expected a number or a string, found " + JsonValue.KindName(value.Kind);
            return false;
        }

        public double FromInternal(ForgeTypeId spec, double internalValue)
        {
            var unit = JobUnitFor(spec);
            return unit is null ? internalValue : UnitUtils.ConvertFromInternalUnits(internalValue, unit);
        }

        /// <summary>Tolerance for comparing two internal values of this spec.</summary>
        public double InternalTolerance(ForgeTypeId spec, double magnitude)
        {
            if (ParameterApi.SameSpec(spec, SpecTypeId.Length)) return LengthToleranceInternal;
            return Math.Max(1e-9, Math.Abs(magnitude) * 1e-7);
        }

        public JsonObject DescribeJobUnits() => new JsonObject()
            .Add("length", Length.Symbol(JobUnit)).Add("area", AreaSymbol).Add("volume", VolumeSymbol).Add("angle", "deg");

        /// <summary>The project's display unit for a spec: { unit, label, accuracy }.</summary>
        public static JsonObject ProjectUnit(Document doc, ForgeTypeId spec)
        {
            var o = new JsonObject();
            try
            {
                var fo = doc.GetUnits().GetFormatOptions(spec);
                var u = fo.GetUnitTypeId();
                o.Add("unit", u.TypeId);
                string label;
                try { label = LabelUtils.GetLabelForUnit(u); } catch (Exception) { label = u.TypeId; }
                o.Add("label", label);
                o.Add("accuracy", Num(fo.Accuracy));
                o.Add("example", UnitFormatUtils.Format(doc.GetUnits(), spec, UnitUtils.ConvertToInternalUnits(1, spec.Equals(SpecTypeId.Angle) ? UnitTypeId.Degrees : spec.Equals(SpecTypeId.Area) ? UnitTypeId.SquareMeters : UnitTypeId.Meters), false));
            }
            catch (Exception e) { o.Add("error", e.Message); }
            return o;
        }
    }
}
