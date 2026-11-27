using SA.Core.Standards;
using SA.Core.Units;
using Xunit;

namespace SA.Core.Tests
{
    public class UnitsTests
    {
        [Theory]
        [InlineData("10'-6\"", 126)]
        [InlineData("10' 6 1/2\"", 126.5)]
        [InlineData("10'-6 1/2\"", 126.5)]
        [InlineData("10'6\"", 126)]
        [InlineData("10'6", 126)]
        [InlineData("10' - 6\"", 126)]
        [InlineData("6\"", 6)]
        [InlineData("6 in", 6)]
        [InlineData("12in", 12)]
        [InlineData("10'", 120)]
        [InlineData("10 ft", 120)]
        [InlineData("10.5'", 126)]
        [InlineData("10 ft 6 in", 126)]
        [InlineData("10ft6in", 126)]
        [InlineData("6-1/2\"", 6.5)]
        [InlineData("6 1/2\"", 6.5)]
        [InlineData("3/4\"", 0.75)]
        [InlineData("1'-0 3/4\"", 12.75)]
        [InlineData("0'-1/2\"", 0.5)]
        [InlineData("1'-1/2\"", 12.5)]
        [InlineData("10' 6.25\"", 126.25)]
        [InlineData("-2'-3\"", -27)]
        [InlineData("10′-6″", 126)]
        [InlineData("10''", 10)]
        [InlineData("  4'  ", 48)]
        [InlineData("2 feet", 24)]
        public void ImperialStringsInInches(string text, double inches)
        {
            Assert.True(Length.TryParse(text, LengthUnit.Inch, out double v, out string err), err);
            Assert.Equal(inches, v, 9);
        }

        [Theory]
        [InlineData("3200", LengthUnit.Millimetre, 3200)]
        [InlineData("3.2m", LengthUnit.Millimetre, 3200)]
        [InlineData("3.2 M", LengthUnit.Millimetre, 3200)]
        [InlineData("320 cm", LengthUnit.Metre, 3.2)]
        [InlineData("3200mm", LengthUnit.Metre, 3.2)]
        [InlineData("1e3mm", LengthUnit.Millimetre, 1000)]
        [InlineData(".5m", LengthUnit.Millimetre, 500)]
        [InlineData("10'-6\"", LengthUnit.Millimetre, 3200.4)]
        [InlineData("1 ft", LengthUnit.Millimetre, 304.8)]
        [InlineData("12", LengthUnit.Foot, 12)]
        [InlineData("1/2", LengthUnit.Millimetre, 0.5)]
        [InlineData("6\"", LengthUnit.Foot, 0.5)]
        [InlineData("+25", LengthUnit.Centimetre, 25)]
        public void MetricAndMixed(string text, LengthUnit unit, double expected)
        {
            Assert.True(Length.TryParse(text, unit, out double v, out string err), err);
            Assert.Equal(expected, v, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("10'-6\"x")]
        [InlineData("1/0\"")]
        [InlineData("--5")]
        [InlineData("-")]
        [InlineData("5 5")]
        [InlineData("10' 6.5 1/2\"")]
        [InlineData("3.2 km")]
        [InlineData("1,5m")]
        [InlineData("6\"6'")]
        [InlineData(null)]
        public void InvalidLengths(string? text)
        {
            Assert.False(Length.TryParse(text, LengthUnit.Millimetre, out _, out string err));
            Assert.NotEqual("", err);
        }

        [Fact]
        public void ConversionIsExact()
        {
            Assert.Equal(304.8, Length.Convert(1, LengthUnit.Foot, LengthUnit.Millimetre), 12);
            Assert.Equal(12, Length.Convert(1, LengthUnit.Foot, LengthUnit.Inch));
            Assert.Equal(1, Length.Convert(25.4, LengthUnit.Millimetre, LengthUnit.Inch), 12);
            Assert.Equal(0.32, Length.Convert(32, LengthUnit.Centimetre, LengthUnit.Metre), 12);
            Assert.True(Length.TryParseUnit("ft", out var u) && u == LengthUnit.Foot);
            Assert.False(Length.TryParseUnit("FT", out _));
        }

        [Theory]
        [InlineData(126.5, LengthStyle.Architectural, "10'-6 1/2\"")]
        [InlineData(0, LengthStyle.Architectural, "0'-0\"")]
        [InlineData(11.999, LengthStyle.Architectural, "1'-0\"")]
        [InlineData(-27, LengthStyle.Architectural, "-2'-3\"")]
        [InlineData(0.375, LengthStyle.Architectural, "0'-3/8\"")]
        [InlineData(126.5, LengthStyle.Engineering, "10'-6.50\"")]
        [InlineData(126.5, LengthStyle.FractionalInches, "126 1/2\"")]
        public void FormatImperial(double inches, LengthStyle style, string expected)
        {
            Assert.Equal(expected, Length.Format(inches, LengthUnit.Inch, style));
        }

        [Fact]
        public void FormatDecimalAndRoundTrip()
        {
            Assert.Equal("3200 mm", Length.Format(3200, LengthUnit.Millimetre));
            Assert.Equal("3.25 m", Length.Format(3.2500001, LengthUnit.Metre));
            Assert.Equal("0 mm", Length.Format(-0.0001, LengthUnit.Millimetre));
            foreach (double mm in new[] { 0.0, 3200.4, 914.4, 12.7, 6096 })
            {
                string s = Length.Format(mm, LengthUnit.Millimetre, LengthStyle.Architectural);
                Assert.Equal(mm, Length.Parse(s, LengthUnit.Millimetre), 1);
            }
        }

        [Fact]
        public void Tolerances()
        {
            Assert.Equal(0.5, Tolerance.DefaultLength(LengthUnit.Millimetre));
            Assert.True(Tolerance.AngleEqual(359.99, 0, 0.02));
            Assert.True(Tolerance.AngleEqual(-90, 270));
            Assert.False(Tolerance.AngleEqual(10, 11, 0.5));
            Assert.True(Tolerance.LengthEqual(1, LengthUnit.Foot, 304.9, LengthUnit.Millimetre, 0.2, LengthUnit.Millimetre));
            Assert.False(Tolerance.LengthEqual(1, LengthUnit.Foot, 306, LengthUnit.Millimetre, 0.2, LengthUnit.Millimetre));
            Assert.Equal(0, Tolerance.NormaliseDegrees(720));
        }

        [Theory]
        [InlineData("45", 45)]
        [InlineData("45°", 45)]
        [InlineData("-30 deg", -30)]
        public void Angles(string text, double deg)
        {
            Assert.True(Angle.TryParseDegrees(text, out double d));
            Assert.Equal(deg, d, 9);
        }

        [Theory]
        [InlineData("1:100", 100)]
        [InlineData("1 : 50", 50)]
        [InlineData("2:1", 0.5)]
        [InlineData("1/4\" = 1'-0\"", 48)]
        [InlineData("1\" = 10'-0\"", 120)]
        [InlineData("3/32\"=1'", 128)]
        [InlineData("1/8\" = 1'", 96)]
        public void ScaleRatios(string text, double ratio)
        {
            Assert.True(Scale.TryParseRatio(text, out double r));
            Assert.Equal(ratio, r, 9);
        }

        [Theory]
        [InlineData("1:0")]
        [InlineData("100")]
        [InlineData("abc")]
        [InlineData("1 = 100")]
        [InlineData("")]
        public void BadScales(string text) => Assert.False(Scale.TryParseRatio(text, out _));

        [Fact]
        public void TextHeightByScale()
        {
            Assert.Equal(250, Scale.ModelTextHeight(2.5, LengthUnit.Millimetre, 100, LengthUnit.Millimetre), 9);
            Assert.Equal(4.5, Scale.ModelTextHeight(3.0 / 32, LengthUnit.Inch, 48, LengthUnit.Inch), 9);
            Assert.Equal(0.125, Scale.ModelTextHeight(2.5, LengthUnit.Millimetre, 50, LengthUnit.Metre), 9);
            Assert.Equal(0.375, Scale.ModelTextHeight(3.0 / 32, LengthUnit.Inch, 48, LengthUnit.Foot), 9);
            Assert.Equal(2.5, Scale.PlottedTextHeight(250, LengthUnit.Millimetre, 100, LengthUnit.Millimetre), 9);
        }
    }
}
