using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Output;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs.Support
{
    /// <summary>
    /// The output path of a single-file data export, the way the wave-1 exports plan theirs: outputDir (absolute) or
    /// the job's output folder, a fileName pattern with only the listed placeholders, a safe file name, and an
    /// existing file refused unless overwrite. Files are written into the op's staging folder and published by the
    /// runner after the job (OpContext.StageFolder / StageFile); readback reads them at OpContext.ActualPath.
    /// </summary>
    internal static class FileTarget
    {
        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// The final path. A fileName extension is kept only when it is one of <paramref name="extensions"/> (so a data
        /// file is never written as .exe or .bat); otherwise <paramref name="defaultExt"/> is added.
        /// </summary>
        public static string? Plan(OpContext ctx, OpArgs args, List<Issue> issues, string pattern, string[] allowed, View? view, string defaultExt, params string[] extensions)
        {
            string? dir = OutputSupport.OutputDir(ctx, args, issues);
            var unknown = OutputSupport.UnknownPlaceholders(pattern);
            var bad = System.Text.RegularExpressions.Regex.Matches(pattern, @"\{([A-Za-z]+)\}").Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value).Where(n => !allowed.Contains(n, StringComparer.Ordinal)).Distinct().ToList();
            if (unknown.Count > 0 || bad.Count > 0)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "fileName"), RevitIssueCodes.Invalid,
                    "placeholder(s) " + string.Join(", ", unknown.Union(bad).Select(u => "{" + u + "}")) + " cannot be used here; use " + string.Join(", ", allowed.Select(a => "{" + a + "}")));
                return null;
            }
            if (dir is null) return null;
            string expanded = OutputSupport.Expand(pattern, ctx.Doc, view, ctx.JobId).Trim();
            string ext = Path.GetExtension(expanded);
            if (!extensions.Concat(new[] { defaultExt }).Contains(ext, StringComparer.OrdinalIgnoreCase)) ext = defaultExt;
            string name = OutputSupport.FileName(expanded, ext);
            string stem = name.Substring(0, name.Length - ext.Length).Split('.')[0].Trim();
            if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
            string path = Path.Combine(dir, name);
            if (!OutputSupport.CheckTarget(path, args.GetBool("overwrite"), new HashSet<string>(StringComparer.OrdinalIgnoreCase), issues, ctx.FieldPath(args, "fileName"))) return null;
            return path;
        }

        /// <summary>Writes the staged copy of <paramref name="final"/> and registers it.</summary>
        public static void Write(OpContext ctx, string final, byte[] bytes, bool overwrite)
        {
            string stage = ctx.StageFolder(Path.GetDirectoryName(final)!);
            string staged = Path.Combine(stage, Path.GetFileName(final));
            File.WriteAllBytes(staged, bytes);
            ctx.StageFile(staged, final, overwrite);
        }

        public static void Artifact(OpContext ctx, string kind, string final, View? target)
        {
            if (!ctx.IsRehearsal) ctx.AddArtifact(kind, final, target is null ? null : ElementIds.ToWire(target.Id));
        }

        /// <summary>Readback fields every file op reports: path, bytes, and the staging note.</summary>
        public static byte[]? ReadBack(OpContext ctx, OpResult result, string final, ReadbackChecker check)
        {
            string actual = ctx.ActualPath(final);
            long bytes = OutputSupport.Size(actual);
            result.Readback.Set("path", final);
            result.Readback.Set("bytes", bytes);
            OutputSupport.NoteStaging(ctx, result);
            if (bytes <= 0) { check.Fail("file " + final + " is missing or empty"); return null; }
            return File.ReadAllBytes(actual);
        }
    }
}
