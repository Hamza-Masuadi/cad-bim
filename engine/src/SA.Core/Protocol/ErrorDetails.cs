using SA.Core.Json;

namespace SA.Core.Protocol
{
    /// <summary>Agreed keys inside an error's <c>details</c> object.</summary>
    public static class ErrorDetails
    {
        /// <summary>
        /// On <see cref="ErrorCodes.Timeout"/> from an engine: false when the request never started (it waited too long for
        /// the application and was cancelled, so nothing ran and it is safe to send again); true when it started and is
        /// still running or its outcome is unknown (use <c>status</c>). A timeout without this key is the client's own.
        /// </summary>
        public const string Started = "started";

        /// <summary>Older spelling of <see cref="Started"/> with the same meaning (the AutoCAD engine writes "ran": false). Readers accept both.</summary>
        public const string Ran = "ran";

        /// <summary>The method that timed out (optional, with <see cref="Started"/>).</summary>
        public const string Method = "method";

        /// <summary>Details for an engine timeout: <c>{ "started": false|true, "method": ... }</c>.</summary>
        public static JsonObject Timeout(bool started, string? method = null)
        {
            var o = new JsonObject().Add(Started, started);
            if (method != null) o.Add(Method, method);
            return o;
        }

        /// <summary>Reads details.started (or details.ran): true or false when the engine said, null when absent (a client-side timeout).</summary>
        public static bool? StartedOf(JsonObject? details) =>
            details?[Started] is JsonBool b ? b.Value : details?[Ran] is JsonBool r ? r.Value : (bool?)null;
    }
}
