using System;
using System.Globalization;
using System.Text;

namespace SA.Core.Json
{
    /// <summary>Thrown for malformed JSON text. Line and column are 1-based.</summary>
    public sealed class JsonParseException : FormatException
    {
        public JsonParseException(string reason, int position, int line, int column)
            : base("Invalid JSON at line " + line + ", column " + column + ": " + reason)
        {
            Reason = reason;
            Position = position;
            Line = line;
            Column = column;
        }

        public string Reason { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }
    }

    /// <summary>
    /// Strict RFC 8259 reader: no comments, no trailing commas, no single quotes, no NaN, no duplicate keys,
    /// no unescaped control characters, no lone surrogate escapes. Nesting is limited to <see cref="MaxDepth"/>.
    /// </summary>
    public sealed class JsonReader
    {
        public const int MaxDepth = 256;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly string _s;
        private int _i;
        private int _depth;

        private JsonReader(string text) { _s = text; }

        public static JsonValue Parse(string text)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));
            var r = new JsonReader(text);
            if (r._s.Length > 0 && r._s[0] == '﻿') r._i = 1;
            r.SkipWs();
            if (r._i >= r._s.Length) throw r.Error("empty document, expected a value");
            var v = r.ReadValue();
            r.SkipWs();
            if (r._i < r._s.Length) throw r.Error("unexpected '" + Printable(r._s[r._i]) + "' after the end of the document");
            return v;
        }

        public static JsonValue Parse(byte[] utf8)
        {
            if (utf8 is null) throw new ArgumentNullException(nameof(utf8));
            string text;
            try
            {
                int start = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
                text = StrictUtf8.GetString(utf8, start, utf8.Length - start);
            }
            catch (DecoderFallbackException)
            {
                throw new JsonParseException("input is not valid UTF-8", 0, 1, 1);
            }
            return Parse(text);
        }

        public static bool TryParse(string text, out JsonValue? value, out JsonParseException? error)
        {
            try
            {
                value = Parse(text);
                error = null;
                return true;
            }
            catch (JsonParseException e)
            {
                value = null;
                error = e;
                return false;
            }
        }

        internal static bool IsValidNumberText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            var r = new JsonReader(raw);
            try
            {
                r.ScanNumber();
                return r._i == raw.Length;
            }
            catch (JsonParseException)
            {
                return false;
            }
        }

        private JsonValue ReadValue()
        {
            if (_i >= _s.Length) throw Error("unexpected end of input, expected a value");
            char c = _s[_i];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return new JsonString(ReadString());
                case 't': ReadLiteral("true"); return JsonBool.True;
                case 'f': ReadLiteral("false"); return JsonBool.False;
                case 'n': ReadLiteral("null"); return JsonNull.Instance;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw Error("unexpected '" + Printable(c) + "', expected a value");
            }
        }

        private JsonObject ReadObject()
        {
            Enter();
            _i++; // {
            var obj = new JsonObject();
            SkipWs();
            if (Peek() == '}') { _i++; _depth--; return obj; }
            while (true)
            {
                SkipWs();
                if (Peek() != '"')
                {
                    if (Peek() == '}') throw Error("trailing comma before '}'");
                    throw Error(AtEnd ? "unexpected end of input inside an object" : "expected a string key, found '" + Printable(_s[_i]) + "'");
                }
                int keyPos = _i;
                string key = ReadString();
                if (obj.ContainsKey(key)) throw ErrorAt(keyPos, "duplicate key '" + key + "'");
                SkipWs();
                if (Peek() != ':') throw Error(AtEnd ? "unexpected end of input, expected ':'" : "expected ':' after key '" + key + "'");
                _i++;
                SkipWs();
                var value = ReadValue();
                obj.Add(key, value);
                SkipWs();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == '}') { _i++; break; }
                throw Error(AtEnd ? "unexpected end of input inside an object" : "expected ',' or '}' after a member, found '" + Printable(c) + "'");
            }
            _depth--;
            return obj;
        }

        private JsonArray ReadArray()
        {
            Enter();
            _i++; // [
            var arr = new JsonArray();
            SkipWs();
            if (Peek() == ']') { _i++; _depth--; return arr; }
            while (true)
            {
                SkipWs();
                if (Peek() == ']') throw Error("trailing comma before ']'");
                arr.Add(ReadValue());
                SkipWs();
                char c = Peek();
                if (c == ',') { _i++; continue; }
                if (c == ']') { _i++; break; }
                throw Error(AtEnd ? "unexpected end of input inside an array" : "expected ',' or ']' after an element, found '" + Printable(c) + "'");
            }
            _depth--;
            return arr;
        }

        private string ReadString()
        {
            int start = _i;
            _i++; // opening quote
            StringBuilder? sb = null;
            int runStart = _i;
            while (true)
            {
                if (_i >= _s.Length) throw ErrorAt(start, "unterminated string");
                char c = _s[_i];
                if (c == '"')
                {
                    string result;
                    if (sb is null) result = _s.Substring(runStart, _i - runStart);
                    else { sb.Append(_s, runStart, _i - runStart); result = sb.ToString(); }
                    _i++;
                    return result;
                }
                if (c < 0x20) throw Error("control character U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + " must be escaped inside a string");
                if (c != '\\') { _i++; continue; }

                sb ??= new StringBuilder();
                sb.Append(_s, runStart, _i - runStart);
                _i++;
                if (_i >= _s.Length) throw ErrorAt(start, "unterminated string");
                char e = _s[_i];
                switch (e)
                {
                    case '"': sb.Append('"'); _i++; break;
                    case '\\': sb.Append('\\'); _i++; break;
                    case '/': sb.Append('/'); _i++; break;
                    case 'b': sb.Append('\b'); _i++; break;
                    case 'f': sb.Append('\f'); _i++; break;
                    case 'n': sb.Append('\n'); _i++; break;
                    case 'r': sb.Append('\r'); _i++; break;
                    case 't': sb.Append('\t'); _i++; break;
                    case 'u':
                    {
                        int escPos = _i - 1;
                        char u = ReadHex4();
                        if (char.IsHighSurrogate(u))
                        {
                            if (_i + 1 < _s.Length && _s[_i] == '\\' && _s[_i + 1] == 'u')
                            {
                                _i++;
                                char low = ReadHex4();
                                if (!char.IsLowSurrogate(low)) throw ErrorAt(escPos, "high surrogate escape not followed by a low surrogate");
                                sb.Append(u).Append(low);
                            }
                            else throw ErrorAt(escPos, "high surrogate escape not followed by a low surrogate");
                        }
                        else if (char.IsLowSurrogate(u)) throw ErrorAt(escPos, "lone low surrogate escape");
                        else sb.Append(u);
                        break;
                    }
                    default:
                        throw Error("invalid escape '\\" + Printable(e) + "'");
                }
                runStart = _i;
            }
        }

        private char ReadHex4()
        {
            _i++; // 'u'
            if (_i + 4 > _s.Length) throw Error("incomplete \\u escape");
            int v = 0;
            for (int k = 0; k < 4; k++)
            {
                char h = _s[_i + k];
                int d = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                if (d < 0) throw ErrorAt(_i + k, "invalid hex digit '" + Printable(h) + "' in \\u escape");
                v = v * 16 + d;
            }
            _i += 4;
            return (char)v;
        }

        private JsonNumber ReadNumber()
        {
            int start = _i;
            ScanNumber();
            string raw = _s.Substring(start, _i - start);
            double v = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(v)) throw ErrorAt(start, "number " + raw + " is out of range");
            return JsonNumber.FromValidatedRaw(raw, v);
        }

        private void ScanNumber()
        {
            if (Peek() == '-') _i++;
            if (AtEnd) throw Error("expected a digit after '-'");
            char c = _s[_i];
            if (c == '0')
            {
                _i++;
                if (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') throw Error("leading zeros are not allowed");
            }
            else if (c >= '1' && c <= '9')
            {
                while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            else throw Error("expected a digit, found '" + Printable(c) + "'");

            if (Peek() == '.')
            {
                _i++;
                if (AtEnd || _s[_i] < '0' || _s[_i] > '9') throw Error("expected a digit after the decimal point");
                while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                _i++;
                if (Peek() == '+' || Peek() == '-') _i++;
                if (AtEnd || _s[_i] < '0' || _s[_i] > '9') throw Error("expected a digit in the exponent");
                while (!AtEnd && _s[_i] >= '0' && _s[_i] <= '9') _i++;
            }
        }

        private void ReadLiteral(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
            {
                int end = _i;
                while (end < _s.Length && char.IsLetter(_s[end])) end++;
                string found = end > _i ? _s.Substring(_i, Math.Min(end - _i, 20)) : Printable(_s[_i]);
                throw Error("unexpected '" + found + "', expected a value");
            }
            _i += word.Length;
            if (!AtEnd && char.IsLetterOrDigit(_s[_i])) throw Error("unexpected characters after '" + word + "'");
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                else if (c == '/' && _i + 1 < _s.Length && (_s[_i + 1] == '/' || _s[_i + 1] == '*')) throw Error("comments are not allowed in JSON");
                else break;
            }
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Error("nesting deeper than " + MaxDepth + " levels");
        }

        private bool AtEnd => _i >= _s.Length;
        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private JsonParseException Error(string reason) => ErrorAt(_i, reason);

        private JsonParseException ErrorAt(int pos, string reason)
        {
            int line = 1, col = 1;
            for (int k = 0; k < pos && k < _s.Length; k++)
            {
                if (_s[k] == '\n') { line++; col = 1; }
                else if (_s[k] != '\r') col++;
            }
            return new JsonParseException(reason, pos, line, col);
        }

        private static string Printable(char c)
        {
            if (c < 0x20 || c == 0x7F) return "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
            return c.ToString();
        }
    }
}
