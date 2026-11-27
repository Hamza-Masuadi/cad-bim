using System;
using SA.Core.Json;

namespace SA.Core.Jobs
{
    /// <summary>
    /// The shared convention for preflight validation, when earlier ops have not produced results yet: every reference
    /// string (<c>"$w1.walls.2"</c>) is replaced by a marker object <c>{ "$pending": "$w1.walls.2" }</c> and every
    /// <c>"$$..."</c> escape is unescaped, so <see cref="OpArgs"/> can read the args. Handlers skip document checks for
    /// pending fields (<see cref="OpArgs.IsPending"/>); they run again on the resolved args before the op executes.
    /// </summary>
    public static class PendingRefs
    {
        public const string Key = "$pending";

        /// <summary>A copy of the value with each reference replaced by a pending marker and "$$" escapes unescaped.</summary>
        public static JsonValue Mark(JsonValue value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            switch (value)
            {
                case JsonString s:
                    if (s.Value.StartsWith("$$", StringComparison.Ordinal)) return new JsonString(s.Value.Substring(1));
                    if (Refs.IsRefText(s.Value)) return Marker(s.Value);
                    return s;
                case JsonArray a:
                {
                    var c = new JsonArray();
                    foreach (var i in a) c.Add(Mark(i));
                    return c;
                }
                case JsonObject o:
                {
                    var c = new JsonObject();
                    foreach (var kv in o) c.Add(kv.Key, Mark(kv.Value));
                    return c;
                }
                default:
                    return value;
            }
        }

        /// <summary><see cref="Mark(JsonValue)"/> for an args object.</summary>
        public static JsonObject MarkArgs(JsonObject args) => (JsonObject)Mark(args);

        public static JsonObject Marker(string reference) => new JsonObject().Add(Key, reference);

        /// <summary>True when the value is a pending marker.</summary>
        public static bool IsMarker(JsonValue? v) => v is JsonObject o && o.Count == 1 && o[Key] is JsonString;

        /// <summary>True when the value is, or contains, a pending marker.</summary>
        public static bool IsPending(JsonValue? v)
        {
            switch (v)
            {
                case JsonObject o:
                    if (o.ContainsKey(Key)) return true;
                    foreach (var kv in o) if (IsPending(kv.Value)) return true;
                    return false;
                case JsonArray a:
                    foreach (var i in a) if (IsPending(i)) return true;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>The reference text of a pending marker, or null.</summary>
        public static string? ReferenceOf(JsonValue? v) => IsMarker(v) ? ((JsonObject)v!).GetStringOrNull(Key) : null;
    }
}
