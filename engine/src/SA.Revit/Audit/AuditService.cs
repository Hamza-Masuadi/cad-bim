using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Revit.Ops;
using SA.Revit.Ops.Support;

namespace SA.Revit.Audit
{
    /// <summary>What a fact collector gets: the document (read only; no transaction is open) and the unit helper.</summary>
    internal sealed class AuditContext
    {
        public AuditContext(Document doc, RevitUnits units)
        {
            Doc = doc;
            Units = units;
        }

        public Document Doc { get; }
        /// <summary>Lengths in facts are written in these job units (the job's, or mm for a plain audit).</summary>
        public RevitUnits Units { get; }

        /// <summary>The fields the checks read (verify), or null for every field (a plain audit).</summary>
        public FactsNeeds? Needs { get; set; }

        /// <summary>
        /// False when the checks being run do not read <paramref name="field"/>: a collector skips a costly field then.
        /// Records are always written; only fields may be left out.
        /// </summary>
        public bool Wants(string table, string field) => Needs is null || Needs.NeedsField(table, field);

        /// <summary>Ids of the elements the job created or modified, in a job's verify step; null in an audit.</summary>
        public ISet<long>? JobScope { get; set; }

        private readonly Dictionary<string, object> _shared = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// A value computed once per audit and shared by collectors (for example instance counts per type), so no
        /// collector walks the whole model twice for the same thing.
        /// </summary>
        public T Shared<T>(string key, Func<T> make) where T : class
        {
            if (_shared.TryGetValue(key, out var v) && v is T t) return t;
            var made = make();
            _shared[key] = made;
            return made;
        }

        /// <summary>Truncation and omission notes, written to facts meta "truncated" (never silent).</summary>
        internal JsonObject Truncation { get; } = new JsonObject();

        /// <summary>
        /// Records that a table or a field was cut for size: <paramref name="what"/> is "records" or a field name,
        /// <paramref name="kept"/> how many records carry it, <paramref name="total"/> how many would.
        /// </summary>
        public void Truncated(string table, string what, long kept, long total, string reason)
        {
            var t = Truncation[table] as JsonObject;
            if (t is null) { t = new JsonObject(); Truncation.Add(table, t); }
            t.Set(what, new JsonObject().Add("kept", kept).Add("total", total).Add("reason", reason));
        }

        /// <summary>Per table, fields left out of some records because Revit threw while reading them (field -> count).</summary>
        internal Dictionary<string, Dictionary<string, int>> Omissions { get; } = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public void Omitted(string table, string field)
        {
            if (!Omissions.TryGetValue(table, out var m)) Omissions[table] = m = new Dictionary<string, int>(StringComparer.Ordinal);
            m[field] = m.TryGetValue(field, out int n) ? n + 1 : 1;
        }
    }

    /// <summary>
    /// Fills one Core facts table from the model. Implement it with a parameterless constructor and it is found at
    /// start-up. Rules: read only, never open a transaction, write lengths in ctx.Units, ids as strings, one record
    /// per element with the element id as the record id. Throwing leaves the table out, which makes every check that
    /// needs it report not_implemented rather than pass.
    /// </summary>
    internal interface IFactCollector
    {
        /// <summary>Table name agreed with the checks data, e.g. "warnings".</summary>
        string Table { get; }
        void Collect(AuditContext ctx, FactTable table);
    }

    internal sealed class FactCollectorRegistry
    {
        private readonly Dictionary<string, IFactCollector> _byTable = new Dictionary<string, IFactCollector>(StringComparer.Ordinal);

        public FactCollectorRegistry()
        {
            foreach (var t in HandlerRegistry.SafeTypes(typeof(FactCollectorRegistry).Assembly))
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IFactCollector).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) is null) continue;
                try
                {
                    var c = (IFactCollector)Activator.CreateInstance(t)!;
                    if (_byTable.ContainsKey(c.Table)) Problems.Add(t.FullName + ": a second collector for table '" + c.Table + "'");
                    else _byTable.Add(c.Table, c);
                }
                catch (Exception e) { Problems.Add(t.FullName + ": " + e.Message); }
            }
        }

        public List<string> Problems { get; } = new List<string>();
        public IEnumerable<string> Tables => _byTable.Keys.OrderBy(k => k, StringComparer.Ordinal);
        public IEnumerable<IFactCollector> All => _byTable.Values;
    }

    /// <summary>The audit method and the job's verify.checks: collect facts, load checks, run Core's runner.</summary>
    internal sealed class AuditService
    {
        private readonly FactCollectorRegistry _collectors;
        private readonly EvaluatorRegistry _evaluators;

        public AuditService(FactCollectorRegistry collectors)
        {
            _collectors = collectors;
            _evaluators = EvaluatorRegistry.CreateNeutral();
            foreach (var t in HandlerRegistry.SafeTypes(typeof(AuditService).Assembly))
            {
                if (t.IsAbstract || t.IsInterface || !typeof(ICheckEvaluator).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) is null) continue;
                try { var ev = (ICheckEvaluator)Activator.CreateInstance(t)!; if (!_evaluators.TryRegister(ev)) Host.Log.Warn("Evaluator " + t.FullName + " not registered: the name " + ev.Name + " is taken by Core"); }
                catch (Exception e) { Host.Log.Warn("Evaluator " + t.FullName + " not registered: " + e.Message); }
            }
        }

        public FactCollectorRegistry Collectors => _collectors;

        public Facts Collect(Document doc, RevitUnits units, ICollection<string>? onlyTables = null, FactsNeeds? needs = null, ISet<long>? jobScope = null)
        {
            var facts = new Facts("revit");
            // FACTS.md section 0: units.length is the project's display unit for Length, mapped to mm/cm/m/in/ft,
            // and left out when it maps to none of them. Lengths the collectors write without a unit in the field name
            // are in the job unit, given as units.facts.
            string? projectUnit = SA.Revit.Audit.Collectors.AuditCache.ProjectLengthSymbol(doc);
            if (projectUnit != null) facts.Meta.Add("units.length", projectUnit);
            facts.Meta.Add("units.facts", Length.Symbol(units.JobUnit));
            facts.Meta.Add("document.title", doc.Title ?? "");
            facts.Meta.Add("engine.release", Compat.ReleaseInfo.Year);
            var errors = new JsonObject();
            var ctx = new AuditContext(doc, units) { Needs = needs, JobScope = jobScope };
            if (jobScope != null) facts.Meta.Set(LayoutEvaluators.JobScopeKey, true);
            foreach (var c in _collectors.All)
            {
                if (onlyTables != null && onlyTables.Count > 0 && !onlyTables.Contains(c.Table)) continue;
                var table = new FactTable(c.Table);
                try
                {
                    c.Collect(ctx, table);
                    var t = facts.Table(c.Table);
                    t.Records.AddRange(table.Records);
                }
                catch (Exception e)
                {
                    errors.Add(c.Table, e.GetType().Name + ": " + e.Message);
                    Host.Log.Error("Fact collector '" + c.Table + "' failed", e);
                }
            }
            if (errors.Count > 0) facts.Meta.Add("collectorErrors", errors);
            if (ctx.Truncation.Count > 0) facts.Meta.Add("truncated", ctx.Truncation);
            if (ctx.Omissions.Count > 0)
            {
                var om = new JsonObject();
                foreach (var t in ctx.Omissions.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var f = new JsonObject();
                    foreach (var kv in t.Value.OrderBy(k => k.Key, StringComparer.Ordinal)) f.Add(kv.Key, kv.Value);
                    om.Add(t.Key, f);
                }
                facts.Meta.Add("omittedFields", om);
            }
            return facts;
        }

        // ------------------------------------------------------------------ checks and standard

        /// <summary>The checks file the Revit engine ships (data/checks/revit.json, installed as &lt;add-in&gt;\checks\revit.json).</summary>
        public const string BundledChecksFile = "revit.json";

        /// <summary>Bundled standards by unit system (data/standards, installed as &lt;add-in&gt;\standards).</summary>
        public const string ImperialStandardFile = "us-ncs-style.json";
        public const string MetricStandardFile = "iso19650-uk-style.json";

        private static string? AddinFolder()
        {
            try { return Path.GetDirectoryName(typeof(AuditService).Assembly.Location); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The first existing of: &lt;add-in&gt;\&lt;folder&gt;\&lt;file&gt;, %LOCALAPPDATA%\CadBim\&lt;folder&gt;\&lt;file&gt;, and
        /// data\&lt;folder&gt;\&lt;file&gt; in a parent of the add-in folder (a development tree). Null when none exists.
        /// </summary>
        public static string? FindBundled(string folder, string file)
        {
            var candidates = new List<string>();
            string? here = AddinFolder();
            if (!string.IsNullOrEmpty(here)) candidates.Add(Path.Combine(here!, folder, file));
            candidates.Add(Path.Combine(SaPaths.Root(), folder, file));
            for (var d = string.IsNullOrEmpty(here) ? null : new DirectoryInfo(here!); d != null; d = d.Parent)
                candidates.Add(Path.Combine(d.FullName, "data", folder, file));
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>The bundled checks: revit.json from the first bundled location that has it (one file, so no id is loaded twice).</summary>
        public static List<string> BundledChecks()
        {
            var f = FindBundled("checks", BundledChecksFile);
            return f is null ? new List<string>() : new List<string> { f };
        }

        /// <summary>
        /// The bundled default standard for a project length unit: us-ncs-style for in and ft, iso19650-uk-style
        /// otherwise. Null when the file is not installed.
        /// </summary>
        public static string? DefaultStandardPath(string? projectUnit) =>
            FindBundled("standards", projectUnit == "in" || projectUnit == "ft" ? ImperialStandardFile : MetricStandardFile);

        /// <summary>The folder relative checks and standard paths are resolved against: the model's folder, when it has one on disk.</summary>
        public static string? DocumentFolder(Document doc)
        {
            try
            {
                string p = doc.PathName ?? "";
                if (p.Length == 0 || !Path.IsPathRooted(p)) return null;
                return Path.GetDirectoryName(p);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Loads the checks and the standard for an audit or a job's verify through Core's AuditLoader:
        /// checks = options.ChecksFiles (params checksFile or catalog; replace or, with checksMode "add", add to) the
        /// bundled revit.json; standard = options.StandardPath (params standard), else <paramref name="jobStandard"/>,
        /// else the bundled default for the project's length unit. Relative paths resolve against the model's folder.
        /// <paramref name="standardSource"/> is "explicit", "job", "bundled" or "none".
        /// </summary>
        public AuditSetup Load(AuditOptions options, Document doc, string? jobStandard, out string standardSource)
        {
            if (options.StandardPath is null && !string.IsNullOrEmpty(jobStandard))
            {
                options.StandardPath = jobStandard;
                standardSource = "job";
            }
            else standardSource = options.StandardPath != null ? "explicit" : "bundled";
            string? fallback = null;
            if (options.StandardPath is null)
            {
                fallback = DefaultStandardPath(SA.Revit.Audit.Collectors.AuditCache.ProjectLengthSymbol(doc));
                if (fallback is null) standardSource = "none";
            }
            var setup = AuditLoader.Load(options, BundledChecks(), DocumentFolder(doc), fallback);
            if (options.ChecksFiles.Count == 0 && setup.CatalogFiles.Count == 0)
                setup.Problems.Add("The bundled checks file " + BundledChecksFile + " was not found next to the add-in (checks\\" + BundledChecksFile +
                                   "), in " + Path.Combine(SaPaths.Root(), "checks") + ", or in a data\\checks folder above the add-in; name a checks file with params.checksFile.");
            if (standardSource == "none")
                setup.Problems.Add("No standard was named and the bundled standards (standards\\" + ImperialStandardFile + ", " + MetricStandardFile +
                                   ") were not found; checks that need a standard report error.");
            return setup;
        }

        /// <summary>
        /// Runs the chosen checks. Unknown ids come back as not_implemented results, never as passes. With no standard,
        /// the bundled default for facts meta units.length is loaded (<paramref name="defaultStandard"/> false turns this off). Review-by-eye checks get status "review" (CheckRunner.ReviewStatus) unless
        /// <paramref name="reviewStatus"/> is false, so they are never counted as failures that roll a job back.
        /// Findings without a record id are given the element ids of the records they describe (see
        /// <see cref="AttachElementIds"/>).
        /// </summary>
        public List<CheckResult> Run(Facts facts, CheckCatalog catalog, IEnumerable<string>? ids, IEnumerable<string>? groups, ProjectStandard? standard,
            List<string>? problems = null, bool reviewStatus = true, bool defaultStandard = true)
        {
            if (standard is null && defaultStandard)
            {
                string? unit = facts.Meta["units.length"] is JsonString us ? us.Value : null;
                string? path = DefaultStandardPath(unit);
                if (path != null)
                {
                    if (StandardLoader.TryLoadFile(path, out var std, out var error)) standard = std;
                    else problems?.Add(error ?? ("bundled standard " + path + " could not be loaded"));
                }
            }
            var chosen = catalog.Select(CheckApp.Revit, ids, groups, out var unknown);
            var results = new CheckRunner(_evaluators) { ReviewStatus = reviewStatus }.RunAll(chosen, facts, standard);
            foreach (var u in unknown)
                results.Add(new CheckResult(u, CheckStatus.NotImplemented, "No Revit check with id '" + u + "' is loaded."));
            AttachElementIds(results, facts);
            return results;
        }

        /// <summary>Most element ids attached to one finding.</summary>
        public const int MaxIdsPerFinding = 1000;

        /// <summary>
        /// A finding with no record id (warnings have none; group counts describe many records) gets data.elementIds:
        /// the union of the elementIds field of every record of its table whose data.field equals data.value. So a
        /// "slightly off axis" finding names the walls Revit flagged, and the agent can act on them.
        /// </summary>
        public static void AttachElementIds(IEnumerable<CheckResult> results, Facts facts)
        {
            foreach (var r in results)
            {
                foreach (var f in r.Findings)
                {
                    if (f.RecordId != null || f.Table is null || f.Data.ContainsKey("elementIds")) continue;
                    if (!facts.TryGetTable(f.Table, out var table)) continue;
                    string? field = f.Data.GetStringOrNull("field");
                    var value = f.Data["value"];
                    if (field is null || value is null || value.IsNull) continue;
                    var ids = new List<string>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    bool cut = false;
                    foreach (var rec in table.Records)
                    {
                        if (!(rec.Get("elementIds") is JsonArray a)) continue;
                        if (!JsonValue.DeepEquals(rec.Get(field), value)) continue;
                        foreach (var id in a.OfType<JsonString>())
                        {
                            if (!seen.Add(id.Value)) continue;
                            if (ids.Count >= MaxIdsPerFinding) { cut = true; break; }
                            ids.Add(id.Value);
                        }
                    }
                    if (ids.Count == 0) continue;
                    f.Data.Set("elementIds", JsonArray.FromStrings(ids));
                    if (cut) f.Data.Set("elementIdsTruncated", true);
                }
            }
        }
    }
}
