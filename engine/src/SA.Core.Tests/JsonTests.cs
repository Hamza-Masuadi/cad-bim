using System;
using System.Linq;
using System.Text;
using SA.Core.Json;
using Xunit;

namespace SA.Core.Tests
{
    public class JsonTests
    {
        [Fact]
        public void RoundTripKeepsMemberOrderAndValues()
        {
            string text = "{\"z\":1,\"a\":[true,false,null,\"x\"],\"m\":{\"b\":2.5,\"a\":-0.125}}";
            var v = JsonValue.Parse(text);
            Assert.Equal(text, v.ToJson());
            Assert.Equal(new[] { "z", "a", "m" }, v.AsObject().Keys.ToArray());
        }

        [Fact]
        public void IndentedOutputParsesBack()
        {
            var v = JsonValue.Parse("{\"a\":[1,{\"b\":[]}],\"c\":{}}");
            string pretty = v.ToJson(true);
            Assert.Contains("\n  \"a\": [", pretty);
            Assert.True(JsonValue.DeepEquals(v, JsonValue.Parse(pretty)));
        }

        [Theory]
        [InlineData("9007199254740993")]
        [InlineData("9223372036854775807")]
        [InlineData("-9223372036854775808")]
        public void LargeIntegersArePreservedExactly(string raw)
        {
            var n = (JsonNumber)JsonValue.Parse(raw);
            Assert.Equal(raw, n.ToJson());
            Assert.True(n.TryGetInt64(out long l));
            Assert.Equal(long.Parse(raw), l);
        }

        [Fact]
        public void IntegerTooBigForInt64IsKeptAsText()
        {
            var n = (JsonNumber)JsonValue.Parse("92233720368547758070");
            Assert.False(n.TryGetInt64(out _));
            Assert.Equal("92233720368547758070", n.ToJson());
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(1e-300)]
        [InlineData(123456.789)]
        [InlineData(-2.5e18)]
        [InlineData(double.Epsilon)]
        public void DoublesRoundTrip(double d)
        {
            var text = JsonNumber.FromDouble(d).ToJson();
            Assert.Equal(d, JsonValue.Parse(text).AsDouble());
        }

        [Fact]
        public void IntegralDoubleIsAnInteger()
        {
            Assert.True(((JsonNumber)JsonValue.Parse("1e3")).TryGetInt64(out long v));
            Assert.Equal(1000, v);
            Assert.False(((JsonNumber)JsonValue.Parse("1.5")).TryGetInt64(out _));
        }

        [Fact]
        public void NaNAndInfinityCannotBeWritten()
        {
            Assert.Throws<ArgumentException>(() => JsonNumber.FromDouble(double.NaN));
            Assert.Throws<ArgumentException>(() => JsonNumber.FromDouble(double.PositiveInfinity));
        }

        [Fact]
        public void EscapesAndSurrogatePairs()
        {
            var v = JsonValue.Parse("\"a\\\"b\\\\c\\/d\\n\\t\\u00e9\\ud83d\\ude00\"");
            Assert.Equal("a\"b\\c/d\n\té\U0001F600", v.AsString());
            var back = JsonValue.Parse(v.ToJson());
            Assert.Equal(v.AsString(), back.AsString());
        }

        [Fact]
        public void WriterEscapesControlCharacters()
        {
            string s = new JsonString("x\u0001y\u2028").ToJson();
            Assert.Equal("\"x\\u0001y\\u2028\"", s);
        }

        [Theory]
        [InlineData("{\"a\":1,}", "trailing comma")]
        [InlineData("[1,2,]", "trailing comma")]
        [InlineData("{\"a\":1 // c\n}", "comments")]
        [InlineData("01", "leading zeros")]
        [InlineData("{'a':1}", "expected a string key")]
        [InlineData("NaN", "expected a value")]
        [InlineData("{\"a\":1,\"a\":2}", "duplicate key 'a'")]
        [InlineData("\"abc", "unterminated string")]
        [InlineData("\"a\u0001\"", "control character")]
        [InlineData("\"\\ud800\"", "high surrogate")]
        [InlineData("\"\\udc00\"", "lone low surrogate")]
        [InlineData("\"\\x\"", "invalid escape")]
        [InlineData("", "empty document")]
        [InlineData("{} x", "after the end")]
        [InlineData("[1 2]", "expected ',' or ']'")]
        [InlineData("{\"a\" 1}", "expected ':'")]
        [InlineData("1.", "decimal point")]
        [InlineData("1e", "exponent")]
        [InlineData("-", "digit")]
        [InlineData("tru", "expected a value")]
        [InlineData("1e999", "out of range")]
        public void StrictErrors(string text, string expected)
        {
            var e = Assert.Throws<JsonParseException>(() => JsonValue.Parse(text));
            Assert.Contains(expected, e.Message);
        }

        [Fact]
        public void ErrorReportsLineAndColumn()
        {
            var e = Assert.Throws<JsonParseException>(() => JsonValue.Parse("{\n  \"a\": 1,\n  \"b\" 2\n}"));
            Assert.Equal(3, e.Line);
            Assert.Equal(7, e.Column);
            Assert.StartsWith("Invalid JSON at line 3, column 7", e.Message);
        }

        [Fact]
        public void DeepNestingIsRejected()
        {
            string deep = new string('[', 300) + new string(']', 300);
            var e = Assert.Throws<JsonParseException>(() => JsonValue.Parse(deep));
            Assert.Contains("nesting", e.Message);
            Assert.NotNull(JsonValue.Parse(new string('[', 200) + new string(']', 200)));
        }

        [Fact]
        public void Utf8BytesWithBomAndInvalidBytes()
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":\"é\"}")).ToArray();
            Assert.Equal("é", JsonValue.Parse(bom).AsObject()["a"]!.AsString());
            var e = Assert.Throws<JsonParseException>(() => JsonValue.Parse(new byte[] { 0x22, 0xC3, 0x28, 0x22 }));
            Assert.Contains("UTF-8", e.Message);
        }

        [Fact]
        public void DeepEqualsIgnoresMemberOrderAndNumberSpelling()
        {
            Assert.True(JsonValue.DeepEquals(JsonValue.Parse("{\"a\":1,\"b\":[1.0]}"), JsonValue.Parse("{\"b\":[1],\"a\":1e0}")));
            Assert.False(JsonValue.DeepEquals(JsonValue.Parse("[1,2]"), JsonValue.Parse("[2,1]")));
            Assert.False(JsonValue.DeepEquals(JsonValue.Parse("{\"a\":null}"), JsonValue.Parse("{}")));
        }

        [Fact]
        public void ObjectSetKeepsPositionAndAddRejectsDuplicates()
        {
            var o = new JsonObject().Add("a", 1).Add("b", 2);
            o.Set("a", 3);
            Assert.Equal(new[] { "a", "b" }, o.Keys.ToArray());
            Assert.Equal(3, o["a"]!.AsInt64());
            Assert.Throws<ArgumentException>(() => o.Add("b", 1));
            Assert.Null(o["missing"]);
            Assert.True(o.Remove("a"));
            Assert.Equal(new[] { "b" }, o.Keys.ToArray());
        }

        [Fact]
        public void CursorErrorsNameTheExactPath()
        {
            var c = new JsonCursor(JsonValue.Parse("{\"layers\":[{\"name\":\"A\"},{\"name\":5}]}"));
            var e = Assert.Throws<JsonAccessException>(() => c.Required("layers").At(1).GetString("name"));
            Assert.Equal("$.layers[1].name", e.Path);
            Assert.Contains("expected string, found number", e.Message);
            var missing = Assert.Throws<JsonAccessException>(() => c.Required("layers").At(0).GetString("color"));
            Assert.Equal("$.layers[0].color", missing.Path);
            Assert.Equal("fallback", c.Required("layers").At(0).GetString("color", "fallback"));
        }

        [Fact]
        public void CursorRejectsUnknownKeys()
        {
            var c = new JsonCursor(JsonValue.Parse("{\"name\":\"x\",\"colour\":1}"));
            var e = Assert.Throws<JsonAccessException>(() => c.RejectUnknownKeys("name", "color"));
            Assert.Equal("$.colour", e.Path);
        }

        [Fact]
        public void PathsQuoteOddKeysAndSelectFindsThem()
        {
            Assert.Equal("$.ops[2].args", JsonPath.Key(JsonPath.Index("$.ops", 2), "args"));
            Assert.Equal("$[\"a b\"]", JsonPath.Key("$", "a b"));
            var root = JsonValue.Parse("{\"a b\":[{\"c\":7}]}");
            Assert.Equal(7, JsonPath.Select(root, "$[\"a b\"][0].c")!.AsInt64());
            Assert.Null(JsonPath.Select(root, "$.x[3]"));
        }

        [Fact]
        public void IntegerCursorRejectsFractions()
        {
            var c = new JsonCursor(JsonValue.Parse("{\"n\":1.5}"));
            Assert.Throws<JsonAccessException>(() => c.GetInt32("n"));
        }
    }
}
