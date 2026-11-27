using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// area.report: measures closed polylines, circles, ellipses, closed splines, hatches and regions lying flat in plan.
    /// Areas are computed from the geometry (exact for polyline arcs) and cross-checked against AutoCAD's own
    /// Curve.Area, Hatch.Area or Region.Area; a difference over 0.5 % is a warning. With islands "subtract", a selected
    /// boundary inside another selected boundary on the same layer is a hole in its smallest container, and one inside a
    /// hole counts again (even-odd). Hatch loops nest the same way inside the hatch. Read-only unless a table is asked
    /// for; files go to the job's output folder (a temporary folder in a rehearsal).
    /// </summary>
    internal sealed class AreaReport : OpHandler<AreaReport.Plan>
    {
        public override string Name => "area.report";

        internal sealed class Item
        {
            public string Handle = "", Type = "", Layer = "";
            public Polygon2? Outline;
            public double Gross, Net, Perimeter;
            public List<string> Holes = new List<string>();
            public bool IsHole;
            public Room? Room;
            public Point2 Inside;
        }

        internal sealed class Plan
        {
            public List<Item> Items = new List<Item>();
            public string AreaUnit = "m2";
            public int Precision = 2;
            public string GroupBy = "layer";
            public TableSpec? Table;
            public OutputFile? File;
            public List<(string Key, int Count, double Area)> Groups = new List<(string, int, double)>();
            public double Total;
        }

        private static readonly Dictionary<string, string> TypeOf = new Dictionary<string, string>
        {
            { "LWPOLYLINE", "polyline" }, { "CIRCLE", "circle" }, { "HATCH", "hatch" }, { "REGION", "region" }, { "ELLIPSE", "ellipse" }, { "SPLINE", "spline" },
        };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan
            {
                AreaUnit = args.GetStringOrNull("areaUnit") ?? (Length.IsImperial(ctx.JobUnit) ? "ft2" : "m2"),
                Precision = args.GetInt("precision"),
                GroupBy = args.GetString("groupBy"),
            };
            var rooms = RoomFinder.Plan(ctx, args, issues);
            if (p.GroupBy == "room" && rooms is null) OpContext.Error(issues, args, "groupBy", "rooms_missing", "groupBy room needs rooms");
            p.File = OutputFile.Plan(ctx, args, issues);
            var table = args.GetObject("table");
            if (table != null)
            {
                var t = new TableSpec { Position = table.GetPoint2("position"), Title = table.GetStringOrNull("title") };
                TableBuilder.Plan(ctx, table, "style", t, issues);
                t.Layer = DraftDb.Layer(ctx, table, issues, "layer", "schedules");
                p.Table = t;
            }

            var ids = new List<ObjectId>();
            if (args.Has("handles"))
            {
                if (ctx.IsRef(args, "handles")) return p;
                foreach (var h in args.GetRefIds("handles"))
                {
                    var id = ctx.IdOf(h);
                    if (id.IsNull) { OpContext.Error(issues, args, "handles", "handle_missing", "handle " + h + " is not in the drawing"); continue; }
                    if (!TypeOf.ContainsKey(id.ObjectClass.DxfName)) { OpContext.Error(issues, args, "handles", "not_measurable", "entity " + h + " is a " + id.ObjectClass.DxfName + "; give closed polylines, circles, ellipses, splines, hatches or regions"); continue; }
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
            else
            {
                var f = args.GetObject("filter")!;
                var layers = Targets.Patterns(f, "layers");
                var types = new HashSet<string>(f.GetStringList("types"));
                Db.Read(ctx, tr =>
                {
                    foreach (var s in SheetDb.Spaces(tr, ctx.Database, f.GetString("space"), false, false))
                        foreach (ObjectId id in (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead))
                        {
                            if (id.IsErased || !TypeOf.TryGetValue(id.ObjectClass.DxfName, out string? type) || !types.Contains(type)) continue;
                            var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            if (layers.Count > 0 && !Targets.AnyMatch(layers, e.Layer)) continue;
                            if (e is Curve c && !c.Closed && !(e is Polyline)) continue;
                            ids.Add(id);
                        }
                    return 0;
                });
            }

            int open = 0;
            var notPlan = new List<string>();
            Db.Read(ctx, tr =>
            {
                foreach (var id in ids)
                {
                    var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    var it = Measure(ctx, e, out bool isOpen, out string? warning);
                    if (isOpen) { open++; continue; }
                    if (it is null) { notPlan.Add(OpContext.HandleOf(id)); continue; }
                    if (warning != null && ctx.IsValidating) OpContext.Warning(issues, args, null, "area_check", warning);
                    p.Items.Add(it);
                }
                return 0;
            });
            if (ctx.IsValidating)
            {
                if (open > 0) OpContext.Warning(issues, args, args.Has("handles") ? "handles" : "filter", "open_skipped", open + " open polylines are skipped (they enclose no area)");
                if (notPlan.Count > 0) OpContext.Warning(issues, args, null, "not_plan_skipped", notPlan.Count + " entities are not closed or do not lie flat in plan and are skipped (first " + notPlan[0] + ")");
                if (p.Items.Count == 0) OpContext.Warning(issues, args, null, "nothing_measured", "nothing to measure");
            }

            if (args.GetString("islands") == "subtract") Nest(ctx, p.Items);
            foreach (var it in p.Items.Where(i => !i.IsHole))
                if (rooms != null) it.Room = rooms.At(it.Inside);

            var key = (Func<Item, string>)(i => p.GroupBy == "layer" ? i.Layer : p.GroupBy == "room" ? (i.Room is null ? "(no room)" : RoomFinder.Label(i.Room)) : "all");
            p.Groups = p.Items.Where(i => !i.IsHole).GroupBy(key).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Count(), g.Sum(i => Convert(ctx, p, i.Net)))).ToList();
            p.Total = p.Items.Where(i => !i.IsHole).Sum(i => Convert(ctx, p, i.Net));
            return p;
        }

        private static double Convert(OpContext ctx, Plan p, double jobArea) => Num.Area(jobArea, ctx.JobUnit, p.AreaUnit);

        private static Item? Measure(OpContext ctx, Entity e, out bool isOpen, out string? warning)
        {
            isOpen = false;
            warning = null;
            double f = ctx.Units.ToJob(1.0);
            var it = new Item { Handle = OpContext.HandleOf(e.ObjectId), Type = TypeOf[e.ObjectId.ObjectClass.DxfName], Layer = e.Layer };
            double? acad = null;
            switch (e)
            {
                case Polyline pl when !pl.Closed && (pl.NumberOfVertices < 3 || pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(pl.NumberOfVertices - 1)) > ctx.Units.ToDrawing(ctx.LengthTolerance)):
                    isOpen = true;
                    return null;
                case Curve c:
                {
                    var loop = PlanGeometry.ClosedLoop(ctx, c);
                    if (loop?.Polygon is null) return null;
                    it.Outline = loop.Polygon;
                    it.Gross = loop.Area;
                    try { it.Perimeter = c.GetDistanceAtParameter(c.EndParam) * f; } catch (Autodesk.AutoCAD.Runtime.Exception) { it.Perimeter = loop.Perimeter; }
                    try { acad = c.Area * f * f; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    break;
                }
                case Hatch h:
                {
                    var loops = PlanGeometry.HatchLoops(ctx, h);
                    if (loops is null || loops.Count == 0) return null;
                    var sorted = loops.Where(l => l.Polygon != null).OrderByDescending(l => l.Area).ToList();
                    double net = 0;
                    foreach (var l in sorted)
                    {
                        int depth = sorted.Count(o => o != l && o.Area > l.Area && PlanGeometry.Contains(o.Polygon!, l.Polygon!, ctx.LengthTolerance));
                        net += depth % 2 == 0 ? l.Area : -l.Area;
                    }
                    it.Outline = sorted[0].Polygon;
                    it.Gross = net;
                    it.Perimeter = loops.Sum(l => l.Perimeter);
                    try { acad = h.Area * f * f; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    break;
                }
                case Region r:
                {
                    if (!PlanGeometry.IsPlan(r.Normal)) return null;
                    it.Gross = r.Area * f * f;
                    it.Perimeter = r.Perimeter * f;
                    try
                    {
                        var ext = r.GeometricExtents;
                        var a = ctx.Units.ToJob(ext.MinPoint); var b = ctx.Units.ToJob(ext.MaxPoint);
                        it.Inside = (a + b) * 0.5;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    break;
                }
                default:
                    return null;
            }
            if (it.Outline != null) it.Inside = PlanGeometry.InteriorPoint(it.Outline);
            it.Net = it.Gross;
            if (acad.HasValue && it.Gross > 0 && Math.Abs(acad.Value - it.Gross) > 0.005 * it.Gross)
                warning = it.Type + " " + it.Handle + ": AutoCAD reports area " + AcadValues.Round(acad.Value, 3) + ", the geometry gives " + AcadValues.Round(it.Gross, 3) + " (job units squared); the geometry value is used";
            return it;
        }

        /// <summary>Even-odd nesting of outlines on the same layer: odd depth is a hole of its smallest container.</summary>
        private static void Nest(OpContext ctx, List<Item> items)
        {
            var withOutline = items.Where(i => i.Outline != null && i.Type != "hatch").OrderBy(i => i.Gross).ToList();
            var parent = new Dictionary<Item, Item?>();
            foreach (var it in withOutline)
                parent[it] = withOutline.Where(o => o != it && o.Gross > it.Gross && string.Equals(o.Layer, it.Layer, StringComparison.OrdinalIgnoreCase)
                                                    && PlanGeometry.Contains(o.Outline!, it.Outline!, ctx.LengthTolerance)).OrderBy(o => o.Gross).FirstOrDefault();
            int Depth(Item i) { int d = 0; for (var q = parent[i]; q != null; q = parent[q]) d++; return d; }
            foreach (var it in withOutline)
            {
                if (Depth(it) % 2 == 0) continue;
                var host = parent[it]!;
                it.IsHole = true;
                host.Net -= it.Gross;
                host.Holes.Add(it.Handle);
            }
        }

        private static string Fmt(Plan p, double v) => v.ToString("F" + p.Precision, CultureInfo.InvariantCulture);

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var items = new JsonArray();
            var measured = p.Items.Where(i => !i.IsHole).ToList();
            foreach (var it in measured)
                items.Add(new JsonObject().Add("handle", it.Handle).Add("type", it.Type).Add("layer", it.Layer)
                    .Add("area", AcadValues.Round(Convert(ctx, p, it.Net), 6)).Add("gross", AcadValues.Round(Convert(ctx, p, it.Gross), 6))
                    .Add("holes", JsonArray.FromStrings(it.Holes)).Add("perimeter", AcadValues.Round(it.Perimeter, 6)).Add("room", RoomFinder.Json(it.Room)));
            var groups = new JsonArray(p.Groups.Select(g => (JsonValue)new JsonObject().Add("key", g.Key).Add("count", g.Count).Add("area", AcadValues.Round(g.Area, 6))));
            string unitLabel = p.AreaUnit == "job" ? Length.Symbol(ctx.JobUnit) + "2" : p.AreaUnit;

            if (p.Table != null)
            {
                var t = p.Table;
                if (p.GroupBy == "none")
                {
                    t.Headers = new List<string> { "Handle", "Layer", "Area (" + unitLabel + ")" };
                    t.Rows = measured.Select(i => new List<string> { i.Handle, i.Layer, Fmt(p, Convert(ctx, p, i.Net)) }).ToList();
                }
                else
                {
                    t.Headers = new List<string> { p.GroupBy == "room" ? "Room" : "Layer", "Count", "Area (" + unitLabel + ")" };
                    t.Rows = p.Groups.Select(g => new List<string> { g.Key, g.Count.ToString(CultureInfo.InvariantCulture), Fmt(p, g.Area) }).ToList();
                }
                t.Rows.Add(new List<string> { "Total", measured.Count.ToString(CultureInfo.InvariantCulture), Fmt(p, p.Total) });
                t.Columns = 3;
                TableBuilder.Build(ctx, t);
                result.Created.Add(t.Handle);
            }
            if (p.File != null)
            {
                if (p.File.IsJson)
                    p.File.WriteJson(ctx, new JsonObject().Add("areaUnit", p.AreaUnit).Add("total", AcadValues.Round(p.Total, 6)).Add("items", items).Add("groups", groups));
                else
                    p.File.WriteCsv(ctx, new[] { "handle", "type", "layer", "area", "gross", "holes", "perimeter", "roomNumber", "roomName" },
                        measured.Select(i => (IReadOnlyList<string>)new[]
                        {
                            i.Handle, i.Type, i.Layer, Fmt(p, Convert(ctx, p, i.Net)), Fmt(p, Convert(ctx, p, i.Gross)), string.Join(" ", i.Holes),
                            i.Perimeter.ToString("F" + Math.Max(p.Precision, 3), CultureInfo.InvariantCulture), i.Room?.Number ?? "", i.Room?.Name ?? "",
                        }));
            }
            result.Outputs.Add("total", AcadValues.Round(p.Total, 6)).Add("areaUnit", p.AreaUnit).Add("items", items).Add("groups", groups)
                .Add("path", p.File != null ? (JsonValue)p.File.Path : JsonValue.Null)
                .Add("table", p.Table != null ? (JsonValue)p.Table.Handle : JsonValue.Null);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            try
            {
                var mismatches = new List<string>();
                result.Readback.Add("measured", p.Items.Count(i => !i.IsHole)).Add("holes", p.Items.Count(i => i.IsHole)).Add("total", AcadValues.Round(p.Total, 6));
                if (p.Table != null)
                {
                    var tb = new JsonObject();
                    TableBuilder.Verify(ctx, p.Table, tb, mismatches);
                    result.Readback.Add("table", tb);
                }
                p.File?.Verify(result.Readback, mismatches, p.Items.Count(i => !i.IsHole) + 1);
                foreach (var m in mismatches) Mismatch(result, m);
                return mismatches.Count == 0;
            }
            finally { p.File?.Cleanup(ctx); }
        }
    }
}
