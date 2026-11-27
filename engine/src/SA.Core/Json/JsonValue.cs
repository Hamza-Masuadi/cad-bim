using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace SA.Core.Json
{
    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A node of a JSON document. Objects keep member insertion order; numbers keep their source text
    /// so 64-bit integers and doubles survive a round trip exactly.
    /// </summary>
    public abstract class JsonValue
    {
        internal JsonValue() { }

        public abstract JsonKind Kind { get; }

        public bool IsNull => Kind == JsonKind.Null;
        public bool IsString => Kind == JsonKind.String;
        public bool IsNumber => Kind == JsonKind.Number;
        public bool IsBool => Kind == JsonKind.Bool;
        public bool IsArray => Kind == JsonKind.Array;
        public bool IsObject => Kind == JsonKind.Object;

        public static JsonNull Null => JsonNull.Instance;

        public static JsonValue Parse(string text) => JsonReader.Parse(text);
        public static JsonValue Parse(byte[] utf8) => JsonReader.Parse(utf8);

        public string ToJson(bool indented = false) => JsonWriter.Write(this, indented);
        public byte[] ToUtf8Bytes(bool indented = false) => JsonWriter.WriteUtf8(this, indented);
        public override string ToString() => ToJson();

        public abstract JsonValue DeepClone();

        /// <summary>The string value. Throws <see cref="InvalidOperationException"/> if this is not a string.</summary>
        public string AsString() => this is JsonString s ? s.Value : throw WrongKind(JsonKind.String);
        public double AsDouble() => this is JsonNumber n ? n.Value : throw WrongKind(JsonKind.Number);
        public long AsInt64()
        {
            if (this is JsonNumber n && n.TryGetInt64(out long v)) return v;
            throw this is JsonNumber ? new InvalidOperationException("JSON number " + ToJson() + " is not a 64-bit integer.") : WrongKind(JsonKind.Number);
        }
        public bool AsBool() => this is JsonBool b ? b.Value : throw WrongKind(JsonKind.Bool);
        public JsonObject AsObject() => this as JsonObject ?? throw WrongKind(JsonKind.Object);
        public JsonArray AsArray() => this as JsonArray ?? throw WrongKind(JsonKind.Array);

        private InvalidOperationException WrongKind(JsonKind expected) =>
            new InvalidOperationException("Expected JSON " + KindName(expected) + ", found " + KindName(Kind) + ".");

        public static string KindName(JsonKind kind)
        {
            switch (kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return "boolean";
                case JsonKind.Number: return "number";
                case JsonKind.String: return "string";
                case JsonKind.Array: return "array";
                default: return "object";
            }
        }

        /// <summary>Structural equality. Numbers compare by value; object member order is ignored.</summary>
        public static bool DeepEquals(JsonValue? a, JsonValue? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (a.Kind != b.Kind) return false;
            switch (a)
            {
                case JsonNull _: return true;
                case JsonBool ab: return ab.Value == ((JsonBool)b).Value;
                case JsonString sa: return sa.Value == ((JsonString)b).Value;
                case JsonNumber na: return na.NumericEquals((JsonNumber)b);
                case JsonArray aa:
                {
                    var ba = (JsonArray)b;
                    if (aa.Count != ba.Count) return false;
                    for (int i = 0; i < aa.Count; i++)
                        if (!DeepEquals(aa[i], ba[i])) return false;
                    return true;
                }
                case JsonObject oa:
                {
                    var ob = (JsonObject)b;
                    if (oa.Count != ob.Count) return false;
                    foreach (var kv in oa)
                    {
                        if (!ob.TryGetValue(kv.Key, out var other) || !DeepEquals(kv.Value, other)) return false;
                    }
                    return true;
                }
            }
            return false;
        }

        public static implicit operator JsonValue(string? value) => value is null ? (JsonValue)JsonNull.Instance : new JsonString(value);
        public static implicit operator JsonValue(bool value) => value ? JsonBool.True : JsonBool.False;
        public static implicit operator JsonValue(int value) => JsonNumber.FromInt64(value);
        public static implicit operator JsonValue(long value) => JsonNumber.FromInt64(value);
        public static implicit operator JsonValue(double value) => JsonNumber.FromDouble(value);
    }

    public sealed class JsonNull : JsonValue
    {
        public static readonly JsonNull Instance = new JsonNull();
        private JsonNull() { }
        public override JsonKind Kind => JsonKind.Null;
        public override JsonValue DeepClone() => this;
    }

    public sealed class JsonBool : JsonValue
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);
        private JsonBool(bool value) { Value = value; }
        public bool Value { get; }
        public override JsonKind Kind => JsonKind.Bool;
        public override JsonValue DeepClone() => this;
        public static JsonBool Of(bool value) => value ? True : False;
    }

    public sealed class JsonString : JsonValue
    {
        public JsonString(string value) { Value = value ?? throw new ArgumentNullException(nameof(value)); }
        public string Value { get; }
        public override JsonKind Kind => JsonKind.String;
        public override JsonValue DeepClone() => this;
    }

    /// <summary>A JSON number kept as its exact source text (RFC 8259 grammar).</summary>
    public sealed class JsonNumber : JsonValue
    {
        private readonly double _value;

        private JsonNumber(string raw, double value) { Raw = raw; _value = value; }

        /// <summary>The number exactly as written, for example "9007199254740993" or "1.5e-3".</summary>
        public string Raw { get; }

        /// <summary>The nearest double. Large integers may lose precision here; use <see cref="TryGetInt64"/>.</summary>
        public double Value => _value;

        /// <summary>True when the source text has no fraction or exponent.</summary>
        public bool IsIntegerLiteral => Raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0;

        public override JsonKind Kind => JsonKind.Number;
        public override JsonValue DeepClone() => this;

        public bool TryGetInt64(out long value)
        {
            if (IsIntegerLiteral)
                return long.TryParse(Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            // 1.0 or 1e3 are integral; accept them only where a double represents them exactly.
            if (Math.Floor(_value) == _value && Math.Abs(_value) <= 9007199254740992.0)
            {
                value = (long)_value;
                return true;
            }
            value = 0;
            return false;
        }

        public static JsonNumber FromInt64(long value) => new JsonNumber(value.ToString(CultureInfo.InvariantCulture), value);

        public static JsonNumber FromDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("JSON cannot represent NaN or infinity.", nameof(value));
            string raw = value.ToString("R", CultureInfo.InvariantCulture);
            if (double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture) != value)
                raw = value.ToString("G17", CultureInfo.InvariantCulture);
            return new JsonNumber(raw, value);
        }

        /// <summary>Creates a number from JSON number text. Throws <see cref="FormatException"/> if the text is not a valid JSON number.</summary>
        public static JsonNumber FromRaw(string raw)
        {
            if (!JsonReader.IsValidNumberText(raw)) throw new FormatException("Not a JSON number: " + raw);
            double v = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(v)) throw new FormatException("JSON number out of range: " + raw);
            return new JsonNumber(raw, v);
        }

        internal static JsonNumber FromValidatedRaw(string raw, double value) => new JsonNumber(raw, value);

        internal bool NumericEquals(JsonNumber other)
        {
            if (Raw == other.Raw) return true;
            if (IsIntegerLiteral && other.IsIntegerLiteral && TryGetInt64(out long a) && other.TryGetInt64(out long b)) return a == b;
            return _value == other._value;
        }
    }

    public sealed class JsonArray : JsonValue, IReadOnlyList<JsonValue>
    {
        private readonly List<JsonValue> _items;

        public JsonArray() { _items = new List<JsonValue>(); }
        public JsonArray(IEnumerable<JsonValue> items)
        {
            _items = new List<JsonValue>();
            foreach (var i in items) Add(i);
        }
        public JsonArray(params JsonValue[] items) : this((IEnumerable<JsonValue>)items) { }

        public override JsonKind Kind => JsonKind.Array;
        public int Count => _items.Count;

        public JsonValue this[int index]
        {
            get => _items[index];
            set => _items[index] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public JsonArray Add(JsonValue item)
        {
            _items.Add(item ?? throw new ArgumentNullException(nameof(item)));
            return this;
        }

        public void RemoveAt(int index) => _items.RemoveAt(index);

        public override JsonValue DeepClone()
        {
            var copy = new JsonArray();
            foreach (var i in _items) copy.Add(i.DeepClone());
            return copy;
        }

        public IEnumerator<JsonValue> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public static JsonArray FromStrings(IEnumerable<string> values)
        {
            var a = new JsonArray();
            foreach (var v in values) a.Add(new JsonString(v));
            return a;
        }
    }

    /// <summary>A JSON object that preserves member insertion order. Keys are case-sensitive and unique.</summary>
    public sealed class JsonObject : JsonValue, IEnumerable<KeyValuePair<string, JsonValue>>
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, JsonValue> _map = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        public override JsonKind Kind => JsonKind.Object;
        public int Count => _keys.Count;
        public IReadOnlyList<string> Keys => _keys;

        /// <summary>Gets a member or null when absent (a JSON null member returns <see cref="JsonNull"/>). Setting replaces in place or appends.</summary>
        public JsonValue? this[string key]
        {
            get => _map.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (value is null) throw new ArgumentNullException(nameof(value), "Use JsonValue.Null for a JSON null, or Remove().");
                Set(key, value);
            }
        }

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public bool TryGetValue(string key, out JsonValue value)
        {
            if (_map.TryGetValue(key, out var v)) { value = v; return true; }
            value = JsonNull.Instance;
            return false;
        }

        /// <summary>Adds a new member. Throws if the key already exists.</summary>
        public JsonObject Add(string key, JsonValue value)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            if (_map.ContainsKey(key)) throw new ArgumentException("Duplicate key '" + key + "'.", nameof(key));
            _keys.Add(key);
            _map[key] = value ?? throw new ArgumentNullException(nameof(value));
            return this;
        }

        /// <summary>Sets a member, keeping its position if it already exists.</summary>
        public JsonObject Set(string key, JsonValue value)
        {
            if (key is null) throw new ArgumentNullException(nameof(key));
            if (!_map.ContainsKey(key)) _keys.Add(key);
            _map[key] = value ?? throw new ArgumentNullException(nameof(value));
            return this;
        }

        public bool Remove(string key)
        {
            if (!_map.Remove(key)) return false;
            _keys.Remove(key);
            return true;
        }

        public override JsonValue DeepClone()
        {
            var copy = new JsonObject();
            foreach (var k in _keys) copy.Add(k, _map[k].DeepClone());
            return copy;
        }

        public IEnumerator<KeyValuePair<string, JsonValue>> GetEnumerator()
        {
            foreach (var k in _keys) yield return new KeyValuePair<string, JsonValue>(k, _map[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public string? GetStringOrNull(string key) => _map.TryGetValue(key, out var v) && v is JsonString s ? s.Value : null;
    }
}
