using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;

namespace SA.Core.Standards
{
    /// <summary>Parses and validates layer names against a project's layer naming scheme.</summary>
    public interface ILayerNameScheme
    {
        /// <summary>"us" or "iso13567".</summary>
        string Kind { get; }
        NameResult Parse(string layerName);
    }

    /// <summary>
    /// US-style delimited layer names: discipline designator (one discipline character plus an optional modifier
    /// character), major group, up to N minor groups, optional one-character status, e.g. "A-WALL-FULL-N", "AD-DOOR".
    /// Every code list comes from the standards file.
    /// </summary>
    public sealed class UsLayerScheme : ILayerNameScheme
    {
        public string Kind => "us";
        public string Separator { get; private set; } = "-";
        public NameCase Case { get; private set; } = NameCase.Upper;
        public IReadOnlyList<CodeEntry> Disciplines { get; private set; } = new List<CodeEntry>();
        public IReadOnlyList<CodeEntry> Modifiers { get; private set; } = new List<CodeEntry>();
        public IReadOnlyList<CodeEntry> MajorGroups { get; private set; } = new List<CodeEntry>();
        public int MajorLength { get; private set; } = 4;
        public IReadOnlyList<CodeEntry> MinorGroups { get; private set; } = new List<CodeEntry>();
        public int MinorLength { get; private set; } = 4;
        public int MaxMinorGroups { get; private set; } = 2;
        /// <summary>When true a minor group not in the list is accepted if it has the right length and is alphanumeric.</summary>
        public bool AllowOtherMinor { get; private set; }
        public IReadOnlyList<CodeEntry> Status { get; private set; } = new List<CodeEntry>();

        internal static UsLayerScheme FromJson(JsonCursor c, IReadOnlyDictionary<string, List<CodeEntry>> lists)
        {
            c.RejectUnknownKeys("scheme", "description", "separator", "case", "discipline", "majorGroup", "minorGroup", "status", "examples");
            var s = new UsLayerScheme
            {
                Separator = c.GetString("separator", "-"),
                Case = c.Has("case") ? NamingUtil.ParseCase(c.Optional("case")) : NameCase.Upper,
            };
            if (s.Separator.Length != 1) throw c.Required("separator").Fail("separator must be one character");

            var d = c.Required("discipline");
            d.RejectUnknownKeys("codes", "codeList", "modifiers", "description");
            s.Disciplines = StandardLoader.ReadCodes(d, lists) ?? throw d.Fail("discipline needs 'codes' or 'codeList'");
            if (s.Disciplines.Any(x => x.Code.Length != 1)) throw d.Fail("discipline codes must be one character");
            var mods = d.Optional("modifiers");
            if (mods != null)
            {
                mods.RejectUnknownKeys("codes", "codeList", "description");
                s.Modifiers = StandardLoader.ReadCodes(mods, lists) ?? new List<CodeEntry>();
                if (s.Modifiers.Any(x => x.Code.Length != 1)) throw mods.Fail("modifier codes must be one character");
            }

            var mj = c.Required("majorGroup");
            mj.RejectUnknownKeys("codes", "codeList", "length", "description");
            s.MajorLength = mj.GetInt32("length", 4);
            s.MajorGroups = StandardLoader.ReadCodes(mj, lists) ?? throw mj.Fail("majorGroup needs 'codes' or 'codeList'");

            var mn = c.Optional("minorGroup");
            if (mn != null)
            {
                mn.RejectUnknownKeys("codes", "codeList", "length", "maxCount", "allowOther", "description");
                s.MinorLength = mn.GetInt32("length", 4);
                s.MaxMinorGroups = mn.GetInt32("maxCount", 2);
                s.AllowOtherMinor = mn.GetBool("allowOther", false);
                s.MinorGroups = StandardLoader.ReadCodes(mn, lists) ?? new List<CodeEntry>();
            }
            else s.MaxMinorGroups = 0;

            var st = c.Optional("status");
            if (st != null)
            {
                st.RejectUnknownKeys("codes", "codeList", "description");
                s.Status = StandardLoader.ReadCodes(st, lists) ?? new List<CodeEntry>();
                if (s.Status.Any(x => x.Code.Length != 1)) throw st.Fail("status codes must be one character");
            }
            if (s.MinorLength == 1) throw c.Fail("minor groups of one character cannot be told apart from a status code");
            return s;
        }

        public NameResult Parse(string layerName)
        {
            var r = new NameResult(layerName ?? "");
            if (layerName is null || layerName.Length == 0) return r.Problem("", NameProblemCodes.Empty, "layer name is empty");
            if (!NamingUtil.CaseOk(layerName, Case)) r.Problem("", NameProblemCodes.BadCase, "layer names must be " + NamingUtil.CaseWord(Case));
            var tokens = layerName.Split(new[] { Separator[0] });
            if (tokens.Any(t => t.Length == 0)) return r.Problem("", NameProblemCodes.BadCharacters, "empty field (doubled or trailing '" + Separator + "')");
            if (tokens.Length < 2) return r.Problem("major", NameProblemCodes.MissingField, "a major group is required, e.g. A" + Separator + "WALL");

            string des = tokens[0];
            if (des.Length > 2) r.Problem("discipline", NameProblemCodes.BadLength, "discipline designator '" + des + "' must be 1 or 2 characters");
            else
            {
                string disc = des.Substring(0, 1);
                if (!Disciplines.Any(x => x.Code == disc)) r.Problem("discipline", NameProblemCodes.UnknownCode, "'" + disc + "' is not a discipline (allowed: " + NamingUtil.Allowed(Disciplines) + ")");
                r.Fields.Add(new KeyValuePair<string, string>("discipline", disc));
                if (des.Length == 2)
                {
                    string mod = des.Substring(1, 1);
                    if (!Modifiers.Any(x => x.Code == mod))
                        r.Problem("modifier", NameProblemCodes.UnknownCode, Modifiers.Count == 0
                            ? "this standard has no discipline modifiers"
                            : "'" + mod + "' is not a discipline modifier (allowed: " + NamingUtil.Allowed(Modifiers) + ")");
                    r.Fields.Add(new KeyValuePair<string, string>("modifier", mod));
                }
            }

            string major = tokens[1];
            if (major.Length != MajorLength) r.Problem("major", NameProblemCodes.BadLength, "major group '" + major + "' must be " + MajorLength + " characters");
            else if (!MajorGroups.Any(x => x.Code == major)) r.Problem("major", NameProblemCodes.UnknownCode, "'" + major + "' is not a major group in this standard");
            r.Fields.Add(new KeyValuePair<string, string>("major", major));

            int end = tokens.Length;
            string? status = null;
            if (end > 2 && tokens[end - 1].Length == 1)
            {
                status = tokens[end - 1];
                end--;
                if (!Status.Any(x => x.Code == status))
                    r.Problem("status", NameProblemCodes.UnknownCode, Status.Count == 0 ? "this standard has no status codes" : "'" + status + "' is not a status code (allowed: " + NamingUtil.Allowed(Status) + ")");
            }

            int minorCount = end - 2;
            if (minorCount > MaxMinorGroups)
                r.Problem("minor", NameProblemCodes.TooManyFields, MaxMinorGroups == 0 ? "this standard allows no minor groups" : "at most " + MaxMinorGroups + " minor groups are allowed");
            for (int k = 2; k < end; k++)
            {
                string minor = tokens[k];
                string fieldName = "minor" + (k - 1);
                if (minor.Length != MinorLength) r.Problem(fieldName, NameProblemCodes.BadLength, "minor group '" + minor + "' must be " + MinorLength + " characters");
                else if (!MinorGroups.Any(x => x.Code == minor))
                {
                    if (!AllowOtherMinor) r.Problem(fieldName, NameProblemCodes.UnknownCode, "'" + minor + "' is not a minor group in this standard");
                    else if (!NamingUtil.IsAlnum(minor)) r.Problem(fieldName, NameProblemCodes.BadCharacters, "'" + minor + "' must be letters and digits");
                }
                r.Fields.Add(new KeyValuePair<string, string>(fieldName, minor));
            }
            if (status != null) r.Fields.Add(new KeyValuePair<string, string>("status", status));
            return r;
        }
    }

    /// <summary>
    /// ISO 13567-style fixed-position layer names: each field has a fixed length and position; mandatory fields come
    /// first, unused optional fields are written with the filler character, and the name may stop after any field.
    /// Characters after the last fixed field form the user-defined field.
    /// </summary>
    public sealed class IsoLayerScheme : ILayerNameScheme
    {
        public enum CodeMatch { Exact, Prefix }

        public sealed class IsoField
        {
            internal IsoField(string name, int length, bool required, IReadOnlyList<CodeEntry>? codes, CodeMatch match, string? description)
            {
                Name = name; Length = length; Required = required; Codes = codes; Match = match; Description = description;
            }

            public string Name { get; }
            public int Length { get; }
            public bool Required { get; }
            public IReadOnlyList<CodeEntry>? Codes { get; }
            /// <summary>Exact: the value without trailing filler is a code. Prefix: the value starts with a code (classification codes refined to the right).</summary>
            public CodeMatch Match { get; }
            public string? Description { get; }
        }

        public string Kind => "iso13567";
        public char Filler { get; private set; } = '_';
        public NameCase Case { get; private set; } = NameCase.Upper;
        public IReadOnlyList<IsoField> Fields { get; private set; } = new List<IsoField>();
        public bool UserDefinedAllowed { get; private set; } = true;
        public int UserDefinedMaxLength { get; private set; } = int.MaxValue;

        internal static IsoLayerScheme FromJson(JsonCursor c, IReadOnlyDictionary<string, List<CodeEntry>> lists)
        {
            c.RejectUnknownKeys("scheme", "description", "filler", "case", "fields", "userDefined", "examples");
            var s = new IsoLayerScheme { Case = c.Has("case") ? NamingUtil.ParseCase(c.Optional("case")) : NameCase.Upper };
            string filler = c.GetString("filler", "_");
            if (filler.Length != 1) throw c.Required("filler").Fail("filler must be one character");
            s.Filler = filler[0];
            var fields = new List<IsoField>();
            bool optionalSeen = false;
            foreach (var f in c.Required("fields").Items())
            {
                f.RejectUnknownKeys("name", "length", "required", "codes", "codeList", "match", "description");
                int len = f.GetInt32("length");
                if (len < 1) throw f.Required("length").Fail("length must be at least 1");
                bool req = f.GetBool("required", false);
                if (req && optionalSeen) throw f.Required("required").Fail("mandatory fields must come before optional ones");
                if (!req) optionalSeen = true;
                string m = f.GetString("match", "exact");
                if (m != "exact" && m != "prefix") throw f.Required("match").Fail("expected 'exact' or 'prefix'");
                var codes = StandardLoader.ReadCodes(f, lists);
                if (codes != null && codes.Any(x => x.Code.Length > len)) throw f.Fail("a code is longer than the field");
                fields.Add(new IsoField(f.GetNonEmptyString("name"), len, req, codes, m == "prefix" ? CodeMatch.Prefix : CodeMatch.Exact, f.GetStringOrNull("description")));
            }
            if (fields.Count == 0) throw c.Required("fields").Fail("at least one field is required");
            s.Fields = fields;
            var u = c.Optional("userDefined");
            if (u != null)
            {
                u.RejectUnknownKeys("allowed", "maxLength", "description");
                s.UserDefinedAllowed = u.GetBool("allowed", true);
                s.UserDefinedMaxLength = u.GetInt32("maxLength", int.MaxValue);
            }
            return s;
        }

        public NameResult Parse(string layerName)
        {
            var r = new NameResult(layerName ?? "");
            if (layerName is null || layerName.Length == 0) return r.Problem("", NameProblemCodes.Empty, "layer name is empty");
            if (!NamingUtil.CaseOk(layerName, Case)) r.Problem("", NameProblemCodes.BadCase, "layer names must be " + NamingUtil.CaseWord(Case));
            int pos = 0;
            foreach (var f in Fields)
            {
                if (pos >= layerName.Length)
                {
                    if (f.Required) r.Problem(f.Name, NameProblemCodes.MissingField, "mandatory field '" + f.Name + "' (" + f.Length + " characters) is missing");
                    continue;
                }
                if (pos + f.Length > layerName.Length)
                {
                    r.Problem(f.Name, NameProblemCodes.BadLength, "field '" + f.Name + "' needs " + f.Length + " characters, found " + (layerName.Length - pos) +
                        "; pad with '" + Filler + "'");
                    return r;
                }
                string v = layerName.Substring(pos, f.Length);
                pos += f.Length;
                if (v.All(ch => ch == Filler))
                {
                    if (f.Required) r.Problem(f.Name, NameProblemCodes.MissingField, "mandatory field '" + f.Name + "' is empty");
                    continue;
                }
                if (!v.All(ch => char.IsLetterOrDigit(ch) || ch == Filler))
                {
                    r.Problem(f.Name, NameProblemCodes.BadCharacters, "'" + v + "' may contain only letters, digits and '" + Filler + "'");
                }
                else if (f.Codes != null)
                {
                    string trimmed = v.TrimEnd(Filler);
                    bool ok = f.Match == CodeMatch.Exact
                        ? f.Codes.Any(x => x.Code == trimmed)
                        : f.Codes.Any(x => v.StartsWith(x.Code, StringComparison.Ordinal));
                    if (!ok) r.Problem(f.Name, NameProblemCodes.UnknownCode, "'" + trimmed + "' is not an allowed " + f.Name + " code (allowed: " + NamingUtil.Allowed(f.Codes) + ")");
                }
                r.Fields.Add(new KeyValuePair<string, string>(f.Name, v));
            }
            if (pos < layerName.Length)
            {
                string user = layerName.Substring(pos);
                if (!UserDefinedAllowed) r.Problem("userDefined", NameProblemCodes.TooManyFields, "'" + user + "' follows the last field and this standard allows no user-defined field");
                else if (user.Length > UserDefinedMaxLength) r.Problem("userDefined", NameProblemCodes.BadLength, "user-defined field is limited to " + UserDefinedMaxLength + " characters");
                r.Fields.Add(new KeyValuePair<string, string>("userDefined", user));
            }
            return r;
        }
    }
}
