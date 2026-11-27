using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling.Support
{
    internal enum RoomCondition { Placed, Unplaced, NotEnclosed, Redundant }

    /// <summary>
    /// Room state as the practice notes define it (rooms-areas.md, RV-037).
    /// Unplaced: no location. Not enclosed: placed, area 0, and no other room covers its point. Redundant: placed, and
    /// another room of the same level and phase covers its point (whichever of the two Revit gave the area to).
    /// </summary>
    internal static class Rooms
    {
        public static string Name(RoomCondition c) =>
            c == RoomCondition.Placed ? "placed" : c == RoomCondition.Unplaced ? "unplaced" : c == RoomCondition.NotEnclosed ? "not_enclosed" : "redundant";

        public static XYZ? Point(Room room) => (room.Location as LocationPoint)?.Point;

        public static ElementId PhaseOf(Room r) => r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() ?? ElementId.InvalidElementId;

        /// <summary>A point inside the room's volume above a plan point (level + base offset + 300 mm).</summary>
        public static XYZ ProbeIn(Room r, XYZ plan)
        {
            double z = (r.Level?.ProjectElevation ?? plan.Z) + r.BaseOffset + Kit.ProbeHeight;
            return new XYZ(plan.X, plan.Y, z);
        }

        /// <summary>Other placed rooms of the same level and phase that cover this room's point, or whose point this room covers.</summary>
        public static List<Room> Sharing(Document doc, Room room)
        {
            var list = new List<Room>();
            var p = Point(room);
            if (p is null) return list;
            var phase = PhaseOf(room);
            foreach (var other in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                if (other.Id == room.Id || other.LevelId != room.LevelId) continue;
                var op = Point(other);
                if (op is null) continue;
                var oph = PhaseOf(other);
                if (ElementIds.IsValid(phase) && ElementIds.IsValid(oph) && phase != oph) continue;
                bool hit = false;
                try
                {
                    if (other.Area > 0 && other.IsPointInRoom(ProbeIn(other, p))) hit = true;
                    else if (room.Area > 0 && room.IsPointInRoom(ProbeIn(room, op))) hit = true;
                }
                catch (Exception) { }
                if (hit) list.Add(other);
            }
            return list;
        }

        public static RoomCondition Classify(Document doc, Room room, out List<Room> sharing)
        {
            sharing = new List<Room>();
            if (room.Location is null) return RoomCondition.Unplaced;
            sharing = Sharing(doc, room);
            // The room Revit gave the area to is the placed one; the other one in the same region is the redundant one.
            if (room.Area > 0) return RoomCondition.Placed;
            return sharing.Count > 0 ? RoomCondition.Redundant : RoomCondition.NotEnclosed;
        }

        /// <summary>Placed rooms on a level whose volume contains the plan point.</summary>
        /// <summary>
        /// Placed rooms of the level containing the plan point; with <paramref name="phase"/>, only rooms of that phase
        /// (a room of another phase at the point is not redundant with a new one).
        /// </summary>
        public static List<Room> At(Document doc, Level level, XYZ plan, ElementId? phase = null) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                .Where(r => r.LevelId == level.Id && r.Location != null && r.Area > 0 &&
                            (phase is null || !ElementIds.IsValid(PhaseOf(r)) || PhaseOf(r) == phase) && SafeIn(r, plan)).ToList();

        private static bool SafeIn(Room r, XYZ plan)
        {
            try { return r.IsPointInRoom(ProbeIn(r, plan)); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>An insert's extent along its host wall and in height, internal units.</summary>
    internal sealed class OpeningSpan
    {
        public ElementId Id = ElementId.InvalidElementId;
        public string Category = "";
        public double From, To;
        public double? Bottom, Top;
    }

    /// <summary>Hosted openings on walls: where they sit along the wall and how wide and high they are.</summary>
    internal static class Openings
    {
        /// <summary>Distance along the curve from its start to the projection of p (internal), or null.</summary>
        public static double? Along(Curve c, XYZ p)
        {
            var flat = new XYZ(p.X, p.Y, c.GetEndPoint(0).Z);
            var r = c.Project(flat);
            if (r is null) return null;
            return c.ComputeNormalizedParameter(r.Parameter) * c.Length;
        }

        /// <summary>The point at a distance along the curve from its start (internal).</summary>
        public static XYZ At(Curve c, double along) => c.Evaluate(Math.Max(0, Math.Min(1, along / c.Length)), true);

        public static double? Width(Element? instance, Element? type) =>
            Kit.ParamDouble(instance, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM)
            ?? Kit.ParamDouble(type, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM);

        public static double? Height(Element? instance, Element? type) =>
            Kit.ParamDouble(instance, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM)
            ?? Kit.ParamDouble(type, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM);

        public static OpeningSpan? Span(Document doc, FamilyInstance fi, Curve hostCurve)
        {
            if (!(fi.Location is LocationPoint lp)) return null;
            var along = Along(hostCurve, lp.Point);
            var type = doc.GetElement(fi.GetTypeId());
            var w = Width(fi, type);
            if (along is null || w is null) return null;
            double? bottom = Kit.ParamDouble(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            double? top = Kit.ParamDouble(fi, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM);
            var h = Height(fi, type);
            if (bottom is null && fi.Category?.BuiltInCategory == BuiltInCategory.OST_Doors) bottom = 0;
            if (top is null && bottom.HasValue && h.HasValue) top = bottom + h;
            return new OpeningSpan
            {
                Id = fi.Id, Category = fi.Category?.Name ?? "", From = along.Value - w.Value / 2, To = along.Value + w.Value / 2, Bottom = bottom, Top = top,
            };
        }

        /// <summary>Doors and windows hosted by the wall, with their spans (inserts whose extent is unknown are left out).</summary>
        public static List<OpeningSpan> OnWall(Document doc, Wall wall, ElementId? except, out int unknown)
        {
            unknown = 0;
            var list = new List<OpeningSpan>();
            if (!(wall.Location is LocationCurve lc)) return list;
            foreach (var id in wall.FindInserts(true, false, true, false))
            {
                if (except != null && id == except) continue;
                if (!(doc.GetElement(id) is FamilyInstance fi)) { unknown++; continue; }
                var s = Span(doc, fi, lc.Curve);
                if (s is null) unknown++;
                else list.Add(s);
            }
            return list;
        }

        public static OpeningSpan? Clash(OpeningSpan me, IEnumerable<OpeningSpan> others, double tol)
        {
            foreach (var o in others)
            {
                bool along = me.From < o.To - tol && o.From < me.To - tol;
                if (!along) continue;
                bool vertical = !(me.Bottom.HasValue && me.Top.HasValue && o.Bottom.HasValue && o.Top.HasValue) ||
                                (me.Bottom.Value < o.Top.Value - tol && o.Bottom.Value < me.Top.Value - tol);
                if (vertical) return o;
            }
            return null;
        }
    }

    /// <summary>Deletion planning: which elements Revit will delete along with the listed ones.</summary>
    internal static class Deletion
    {
        /// <summary>
        /// Splits the dependents (Element.GetDependentElements) of the listed elements into parts of the element
        /// itself (its sketch, sketch plane and sketch lines, elements without a model or annotation category) and
        /// separate elements a user would notice (hosted inserts, tags, instances of a deleted type, view-specific
        /// annotation).
        /// </summary>
        public static void Dependents(Document doc, ICollection<ElementId> listed, out List<ElementId> parts, out List<ElementId> separate)
        {
            parts = new List<ElementId>();
            separate = new List<ElementId>();
            var seen = new HashSet<long>(listed.Select(ElementIds.ToLong));
            foreach (var id in listed)
            {
                var e = doc.GetElement(id);
                if (e is null) continue;
                IList<ElementId> deps;
                try { deps = e.GetDependentElements(null); }
                catch (Exception) { continue; }
                foreach (var d in deps)
                {
                    if (!seen.Add(ElementIds.ToLong(d))) continue;
                    if (IsPart(doc.GetElement(d))) parts.Add(d);
                    else separate.Add(d);
                }
            }
        }

        public static bool IsPart(Element? e)
        {
            if (e is null) return true;
            if (e is Sketch || e is SketchPlane) return true;
            var cat = e.Category;
            if (cat is null) return true;
            if (cat.BuiltInCategory == BuiltInCategory.OST_SketchLines) return true;
            return cat.CategoryType != CategoryType.Model && cat.CategoryType != CategoryType.Annotation;
        }

        public static JsonObject Describe(Document doc, ElementId id)
        {
            var e = doc.GetElement(id);
            return new JsonObject().Add("id", ElementIds.ToWire(id))
                .Add("category", e?.Category?.Name ?? "")
                .Add("name", e?.Name ?? "");
        }
    }

    /// <summary>Element selection by listed ids or by a filter object (category, level, type, where, view).</summary>
    internal static class ElementSelect
    {
        public static List<Element> Resolve(OpContext ctx, OpArgs args, string listField, string filterField, List<Issue> issues, out bool pending)
        {
            var doc = ctx.Doc;
            var list = new List<Element>();
            pending = false;
            if (args.Has(listField))
            {
                foreach (var kv in Kit.IdList(ctx, args, listField, out pending))
                {
                    if (!ElementIds.TryParse(kv.Value, out var id) || !(doc.GetElement(id) is Element e))
                        OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist");
                    else list.Add(e);
                }
                return list;
            }
            var f = args.GetObject(filterField);
            if (f is null) return list;
            var cat = Lookup.Category(doc, f.GetString("category"), out string? cerr);
            if (cat is null) { OpContext.Error(issues, f.Path + ".category", RevitIssueCodes.NotFound, cerr!); return list; }
            FilteredElementCollector col;
            if (f.Has("view"))
            {
                if (OpContext.IsPending(f.Raw["view"])) { pending = true; return list; }
                var v = Lookup.View(doc, f.Get("view"));
                if (!v.Ok) { OpContext.Error(issues, f.Path + ".view", v.Code, v.Error!); return list; }
                col = new FilteredElementCollector(doc, v.Value!.Id);
            }
            else col = new FilteredElementCollector(doc);
            IEnumerable<Element> q = col.OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();
            if (f.GetStringOrNull("level") is string lname)
            {
                var lv = Lookup.Level(doc, lname);
                if (!lv.Ok) { OpContext.Error(issues, f.Path + ".level", lv.Code, lv.Error!); return list; }
                var lid = lv.Value!.Id;
                q = q.Where(e => e.LevelId == lid);
            }
            if (f.GetStringOrNull("type") is string tname)
                q = q.Where(e => doc.GetElement(e.GetTypeId()) is ElementType t &&
                                 (string.Equals(t.Name, tname, StringComparison.Ordinal) || string.Equals(Lookup.FamilyTypeName(t), tname, StringComparison.Ordinal)));
            var where = f.GetMap("where");
            if (where.Count > 0)
                q = q.Where(e => where.All(kv =>
                {
                    var p = ParamAccess.Find(e, kv.Key, out _);
                    if (p is null) return false;
                    if (kv.Value is JsonString s && p.StorageType != StorageType.String)
                        return string.Equals(p.AsValueString(), s.Value, StringComparison.Ordinal);
                    return ParamAccess.TryPrepare(doc, p, kv.Value, ctx.Units, out var pv, out _) && ParamAccess.Matches(p, pv, ctx.Units);
                }));
            list.AddRange(q);
            return list;
        }
    }
}
