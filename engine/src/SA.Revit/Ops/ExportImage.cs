using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Capture;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    /// <summary>
    /// export.image: export views or sheets to PNG or JPEG files. Changes nothing in the model, so no transaction
    /// is opened (ModifiesModel is false). Files are staged and put in place when the job commits; a rehearsal
    /// exports into a temporary folder that is deleted after readback (see OutputSupport).
    /// Readback opens each file and checks it exists, is not empty, and has the requested pixel width (±2 px).
    /// File names: fileName pattern with the placeholders of OutputSupport.Expand ({view}, {sheetNumber}, {sheetName},
    /// {revision}, {viewType}, {drawing}, {jobId}, {date}); default "{view}". An unknown placeholder is refused. A
    /// trailing .png, .jpg or .jpeg in the pattern is replaced by the format's extension.
    /// </summary>
    public sealed class ExportImage : OpHandler
    {
        public override string Name => "export.image";
        public override bool ModifiesModel => false;

        private sealed class Target
        {
            public View View = null!;
            public string Path = "";
        }

        private static List<Target>? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            bool ok = true;
            int width = args.GetInt("pixelWidth");
            if (width < CaptureService.MinPixels || width > CaptureService.MaxPixels)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "pixelWidth"), RevitIssueCodes.Invalid, "pixelWidth must be " + CaptureService.MinPixels + " to " + CaptureService.MaxPixels);
                ok = false;
            }
            string dir = args.GetStringOrNull("outputDir") ?? ctx.OutputDir;
            if (!Path.IsPathRooted(dir)) { OpContext.Error(issues, ctx.FieldPath(args, "outputDir"), RevitIssueCodes.Invalid, "outputDir must be an absolute path"); ok = false; }
            string ext = args.GetString("format") == "jpg" ? ".jpg" : ".png";
            string pattern = args.GetStringOrNull("fileName") ?? "{view}";
            var unknown = Output.OutputSupport.UnknownPlaceholders(pattern);
            if (unknown.Count > 0)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid,
                    "unknown placeholder(s) " + string.Join(", ", unknown.Select(x => "{" + x + "}")) + "; use " + string.Join(", ", Output.OutputSupport.Placeholders.Select(x => "{" + x + "}")));
                ok = false;
            }
            bool overwrite = args.GetBool("overwrite");
            var raw = Views(args);
            var list = new List<Target>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < raw.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "views"), i);
                if (OpContext.IsPending(raw[i])) continue;
                var f = Lookup.View(doc, raw[i]);
                if (!f.Ok) { OpContext.Error(issues, at, f.Code, f.Error!); ok = false; continue; }
                var v = f.Value!;
                string? why = CaptureService.CannotExport(v);
                if (why != null) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, why); ok = false; continue; }
                string name = Output.OutputSupport.Expand(pattern, doc, v, ctx.JobId).Trim();
                foreach (var known in new[] { ".png", ".jpg", ".jpeg" })
                    if (name.EndsWith(known, StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - known.Length); break; }
                string path = Path.Combine(dir, Output.OutputSupport.FileName(name, ext));
                if (!paths.Add(path)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "two views would write the same file " + path + "; use a fileName pattern with {view}"); ok = false; continue; }
                if (File.Exists(path) && !overwrite) { OpContext.Error(issues, at, RevitIssueCodes.Exists, path + " exists; set overwrite:true to replace it"); ok = false; continue; }
                list.Add(new Target { View = v, Path = path });
            }
            return ok ? list : null;
        }

        /// <summary>The views field as a list; a whole-field reference may resolve to one result object.</summary>
        private static JsonArray Views(OpArgs args)
        {
            var value = args.Raw["views"];
            if (value is JsonArray a) return a;
            return value is null || value.IsNull ? new JsonArray() : new JsonArray(value);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var targets = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            bool jpeg = args.GetString("format") == "jpg";
            int width = args.GetInt("pixelWidth");
            bool overwrite = args.GetBool("overwrite");
            var files = new JsonArray();
            string? stage = null;
            foreach (var t in targets)
            {
                if (File.Exists(t.Path) && !overwrite)
                    throw new OpFailedException("export_failed", t.Path + " appeared while exporting and overwrite is false; nothing was replaced");
                stage ??= ctx.StageFolder(Path.GetDirectoryName(t.Path)!);
                string staged = Path.Combine(stage, Path.GetFileName(t.Path));
                try { CaptureService.ExportImage(ctx.Doc, t.View, staged, jpeg, width, false); }
                catch (Exception e) { throw new OpFailedException("export_failed", "exporting '" + t.View.Name + "': " + e.Message); }
                ctx.StageFile(staged, t.Path, overwrite);
                files.Add(new JsonObject().Add("view", ElementIds.ToWire(t.View.Id)).Add("name", t.View.Name).Add("path", t.Path));
                if (!ctx.IsRehearsal) ctx.AddArtifact(jpeg ? "jpg" : "png", t.Path, ElementIds.ToWire(t.View.Id));
            }
            result.Outputs.Add("files", files);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string kind = args.GetString("format") == "jpg" ? "jpg" : "png";
            int width = args.GetInt("pixelWidth");
            var arr = new JsonArray();
            var files = result.Outputs["files"] as JsonArray ?? new JsonArray();
            foreach (var f in files.OfType<JsonObject>())
            {
                string path = f.GetStringOrNull("path") ?? "";
                var r = CaptureService.Inspect(ctx.ActualPath(path), kind);
                var o = r.ToJson().Set("path", path).Add("view", f.GetStringOrNull("name") ?? "");
                arr.Add(o);
                if (r.Bytes <= 0) { check.Fail("file " + path + " is missing or empty"); continue; }
                if (!r.Width.HasValue) check.Fail("file " + path + " is not a readable " + kind);
                else if (Math.Abs(r.Width.Value - width) > 2) check.Fail("file " + path + " is " + r.Width.Value + " px wide, requested " + width);
            }
            int expected = Views(args).Count;
            if (files.Count != expected) check.Fail(files.Count + " file(s) written for " + expected + " view(s)");
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("files", arr);
            Output.OutputSupport.NoteStaging(ctx, result);
            check.Apply(result);
        }
    }
}
