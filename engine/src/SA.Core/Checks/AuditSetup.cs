using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Json;
using SA.Core.Standards;

namespace SA.Core.Checks
{
    /// <summary>Counts over a set of check results. Review-by-eye items are counted apart and never as pass or fail.</summary>
    public sealed class CheckSummary
    {
        public int Total { get; private set; }
        public int Pass { get; private set; }
        public int Fail { get; private set; }
        public int Review { get; private set; }
        public int NotImplemented { get; private set; }
        public int Error { get; private set; }
        /// <summary>Checks with status incomplete (no finding on cut facts). A failing check on cut facts counts as Fail.</summary>
        public int Incomplete { get; private set; }

        public static CheckSummary Of(IEnumerable<CheckResult> results)
        {
            var s = new CheckSummary();
            foreach (var r in results)
            {
                s.Total++;
                if (r.IsManualReview || r.Status == CheckStatus.Review) s.Review++;
                else if (r.Status == CheckStatus.Pass) s.Pass++;
                else if (r.Status == CheckStatus.Fail) s.Fail++;
                else if (r.Status == CheckStatus.NotImplemented) s.NotImplemented++;
                else if (r.Status == CheckStatus.Incomplete) s.Incomplete++;
                else s.Error++;
            }
            return s;
        }

        /// <summary>The results that are not review items, and the review items, in their original order.</summary>
        public static void Split(IEnumerable<CheckResult> results, out List<CheckResult> automatic, out List<CheckResult> review)
        {
            automatic = new List<CheckResult>();
            review = new List<CheckResult>();
            foreach (var r in results) (r.IsManualReview || r.Status == CheckStatus.Review ? review : automatic).Add(r);
        }

        public JsonObject ToJson() => new JsonObject().Add("total", Total).Add("pass", Pass).Add("fail", Fail).Add("review", Review)
            .Add("notImplemented", NotImplemented).Add("error", Error).Add("incomplete", Incomplete);
    }

    /// <summary>
    /// What an <c>audit</c> request (or a job's verify) asks for, including where the checks and the standard come from.
    /// Read from the request params with <see cref="FromParams"/>; the client writes them with <see cref="ToParams"/>.
    /// Params: <c>checks</c> (ids), <c>group</c> (one) or <c>groups</c> (list), <c>checksFile</c> (a checks file or a
    /// folder of them, or a list; <c>catalog</c> is accepted as an alias), <c>checksMode</c> (<c>replace</c>, the default:
    /// the given files replace the bundled ones; <c>add</c>: they are loaded after the bundled ones), <c>standard</c> (a
    /// standards file) and <c>includeFacts</c>.
    /// </summary>
    public sealed class AuditOptions
    {
        public List<string> CheckIds { get; } = new List<string>();
        public List<string> Groups { get; } = new List<string>();
        /// <summary>Checks files or folders (every *.json inside, in name order). Empty: use the bundled checks.</summary>
        public List<string> ChecksFiles { get; } = new List<string>();
        /// <summary>When true the <see cref="ChecksFiles"/> are loaded after the bundled checks instead of replacing them.</summary>
        public bool AddToBundled { get; set; }
        /// <summary>A standards file; null: none, or whatever the engine uses by default.</summary>
        public string? StandardPath { get; set; }
        public bool IncludeFacts { get; set; }

        /// <summary>Reads the options. Throws <see cref="FormatException"/> naming the param for a wrongly typed value.</summary>
        public static AuditOptions FromParams(JsonObject? p)
        {
            var o = new AuditOptions();
            if (p is null) return o;
            o.CheckIds.AddRange(Strings(p, "checks"));
            o.Groups.AddRange(Strings(p, "group"));
            foreach (var g in Strings(p, "groups")) if (!o.Groups.Contains(g)) o.Groups.Add(g);
            o.ChecksFiles.AddRange(Strings(p, "checksFile"));
            o.ChecksFiles.AddRange(Strings(p, "catalog"));
            var mode = p["checksMode"];
            if (mode != null && !mode.IsNull)
            {
                string m = mode is JsonString ms ? ms.Value : throw new FormatException("params.checksMode must be 'replace' or 'add'.");
                if (m == "add") o.AddToBundled = true;
                else if (m != "replace") throw new FormatException("params.checksMode must be 'replace' or 'add'.");
            }
            var std = p["standard"];
            if (std != null && !std.IsNull)
                o.StandardPath = std is JsonString ss && ss.Value.Length > 0 ? ss.Value : throw new FormatException("params.standard must be a file path.");
            var inc = p["includeFacts"];
            if (inc != null && !inc.IsNull) o.IncludeFacts = inc is JsonBool b ? b.Value : throw new FormatException("params.includeFacts must be true or false.");
            return o;
        }

        public JsonObject ToParams()
        {
            var o = new JsonObject();
            if (CheckIds.Count > 0) o.Add("checks", JsonArray.FromStrings(CheckIds));
            if (Groups.Count == 1) o.Add("group", Groups[0]);
            else if (Groups.Count > 1) o.Add("groups", JsonArray.FromStrings(Groups));
            if (ChecksFiles.Count == 1) o.Add("checksFile", ChecksFiles[0]);
            else if (ChecksFiles.Count > 1) o.Add("checksFile", JsonArray.FromStrings(ChecksFiles));
            if (AddToBundled) o.Add("checksMode", "add");
            if (StandardPath != null) o.Add("standard", StandardPath);
            if (IncludeFacts) o.Add("includeFacts", true);
            return o;
        }

        private static IEnumerable<string> Strings(JsonObject p, string key)
        {
            var v = p[key];
            if (v is null || v.IsNull) return Array.Empty<string>();
            if (v is JsonString s) return s.Value.Length > 0 ? new[] { s.Value } : Array.Empty<string>();
            if (v is JsonArray a && a.All(x => x is JsonString)) return a.Select(x => ((JsonString)x).Value).Where(x => x.Length > 0).ToList();
            throw new FormatException("params." + key + " must be a string or a list of strings.");
        }
    }

    /// <summary>The checks and standard an audit runs with, where they came from, and what went wrong loading them.</summary>
    public sealed class AuditSetup
    {
        internal AuditSetup(CheckCatalog catalog) { Catalog = catalog; }

        public CheckCatalog Catalog { get; }
        public ProjectStandard? Standard { get; internal set; }
        /// <summary>Every checks file loaded, as a full path, in load order.</summary>
        public List<string> CatalogFiles { get; } = new List<string>();
        public string? StandardFile { get; internal set; }
        /// <summary>Messages naming the file (and JSON path) of each problem. A file that failed adds no checks.</summary>
        public List<string> Problems { get; } = new List<string>();
        public bool Ok => Problems.Count == 0;

        public List<CheckDefinition> Select(CheckApp app, AuditOptions options, out List<string> unknownIds) =>
            Catalog.Select(app, options.CheckIds, options.Groups, out unknownIds);
    }

    /// <summary>Loads checks files and a standard for an audit, from explicit paths or the bundled locations.</summary>
    public static class AuditLoader
    {
        /// <summary>
        /// Resolves the options' paths (relative paths against <paramref name="baseDirectory"/>, e.g. the document's
        /// folder, else the current directory) and loads them. <paramref name="bundledChecks"/> are the engine's default
        /// checks files or folders, used unless the options name their own (or with checksMode "add"). Missing folders
        /// among the bundled locations are skipped silently; a missing explicit path is a problem.
        /// <paramref name="defaultStandard"/> is used when the options name no standard.
        /// </summary>
        public static AuditSetup Load(AuditOptions options, IEnumerable<string>? bundledChecks, string? baseDirectory = null, string? defaultStandard = null)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var setup = new AuditSetup(new CheckCatalog());
            bool useBundled = options.ChecksFiles.Count == 0 || options.AddToBundled;
            if (useBundled && bundledChecks != null)
                foreach (var b in bundledChecks)
                    if (File.Exists(b) || Directory.Exists(b)) AddPath(setup, b, true);
            foreach (var f in options.ChecksFiles) AddPath(setup, Resolve(f, baseDirectory), false);

            string? std = options.StandardPath ?? defaultStandard;
            if (std != null)
            {
                string path = options.StandardPath != null ? Resolve(std, baseDirectory) : std;
                if (StandardLoader.TryLoadFile(path, out var standard, out var error))
                {
                    setup.Standard = standard;
                    setup.StandardFile = Path.GetFullPath(path);
                }
                else setup.Problems.Add(error!);
            }
            return setup;
        }

        public static string Resolve(string path, string? baseDirectory)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), path));
        }

        private static void AddPath(AuditSetup setup, string path, bool bundled)
        {
            IEnumerable<string> files;
            if (Directory.Exists(path)) files = Directory.GetFiles(path, "*.json").OrderBy(f => f, StringComparer.Ordinal);
            else if (File.Exists(path)) files = new[] { path };
            else { setup.Problems.Add("Checks file or folder '" + path + "' was not found."); return; }
            foreach (var f in files)
            {
                string full = Path.GetFullPath(f);
                if (setup.CatalogFiles.Contains(full)) continue;
                string? error = setup.Catalog.TryAddFile(full);
                if (error is null) setup.CatalogFiles.Add(full);
                else setup.Problems.Add(error);
            }
        }
    }
}
