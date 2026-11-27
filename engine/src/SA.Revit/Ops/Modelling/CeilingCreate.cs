using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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
    /// ceiling.create: a ceiling from a boundary or from a placed, enclosed room's finish-face boundary, created with
    /// Ceiling.Create(doc, loops, typeId, levelId) (added in 2022; the 2024-2027 RevitAPI.xml list the same overloads),
    /// then given its height above the level (CEILING_HEIGHTABOVELEVEL_PARAM).
    ///
    /// Refused: a height of 0 or less, a ceiling at or above the next level up (it would sit in the floor above),
    /// unplaced / not enclosed / redundant rooms and invalid loops. Readback compares level, type, height and the area
    /// (within 0.5 %).
    /// </summary>
    public sealed class CeilingCreate : OpHandler
    {
        public override string Name => "ceiling.create";

        private sealed class Plan
        {
            public HostOutline Outline = null!;
            public CeilingType Type = null!;
            public double Height;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            var t = Lookup.Type<CeilingType>(doc, args.GetString("type"), "ceiling type");
            if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "type"), t.Code, t.Error!);
            else p.Type = t.Value!;
            p.Height = u.LengthToInternal(args.GetLength("height"));
            if (p.Height <= u.LengthToleranceInternal) OpContext.Error(issues, ctx.FieldPath(args, "height"), RevitIssueCodes.Invalid, "ceiling height must be greater than zero");
            var o = HostOutline.Read(ctx, args, "", issues, "ceiling");
            if (o is null) return null;
            p.Outline = o;
            if (o.Level != null)
            {
                var above = Lookup.Levels(doc).Where(l => l.ProjectElevation > o.Level.ProjectElevation + u.LengthToleranceInternal).OrderBy(l => l.ProjectElevation).FirstOrDefault();
                if (above != null && o.Level.ProjectElevation + p.Height >= above.ProjectElevation - u.LengthToleranceInternal)
                    OpContext.Error(issues, ctx.FieldPath(args, "height"), RevitIssueCodes.Invalid, "a ceiling " + Kit.Num(args.GetLength("height")) + " " + Length.Symbol(u.JobUnit) +
                        " above '" + o.Level.Name + "' is at or above the next level '" + above.Name + "'");
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
            var ceiling = Ceiling.Create(doc, p.Outline.Loops(ctx.Units, level.ProjectElevation), p.Type.Id, level.Id)
                          ?? throw new OpFailedException("create_failed", "Revit did not create the ceiling");
            if (!Kit.SetDouble(ceiling, BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM, p.Height))
                throw new OpFailedException("parameter_value", "Revit refused the ceiling height");
            string id = Kit.Id(ceiling.Id);
            result.Created.Add(id);
            result.Outputs.Add("ceiling", id);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("ceiling");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Ceiling ceiling))
            {
                check.Fail("the ceiling no longer exists after commit");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            var o = HostOutline.Read(ctx, args, "", new List<Issue>(), "ceiling");
            check.Text(rb, "type", doc.GetElement(ceiling.GetTypeId())?.Name, Lookup.Type<CeilingType>(doc, args.GetString("type"), "ceiling type").Value?.Name, "ceiling type");
            check.Text(rb, "level", Lookup.LevelName(doc, ceiling.LevelId), o?.Level?.Name ?? args.GetStringOrNull("level"), "ceiling level");
            var h = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
            if (h != null) check.Length(rb, "height", h.AsDouble(), args.GetLength("height"), "ceiling height");
            else check.Fail("the ceiling has no height parameter");
            double area = Kit.ParamDouble(ceiling, BuiltInParameter.HOST_AREA_COMPUTED) ?? 0;
            rb.Add("area", RevitUnits.Num(u.AreaFromInternal(area)));
            rb.Add("areaUnit", u.AreaSymbol);
            if (o != null && o.Area > 0)
            {
                rb.Add("expectedArea", RevitUnits.Num(u.AreaFromInternal(o.Area)));
                if (!Kit.AreaClose(area, o.Area))
                    check.Fail("ceiling area reads " + Kit.Num(u.AreaFromInternal(area)) + " " + u.AreaSymbol + ", the outline encloses " + Kit.Num(u.AreaFromInternal(o.Area)));
            }
            else if (area <= 0) check.Fail("the ceiling has no area");
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            result.Outputs.Set("area", RevitUnits.Num(u.AreaFromInternal(area)));
            check.Apply(result);
        }
    }
}
