using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Validation;

namespace SA.AutoCAD.Host
{
    /// <summary>One ledger record: what ran, against which document state, and its report.</summary>
    internal sealed class LedgerEntry
    {
        public string JobId = "";
        public JobStatus Status;
        public RunMode Mode;
        public string JobHash = "";
        public string DocumentPath = "";
        public string FingerprintBefore = "";
        public string? FingerprintAfter;
        public bool AllowScript;
        public int ProcessId;
        public DateTimeOffset StartedAt;
        public DateTimeOffset? FinishedAt;
        public JsonObject? Report;

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Add("jobId", JobId)
                .Add("status", JobStatuses.ToWire(Status))
                .Add("mode", JobStatuses.ToWire(Mode))
                .Add("jobHash", JobHash)
                .Add("documentPath", DocumentPath)
                .Add("fingerprintBefore", FingerprintBefore)
                .Add("allowScript", AllowScript)
                .Add("processId", ProcessId)
                .Add("startedAt", StartedAt.ToString("o", CultureInfo.InvariantCulture));
            if (FingerprintAfter != null) o.Add("fingerprintAfter", FingerprintAfter);
            if (FinishedAt.HasValue) o.Add("finishedAt", FinishedAt.Value.ToString("o", CultureInfo.InvariantCulture));
            if (Report != null) o.Add("report", Report);
            return o;
        }

        public static LedgerEntry Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var e = new LedgerEntry
            {
                JobId = c.GetNonEmptyString("jobId"),
                Status = JobStatuses.Parse(c.GetString("status")),
                Mode = JobStatuses.ParseMode(c.GetString("mode")),
                JobHash = c.GetString("jobHash", ""),
                DocumentPath = c.GetString("documentPath", ""),
                FingerprintBefore = c.GetString("fingerprintBefore", ""),
                FingerprintAfter = c.GetStringOrNull("fingerprintAfter"),
                AllowScript = c.GetBool("allowScript", false),
                ProcessId = c.GetInt32("processId", 0),
            };
            if (DateTimeOffset.TryParse(c.GetString("startedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s)) e.StartedAt = s;
            if (DateTimeOffset.TryParse(c.GetString("finishedAt", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var f)) e.FinishedAt = f;
            e.Report = c.Optional("report")?.AsObject();
            return e;
        }
    }

    /// <summary>
    /// Job ledger (ARCHITECTURE section 8), outside the drawing: %LOCALAPPDATA%\CadBim\ledger\acad\&lt;jobId&gt;.json.
    ///
    /// Rules:
    /// - A commit-mode run writes a "running" record before its first change and the final record after commit or rollback.
    ///   Once a commit-mode record exists for a job id (any status), every later run of that id is refused with
    ///   duplicate_job. A failed commit made no change, but its id is still spent: the caller must use a new id.
    /// - Rehearsals are recorded under ledger\acad\rehearse\&lt;jobId&gt;.json. They do not spend the id (the usual flow
    ///   is rehearse, then commit the same file) and may be repeated.
    /// - The ledger folder is shared by every AutoCAD session of the user. A "running" record whose process is still alive
    ///   (same id, started before the job) reads back as running. One whose process is gone reads back as unknown: the
    ///   engine may have stopped mid-job, or the final record may not have been written after a commit, so the caller
    ///   must check the drawing.
    /// - The commit-mode "running" record is created with a create-only move, so two sessions cannot both claim one id.
    /// - <c>status</c> reads memory first, then the commit record, then the rehearsal record.
    /// </summary>
    internal sealed class JobLedger
    {
        private static readonly Regex SafeId = new Regex(JobValidator.JobIdPattern, RegexOptions.CultureInvariant);
        private readonly ConcurrentDictionary<string, LedgerEntry> _memory = new ConcurrentDictionary<string, LedgerEntry>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, LedgerEntry> _rehearsals = new ConcurrentDictionary<string, LedgerEntry>(StringComparer.Ordinal);
        private readonly string _folder;
        private readonly int _pid = Process.GetCurrentProcess().Id;

        internal JobLedger(string? folder = null)
        {
            _folder = folder ?? SaPaths.Ledger(EngineApps.AutoCad);
        }

        internal static bool IsSafeId(string? jobId) => jobId != null && SafeId.IsMatch(jobId);

        private string CommitFile(string jobId) => Path.Combine(_folder, jobId + ".json");

        private string RehearseFile(string jobId) => Path.Combine(_folder, "rehearse", jobId + ".json");

        /// <summary>The commit-mode record for a job id (memory or disk), or null.</summary>
        internal LedgerEntry? FindCommit(string jobId)
        {
            if (!IsSafeId(jobId)) return null;
            if (_memory.TryGetValue(jobId, out var m)) return m;
            return ReadFile(CommitFile(jobId));
        }

        internal LedgerEntry? FindRehearsal(string jobId)
        {
            if (!IsSafeId(jobId)) return null;
            if (_rehearsals.TryGetValue(jobId, out var m)) return m;
            return ReadFile(RehearseFile(jobId));
        }

        /// <summary>
        /// Status lookup: memory, commit record, rehearsal record. A "running" record of another live process stays running;
        /// one whose process is gone reads as unknown.
        /// </summary>
        internal LedgerEntry? Lookup(string jobId)
        {
            var e = FindCommit(jobId) ?? FindRehearsal(jobId);
            if (e is null) return null;
            if (e.Status == JobStatus.Running && !((_memory.ContainsKey(jobId) || _rehearsals.ContainsKey(jobId)) && e.ProcessId == _pid))
            {
                if (e.ProcessId != _pid && IsAlive(e.ProcessId, e.StartedAt)) return e;
                var stale = LedgerEntry.Parse(e.ToJson());
                stale.Status = JobStatus.Unknown;
                var r = new Report(jobId, JobStatus.Unknown, e.Mode)
                {
                    Error = new OpError(ErrorCodes.Internal,
                        "The engine (process " + e.ProcessId + ") left this job's record at 'running': it stopped during the job, or the job finished " +
                        "but its final record could not be written. The outcome is unknown. Check the drawing (and its undo list) before running the job " +
                        "again under a new id."),
                };
                stale.Report = r.ToJson();
                return stale;
            }
            return e;
        }

        /// <summary>True when process <paramref name="pid"/> runs and was started before <paramref name="startedAt"/> (not a reused id).</summary>
        private static bool IsAlive(int pid, DateTimeOffset startedAt)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return false;
                    return p.StartTime <= startedAt.LocalDateTime.AddSeconds(1);
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }

        /// <summary>Records the start of a job. Commit mode writes to disk before any change is made.</summary>
        internal void Begin(LedgerEntry entry)
        {
            if (entry.Mode == RunMode.Commit)
            {
                // Disk first: if the record cannot be written the job does not start and the id is not spent. Create-only, so
                // a record written meanwhile by another session refuses this run instead of being overwritten.
                WriteFile(CommitFile(entry.JobId), entry.ToJson(), createOnly: true);
                _memory[entry.JobId] = entry;
            }
            else
            {
                _rehearsals[entry.JobId] = entry;
            }
        }

        /// <summary>Records the end of a job: memory at once, disk with up to three attempts (a scanner may hold the file briefly).</summary>
        internal void Finish(LedgerEntry entry)
        {
            entry.FinishedAt = DateTimeOffset.Now;
            string path;
            if (entry.Mode == RunMode.Commit) { _memory[entry.JobId] = entry; path = CommitFile(entry.JobId); }
            else { _rehearsals[entry.JobId] = entry; path = RehearseFile(entry.JobId); }
            for (int attempt = 1; ; attempt++)
            {
                try { WriteFile(path, entry.ToJson()); return; }
                catch (EngineException) when (attempt < 3) { System.Threading.Thread.Sleep(100 * attempt * attempt); }
            }
        }

        /// <summary>A stable hash of the job as sent (canonical compact JSON), binding rehearsal approval to exact content.</summary>
        internal static string HashJob(Job job)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(job.ToJson().ToJson(false)));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static LedgerEntry? ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return LedgerEntry.Parse(JsonValue.Parse(File.ReadAllBytes(path)));
            }
            catch (Exception e)
            {
                Log.Warn("Unreadable ledger file '" + path + "': " + e.Message);
                // An unreadable commit record must still block re-running the id.
                return new LedgerEntry { JobId = Path.GetFileNameWithoutExtension(path), Status = JobStatus.Unknown, Mode = RunMode.Commit };
            }
        }

        private static void WriteFile(string path, JsonObject content, bool createOnly = false)
        {
            string? tmp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllBytes(tmp, content.ToUtf8Bytes(true));
                if (createOnly) File.Move(tmp, path);
                else if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (IOException) when (createOnly && File.Exists(path))
            {
                TryDelete(tmp);
                throw new EngineException(ErrorCodes.DuplicateJob, "Another run of job '" + Path.GetFileNameWithoutExtension(path) +
                    "' claimed this id first (possibly in another AutoCAD session); nothing was run. Use a new job id.");
            }
            catch (Exception e)
            {
                TryDelete(tmp);
                Log.Error("Could not write ledger file '" + path + "'.", e);
                throw new EngineException(ErrorCodes.Internal, "Could not write the job ledger (" + e.Message + "); refusing to run without a ledger record.");
            }
        }

        private static void TryDelete(string? path)
        {
            if (path is null) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
