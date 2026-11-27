using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling.Support
{
    /// <summary>A floor or ceiling outline: from points (with optional holes) or from a room's finish boundary.</summary>
    internal sealed class HostOutline
    {
        public Level? Level;
        public Room? Room;
        public List<Point2> Outer = new List<Point2>();
        public List<List<Point2>> Holes = new List<List<Point2>>();
        /// <summary>Expected area in internal units (outer minus holes).</summary>
        public double Area;
        public bool Pending;

        /// <summary>The loops at height z (internal): the outer loop first, then the holes.</summary>
        public IList<CurveLoop> Loops(RevitUnits u, double z)
        {
            if (Room != null)
                return Kit.RoomLoops(Room, SpatialElementBoundaryLocation.Finish, z, out string err) ?? throw new OpFailedException("room_boundary", err);
            var loops = new List<CurveLoop> { Kit.ToCurveLoop(Outer, u, z) };
            foreach (var h in Holes) loops.Add(Kit.ToCurveLoop(h, u, z));
            return loops;
        }

        /// <summary>
        /// Reads boundary / fromRoom / openings and level. With fromRoom, the room must be placed and enclosed, and
        /// the level defaults to the room's level. Every loop is checked (size, crossing, holes inside the outer loop
        /// and apart from each other) and the set is checked with BoundaryValidation.IsValidHorizontalBoundary.
        /// </summary>
        public static HostOutline? Read(OpContext ctx, OpArgs args, string holesField, List<Issue> issues, string what)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var o = new HostOutline();
            double shortest = doc.Application.ShortCurveTolerance;
            if (args.GetStringOrNull("level") is string lname)
            {
                var lv = Lookup.Level(doc, lname);
                if (!lv.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!);
                else o.Level = lv.Value;
            }
            if (args.Has("fromRoom"))
            {
                if (ctx.IsPending(args, "fromRoom")) { o.Pending = true; }
                else
                {
                    var e = Kit.Element(doc, args.Get("fromRoom"), out string err);
                    if (e is null) OpContext.Error(issues, ctx.FieldPath(args, "fromRoom"), RevitIssueCodes.NotFound, err);
                    else if (!(e is Room room)) OpContext.Error(issues, ctx.FieldPath(args, "fromRoom"), RevitIssueCodes.Invalid, "element " + Kit.Id(e.Id) + " is not a room");
                    else
                    {
                        var state = Rooms.Classify(doc, room, out _);
                        if (state != RoomCondition.Placed)
                            OpContext.Error(issues, ctx.FieldPath(args, "fromRoom"), RevitIssueCodes.Invalid, "room " + Kit.Id(room.Id) + " is " + Rooms.Name(state) + "; its boundary cannot define a " + what);
                        else
                        {
                            o.Room = room;
                            if (o.Level is null) o.Level = room.Level;
                            else if (room.Level != null && room.Level.Id != o.Level.Id)
                                OpContext.Warning(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Unchecked, "the " + what + " is placed on '" + o.Level.Name + "' but room " + Kit.Id(room.Id) + " is on '" + room.Level.Name + "'");
                            var loops = Kit.RoomLoops(room, SpatialElementBoundaryLocation.Finish, 0, out string lerr);
                            if (loops is null) OpContext.Error(issues, ctx.FieldPath(args, "fromRoom"), RevitIssueCodes.Invalid, lerr);
                            else o.Area = Kit.LoopsArea(loops);
                        }
                    }
                }
            }
            else
            {
                o.Outer = Kit.CleanLoop(args.GetPoint2List("boundary"), u.LengthTolerance);
                string? bad = Kit.CheckLoop(o.Outer, u, shortest, "boundary");
                if (bad != null) { OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Invalid, bad); return null; }
                o.Area = InternalArea(o.Outer, u);
            }
            if (holesField.Length > 0 && args.Has(holesField))
            {
                var raw = args.Get(holesField) as JsonArray ?? new JsonArray();
                for (int i = 0; i < raw.Count; i++)
                {
                    string at = JsonPath.Index(ctx.FieldPath(args, holesField), i);
                    var pts = new List<Point2>();
                    if (raw[i] is JsonArray a) for (int k = 0; k < a.Count; k++) pts.Add(OpArgs.ToPoint2(a[k], u.JobUnit, JsonPath.Index(at, k)));
                    pts = Kit.CleanLoop(pts, u.LengthTolerance);
                    string? bad = Kit.CheckLoop(pts, u, shortest, "opening " + i);
                    if (bad != null) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, bad); continue; }
                    if (o.Room is null && o.Outer.Count >= 3 && !Kit.Inside(pts, o.Outer, u.LengthTolerance))
                    { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "opening " + i + " is not wholly inside the boundary"); continue; }
                    foreach (var other in o.Holes)
                        if (pts.Any(q => new Polygon2(other).Locate(q, u.LengthTolerance) != PointLocation.Outside) || other.Any(q => new Polygon2(pts).Locate(q, u.LengthTolerance) != PointLocation.Outside))
                        { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "opening " + i + " overlaps another opening"); break; }
                    o.Holes.Add(pts);
                    o.Area -= InternalArea(pts, u);
                }
            }
            if (o.Level is null && !o.Pending && !Kit.HasErrors(issues))
                OpContext.Error(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Invalid, "give level (it defaults to the room's level only with fromRoom)");
            if (Kit.HasErrors(issues) || o.Pending) return Kit.HasErrors(issues) ? null : o;
            try
            {
                if (!BoundaryValidation.IsValidHorizontalBoundary(o.Loops(u, o.Level!.ProjectElevation)))
                    OpContext.Error(issues, ctx.FieldPath(args, o.Room != null ? "fromRoom" : "boundary"), RevitIssueCodes.Invalid, "Revit does not accept these loops as a " + what + " boundary (open, crossing or overlapping loops)");
            }
            catch (System.Exception ex) { OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Invalid, "the boundary could not be built: " + ex.Message); }
            return Kit.HasErrors(issues) ? null : o;
        }

        /// <summary>The polygon's area in internal units (points converted first, so no area factor is needed).</summary>
        public static double InternalArea(IList<Point2> pts, RevitUnits u) =>
            new Polygon2(pts.Select(q => new Point2(u.LengthToInternal(q.X), u.LengthToInternal(q.Y)))).Area;
    }
}
