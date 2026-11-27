using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// cleanup.unplacedRooms (destructive): delete listed rooms that are unplaced, and, only when allowed, placed
    /// rooms that are not enclosed (includeNotEnclosed) or redundant (includeRedundant).
    ///
    /// Each id must be a room. Its state is read from the live model (see <see cref="Rooms"/>), not trusted from the
    /// snapshot, so a room that has become placed and enclosed since is refused. A room's dependents (its tags) are
    /// declared and deleted with it and listed in the readback; any other dependent is refused. As in element.delete,
    /// the ids Revit actually deletes are compared with the declared set and anything else fails the op.
    /// </summary>
    public sealed class CleanupUnplacedRooms : OpHandler
    {
        public override string Name => "cleanup.unplacedRooms";

        private sealed class Plan
        {
            public List<ElementId> Ids = new List<ElementId>();
            public Dictionary<long, string> States = new Dictionary<long, string>();
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            bool notEnclosed = args.GetBool("includeNotEnclosed"), redundant = args.GetBool("includeRedundant");
            var listed = Kit.IdList(ctx, args, "ids", out _);
            var seen = new HashSet<long>();
            foreach (var kv in listed)
            {
                if (!ElementIds.TryParse(kv.Value, out var id)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "'" + kv.Value + "' is not an element id"); continue; }
                if (!seen.Add(ElementIds.ToLong(id))) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "room " + kv.Value + " is listed twice"); continue; }
                var e = doc.GetElement(id);
                if (e is null) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist (the snapshot may be stale)"); continue; }
                if (!(e is Room room)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is a " + (e.Category?.Name ?? e.GetType().Name) + ", not a room"); continue; }
                var state = Rooms.Classify(doc, room, out var sharing);
                string label = "room " + kv.Value + " ('" + room.Number + " " + room.Name + "')";
                switch (state)
                {
                    case RoomCondition.Placed:
                        OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, label + " is placed and enclosed; this op only deletes unplaced, not enclosed or redundant rooms");
                        continue;
                    case RoomCondition.NotEnclosed when !notEnclosed:
                        OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, label + " is placed but not enclosed; set includeNotEnclosed:true to delete it (or close its boundary instead)");
                        continue;
                    case RoomCondition.Redundant when !redundant:
                        OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, label + " is redundant (shares its region with room " + Kit.Id(sharing[0].Id) + "); set includeRedundant:true to delete it");
                        continue;
                }
                p.Ids.Add(id);
                p.States[ElementIds.ToLong(id)] = Rooms.Name(state);
            }
            if (Kit.HasErrors(issues)) return null;
            Deletion.Dependents(doc, p.Ids, out var parts, out var separate);
            foreach (var d in separate)
            {
                var de = doc.GetElement(d);
                if (!(de is RoomTag))
                    OpContext.Error(issues, ctx.FieldPath(args, "ids"), RevitIssueCodes.NotAllowed, "deleting the rooms would also delete " + (de?.Category?.Name ?? "element") + " " + Kit.Id(d) + "; delete it explicitly with element.delete first");
            }
            foreach (var id in p.Ids.Concat(parts).Concat(separate)) ctx.DeclareDeletes(id);
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var deleted = doc.Delete(p!.Ids) ?? new List<ElementId>();
            var allowed = new HashSet<long>(ctx.Deletes);
            var extra = deleted.Where(d => !allowed.Contains(ElementIds.ToLong(d)) && !ctx.IsCreatedByJob(d)).ToList();
            if (extra.Count > 0)
                throw new OpFailedException("unexpected_dependents", "Revit would also delete " + extra.Count + " element(s) that were not declared: " + string.Join(", ", extra.Take(20).Select(Kit.Id)) + "; the op was rolled back");
            result.Deleted.AddRange(deleted.Select(Kit.Id));
            result.Outputs.Add("deleted", JsonArray.FromStrings(p.Ids.Select(Kit.Id)));
            var states = new JsonObject();
            foreach (var kv in p.States) states.Add(kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv.Value);
            result.Readback.Add("stateBefore", states);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var rooms = Refs.ElementIds(result.Outputs["deleted"]);
            foreach (var idText in rooms)
                if (ElementIds.TryParse(idText, out var id) && doc.GetElement(id) != null) check.Fail("room " + idText + " still exists after the op");
            var set = new HashSet<string>(rooms);
            result.Readback.Add("rooms", rooms.Count);
            result.Readback.Add("tagsDeleted", JsonArray.FromStrings(result.Deleted.Where(d => !set.Contains(d))));
            check.Apply(result);
        }
    }
}
