using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Json;

namespace SA.Core.Checks
{
    public enum Severity { Info, Warning, Error, Critical }

    public static class Severities
    {
        public static string ToWire(Severity s) =>
            s == Severity.Info ? "info" : s == Severity.Warning ? "warning" : s == Severity.Error ? "error" : "critical";

        public static bool TryParse(string? s, out Severity severity)
        {
            switch (s)
            {
                case "info": severity = Severity.Info; return true;
                case "warning": severity = Severity.Warning; return true;
                case "error": severity = Severity.Error; return true;
                case "critical": severity = Severity.Critical; return true;
                default: severity = Severity.Info; return false;
            }
        }
    }

    public enum CheckApp { AutoCad, Revit, Both }

    /// <summary>
    /// A suggested fix: either an op with an args template, or manual instructions. In an args template a string
    /// "{{name}}" is replaced by the finding's data value "name" (keeping its JSON type); "{{name}}" inside a longer
    /// string is replaced by the value's text. "{{recordId}}" is the finding's record id.
    /// </summary>
    public sealed class CheckFix
    {
        private CheckFix(string? op, JsonObject? args, string? instructions)
        {
            Op = op; ArgsTemplate = args; Instructions = instructions;
        }

        public string? Op { get; }
        public JsonObject? ArgsTemplate { get; }
        public string? Instructions { get; }
        public bool IsManual => Op is null;

        public static CheckFix ForOp(string op, JsonObject? args, string? note = null) => new CheckFix(op, args ?? new JsonObject(), note);
        public static CheckFix Manual(string instructions) => new CheckFix(null, null, instructions);

        private static readonly Regex Placeholder = new Regex(@"\{\{([A-Za-z0-9_.-]+)\}\}", RegexOptions.CultureInvariant);

        /// <summary>Fills the args template from a finding. Returns null for a manual fix.</summary>
        public JsonObject? Instantiate(Finding finding)
        {
            if (ArgsTemplate is null) return null;
            return (JsonObject)Fill(ArgsTemplate, finding);
        }

        private static JsonValue Fill(JsonValue v, Finding f)
        {
            switch (v)
            {
                case JsonString s:
                {
                    var whole = Placeholder.Match(s.Value);
                    if (whole.Success && whole.Length == s.Value.Length)
                        return Lookup(whole.Groups[1].Value, f)?.DeepClone() ?? JsonValue.Null;
                    return new JsonString(Placeholder.Replace(s.Value, m =>
                    {
                        var x = Lookup(m.Groups[1].Value, f);
                        return x is null ? "" : x is JsonString xs ? xs.Value : x.ToJson();
                    }));
                }
                case JsonArray a:
                {
                    var c = new JsonArray();
                    foreach (var i in a) c.Add(Fill(i, f));
                    return c;
                }
                case JsonObject o:
                {
                    var c = new JsonObject();
                    foreach (var kv in o) c.Add(kv.Key, Fill(kv.Value, f));
                    return c;
                }
                default: return v;
            }
        }

        private static JsonValue? Lookup(string name, Finding f)
        {
            if (name == "recordId") return f.RecordId is null ? null : new JsonString(f.RecordId);
            return f.Data[name];
        }

        public JsonObject ToJson()
        {
            if (IsManual) return new JsonObject().Add("kind", "manual").Add("instructions", Instructions ?? "");
            var o = new JsonObject().Add("kind", "op").Add("op", Op!).Add("args", ArgsTemplate!);
            if (Instructions != null) o.Add("note", Instructions);
            return o;
        }
    }

    /// <summary>One check loaded from a checks file.</summary>
    public sealed class CheckDefinition
    {
        public CheckDefinition(string id, CheckApp app, string title, Severity severity, string evaluator, JsonObject? parameters = null,
            string? why = null, CheckFix? fix = null, IEnumerable<string>? groups = null, string? source = null)
        {
            Id = id; App = app; Title = title; Severity = severity; Evaluator = evaluator;
            Params = parameters ?? new JsonObject(); Why = why; Fix = fix;
            Groups = groups?.ToList() ?? new List<string>(); Source = source;
        }

        public string Id { get; }
        public CheckApp App { get; }
        public string Title { get; }
        public Severity Severity { get; }
        /// <summary>Name of the evaluator in the <see cref="EvaluatorRegistry"/>.</summary>
        public string Evaluator { get; }
        public JsonObject Params { get; }
        public string? Why { get; }
        public CheckFix? Fix { get; }
        public IReadOnlyList<string> Groups { get; }
        public string? Source { get; }

        public bool AppliesTo(CheckApp app) => App == CheckApp.Both || App == app;
    }

    public sealed class CheckFormatException : FormatException
    {
        public CheckFormatException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>The checks of one or more checks files.</summary>
    public sealed class CheckCatalog
    {
        public const string IdPattern = "^[a-z0-9][a-z0-9_-]*(\\.[a-z0-9][a-z0-9_-]*)+$";
        private static readonly Regex IdRegex = new Regex(IdPattern, RegexOptions.CultureInvariant);

        private readonly List<CheckDefinition> _checks = new List<CheckDefinition>();

        public IReadOnlyList<CheckDefinition> Checks => _checks;

        public CheckDefinition? Find(string id) => _checks.FirstOrDefault(c => c.Id == id);

        public void Add(CheckDefinition check)
        {
            if (Find(check.Id) != null) throw new CheckFormatException("Check id '" + check.Id + "' is defined twice.");
            _checks.Add(check);
        }

        public static CheckCatalog Parse(string text)
        {
            JsonValue json;
            try { json = JsonValue.Parse(text); }
            catch (JsonParseException e) { throw new CheckFormatException(e.Message, e); }
            var cat = new CheckCatalog();
            cat.AddFrom(json);
            return cat;
        }

        public static CheckCatalog LoadFile(string path) => Parse(File.ReadAllText(path));

        /// <summary>Loads several checks files (or folders: every *.json inside, in name order) into one catalogue. A check id defined twice is an error.</summary>
        public static CheckCatalog LoadFiles(IEnumerable<string> paths)
        {
            var cat = new CheckCatalog();
            foreach (var p in paths)
            {
                var files = Directory.Exists(p) ? Directory.GetFiles(p, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToArray() : new[] { p };
                foreach (var f in files)
                {
                    var error = cat.TryAddFile(f);
                    if (error != null) throw new CheckFormatException(error);
                }
            }
            return cat;
        }

        /// <summary>
        /// Adds every check of one file, all or nothing. Returns null on success, else a message naming the file and the
        /// JSON path (or the id defined twice).
        /// </summary>
        public string? TryAddFile(string path)
        {
            CheckCatalog one;
            try { one = LoadFile(path); }
            catch (CheckFormatException e) { return path + ": " + e.Message; }
            catch (IOException e) { return path + ": " + e.Message; }
            catch (UnauthorizedAccessException e) { return path + ": " + e.Message; }
            var clash = one.Checks.FirstOrDefault(c => Find(c.Id) != null);
            if (clash != null) return path + ": check id '" + clash.Id + "' is already defined by an earlier checks file.";
            foreach (var c in one.Checks) _checks.Add(c);
            return null;
        }

        /// <summary>Adds every check from a parsed checks file. Messages name the JSON path of the problem.</summary>
        public void AddFrom(JsonValue json)
        {
            try
            {
                var root = new JsonCursor(json);
                root.RejectUnknownKeys("$schema", "version", "description", "checks");
                int version = root.GetInt32("version");
                if (version != 1) throw root.Required("version").Fail("unsupported checks file version " + version);
                foreach (var c in root.Required("checks").Items()) Add(ParseCheck(c));
            }
            catch (JsonAccessException e)
            {
                throw new CheckFormatException(e.Message, e);
            }
        }

        private CheckDefinition ParseCheck(JsonCursor c)
        {
            c.RejectUnknownKeys("id", "app", "title", "severity", "why", "evaluator", "params", "fix", "groups", "source");
            string id = c.GetNonEmptyString("id");
            if (!IdRegex.IsMatch(id)) throw c.Required("id").Fail("check id '" + id + "' must be lower-case dotted, e.g. 'layers.naming'");
            if (Find(id) != null) throw c.Required("id").Fail("check id '" + id + "' is defined twice");
            string appText = c.GetString("app");
            CheckApp app = appText == "autocad" ? CheckApp.AutoCad : appText == "revit" ? CheckApp.Revit : appText == "both" ? CheckApp.Both
                : throw c.Required("app").Fail("expected 'autocad', 'revit' or 'both'");
            if (!Severities.TryParse(c.GetString("severity"), out var sev)) throw c.Required("severity").Fail("expected info, warning, error or critical");
            CheckFix? fix = null;
            var f = c.Optional("fix");
            if (f != null)
            {
                string kind = f.GetString("kind");
                if (kind == "op")
                {
                    f.RejectUnknownKeys("kind", "op", "args", "note");
                    fix = CheckFix.ForOp(f.GetNonEmptyString("op"), f.Optional("args")?.AsObject(), f.GetStringOrNull("note"));
                }
                else if (kind == "manual")
                {
                    f.RejectUnknownKeys("kind", "instructions");
                    fix = CheckFix.Manual(f.GetNonEmptyString("instructions"));
                }
                else throw f.Required("kind").Fail("expected 'op' or 'manual'");
            }
            return new CheckDefinition(id, app, c.GetNonEmptyString("title"), sev, c.GetNonEmptyString("evaluator"),
                c.Optional("params")?.AsObject(), c.GetStringOrNull("why"), fix, c.GetStringList("groups"), c.GetStringOrNull("source"));
        }

        /// <summary>
        /// Checks for <paramref name="app"/> chosen by id and/or group (both empty means all). Ids that do not exist,
        /// or exist only for the other app, are returned in <paramref name="unknownIds"/>.
        /// </summary>
        public List<CheckDefinition> Select(CheckApp app, IEnumerable<string>? ids, IEnumerable<string>? groups, out List<string> unknownIds)
        {
            var idList = ids?.ToList() ?? new List<string>();
            var groupList = groups?.ToList() ?? new List<string>();
            unknownIds = new List<string>();
            var chosen = new List<CheckDefinition>();
            foreach (var id in idList)
            {
                var c = Find(id);
                if (c is null || !c.AppliesTo(app)) unknownIds.Add(id);
                else if (!chosen.Contains(c)) chosen.Add(c);
            }
            foreach (var c in _checks)
            {
                if (!c.AppliesTo(app) || chosen.Contains(c)) continue;
                bool all = idList.Count == 0 && groupList.Count == 0;
                if (all || c.Groups.Any(g => groupList.Contains(g))) chosen.Add(c);
            }
            return chosen;
        }
    }
}
