using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// room.separator: room separation lines on a level, drawn with Document.NewRoomBoundaryLines(sketchPlane,
    /// curves, view) on a sketch plane made from the level (SketchPlane.Create(doc, levelId)), in the given plan or
    /// the level's floor plan.
    ///
    /// Separation lines belong only where no wall bounds the room (rooms-areas.md section 2). Refused: a line lying
    /// along a room-bounding wall of the level, within half the wall's thickness (Revit's top warning "A wall and a
    /// room separation line overlap", RV-004); a line on top of an existing separation line; two requested lines that
    /// overlap. Readback checks each line's category, curve type and end points. The sketch plane the lines need is
    /// created too and reported in created.
    /// </summary>
    public sealed class RoomSeparator : OpHandler
    {
        public override string Name => "room.separator";

        private sealed class Plan
        {
            public Level Level = null!;
            public View View = null!;
            public List<KeyValuePair<Point2, Point2>> Lines = new List<KeyValuePair<Point2, Point2>>();
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool afterCommit = false)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            var lv = Lookup.Level(doc, args.GetString("level"));
            if (!lv.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!); return null; }
            p.Level = lv.Value!;
            if (args.Has("view"))
            {
                if (!ctx.IsPending(args, "view"))
                {
                    var v = Lookup.View(doc, args.Get("view"));
                    if (!v.Ok) OpContext.Error(issues, ctx.FieldPath(args, "view"), v.Code, v.Error!);
                    else if (!(v.Value is ViewPlan vp) || vp.GenLevel is null || vp.GenLevel.Id != p.Level.Id)
                        OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view '" + v.Value!.Name + "' is not a plan of level '" + p.Level.Name + "'");
                    else p.View = vp;
                }
            }
            else
            {
                var plan = Kit.PlanFor(doc, p.Level);
                if (plan is null) OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotFound, "level '" + p.Level.Name + "' has no floor plan; give view");
                else p.View = plan;
            }

            double shortest = doc.Application.ShortCurveTolerance;
            var segs = new List<Segment2>();
            foreach (var l in args.GetObjectList("lines"))
            {
                var a = l.GetPoint2("start");
                var b = l.GetPoint2("end");
                if (u.LengthToInternal(a.DistanceTo(b)) <= shortest) { OpContext.Error(issues, l.Path, RevitIssueCodes.Invalid, "the line is too short for Revit"); continue; }
                if ((Kit.CoordinateError(a, u) ?? Kit.CoordinateError(b, u)) is string far) { OpContext.Error(issues, l.Path, RevitIssueCodes.Invalid, far); continue; }
                var s = new Segment2(a, b);
                foreach (var o in segs)
                    if (SegmentAnalysis.SharedLength(s, o, u.LengthTolerance) > u.LengthTolerance)
                    { OpContext.Error(issues, l.Path, RevitIssueCodes.Invalid, "this line overlaps another line of the op"); break; }
                segs.Add(s);
                p.Lines.Add(new KeyValuePair<Point2, Point2>(a, b));
            }
            if (Kit.HasErrors(issues) || afterCommit) return Kit.HasErrors(issues) ? null : p;

            // Room-bounding walls of the level, and existing separation lines.
            var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.LevelId == p.Level.Id && (w.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() ?? 1) == 1)
                .Select(w => new { w, c = (w.Location as LocationCurve)?.Curve as Line }).Where(x => x.c != null).ToList();
            var seps = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_RoomSeparationLines).WhereElementIsNotElementType()
                .OfType<CurveElement>().Where(c => c.LevelId == p.Level.Id && c.GeometryCurve is Line).ToList();
            var lines = args.GetObjectList("lines");
            for (int i = 0; i < p.Lines.Count && i < lines.Count; i++)
            {
                var s = new Segment2(p.Lines[i].Key, p.Lines[i].Value);
                foreach (var x in walls)
                {
                    var wl = x.c!;
                    var ws = new Segment2(u.PointFromInternal(wl.GetEndPoint(0)), u.PointFromInternal(wl.GetEndPoint(1)));
                    double half = u.LengthFromInternal(x.w.Width) / 2 + u.LengthTolerance;
                    if (Parallel(s, ws) && ws.LineDistanceTo(s.A) <= half && ws.LineDistanceTo(s.B) <= half && Overlap(s, ws) > u.LengthTolerance)
                    {
                        OpContext.Error(issues, lines[i].Path, RevitIssueCodes.Invalid, "the line lies along room-bounding wall " + Kit.Id(x.w.Id) +
                            "; walls already bound rooms, and a separation line on a wall is a Revit warning (RV-004)");
                        break;
                    }
                }
                foreach (var c in seps)
                {
                    var cl = (Line)c.GeometryCurve;
                    var cs = new Segment2(u.PointFromInternal(cl.GetEndPoint(0)), u.PointFromInternal(cl.GetEndPoint(1)));
                    if (SegmentAnalysis.SharedLength(s, cs, u.LengthTolerance) > u.LengthTolerance)
                    {
                        OpContext.Error(issues, lines[i].Path, RevitIssueCodes.Exists, "the line overlaps existing room separation line " + Kit.Id(c.Id));
                        break;
                    }
                }
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        private static bool Parallel(Segment2 a, Segment2 b) => Math.Abs(a.Vector.Normalised().Cross(b.Vector.Normalised())) < 1e-3;

        /// <summary>Length of a's projection onto b that lies within b.</summary>
        private static double Overlap(Segment2 a, Segment2 b)
        {
            double len = b.Length;
            var dir = b.Vector / len;
            double t0 = (a.A - b.A).Dot(dir), t1 = (a.B - b.A).Dot(dir);
            double lo = Math.Max(0, Math.Min(t0, t1)), hi = Math.Min(len, Math.Max(t0, t1));
            return hi - lo;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            double z = p!.Level.ProjectElevation;
            var sp = SketchPlane.Create(doc, p.Level.Id);
            var curves = new CurveArray();
            foreach (var l in p.Lines) curves.Append(Line.CreateBound(u.PointToInternal(l.Key, z), u.PointToInternal(l.Value, z)));
            var made = doc.Create.NewRoomBoundaryLines(sp, curves, p.View) ?? throw new OpFailedException("create_failed", "Revit did not create the separation lines");
            var ids = new List<string>();
            foreach (ModelCurve mc in made) ids.Add(Kit.Id(mc.Id));
            if (ids.Count != p.Lines.Count) throw new OpFailedException("create_failed", "Revit created " + ids.Count + " separation line(s) for " + p.Lines.Count + " requested");
            result.Created.AddRange(ids);
            result.Created.Add(Kit.Id(sp.Id));
            result.Outputs.Add("lines", JsonArray.FromStrings(ids));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var p = Build(ctx, args, new List<Issue>(), afterCommit: true);
            var ids = Refs.ElementIds(result.Outputs["lines"]);
            var arr = new JsonArray();
            for (int i = 0; i < ids.Count; i++)
            {
                string label = "separation line " + (i + 1);
                if (!ElementIds.TryParse(ids[i], out var id) || !(doc.GetElement(id) is CurveElement ce))
                {
                    check.Fail(label + " (" + ids[i] + ") no longer exists after commit");
                    continue;
                }
                var rb = new JsonObject().Add("id", ids[i]);
                check.Text(rb, "category", ce.Category?.BuiltInCategory.ToString(), BuiltInCategory.OST_RoomSeparationLines.ToString(), label + " category");
                check.Text(rb, "curveType", ce.CurveElementType.ToString(), CurveElementType.RoomSeparation.ToString(), label + " curve type");
                var want = p != null && i < p.Lines.Count ? p.Lines[i] : (KeyValuePair<Point2, Point2>?)null;
                check.Point(rb, "start", ce.GeometryCurve.GetEndPoint(0), want?.Key, label + " start");
                check.Point(rb, "end", ce.GeometryCurve.GetEndPoint(1), want?.Value, label + " end");
                rb.Add("level", Lookup.LevelName(doc, ce.LevelId));
                arr.Add(rb);
            }
            if (p != null && ids.Count != p.Lines.Count) check.Fail(ids.Count + " line(s) read back, expected " + p.Lines.Count);
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("lines", arr);
            check.Apply(result);
        }
    }
}
