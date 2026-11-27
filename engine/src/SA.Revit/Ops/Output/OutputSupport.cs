using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Capture;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Output
{
    /// <summary>One view or sheet to export, in the order the job gave it.</summary>
    internal sealed class ExportItem
    {
        public View View = null!;
        public string Path = "";
        public bool IsSheet => View is ViewSheet;
    }

    /// <summary>
    /// Shared code for export.pdf, export.dwg and export.ifc.
    ///
    /// Files: every export goes into a fresh scratch folder inside the op's staging folder (<see cref="OpContext.StageFolder"/>),
    /// and the files Revit produced are renamed there to their final names and registered with <see cref="Stage"/>.
    /// Revit's own naming is therefore never relied on. Nothing in the output folder is touched while the job runs: a
    /// commit puts the staged files in place after the model change has committed, and a rehearsal stages into the
    /// temp folder, which is deleted after readback (outputs and readback name the paths commit will write). An
    /// existing file is replaced only when the op's <c>overwrite</c> is true; the check runs in Validate (both phases),
    /// when staging, and again before the runner moves the files.
    ///
    /// Refusals: Revit documents that Document.Export throws InvalidOperationException when "Exporting is not allowed
    /// in the current application mode" or "Export is temporarily disabled" (RevitAPI.xml, Document.Export overloads).
    /// A job whose ops are all exports runs without a transaction group; a job that also changes the model runs its
    /// exports inside the group, and it is not documented whether an open group counts as such a mode.
    /// <see cref="Guard{T}"/> turns any such refusal into the op error <c>export_refused</c>, with what to do instead.
    /// </summary>
    internal static class OutputSupport
    {
        public const string Refused = "export_refused";
        public const string Failed = "export_failed";

        private static readonly Regex Placeholder = new Regex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant);

        /// <summary>Placeholders accepted in fileName patterns.</summary>
        public static readonly string[] Placeholders = { "sheetNumber", "sheetName", "revision", "view", "viewType", "drawing", "jobId", "date" };

        /// <summary>The output folder: the op's outputDir, else the job's output folder. Must be absolute.</summary>
        public static string? OutputDir(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            string dir = args.GetStringOrNull("outputDir") ?? ctx.OutputDir;
            if (string.IsNullOrWhiteSpace(dir) || !System.IO.Path.IsPathRooted(dir))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "outputDir"), RevitIssueCodes.Invalid, "outputDir must be an absolute path");
                return null;
            }
            return dir;
        }

        /// <summary>Unknown placeholders in a pattern, so a typo such as {sheetnumber} is refused instead of written literally.</summary>
        public static List<string> UnknownPlaceholders(string pattern) =>
            Placeholder.Matches(pattern).Cast<Match>().Select(m => m.Groups[1].Value)
                .Where(n => !Placeholders.Contains(n, StringComparer.Ordinal)).Distinct().ToList();

        /// <summary>
        /// Expands a fileName pattern for one view or sheet (view null: the whole document, as for a combined PDF or
        /// IFC). {revision} is the sheet's "Current Revision" parameter (SHEET_CURRENT_REVISION), empty when none.
        /// </summary>
        public static string Expand(string pattern, Document doc, View? v, string jobId)
        {
            var sheet = v as ViewSheet;
            return Placeholder.Replace(pattern, m =>
            {
                switch (m.Groups[1].Value)
                {
                    case "sheetNumber": return sheet?.SheetNumber ?? "";
                    case "sheetName": return sheet?.Name ?? "";
                    case "revision": return sheet is null ? "" : (sheet.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "");
                    case "view": return v?.Name ?? "";
                    case "viewType": return v?.ViewType.ToString() ?? "";
                    case "drawing": return DocumentName(doc);
                    case "jobId": return jobId;
                    case "date": return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    default: return m.Value;
                }
            });
        }

        /// <summary>The document title without a file extension.</summary>
        public static string DocumentName(Document doc)
        {
            string t = doc.Title ?? "model";
            return t.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? t.Substring(0, t.Length - 4) : t;
        }

        /// <summary>A safe file name with exactly one extension <paramref name="ext"/> (".pdf").</summary>
        public static string FileName(string expanded, string ext)
        {
            string name = expanded.Trim();
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ext.Length);
            return CaptureService.SafeFileName(name) + ext;
        }

        /// <summary>
        /// The views and sheets named by the op: <c>sheets</c> (sheet numbers) first, then <c>views</c> (refs, names or
        /// sheet:&lt;number&gt;), in the order given. Pending references are skipped (they are checked in the execute
        /// phase). The same view twice is refused, because Revit's PDF export refuses duplicate views.
        /// </summary>
        public static List<View>? Items(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            bool ok = true;
            var list = new List<View>();
            var seen = new HashSet<long>();
            var sheets = args.Raw["sheets"] as JsonArray ?? new JsonArray();
            for (int i = 0; i < sheets.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "sheets"), i);
                if (!(sheets[i] is JsonString s)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "a sheet number (string) is expected"); ok = false; continue; }
                var sheet = Lookup.SheetByNumber(doc, s.Value);
                if (sheet is null)
                {
                    var numbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(x => x.SheetNumber).ToList();
                    OpContext.Error(issues, at, RevitIssueCodes.NotFound, "no sheet numbered '" + s.Value + "'" + Lookup.Suggest(s.Value, numbers));
                    ok = false; continue;
                }
                if (!Add(sheet, at)) ok = false;
            }
            var views = ViewsArg(args);
            for (int i = 0; i < views.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "views"), i);
                if (OpContext.IsPending(views[i])) continue;
                var f = Lookup.View(doc, views[i]);
                if (!f.Ok) { OpContext.Error(issues, at, f.Code, f.Error!); ok = false; continue; }
                if (!Add(f.Value!, at)) ok = false;
            }
            return ok ? list : null;

            bool Add(View v, string at)
            {
                string? why = CannotExport(v);
                if (why != null) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, why); return false; }
                if (!seen.Add(ElementIds.ToLong(v.Id))) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "'" + v.Name + "' is listed twice"); return false; }
                list.Add(v);
                return true;
            }
        }

        /// <summary>Number of view and sheet entries the job asked for, pending ones included.</summary>
        public static int RequestedCount(OpArgs args) => ((args.Raw["sheets"] as JsonArray)?.Count ?? 0) + ViewsArg(args).Count;

        /// <summary>The views field as a list; a whole-field reference may resolve to one result object.</summary>
        public static JsonArray ViewsArg(OpArgs args)
        {
            var value = args.Raw["views"];
            if (value is JsonArray a) return a;
            return value is null || value.IsNull ? new JsonArray() : new JsonArray(value);
        }

        /// <summary>Why a view or sheet cannot be exported to a printed format, or null.</summary>
        public static string? CannotExport(View v)
        {
            if (v is ViewSheet sh && sh.IsPlaceholder) return "sheet '" + sh.SheetNumber + "' is a placeholder sheet and cannot be exported";
            if (v is ViewSchedule vs && vs.IsTitleblockRevisionSchedule) return "'" + v.Name + "' is a title block revision schedule";
            if (v.IsTemplate) return "view '" + v.Name + "' is a template";
            bool printable;
            try { printable = v.CanBePrinted; } catch (Exception) { printable = false; }
            return printable ? null : "view '" + v.Name + "' (" + v.ViewType + ") cannot be printed or exported";
        }

        /// <summary>Checks one planned output path: unique within the op, and not existing unless overwrite.</summary>
        public static bool CheckTarget(string path, bool overwrite, HashSet<string> planned, List<Issue> issues, string at)
        {
            if (!planned.Add(path))
            {
                OpContext.Error(issues, at, RevitIssueCodes.Invalid, "two items would write the same file " + path + "; use a fileName pattern with {sheetNumber} or {view}");
                return false;
            }
            if (File.Exists(path) && !overwrite)
            {
                OpContext.Error(issues, at, RevitIssueCodes.Exists, path + " exists; set overwrite:true to replace it");
                return false;
            }
            return true;
        }

        public static string NewScratch(string dir)
        {
            Directory.CreateDirectory(dir);
            string scratch = System.IO.Path.Combine(dir, ".sa-export-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(scratch);
            return scratch;
        }

        public static void DeleteScratch(string? scratch)
        {
            if (scratch is null) return;
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
            catch (Exception e) { Host.Log.Warn("Could not delete scratch folder " + scratch + ": " + e.Message); }
        }

        /// <summary>
        /// Renames a file Revit produced to its final name inside the op's staging folder <paramref name="stage"/> and
        /// registers it for <paramref name="final"/>. Refuses when the final file exists and overwrite is false. Returns
        /// the staged path, which readback inspects.
        /// </summary>
        public static string Stage(OpContext ctx, string stage, string produced, string final, bool overwrite)
        {
            if (File.Exists(final) && !overwrite)
                throw new OpFailedException(Failed, final + " appeared while exporting and overwrite is false; nothing was replaced");
            string staged = System.IO.Path.Combine(stage, System.IO.Path.GetFileName(final));
            if (File.Exists(staged))
            {
                // Two exports of the op wrote the same file (a shared DWG companion): the later one wins, as on disk.
                if (!overwrite) throw new OpFailedException(Failed, "two outputs of the op write " + final + " and overwrite is false");
                File.Delete(staged);
                File.Move(produced, staged);
                return staged;
            }
            File.Move(produced, staged);
            ctx.StageFile(staged, final, overwrite);
            return staged;
        }

        /// <summary>Readback text for an export: where the files are, and in a rehearsal that nothing was written there.</summary>
        public static void NoteStaging(OpContext ctx, OpResult result)
        {
            result.Readback.Add("rehearsal", ctx.IsRehearsal);
            if (ctx.IsRehearsal)
                result.Readback.Add("note", "Rehearsal: the files were exported to a temporary folder, checked and deleted; the paths are where commit will write.");
        }

        /// <summary>
        /// Runs one Revit export call and turns Revit's refusals into clear op errors. <paramref name="what"/> names the
        /// export ("PDF export of 'A-101'").
        /// </summary>
        public static T Guard<T>(OpContext ctx, string what, Func<T> call)
        {
            try { return call(); }
            catch (OpFailedException) { throw; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException e)
            {
                throw new OpFailedException(Refused, what + " was refused by Revit: " + e.Message +
                    " Revit refuses exports in some application modes; in a job that also changes the model, exports run inside the job's transaction group, which may be such a mode.",
                    new JsonObject()
                        .Add("revitMessage", e.Message)
                        .Add("exception", e.GetType().FullName ?? "InvalidOperationException")
                        .Add("documentModifiable", ctx.Doc.IsModifiable)
                        .Add("hint", "Run the export as a job of its own (a job of export ops only runs without a transaction group), after the modelling job has committed. For PDF of a single view the capture method is an alternative."));
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException e)
            {
                throw new OpFailedException(Failed, what + ": Revit rejected the arguments: " + e.Message, new JsonObject().Add("revitMessage", e.Message));
            }
            catch (Exception e)
            {
                throw new OpFailedException(Failed, what + " failed: " + e.GetType().Name + ": " + e.Message, new JsonObject().Add("revitMessage", e.Message));
            }
        }

        /// <summary>For PDF and DWG export: refuses up front when a transaction is open, which the runner never does for these ops.</summary>
        public static void RequireNoTransaction(OpContext ctx, string what)
        {
            if (ctx.Doc.IsModifiable)
                throw new OpFailedException(Refused, what + ": the document is modifiable (a transaction is open), and Revit's view exports must run outside a transaction. This is an engine fault; nothing was exported.");
        }

        // ------------------------------------------------------------------ file inspection

        /// <summary>Size and first bytes of a file; Bytes is 0 when it does not exist.</summary>
        public static long Size(string path)
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : 0;
        }

        public static byte[] Head(string path, int max)
        {
            using (var fs = File.OpenRead(path))
            {
                var b = new byte[(int)Math.Min(max, fs.Length)];
                int n = 0;
                while (n < b.Length)
                {
                    int r = fs.Read(b, n, b.Length - n);
                    if (r <= 0) break;
                    n += r;
                }
                if (n < b.Length) Array.Resize(ref b, n);
                return b;
            }
        }

        /// <summary>PDFs up to this size are scanned for their page count; larger files report pages as unknown.</summary>
        public const long PdfScanLimit = 512L * 1024 * 1024;

        private static readonly Regex PagesCountA = new Regex(@"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex PagesCountB = new Regex(@"/Count\s+(\d+)[^>]*?/Type\s*/Pages\b", RegexOptions.CultureInvariant);
        private static readonly Regex PageObject = new Regex(@"/Type\s*/Page(?![A-Za-z])", RegexOptions.CultureInvariant);

        /// <summary>
        /// Page count of a PDF: the largest /Count of a /Type /Pages node (the root page tree counts every page), source
        /// "pageTree"; else the number of /Type /Page objects, source "pageObjects"; else null. A PDF whose page tree is
        /// inside compressed object streams gives null: the engine does not decompress.
        /// </summary>
        public static int? PdfPages(string path, out string source)
        {
            source = "unknown";
            long size = Size(path);
            if (size <= 0 || size > PdfScanLimit) return null;
            string text;
            try { text = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(path)); }
            catch (Exception) { return null; }
            int best = -1;
            foreach (Match m in PagesCountA.Matches(text)) best = Math.Max(best, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            foreach (Match m in PagesCountB.Matches(text)) best = Math.Max(best, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (best >= 0) { source = "pageTree"; return best; }
            int objects = PageObject.Matches(text).Count;
            if (objects > 0) { source = "pageObjects"; return objects; }
            return null;
        }

        public static bool LooksLikePdf(byte[] head) => head.Length >= 5 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F' && head[4] == '-';

        /// <summary>DWG files start with the version string "AC10xx" (AC1032 = 2018 format).</summary>
        public static string? DwgVersion(byte[] head)
        {
            if (head.Length < 6 || head[0] != 'A' || head[1] != 'C') return null;
            string v = Encoding.ASCII.GetString(head, 0, 6);
            return Regex.IsMatch(v, "^AC[0-9]{4}$") ? v : null;
        }

        private static readonly Regex ViewDefinition = new Regex(@"ViewDefinition\s*\[([^\]]*)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The model view definition named in an IFC header's FILE_DESCRIPTION ("ViewDefinition [ReferenceView]"), or null.</summary>
        public static string? IfcModelView(byte[] head)
        {
            var m = ViewDefinition.Match(Encoding.ASCII.GetString(head));
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static readonly Regex FileSchema = new Regex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']+)'", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The schema named in an IFC STEP file header (FILE_SCHEMA(('IFC4'))), or null when it is not a STEP file.</summary>
        public static string? IfcSchema(byte[] head)
        {
            string text = Encoding.ASCII.GetString(head);
            if (!text.TrimStart().StartsWith("ISO-10303-21", StringComparison.Ordinal)) return null;
            var m = FileSchema.Match(text);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
