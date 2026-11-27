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
    /// export.pdf: sheets (by number) and views (refs or names) to PDF, one file per item or one combined file.
    ///
    /// API: Document.Export(folder, IList&lt;ElementId&gt;, PDFExportOptions) (2022+). Each output file is one Export call
    /// with Combine = true and FileName set, into a scratch folder, so the file name is the engine's and Revit's
    /// naming rule (SetNamingRule) is never involved. Paper size comes from the sheet: PaperFormat =
    /// ExportPaperFormat.Default, documented as "Use Sheet Size"; for a set that holds views other than sheets Revit
    /// documents Letter paper with fit-to-page, and the op warns when a combined set mixes them.
    ///
    /// The model is not changed and no transaction is opened (ModifiesModel false). A refusal by Revit becomes the op
    /// error export_refused. Files are staged and put in place only when the job commits (see OutputSupport).
    ///
    /// File names: <c>fileName</c> with {sheetNumber}, {sheetName}, {revision} (current revision), {view},
    /// {viewType}, {drawing} (document title), {jobId}, {date}; ".pdf" is added when missing. Defaults: per sheet
    /// "{sheetNumber} - {sheetName}", per view "{view}", combined "{drawing}". A combined file of several items cannot
    /// use the per-item placeholders. Existing files are refused unless <c>overwrite</c>.
    ///
    /// Readback: each file exists, is not empty and starts with "%PDF-". Pages are counted from the file's page tree
    /// when it is readable without decompression; a page tree that disagrees with the number of items fails the op,
    /// and a count that could not be read is reported as null.
    /// </summary>
    public sealed class ExportPdf : OpHandler
    {
        public override string Name => "export.pdf";
        public override bool ModifiesModel => false;

        private static readonly string[] PerItem = { "sheetNumber", "sheetName", "revision", "view", "viewType" };

        private sealed class OutFile
        {
            public List<View> Views = new List<View>();
            public string Path = "";
        }

        private static List<OutFile>? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            bool ok = true;
            string? dir = OutputSupport.OutputDir(ctx, args, issues);
            if (dir is null) ok = false;
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

            bool combine = args.GetBool("combine");
            bool overwrite = args.GetBool("overwrite");
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<OutFile>();
            if (combine)
            {
                if (items.Count == 0) return files; // every item is still a pending reference
                bool several = OutputSupport.RequestedCount(args) > 1;
                if (pattern != null && several && PerItem.Any(p => pattern.Contains("{" + p + "}")))
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid,
                        "a combined PDF of several items has one name; per-item placeholders ({sheetNumber}, {sheetName}, {revision}, {view}, {viewType}) cannot be used; use {drawing}, {jobId}, {date} or text");
                    return null;
                }
                string name = OutputSupport.Expand(pattern ?? "{drawing}", ctx.Doc, several ? null : items[0], ctx.JobId);
                string path = System.IO.Path.Combine(dir, OutputSupport.FileName(name, ".pdf"));
                if (!OutputSupport.CheckTarget(path, overwrite, planned, issues, ctx.FieldPath(args, "fileName"))) return null;
                if (items.Any(v => v is ViewSheet) && items.Any(v => !(v is ViewSheet)))
                    OpContext.Warning(issues, ctx.FieldPath(args, "combine"), RevitIssueCodes.Unchecked,
                        "the combined set mixes sheets and views; Revit documents that such a set is printed on Letter paper, fit to page, so the sheets will not keep their sheet size. Export sheets and views as separate ops to keep sheet sizes.");
                files.Add(new OutFile { Views = items, Path = path });
                return files;
            }
            foreach (var v in items)
            {
                string pat = pattern ?? (v is ViewSheet ? "{sheetNumber} - {sheetName}" : "{view}");
                string path = System.IO.Path.Combine(dir, OutputSupport.FileName(OutputSupport.Expand(pat, ctx.Doc, v, ctx.JobId), ".pdf"));
                if (!OutputSupport.CheckTarget(path, overwrite, planned, issues, ctx.FieldPath(args, "fileName"))) { ok = false; continue; }
                files.Add(new OutFile { Views = new List<View> { v }, Path = path });
            }
            return ok ? files : null;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static PDFExportOptions Options(OpArgs args)
        {
            string colour = args.GetString("colour");
            string raster = args.GetString("rasterQuality");
            return new PDFExportOptions
            {
                Combine = true,
                FileName = "export",
                PaperFormat = ExportPaperFormat.Default,
                HideCropBoundaries = args.GetBool("hideCropBoundaries"),
                HideScopeBoxes = args.GetBool("hideScopeBoxes"),
                HideReferencePlane = args.GetBool("hideReferencePlanes"),
                ColorDepth = colour == "grayscale" ? ColorDepthType.GrayScale : colour == "black_line" ? ColorDepthType.BlackLine : ColorDepthType.Color,
                RasterQuality = raster == "low" ? RasterQualityType.Low : raster == "medium" ? RasterQualityType.Medium :
                                raster == "presentation" ? RasterQualityType.Presentation : RasterQualityType.High,
                StopOnError = true,
            };
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var files = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            bool overwrite = args.GetBool("overwrite");
            var outputs = new JsonArray();
            string? stage = null;
            foreach (var f in files)
            {
                string label = f.Views.Count == 1 ? Label(f.Views[0]) : f.Views.Count + " items";
                OutputSupport.RequireNoTransaction(ctx, "PDF export of " + label);
                stage ??= ctx.StageFolder(System.IO.Path.GetDirectoryName(f.Path)!);
                string? scratch = null;
                string staged;
                try
                {
                    string sc = OutputSupport.NewScratch(stage);
                    scratch = sc;
                    var ids = f.Views.Select(v => v.Id).ToList();
                    var opts = Options(args);
                    bool exported = OutputSupport.Guard(ctx, "PDF export of " + label, () => ctx.Doc.Export(sc, ids, opts));
                    var produced = Directory.GetFiles(sc, "*.pdf");
                    if (!exported) throw new OpFailedException(OutputSupport.Failed, "Revit reported that exporting " + label + " to PDF failed (StopOnError is set, so nothing partial is kept)");
                    if (produced.Length != 1) throw new OpFailedException(OutputSupport.Failed, "Revit wrote " + produced.Length + " PDF file(s) for " + label + "; expected one");
                    staged = OutputSupport.Stage(ctx, stage, produced[0], f.Path, overwrite);
                }
                finally { OutputSupport.DeleteScratch(scratch); }
                int? pages = OutputSupport.PdfPages(staged, out string source);
                var o = new JsonObject()
                    .Add("path", f.Path)
                    .Add("pages", pages.HasValue ? (JsonValue)pages.Value : JsonValue.Null)
                    .Add("items", JsonArray.FromStrings(f.Views.Select(v => ElementIds.ToWire(v.Id))));
                outputs.Add(o);
                if (!ctx.IsRehearsal) ctx.AddArtifact("pdf", f.Path, f.Views.Count == 1 ? Target(f.Views[0]) : null);
            }
            result.Outputs.Add("files", outputs);
        }

        private static string Label(View v) => v is ViewSheet s ? "sheet '" + s.SheetNumber + "'" : "view '" + v.Name + "'";
        private static string Target(View v) => v is ViewSheet s ? "sheet:" + s.SheetNumber : ElementIds.ToWire(v.Id);

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var files = result.Outputs["files"] as JsonArray ?? new JsonArray();
            var arr = new JsonArray();
            int totalPages = 0;
            bool allCounted = true;
            foreach (var f in files.OfType<JsonObject>())
            {
                string path = f.GetStringOrNull("path") ?? "";
                string actual = ctx.ActualPath(path);
                int expected = (f["items"] as JsonArray)?.Count ?? 0;
                long bytes = OutputSupport.Size(actual);
                var o = new JsonObject().Add("path", path).Add("bytes", bytes).Add("expectedPages", expected);
                arr.Add(o);
                if (bytes <= 0) { check.Fail("file " + path + " is missing or empty"); continue; }
                if (!OutputSupport.LooksLikePdf(OutputSupport.Head(actual, 16))) { check.Fail("file " + path + " is not a PDF"); continue; }
                int? pages = OutputSupport.PdfPages(actual, out string source);
                o.Add("pages", pages.HasValue ? (JsonValue)pages.Value : JsonValue.Null).Add("pagesSource", source);
                if (pages.HasValue) totalPages += pages.Value; else allCounted = false;
                if (pages.HasValue && source == "pageTree" && pages.Value != expected)
                    check.Fail("file " + path + " has " + pages.Value + " page(s); " + expected + " item(s) were exported into it");
            }
            int expectedFiles = args.GetBool("combine") ? 1 : OutputSupport.RequestedCount(args);
            if (files.Count != expectedFiles) check.Fail(files.Count + " PDF file(s) written; expected " + expectedFiles);
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("pages", allCounted ? (JsonValue)totalPages : JsonValue.Null);
            result.Readback.Add("files", arr);
            OutputSupport.NoteStaging(ctx, result);
            check.Apply(result);
        }
    }
}
