using System;
using System.Collections.Generic;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.Core.Imaging
{
    /// <summary>
    /// <c>verify.visual</c>: capture <see cref="Target"/> before the first op and after the last, compare the two and
    /// report the change. Engines read <see cref="Raw"/> for their own members (<c>window</c> in AutoCAD, <c>pixelWidth</c>
    /// in Revit), which have the same meaning as in a capture request.
    /// </summary>
    public sealed class VisualSpec
    {
        public const double DefaultMaxUnexpectedChange = 0.002;
        public const int DefaultThreshold = 32;
        public const int DefaultPadding = 16;

        private static readonly string[] Members = { "target", "window", "pixelWidth", "maxUnexpectedChange", "threshold", "padding", "expectChange" };

        private VisualSpec(JsonObject raw, string target)
        {
            Raw = raw;
            Target = target;
        }

        public JsonObject Raw { get; }
        /// <summary>What to capture, written as a capture target (AutoCAD "model", "activeView", "layout:A101"; Revit "activeView", a view name or id).</summary>
        public string Target { get; }
        /// <summary>Largest fraction of the image's pixels that may change outside the expected region (0 to 1).</summary>
        public double MaxUnexpectedChange { get; private set; } = DefaultMaxUnexpectedChange;
        /// <summary>Per-channel difference (0-255) below which a pixel counts as unchanged.</summary>
        public int Threshold { get; private set; } = DefaultThreshold;
        /// <summary>Pixels added around each element's projected extents to form the expected region.</summary>
        public int Padding { get; private set; } = DefaultPadding;
        /// <summary>
        /// True: the view must change; false: nothing in the view may change (every changed pixel is unexpected); null: the
        /// view must change when the job created elements that fall inside the captured frame.
        /// </summary>
        public bool? ExpectChange { get; private set; }

        /// <summary>The spec, or null when <paramref name="value"/> is absent or has errors (added to <paramref name="issues"/>).</summary>
        public static VisualSpec? Validate(JsonValue? value, string path, List<Issue> issues)
        {
            if (value is null) return null;
            if (!(value is JsonObject o))
            {
                issues.Add(new Issue(path, IssueCodes.Type, "verify.visual must be an object such as { \"target\": \"activeView\" }"));
                return null;
            }
            int before = issues.Count;
            foreach (var kv in o)
                if (Array.IndexOf(Members, kv.Key) < 0)
                    issues.Add(new Issue(JsonPath.Key(path, kv.Key), IssueCodes.UnknownField, "unknown verify.visual member '" + kv.Key + "' (allowed: " + string.Join(", ", Members) + ")"));
            string? target = o["target"] is JsonString t && t.Value.Trim().Length > 0 ? t.Value : null;
            if (target is null) issues.Add(new Issue(path + ".target", IssueCodes.Required, "verify.visual.target is required (a capture target such as \"activeView\")"));

            var spec = new VisualSpec(o, target ?? "");
            if (o["window"] is JsonValue win && !WindowOk(win))
                issues.Add(new Issue(path + ".window", IssueCodes.Type, "window must be [[x1, y1], [x2, y2]] with distinct corners, in drawing units"));
            if (o["pixelWidth"] is JsonValue pw && !(pw is JsonNumber pn && pn.TryGetInt64(out long px) && px >= 32 && px <= 15000))
                issues.Add(new Issue(path + ".pixelWidth", IssueCodes.Range, "pixelWidth must be a whole number from 32 to 15000"));
            if (o["maxUnexpectedChange"] is JsonValue mu)
            {
                if (mu is JsonNumber mn && mn.Value >= 0 && mn.Value <= 1) spec.MaxUnexpectedChange = mn.Value;
                else issues.Add(new Issue(path + ".maxUnexpectedChange", IssueCodes.Range, "maxUnexpectedChange is a fraction of the image's pixels, 0 to 1 (0.002 = 0.2%)"));
            }
            if (o["threshold"] is JsonValue th)
            {
                if (th is JsonNumber tn && tn.TryGetInt64(out long tv) && tv >= 0 && tv <= 255) spec.Threshold = (int)tv;
                else issues.Add(new Issue(path + ".threshold", IssueCodes.Range, "threshold is a whole number from 0 to 255"));
            }
            if (o["padding"] is JsonValue pd)
            {
                if (pd is JsonNumber pdn && pdn.TryGetInt64(out long pv) && pv >= 0 && pv <= 1000) spec.Padding = (int)pv;
                else issues.Add(new Issue(path + ".padding", IssueCodes.Range, "padding is a whole number of pixels from 0 to 1000"));
            }
            if (o["expectChange"] is JsonValue ec)
            {
                if (ec is JsonBool eb) spec.ExpectChange = eb.Value;
                else if (!ec.IsNull) issues.Add(new Issue(path + ".expectChange", IssueCodes.Type, "expectChange must be true, false or left out"));
            }
            return issues.Count > before ? null : spec;
        }

        private static bool WindowOk(JsonValue v)
        {
            if (!(v is JsonArray a) || a.Count != 2) return false;
            if (!(a[0] is JsonArray p) || !(a[1] is JsonArray q) || p.Count != 2 || q.Count != 2) return false;
            if (!(p[0] is JsonNumber x1 && p[1] is JsonNumber y1 && q[0] is JsonNumber x2 && q[1] is JsonNumber y2)) return false;
            return x1.Value != x2.Value && y1.Value != y2.Value;
        }

        /// <summary>Validates a spec on its own, for tests and the client; throws <see cref="FormatException"/> listing the problems.</summary>
        public static VisualSpec Parse(JsonValue value)
        {
            var issues = new List<Issue>();
            var spec = Validate(value, "$.verify.visual", issues);
            if (spec is null) throw new FormatException(string.Join("; ", issues));
            return spec;
        }
    }
}
