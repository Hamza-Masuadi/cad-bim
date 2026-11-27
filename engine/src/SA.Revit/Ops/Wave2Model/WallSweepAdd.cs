using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// wallsweep.add: one standalone, horizontal wall sweep or reveal per wall, with WallSweep.Create(wall, typeId,
    /// WallSweepInfo(kind, false)) and the info's Distance, DistanceMeasuredFrom, WallSide, WallOffset and
    /// IsCutByInserts.
    ///
    /// The type is an element type of the Wall Sweeps category (OST_Cornices) for sweeps, or Reveals (OST_Reveals).
    /// Walls are checked with WallSweep.WallAllowsWallSweep (curtain walls and the main wall of a stacked wall are
    /// refused). The API documents that "the values set in the WallSweepInfo are ignored" next to "profile and type are
    /// taken from the wall sweep type", which leaves open whether placement values are honoured; readback therefore
    /// reads GetWallSweepInfo back and compares distance, side, measuring end and offset.
    ///
    /// With skipExisting a wall that already carries a standalone sweep of the same type on the same side at the same
    /// distance is skipped. The walls are declared as modified: a sweep can cut its host.
    /// </summary>
    public sealed class WallSweepAdd : OpHandler
    {
        public override string Name => "wallsweep.add";

        private sealed class Plan
        {
            public ElementType Type = null!;
            public List<Wall> Walls = new List<Wall>();
            public List<Wall> Skipped = new List<Wall>();
            public WallSweepType Kind;
            public double Distance, Offset;
            public DistanceMeasuredFrom From;
            public WallSide Side;
        }

        private static bool SameSweep(WallSweepInfo i, Plan p, double tol) =>
            !i.IsVertical && i.WallSweepType == p.Kind && i.WallSide == p.Side && i.DistanceMeasuredFrom == p.From && Math.Abs(i.Distance - p.Distance) <= tol;

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan
            {
                Kind = args.GetString("kind") == "reveal" ? WallSweepType.Reveal : WallSweepType.Sweep,
                Distance = u.LengthToInternal(args.GetLength("distance")),
                Offset = u.LengthToInternal(args.GetLength("wallOffset")),
                From = args.GetString("measuredFrom") == "top" ? DistanceMeasuredFrom.Top : DistanceMeasuredFrom.Base,
                Side = args.GetString("side") == "exterior" ? WallSide.Exterior : WallSide.Interior,
            };
            var cat = p.Kind == WallSweepType.Reveal ? BuiltInCategory.OST_Reveals : BuiltInCategory.OST_Cornices;
            var types = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>().ToList();
            var t = Lookup.ByName(types, args.GetString("type"), p.Kind == WallSweepType.Reveal ? "reveal type" : "wall sweep type");
            if (!t.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "type"), t.Code, t.Error!); return null; }
            p.Type = t.Value!;

            string listAt = ctx.FieldPath(args, args.Has("walls") ? "walls" : "filter");
            var elements = ElementSelect.Resolve(ctx, args, "walls", "filter", issues, out bool pending);
            if (!pending && elements.Count == 0 && !Kit.HasErrors(issues)) OpContext.Error(issues, listAt, RevitIssueCodes.NotFound, "no walls selected");
            var existing = new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>().ToList();
            var seen = new HashSet<long>();
            foreach (var e in elements)
            {
                if (!seen.Add(ElementIds.ToLong(e.Id))) continue;
                if (!(e is Wall w)) { OpContext.Error(issues, listAt, RevitIssueCodes.Invalid, "element " + Kit.Id(e.Id) + " is a " + (e.Category?.Name ?? e.GetType().Name) + ", not a wall"); continue; }
                if (!WallSweep.WallAllowsWallSweep(w)) { OpContext.Error(issues, listAt, RevitIssueCodes.NotAllowed, "wall " + Kit.Id(w.Id) + " cannot host a sweep or reveal (curtain wall or stacked wall)"); continue; }
                var h = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (h != null && h.StorageType == StorageType.Double && p.Distance > h.AsDouble() + u.LengthToleranceInternal)
                    OpContext.Warning(issues, listAt, RevitIssueCodes.Invalid, "wall " + Kit.Id(w.Id) + " is " + Kit.Num(u.LengthFromInternal(h.AsDouble())) + " high; distance " + Kit.Num(args.GetLength("distance")) + " is beyond it");
                bool has = existing.Any(s => s.GetTypeId() == p.Type.Id && s.GetHostIds().Contains(w.Id) && SameSweep(s.GetWallSweepInfo(), p, u.LengthToleranceInternal));
                if (has && args.GetBool("skipExisting")) p.Skipped.Add(w);
                else
                {
                    p.Walls.Add(w);
                    ctx.DeclareModifies(w.Id);
                }
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var made = new List<string>();
            foreach (var w in p!.Walls)
            {
                var info = new WallSweepInfo(p.Kind, false)
                {
                    Distance = p.Distance,
                    DistanceMeasuredFrom = p.From,
                    WallSide = p.Side,
                    WallOffset = p.Offset,
                    IsCutByInserts = args.GetBool("cutByInserts"),
                };
                WallSweep s;
                try { s = WallSweep.Create(w, p.Type.Id, info); }
                catch (Autodesk.Revit.Exceptions.ArgumentException e) { throw new OpFailedException("create_failed", "Revit refused the sweep on wall " + Kit.Id(w.Id) + ": " + e.Message); }
                if (s is null) throw new OpFailedException("create_failed", "Revit did not create the sweep on wall " + Kit.Id(w.Id));
                made.Add(Kit.Id(s.Id));
            }
            result.Created.AddRange(made);
            result.Outputs.Add("sweeps", Ids(made));
            result.Outputs.Add("skipped", Ids(p.Skipped.Select(w => Kit.Id(w.Id))));
            result.Readback.Add("walls", Ids(p.Walls.Select(w => Kit.Id(w.Id))));
            result.Readback.Add("type", new JsonObject().Add("id", Kit.Id(p.Type.Id)).Add("name", p.Type.Name));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var sweeps = (result.Outputs["sweeps"] as JsonArray ?? new JsonArray()).OfType<JsonString>().Select(s => s.Value).ToList();
            var walls = (result.Readback["walls"] as JsonArray ?? new JsonArray()).OfType<JsonString>().Select(s => s.Value).ToList();
            string kind = args.GetString("kind");
            string? wantType = (result.Readback["type"] as JsonObject)?.GetStringOrNull("id");
            var items = new JsonArray();
            if (sweeps.Count != walls.Count) check.Fail(sweeps.Count + " " + kind + "(s) made for " + walls.Count + " wall(s)");
            for (int i = 0; i < sweeps.Count; i++)
            {
                string label = (kind == "reveal" ? "reveal " : "sweep ") + sweeps[i];
                if (!ElementIds.TryParse(sweeps[i], out var id) || !(doc.GetElement(id) is WallSweep s)) { check.Fail(label + " no longer exists"); continue; }
                var info = s.GetWallSweepInfo();
                var o = new JsonObject().Add("id", sweeps[i]);
                var hosts = s.GetHostIds().Select(Kit.Id).ToList();
                o.Add("hosts", Ids(hosts));
                if (i < walls.Count && !hosts.Contains(walls[i])) check.Fail(label + " is not hosted by wall " + walls[i]);
                check.Text(o, "typeId", Kit.Id(s.GetTypeId()), wantType, label + " type");
                check.Text(o, "kind", info.WallSweepType == WallSweepType.Reveal ? "reveal" : "sweep", kind, label + " kind");
                check.Length(o, "distance", info.Distance, args.GetLength("distance"), label + " distance");
                check.Text(o, "measuredFrom", info.DistanceMeasuredFrom == DistanceMeasuredFrom.Top ? "top" : "base", args.GetString("measuredFrom"), label + " measured from");
                check.Text(o, "side", info.WallSide == WallSide.Exterior ? "exterior" : "interior", args.GetString("side"), label + " side");
                check.Length(o, "wallOffset", info.WallOffset, args.GetLength("wallOffset"), label + " offset");
                check.Flag(o, "cutByInserts", info.IsCutByInserts, args.GetBool("cutByInserts"), label + " cut by inserts");
                items.Add(o);
            }
            result.Readback.Add("items", items);
            check.Apply(result);
        }
    }
}
