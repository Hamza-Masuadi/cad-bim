using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    /// <summary>
    /// A report file in the job's output folder (as schedule.fromBlocks writes its CSV). A rehearsal writes to a
    /// temporary folder instead, never over the real file, and <see cref="Cleanup"/> removes it after the readback.
    /// </summary>
    internal sealed class OutputFile
    {
        private OutputFile(string path, bool json)
        {
            Path = path;
            IsJson = json;
        }

        /// <summary>The real path reported in outputs.</summary>
        public string Path { get; }
        public bool IsJson { get; }
        /// <summary>Where the file was actually written (Path, or the rehearsal copy).</summary>
        public string? Written { get; private set; }
        private string? _tempDir;

        /// <summary>Checks fileName (plain name, .csv or .json) and overwrite; null when absent or invalid.</summary>
        internal static OutputFile? Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            string? name = args.GetStringOrNull("fileName")?.Trim();
            if (name is null) return null;
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            if (ext != ".csv" && ext != ".json")
            {
                OpContext.Error(issues, args, "fileName", "file_name_invalid", "'" + name + "' must end in .csv or .json; the extension decides the format");
                return null;
            }
            if (!DraftDb.IsPlainFileName(name))
            {
                OpContext.Error(issues, args, "fileName", "file_name_invalid", "'" + name + "' must be a plain file name (no folders); it is written into the job's output folder");
                return null;
            }
            if (IsReservedDeviceName(name))
            {
                OpContext.Error(issues, args, "fileName", "file_name_invalid", "'" + name + "' is a Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9); use another file name");
                return null;
            }
            string path = System.IO.Path.Combine(DraftDb.OutputDir(ctx), name);
            if (!args.GetBool("overwrite") && File.Exists(path))
                OpContext.Error(issues, args, "overwrite", "file_exists", "'" + path + "' exists; set overwrite true or use another file name");
            return new OutputFile(path, ext == ".json");
        }

        internal void WriteCsv(OpContext ctx, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", headers.Select(Csv))).Append("\r\n");
            foreach (var r in rows) sb.Append(string.Join(",", r.Select(Csv))).Append("\r\n");
            Write(ctx, sb.ToString());
        }

        internal void WriteJson(OpContext ctx, JsonValue value) => Write(ctx, value.ToJson(true));

        private void Write(OpContext ctx, string text)
        {
            Written = Path;
            if (ctx.Mode == RunMode.Rehearse)
            {
                _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SA-rehearse", Safe(ctx.Job.JobId), Safe(ctx.CurrentOp.Id));
                if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
                Written = System.IO.Path.Combine(_tempDir, System.IO.Path.GetFileName(Path));
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Written)!);
            File.WriteAllText(Written, text, new UTF8Encoding(true));
        }

        /// <summary>Readback: the file exists and has at least <paramref name="minLines"/> lines (CSV) or parses (JSON).</summary>
        internal void Verify(JsonObject readback, List<string> mismatches, int minLines)
        {
            if (Written is null) return;
            if (!File.Exists(Written)) { mismatches.Add("the file " + Written + " was not written"); return; }
            readback.Add("fileBytes", new FileInfo(Written).Length);
            if (_tempDir != null) readback.Add("rehearsalPath", Written);
            if (IsJson)
            {
                try { JsonValue.Parse(File.ReadAllText(Written, Encoding.UTF8)); }
                catch (Exception e) { mismatches.Add("the JSON file does not parse: " + e.Message); }
                return;
            }
            int lines = File.ReadAllLines(Written, Encoding.UTF8).Length;
            readback.Add("fileLines", lines);
            // Quoted values with line breaks add physical lines; only a shortfall is a mismatch.
            if (lines < minLines) mismatches.Add("the CSV file has " + lines + " lines, expected at least " + minLines);
        }

        internal void Cleanup(OpContext ctx)
        {
            if (_tempDir is null) return;
            try { Directory.Delete(_tempDir, true); }
            catch (IOException e) { ctx.Warn("Could not remove the rehearsal folder '" + _tempDir + "': " + e.Message); }
            catch (UnauthorizedAccessException e) { ctx.Warn("Could not remove the rehearsal folder '" + _tempDir + "': " + e.Message); }
        }

        /// <summary>True when the part before the first dot is a DOS device name, which Windows opens as the device in any folder.</summary>
        internal static bool IsReservedDeviceName(string fileName)
        {
            string stem = fileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9';
        }

        private static string Safe(string s) => new string(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        internal static string Csv(string v) =>
            v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || v.StartsWith(" ", StringComparison.Ordinal) || v.EndsWith(" ", StringComparison.Ordinal)
                ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
