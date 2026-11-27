using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// room.place: a room at a point, or one in every enclosed region of a level that has none, in a chosen phase
    /// (default: the project's last phase).
    ///
    /// Not-enclosed and redundant rooms are never left in the model (rooms-areas.md, RV-037): a point already inside
    /// a room of the level is refused in Validate; after placement (NewRoom regenerates) a room with no area, or one
    /// sharing its region with another room, fails the op, which rolls it back, and the failure lists the room and
    /// the room it collides with. Readback checks the same on the committed model.
    ///
    /// Defaults chosen for practice: the upper limit is the next level up with offset 0 when upperLimit is not given
    /// (a room limited to its own level gives a wrong volume); a number already used by another room is refused
    /// (RV-005); with allEnclosed a number is refused, because every room would get the same one; enclosed regions
    /// under 0.5 m2 (wall cavities and slivers) are skipped and reported. The room is tagged in the given view or in
    /// the level's floor plan unless tag is false.
    ///
    /// Phase: every room is made with NewRoom(phase) (an unplaced room of that phase) and placed with
    /// NewRoom(room, circuit) in a circuit of PlanTopology(level, phase). For allEnclosed that is every circuit without
    /// a room. For a point, PlanCircuit has no point test, so each free circuit is tried, nearest GetPointInside first,
    /// inside a SubTransaction that is kept only when the placed room contains the point (Room.IsPointInRoom). In the
    /// last phase a point room is first placed with NewRoom(level, uv), which needs no enclosure; it is kept only when
    /// its ROOM_PHASE is the requested phase. So allowNotEnclosed works only in the last phase: a room no boundary
    /// encloses has no circuit. The redundancy check, the tag view (VIEW_PHASE) and readback all use the phase.
    /// </summary>
    public sealed class RoomPlace : OpHandler
    {
        public override string Name => "room.place";

        private static double MinRegionArea => UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.SquareMeters);

        private sealed class Plan
        {
            public Level Level = null!;
            public XYZ? Point;
            public Level? Upper;
            public double LimitOffset;
            public bool UpperGiven;
            public View? TagView;
            public FamilySymbol? TagType;
            public Phase Phase = null!;
            public bool AllowNotEnclosed;
        }

        private static Phase? FindPhase(Document doc, string name, out string error, out string code)
        {
            var phases = doc.Phases.Cast<Phase>().ToList();
            var hit = phases.Where(x => string.Equals(x.Name, name, StringComparison.Ordinal)).ToList();
            if (hit.Count == 0) hit = phases.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            code = hit.Count == 0 ? RevitIssueCodes.NotFound : RevitIssueCodes.Ambiguous;
            error = hit.Count == 1 ? "" : "phase '" + name + "' " + (hit.Count == 0 ? "not found" : "is ambiguous") + "; phases: " + string.Join(", ", phases.Select(x => "'" + x.Name + "'"));
            return hit.Count == 1 ? hit[0] : null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool afterCommit = false)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            var lv = Lookup.Level(doc, args.GetString("level"));
            if (!lv.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!); return null; }
            p.Level = lv.Value!;
            bool all = args.GetBool("allEnclosed");
            if (args.Has("allEnclosed") && !all) OpContext.Error(issues, ctx.FieldPath(args, "allEnclosed"), RevitIssueCodes.Invalid, "allEnclosed:false places nothing; give point instead");
            if (args.Has("point")) p.Point = u.PointToInternal(args.GetPoint2("point"), p.Level.ProjectElevation);

            var lastPhase = Kit.LastPhase(doc);
            if (args.GetStringOrNull("phase") is string phaseName)
            {
                var ph = FindPhase(doc, phaseName, out string phaseError, out string phaseCode);
                if (ph is null) { OpContext.Error(issues, ctx.FieldPath(args, "phase"), phaseCode, phaseError); return null; }
                p.Phase = ph;
            }
            else if (doc.GetElement(lastPhase) is Phase lp) p.Phase = lp;
            else { OpContext.Error(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.NotFound, "the project has no phase"); return null; }
            p.AllowNotEnclosed = args.GetBool("allowNotEnclosed");
            if (p.AllowNotEnclosed && all)
                OpContext.Error(issues, ctx.FieldPath(args, "allowNotEnclosed"), RevitIssueCodes.Invalid, "allowNotEnclosed applies to a room at a point; allEnclosed places rooms only in enclosed regions");
            else if (p.AllowNotEnclosed && p.Phase.Id != lastPhase)
                OpContext.Error(issues, ctx.FieldPath(args, "allowNotEnclosed"), RevitIssueCodes.Invalid, "a room that no boundary encloses can be placed only in the last phase ('" + doc.GetElement(lastPhase)?.Name +
                    "'): the API places such a room at a point with no choice of phase; close the boundary, or leave out phase");

            if (args.GetStringOrNull("number") is string number)
            {
                if (number.Trim().Length == 0) OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Invalid, "room number is empty");
                if (all) OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Invalid, "a number with allEnclosed would give every new room the same number; place them without one and number them afterwards");
                else if (!afterCommit)
                {
                    var dup = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                        .FirstOrDefault(r => string.Equals(r.Number, number, StringComparison.Ordinal));
                    if (dup != null) OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Exists, "room number '" + number + "' is already used by room " + Kit.Id(dup.Id) + " ('" + dup.Name + "'); numbers must be unique");
                }
            }
            if (args.GetStringOrNull("name") is string nm && nm.Trim().Length == 0) OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid, "room name is empty");

            if (args.GetStringOrNull("upperLimit") is string up)
            {
                var ul = Lookup.Level(doc, up);
                if (!ul.Ok) OpContext.Error(issues, ctx.FieldPath(args, "upperLimit"), ul.Code, ul.Error!);
                else { p.Upper = ul.Value; p.UpperGiven = true; }
            }
            else
            {
                p.Upper = Lookup.Levels(doc).Where(l => l.ProjectElevation > p.Level.ProjectElevation + u.LengthToleranceInternal).OrderBy(l => l.ProjectElevation).FirstOrDefault();
                if (p.Upper is null && !args.Has("limitOffset"))
                    OpContext.Warning(issues, ctx.FieldPath(args, "upperLimit"), RevitIssueCodes.Unchecked, "there is no level above '" + p.Level.Name + "'; Revit's default room height is kept (give upperLimit/limitOffset)");
            }
            p.LimitOffset = u.LengthToInternal(args.GetLengthOrNull("limitOffset") ?? 0);
            if (p.Upper != null && p.Upper.ProjectElevation + p.LimitOffset <= p.Level.ProjectElevation + u.LengthToleranceInternal)
                OpContext.Error(issues, ctx.FieldPath(args, "upperLimit"), RevitIssueCodes.Invalid, "the room's top ('" + p.Upper.Name + "' + limitOffset) is not above its level; set the upper limit to the level above or the ceiling");

            if (args.GetBool("tag"))
            {
                if (args.Has("view"))
                {
                    if (!ctx.IsPending(args, "view"))
                    {
                        var v = Lookup.View(doc, args.Get("view"));
                        if (!v.Ok) OpContext.Error(issues, ctx.FieldPath(args, "view"), v.Code, v.Error!);
                        else if (!(v.Value is ViewPlan vp) || vp.GenLevel is null || vp.GenLevel.Id != p.Level.Id)
                            OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view '" + v.Value!.Name + "' is not a plan of level '" + p.Level.Name + "'");
                        else if (Kit.ViewPhase(vp) != p.Phase.Id)
                            OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Mismatch,
                                "view '" + vp.Name + "' shows phase '" + doc.GetElement(Kit.ViewPhase(vp))?.Name + "', and the rooms go in phase '" + p.Phase.Name + "'; give a plan of that phase, or tag:false");
                        else p.TagView = vp;
                    }
                }
                else
                {
                    p.TagView = Kit.PlanFor(doc, p.Level, ViewType.FloorPlan, p.Phase.Id);
                    if (p.TagView is null) OpContext.Error(issues, ctx.FieldPath(args, "tag"), RevitIssueCodes.NotFound, "level '" + p.Level.Name + "' has no floor plan in phase '" + p.Phase.Name + "' to tag the room in; give view or set tag:false");
                }
                if (args.GetStringOrNull("tagType") is string tt)
                {
                    var s = Lookup.Symbol(doc, BuiltInCategory.OST_RoomTags, tt, "room tag type");
                    if (!s.Ok) OpContext.Error(issues, ctx.FieldPath(args, "tagType"), s.Code, s.Error!);
                    else p.TagType = s.Value;
                }
            }
            if (all && Kit.PlanFor(doc, p.Level) is null)
                OpContext.Error(issues, ctx.FieldPath(args, "allEnclosed"), RevitIssueCodes.NotFound, "Revit needs a plan view of '" + p.Level.Name + "' to place rooms in its regions");

            Kit.CheckParameterNames(ctx, args, "parameters", BuiltInCategory.OST_Rooms, new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().FirstElement(), issues, "rooms");

            if (p.Point != null && !afterCommit)
            {
                var there = Rooms.At(doc, p.Level, p.Point, p.Phase.Id);
                if (there.Count > 0)
                    OpContext.Error(issues, ctx.FieldPath(args, "point"), RevitIssueCodes.Exists, "the point is already inside room " + Kit.Id(there[0].Id) + " ('" + there[0].Number + " " + there[0].Name +
                        "'); a second room there would be redundant. Change that room with param.set instead");
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var rooms = new List<Room>();
            if (p!.Point != null)
            {
                rooms.Add(PlaceAt(doc, p, u));
            }
            else
            {
                var skipped = new List<string>();
                foreach (var c in doc.get_PlanTopology(p.Level, p.Phase).Circuits.Cast<PlanCircuit>().ToList())
                {
                    if (c.IsRoomLocated) continue;
                    if (c.Area < MinRegionArea) { skipped.Add(Kit.Num(u.AreaFromInternal(c.Area)) + " " + u.AreaSymbol); continue; }
                    var unplaced = doc.Create.NewRoom(p.Phase) ?? throw new OpFailedException("create_failed", "Revit did not create a room in phase '" + p.Phase.Name + "'");
                    rooms.Add(doc.Create.NewRoom(unplaced, c) ?? throw new OpFailedException("create_failed", "Revit did not place a room in an enclosed region"));
                }
                if (skipped.Count > 0) ctx.Warn(skipped.Count + " enclosed region(s) under 0.5 m2 were left without a room: " + string.Join(", ", skipped.Take(10)));
                if (rooms.Count == 0) ctx.Warn("no enclosed region of '" + p.Level.Name + "' was without a room; nothing was placed");
            }
            doc.Regenerate();
            foreach (var room in rooms)
            {
                // Never leave a not-enclosed or redundant room behind: fail the op so it rolls back.
                var others = Rooms.Sharing(doc, room);
                if (others.Count > 0)
                    throw new OpFailedException("redundant_room", "the new room shares its enclosed region with room " + Kit.Id(others[0].Id) + " ('" + others[0].Number + " " + others[0].Name + "'); the op was rolled back",
                        new JsonObject().Add("room", Kit.Id(room.Id)).Add("existing", JsonArray.FromStrings(others.Select(o => Kit.Id(o.Id)))));
                if (Rooms.PhaseOf(room) != p.Phase.Id)
                    throw new OpFailedException("phase_mismatch", "room " + Kit.Id(room.Id) + " is in phase '" + doc.GetElement(Rooms.PhaseOf(room))?.Name + "', not '" + p.Phase.Name + "'; the op was rolled back");
                if (room.Area <= 0 && p.AllowNotEnclosed)
                    ctx.Warn("room " + Kit.Id(room.Id) + " is not enclosed (area 0), as allowNotEnclosed permits; close its boundary with walls or room separation lines");
                else if (room.Area <= 0)
                    throw new OpFailedException("not_enclosed", "the room at [" + string.Join(", ", u.PointJson(Rooms.Point(room) ?? XYZ.Zero).Select(v => v.ToJson())) + "] on '" + p.Level.Name +
                        "' is not enclosed: close the boundary with walls or room separation lines first; the op was rolled back");
                if (args.GetStringOrNull("name") is string name) room.Name = name;
                if (args.GetStringOrNull("number") is string number) room.Number = number;
                if (p.Upper != null) { room.UpperLimit = p.Upper; room.LimitOffset = p.LimitOffset; }
                else if (args.Has("limitOffset")) room.LimitOffset = p.LimitOffset;
                Kit.SetParameters(doc, room, args.GetMap("parameters"), u, "room " + Kit.Id(room.Id));
                result.Created.Add(Kit.Id(room.Id));
            }
            if (args.GetBool("tag") && p.TagView != null && rooms.Count > 0)
            {
                result.Readback.Set("tagView", Kit.Id(p.TagView.Id));
                foreach (var room in rooms)
                {
                    var at = Rooms.Point(room) ?? throw new OpFailedException("create_failed", "room " + Kit.Id(room.Id) + " has no location point");
                    var tag = doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(at.X, at.Y), p.TagView.Id)
                              ?? throw new OpFailedException("tag_failed", "Revit did not tag room " + Kit.Id(room.Id) + " in '" + p.TagView.Name + "'");
                    if (p.TagType != null && tag.GetTypeId() != p.TagType.Id) tag.ChangeTypeId(p.TagType.Id);
                    result.Created.Add(Kit.Id(tag.Id));
                }
            }
            result.Outputs.Add("rooms", JsonArray.FromStrings(rooms.Select(r => Kit.Id(r.Id))));
        }

        /// <summary>A room of the plan's phase containing the plan point; not_enclosed when there is none.</summary>
        private static Room PlaceAt(Document doc, Plan p, RevitUnits u)
        {
            var point = p.Point!;
            if (p.Phase.Id == Kit.LastPhase(doc))
            {
                var room = doc.Create.NewRoom(p.Level, new UV(point.X, point.Y)) ?? throw new OpFailedException("create_failed", "Revit did not create the room");
                if (Rooms.PhaseOf(room) == p.Phase.Id) return room;
                doc.Delete(room.Id);
            }
            var unplaced = doc.Create.NewRoom(p.Phase) ?? throw new OpFailedException("create_failed", "Revit did not create a room in phase '" + p.Phase.Name + "'");
            var candidates = doc.get_PlanTopology(p.Level, p.Phase).Circuits.Cast<PlanCircuit>().Where(c => !c.IsRoomLocated)
                .Select(c => new { Circuit = c, Distance = Inside(c) is UV uv ? uv.DistanceTo(new UV(point.X, point.Y)) : double.MaxValue })
                .OrderBy(x => x.Distance).Select(x => x.Circuit).ToList();
            foreach (var c in candidates)
            {
                using (var probe = new SubTransaction(doc))
                {
                    probe.Start();
                    bool hit;
                    try { hit = doc.Create.NewRoom(unplaced, c) is Room placed && placed.Area > 0 && placed.IsPointInRoom(Rooms.ProbeIn(placed, point)); }
                    catch (Exception) { hit = false; }
                    if (hit) { probe.Commit(); return unplaced; }
                    probe.RollBack();
                }
            }
            throw new OpFailedException("not_enclosed", "no enclosed region of '" + p.Level.Name + "' in phase '" + p.Phase.Name + "' without a room contains [" +
                string.Join(", ", u.PointJson(point).Select(v => v.ToJson())) + "]: close the boundary with walls or room separation lines first; the op was rolled back");
        }

        private static UV? Inside(PlanCircuit c)
        {
            try { return c.GetPointInside(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var p = Build(ctx, args, new List<Issue>(), afterCommit: true);
            if (p != null && result.Readback.GetStringOrNull("tagView") is string tv && ElementIds.TryParse(tv, out var tvId) && doc.GetElement(tvId) is View tagView)
                p.TagView = tagView;
            var ids = Refs.ElementIds(result.Outputs["rooms"]);
            var arr = new JsonArray();
            var areas = new JsonArray();
            var tags = p?.TagView != null
                ? new FilteredElementCollector(doc, p.TagView.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType().OfType<RoomTag>().ToList()
                : new List<RoomTag>();
            foreach (var idText in ids)
            {
                if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Room room))
                {
                    check.Fail("room " + idText + " no longer exists after commit");
                    continue;
                }
                string label = "room " + idText;
                var rb = new JsonObject().Add("id", idText);
                check.Text(rb, "level", Lookup.LevelName(doc, room.LevelId), p?.Level.Name, label + " level");
                var lp = Rooms.Point(room);
                if (lp != null) check.Point(rb, "point", lp, args.Has("point") ? args.GetPoint2("point") : (SA.Core.Geometry.Point2?)null, label + " location");
                if (args.GetStringOrNull("name") is string name) check.Text(rb, "name", room.Name, name, label + " name");
                else rb.Add("name", room.Name);
                if (args.GetStringOrNull("number") is string number) check.Text(rb, "number", room.Number, number, label + " number");
                else rb.Add("number", room.Number);
                var state = Rooms.Classify(doc, room, out var sharing);
                rb.Add("state", Rooms.Name(state));
                check.Text(rb, "phase", doc.GetElement(Rooms.PhaseOf(room))?.Name, p?.Phase.Name, label + " phase");
                if (state == RoomCondition.NotEnclosed && p != null && p.AllowNotEnclosed) { }
                else if (state != RoomCondition.Placed) check.Fail(label + " is " + Rooms.Name(state));
                else if (sharing.Count > 0) check.Fail(label + " shares its region with room " + Kit.Id(sharing[0].Id) + " (redundant room)");
                rb.Add("area", RevitUnits.Num(u.AreaFromInternal(room.Area)));
                areas.Add(RevitUnits.Num(u.AreaFromInternal(room.Area)));
                if (p?.Upper != null)
                {
                    check.Text(rb, "upperLimit", room.UpperLimit?.Name, p.Upper.Name, label + " upper limit");
                    check.Length(rb, "limitOffset", room.LimitOffset, u.LengthFromInternal(p.LimitOffset), label + " limit offset");
                }
                rb.Add("unboundedHeight", u.LengthJson(room.UnboundedHeight));
                if (args.GetBool("tag"))
                {
                    var tag = tags.FirstOrDefault(t => t.TaggedLocalRoomId == room.Id);
                    check.Flag(rb, "tagged", tag != null, true, label + " tag in '" + (p?.TagView?.Name ?? "?") + "'");
                    if (tag != null && p?.TagType != null)
                        check.Text(rb, "tagType", doc.GetElement(tag.GetTypeId()) is ElementType tt ? Lookup.FamilyTypeName(tt) : null, Lookup.FamilyTypeName(p.TagType), label + " tag type");
                }
                Kit.CheckParameters(doc, room, args.GetMap("parameters"), u, check, rb, label);
                arr.Add(rb);
            }
            if (args.Has("point") && ids.Count != 1) check.Fail(ids.Count + " room(s) placed, expected 1");
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("areaUnit", u.AreaSymbol);
            result.Readback.Add("rooms", arr);
            result.Outputs.Set("areas", areas);
            check.Apply(result);
        }
    }
}
