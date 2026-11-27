using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SA.Core.Units
{
    public enum LengthUnit { Millimetre, Centimetre, Metre, Inch, Foot }

    public enum LengthStyle
    {
        /// <summary>Decimal value with the unit symbol, e.g. "3200 mm", "10.5 ft".</summary>
        Decimal,
        /// <summary>Feet and fractional inches, e.g. 10'-6 1/2".</summary>
        Architectural,
        /// <summary>Feet and decimal inches, e.g. 10'-6.50".</summary>
        Engineering,
        /// <summary>Inches with a fraction, e.g. 126 1/2".</summary>
        FractionalInches,
    }

    /// <summary>Length units, conversion, parsing and formatting. Conversion uses exact factors (1 in = 25.4 mm).</summary>
    public static class Length
    {
        public static double MillimetresPer(LengthUnit unit)
        {
            switch (unit)
            {
                case LengthUnit.Millimetre: return 1.0;
                case LengthUnit.Centimetre: return 10.0;
                case LengthUnit.Metre: return 1000.0;
                case LengthUnit.Inch: return 25.4;
                case LengthUnit.Foot: return 304.8;
                default: throw new ArgumentOutOfRangeException(nameof(unit));
            }
        }

        public static double Convert(double value, LengthUnit from, LengthUnit to)
        {
            if (from == to) return value;
            // Imperial to imperial stays exact (12 in per ft) rather than going through 25.4.
            if (from == LengthUnit.Foot && to == LengthUnit.Inch) return value * 12.0;
            if (from == LengthUnit.Inch && to == LengthUnit.Foot) return value / 12.0;
            return value * MillimetresPer(from) / MillimetresPer(to);
        }

        /// <summary>The job symbol: mm, cm, m, in, ft.</summary>
        public static string Symbol(LengthUnit unit)
        {
            switch (unit)
            {
                case LengthUnit.Millimetre: return "mm";
                case LengthUnit.Centimetre: return "cm";
                case LengthUnit.Metre: return "m";
                case LengthUnit.Inch: return "in";
                case LengthUnit.Foot: return "ft";
                default: throw new ArgumentOutOfRangeException(nameof(unit));
            }
        }

        /// <summary>Parses a job unit symbol (mm, cm, m, in, ft). Case-sensitive, as in job files.</summary>
        public static bool TryParseUnit(string? symbol, out LengthUnit unit)
        {
            switch (symbol)
            {
                case "mm": unit = LengthUnit.Millimetre; return true;
                case "cm": unit = LengthUnit.Centimetre; return true;
                case "m": unit = LengthUnit.Metre; return true;
                case "in": unit = LengthUnit.Inch; return true;
                case "ft": unit = LengthUnit.Foot; return true;
                default: unit = LengthUnit.Millimetre; return false;
            }
        }

        public static LengthUnit ParseUnit(string symbol) =>
            TryParseUnit(symbol, out var u) ? u : throw new FormatException("Unknown length unit '" + symbol + "'. Use mm, cm, m, in or ft.");

        public static bool IsImperial(LengthUnit unit) => unit == LengthUnit.Inch || unit == LengthUnit.Foot;

        private static readonly Regex MetricOrSimple = new Regex(
            @"^(?<num>(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(?<unit>mm|cm|m|in|inch|inches|ft|feet|foot|""|')?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // Feet and/or inches: 10'-6", 10' 6 1/2", 10'6", 10 ft 6 in, 6-1/2", 3/4", 10'-0".
        private static readonly Regex Imperial = new Regex(
            @"^(?:(?<ft>\d+(?:\.\d+)?)\s*(?:'|ft|feet|foot)\s*(?:-\s*)?)?" +
            @"(?:(?<in>\d+(?:\.\d+)?)?(?:(?:\s+|\s*-\s*)?(?<n>\d+)\s*/\s*(?<d>\d+))?\s*(?<im>""|in|inch|inches)?)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses a length and returns it in <paramref name="defaultUnit"/>. A bare number ("3200") is already in
        /// <paramref name="defaultUnit"/>. Accepted: "3200", "3.2m", "320 cm", "12in", "6\"", "10'", "10'-6\"",
        /// "10' 6 1/2\"", "10'-6 1/2\"", "6-1/2\"", "3/4\"", "10 ft 6 in", "-2'-3\"". Unicode primes are accepted.
        /// </summary>
        public static bool TryParse(string? text, LengthUnit defaultUnit, out double value, out string error)
        {
            value = 0;
            error = "";
            if (text is null) { error = "length is null"; return false; }
            string s = Normalise(text);
            if (s.Length == 0) { error = "length is empty"; return false; }

            double sign = 1;
            if (s[0] == '-' || s[0] == '+')
            {
                if (s[0] == '-') sign = -1;
                s = s.Substring(1).TrimStart();
                if (s.Length == 0) { error = "'" + text + "' has a sign but no value"; return false; }
            }

            var m = MetricOrSimple.Match(s);
            if (m.Success)
            {
                double num = double.Parse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(num)) { error = "'" + text + "' is out of range"; return false; }
                LengthUnit unit = defaultUnit;
                if (m.Groups["unit"].Success && !TryUnitWord(m.Groups["unit"].Value, out unit))
                {
                    error = "unknown unit in '" + text + "'";
                    return false;
                }
                value = sign * Convert(num, unit, defaultUnit);
                return true;
            }

            var im = Imperial.Match(s);
            if (im.Success)
            {
                bool hasFt = im.Groups["ft"].Success;
                bool hasIn = im.Groups["in"].Success;
                bool hasFrac = im.Groups["n"].Success;
                bool hasInchMark = im.Groups["im"].Success;
                if (!hasFt && !hasIn && !hasFrac) { error = "'" + text + "' is not a length"; return false; }
                if (hasIn && !hasFrac && !hasFt && !hasInchMark) { error = "'" + text + "' is not a length"; return false; }

                double inches = 0;
                if (hasIn) inches += double.Parse(im.Groups["in"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (hasFrac)
                {
                    if (hasIn && im.Groups["in"].Value.IndexOf('.') >= 0) { error = "'" + text + "' mixes a decimal and a fraction"; return false; }
                    double n = double.Parse(im.Groups["n"].Value, CultureInfo.InvariantCulture);
                    double d = double.Parse(im.Groups["d"].Value, CultureInfo.InvariantCulture);
                    if (d == 0) { error = "'" + text + "' has a zero denominator"; return false; }
                    inches += n / d;
                }
                double feet = hasFt ? double.Parse(im.Groups["ft"].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

                if (!hasFt && !hasInchMark)
                {
                    // A bare fraction such as "1/2" or "6 1/2" is in the default unit.
                    value = sign * inches;
                    return true;
                }
                value = sign * Convert(feet * 12.0 + inches, LengthUnit.Inch, defaultUnit);
                return true;
            }

            error = "'" + text + "' is not a length. Examples: 3200, 3.2m, 10'-6\", 10' 6 1/2\", 6\"";
            return false;
        }

        public static double Parse(string text, LengthUnit defaultUnit)
        {
            if (TryParse(text, defaultUnit, out double v, out string err)) return v;
            throw new FormatException(err);
        }

        private static string Normalise(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text.Trim())
            {
                switch (c)
                {
                    case '′': case '’': sb.Append('\''); break;           // prime, right single quote
                    case '″': case '”': case '“': sb.Append('"'); break; // double prime, curly double quotes
                    case '−': case '–': sb.Append('-'); break;            // minus sign, en dash
                    case ' ': sb.Append(' '); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString().Replace("''", "\"");
        }

        private static bool TryUnitWord(string word, out LengthUnit unit)
        {
            switch (word.ToLowerInvariant())
            {
                case "mm": unit = LengthUnit.Millimetre; return true;
                case "cm": unit = LengthUnit.Centimetre; return true;
                case "m": unit = LengthUnit.Metre; return true;
                case "in": case "inch": case "inches": case "\"": unit = LengthUnit.Inch; return true;
                case "ft": case "feet": case "foot": case "'": unit = LengthUnit.Foot; return true;
                default: unit = LengthUnit.Millimetre; return false;
            }
        }

        /// <summary>
        /// Formats a length given in <paramref name="unit"/>. For the imperial styles the value is converted to
        /// inches and rounded to 1/<paramref name="denominator"/> inch (Architectural, FractionalInches) or to
        /// <paramref name="decimals"/> places (Engineering, Decimal).
        /// </summary>
        public static string Format(double value, LengthUnit unit, LengthStyle style = LengthStyle.Decimal, int decimals = 2, int denominator = 16)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("Length must be finite.", nameof(value));
            if (decimals < 0 || decimals > 12) throw new ArgumentOutOfRangeException(nameof(decimals));
            if (denominator < 1 || denominator > 256 || (denominator & (denominator - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be a power of two from 1 to 256.");

            if (style == LengthStyle.Decimal)
                return TrimNumber(Math.Round(value, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture)) + " " + Symbol(unit);

            double inchesTotal = Convert(value, unit, LengthUnit.Inch);
            bool negative = inchesTotal < 0;
            inchesTotal = Math.Abs(inchesTotal);
            string sign = negative ? "-" : "";

            if (style == LengthStyle.Engineering)
            {
                double rounded = Math.Round(inchesTotal, decimals, MidpointRounding.AwayFromZero);
                long feet = (long)Math.Floor(rounded / 12.0 + 1e-12);
                double inches = Math.Round(rounded - feet * 12.0, decimals, MidpointRounding.AwayFromZero);
                if (inches >= 12.0) { feet++; inches -= 12.0; }
                if (negative && feet == 0 && inches == 0) sign = "";
                return sign + feet.ToString(CultureInfo.InvariantCulture) + "'-" + inches.ToString("F" + decimals, CultureInfo.InvariantCulture) + "\"";
            }

            long units = (long)Math.Round(inchesTotal * denominator, MidpointRounding.AwayFromZero);
            if (negative && units == 0) sign = "";
            if (style == LengthStyle.FractionalInches)
                return sign + FractionText(units, denominator) + "\"";

            long perFoot = 12L * denominator;
            long ft = units / perFoot;
            long rem = units % perFoot;
            return sign + ft.ToString(CultureInfo.InvariantCulture) + "'-" + FractionText(rem, denominator) + "\"";
        }

        private static string FractionText(long units, int denominator)
        {
            long whole = units / denominator;
            long num = units % denominator;
            if (num == 0) return whole.ToString(CultureInfo.InvariantCulture);
            long den = denominator;
            while (num % 2 == 0 && den % 2 == 0) { num /= 2; den /= 2; }
            string frac = num.ToString(CultureInfo.InvariantCulture) + "/" + den.ToString(CultureInfo.InvariantCulture);
            return whole == 0 ? frac : whole.ToString(CultureInfo.InvariantCulture) + " " + frac;
        }

        private static string TrimNumber(string s)
        {
            if (s.IndexOf('.') < 0) return s;
            s = s.TrimEnd('0');
            if (s.EndsWith(".", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            if (s == "-0") s = "0";
            return s;
        }
    }

    /// <summary>Tolerance comparisons for lengths and angles.</summary>
    public static class Tolerance
    {
        /// <summary>Default length tolerance: 0.5 mm expressed in <paramref name="unit"/>.</summary>
        public static double DefaultLength(LengthUnit unit) => Length.Convert(0.5, LengthUnit.Millimetre, unit);

        /// <summary>Default angle tolerance in degrees.</summary>
        public const double DefaultAngleDegrees = 1e-4;

        public static bool Equal(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

        public static bool IsZero(double a, double tolerance) => Math.Abs(a) <= tolerance;

        /// <summary>Compares two lengths given in different units, using a tolerance in <paramref name="toleranceUnit"/>.</summary>
        public static bool LengthEqual(double a, LengthUnit aUnit, double b, LengthUnit bUnit, double tolerance, LengthUnit toleranceUnit) =>
            Math.Abs(Length.Convert(a, aUnit, toleranceUnit) - Length.Convert(b, bUnit, toleranceUnit)) <= tolerance;

        /// <summary>Compares angles in degrees modulo 360, so 359.99 and 0 are equal within 0.02.</summary>
        public static bool AngleEqual(double aDegrees, double bDegrees, double toleranceDegrees = DefaultAngleDegrees)
        {
            double d = Math.Abs(NormaliseDegrees(aDegrees) - NormaliseDegrees(bDegrees));
            return Math.Min(d, 360.0 - d) <= toleranceDegrees;
        }

        /// <summary>Maps any angle into [0, 360).</summary>
        public static double NormaliseDegrees(double degrees)
        {
            double r = degrees % 360.0;
            if (r < 0) r += 360.0;
            if (r >= 360.0) r -= 360.0;
            return r;
        }
    }

    public static class Angle
    {
        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        public static double ToDegrees(double radians) => radians * 180.0 / Math.PI;

        private static readonly Regex AngleText = new Regex(@"^(?<num>[-+]?(?:\d+(?:\.\d*)?|\.\d+))\s*(?<u>°|deg|degrees|rad)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Parses "45", "45°", "45deg" or "0.7854rad" into degrees.</summary>
        public static bool TryParseDegrees(string? text, out double degrees)
        {
            degrees = 0;
            if (text is null) return false;
            var m = AngleText.Match(text.Trim());
            if (!m.Success) return false;
            double v = double.Parse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            degrees = m.Groups["u"].Success && m.Groups["u"].Value.ToLowerInvariant() == "rad" ? ToDegrees(v) : v;
            return true;
        }
    }
}
