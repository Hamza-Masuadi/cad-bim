using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
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
    /// floor.create: a floor from a boundary (with optional openings) or from a placed, enclosed room's finish-face
    /// boundary, created with Floor.Create(doc, loops, typeId, levelId, structural, null, 0) (present from 2022 and in
    /// all four target releases; the 2024-2027 RevitAPI.xml list the same two overloads).
    ///
    /// Refused: foundation-slab types (use a floor type), unplaced / not enclosed / redundant rooms, loops that cross,
    /// openings outside the boundary or overlapping each other, and any loop set BoundaryValidation rejects. Warned:
    /// a height offset over 200 mm (RV-031, a published checkset's threshold) and an existing floor on the same level
    /// that already covers the outline's centre (RV: overlapping floors). Readback compares level, type, height
    /// offset, structural flag and the computed area (within 0.5 %) with the requested outline.
    /// </summary>
    public sealed class FloorCreate : OpHandler
    {
        public override string Name => "floor.create";

        private sealed class Plan
        {
            public HostOutline Outline = null!;
            public FloorType Type = null!;
            public double Offset;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            var t = Lookup.Type<FloorType>(doc, args.GetString("type"), "floor type");
            if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "type"), t.Code, t.Error!);
            else if (t.Value!.IsFoundationSlab) OpContext.Error(issues, ctx.FieldPath(args, "type"), RevitIssueCodes.Invalid, "'" + t.Value.Name + "' is a foundation slab type; use a floor type");
            else p.Type = t.Value;
            p.Offset = u.LengthToInternal(args.GetLength("offset"));
            if (Math.Abs(p.Offset) > UnitUtils.ConvertToInternalUnits(200, UnitTypeId.Millimeters))
                OpContext.Warning(issues, ctx.FieldPath(args, "offset"), RevitIssueCodes.Unchecked, "a height offset of " + Kit.Num(args.GetLength("offset")) + " " + Length.Symbol(u.JobUnit) +
                    " usually means the floor belongs to another level (RV-031)");
            var o = HostOutline.Read(ctx, args, "openings", issues, "floor");
            if (o is null) return null;
            p.Outline = o;
            if (!o.Pending && o.Level != null)
            {
                XYZ? probe = o.Room != null ? Rooms.Point(o.Room) : o.Outer.Count >= 3 ? u.PointToInternal(new Polygon2(o.Outer).Centroid, 0) : null;
                if (probe != null)
                    foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>().Where(f => f.LevelId == o.Level.Id))
                    {
                        var bb = f.get_BoundingBox(null);
                        if (bb != null && probe.X >= bb.Min.X && probe.X <= bb.Max.X && probe.Y >= bb.Min.Y && probe.Y <= bb.Max.Y)
                        {
                            OpContext.Warning(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Exists, "floor " + Kit.Id(f.Id) + " ('" + f.Name + "') on '" + o.Level.Name + "' may already cover this area; overlapping floors double-count");
                            break;
                        }
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
            var doc = ctx.Doc;
            var level = p!.Outline.Level!;
            var loops = p.Outline.Loops(ctx.Units, level.ProjectElevation);
            var floor = Floor.Create(doc, loops, p.Type.Id, level.Id, args.GetBool("structural"), null, 0)
                        ?? throw new OpFailedException("create_failed", "Revit did not create the floor");
            if (!Kit.SetDouble(floor, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM, p.Offset))
                throw new OpFailedException("parameter_value", "Revit refused the floor's height offset");
            string id = Kit.Id(floor.Id);
            result.Created.Add(id);
            result.Outputs.Add("floor", id);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("floor");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Floor floor))
            {
                check.Fail("the floor no longer exists after commit");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            var o = HostOutline.Read(ctx, args, "openings", new List<Issue>(), "floor");
            check.Text(rb, "type", floor.FloorType?.Name, Lookup.Type<FloorType>(doc, args.GetString("type"), "floor type").Value?.Name, "floor type");
            check.Text(rb, "level", Lookup.LevelName(doc, floor.LevelId), o?.Level?.Name ?? args.GetStringOrNull("level"), "floor level");
            var off = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (off != null) check.Length(rb, "offset", off.AsDouble(), args.GetLength("offset"), "floor height offset");
            else check.Fail("the floor has no height offset parameter");
            var st = floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            if (st != null) check.Flag(rb, "structural", st.AsInteger() == 1, args.GetBool("structural"), "floor structural");
            double area = Kit.ParamDouble(floor, BuiltInParameter.HOST_AREA_COMPUTED) ?? 0;
            rb.Add("area", RevitUnits.Num(u.AreaFromInternal(area)));
            rb.Add("areaUnit", u.AreaSymbol);
            if (o != null && o.Area > 0)
            {
                rb.Add("expectedArea", RevitUnits.Num(u.AreaFromInternal(o.Area)));
                if (!Kit.AreaClose(area, o.Area))
                    check.Fail("floor area reads " + Kit.Num(u.AreaFromInternal(area)) + " " + u.AreaSymbol + ", the outline encloses " + Kit.Num(u.AreaFromInternal(o.Area)));
            }
            else if (area <= 0) check.Fail("the floor has no area");
            if (o?.Room != null) rb.Add("fromRoom", Kit.Id(o.Room.Id));
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            result.Outputs.Set("area", RevitUnits.Num(u.AreaFromInternal(area)));
            check.Apply(result);
        }
    }
}
