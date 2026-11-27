using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    internal sealed class Room
    {
        public string Handle = "";
        public string Name = "";
        public string Number = "";
        public Polygon2 Outline = null!;
        public double Area;

        public JsonObject ToJson() => new JsonObject().Add("handle", Handle).Add("name", Name).Add("number", Number);
    }

    /// <summary>
    /// Rooms in model space: closed polylines on the room layers. A room's name and number come from a reference of the
    /// tag block (or, without one, any reference with a NAME attribute) whose insertion point lies inside it, else from
    /// a multiline text inside it whose first two lines are name and number (as room.create writes its untagged rooms).
    /// A point belongs to the smallest room containing it.
    /// </summary>
    internal sealed class RoomFinder
    {
        private readonly List<Room> _rooms;

        private RoomFinder(List<Room> rooms) { _rooms = rooms; }

        public int Count => _rooms.Count;

        /// <summary>Checks the rooms object; null when it is absent.</summary>
        internal static RoomFinder? Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var r = args.GetObject("rooms");
            if (r is null) return null;
            var layers = Targets.Patterns(r, "layers");
            string? tagBlock = r.GetStringOrNull("tagBlock");
            if (tagBlock != null && !ctx.IsRef(r, "tagBlock") && !Db.Read(ctx, tr => !DraftDb.BlockId(tr, ctx.Database, tagBlock).IsNull))
                OpContext.Error(issues, r, "tagBlock", "block_missing", "block '" + tagBlock + "' is not in the drawing");
            var finder = Db.Read(ctx, tr => Collect(ctx, tr, layers, tagBlock));
            if (ctx.IsValidating && finder.Count == 0)
                OpContext.Warning(issues, r, "layers", "no_rooms", "no closed polylines were found on " + string.Join(", ", r.GetStringList("layers")) + " in model space; every room will be empty");
            return finder;
        }

        private static RoomFinder Collect(OpContext ctx, Transaction tr, List<System.Text.RegularExpressions.Regex> layers, string? tagBlock)
        {
            var rooms = new List<Room>();
            var tags = new List<(Point2 At, string Name, string Number)>();
            var texts = new List<(Point2 At, string Name, string Number)>();
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e is null) continue;
                if (e is Polyline pl && Targets.AnyMatch(layers, pl.Layer))
                {
                    var loop = PlanGeometry.ClosedLoop(ctx, pl);
                    if (loop?.Polygon != null) rooms.Add(new Room { Handle = OpContext.HandleOf(id), Outline = loop.Polygon, Area = loop.Area });
                }
                else if (e is BlockReference br)
                {
                    if (tagBlock != null && !string.Equals(SheetDb.EffectiveName(tr, br), tagBlock, StringComparison.OrdinalIgnoreCase)) continue;
                    var values = BlockSupport.Read(tr, br);
                    if (!values.TryGetValue("NAME", out string? name)) continue;
                    values.TryGetValue("NUMBER", out string? number);
                    tags.Add((ctx.Units.ToJob(br.Position), name ?? "", number ?? ""));
                }
                else if (e is MText mt)
                {
                    var lines = (mt.Text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                    if (lines.Length >= 2) texts.Add((ctx.Units.ToJob(mt.Location), lines[0].Trim(), lines[1].Trim()));
                }
            }
            rooms = rooms.OrderBy(x => x.Area).ToList();
            var finder = new RoomFinder(rooms);
            foreach (var t in tags)
            {
                var room = finder.At(t.At);
                if (room != null && room.Name.Length == 0) { room.Name = t.Name; room.Number = t.Number; }
            }
            foreach (var t in texts)
            {
                var room = finder.At(t.At);
                if (room != null && room.Name.Length == 0) { room.Name = t.Name; room.Number = t.Number; }
            }
            return finder;
        }

        /// <summary>The smallest room containing the point, or null.</summary>
        internal Room? At(Point2 p) => _rooms.FirstOrDefault(r => r.Outline.Locate(p, 0) == PointLocation.Inside);

        internal static JsonValue Json(Room? r) => r is null ? (JsonValue)JsonValue.Null : r.ToJson();

        internal static string Label(Room? r) =>
            r is null ? "" : (r.Number.Length > 0 && r.Name.Length > 0 ? r.Number + " " + r.Name : r.Number + r.Name).Trim() is string s && s.Length > 0 ? s : r.Handle;
    }
}
