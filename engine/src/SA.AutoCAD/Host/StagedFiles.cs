using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>A file an op wrote into a staging folder, to be put at <see cref="Final"/> after the job commits.</summary>
    internal sealed class StagedFile
    {
        public string Staged = "";
        public string Final = "";
        public bool Overwrite;
        public string OpId = "";
        /// <summary>Set when the file could not be published: its staging folder is kept.</summary>
        public bool Kept;
    }

    /// <summary>
    /// Files written outside the drawing. An op writes into <see cref="Folder"/> and registers each file with
    /// <see cref="Add"/>. A commit publishes them after the drawing has committed: every precondition is checked before
    /// anything is moved, so a refusal leaves every final path as it was; a replaced file is moved aside first and
    /// restored if the move fails. A rehearsal or a failed job publishes nothing. The staging folders are always deleted,
    /// except one holding a file that could not be published.
    /// </summary>
    internal sealed class StagedFiles
    {
        private readonly List<StagedFile> _files = new List<StagedFile>();
        private readonly List<string> _folders = new List<string>();

        internal IReadOnlyList<StagedFile> Files => _files;

        /// <summary>
        /// A new, empty folder. Rehearse: under the temp folder, so a rehearsal never touches the output folder. Commit: a
        /// hidden folder inside <paramref name="finalDir"/> (same volume, so publishing is a rename).
        /// </summary>
        internal string Folder(string finalDir, RunMode mode, string jobId, string opId)
        {
            string tag = Guid.NewGuid().ToString("N").Substring(0, 12);
            string dir = mode == RunMode.Rehearse
                ? Path.Combine(Path.GetTempPath(), "SA-rehearse", Safe(jobId), Safe(opId) + "-" + tag)
                : Path.Combine(finalDir, ".sa-stage-" + tag);
            Directory.CreateDirectory(dir);
            _folders.Add(dir);
            return dir;
        }

        internal void Add(string staged, string final, bool overwrite, string opId) =>
            _files.Add(new StagedFile { Staged = staged, Final = final, Overwrite = overwrite, OpId = opId });

        /// <summary>After commit. A file that cannot be published makes the job failed (the drawing stays committed).</summary>
        internal void Publish(Report report)
        {
            if (_files.Count == 0) return;
            var problems = new List<KeyValuePair<StagedFile, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _files)
            {
                string? why = null;
                if (!File.Exists(s.Staged)) why = "the staged file is missing";
                else if (!seen.Add(s.Final) && !s.Overwrite) why = "another file of the job has the same path and overwrite is false";
                else if (File.Exists(s.Final) && !s.Overwrite) why = "the file appeared while the job ran and overwrite is false";
                if (why != null) problems.Add(new KeyValuePair<StagedFile, string>(s, why));
            }
            if (problems.Count > 0)
            {
                var refused = new HashSet<StagedFile>(problems.Select(p => p.Key));
                foreach (var s in _files.Where(f => !refused.Contains(f)))
                    problems.Add(new KeyValuePair<StagedFile, string>(s, "not put in place because another file of the job was refused"));
            }
            else
            {
                foreach (var s in _files)
                {
                    try { Move(s); }
                    catch (Exception e) { problems.Add(new KeyValuePair<StagedFile, string>(s, e.GetType().Name + ": " + e.Message)); }
                }
            }
            if (problems.Count == 0) return;
            foreach (var p in problems) p.Key.Kept = true;
            var unpublished = new HashSet<string>(problems.Select(p => p.Key.Final), StringComparer.OrdinalIgnoreCase);
            report.Artifacts.RemoveAll(a => unpublished.Contains(a.Path));
            report.Status = JobStatus.Failed;
            report.FailedOpId ??= problems[0].Key.OpId;
            report.Error = new OpError(ErrorCodes.OpFailed,
                "The job committed, but " + problems.Count + " output file(s) could not be put in place: " +
                string.Join("; ", problems.Take(3).Select(p => p.Key.Final + ": " + p.Value)) + ". The written files are kept at the staged paths listed in details.",
                new JsonObject().Add("files", new JsonArray(problems.Select(p => (JsonValue)new JsonObject()
                    .Add("opId", p.Key.OpId).Add("final", p.Key.Final).Add("staged", p.Key.Staged).Add("reason", p.Value)))));
        }

        private static void Move(StagedFile s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s.Final)!);
            if (!File.Exists(s.Final)) { File.Move(s.Staged, s.Final); return; }
            string aside = s.Final + ".sa-old-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.Move(s.Final, aside);
            try { File.Move(s.Staged, s.Final); }
            catch
            {
                try { File.Move(aside, s.Final); } catch (Exception e) { Log.Error("Could not restore " + s.Final + " from " + aside, e); }
                throw;
            }
            try { File.Delete(aside); } catch (Exception e) { Log.Warn("Could not delete " + aside + ": " + e.Message); }
        }

        /// <summary>Always, at the end of the job.</summary>
        internal void Discard(Report report)
        {
            var keep = _files.Where(s => s.Kept).Select(s => s.Staged).ToList();
            foreach (var dir in _folders)
            {
                if (keep.Any(k => k.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception e) { report.Warnings.Add(new ReportWarning("engine", "Could not delete the staging folder '" + dir + "': " + e.Message)); }
            }
            try
            {
                string temp = Path.GetTempPath();
                foreach (var d in _folders.Where(d => d.StartsWith(temp, StringComparison.OrdinalIgnoreCase)).Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (d != null && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch (Exception) { }
        }

        private static string Safe(string s) => new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }
}
