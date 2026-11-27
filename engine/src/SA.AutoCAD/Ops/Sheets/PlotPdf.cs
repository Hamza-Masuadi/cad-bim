using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// plot.pdf: plots layouts to PDF through the publish engine (the capture service's mechanism), one file per layout
    /// or one multi-sheet file. Settings: the layout's own (or a named page setup), with plotDevice, paperSize and
    /// plotStyleTable overriding; plot type Layout at the page setup's scale (never fit-to-page on its own account).
    /// The settings travel as PlotInfo.OverrideSettings on a non-database copy, so no layout or page setup is written.
    ///
    /// Files: outputDir (else the job's output folder), names from fileName (placeholders {drawing}, {layout},
    /// {sheetNumber}, {sheetName}, {view}, {date}, {jobId}; sheetNumber and sheetName come from the title block
    /// attributes the standard maps to sheet.number and sheet.name, else the layout name), checked against the
    /// standard's file naming rule. An existing file fails validation unless overwrite is true.
    ///
    /// Rehearsal: the plot runs for real but into a temporary folder that is deleted after the readback, so a
    /// rehearsal neither leaves files behind nor blocks the commit with "file exists".
    ///
    /// A job holding plot.pdf may hold only plot.pdf ops (validation error plot_not_alone). Plotting switches the current
    /// layout and drives the plot pipeline while the job transaction is open; whether that pipeline sees objects that
    /// earlier ops created or opened for write in the same uncommitted transaction is not documented, so the engine does
    /// not rely on it. Relax this only after an in-app test shows it works.
    /// </summary>
    internal sealed class PlotPdf : OpHandler<PlotPdf.Plan>
    {
        public override string Name => "plot.pdf";

        internal sealed class FilePlan
        {
            public string Path = "";
            public string FinalPath = "";
            public List<string> Layouts = new List<string>();
        }

        internal sealed class Plan
        {
            public List<string> Layouts = new List<string>();
            public List<FilePlan> Files = new List<FilePlan>();
            public string Device = "";
            public string? PageSetup;
            public string? Media;
            public string? StyleSheet;
            public string? TempDir;
            public Dictionary<string, SheetSettings> Before = new Dictionary<string, SheetSettings>(StringComparer.OrdinalIgnoreCase);
            public string CurrentLayoutBefore = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var others = ctx.Job.Ops.Where(o => o.Op != Name).Select(o => o.Id).ToList();
            if (others.Count > 0)
                OpContext.Error(issues, args, null, "plot_not_alone", "plot.pdf runs only in a job of plot.pdf ops: it switches layouts and plots while the job " +
                    "transaction is open, and whether the plot sees objects that earlier ops created or changed in that transaction is not established. " +
                    "Commit the other ops (" + string.Join(", ", others.Take(5)) + ") first, then plot in a job of its own");
            Build(ctx, args, issues);
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan
            {
                PageSetup = args.GetStringOrNull("pageSetup"),
                Media = args.GetStringOrNull("paperSize"),
                StyleSheet = args.GetStringOrNull("plotStyleTable"),
            };
            var device = PlotSupport.FindDevice(args.GetString("plotDevice"));
            if (device is null)
            {
                OpContext.Error(issues, args, "plotDevice", "plot_device_missing", "plotter configuration '" + args.GetString("plotDevice") + "' is not installed");
                return null;
            }
            if (device.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) < 0)
            {
                OpContext.Error(issues, args, "plotDevice", "plot_device_not_pdf", "'" + device + "' is not a PDF plotter configuration");
                return null;
            }
            p.Device = device;
            var media = PlotSupport.MediaFor(device);
            if (p.Media != null)
            {
                var hit = PlotSupport.FindMedia(media, p.Media);
                if (hit is null) { OpContext.Error(issues, args, "paperSize", "paper_size_missing", "'" + p.Media + "' is not a paper size of " + device); return null; }
                p.Media = hit;
            }
            if (p.StyleSheet != null)
            {
                var problem = PlotSupport.StyleProblem(ctx.Database, p.StyleSheet);
                if (problem != null) { OpContext.Error(issues, args, "plotStyleTable", "plot_style_invalid", problem); return null; }
            }

            string drawing = SheetDb.DrawingBaseName(ctx);
            string date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool combine = args.GetBool("combine");
            string pattern = args.GetStringOrNull("fileName") ?? (combine ? "{drawing}.pdf" : "{drawing}-{layout}.pdf");
            if (combine && ctx.IsValidating && (pattern.Contains("{layout}") || pattern.Contains("{sheetNumber}") || pattern.Contains("{sheetName}") || pattern.Contains("{view}")))
                OpContext.Warning(issues, args, "fileName", "combined_name", "with combine, layout placeholders take the first layout's values");
            string dir = args.GetStringOrNull("outputDir") ?? ctx.Job.OutputDir ?? SaPaths.JobOutput(ctx.Job.JobId);
            if (!Path.IsPathRooted(dir))
            {
                OpContext.Error(issues, args, "outputDir", "output_dir_invalid", "outputDir must be a full path");
                return null;
            }
            bool overwrite = args.GetBool("overwrite");
            bool ok = true;

            Db.Read(ctx, tr =>
            {
                var all = SheetDb.PaperLayouts(tr, ctx.Database);
                var chosen = new List<Layout>();
                if (args.Has("layouts"))
                {
                    foreach (var n in args.GetStringList("layouts"))
                    {
                        var l = all.FirstOrDefault(x => string.Equals(x.LayoutName, n, StringComparison.OrdinalIgnoreCase));
                        if (l != null) { chosen.Add(l); continue; }
                        if (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, n))
                            OpContext.Warning(issues, args, "layouts", "layout_planned", "layout '" + n + "' is created earlier in this job; its plot settings are checked at execution");
                        else { OpContext.Error(issues, args, "layouts", "layout_missing", "there is no layout named '" + n + "'"); ok = false; }
                    }
                }
                else chosen = all;
                if (chosen.Count == 0 && !args.Has("layouts")) { OpContext.Error(issues, args, "layouts", "no_layouts", "the drawing has no paper space layouts"); ok = false; return 0; }

                SheetSettings? named = null;
                if (p.PageSetup != null)
                {
                    named = SideDatabase.PageSetup(tr, ctx.Database, p.PageSetup);
                    if (named is null) { OpContext.Error(issues, args, "pageSetup", "page_setup_missing", "there is no paper space page setup named '" + p.PageSetup + "'"); ok = false; return 0; }
                }
                var names = new Dictionary<string, FilePlan>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in chosen)
                {
                    p.Layouts.Add(l.LayoutName);
                    p.Before[l.LayoutName] = SheetSettings.From(l);
                    var eff = named ?? SheetSettings.From(l);
                    string m = p.Media ?? eff.Media;
                    if (PlotSupport.FindMedia(media, m) is null)
                    {
                        OpContext.Error(issues, args, "paperSize", "paper_size_missing", "layout '" + l.LayoutName + "' uses paper '" + m + "', which " + device + " does not offer; give paperSize (the PDF should be the sheet's own size)");
                        ok = false;
                    }
                    string style = p.StyleSheet ?? eff.StyleSheet;
                    if (p.StyleSheet is null && style.Length > 0)
                    {
                        var problem = PlotSupport.StyleProblem(ctx.Database, style);
                        if (problem != null) { OpContext.Error(issues, args, "plotStyleTable", "plot_style_invalid", "layout '" + l.LayoutName + "': " + problem); ok = false; }
                    }
                    if (ctx.IsValidating && eff.UseStandardScale && eff.StdScale == StdScaleType.ScaleToFit)
                        OpContext.Warning(issues, args, "layouts", "scale_to_fit", "layout '" + l.LayoutName + "' plots scaled to fit, not at 100%; sheets should plot 1:1");
                    if (combine && p.Files.Count == 1) { p.Files[0].Layouts.Add(l.LayoutName); continue; }

                    var tb = TitleBlocks.Find(tr, ctx, l, null, out _);
                    string sheetNo = TitleBlocks.ValueOfSource(tr, ctx.Standard, tb, "sheet.number") ?? l.LayoutName;
                    string sheetName = TitleBlocks.ValueOfSource(tr, ctx.Standard, tb, "sheet.name") ?? l.LayoutName;
                    string file = pattern.Replace("{drawing}", drawing).Replace("{layout}", l.LayoutName).Replace("{sheetNumber}", sheetNo)
                        .Replace("{sheetName}", sheetName).Replace("{view}", l.LayoutName).Replace("{date}", date).Replace("{jobId}", ctx.Job.JobId);
                    if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) file += ".pdf";
                    if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                    {
                        OpContext.Error(issues, args, "fileName", "file_name_invalid", "'" + file + "' contains characters not allowed in file names");
                        ok = false;
                        continue;
                    }
                    if (ctx.Standard?.FileNaming != null)
                    {
                        var nr = ctx.Standard.ParseFileName(file);
                        if (!nr.Valid) { OpContext.Error(issues, args, "fileName", "file_name_rule", "'" + file + "' breaks the standard's file naming rule: " + nr.Explain()); ok = false; }
                    }
                    if (names.ContainsKey(file)) { OpContext.Error(issues, args, "fileName", "file_name_duplicate", "layouts '" + names[file].Layouts[0] + "' and '" + l.LayoutName + "' would both write '" + file + "'"); ok = false; continue; }
                    var fp = new FilePlan { FinalPath = Path.Combine(dir, file) };
                    fp.Layouts.Add(l.LayoutName);
                    names[file] = fp;
                    p.Files.Add(fp);
                    if (!overwrite && SheetDb.FileExists(fp.FinalPath))
                    {
                        OpContext.Error(issues, args, "overwrite", "file_exists", "'" + fp.FinalPath + "' exists; set overwrite true or use a new folder for this issue");
                        ok = false;
                    }
                }
                return 0;
            });
            return ok ? p : null;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the plot could not be planned.");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            p.CurrentLayoutBefore = LayoutManager.Current.CurrentLayout;
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            if (rehearse)
            {
                p.TempDir = Path.Combine(Path.GetTempPath(), "SA-rehearse", Safe(ctx.Job.JobId), Safe(ctx.CurrentOp.Id));
                if (Directory.Exists(p.TempDir)) Directory.Delete(p.TempDir, true);
                Directory.CreateDirectory(p.TempDir);
            }
            ctx.SetSysVar("BACKGROUNDPLOT", 0);

            ObjectId namedId = p.PageSetup != null ? SideDatabase.PageSetupId(tr, db, p.PageSetup) : ObjectId.Null;
            foreach (var f in p.Files)
            {
                f.Path = rehearse ? Path.Combine(p.TempDir!, Path.GetFileName(f.FinalPath)) : f.FinalPath;
                var dir = Path.GetDirectoryName(f.Path)!;
                Directory.CreateDirectory(dir);
                if (File.Exists(f.Path)) File.Delete(f.Path); // overwrite was checked in validation; the rehearsal folder is private
                var pages = new List<PlotSupport.Page>();
                try
                {
                    foreach (var name in f.Layouts)
                    {
                        var lid = SheetDb.LayoutId(tr, db, name);
                        var layout = (Layout)tr.GetObject(lid, OpenMode.ForRead);
                        var ps = new PlotSettings(false);
                        ps.CopyFrom(namedId.IsNull ? (DBObject)layout : tr.GetObject(namedId, OpenMode.ForRead));
                        var src = SheetSettings.From(ps);
                        var psv = PlotSettingsValidator.Current;
                        string m = p.Media ?? src.Media;
                        psv.SetPlotConfigurationName(ps, p.Device, null);
                        psv.RefreshLists(ps);
                        psv.SetPlotConfigurationName(ps, p.Device, m);
                        psv.SetPlotType(ps, PlotType.Layout);
                        if (p.StyleSheet != null) psv.SetCurrentStyleSheet(ps, p.StyleSheet);
                        pages.Add(new PlotSupport.Page { LayoutId = lid, LayoutName = layout.LayoutName, Settings = ps });
                    }
                    PlotSupport.PlotFile(ctx.Document, pages, f.Path);
                }
                finally
                {
                    foreach (var pg in pages) pg.Settings.Dispose();
                }
            }
            return p;
        }

        private static string Safe(string s) => new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            bool ok = true;
            var files = new JsonArray();
            var tr = ctx.Transaction;
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            try
            {
                foreach (var f in p.Files)
                {
                    bool good = PlotSupport.IsPdf(f.Path, out long length);
                    if (!good) ok = Mismatch(result, "'" + f.Path + "' was not written, is empty, or is not a PDF");
                    files.Add(new JsonObject().Add("layout", f.Layouts.Count == 1 ? (JsonValue)f.Layouts[0] : JsonArray.FromStrings(f.Layouts))
                        .Add("path", rehearse ? f.FinalPath : f.Path).Add("pages", f.Layouts.Count).Add("bytes", length));
                    if (good && !rehearse) ctx.Report.Artifacts.Add(new Artifact("pdf", f.Path, "layout:" + string.Join(",", f.Layouts)));
                }
            }
            finally
            {
                if (p.TempDir != null)
                {
                    try { Directory.Delete(p.TempDir, true); }
                    catch (IOException e) { ctx.Warn("Could not remove the rehearsal plot folder '" + p.TempDir + "': " + e.Message); }
                    catch (UnauthorizedAccessException e) { ctx.Warn("Could not remove the rehearsal plot folder '" + p.TempDir + "': " + e.Message); }
                }
            }
            // The drawing is not changed by plotting: layouts keep their settings and the current layout is restored.
            foreach (var kv in p.Before)
            {
                var lid = SheetDb.LayoutId(tr, ctx.Database, kv.Key);
                var now = SheetSettings.From((Layout)tr.GetObject(lid, OpenMode.ForRead));
                if (!now.SameAs(kv.Value)) ok = Mismatch(result, "layout '" + kv.Key + "' plot settings changed during plotting");
            }
            string cur = LayoutManager.Current.CurrentLayout;
            if (!string.Equals(cur, p.CurrentLayoutBefore, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "the current layout is '" + cur + "', not '" + p.CurrentLayoutBefore + "' as before plotting");
            result.Outputs.Add("files", files);
            result.Readback.Add("files", files);
            result.Readback.Add("rehearsal", rehearse);
            if (rehearse) result.Readback.Add("note", "Rehearsal plotted into a temporary folder that was then deleted; paths are where commit will write.");
            return ok;
        }
    }
}
