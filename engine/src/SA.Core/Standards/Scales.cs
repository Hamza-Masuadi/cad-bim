using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SA.Core.Units;

namespace SA.Core.Standards
{
    /// <summary>A drawing scale. <see cref="Ratio"/> is model length per paper length: 100 for 1:100, 48 for 1/4" = 1'-0".</summary>
    public sealed class Scale
    {
        public Scale(string name, double ratio)
        {
            if (!(ratio > 0) || double.IsInfinity(ratio)) throw new ArgumentOutOfRangeException(nameof(ratio), "Scale ratio must be positive.");
            Name = name; Ratio = ratio;
        }

        public string Name { get; }
        public double Ratio { get; }

        private static readonly Regex RatioForm = new Regex(@"^\s*(\d+(?:\.\d+)?)\s*:\s*(\d+(?:\.\d+)?)\s*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses "1:100", "1 : 50", "2:1", or imperial "paper = model" forms such as 1/4" = 1'-0", 1" = 10'-0", 3/32"=1'.
        /// </summary>
        public static bool TryParseRatio(string text, out double ratio)
        {
            ratio = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = RatioForm.Match(text);
            if (m.Success)
            {
                double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (a <= 0 || b <= 0) return false;
                ratio = b / a;
                return true;
            }
            int eq = text.IndexOf('=');
            if (eq <= 0 || text.IndexOf('=', eq + 1) >= 0) return false;
            // Each side needs an explicit unit; a bare number on either side is ambiguous.
            string paper = text.Substring(0, eq).Trim(), model = text.Substring(eq + 1).Trim();
            if (!HasImperialMark(paper) || !HasImperialMark(model)) return false;
            if (!Length.TryParse(paper, LengthUnit.Inch, out double p, out _) || !Length.TryParse(model, LengthUnit.Inch, out double q, out _)) return false;
            if (p <= 0 || q <= 0) return false;
            ratio = q / p;
            return true;
        }

        private static bool HasImperialMark(string s) => s.IndexOf('"') >= 0 || s.IndexOf('\'') >= 0 || s.IndexOf("in", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("ft", StringComparison.OrdinalIgnoreCase) >= 0;

        public static Scale Parse(string text) =>
            TryParseRatio(text, out double r) ? new Scale(text, r) : throw new FormatException("'" + text + "' is not a scale. Examples: 1:100, 1/4\" = 1'-0\"");

        /// <summary>
        /// Model-space text height that plots at <paramref name="plottedHeight"/> at a given scale.
        /// Example: 2.5 mm plotted at 1:100 in a millimetre drawing is 250; 3/32" at 1/4" = 1'-0" in an inch drawing is 4.5.
        /// </summary>
        public static double ModelTextHeight(double plottedHeight, LengthUnit plottedUnit, double ratio, LengthUnit modelUnit)
        {
            if (!(ratio > 0)) throw new ArgumentOutOfRangeException(nameof(ratio));
            return Length.Convert(plottedHeight * ratio, plottedUnit, modelUnit);
        }

        /// <summary>The inverse of <see cref="ModelTextHeight"/>.</summary>
        public static double PlottedTextHeight(double modelHeight, LengthUnit modelUnit, double ratio, LengthUnit plottedUnit)
        {
            if (!(ratio > 0)) throw new ArgumentOutOfRangeException(nameof(ratio));
            return Length.Convert(modelHeight / ratio, modelUnit, plottedUnit);
        }
    }

    /// <summary>A named plotted text height, e.g. "body" = 2.5 mm, "title" = 5 mm.</summary>
    public sealed class TextHeight
    {
        public TextHeight(string name, double plotted, LengthUnit unit, string? use = null)
        {
            if (!(plotted > 0)) throw new ArgumentOutOfRangeException(nameof(plotted));
            Name = name; Plotted = plotted; Unit = unit; Use = use;
        }

        public string Name { get; }
        public double Plotted { get; }
        public LengthUnit Unit { get; }
        public string? Use { get; }

        public double ModelHeight(double ratio, LengthUnit modelUnit) => Scale.ModelTextHeight(Plotted, Unit, ratio, modelUnit);
    }
}
