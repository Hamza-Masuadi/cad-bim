using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Output
{
    /// <summary>
    /// export.dwg: sheets (by number) and views (refs or names) to DWG, one file per item.
    ///
    /// API: Document.Export(folder, name, ICollection&lt;ElementId&gt;, DWGExportOptions). Setup: <c>setup</c> names a
    /// predefined export setup of the document, read with DWGExportOptions.GetPredefinedOptions(doc, name) (null when
    /// the name is unknown: a validation error listing BaseExportOptions.GetPredefinedSetupNames). Without
    /// <c>setup</c>, the document's active predefined setup (ExportDWGSettings.GetActivePredefinedSettings) is used,
    /// else new DWGExportOptions() (Revit's defaults). <c>mergedViews</c> is applied on top of either (schema default
    /// true): DWGExportOptions.MergedViews; when false, Revit writes the views on a sheet as separate xref files.
    ///
    /// Each item is exported alone into a scratch folder. The main file is the produced .dwg named like the requested
    /// base name, or the only .dwg when there is one. Every other file Revit writes for the item (view xrefs when
    /// mergedViews is false, and any side files) is moved next to it under its own name, refused if it exists unless
    /// <c>overwrite</c>, and listed in readback as a companion.
    ///
    /// The model is not changed and no transaction is opened (ModifiesModel false); a refusal by Revit while the
    /// job's transaction group is open becomes export_refused.
    ///
    /// Readback: each main file exists, is not empty, and starts with a DWG version string (AC10xx), which is
    /// reported. Companions are checked to exist and not be empty.
    /// </summary>
    public sealed class ExportDwg : OpHandler
    {
        public override string Name => "export.dwg";
        public override bool ModifiesModel => false;

        private sealed class OutFile
        {
            public View View = null!;
            public string Path = "";
            public string Base = "";
        }

        private sealed class Plan
        {
            public List<OutFile> Files = new List<OutFile>();
            public string SetupName = "";
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            bool ok = true;
            string? dir = OutputSupport.OutputDir(ctx, args, issues);
            if (dir is null) ok = false;
            var plan = new Plan();
            string? setup = args.GetStringOrNull("setup");
            if (setup != null)
            {
                if (LoadSetup(ctx.Doc, setup) is null)
                {
                    var names = SafeSetupNames(ctx.Doc);
                    OpContext.Error(issues, ctx.FieldPath(args, "setup"), RevitIssueCodes.NotFound, "no DWG export setup named '" + setup + "'" + Lookup.Suggest(setup, names));
                    ok = false;
                }
                plan.SetupName = setup;
            }
            else plan.SetupName = ActiveSetupName(ctx.Doc) ?? "(Revit defaults)";
            string? pattern = args.GetStringOrNull("fileName");
            if (pattern != null)
            {
                var unknown = OutputSupport.UnknownPlaceholders(pattern);
                if (unknown.Count > 0)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid,
                        "unknown placeholder(s) " + string.Join(", ", unknown.Select(u => "{" + u + "}")) + "; use " + string.Join(", ", OutputSupport.Placeholders.Select(p => "{" + p + "}")));
                    ok = false;
                }
            }
            var items = OutputSupport.Items(ctx, args, issues);
            if (items is null || !ok || dir is null) return null;
            bool overwrite = args.GetBool("overwrite");
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in items)
            {
                string pat = pattern ?? (v is ViewSheet ? "{sheetNumber}" : "{view}");
                string file = OutputSupport.FileName(OutputSupport.Expand(pat, ctx.Doc, v, ctx.JobId), ".dwg");
                string path = System.IO.Path.Combine(dir, file);
                if (!OutputSupport.CheckTarget(path, overwrite, planned, issues, ctx.FieldPath(args, "fileName"))) { ok = false; continue; }
                plan.Files.Add(new OutFile { View = v, Path = path, Base = System.IO.Path.GetFileNameWithoutExtension(file) });
            }
            return ok ? plan : null;
        }

        private static IList<string> SafeSetupNames(Document doc)
        {
            try { return BaseExportOptions.GetPredefinedSetupNames(doc).ToList(); }
            catch (Exception) { return new List<string>(); }
        }

        private static DWGExportOptions? LoadSetup(Document doc, string name)
        {
            try { return DWGExportOptions.GetPredefinedOptions(doc, name); }
            catch (Exception) { return null; }
        }

        private static string? ActiveSetupName(Document doc)
        {
            try { return ExportDWGSettings.GetActivePredefinedSettings(doc)?.Name; }
            catch (Exception) { return null; }
        }

        private static DWGExportOptions Options(Document doc, OpArgs args)
        {
            string? setup = args.GetStringOrNull("setup");
            DWGExportOptions? opts = setup != null ? LoadSetup(doc, setup) : null;
            if (setup != null && opts is null) throw new OpFailedException("validation_failed", "no DWG export setup named '" + setup + "'");
            if (opts is null)
            {
                try { opts = ExportDWGSettings.GetActivePredefinedSettings(doc)?.GetDWGExportOptions(); }
                catch (Exception) { opts = null; }
            }
            opts ??= new DWGExportOptions();
            opts.MergedViews = args.GetBool("mergedViews");
            return opts;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plan = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            bool overwrite = args.GetBool("overwrite");
            var paths = new List<string>();
            var allCompanions = new List<string>();
            var details = new JsonArray();
            string? stage = null;
            foreach (var f in plan.Files)
            {
                string label = f.View is ViewSheet s ? "sheet '" + s.SheetNumber + "'" : "view '" + f.View.Name + "'";
                OutputSupport.RequireNoTransaction(ctx, "DWG export of " + label);
                string dir = System.IO.Path.GetDirectoryName(f.Path)!;
                stage ??= ctx.StageFolder(dir);
                string? scratch = null;
                var companions = new List<string>();
                try
                {
                    string sc = OutputSupport.NewScratch(stage);
                    scratch = sc;
                    var opts = Options(ctx.Doc, args);
                    var ids = new List<ElementId> { f.View.Id };
                    bool exported = OutputSupport.Guard(ctx, "DWG export of " + label, () => ctx.Doc.Export(sc, f.Base, ids, opts));
                    if (!exported) throw new OpFailedException(OutputSupport.Failed, "Revit reported that exporting " + label + " to DWG failed");
                    var produced = Directory.GetFiles(sc);
                    var dwgs = produced.Where(p => p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)).ToList();
                    string? main = dwgs.FirstOrDefault(p => string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), f.Base, StringComparison.OrdinalIgnoreCase))
                                   ?? (dwgs.Count == 1 ? dwgs[0] : null);
                    if (main is null)
                        throw new OpFailedException(OutputSupport.Failed, "Revit wrote " + dwgs.Count + " DWG file(s) for " + label + " and none is named '" + f.Base + ".dwg'",
                            new JsonObject().Add("produced", JsonArray.FromStrings(produced.Select(System.IO.Path.GetFileName).Select(x => x ?? ""))));
                    // Refuse before staging anything; the output folder itself is only written after commit.
                    var others = produced.Where(p => !string.Equals(p, main, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!overwrite)
                    {
                        var clash = others.Select(p => System.IO.Path.Combine(dir, System.IO.Path.GetFileName(p))).Where(File.Exists).ToList();
                        if (clash.Count > 0)
                            throw new OpFailedException(OutputSupport.Failed, "DWG export of " + label + " also wrote " + clash.Count + " file(s) that already exist in the output folder; set overwrite:true to replace them",
                                new JsonObject().Add("existing", JsonArray.FromStrings(clash)));
                    }
                    OutputSupport.Stage(ctx, stage, main, f.Path, overwrite);
                    foreach (var p in others)
                    {
                        string target = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(p));
                        OutputSupport.Stage(ctx, stage, p, target, overwrite);
                        companions.Add(target);
                        allCompanions.Add(target);
                    }
                }
                finally { OutputSupport.DeleteScratch(scratch); }
                paths.Add(f.Path);
                details.Add(new JsonObject().Add("path", f.Path).Add("view", ElementIds.ToWire(f.View.Id)).Add("companions", JsonArray.FromStrings(companions)));
                if (!ctx.IsRehearsal) ctx.AddArtifact("dwg", f.Path, f.View is ViewSheet sh ? "sheet:" + sh.SheetNumber : ElementIds.ToWire(f.View.Id));
            }
            result.Outputs.Add("files", JsonArray.FromStrings(paths));
            result.Outputs.Add("companions", JsonArray.FromStrings(allCompanions.Distinct(StringComparer.OrdinalIgnoreCase)));
            // Not a schema output; carried in readback so the companions can be checked and reported.
            result.Readback.Add("setup", plan.SetupName);
            result.Readback.Add("written", details);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var written = result.Readback["written"] as JsonArray ?? new JsonArray();
            var arr = new JsonArray();
            foreach (var w in written.OfType<JsonObject>())
            {
                string path = w.GetStringOrNull("path") ?? "";
                long bytes = OutputSupport.Size(ctx.ActualPath(path));
                var o = new JsonObject().Add("path", path).Add("bytes", bytes);
                arr.Add(o);
                if (bytes <= 0) { check.Fail("file " + path + " is missing or empty"); continue; }
                string? version = OutputSupport.DwgVersion(OutputSupport.Head(ctx.ActualPath(path), 16));
                if (version is null) check.Fail("file " + path + " does not start with a DWG version string");
                else o.Add("dwgVersion", version);
                var comp = new JsonArray();
                foreach (var c in (w["companions"] as JsonArray ?? new JsonArray()).OfType<JsonString>())
                {
                    long cb = OutputSupport.Size(ctx.ActualPath(c.Value));
                    comp.Add(new JsonObject().Add("path", c.Value).Add("bytes", cb));
                    if (cb <= 0) check.Fail("companion file " + c.Value + " is missing or empty");
                }
                o.Add("companions", comp);
            }
            int expected = OutputSupport.RequestedCount(args);
            if (arr.Count != expected) check.Fail(arr.Count + " DWG file(s) written for " + expected + " item(s)");
            result.Readback.Remove("written");
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("files", arr);
            OutputSupport.NoteStaging(ctx, result);
            check.Apply(result);
        }
    }
}
