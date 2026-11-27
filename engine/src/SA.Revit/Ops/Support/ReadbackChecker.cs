using System;
using System.Collections.Generic;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Core.Units;

namespace SA.Revit.Ops.Support
{
    /// <summary>
    /// Records values read back and compares them with what was requested. An op is ok only when every compared
    /// value matched; <see cref="Apply"/> sets OpResult.Ok and, on a mismatch, an error listing every difference.
    /// </summary>
    internal sealed class ReadbackChecker
    {
        private readonly RevitUnits _units;
        private readonly List<string> _mismatches = new List<string>();

        public ReadbackChecker(RevitUnits units)
        {
            _units = units;
        }

        public IReadOnlyList<string> Mismatches => _mismatches;
        public bool Ok => _mismatches.Count == 0;

        public void Fail(string message) => _mismatches.Add(message);

        /// <summary>Writes the length (job units) into <paramref name="into"/> and compares with the request when given.</summary>
        /// <remarks>Pass <paramref name="units"/> (OpContext.Paper) for a length measured on paper.</remarks>
        public void Length(JsonObject into, string name, double actualInternal, double? requestedJob, string? label = null, RevitUnits? units = null)
        {
            var un = units ?? _units;
            double actual = un.LengthFromInternal(actualInternal);
            into.Set(name, RevitUnits.Num(actual));
            if (requestedJob.HasValue && !Tolerance.Equal(actual, requestedJob.Value, un.LengthTolerance))
                _mismatches.Add((label ?? name) + ": read back " + Fmt(actual) + ", requested " + Fmt(requestedJob.Value) + " " + Core.Units.Length.Symbol(un.JobUnit));
        }

        public void Point(JsonObject into, string name, Autodesk.Revit.DB.XYZ actualInternal, Core.Geometry.Point2? requested, string? label = null, RevitUnits? units = null)
        {
            var un = units ?? _units;
            var p = un.PointFromInternal(actualInternal);
            into.Set(name, new JsonArray(RevitUnits.Num(p.X), RevitUnits.Num(p.Y)));
            if (requested.HasValue)
            {
                double d = (p - requested.Value).Length;
                if (d > un.LengthTolerance)
                    _mismatches.Add((label ?? name) + ": read back [" + Fmt(p.X) + ", " + Fmt(p.Y) + "], requested [" + Fmt(requested.Value.X) + ", " + Fmt(requested.Value.Y) + "]");
            }
        }

        public void Text(JsonObject into, string name, string? actual, string? requested, string? label = null)
        {
            into.Set(name, actual is null ? (JsonValue)JsonValue.Null : actual);
            if (requested != null && !string.Equals(actual, requested, StringComparison.Ordinal))
                _mismatches.Add((label ?? name) + ": read back '" + (actual ?? "(none)") + "', requested '" + requested + "'");
        }

        public void Flag(JsonObject into, string name, bool actual, bool? requested, string? label = null)
        {
            into.Set(name, actual);
            if (requested.HasValue && requested.Value != actual)
                _mismatches.Add((label ?? name) + ": read back " + (actual ? "true" : "false") + ", requested " + (requested.Value ? "true" : "false"));
        }

        public void Apply(OpResult result)
        {
            result.Ok = Ok && result.Error is null;
            if (!Ok)
            {
                var details = new JsonObject().Add("mismatches", JsonArray.FromStrings(_mismatches));
                result.Error = new OpError(IssueCodes.ReadbackMismatch,
                    _mismatches.Count + " value(s) read back differ from the request: " + string.Join("; ", Take(_mismatches, 5)), details);
            }
        }

        private static IEnumerable<string> Take(List<string> l, int n)
        {
            for (int i = 0; i < l.Count && i < n; i++) yield return l[i];
        }

        private static string Fmt(double v) => Math.Round(v, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    }
}
