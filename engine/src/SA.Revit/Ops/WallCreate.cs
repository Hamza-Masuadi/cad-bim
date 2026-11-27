using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    /// <summary>
    /// wall.create: one wall from start/end, or one per segment of a path.
    ///
    /// The points describe the chosen location line. Revit creates a wall on its centreline, so for any other
    /// location line the handler sets the Location Line parameter and then assigns the location curve again: the
    /// curve now stands for the chosen line, so the wall moves until that line lies on the requested points. No
    /// offset is computed by hand. Readback compares the location curve end points with the request.
    /// </summary>
    public sealed class WallCreate : OpHandler
    {
        public override string Name => "wall.create";

        // Revit's limit is about 20 miles from the internal origin; be well inside it.
        private const double MaxCoordinateFeet = 52800;

        private static readonly Dictionary<string, WallLocationLine> LocationLines = new Dictionary<string, WallLocationLine>(StringComparer.Ordinal)
        {
            { "wall_centerline", WallLocationLine.WallCenterline },
            { "core_centerline", WallLocationLine.CoreCenterline },
            { "finish_face_exterior", WallLocationLine.FinishFaceExterior },
            { "finish_face_interior", WallLocationLine.FinishFaceInterior },
            { "core_face_exterior", WallLocationLine.CoreExterior },
            { "core_face_interior", WallLocationLine.CoreInterior },
        };

        internal static string LocationLineName(int value)
        {
            foreach (var kv in LocationLines) if ((int)kv.Value == value) return kv.Key;
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class Plan
        {
            public WallType Type = null!;
            public Level Level = null!;
            public Level? Top;
            public List<KeyValuePair<Point2, Point2>> Segments = new List<KeyValuePair<Point2, Point2>>();
            public double? Height;
            public double TopOffset, BaseOffset;
            public WallLocationLine Line;
            public string LineName = "wall_centerline";
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            bool ok = true;

            var type = Lookup.Type<WallType>(doc, args.GetString("type"), "wall type");
            if (!type.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "type"), type.Code, type.Error!); ok = false; }
            else p.Type = type.Value!;

            var level = Lookup.Level(doc, args.GetString("level"));
            if (!level.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "level"), level.Code, level.Error!); ok = false; }
            else p.Level = level.Value!;

            if (args.Has("topLevel"))
            {
                var top = Lookup.Level(doc, args.GetString("topLevel"));
                if (!top.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "topLevel"), top.Code, top.Error!); ok = false; }
                else p.Top = top.Value!;
            }
            p.Height = args.GetLengthOrNull("height");
            p.TopOffset = args.GetLength("topOffset");
            p.BaseOffset = args.GetLength("baseOffset");
            if (p.Height.HasValue && p.Height.Value <= 0) { OpContext.Error(issues, ctx.FieldPath(args, "height"), RevitIssueCodes.Invalid, "height must be greater than zero"); ok = false; }
            if (ok && p.Top != null)
            {
                double baseZ = p.Level.ProjectElevation + u.LengthToInternal(p.BaseOffset);
                double topZ = p.Top.ProjectElevation + u.LengthToInternal(p.TopOffset);
                if (topZ - baseZ <= u.LengthToleranceInternal)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "topLevel"), RevitIssueCodes.Invalid,
                        "the top ('" + p.Top.Name + "' + topOffset) is not above the base ('" + p.Level.Name + "' + baseOffset)");
                    ok = false;
                }
            }

            p.LineName = args.GetString("locationLine");
            p.Line = LocationLines[p.LineName];
            if (ok && p.Line != WallLocationLine.WallCenterline && p.Type.Kind != WallKind.Basic)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "locationLine"), RevitIssueCodes.Invalid,
                    "location line '" + p.LineName + "' needs a basic wall type; '" + p.Type.Name + "' is " + p.Type.Kind);
                ok = false;
            }

            if (args.Has("start"))
            {
                p.Segments.Add(new KeyValuePair<Point2, Point2>(args.GetPoint2("start"), args.GetPoint2("end")));
            }
            else
            {
                var pts = args.GetPoint2List("path");
                for (int i = 0; i + 1 < pts.Count; i++) p.Segments.Add(new KeyValuePair<Point2, Point2>(pts[i], pts[i + 1]));
                if (args.GetBool("closed"))
                {
                    if (pts.Count < 3) { OpContext.Error(issues, ctx.FieldPath(args, "closed"), RevitIssueCodes.Invalid, "a closed path needs at least 3 points"); ok = false; }
                    else p.Segments.Add(new KeyValuePair<Point2, Point2>(pts[pts.Count - 1], pts[0]));
                }
            }
            double shortest = doc.Application.ShortCurveTolerance;
            for (int i = 0; i < p.Segments.Count; i++)
            {
                var s = p.Segments[i];
                double len = u.LengthToInternal((s.Value - s.Key).Length);
                if (len <= shortest)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, args.Has("start") ? "end" : "path"), RevitIssueCodes.Invalid,
                        "segment " + i + " is too short for Revit (" + Math.Round((s.Value - s.Key).Length, 4) + " " + SA.Core.Units.Length.Symbol(u.JobUnit) + ")");
                    ok = false;
                }
                foreach (var pt in new[] { s.Key, s.Value })
                    if (Math.Abs(u.LengthToInternal(pt.X)) > MaxCoordinateFeet || Math.Abs(u.LengthToInternal(pt.Y)) > MaxCoordinateFeet)
                    {
                        OpContext.Error(issues, ctx.FieldPath(args, args.Has("start") ? "start" : "path"), RevitIssueCodes.Invalid,
                            "point [" + pt.X + ", " + pt.Y + "] is more than 10 miles from the internal origin; check the units");
                        ok = false;
                    }
            }

            if (ok)
            {
                Element? sample = new FilteredElementCollector(doc).OfClass(typeof(Wall)).FirstElement();
                foreach (var kv in args.GetMap("parameters"))
                {
                    var known = ParamAccess.KnownFor(doc, BuiltInCategory.OST_Walls, kv.Key, sample);
                    string at = JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key);
                    if (known == false) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "walls have no parameter '" + kv.Key + "'"); ok = false; }
                    else if (known is null) OpContext.Warning(issues, at, RevitIssueCodes.Unchecked, "parameter '" + kv.Key + "' could not be checked before the wall exists");
                }
            }
            return ok ? p : null;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            var doc = ctx.Doc;
            var u = ctx.Units;
            double z = p.Level.ProjectElevation;
            // An unconnected height is required by Wall.Create; with a top level it is replaced by the constraint below.
            double height = p.Height.HasValue ? u.LengthToInternal(p.Height.Value)
                : Math.Max(u.LengthToleranceInternal * 10, p.Top!.ProjectElevation + u.LengthToInternal(p.TopOffset) - z - u.LengthToInternal(p.BaseOffset));
            string? mark = args.GetStringOrNull("mark");
            var parameters = args.GetMap("parameters");
            var ids = new JsonArray();
            for (int i = 0; i < p.Segments.Count; i++)
            {
                var s = p.Segments[i];
                var line = Line.CreateBound(u.PointToInternal(s.Key, z), u.PointToInternal(s.Value, z));
                var wall = Wall.Create(doc, line, p.Type.Id, p.Level.Id, height, u.LengthToInternal(p.BaseOffset), args.GetBool("flip"), args.GetBool("structural"));
                if (p.Top != null)
                {
                    Set(wall, BuiltInParameter.WALL_HEIGHT_TYPE, p.Top.Id);
                    Set(wall, BuiltInParameter.WALL_TOP_OFFSET, u.LengthToInternal(p.TopOffset));
                }
                if (p.Line != WallLocationLine.WallCenterline)
                {
                    Set(wall, BuiltInParameter.WALL_KEY_REF_PARAM, (int)p.Line);
                    if (wall.Location is LocationCurve lc)
                    {
                        double cz = lc.Curve.GetEndPoint(0).Z;
                        lc.Curve = Line.CreateBound(u.PointToInternal(s.Key, cz), u.PointToInternal(s.Value, cz));
                    }
                }
                if (args.Get("joins") is JsonString j && j.Value == "disallow")
                {
                    WallUtils.DisallowWallJoinAtEnd(wall, 0);
                    WallUtils.DisallowWallJoinAtEnd(wall, 1);
                }
                Set(wall, BuiltInParameter.WALL_ATTR_ROOM_BOUNDING, args.GetBool("roomBounding") ? 1 : 0);
                if (mark != null) Set(wall, BuiltInParameter.ALL_MODEL_MARK, p.Segments.Count > 1 ? mark + "-" + (i + 1).ToString(CultureInfo.InvariantCulture) : mark);
                foreach (var kv in parameters)
                {
                    var prm = ParamAccess.Find(wall, kv.Key, out string? err) ?? throw new OpFailedException("parameter_not_found", err ?? kv.Key);
                    if (prm.IsReadOnly) throw new OpFailedException("parameter_read_only", "parameter '" + kv.Key + "' is read-only on walls");
                    if (!ParamAccess.TryPrepare(doc, prm, kv.Value, u, out var pv, out string perr))
                        throw new OpFailedException("parameter_value", "parameter '" + kv.Key + "': " + perr);
                    if (!ParamAccess.Apply(prm, pv)) throw new OpFailedException("parameter_value", "Revit refused the value for '" + kv.Key + "'");
                }
                string id = ElementIds.ToWire(wall.Id);
                result.Created.Add(id);
                ids.Add(id);
            }
            result.Outputs.Add("walls", ids);
        }

        private static void Set(Element e, BuiltInParameter bip, int v)
        {
            var p = e.get_Parameter(bip) ?? throw new OpFailedException("parameter_not_found", "the wall has no " + bip + " parameter");
            if (!p.Set(v)) throw new OpFailedException("parameter_value", "Revit refused " + bip + " = " + v);
        }

        private static void Set(Element e, BuiltInParameter bip, double v)
        {
            var p = e.get_Parameter(bip) ?? throw new OpFailedException("parameter_not_found", "the wall has no " + bip + " parameter");
            if (!p.Set(v)) throw new OpFailedException("parameter_value", "Revit refused " + bip);
        }

        private static void Set(Element e, BuiltInParameter bip, ElementId v)
        {
            var p = e.get_Parameter(bip) ?? throw new OpFailedException("parameter_not_found", "the wall has no " + bip + " parameter");
            if (!p.Set(v)) throw new OpFailedException("parameter_value", "Revit refused " + bip);
        }

        private static void Set(Element e, BuiltInParameter bip, string v)
        {
            var p = e.get_Parameter(bip) ?? throw new OpFailedException("parameter_not_found", "the wall has no " + bip + " parameter");
            if (!p.Set(v)) throw new OpFailedException("parameter_value", "Revit refused " + bip);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            if (p is null)
                check.Fail("could not resolve the request again to compare the walls: " + string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            var walls = new JsonArray();
            var lengths = new JsonArray();
            string? mark = args.GetStringOrNull("mark");
            var parameters = args.GetMap("parameters");
            for (int i = 0; i < result.Created.Count; i++)
            {
                string label = "wall " + (i + 1);
                if (!ElementIds.TryParse(result.Created[i], out var id) || !(doc.GetElement(id) is Wall w))
                {
                    check.Fail(label + " (" + result.Created[i] + ") no longer exists after commit");
                    continue;
                }
                var rb = new JsonObject().Add("id", result.Created[i]);
                var seg = p != null && i < p.Segments.Count ? p.Segments[i] : (KeyValuePair<Point2, Point2>?)null;
                if (w.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    check.Point(rb, "start", c.GetEndPoint(0), seg?.Key, label + " start");
                    check.Point(rb, "end", c.GetEndPoint(1), seg?.Value, label + " end");
                    check.Length(rb, "length", c.Length, seg.HasValue ? (seg.Value.Value - seg.Value.Key).Length : (double?)null, label + " length");
                    lengths.Add(RevitUnits.Num(u.LengthFromInternal(c.Length)));
                }
                else check.Fail(label + " has no location curve");
                check.Text(rb, "type", w.WallType?.Name, p?.Type.Name, label + " type");
                check.Text(rb, "level", Lookup.LevelName(doc, w.LevelId), p?.Level.Name, label + " level");
                var bo = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (bo != null) check.Length(rb, "baseOffset", bo.AsDouble(), args.GetLength("baseOffset"), label + " base offset");
                var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                string topName = ElementIds.IsValid(topId) ? Lookup.LevelName(doc, topId) : "unconnected";
                check.Text(rb, "topConstraint", topName, p?.Top != null ? p.Top.Name : "unconnected", label + " top constraint");
                if (p?.Top != null)
                {
                    var to = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                    if (to != null) check.Length(rb, "topOffset", to.AsDouble(), args.GetLength("topOffset"), label + " top offset");
                }
                else
                {
                    var hp = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (hp != null) check.Length(rb, "height", hp.AsDouble(), args.GetLengthOrNull("height"), label + " height");
                }
                var kr = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (kr != null) check.Text(rb, "locationLine", LocationLineName(kr.AsInteger()), args.GetString("locationLine"), label + " location line");
                var st = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
                if (st != null) check.Flag(rb, "structural", st.AsInteger() == 1, args.GetBool("structural"), label + " structural");
                check.Flag(rb, "flipped", w.Flipped, args.GetBool("flip"), label + " flipped");
                var bound = w.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
                if (bound != null) check.Flag(rb, "roomBounding", bound.AsInteger() == 1, args.GetBool("roomBounding"), label + " room bounding");
                if (mark != null)
                {
                    string want = result.Created.Count > 1 ? mark + "-" + (i + 1).ToString(CultureInfo.InvariantCulture) : mark;
                    check.Text(rb, "mark", w.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), want, label + " mark");
                }
                if (parameters.Count > 0)
                {
                    var po = new JsonObject();
                    foreach (var kv in parameters)
                    {
                        var prm = ParamAccess.Find(w, kv.Key, out string? err);
                        if (prm is null) { check.Fail(label + ": " + err); continue; }
                        po.Add(kv.Key, ParamAccess.Read(prm, u));
                        if (ParamAccess.TryPrepare(doc, prm, kv.Value, u, out var pv, out _) && !ParamAccess.Matches(prm, pv, u))
                            check.Fail(label + " parameter '" + kv.Key + "' reads " + ParamAccess.Read(prm, u).ToJson() + ", requested " + kv.Value.ToJson());
                    }
                    rb.Add("parameters", po);
                }
                walls.Add(rb);
            }
            if (p != null && result.Created.Count != p.Segments.Count)
                check.Fail(result.Created.Count + " wall(s) created, expected " + p.Segments.Count);
            result.Readback.Add("count", walls.Count);
            if (walls.Count == 1)
                foreach (var kv in (JsonObject)walls[0]) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            result.Readback.Add("walls", walls);
            result.Outputs.Set("lengths", lengths);
            check.Apply(result);
        }
    }
}
