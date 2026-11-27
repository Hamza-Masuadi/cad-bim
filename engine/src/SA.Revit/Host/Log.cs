using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SA.Core.Protocol;

namespace SA.Revit.Host
{
    /// <summary>
    /// Rolling log at %LOCALAPPDATA%\CadBim\logs\revit-&lt;pid&gt;.log, rolled to .1 ... .4 at 4 MB.
    /// Never throws: logging failures are swallowed so they can never reach Revit.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 4L * 1024 * 1024;
        private const int Keep = 4;
        private static readonly object Gate = new object();
        private static string? _path;

        public static string Folder => Path.Combine(SaPaths.Root(), "logs");

        public static string? FilePath => _path;

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                _path = Path.Combine(Folder, "revit-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".log");
                // One file set per process; drop sets older than 14 days so the folder does not grow without bound.
                foreach (var f in Directory.GetFiles(Folder, "revit-*.log*"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { _path = null; }
        }

        public static void Info(string message) => Write("INFO ", message, null);
        public static void Warn(string message) => Write("WARN ", message, null);
        public static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

        private static void Write(string level, string message, Exception? e)
        {
            try
            {
                var p = _path;
                if (p is null) return;
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
                  .Append(' ').Append(level).Append(" [").Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ")
                  .Append(message);
                if (e != null) sb.Append(Environment.NewLine).Append(e);
                sb.Append(Environment.NewLine);
                lock (Gate)
                {
                    Roll(p);
                    File.AppendAllText(p, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }

        private static void Roll(string p)
        {
            var fi = new FileInfo(p);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            for (int i = Keep - 1; i >= 1; i--)
            {
                string from = p + "." + i, to = p + "." + (i + 1);
                if (File.Exists(to)) File.Delete(to);
                if (File.Exists(from)) File.Move(from, to);
            }
            string first = p + ".1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(p, first);
        }
    }
}
