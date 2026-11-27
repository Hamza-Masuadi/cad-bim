using System;
using System.Collections.Generic;
using System.IO;

namespace SA.Core.Protocol
{
    public static class ProtocolInfo
    {
        /// <summary>The "v" field of every request and response.</summary>
        public const int Version = 1;

        /// <summary>Largest frame payload accepted or sent: 64 MB.</summary>
        public const int MaxMessageBytes = 64 * 1024 * 1024;
    }

    /// <summary>Method names, identical in both engines.</summary>
    public static class Methods
    {
        public const string Ping = "ping";
        public const string Snapshot = "snapshot";
        public const string Validate = "validate";
        public const string Run = "run";
        public const string Status = "status";
        public const string Audit = "audit";
        public const string Capture = "capture";
        public const string Probe = "probe";

        public static readonly IReadOnlyList<string> All = new[] { Ping, Snapshot, Validate, Run, Status, Audit, Capture, Probe };

        public static bool IsKnown(string method) => Array.IndexOf((string[])All, method) >= 0;
    }

    /// <summary>The two engine identifiers used in pipe names and session files.</summary>
    public static class EngineApps
    {
        public const string AutoCad = "acad";
        public const string Revit = "revit";

        public static bool IsKnown(string app) => app == AutoCad || app == Revit;

        /// <summary>Pipe name for an engine process: <c>sa-&lt;app&gt;-&lt;pid&gt;</c>.</summary>
        public static string PipeName(string app, int processId)
        {
            if (!IsKnown(app)) throw new ArgumentException("Unknown engine app '" + app + "'.", nameof(app));
            return "sa-" + app + "-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Maps the job "app" value (autocad, revit) to the engine id (acad, revit).</summary>
        public static string? FromJobApp(string jobApp) =>
            jobApp == "autocad" ? AutoCad : jobApp == "revit" ? Revit : null;

        public static string ToJobApp(string engineApp) =>
            engineApp == AutoCad ? "autocad" : engineApp == Revit ? "revit" : throw new ArgumentException("Unknown engine app '" + engineApp + "'.", nameof(engineApp));
    }

    /// <summary>Per-user folders under %LOCALAPPDATA%\CadBim (ARCHITECTURE.md sections 4, 5 and 8).</summary>
    public static class SaPaths
    {
        public static string Root(string? localAppData = null) =>
            Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadBim");

        public static string Sessions(string? localAppData = null) => Path.Combine(Root(localAppData), "sessions");

        public static string SessionFile(string app, int processId, string? localAppData = null) =>
            Path.Combine(Sessions(localAppData), app + "-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");

        public static string JobOutput(string jobId, string? localAppData = null) => Path.Combine(Root(localAppData), "jobs", jobId);

        public static string Ledger(string app, string? localAppData = null) => Path.Combine(Root(localAppData), "ledger", app);

        public static string LedgerFile(string app, string jobId, string? localAppData = null) => Path.Combine(Ledger(app, localAppData), jobId + ".json");
    }
}
