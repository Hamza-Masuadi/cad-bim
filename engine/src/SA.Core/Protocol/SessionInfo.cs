using System;
using System.Globalization;
using SA.Core.Json;

namespace SA.Core.Protocol
{
    /// <summary>
    /// The discovery file an engine writes to <c>%LOCALAPPDATA%\CadBim\sessions\&lt;app&gt;-&lt;pid&gt;.json</c>
    /// on start and deletes on shutdown.
    /// </summary>
    public sealed class SessionInfo
    {
        public SessionInfo(string app, int processId, string pipeName, int releaseYear, string productVersion, DateTimeOffset startTime, string? engineVersion = null)
        {
            if (!EngineApps.IsKnown(app)) throw new ArgumentException("Unknown engine app '" + app + "'.", nameof(app));
            App = app;
            ProcessId = processId;
            PipeName = pipeName;
            ReleaseYear = releaseYear;
            ProductVersion = productVersion ?? "";
            StartTime = startTime;
            EngineVersion = engineVersion;
        }

        public string App { get; }
        public int ProcessId { get; }
        public string PipeName { get; }
        public int ReleaseYear { get; }
        public string ProductVersion { get; }
        public DateTimeOffset StartTime { get; }
        public string? EngineVersion { get; }

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Add("pipeName", PipeName)
                .Add("app", App)
                .Add("releaseYear", ReleaseYear)
                .Add("productVersion", ProductVersion)
                .Add("processId", ProcessId)
                .Add("startTime", StartTime.ToString("o", CultureInfo.InvariantCulture));
            if (EngineVersion != null) o.Add("engineVersion", EngineVersion);
            return o;
        }

        public static SessionInfo Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            string startText = c.GetString("startTime");
            if (!DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start))
                throw c.Required("startTime").Fail("not an ISO 8601 date-time");
            string app = c.GetString("app");
            if (!EngineApps.IsKnown(app)) throw c.Required("app").Fail("expected 'acad' or 'revit'");
            return new SessionInfo(app, c.GetInt32("processId"), c.GetNonEmptyString("pipeName"), c.GetInt32("releaseYear"),
                c.GetString("productVersion", ""), start, c.GetStringOrNull("engineVersion"));
        }
    }
}
