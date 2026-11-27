using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.Revit.Host
{
    /// <summary>
    /// Job ledger (ARCHITECTURE section 8): <c>%LOCALAPPDATA%\CadBim\ledger\revit\&lt;jobId&gt;.json</c>.
    ///
    /// A commit-mode job writes a "running" record just before its transaction group starts and its report when it
    /// ends, so its id is used up even if Revit dies mid-job. A rehearsal writes <c>rehearse\&lt;jobId&gt;.json</c> (a subfolder, so no job id can name it) and
    /// does not use up the id, so the usual sequence "rehearse, then commit the same job" works. A job refused
    /// before it started (schema, validation, identity, ownership, busy) writes nothing.
    /// Nothing is ever written into the model.
    /// </summary>
    internal sealed class JobLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, JsonObject> _memory = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private const int MemoryCap = 200;

        public string Folder => SaPaths.Ledger(EngineApps.Revit);

        private string MainFile(string jobId) => SaPaths.LedgerFile(EngineApps.Revit, jobId);
        private string RehearseFile(string jobId) => Path.Combine(Folder, "rehearse", jobId + ".json");

        /// <summary>True when a commit-mode run of this id has started or finished in any session.</summary>
        public bool IsUsed(string jobId, out JsonObject? record)
        {
            lock (_gate)
            {
                if (_memory.TryGetValue(jobId, out var m) && m.GetStringOrNull("mode") == "commit") { record = m; return true; }
            }
            record = Read(MainFile(jobId));
            return record != null || File.Exists(MainFile(jobId));
        }

        public void MarkRunning(string jobId, string mode)
        {
            var o = new JsonObject().Add("jobId", jobId).Add("status", "running").Add("mode", mode)
                .Add("startedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .Add("processId", System.Diagnostics.Process.GetCurrentProcess().Id);
            Write(mode == "commit" ? MainFile(jobId) : RehearseFile(jobId), o);
        }

        public void Finish(string jobId, string mode, JsonObject report)
        {
            lock (_gate)
            {
                if (!_memory.ContainsKey(jobId)) _order.Enqueue(jobId);
                _memory[jobId] = report;
                while (_order.Count > MemoryCap) _memory.Remove(_order.Dequeue());
            }
            Write(mode == "commit" ? MainFile(jobId) : RehearseFile(jobId), report);
        }

        /// <summary>The newest record for the id (commit before rehearsal), or null.</summary>
        public JsonObject? Lookup(string jobId)
        {
            lock (_gate)
            {
                if (_memory.TryGetValue(jobId, out var m) && m.GetStringOrNull("mode") == "commit") return m;
            }
            var main = Read(MainFile(jobId));
            if (main != null) return main;
            lock (_gate)
            {
                if (_memory.TryGetValue(jobId, out var m)) return m;
            }
            return Read(RehearseFile(jobId));
        }

        private static JsonObject? Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonValue.Parse(File.ReadAllBytes(path)) as JsonObject;
            }
            catch (Exception e)
            {
                Log.Warn("Unreadable ledger file " + path + ": " + e.Message);
                return null;
            }
        }

        private static void Write(string path, JsonObject o)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, o.ToJson(true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Log.Error("Could not write ledger file " + path, e); }
        }
    }
}
