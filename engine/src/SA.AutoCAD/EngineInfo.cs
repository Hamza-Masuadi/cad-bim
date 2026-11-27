using System;
using System.Diagnostics;
using System.Reflection;
using SA.Core.Json;

namespace SA.AutoCAD
{
    /// <summary>Static facts about this engine build and the AutoCAD process, cached at start-up (safe on any thread).</summary>
    internal static class EngineInfo
    {
        internal const string App = SA.Core.Protocol.EngineApps.AutoCad;

        internal static int ProtocolVersion => SA.Core.Protocol.ProtocolInfo.Version;

        internal static string ApiAssemblyName =>
            typeof(Autodesk.AutoCAD.DatabaseServices.Database).Assembly.GetName().Name ?? "";

        /// <summary>The release this DLL was built for.</summary>
        internal static int ReleaseYear => Compat.ReleaseInfo.Year;

        internal static string EngineVersion =>
            typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(EngineInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        internal static int ProcessId { get; } = Process.GetCurrentProcess().Id;

        /// <summary>AutoCAD's own version string (Application.Version), set on the main thread at Initialize.</summary>
        internal static string ProductVersion { get; set; } = "";

        /// <summary>AutoCAD's major.minor (25.1 = 2026). Set at Initialize.</summary>
        internal static Version? ProductRelease { get; set; }

        internal static DateTimeOffset StartTime { get; } = DateTimeOffset.Now;

        internal static string RuntimeDescription => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        /// <summary>The release year the running AutoCAD reports, from its major.minor version, or 0 if unknown.</summary>
        internal static int RunningReleaseYear
        {
            get
            {
                var v = ProductRelease;
                if (v is null) return 0;
                if (v.Major == 24 && v.Minor == 3) return 2024;
                if (v.Major == 25 && v.Minor == 0) return 2025;
                if (v.Major == 25 && v.Minor == 1) return 2026;
                if (v.Major == 26 && v.Minor == 0) return 2027;
                return 0;
            }
        }

        internal static JsonObject Describe() => new JsonObject()
            .Add("app", App)
            .Add("engineVersion", EngineVersion)
            .Add("protocol", ProtocolVersion)
            .Add("builtForRelease", ReleaseYear)
            .Add("runningRelease", RunningReleaseYear)
            .Add("productVersion", ProductVersion)
            .Add("runtime", RuntimeDescription)
            .Add("processId", ProcessId);
    }
}
