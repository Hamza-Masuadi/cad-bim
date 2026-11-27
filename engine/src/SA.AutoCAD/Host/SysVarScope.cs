using System;
using System.Collections.Generic;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Sets system variables for the duration of a job and restores every one, in reverse order, on Dispose (always called
    /// from a finally block by the job runner). The first value seen for a name is the one restored, however often it is set.
    /// System variables apply to the active document, which is why jobs require their document to be active.
    /// </summary>
    internal sealed class SysVarScope : IDisposable
    {
        private readonly List<KeyValuePair<string, object>> _saved = new List<KeyValuePair<string, object>>();
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>Restore problems, reported as job warnings.</summary>
        internal List<string> RestoreFailures { get; } = new List<string>();

        internal static object? Get(string name)
        {
            try { return AcApp.GetSystemVariable(name); }
            catch { return null; }
        }

        internal void Set(string name, object value)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SysVarScope));
            if (_names.Add(name))
            {
                var before = AcApp.GetSystemVariable(name);
                _saved.Add(new KeyValuePair<string, object>(name, before));
            }
            AcApp.SetSystemVariable(name, value);
        }

        /// <summary>
        /// Stops restoring <paramref name="name"/>: the job has written a value that must persist. A later
        /// <see cref="Set"/> records the then current value again.
        /// </summary>
        internal void Keep(string name)
        {
            if (!_names.Remove(name)) return;
            _saved.RemoveAll(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The value a name had before this scope first changed it, or null if it was not changed.</summary>
        internal object? Original(string name)
        {
            foreach (var kv in _saved)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        /// <summary>
        /// The arrowhead variables read back "" for the default closed-filled arrow, but AutoCAD takes a single period to
        /// select it again.
        /// </summary>
        private static object RestoreValue(string name, object value)
        {
            bool arrow = string.Equals(name, "DIMBLK", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "DIMBLK1", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "DIMBLK2", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "DIMLDRBLK", StringComparison.OrdinalIgnoreCase);
            return arrow && value is string s && s.Length == 0 ? "." : value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _saved.Count - 1; i >= 0; i--)
            {
                var kv = _saved[i];
                try
                {
                    AcApp.SetSystemVariable(kv.Key, RestoreValue(kv.Key, kv.Value));
                }
                catch (Exception e)
                {
                    string msg = "Could not restore system variable " + kv.Key + " to " + kv.Value + ": " + e.Message;
                    RestoreFailures.Add(msg);
                    Log.Error(msg);
                }
            }
        }
    }
}
