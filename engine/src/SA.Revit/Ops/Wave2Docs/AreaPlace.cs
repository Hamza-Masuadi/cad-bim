using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// area.place: area boundary lines and areas in an area plan.
    ///
    /// <para><b>API.</b> Lines: Document.Create.NewAreaBoundaryLine(sketchPlane, curve, areaPlan) on
    /// SketchPlane.Create(doc, levelId) of the plan's level, at the level's elevation. Areas:
    /// Document.Create.NewArea(areaPlan, uv), which the API describes as placing the area in the region the area
    /// boundary lines enclose around the point; the document is regenerated after the lines so the new boundaries
    /// count. Name: the area's ROOM_NAME parameter (areas share the room name parameter), else SpatialElement.Name;
    /// number: SpatialElement.Number. Tags: Document.Create.NewAreaTag(areaPlan, area, uv), type changed with
    /// ChangeTypeId when tagType is given.</para>
    ///
    /// <para><b>Checks.</b> The view must be an area plan. A point already inside an enclosed area of the same scheme
    /// and level is refused (one area per region). A new area that comes out with area 0 (not enclosed) fails the op
    /// unless allowNotEnclosed. Parameter names are checked against an existing area when there is one.</para>
    /// </summary>
    public sealed class AreaPlace : OpHandler
    {
        public override string Name => "area.place";

        private sealed class AreaItem
        {
            public XYZ Point = XYZ.Zero;
            public string? Name, Number;
            public List<KeyValuePair<string, JsonValue>> Parameters = new List<KeyValuePair<string, JsonValue>>();
            public string Path = "";
        }

        private sealed class Plan
        {
            public ViewPlan View = null!;
            public Level Level = null!;
            public List<Line> Lines = new List<Line>();
            public List<AreaItem> Areas = new List<AreaItem>();
            public FamilySymbol? TagType;
        }

        private static List<Area> AreasOf(Document doc, ViewPlan v) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().OfType<Area>()
                .Where(a => a.AreaScheme?.Id == v.AreaScheme?.Id && a.LevelId == v.GenLevel?.Id).ToList();

        private static List<Point2> Outline(Area a)
        {
            var loops = a.GetBoundarySegments(new SpatialElementBoundaryOptions());
            var best = new List<Point2>();
            double bestArea = 0;
            if (loops is null) return best;
            foreach (var segs in loops)
            {
                var pts = new List<Point2>();
                foreach (var s in segs)
                {
                    var t = s.GetCurve().Tessellate();
                    for (int i = 0; i < t.Count - 1; i++) pts.Add(new Point2(t[i].X, t[i].Y));
                }
                if (pts.Count < 3) continue;
                double ar = new Polygon2(pts).Area;
                if (ar > bestArea) { best = pts; bestArea = ar; }
            }
            return best;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (pending || v is null) return null;
            if (!(v is ViewPlan vp) || v.ViewType != ViewType.AreaPlan || v.IsTemplate || vp.GenLevel is null)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, DocsUtil.Describe(v) + " is not an area plan");
                return null;
            }
            var p = new Plan { View = vp, Level = vp.GenLevel };
            double z = p.Level.ProjectElevation;
            double tol = ctx.Units.LengthToleranceInternal;
            var loops = args.Get("boundaries") as JsonArray ?? new JsonArray();
            for (int i = 0; i < loops.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "boundaries"), i);
                var pts = W2.FramePoints(ctx, v, loops[i], at);
                if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= ctx.Units.LengthTolerance) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "a boundary loop needs at least three points"); continue; }
                for (int k = 0; k < pts.Count; k++)
                {
                    var a = ctx.Units.PointToInternal(pts[k], z);
                    var b = ctx.Units.PointToInternal(pts[(k + 1) % pts.Count], z);
                    if (a.DistanceTo(b) <= tol) { OpContext.Error(issues, JsonPath.Index(at, k), RevitIssueCodes.Invalid, "repeats the next point"); continue; }
                    p.Lines.Add(Line.CreateBound(a, b));
                }
            }
            foreach (var l in args.GetObjectList("lines"))
            {
                var a = ctx.Units.PointToInternal(l.GetPoint2("start"), z);
                var b = ctx.Units.PointToInternal(l.GetPoint2("end"), z);
                if (a.DistanceTo(b) <= tol) { OpContext.Error(issues, l.Path, RevitIssueCodes.Invalid, "the line has no length"); continue; }
                p.Lines.Add(Line.CreateBound(a, b));
            }
            var existing = AreasOf(doc, vp).Where(a => a.Area > 0).ToList();
            var outlines = existing.Select(a => new KeyValuePair<Area, List<Point2>>(a, Outline(a))).Where(kv => kv.Value.Count >= 3).ToList();
            var sample = existing.FirstOrDefault();
            var numbers = new HashSet<string>(existing.Select(a => a.Number ?? ""), StringComparer.Ordinal);
            foreach (var a in args.GetObjectList("areas"))
            {
                var item = new AreaItem { Point = ctx.Units.PointToInternal(a.GetPoint2("point"), z), Name = a.GetStringOrNull("name"), Number = a.GetStringOrNull("number"), Path = a.Path };
                var q = new Point2(item.Point.X, item.Point.Y);
                foreach (var kv in outlines)
                    if (new Polygon2(kv.Value).Contains(q))
                    {
                        OpContext.Error(issues, ctx.FieldPath(a, "point"), RevitIssueCodes.Exists, "area " + ElementIds.ToWire(kv.Key.Id) + " ('" + kv.Key.Number + "') already covers this point in " + vp.AreaScheme?.Name + " on " + p.Level.Name);
                        break;
                    }
                if (item.Number != null && !numbers.Add(item.Number))
                    OpContext.Warning(issues, ctx.FieldPath(a, "number"), RevitIssueCodes.Exists, "area number '" + item.Number + "' is used more than once in this scheme and level");
                item.Parameters = a.GetMap("parameters");
                foreach (var kv in item.Parameters)
                    if (ParamAccess.KnownFor(doc, BuiltInCategory.OST_Areas, kv.Key, sample) == false)
                        OpContext.Error(issues, JsonPath.Key(ctx.FieldPath(a, "parameters"), kv.Key), RevitIssueCodes.NotFound, "areas have no parameter '" + kv.Key + "'");
                p.Areas.Add(item);
            }
            if (args.GetStringOrNull("tagType") is string tt)
            {
                var f = Lookup.Symbol(doc, BuiltInCategory.OST_AreaTags, tt, "area tag type");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "tagType"), f.Code, f.Error!);
                else p.TagType = f.Value;
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static void SetName(Area a, string name)
        {
            var np = a.get_Parameter(BuiltInParameter.ROOM_NAME);
            if (np != null && !np.IsReadOnly) np.Set(name);
            else a.Name = name;
        }

        private static string? ReadName(Area a) => a.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? a.Name;

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var lines = new List<ElementId>();
            if (p.Lines.Count > 0)
            {
                var sp = SketchPlane.Create(doc, p.Level.Id);
                foreach (var l in p.Lines)
                {
                    var made = doc.Create.NewAreaBoundaryLine(sp, l, p.View) ?? throw new OpFailedException("create_failed", "Revit did not create an area boundary line");
                    lines.Add(made.Id);
                }
                doc.Regenerate();
            }
            var areas = new List<Area>();
            var tags = new List<ElementId>();
            foreach (var item in p.Areas)
            {
                var area = doc.Create.NewArea(p.View, new UV(item.Point.X, item.Point.Y)) ?? throw new OpFailedException("create_failed", item.Path + ": Revit did not create the area");
                if (item.Name != null) SetName(area, item.Name);
                if (item.Number != null) area.Number = item.Number;
                foreach (var kv in item.Parameters)
                {
                    var prm = ParamAccess.Find(area, kv.Key, out string? err) ?? throw new OpFailedException("parameter_not_found", item.Path + ": " + err);
                    if (prm.IsReadOnly) throw new OpFailedException("parameter_read_only", item.Path + ": parameter '" + kv.Key + "' is read-only");
                    if (!ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out string perr)) throw new OpFailedException("parameter_invalid", item.Path + ".parameters." + kv.Key + ": " + perr);
                    if (!ParamAccess.Apply(prm, pv)) throw new OpFailedException("parameter_refused", item.Path + ": Revit refused the value of '" + kv.Key + "'");
                }
                areas.Add(area);
            }
            doc.Regenerate();
            var values = new JsonArray();
            for (int i = 0; i < areas.Count; i++)
            {
                double a = areas[i].Area;
                if (a <= 0)
                {
                    if (!args.GetBool("allowNotEnclosed"))
                        throw new OpFailedException("not_enclosed", p.Areas[i].Path + ": the area at this point is not enclosed by area boundaries; nothing was kept");
                    ctx.Warn(p.Areas[i].Path + ": area " + ElementIds.ToWire(areas[i].Id) + " is not enclosed (area 0)");
                }
                values.Add(RevitUnits.Num(ctx.Units.AreaFromInternal(a)));
                if (args.GetBool("tag"))
                {
                    var tag = doc.Create.NewAreaTag(p.View, areas[i], new UV(p.Areas[i].Point.X, p.Areas[i].Point.Y));
                    if (tag is null) ctx.Warn(p.Areas[i].Path + ": Revit did not tag the area");
                    else
                    {
                        if (p.TagType != null)
                        {
                            if (!p.TagType.IsActive) p.TagType.Activate();
                            tag.ChangeTypeId(p.TagType.Id);
                        }
                        tags.Add(tag.Id);
                    }
                }
            }
            result.Created.AddRange(lines.Select(ElementIds.ToWire));
            result.Created.AddRange(areas.Select(a => ElementIds.ToWire(a.Id)));
            result.Created.AddRange(tags.Select(ElementIds.ToWire));
            result.Outputs.Set("lines", W2.Ids(lines));
            result.Outputs.Set("areas", W2.Ids(areas.Select(a => a.Id)));
            result.Outputs.Set("values", values);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var issues = new List<Issue>();
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out _) as ViewPlan;
            if (v is null) { check.Fail("could not resolve the view again"); check.Apply(result); return; }
            var rb = result.Readback;
            var lineIds = (result.Outputs["lines"] as JsonArray ?? new JsonArray()).Select(x => x.AsString()).ToList();
            int lineCount = 0;
            foreach (var t in lineIds)
                if (ElementIds.TryParse(t, out var lid) && doc.GetElement(lid) is CurveElement ce && ce.CurveElementType == CurveElementType.AreaSeparation) lineCount++;
                else check.Fail("area boundary line " + t + " is missing after the op");
            rb.Set("lines", lineCount);
            var areaIds = (result.Outputs["areas"] as JsonArray ?? new JsonArray()).Select(x => x.AsString()).ToList();
            var items = args.GetObjectList("areas");
            var recs = new JsonArray();
            var values = new JsonArray();
            for (int i = 0; i < areaIds.Count; i++)
            {
                var area = ElementIds.TryParse(areaIds[i], out var aid) ? doc.GetElement(aid) as Area : null;
                var rec = new JsonObject().Add("id", areaIds[i]);
                recs.Add(rec);
                if (area is null) { check.Fail("area " + areaIds[i] + " is missing after the op"); values.Add(0); continue; }
                var a = i < items.Count ? items[i] : null;
                check.Text(rec, "areaScheme", area.AreaScheme?.Name, v.AreaScheme?.Name);
                check.Text(rec, "level", Lookup.LevelName(doc, area.LevelId), v.GenLevel?.Name);
                check.Text(rec, "name", ReadName(area), a?.GetStringOrNull("name"));
                check.Text(rec, "number", area.Number, a?.GetStringOrNull("number"));
                double value = ctx.Units.AreaFromInternal(area.Area);
                rec.Set("area", RevitUnits.Num(value));
                values.Add(RevitUnits.Num(value));
                if (area.Area <= 0 && !args.GetBool("allowNotEnclosed")) check.Fail("area " + areaIds[i] + " is not enclosed");
                if (a != null)
                    foreach (var kv in a.GetMap("parameters"))
                    {
                        var prm = ParamAccess.Find(area, kv.Key, out _);
                        if (prm is null) { check.Fail("area " + areaIds[i] + ": parameter '" + kv.Key + "' not found"); continue; }
                        if (ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out _) && !ParamAccess.Matches(prm, pv, ctx.Units))
                            check.Fail("area " + areaIds[i] + ": " + kv.Key + " reads " + ParamAccess.Read(prm, ctx.Units).ToJson() + ", requested " + kv.Value.ToJson());
                    }
            }
            if (areaIds.Count != items.Count) check.Fail(areaIds.Count + " area(s) created, " + items.Count + " requested");
            rb.Set("areas", recs);
            int tags = result.Created.Count(t => ElementIds.TryParse(t, out var tid) && doc.GetElement(tid) is AreaTag);
            rb.Set("tags", tags);
            if (args.GetBool("tag") && tags != areaIds.Count) check.Fail(tags + " area tag(s) found for " + areaIds.Count + " area(s)");
            result.Outputs.Set("values", values);
            check.Apply(result);
        }
    }
}
