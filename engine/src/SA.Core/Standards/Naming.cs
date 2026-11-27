using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Json;

namespace SA.Core.Standards
{
    public sealed class CodeEntry
    {
        public CodeEntry(string code, string? name = null, string? description = null)
        {
            Code = code; Name = name; Description = description;
        }

        public string Code { get; }
        public string? Name { get; }
        public string? Description { get; }
    }

    /// <summary>Problem codes for name validation.</summary>
    public static class NameProblemCodes
    {
        public const string Empty = "empty";
        public const string BadCase = "bad_case";
        public const string UnknownCode = "unknown_code";
        public const string BadLength = "bad_length";
        public const string BadPattern = "bad_pattern";
        public const string BadCharacters = "bad_characters";
        public const string MissingField = "missing_field";
        public const string TooManyFields = "too_many_fields";
        public const string BadExtension = "bad_extension";
        public const string NoMatch = "no_match";
    }

    public sealed class NameProblem
    {
        public NameProblem(string field, string code, string message)
        {
            Field = field; Code = code; Message = message;
        }

        /// <summary>The field the problem is in, or "" for the whole name.</summary>
        public string Field { get; }
        public string Code { get; }
        public string Message { get; }

        public override string ToString() => (Field.Length > 0 ? Field + ": " : "") + Message;
    }

    /// <summary>The outcome of parsing a layer name, sheet id or file name.</summary>
    public sealed class NameResult
    {
        public NameResult(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public bool Valid => Problems.Count == 0;
        /// <summary>Recognised field values in order, e.g. discipline = "A", major = "WALL".</summary>
        public List<KeyValuePair<string, string>> Fields { get; } = new List<KeyValuePair<string, string>>();
        public List<NameProblem> Problems { get; } = new List<NameProblem>();

        public string? this[string field]
        {
            get
            {
                foreach (var kv in Fields) if (kv.Key == field) return kv.Value;
                return null;
            }
        }

        internal NameResult Problem(string field, string code, string message)
        {
            Problems.Add(new NameProblem(field, code, message));
            return this;
        }

        public string Explain() => Valid ? "'" + Name + "' is valid" : "'" + Name + "': " + string.Join("; ", Problems.Select(p => p.ToString()));
    }

    public enum NameCase { Any, Upper, Lower }

    internal static class NamingUtil
    {
        public static NameCase ParseCase(JsonCursor? c)
        {
            if (c is null) return NameCase.Any;
            switch (c.AsString())
            {
                case "any": return NameCase.Any;
                case "upper": return NameCase.Upper;
                case "lower": return NameCase.Lower;
                default: throw c.Fail("expected 'any', 'upper' or 'lower'");
            }
        }

        public static bool CaseOk(string s, NameCase c) =>
            c == NameCase.Any || (c == NameCase.Upper ? s == s.ToUpperInvariant() : s == s.ToLowerInvariant());

        public static string CaseWord(NameCase c) => c == NameCase.Upper ? "upper case" : "lower case";

        public static string Allowed(IReadOnlyList<CodeEntry> codes)
        {
            var shown = codes.Take(12).Select(x => x.Code);
            return string.Join(", ", shown) + (codes.Count > 12 ? ", ... (" + codes.Count + " codes)" : "");
        }

        public static bool IsAlnum(string s) => s.Length > 0 && s.All(char.IsLetterOrDigit);

        public static Regex Compile(JsonCursor c)
        {
            string p = c.AsString();
            try { return new Regex("^(?:" + p + ")$", RegexOptions.CultureInvariant); }
            catch (ArgumentException e) { throw c.Fail("invalid regular expression: " + e.Message); }
        }
    }

    /// <summary>
    /// A name made of ordered fields with literal separators, each field limited by a code list, a pattern and/or a
    /// length. Used for sheet identifiers ("A-101") and file/container names ("PRJ-ARC-ZZ-01-DR-A-1001.dwg").
    /// Parsing backtracks, so separators may be empty and fields may be optional.
    /// </summary>
    public sealed class NamingRule
    {
        public sealed class RuleField
        {
            internal RuleField(string name, string separator, IReadOnlyList<CodeEntry>? codes, Regex? pattern, string? patternText,
                int minLength, int maxLength, bool optional, string? description)
            {
                Name = name; Separator = separator; Codes = codes; Pattern = pattern; PatternText = patternText;
                MinLength = minLength; MaxLength = maxLength; Optional = optional; Description = description;
            }

            public string Name { get; }
            /// <summary>Literal text written before this field when any earlier field is present.</summary>
            public string Separator { get; }
            public IReadOnlyList<CodeEntry>? Codes { get; }
            public Regex? Pattern { get; }
            public string? PatternText { get; }
            public int MinLength { get; }
            public int MaxLength { get; }
            public bool Optional { get; }
            public string? Description { get; }

            internal bool Accepts(string v, out string why)
            {
                why = "";
                if (v.Length < MinLength || v.Length > MaxLength)
                {
                    why = "'" + v + "' must be " + (MinLength == MaxLength ? MinLength.ToString() : MinLength + " to " + (MaxLength == int.MaxValue ? "any" : MaxLength.ToString())) + " characters";
                    return false;
                }
                if (Pattern != null && !Pattern.IsMatch(v)) { why = "'" + v + "' does not match " + PatternText; return false; }
                if (Codes != null && !Codes.Any(c => c.Code == v)) { why = "'" + v + "' is not an allowed code (allowed: " + NamingUtil.Allowed(Codes) + ")"; return false; }
                if (Pattern is null && Codes is null && !NamingUtil.IsAlnum(v)) { why = "'" + v + "' must be letters and digits"; return false; }
                return true;
            }
        }

        private NamingRule(List<RuleField> fields, NameCase nameCase, List<string> extensions, string? description, List<string> examples)
        {
            Fields = fields; Case = nameCase; Extensions = extensions; Description = description; Examples = examples;
        }

        public IReadOnlyList<RuleField> Fields { get; }
        public NameCase Case { get; }
        /// <summary>Allowed extensions including the dot, compared case-insensitively. Empty means names carry no extension.</summary>
        public IReadOnlyList<string> Extensions { get; }
        public string? Description { get; }
        public IReadOnlyList<string> Examples { get; }

        internal static NamingRule FromJson(JsonCursor c, IReadOnlyDictionary<string, List<CodeEntry>> codeLists)
        {
            c.RejectUnknownKeys("description", "case", "extensions", "fields", "examples");
            var fields = new List<RuleField>();
            var names = new HashSet<string>();
            foreach (var f in c.Required("fields").Items())
            {
                f.RejectUnknownKeys("name", "separator", "codes", "codeList", "pattern", "minLength", "maxLength", "length", "optional", "description");
                string name = f.GetNonEmptyString("name");
                if (!names.Add(name)) throw f.Required("name").Fail("field '" + name + "' is defined twice");
                var codes = StandardLoader.ReadCodes(f, codeLists);
                Regex? pattern = null;
                string? patternText = null;
                var pc = f.Optional("pattern");
                if (pc != null) { pattern = NamingUtil.Compile(pc); patternText = pc.AsString(); }
                int min = 1, max = int.MaxValue;
                var len = f.Optional("length");
                if (len != null) { min = max = len.AsInt32(); }
                min = f.GetInt32("minLength", min);
                max = f.GetInt32("maxLength", max);
                if (min < 1 || max < min) throw f.Fail("length limits must satisfy 1 <= minLength <= maxLength");
                fields.Add(new RuleField(name, f.GetString("separator", ""), codes, pattern, patternText, min, max, f.GetBool("optional", false), f.GetStringOrNull("description")));
            }
            if (fields.Count == 0) throw c.Required("fields").Fail("a naming rule needs at least one field");
            var ext = c.GetStringList("extensions");
            foreach (var e in ext) if (!e.StartsWith(".", StringComparison.Ordinal)) throw c.Required("extensions").Fail("extension '" + e + "' must start with '.'");
            return new NamingRule(fields, NamingUtil.ParseCase(c.Optional("case")), ext, c.GetStringOrNull("description"), c.GetStringList("examples"));
        }

        public NameResult Parse(string name)
        {
            var r = new NameResult(name ?? "");
            if (name is null || name.Length == 0) return r.Problem("", NameProblemCodes.Empty, "name is empty");
            string s = name;
            if (Extensions.Count > 0)
            {
                string? ext = Extensions.FirstOrDefault(e => s.Length > e.Length && s.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                if (ext is null) return r.Problem("", NameProblemCodes.BadExtension, "extension must be one of " + string.Join(", ", Extensions));
                s = s.Substring(0, s.Length - ext.Length);
                r.Fields.Add(new KeyValuePair<string, string>("extension", name.Substring(s.Length)));
            }
            if (!NamingUtil.CaseOk(s, Case)) r.Problem("", NameProblemCodes.BadCase, "must be " + NamingUtil.CaseWord(Case));

            var matcher = new Matcher(this, s);
            var values = matcher.Run();
            if (values is null)
            {
                string field = matcher.FailField >= 0 && matcher.FailField < Fields.Count ? Fields[matcher.FailField].Name : "";
                string msg = matcher.FailReason.Length > 0
                    ? "at character " + (matcher.FailPos + 1) + ": " + matcher.FailReason
                    : "does not follow the rule" + (Examples.Count > 0 ? " (example: " + Examples[0] + ")" : "");
                r.Problem(field, NameProblemCodes.NoMatch, msg);
                return r;
            }
            int insertAt = 0;
            foreach (var kv in values) r.Fields.Insert(insertAt++, kv);
            return r;
        }

        /// <summary>Builds a name from field values (absent optional fields are skipped) and checks it.</summary>
        public NameResult Format(IReadOnlyDictionary<string, string> values, string? extension = null)
        {
            var sb = new System.Text.StringBuilder();
            bool any = false;
            foreach (var f in Fields)
            {
                if (!values.TryGetValue(f.Name, out var v) || string.IsNullOrEmpty(v))
                {
                    if (f.Optional) continue;
                    var bad = new NameResult(sb.ToString());
                    return bad.Problem(f.Name, NameProblemCodes.MissingField, "required field '" + f.Name + "' has no value");
                }
                if (any) sb.Append(f.Separator);
                sb.Append(v);
                any = true;
            }
            if (extension != null) sb.Append(extension);
            return Parse(sb.ToString());
        }

        private sealed class Matcher
        {
            private readonly NamingRule _rule;
            private readonly string _s;
            private readonly HashSet<long> _dead = new HashSet<long>();
            private readonly List<KeyValuePair<string, string>> _stack = new List<KeyValuePair<string, string>>();
            public int FailPos = -1;
            public int FailField = -1;
            public string FailReason = "";

            public Matcher(NamingRule rule, string s) { _rule = rule; _s = s; }

            public List<KeyValuePair<string, string>>? Run() => Match(0, 0, false) ? new List<KeyValuePair<string, string>>(_stack) : null;

            private void Note(int pos, int field, string reason)
            {
                if (pos > FailPos || (pos == FailPos && FailReason.Length == 0))
                {
                    FailPos = pos; FailField = field; FailReason = reason;
                }
            }

            private bool Match(int i, int pos, bool anyPresent)
            {
                if (i == _rule.Fields.Count)
                {
                    if (pos == _s.Length) return true;
                    Note(pos, i - 1, "unexpected '" + _s.Substring(pos) + "' after the last field");
                    return false;
                }
                long key = ((long)i * (_s.Length + 1) + pos) * 2 + (anyPresent ? 1 : 0);
                if (_dead.Contains(key)) return false;

                var f = _rule.Fields[i];
                int p = pos;
                bool sepOk = true;
                if (anyPresent && f.Separator.Length > 0)
                {
                    if (string.CompareOrdinal(_s, p, f.Separator, 0, f.Separator.Length) == 0) p += f.Separator.Length;
                    else
                    {
                        sepOk = false;
                        if (!f.Optional) Note(p, i, p >= _s.Length ? "missing field '" + f.Name + "'" : "expected '" + f.Separator + "' before field '" + f.Name + "'");
                    }
                }
                if (sepOk)
                {
                    int maxEnd = f.MaxLength == int.MaxValue ? _s.Length : Math.Min(_s.Length, p + f.MaxLength);
                    // Explain with the candidate that ends at the next separator, else the shortest candidate.
                    string sepWhy = "", lastWhy = "";
                    for (int end = maxEnd; end >= p + f.MinLength; end--)
                    {
                        string v = _s.Substring(p, end - p);
                        if (!f.Accepts(v, out var why))
                        {
                            if (sepWhy.Length == 0 && NextSeparatorAt(i, end)) sepWhy = why;
                            lastWhy = why;
                            continue;
                        }
                        _stack.Add(new KeyValuePair<string, string>(f.Name, v));
                        if (Match(i + 1, end, true)) return true;
                        _stack.RemoveAt(_stack.Count - 1);
                    }
                    if (p >= _s.Length && !f.Optional) Note(p, i, "missing field '" + f.Name + "'");
                    else if (sepWhy.Length > 0 || lastWhy.Length > 0) Note(p, i, "field '" + f.Name + "': " + (sepWhy.Length > 0 ? sepWhy : lastWhy));
                }
                if (f.Optional && Match(i + 1, pos, anyPresent)) return true;
                _dead.Add(key);
                return false;
            }

            private bool NextSeparatorAt(int i, int end)
            {
                if (i + 1 >= _rule.Fields.Count) return end == _s.Length;
                string sep = _rule.Fields[i + 1].Separator;
                return sep.Length > 0 && string.CompareOrdinal(_s, end, sep, 0, sep.Length) == 0;
            }
        }
    }
}
