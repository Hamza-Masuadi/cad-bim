using System;
using System.Globalization;
using System.IO;
using System.Text;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Rolling log under %LOCALAPPDATA%\CadBim\logs\acad-&lt;pid&gt;.log. When the file passes 2 MB it is renamed
    /// to .1 (older files shift to .2 ... .4) and a new one is started. Logging never throws.
    /// </summary>
    internal static class Log
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private const int Keep = 4;
        private static readonly object Gate = new object();
        private static string? _path;

        internal static string Folder => Path.Combine(SaPaths.Root(), "logs");

        private static string FilePath =>
            _path ??= Path.Combine(Folder, "acad-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".log");

        internal static void Info(string message) => Write("INFO ", message, null);

        internal static void Warn(string message) => Write("WARN ", message, null);

        internal static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

        private static void Write(string level, string message, Exception? e)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                  .Append(' ').Append(level)
                  .Append(" [").Append(System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ")
                  .Append(message);
                if (e != null) sb.Append(Environment.NewLine).Append(e);
                sb.Append(Environment.NewLine);
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    string path = FilePath;
                    Roll(path);
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never take AutoCAD down.
            }
        }

        private static void Roll(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            for (int i = Keep - 1; i >= 1; i--)
            {
                string from = path + "." + i.ToString(CultureInfo.InvariantCulture);
                string to = path + "." + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (File.Exists(to)) File.Delete(to);
                if (File.Exists(from)) File.Move(from, to);
            }
            string first = path + ".1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(path, first);
        }
    }
}
