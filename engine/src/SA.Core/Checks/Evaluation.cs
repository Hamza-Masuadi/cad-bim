using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;
using SA.Core.Standards;

namespace SA.Core.Checks
{
    public sealed class Finding
    {
        public Finding(string message, string? recordId = null, string? table = null, JsonObject? data = null, Severity? severity = null)
        {
            Message = message;
            RecordId = recordId;
            Table = table;
            Data = data ?? new JsonObject();
            Severity = severity;
        }

        public string Message { get; }
        /// <summary>The element (handle or id) the finding is about, when there is one.</summary>
        public string? RecordId { get; }
        public string? Table { get; }
        /// <summary>Values for the fix template and for the reader (the offending value, the expected value...).</summary>
        public JsonObject Data { get; }
        /// <summary>Overrides the check's severity for this finding when set.</summary>
        public Severity? Severity { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("message", Message);
            if (RecordId != null) o.Add("recordId", RecordId);
            if (Table != null) o.Add("table", Table);
            if (Severity.HasValue) o.Add("severity", Severities.ToWire(Severity.Value));
            if (Data.Count > 0) o.Add("data", Data);
            return o;
        }

        public static Finding Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            Severity? sev = null;
            var s = c.GetStringOrNull("severity");
            if (s != null) sev = Severities.TryParse(s, out var x) ? x : throw c.Required("severity").Fail("unknown severity");
            return new Finding(c.GetString("message"), c.GetStringOrNull("recordId"), c.GetStringOrNull("table"), c.Optional("data")?.AsObject(), sev);
        }
    }

    /// <summary>
    /// Outcome of a check. <see cref="Review"/> is only produced when <see cref="CheckRunner.ReviewStatus"/> is on: by
    /// default a review-by-eye check is <see cref="Fail"/> with <see cref="CheckResult.IsManualReview"/> set.
    /// <see cref="Incomplete"/>: no finding, but the facts the check read were cut or partly omitted (see
    /// <see cref="Facts.IncompleteParts"/>), so a pass cannot be claimed.
    /// </summary>
    public enum CheckStatus { Pass, Fail, NotImplemented, Error, Review, Incomplete }

    public sealed class CheckResult
    {
        public CheckResult(string id, CheckStatus status, string? message = null)
        {
            Id = id; Status = status; Message = message;
        }

        public string Id { get; }
        public CheckStatus Status { get; }
        public string? Message { get; }
        public string? Title { get; set; }
        public Severity? Severity { get; set; }
        public List<Finding> Findings { get; } = new List<Finding>();
        /// <summary>Fix suggestions instantiated per finding (op fixes), or one manual instruction.</summary>
        public JsonArray Fixes { get; } = new JsonArray();

        public static string StatusToWire(CheckStatus s) =>
            s == CheckStatus.Pass ? "pass" : s == CheckStatus.Fail ? "fail" : s == CheckStatus.NotImplemented ? "not_implemented" :
            s == CheckStatus.Review ? "review" : s == CheckStatus.Incomplete ? "incomplete" : "error";

        /// <summary>
        /// True for a review-by-eye item (evaluator manual_review, or every finding marked review = true). Report writers list
        /// these separately and leave them out of pass and fail counts (see <see cref="CheckSummary"/>). On the wire: "review": true.
        /// </summary>
        public bool IsManualReview { get; set; }

        /// <summary>For a review item: what to look at (the check's instructions), when known.</summary>
        public string? ReviewInstructions { get; set; }

        /// <summary>
        /// True when the facts this check read were cut or partly omitted. The status is then <see cref="CheckStatus.Incomplete"/>
        /// when there was no finding, or stays <see cref="CheckStatus.Fail"/> (its findings are real, but there may be more).
        /// On the wire: "incomplete": true and "incompleteReason".
        /// </summary>
        public bool Incomplete { get; set; }

        /// <summary>What was missing, e.g. "facts table 'entities' was cut (records: 100000 of 250000)".</summary>
        public string? IncompleteReason { get; set; }

        public static CheckStatus ParseStatus(string s)
        {
            switch (s)
            {
                case "pass": return CheckStatus.Pass;
                case "fail": return CheckStatus.Fail;
                case "not_implemented": return CheckStatus.NotImplemented;
                case "error": return CheckStatus.Error;
                case "review": return CheckStatus.Review;
                case "incomplete": return CheckStatus.Incomplete;
                default: throw new FormatException("Unknown check status '" + s + "'.");
            }
        }

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("id", Id).Add("status", StatusToWire(Status));
            if (Title != null) o.Add("title", Title);
            if (Severity.HasValue) o.Add("severity", Severities.ToWire(Severity.Value));
            if (Message != null) o.Add("message", Message);
            if (IsManualReview)
            {
                o.Add("review", true);
                if (ReviewInstructions != null) o.Add("reviewInstructions", ReviewInstructions);
            }
            if (Incomplete)
            {
                o.Add("incomplete", true);
                if (IncompleteReason != null) o.Add("incompleteReason", IncompleteReason);
            }
            o.Add("findings", new JsonArray(Findings.Select(f => (JsonValue)f.ToJson())));
            if (Fixes.Count > 0) o.Add("fixes", Fixes);
            return o;
        }

        public static CheckResult Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var r = new CheckResult(c.GetNonEmptyString("id"), ParseStatus(c.GetString("status")), c.GetStringOrNull("message"))
            {
                Title = c.GetStringOrNull("title"),
                ReviewInstructions = c.GetStringOrNull("reviewInstructions"),
            };
            r.IsManualReview = (c.Optional("review")?.Value is JsonBool rv && rv.Value) || r.Status == CheckStatus.Review;
            r.Incomplete = (c.Optional("incomplete")?.Value is JsonBool inc && inc.Value) || r.Status == CheckStatus.Incomplete;
            r.IncompleteReason = c.GetStringOrNull("incompleteReason");
            var s = c.GetStringOrNull("severity");
            if (s != null && Severities.TryParse(s, out var sev)) r.Severity = sev;
            var f = c.Optional("findings");
            if (f != null) foreach (var i in f.Items()) r.Findings.Add(Finding.Parse(i.Value));
            var fx = c.Optional("fixes");
            if (fx != null) foreach (var i in fx.AsArray()) r.Fixes.Add(i);
            return r;
        }
    }

    /// <summary>Thrown by an evaluator when the facts it needs were not collected. The check reports not_implemented.</summary>
    public sealed class FactsMissingException : Exception
    {
        public FactsMissingException(string message) : base(message) { }
    }

    /// <summary>Thrown by an evaluator when the check's params are wrong. The check reports error.</summary>
    public sealed class CheckParamException : Exception
    {
        public CheckParamException(string message) : base(message) { }
    }

    public sealed class CheckContext
    {
        public CheckContext(CheckDefinition check, Facts facts, ProjectStandard? standard)
        {
            Check = check; Facts = facts; Standard = standard;
        }

        public CheckDefinition Check { get; }
        public Facts Facts { get; }
        public ProjectStandard? Standard { get; }
        public JsonObject Params => Check.Params;

        public FactTable RequireTable(string name)
        {
            if (!Facts.TryGetTable(name, out var t)) throw new FactsMissingException("Facts table '" + name + "' was not collected by this engine.");
            return t;
        }

        public ProjectStandard RequireStandard()
        {
            return Standard ?? throw new CheckParamException("Check '" + Check.Id + "' needs a project standard and none is loaded.");
        }

        public string ParamString(string name)
        {
            if (Params[name] is JsonString s && s.Value.Length > 0) return s.Value;
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be a non-empty string.");
        }

        public string? ParamStringOrNull(string name)
        {
            var v = Params[name];
            if (v is null || v.IsNull) return null;
            if (v is JsonString s) return s.Value;
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be a string.");
        }

        public double ParamNumber(string name)
        {
            if (Params[name] is JsonNumber n) return n.Value;
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be a number.");
        }

        public double? ParamNumberOrNull(string name)
        {
            var v = Params[name];
            if (v is null || v.IsNull) return null;
            if (v is JsonNumber n) return n.Value;
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be a number.");
        }

        public bool ParamBool(string name, bool fallback)
        {
            var v = Params[name];
            if (v is null || v.IsNull) return fallback;
            if (v is JsonBool b) return b.Value;
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be true or false.");
        }

        public List<string> ParamStringList(string name)
        {
            var v = Params[name];
            var list = new List<string>();
            if (v is null || v.IsNull) return list;
            if (v is JsonString one) { list.Add(one.Value); return list; }
            if (v is JsonArray a && a.All(x => x is JsonString))
            {
                list.AddRange(a.Select(x => ((JsonString)x).Value));
                return list;
            }
            throw new CheckParamException("Check '" + Check.Id + "': param '" + name + "' must be a string or a list of strings.");
        }
    }

    /// <summary>Evaluates one kind of check. Returns findings; an empty list is a pass.</summary>
    public interface ICheckEvaluator
    {
        string Name { get; }
        IEnumerable<Finding> Evaluate(CheckContext context);
    }

    /// <summary>Evaluators keyed by name. Start from <see cref="CreateNeutral"/> and add engine-specific evaluators.</summary>
    public sealed class EvaluatorRegistry
    {
        private readonly Dictionary<string, ICheckEvaluator> _map = new Dictionary<string, ICheckEvaluator>(StringComparer.Ordinal);

        public void Register(ICheckEvaluator evaluator)
        {
            if (_map.ContainsKey(evaluator.Name)) throw new ArgumentException("Evaluator '" + evaluator.Name + "' is already registered.");
            _map.Add(evaluator.Name, evaluator);
        }

        public bool TryGet(string name, out ICheckEvaluator evaluator)
        {
            if (_map.TryGetValue(name, out var e)) { evaluator = e; return true; }
            evaluator = null!;
            return false;
        }

        public IEnumerable<string> Names => _map.Keys.OrderBy(k => k, StringComparer.Ordinal);

        /// <summary>
        /// A registry with every app-neutral evaluator: <see cref="NeutralEvaluators"/> and <see cref="StandardEvaluators"/>
        /// (conditional_field_equals, field_within, distinct_count_min, scale_in_standard, units_match_standard,
        /// dim_precision_min, matches_standard_entry, group_count_over_threshold, manual_review).
        /// </summary>
        public static EvaluatorRegistry CreateNeutral()
        {
            var r = new EvaluatorRegistry();
            foreach (var e in NeutralEvaluators.All()) r.Register(e);
            foreach (var e in StandardEvaluators.All()) r.Register(e);
            foreach (var e in LayoutEvaluators.All()) r.Register(e);
            return r;
        }

        /// <summary>Registers the evaluator unless one with the same name exists. Returns false when it was skipped.</summary>
        public bool TryRegister(ICheckEvaluator evaluator)
        {
            if (evaluator is null) throw new ArgumentNullException(nameof(evaluator));
            if (_map.ContainsKey(evaluator.Name)) return false;
            _map.Add(evaluator.Name, evaluator);
            return true;
        }

        public bool Contains(string name) => _map.ContainsKey(name);
    }

    /// <summary>Runs checks and turns every outcome into a status. A check never passes by accident: a missing evaluator or missing facts is not_implemented.</summary>
    public sealed class CheckRunner
    {
        public CheckRunner(EvaluatorRegistry registry)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public EvaluatorRegistry Registry { get; }

        /// <summary>Caps findings per check so a model with 50,000 offending elements still yields a usable report.</summary>
        public int MaxFindingsPerCheck { get; set; } = 500;

        /// <summary>
        /// When true, a review-by-eye check (see <see cref="CheckResult.IsManualReview"/>) gets status
        /// <see cref="CheckStatus.Review"/> ("review") instead of Fail. Off by default so existing readers see only the four
        /// original statuses; the "review" flag is written either way.
        /// </summary>
        public bool ReviewStatus { get; set; }

        public CheckResult Run(CheckDefinition check, Facts facts, ProjectStandard? standard)
        {
            CheckResult result;
            if (!Registry.TryGet(check.Evaluator, out var evaluator))
            {
                result = new CheckResult(check.Id, CheckStatus.NotImplemented, "Evaluator '" + check.Evaluator + "' is not implemented by this engine.");
            }
            else
            {
                try
                {
                    var findings = evaluator.Evaluate(new CheckContext(check, facts, standard)).ToList();
                    result = new CheckResult(check.Id, findings.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail,
                        findings.Count > MaxFindingsPerCheck ? findings.Count + " findings; the first " + MaxFindingsPerCheck + " are listed." : null);
                    foreach (var f in findings.Take(MaxFindingsPerCheck))
                    {
                        result.Findings.Add(f);
                        if (check.Fix != null && !check.Fix.IsManual)
                            result.Fixes.Add(new JsonObject().Add("op", check.Fix.Op!).Add("args", check.Fix.Instantiate(f)!));
                    }
                    if (findings.Count > 0 && check.Fix != null && check.Fix.IsManual)
                        result.Fixes.Add(new JsonObject().Add("manual", check.Fix.Instructions ?? ""));
                    bool review = findings.Count > 0 && (check.Evaluator == ManualReview.EvaluatorName ||
                        findings.All(f => f.Data["review"] is JsonBool b && b.Value));
                    if (review)
                    {
                        result.IsManualReview = true;
                        result.ReviewInstructions = findings[0].Data.GetStringOrNull("instructions") ?? check.Fix?.Instructions;
                        if (ReviewStatus)
                        {
                            var swapped = new CheckResult(check.Id, CheckStatus.Review, result.Message) { IsManualReview = true, ReviewInstructions = result.ReviewInstructions };
                            swapped.Findings.AddRange(result.Findings);
                            foreach (var fx in result.Fixes) swapped.Fixes.Add(fx);
                            result = swapped;
                        }
                    }
                    string? gap = CheckInputs.Incompleteness(check, facts);
                    if (gap != null)
                    {
                        if (result.Status == CheckStatus.Pass) result = new CheckResult(check.Id, CheckStatus.Incomplete, gap);
                        result.Incomplete = true;
                        result.IncompleteReason = gap;
                    }
                }
                catch (FactsMissingException e)
                {
                    result = new CheckResult(check.Id, CheckStatus.NotImplemented, e.Message);
                }
                catch (Exception e)
                {
                    result = new CheckResult(check.Id, CheckStatus.Error, e.GetType().Name + ": " + e.Message);
                }
            }
            result.Title = check.Title;
            result.Severity = check.Severity;
            return result;
        }

        public List<CheckResult> RunAll(IEnumerable<CheckDefinition> checks, Facts facts, ProjectStandard? standard) =>
            checks.Select(c => Run(c, facts, standard)).ToList();
    }

    /// <summary>Which facts a check reads, from its params, so the runner can tell when they were incomplete.</summary>
    public static class CheckInputs
    {
        private static readonly string[] FieldParams = { "field", "ifField", "systemField", "heightField", "scaleField", "key", "labelField", "nameField", "groupField", "typeField" };
        private static readonly string[] FieldListParams = { "groupBy", "box" };

        private static readonly Dictionary<string, string[]> Defaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["dim_precision_min"] = new[] { "stepMm", "unitSystem" },
            ["text_height_standard"] = new[] { "height", "scale" },
            ["matches_standard_entry"] = new[] { "name" },
            ["dim_override_value"] = new[] { "textOverride", "measurement", "dimLfac" },
            ["extents_overlap"] = new[] { "minX", "minY", "maxX", "maxY", ExtentsOverlap.ByJobField },
        };

        private static readonly HashSet<string> CoreEvaluators = new HashSet<string>(
            NeutralEvaluators.All().Concat(StandardEvaluators.All()).Concat(LayoutEvaluators.All()).Select(e => e.Name), StringComparer.Ordinal);

        /// <summary>The check's <c>table</c> param, or null when it names none (manual_review, some engine evaluators).</summary>
        public static string? Table(CheckDefinition check) => check.Params["table"] is JsonString s && s.Value.Length > 0 ? s.Value : null;

        /// <summary>
        /// The record fields the check reads: field-naming params, <c>keys</c>, the keys of <c>where</c>, <c>whereNot</c> and <c>properties</c>,
        /// and an evaluator's default fields. <paramref name="complete"/> is false for an evaluator SA.Core does not define,
        /// whose params may name fields in other ways.
        /// </summary>
        public static List<string> Fields(CheckDefinition check, out bool complete)
        {
            var p = check.Params;
            var list = new List<string>();
            void Add(string f) { if (f.Length > 0 && !list.Contains(f)) list.Add(f); }
            foreach (var name in FieldParams) if (p[name] is JsonString s) Add(s.Value);
            if (p["keys"] is JsonArray keys) foreach (var k in keys) if (k is JsonString ks) Add(ks.Value);
            foreach (var name in FieldListParams)
            {
                if (p[name] is JsonString one) Add(one.Value);
                if (p[name] is JsonArray fl) foreach (var f in fl) if (f is JsonString fs) Add(fs.Value);
            }
            if (p["where"] is JsonObject w) foreach (var kv in w) Add(kv.Key);
            if (p["whereNot"] is JsonObject wn) foreach (var kv in wn) Add(kv.Key);
            if (p["properties"] is JsonObject props) foreach (var kv in props) Add(kv.Key);
            if (Defaults.TryGetValue(check.Evaluator, out var defaults)) foreach (var d in defaults) Add(d);
            complete = CoreEvaluators.Contains(check.Evaluator);
            return list;
        }

        /// <summary>
        /// Null when the facts the check reads are complete as far as the engine reported; otherwise why they are not. A
        /// cut table counts for every check on it; a cut or omitted field counts for checks that read it (for an evaluator
        /// SA.Core does not define, any cut field of its table counts).
        /// </summary>
        public static string? Incompleteness(CheckDefinition check, Facts facts)
        {
            string? table = Table(check);
            if (table is null) return null;
            var parts = facts.IncompleteParts(table);
            if (parts.Count == 0) return null;
            if (parts.Contains(Facts.RecordsPart))
                return "facts table '" + table + "' was cut" + Detail(facts, table, Facts.RecordsPart) + "; the check saw only part of the document";
            var fields = Fields(check, out bool complete);
            var hit = complete ? parts.Where(fields.Contains).ToList() : parts.ToList();
            if (hit.Count == 0) return null;
            return "facts table '" + table + "' is missing values of " + string.Join(", ", hit.Select(h => "'" + h + "'" + Detail(facts, table, h))) +
                " in some records; the check saw only part of the document";
        }

        /// <summary>What a collector must gather so that <paramref name="checks"/> can be judged. See <see cref="FactsNeeds"/>.</summary>
        public static FactsNeeds Needs(IEnumerable<CheckDefinition> checks)
        {
            var needs = new FactsNeeds();
            foreach (var check in checks)
            {
                if (check.Evaluator == ManualReview.EvaluatorName) continue;
                var fields = Fields(check, out bool complete);
                string? table = Table(check);
                if (!complete) needs.AddUnknown(check.Id, table);
                else if (table != null) needs.Add(table, fields);
            }
            return needs;
        }

        private static string Detail(Facts facts, string table, string what)
        {
            if (facts.Meta[Facts.TruncatedKey] is JsonObject all && all[table] is JsonObject t && t[what] is JsonObject e &&
                e["kept"] is JsonNumber k && e["total"] is JsonNumber n)
                return " (" + k.Raw + " of " + n.Raw + (e["reason"] is JsonString r ? ", " + r.Value : "") + ")";
            if (facts.Meta[Facts.OmittedFieldsKey] is JsonObject om && om[table] is JsonObject o && o[what] is JsonNumber c)
                return " (omitted from " + c.Raw + " records)";
            if (what == Facts.RecordsPart && facts.Meta[table + ".total"] is JsonNumber total)
                return " (" + (facts.TryGetTable(table, out var ft) ? ft.Records.Count : 0) + " of " + total.Raw + ")";
            return "";
        }
    }
}
