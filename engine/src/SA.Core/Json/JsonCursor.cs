using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SA.Core.Json
{
    /// <summary>Thrown by <see cref="JsonCursor"/> when a value is missing or of the wrong type. <see cref="Path"/> locates it.</summary>
    public sealed class JsonAccessException : FormatException
    {
        public JsonAccessException(string path, string reason) : base(path + ": " + reason)
        {
            Path = path;
            Reason = reason;
        }

        public string Path { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Builds JSON paths in the form used by every issue and error in this library:
    /// <c>$.ops[2].args.start</c>, with keys that are not plain identifiers written as <c>$["a b"]</c>.
    /// </summary>
    public static class JsonPath
    {
        public const string Root = "$";

        public static string Key(string parent, string key)
        {
            bool plain = key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_');
            for (int i = 0; plain && i < key.Length; i++)
            {
                char c = key[i];
                plain = char.IsLetterOrDigit(c) || c == '_' || c == '-';
            }
            return plain ? parent + "." + key : parent + "[" + JsonWriter.Quote(key) + "]";
        }

        public static string Index(string parent, int index) => parent + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        /// <summary>Follows a path produced by <see cref="Key"/> and <see cref="Index"/>. Returns null when it leads nowhere.</summary>
        public static JsonValue? Select(JsonValue root, string path)
        {
            if (path == Root) return root;
            if (!path.StartsWith(Root, StringComparison.Ordinal)) return null;
            JsonValue? cur = root;
            int i = 1;
            while (i < path.Length && cur != null)
            {
                if (path[i] == '.')
                {
                    int j = i + 1;
                    while (j < path.Length && path[j] != '.' && path[j] != '[') j++;
                    string key = path.Substring(i + 1, j - i - 1);
                    cur = cur is JsonObject o ? o[key] : null;
                    i = j;
                }
                else if (path[i] == '[')
                {
                    if (i + 1 < path.Length && path[i + 1] == '"')
                    {
                        var sb = new StringBuilder();
                        int j = i + 2;
                        while (j < path.Length && path[j] != '"')
                        {
                            if (path[j] == '\\' && j + 1 < path.Length) { j++; }
                            sb.Append(path[j]);
                            j++;
                        }
                        cur = cur is JsonObject o ? o[sb.ToString()] : null;
                        i = j + 2;
                    }
                    else
                    {
                        int j = path.IndexOf(']', i);
                        if (j < 0) return null;
                        if (!int.TryParse(path.Substring(i + 1, j - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int idx)) return null;
                        cur = cur is JsonArray a && idx < a.Count ? a[idx] : null;
                        i = j + 1;
                    }
                }
                else return null;
            }
            return cur;
        }
    }

    /// <summary>
    /// Typed, path-aware access to a JSON tree. Every failure throws <see cref="JsonAccessException"/>
    /// naming the exact path, for example <c>$.layers[3].lineweight: expected number, found string</c>.
    /// </summary>
    public sealed class JsonCursor
    {
        public JsonCursor(JsonValue value, string path = JsonPath.Root)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
            Path = path;
        }

        public JsonValue Value { get; }
        public string Path { get; }
        public JsonKind Kind => Value.Kind;
        public bool IsNull => Value.IsNull;

        public JsonAccessException Fail(string reason) => new JsonAccessException(Path, reason);

        private JsonAccessException WrongType(string expected) =>
            Fail("expected " + expected + ", found " + JsonValue.KindName(Value.Kind));

        public JsonObject AsObject() => Value as JsonObject ?? throw WrongType("object");
        public JsonArray AsArray() => Value as JsonArray ?? throw WrongType("array");
        public string AsString() => Value is JsonString s ? s.Value : throw WrongType("string");
        public bool AsBool() => Value is JsonBool b ? b.Value : throw WrongType("boolean");
        public double AsNumber() => Value is JsonNumber n ? n.Value : throw WrongType("number");

        public string AsNonEmptyString()
        {
            string s = AsString();
            if (s.Trim().Length == 0) throw Fail("must not be empty");
            return s;
        }

        public long AsInt64()
        {
            if (!(Value is JsonNumber n)) throw WrongType("integer");
            if (!n.TryGetInt64(out long v)) throw Fail("expected an integer, found " + n.Raw);
            return v;
        }

        public int AsInt32()
        {
            long v = AsInt64();
            if (v < int.MinValue || v > int.MaxValue) throw Fail("integer " + v + " is out of range");
            return (int)v;
        }

        public bool Has(string key) => Value is JsonObject o && o.ContainsKey(key);

        /// <summary>The member, which must exist (it may be JSON null).</summary>
        public JsonCursor Required(string key)
        {
            var o = AsObject();
            if (!o.TryGetValue(key, out var v)) throw new JsonAccessException(JsonPath.Key(Path, key), "is required");
            return new JsonCursor(v, JsonPath.Key(Path, key));
        }

        /// <summary>The member, or null when it is absent or JSON null.</summary>
        public JsonCursor? Optional(string key)
        {
            var o = AsObject();
            if (!o.TryGetValue(key, out var v) || v.IsNull) return null;
            return new JsonCursor(v, JsonPath.Key(Path, key));
        }

        public string GetString(string key) => Required(key).AsString();
        public string GetNonEmptyString(string key) => Required(key).AsNonEmptyString();
        public string? GetStringOrNull(string key) => Optional(key)?.AsString();
        public string GetString(string key, string fallback) => Optional(key)?.AsString() ?? fallback;
        public double GetNumber(string key) => Required(key).AsNumber();
        public double GetNumber(string key, double fallback) => Optional(key)?.AsNumber() ?? fallback;
        public double? GetNumberOrNull(string key) => Optional(key)?.AsNumber();
        public int GetInt32(string key) => Required(key).AsInt32();
        public int GetInt32(string key, int fallback) => Optional(key)?.AsInt32() ?? fallback;
        public bool GetBool(string key, bool fallback) => Optional(key)?.AsBool() ?? fallback;

        public JsonCursor At(int index)
        {
            var a = AsArray();
            if (index < 0 || index >= a.Count) throw Fail("index " + index + " is out of range");
            return new JsonCursor(a[index], JsonPath.Index(Path, index));
        }

        public IEnumerable<JsonCursor> Items()
        {
            var a = AsArray();
            for (int i = 0; i < a.Count; i++) yield return new JsonCursor(a[i], JsonPath.Index(Path, i));
        }

        public IEnumerable<KeyValuePair<string, JsonCursor>> Members()
        {
            var o = AsObject();
            foreach (var kv in o) yield return new KeyValuePair<string, JsonCursor>(kv.Key, new JsonCursor(kv.Value, JsonPath.Key(Path, kv.Key)));
        }

        public List<string> GetStringList(string key)
        {
            var list = new List<string>();
            var c = Optional(key);
            if (c is null) return list;
            foreach (var item in c.Items()) list.Add(item.AsString());
            return list;
        }

        /// <summary>Throws if the object has a member not in <paramref name="allowed"/>. Catches misspelt keys in data files.</summary>
        public void RejectUnknownKeys(params string[] allowed)
        {
            var o = AsObject();
            foreach (var k in o.Keys)
            {
                if (Array.IndexOf(allowed, k) < 0)
                    throw new JsonAccessException(JsonPath.Key(Path, k), "unknown member (allowed: " + string.Join(", ", allowed) + ")");
            }
        }
    }
}
