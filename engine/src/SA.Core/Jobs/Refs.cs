using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SA.Core.Json;

namespace SA.Core.Jobs
{
    /// <summary>A parsed reference: <c>$w1</c> is (w1, []), <c>$w1.outputs.walls.0</c> is (w1, [outputs, walls, 0]).</summary>
    public sealed class RefExpr
    {
        public RefExpr(string opId, IReadOnlyList<string> path)
        {
            OpId = opId;
            Path = path;
        }

        public string OpId { get; }
        public IReadOnlyList<string> Path { get; }

        public override string ToString() => "$" + OpId + (Path.Count > 0 ? "." + string.Join(".", Path) : "");
    }

    public sealed class RefResolutionException : Exception
    {
        public RefResolutionException(string path, string message) : base(path + ": " + message)
        {
            Path = path;
        }

        public string Path { get; }
    }

    /// <summary>
    /// References between ops in one job. A string value that starts with a single <c>$</c> is a reference:
    /// <c>"$id"</c> is the whole result of op <c>id</c>; <c>"$id.a.b"</c> walks into it (numeric segments index arrays).
    /// A literal string starting with <c>$</c> is written with <c>$$</c>: <c>"$$5 off"</c> means "$5 off".
    ///
    /// The value of <c>$id</c> is the op result's ref target:
    /// <c>{ "id", "ids", "created", "modified", "deleted", "readback", "outputs" }</c>, where "id" is the first
    /// created (else modified) element and "ids" is created followed by modified. For <c>$id.name</c> the name is looked
    /// up in the target, then in "outputs", then in "readback".
    /// </summary>
    public static class Refs
    {
        public const string OpIdPattern = "^[A-Za-z_][A-Za-z0-9_-]{0,63}$";

        private static readonly Regex OpIdRegex = new Regex(OpIdPattern, RegexOptions.CultureInvariant);
        private static readonly Regex RefRegex = new Regex(@"^\$(?<id>[A-Za-z_][A-Za-z0-9_-]{0,63})(?<path>(?:\.[A-Za-z0-9_-]+)*)$", RegexOptions.CultureInvariant);

        public static bool IsValidOpId(string? id) => id != null && OpIdRegex.IsMatch(id);

        /// <summary>True for a string that is a reference (starts with one '$'), whether or not it is well formed.</summary>
        public static bool IsRef(JsonValue? v) => v is JsonString s && IsRefText(s.Value);

        public static bool IsRefText(string s) => s.Length > 0 && s[0] == '$' && !(s.Length > 1 && s[1] == '$');

        public static bool TryParse(string text, out RefExpr? expr, out string error)
        {
            expr = null;
            error = "";
            if (!IsRefText(text)) { error = "not a reference"; return false; }
            var m = RefRegex.Match(text);
            if (!m.Success)
            {
                error = "'" + text + "' is not a valid reference; use \"$opId\" or \"$opId.field\" (write \"$$\" for a literal '$')";
                return false;
            }
            string p = m.Groups["path"].Value;
            var parts = p.Length == 0 ? new string[0] : p.Substring(1).Split('.');
            expr = new RefExpr(m.Groups["id"].Value, parts);
            return true;
        }

        /// <summary>Every reference string inside <paramref name="value"/>, with its JSON path.</summary>
        public static List<KeyValuePair<string, string>> Find(JsonValue value, string basePath)
        {
            var list = new List<KeyValuePair<string, string>>();
            Walk(value, basePath, list);
            return list;
        }

        private static void Walk(JsonValue v, string path, List<KeyValuePair<string, string>> acc)
        {
            switch (v)
            {
                case JsonString s when IsRefText(s.Value):
                    acc.Add(new KeyValuePair<string, string>(path, s.Value));
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], JsonPath.Index(path, i), acc);
                    break;
                case JsonObject o:
                    foreach (var kv in o) Walk(kv.Value, JsonPath.Key(path, kv.Key), acc);
                    break;
            }
        }

        /// <summary>
        /// Returns a copy of <paramref name="value"/> with every reference replaced by its target and every
        /// <c>$$</c> escape turned into a literal <c>$</c>. Engines call this on an op's args just before the op runs.
        /// </summary>
        public static JsonValue Resolve(JsonValue value, IReadOnlyDictionary<string, JsonObject> refTargets, string basePath = "$")
        {
            switch (value)
            {
                case JsonString s:
                    if (s.Value.StartsWith("$$", StringComparison.Ordinal)) return new JsonString(s.Value.Substring(1));
                    if (!IsRefText(s.Value)) return s;
                    if (!TryParse(s.Value, out var expr, out var err)) throw new RefResolutionException(basePath, err);
                    return Lookup(expr!, refTargets, basePath);
                case JsonArray a:
                {
                    var copy = new JsonArray();
                    for (int i = 0; i < a.Count; i++) copy.Add(Resolve(a[i], refTargets, JsonPath.Index(basePath, i)));
                    return copy;
                }
                case JsonObject o:
                {
                    var copy = new JsonObject();
                    foreach (var kv in o) copy.Add(kv.Key, Resolve(kv.Value, refTargets, JsonPath.Key(basePath, kv.Key)));
                    return copy;
                }
                default:
                    return value;
            }
        }

        public static JsonValue Lookup(RefExpr expr, IReadOnlyDictionary<string, JsonObject> refTargets, string path)
        {
            if (!refTargets.TryGetValue(expr.OpId, out var target))
                throw new RefResolutionException(path, "no result for op '" + expr.OpId + "' (it must run earlier in the same job)");
            JsonValue cur = target;
            for (int i = 0; i < expr.Path.Count; i++)
            {
                string seg = expr.Path[i];
                JsonValue? next = null;
                if (cur is JsonObject o)
                {
                    next = o[seg];
                    if (next is null && i == 0)
                    {
                        next = (o["outputs"] as JsonObject)?[seg];
                        if (next is null) next = (o["readback"] as JsonObject)?[seg];
                    }
                }
                else if (cur is JsonArray a && int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx < a.Count)
                {
                    next = a[idx];
                }
                if (next is null)
                    throw new RefResolutionException(path, "'" + expr + "' has no '" + seg + "' in the result of op '" + expr.OpId + "'");
                cur = next;
            }
            return cur.DeepClone();
        }

        /// <summary>
        /// The element id a ref-typed field carries after resolution: a string or integer literal, or the "id" of a
        /// resolved result object. Returns null when there is none.
        /// </summary>
        public static string? ElementId(JsonValue? value)
        {
            switch (value)
            {
                case JsonString s: return s.Value.Length > 0 ? s.Value : null;
                case JsonNumber n: return n.TryGetInt64(out long l) ? l.ToString(CultureInfo.InvariantCulture) : null;
                case JsonObject o: return ElementId(o["id"]);
                default: return null;
            }
        }

        /// <summary>All element ids a resolved value carries: a list of ids, a result object's "ids", or a single id.</summary>
        public static List<string> ElementIds(JsonValue? value)
        {
            var list = new List<string>();
            switch (value)
            {
                case JsonArray a:
                    foreach (var item in a) list.AddRange(ElementIds(item));
                    break;
                case JsonObject o when o["ids"] is JsonArray ids:
                    list.AddRange(ElementIds(ids));
                    break;
                default:
                    var one = ElementId(value);
                    if (one != null) list.Add(one);
                    break;
            }
            return list;
        }
    }
}
