using System.Globalization;
using System.Text;

namespace SA.Core.Json
{
    /// <summary>Writes a <see cref="JsonValue"/> tree as compact or indented (two spaces) JSON text.</summary>
    public static class JsonWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);

        public static string Write(JsonValue value, bool indented = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indented, 0);
            return sb.ToString();
        }

        public static byte[] WriteUtf8(JsonValue value, bool indented = false) => Utf8NoBom.GetBytes(Write(value, indented));

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            WriteString(sb, s);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, JsonValue v, bool indented, int depth)
        {
            switch (v)
            {
                case JsonNull _: sb.Append("null"); break;
                case JsonBool b: sb.Append(b.Value ? "true" : "false"); break;
                case JsonNumber n: sb.Append(n.Raw); break;
                case JsonString s: WriteString(sb, s.Value); break;
                case JsonArray a:
                    if (a.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indented) NewLine(sb, depth + 1);
                        WriteValue(sb, a[i], indented, depth + 1);
                    }
                    if (indented) NewLine(sb, depth);
                    sb.Append(']');
                    break;
                case JsonObject o:
                    if (o.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (indented) NewLine(sb, depth + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(indented ? ": " : ":");
                        WriteValue(sb, kv.Value, indented, depth + 1);
                    }
                    if (indented) NewLine(sb, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        bool loneSurrogate =
                            (char.IsHighSurrogate(c) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))) ||
                            (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1])));
                        if (c < 0x20 || c == '\u2028' || c == '\u2029' || loneSurrogate)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
