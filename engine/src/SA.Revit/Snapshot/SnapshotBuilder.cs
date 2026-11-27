using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Units;
using SA.Revit.Host;
using SA.Revit.Ops.Support;

namespace SA.Revit.Snapshot
{
    /// <summary>Inputs every section builder gets. Read only: no transaction is open and none may be started.</summary>
    internal sealed class SnapshotContext
    {
        public SnapshotContext(Engine engine, UIApplication app, Document doc, RevitUnits units, int limit)
        {
            Engine = engine;
            App = app;
            Doc = doc;
            Units = units;
            Limit = limit;
        }

        public Engine Engine { get; }
        public UIApplication App { get; }
        public Document Doc { get; }
        /// <summary>Job units for values written "in job units" (params.units, default mm).</summary>
        public RevitUnits Units { get; }
        /// <summary>Largest list written in full; longer lists are cut with count and truncated:true.</summary>
        public int Limit { get; }

        private Dictionary<long, string>? _viewToSheet;
        public Dictionary<long, string> ViewToSheet => _viewToSheet ??= SheetIndex.ViewToSheet(Doc);

        /// <summary>{ count, truncated, items } from a sequence, keeping at most Limit items.</summary>
        public JsonObject Capped(IEnumerable<JsonValue> items)
        {
            var arr = new JsonArray();
            int count = 0;
            foreach (var i in items)
            {
                if (count < Limit) arr.Add(i);
                count++;
            }
            return new JsonObject().Add("count", count).Add("truncated", count > Limit).Add("items", arr);
        }

        /// <summary>As <see cref="Capped(IEnumerable{JsonValue})"/>, but <paramref name="project"/> runs only for the items kept.</summary>
        public JsonObject Capped<T>(IEnumerable<T> source, Func<T, JsonValue> project)
        {
            var arr = new JsonArray();
            int count = 0;
            foreach (var x in source)
            {
                if (count < Limit) arr.Add(project(x));
                count++;
            }
            return new JsonObject().Add("count", count).Add("truncated", count > Limit).Add("items", arr);
        }
    }

    /// <summary>
    /// The snapshot method: one JSON document with everything a planner needs. Params: sections (list; default all),
    /// limit (per list, default 300, max 5000), units (job units for lengths, default mm), document.path (else the
    /// active document). Each section is built independently; a section that throws is reported under "errors"
    /// and the rest still come back.
    /// </summary>
    internal sealed class SnapshotBuilder
    {
        private readonly Engine _engine;
        private readonly List<KeyValuePair<string, Func<SnapshotContext, JsonValue>>> _sections;

        public SnapshotBuilder(Engine engine)
        {
            _engine = engine;
            _sections = new List<KeyValuePair<string, Func<SnapshotContext, JsonValue>>>
            {
                S("document", Sections.Document),
                S("units", Sections.Units),
                S("levels", Sections.Levels),
                S("grids", Sections.Grids),
                S("phases", Sections.Phases),
                S("worksets", Sections.Worksets),
                S("designOptions", Sections.DesignOptions),
                S("links", Sections.Links),
                S("wallTypes", c => Sections.HostTypes<WallType>(c, true)),
                S("floorTypes", c => Sections.HostTypes<FloorType>(c, false)),
                S("ceilingTypes", c => Sections.HostTypes<CeilingType>(c, false)),
                S("roofTypes", c => Sections.HostTypes<RoofType>(c, false)),
                S("familiesAndTypes", Sections.Families),
                S("viewFamilyTypes", Sections.ViewFamilyTypes),
                S("views", c => Sections.Views(c, false)),
                S("viewTemplates", c => Sections.Views(c, true)),
                S("sheets", Sections.Sheets),
                S("schedules", Sections.Schedules),
                S("rooms", Sections.Rooms),
                S("warnings", Sections.Warnings),
                S("selection", Sections.Selection),
                S("categories", Sections.Categories),
            };
        }

        private static KeyValuePair<string, Func<SnapshotContext, JsonValue>> S(string n, Func<SnapshotContext, JsonValue> f) =>
            new KeyValuePair<string, Func<SnapshotContext, JsonValue>>(n, f);

        public IEnumerable<string> SectionNames => _sections.Select(s => s.Key);

        public Response Build(UIApplication app, Request req)
        {
            var doc = DocResolve.For(_engine, app, req, out var failure);
            if (doc is null) return failure!;
            var p = req.Params;
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (p["sections"] is JsonArray a)
                foreach (var s in a.OfType<JsonString>()) wanted.Add(s.Value);
            var unknown = wanted.Where(w => !_sections.Any(s => s.Key == w)).ToList();
            if (unknown.Count > 0)
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "Unknown snapshot section(s): " + string.Join(", ", unknown) +
                    ". Known: " + string.Join(", ", SectionNames));
            int limit = p["limit"] is JsonNumber n && n.TryGetInt64(out long l) ? (int)Math.Max(1, Math.Min(5000, l)) : 300;
            var unit = LengthUnit.Millimetre;
            string? u = p.GetStringOrNull("units");
            if (u != null && !Length.TryParseUnit(u, out unit))
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.units must be mm, cm, m, in or ft.");
            var ctx = new SnapshotContext(_engine, app, doc, new RevitUnits(doc, unit), limit);

            var clock = Stopwatch.StartNew();
            var identity = _engine.Tracker.Compute(doc);
            var o = new JsonObject()
                .Add("identity", new JsonObject().Add("path", identity.Path).Add("fingerprint", identity.Fingerprint))
                .Add("engine", new JsonObject().Add("release", Compat.ReleaseInfo.Year).Add("version", Engine.EngineVersion))
                .Add("jobUnits", ctx.Units.DescribeJobUnits())
                .Add("limit", limit);
            var errors = new JsonObject();
            var timing = new JsonObject();
            foreach (var s in _sections)
            {
                if (wanted.Count > 0 && !wanted.Contains(s.Key)) continue;
                var t = Stopwatch.StartNew();
                try { o.Add(s.Key, s.Value(ctx)); }
                catch (Exception e)
                {
                    errors.Add(s.Key, e.GetType().Name + ": " + e.Message);
                    Log.Error("Snapshot section " + s.Key + " failed", e);
                }
                timing.Add(s.Key, t.ElapsedMilliseconds);
            }
            if (errors.Count > 0) o.Add("errors", errors);
            timing.Add("total", clock.ElapsedMilliseconds);
            o.Add("timingMs", timing);
            return Response.Success(req.Id, o);
        }
    }
}
