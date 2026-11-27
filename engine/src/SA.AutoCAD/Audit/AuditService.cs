using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Audit.Collectors;
using SA.AutoCAD.Host;
using SA.Core.Checks;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Standards;

namespace SA.AutoCAD.Audit
{
    /// <summary>
    /// Where checks and standards come from. Bundled checks: every *.json in &lt;bundle&gt;\Contents\data\checks\ (beside
    /// the release folders) and in %LOCALAPPDATA%\CadBim\checks\. Bundled standards: &lt;bundle&gt;\Contents\data\standards\
    /// and %LOCALAPPDATA%\CadBim\standards\. A request may name its own checks file(s) or folder(s) ("checksFile",
    /// replacing the bundled checks unless "checksMode" is "add") and its own standard ("standard"). Relative paths are
    /// tried against the drawing's folder, %LOCALAPPDATA%\CadBim\ and the bundle's Contents folder.
    /// Loading is Core's <see cref="AuditLoader"/>: a file that fails to parse adds no checks and is reported.
    /// </summary>
    internal static class CheckCatalogs
    {
        private static string? BundleContents()
        {
            string? asmDir = null;
            try { asmDir = Path.GetDirectoryName(typeof(CheckCatalogs).Assembly.Location); } catch (Exception) { }
            return string.IsNullOrEmpty(asmDir) ? null : Path.GetFullPath(Path.Combine(asmDir!, ".."));
        }

        internal static IEnumerable<string> Folders()
        {
            var contents = BundleContents();
            if (contents != null) yield return Path.Combine(contents, "data", "checks");
            yield return Path.Combine(SaPaths.Root(), "checks");
        }

        internal static IEnumerable<string> StandardFolders()
        {
            var contents = BundleContents();
            if (contents != null) yield return Path.Combine(contents, "data", "standards");
            yield return Path.Combine(SaPaths.Root(), "standards");
        }

        /// <summary>
        /// The bundled standard used when a request or job names none: the US preset for imperial drawings (inches or feet,
        /// or unitless with MEASUREMENT 0), the ISO 19650 preset otherwise. Null when neither file is installed.
        /// </summary>
        internal static string? DefaultStandard(Database db, out string preset)
        {
            string? unit = FactValues.MetaLengthUnit(db, out _);
            bool imperial = unit == "in" || unit == "ft" || (unit is null && (int)db.Measurement == 0);
            preset = imperial ? "us-ncs-style.json" : "iso19650-uk-style.json";
            foreach (var folder in StandardFolders())
            {
                string f = Path.Combine(folder, preset);
                try { if (File.Exists(f)) return f; } catch (Exception) { }
            }
            return null;
        }

        /// <summary>A relative path tried against the drawing folder, %LOCALAPPDATA%\CadBim\ and the bundle Contents.</summary>
        internal static string ResolvePath(string path, string? documentFolder)
        {
            if (Path.IsPathRooted(path)) return path;
            string rel = path.Replace('/', Path.DirectorySeparatorChar);
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(documentFolder)) candidates.Add(Path.Combine(documentFolder!, rel));
            candidates.Add(Path.Combine(SaPaths.Root(), rel));
            var contents = BundleContents();
            if (contents != null) candidates.Add(Path.Combine(contents, rel));
            foreach (var c in candidates)
            {
                try { if (File.Exists(c) || Directory.Exists(c)) return Path.GetFullPath(c); } catch (Exception) { }
            }
            return Path.GetFullPath(candidates[0]);
        }

        internal static string? DocumentFolder(Document doc)
        {
            if (!doc.IsNamedDrawing) return null;
            try { return Path.GetDirectoryName(doc.Name); } catch (Exception) { return null; }
        }

        /// <summary>Loads the checks (and the standard, when <paramref name="standardFile"/> is given) through Core.</summary>
        internal static AuditSetup Load(IEnumerable<string> checksFiles, bool addToBundled, string? standardFile)
        {
            var options = new AuditOptions { AddToBundled = addToBundled };
            options.ChecksFiles.AddRange(checksFiles);
            return AuditLoader.Load(options, Folders(), null, standardFile);
        }

        /// <summary>Ids of the bundled AutoCAD checks.</summary>
        internal static HashSet<string> KnownAutoCadIds() => Ids(Load(Array.Empty<string>(), false, null));

        /// <summary>
        /// Ids of the AutoCAD checks a run or validate request will use: the bundled checks, or the request's own
        /// "checksFile" (with "checksMode"). Also remembers that choice for the job's verify step. Pipe thread: file
        /// access only, no AutoCAD API.
        /// </summary>
        internal static HashSet<string> KnownAutoCadIds(JsonObject? requestParams)
        {
            var req = VerifyRequest.From(requestParams);
            if (requestParams?["job"] is JsonObject jobJson) VerifyRequest.Attach(jobJson, req);
            if (req.Error != null) return KnownAutoCadIds();
            return Ids(Load(req.ChecksFiles, req.AddToBundled, null));
        }

        private static HashSet<string> Ids(AuditSetup setup) =>
            new HashSet<string>(setup.Catalog.Checks.Where(c => c.AppliesTo(CheckApp.AutoCad)).Select(c => c.Id), StringComparer.Ordinal);
    }

    /// <summary>
    /// The checks-file choice of a run or validate request ("checksFile", "checksMode" next to "job", else the job's own
    /// verify.checksFile and verify.checksMode), carried from static validation on the pipe thread to the job's verify step. Keyed by the job's JSON object, which Core keeps as Job.Raw.
    /// </summary>
    internal sealed class VerifyRequest
    {
        private static readonly ConditionalWeakTable<JsonObject, VerifyRequest> Attached = new ConditionalWeakTable<JsonObject, VerifyRequest>();

        internal static readonly VerifyRequest Bundled = new VerifyRequest();

        internal List<string> ChecksFiles { get; } = new List<string>();
        internal bool AddToBundled { get; private set; }
        internal string? Error { get; private set; }

        internal static VerifyRequest From(JsonObject? p)
        {
            var r = new VerifyRequest();
            if (p is null) return r;
            AuditOptions o;
            var only = new JsonObject();
            if (p["checksFile"] != null) only.Add("checksFile", p["checksFile"]!.DeepClone());
            if (p["catalog"] != null) only.Add("catalog", p["catalog"]!.DeepClone());
            if (p["checksMode"] != null) only.Add("checksMode", p["checksMode"]!.DeepClone());
            // Without a choice in the request, the job's own verify.checksFile / checksMode apply.
            if (only.Count == 0 && p["job"] is JsonObject jj && jj["verify"] is JsonObject jv)
            {
                if (jv["checksFile"] != null) only.Add("checksFile", jv["checksFile"]!.DeepClone());
                if (jv["checksMode"] != null) only.Add("checksMode", jv["checksMode"]!.DeepClone());
            }
            try { o = AuditOptions.FromParams(only); }
            catch (FormatException e) { r.Error = e.Message; return r; }
            string? folder = null;
            if (p["job"] is JsonObject job && job["document"] is JsonObject d && d["path"] is JsonString path && path.Value.Length > 0)
            {
                try { if (Path.IsPathRooted(path.Value)) folder = Path.GetDirectoryName(path.Value); } catch (Exception) { }
            }
            foreach (var f in o.ChecksFiles) r.ChecksFiles.Add(CheckCatalogs.ResolvePath(f, folder));
            r.AddToBundled = o.AddToBundled;
            return r;
        }

        internal static void Attach(JsonObject jobJson, VerifyRequest request)
        {
            Attached.Remove(jobJson);
            Attached.Add(jobJson, request);
        }

        internal static VerifyRequest For(Job job) => Attached.TryGetValue(job.Raw, out var r) ? r : Bundled;
    }

    /// <summary>Evaluator registry: Core's neutral evaluators plus every ICheckEvaluator in this assembly.</summary>
    internal static class Evaluators
    {
        private static EvaluatorRegistry? _registry;

        internal static EvaluatorRegistry Registry => _registry ??= Build();

        private static EvaluatorRegistry Build()
        {
            var r = EvaluatorRegistry.CreateNeutral();
            Type[] types;
            try { types = typeof(Evaluators).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface || !typeof(ICheckEvaluator).IsAssignableFrom(t)) continue;
                try
                {
                    var e = (ICheckEvaluator)Activator.CreateInstance(t, true)!;
                    if (!r.TryRegister(e)) Log.Warn("Check evaluator " + t.FullName + " skipped: '" + e.Name + "' is already registered.");
                }
                catch (Exception e) { Log.Error("Check evaluator " + t.FullName + " could not be registered.", e); }
            }
            return r;
        }

        /// <summary>A runner that reports review-by-eye checks with their own status ("review"), never as fail.</summary>
        internal static CheckRunner Runner() => new CheckRunner(Registry) { ReviewStatus = true };
    }

    /// <summary>The audit method and the job's verify.checks: collect facts, select checks, run them.</summary>
    internal static class AuditService
    {
        /// <summary>Every AutoCAD table of data/checks/FACTS.md; one missing from the facts makes its checks not_implemented.</summary>
        internal static readonly string[] ContractTables = { "document", "layers", "entities", "blocks", "xrefs", "styles", "layouts", "annotations", "jobPlacement" };

        /// <summary>The audit method body. Document: the active document (audit takes no document parameter).</summary>
        internal static JsonValue Audit(JsonObject p)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument
                ?? throw new EngineException(ErrorCodes.WrongDocument, "No drawing is open.");
            AuditOptions options;
            try { options = AuditOptions.FromParams(p); }
            catch (FormatException e) { throw new EngineException(ErrorCodes.BadRequest, e.Message); }

            string? folder = CheckCatalogs.DocumentFolder(doc);
            string? standardFile;
            string standardSource;
            if (options.StandardPath != null)
            {
                standardFile = StandardResolver.Resolve(options.StandardPath, doc);
                if (standardFile is null)
                    throw new EngineException(ErrorCodes.BadRequest, "Standard file '" + options.StandardPath + "' was not found (looked in: " +
                        string.Join("; ", StandardResolver.Candidates(options.StandardPath, doc)) + ").");
                standardSource = "request";
            }
            else
            {
                standardFile = CheckCatalogs.DefaultStandard(doc.Database, out _);
                standardSource = standardFile != null ? "default" : "none";
            }
            var files = options.ChecksFiles.Select(f => CheckCatalogs.ResolvePath(f, folder)).ToList();
            var setup = CheckCatalogs.Load(files, options.AddToBundled, standardFile);
            if (standardSource == "request" && setup.Standard is null)
                throw new EngineException(ErrorCodes.BadRequest, "Standard '" + standardFile + "' is invalid: " + string.Join("; ", setup.Problems));

            var selected = setup.Select(CheckApp.AutoCad, options, out var unknown);
            // Facts asked for in the reply are collected whole; otherwise only what the selected checks read.
            var needs = options.IncludeFacts ? null : CheckInputs.Needs(selected);
            if (needs != null && needs.Everything) needs = null;
            using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var facts = FactCollectors.Collect(doc, tr, needs, setup.Standard?.NamesToKeep());
                var results = Evaluators.Runner().RunAll(selected, facts, setup.Standard);
                AddLocations(results, facts);
                tr.Commit();

                var o = new JsonObject()
                    .Add("checks", new JsonArray(results.Select(r => (JsonValue)r.ToJson())))
                    .Add("summary", CheckSummary.Of(results).ToJson())
                    .Add("reviewByEye", new JsonArray(results.Where(r => r.IsManualReview).Select(r => (JsonValue)new JsonObject()
                        .Add("id", r.Id).Add("title", r.Title ?? "").Add("instructions", r.ReviewInstructions ?? ""))))
                    .Add("unknownChecks", JsonArray.FromStrings(unknown))
                    .Add("tables", JsonArray.FromStrings(facts.Tables.Select(t => t.Name)))
                    .Add("tablesMissing", JsonArray.FromStrings(ContractTables.Where(t => (needs is null || needs.NeedsTable(t)) && !facts.HasTable(t))))
                    .Add("factsMeta", facts.Meta.DeepClone())
                    .Add("catalogFiles", JsonArray.FromStrings(setup.CatalogFiles))
                    .Add("standard", StandardJson(setup.StandardFile, standardSource));
                if (setup.Problems.Count > 0) o.Add("catalogProblems", JsonArray.FromStrings(setup.Problems));
                if (needs != null) o.Add("collected", needs.ToJson());
                var truncated = facts.IncompleteTables().ToList();
                if (truncated.Count > 0) o.Add("truncatedTables", JsonArray.FromStrings(truncated));
                if (selected.Count == 0 && options.CheckIds.Count == 0)
                    o.Add("note", setup.Catalog.Checks.Count == 0
                        ? "No check definitions were found in " + string.Join(" or ", files.Count > 0 ? files : CheckCatalogs.Folders()) + "."
                        : "No checks matched the request.");
                if (options.IncludeFacts) o.Add("facts", facts.ToJson());
                o.Add("document", new JsonObject().Add("path", doc.Name ?? "").Add("fingerprint", DocumentIdentity.FingerprintOf(doc)));
                return o;
            }
        }

        /// <summary>
        /// The job's verify.checks, inside the job's transaction before commit. Checks come from the bundled files or the
        /// run request's "checksFile"; the standard is the job's, else the bundled default for the drawing's units. Only the
        /// tables and fields the checks read are collected (<see cref="CheckInputs.Needs"/>), under the lean safety ceiling.
        /// Setup problems, a default standard, incomplete tables and collector failures become report warnings.
        /// </summary>
        internal static List<CheckResult> Verify(Document doc, Transaction tr, Job job, ProjectStandard? jobStandard, List<ReportWarning> warnings, ISet<string>? jobScope = null)
        {
            var req = VerifyRequest.For(job);
            if (req.Error != null) throw new EngineException(ErrorCodes.BadRequest, req.Error);
            string? defaultStandard = jobStandard is null ? CheckCatalogs.DefaultStandard(doc.Database, out _) : null;
            var setup = CheckCatalogs.Load(req.ChecksFiles, req.AddToBundled, defaultStandard);
            var standard = jobStandard ?? setup.Standard;
            foreach (var problem in setup.Problems) warnings.Add(new ReportWarning("engine", "Checks setup: " + problem));
            if (jobStandard is null)
                warnings.Add(new ReportWarning("engine", setup.StandardFile != null
                    ? "The job names no standard; verify checks used the bundled default '" + setup.StandardFile + "'."
                    : "The job names no standard and no bundled default standard is installed; checks that need one report error."));

            var options = new AuditOptions();
            options.CheckIds.AddRange(job.Verify.Checks);
            var selected = setup.Select(CheckApp.AutoCad, options, out var unknown);
            foreach (var u in unknown) warnings.Add(new ReportWarning("engine", "Verify check '" + u + "' is not defined in the checks loaded for this run."));

            var facts = FactCollectors.Collect(doc, tr, CheckInputs.Needs(selected), standard?.NamesToKeep(), jobScope);
            foreach (var t in facts.IncompleteTables())
                warnings.Add(new ReportWarning("engine", "Facts table '" + t + "' is incomplete (" + string.Join(", ", facts.IncompleteParts(t)) +
                    "); checks on it saw only part of the drawing and report incomplete.") { Code = "facts_incomplete", Severity = "warning" });
            if (facts.Meta["errors"] is JsonArray errors)
                foreach (var e in errors.OfType<JsonObject>())
                    warnings.Add(new ReportWarning("engine", "Facts table '" + e.GetStringOrNull("table") + "' could not be collected: " + e.GetStringOrNull("error")));

            var results = Evaluators.Runner().RunAll(selected, facts, standard);
            AddLocations(results, facts);
            return results;
        }

        private static JsonObject StandardJson(string? file, string source)
        {
            var o = new JsonObject().Add("source", source);
            o.Add("path", file is null ? (JsonValue)JsonValue.Null : file);
            return o;
        }

        /// <summary>
        /// Adds data.location to every finding about a record, so the agent can act on it without a second lookup:
        /// { table, handle, type, layer, space, layout, name } as far as the record has them (for xrefs the layer is the
        /// insert's layer). An existing "location" value is never replaced.
        /// </summary>
        internal static void AddLocations(IEnumerable<CheckResult> results, Facts facts)
        {
            var index = new Dictionary<string, Dictionary<string, FactRecord>>(StringComparer.Ordinal);
            foreach (var r in results)
                foreach (var f in r.Findings)
                {
                    if (f.RecordId is null || f.Table is null || f.Data["location"] != null) continue;
                    if (!index.TryGetValue(f.Table, out var byId))
                    {
                        byId = new Dictionary<string, FactRecord>(StringComparer.Ordinal);
                        if (facts.TryGetTable(f.Table, out var table))
                            foreach (var rec in table.Records)
                                if (rec.Id != null && !byId.ContainsKey(rec.Id)) byId[rec.Id] = rec;
                        index[f.Table] = byId;
                    }
                    if (!byId.TryGetValue(f.RecordId, out var record)) continue;
                    var loc = new JsonObject().Add("table", f.Table).Add("handle", f.RecordId);
                    foreach (var key in new[] { "type", "layer", "space", "layout", "name" })
                        if (record.Get(key) is JsonString s) loc.Add(key, s.Value);
                    if (f.Table == "xrefs" && record.Get("insertLayer") is JsonString il) loc.Add("layer", il.Value);
                    f.Data.Set("location", loc);
                }
        }
    }
}
