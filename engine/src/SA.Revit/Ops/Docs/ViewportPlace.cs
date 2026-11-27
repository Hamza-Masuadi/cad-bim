using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// viewport.place: puts one view on a sheet with its box (viewport outline, label excluded) centred on
    /// <c>centre</c>, a sheet point in paper units (millimetres on the sheet, inches for an imperial job; sheet coordinates).
    ///
    /// Validate checks, without changing anything: the sheet is real (not a placeholder), the view can be added
    /// (Viewport.CanAddViewToSheet; for schedules, not already on this sheet; a non-legend view already on another
    /// sheet is refused with the sheet number), the viewport type exists among an existing viewport's valid types,
    /// and the detail number is free on that sheet. It warns when the estimated box (View.Outline at the centre)
    /// leaves the title block's drawable area (<see cref="TitleBlockArea"/>).
    ///
    /// Schedules are placed as ScheduleSheetInstance and then moved so their extent is centred on the point; the
    /// output <c>viewport</c> is then the schedule instance id. Viewport type and detail number do not apply to
    /// schedules and are refused for them.
    /// </summary>
    public sealed class ViewportPlace : OpHandler
    {
        public override string Name => "viewport.place";

        private sealed class Plan
        {
            public ViewSheet? Sheet;
            public View? View;
            public Point2 Centre;
            public ElementType? Type;
            public string? TypeName;
            public string? DetailNumber;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool placedCheck = true)
        {
            var doc = ctx.Doc;
            var p = new Plan { Centre = ctx.PaperPoint(args, "centre") };
            p.Sheet = DocsUtil.Sheet(ctx, args.Get("sheet"), ctx.FieldPath(args, "sheet"), issues, out bool sp);
            p.View = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool vpend);
            p.TypeName = args.GetStringOrNull("viewportType")?.Trim();
            p.DetailNumber = args.GetStringOrNull("detailNumber")?.Trim();
            if (p.DetailNumber != null && p.DetailNumber.Length == 0)
                OpContext.Error(issues, ctx.FieldPath(args, "detailNumber"), RevitIssueCodes.Invalid, "detail number is empty");
            if (p.View is ViewSchedule && (p.TypeName != null || p.DetailNumber != null))
                OpContext.Error(issues, ctx.FieldPath(args, p.TypeName != null ? "viewportType" : "detailNumber"), RevitIssueCodes.Invalid,
                    "schedules are placed as schedule instances, which have no viewport type or detail number");
            if (p.TypeName != null && !(p.View is ViewSchedule))
            {
                var types = SheetPlacement.ViewportTypes(doc);
                if (types is null)
                    OpContext.Warning(issues, ctx.FieldPath(args, "viewportType"), RevitIssueCodes.Unchecked, "the model has no viewport yet, so viewport type '" + p.TypeName + "' is checked after placing");
                else
                {
                    var hit = Lookup.ByName(types, p.TypeName, "viewport type");
                    if (!hit.Ok) OpContext.Error(issues, ctx.FieldPath(args, "viewportType"), hit.Code, hit.Error!);
                    else p.Type = hit.Value;
                }
            }
            if (p.Sheet != null && p.View != null && placedCheck)
            {
                if (!SheetPlacement.CanPlace(doc, p.Sheet, p.View, out string why))
                    OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "view " + DocsUtil.Describe(p.View) + " cannot go on sheet " + p.Sheet.SheetNumber + ": " + why);
                if (p.DetailNumber != null)
                {
                    foreach (var vp in p.Sheet.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>())
                    {
                        var dn = vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString();
                        if (string.Equals(dn, p.DetailNumber, StringComparison.OrdinalIgnoreCase))
                            OpContext.Error(issues, ctx.FieldPath(args, "detailNumber"), RevitIssueCodes.Exists, "detail number '" + p.DetailNumber + "' is already used on sheet " + p.Sheet.SheetNumber);
                    }
                }
                var size = SheetPlacement.EstimateSize(p.View);
                var area = TitleBlockArea.Derive(doc, p.Sheet);
                if (size.HasValue && area != null)
                {
                    var c = ctx.Paper.PointToInternal(p.Centre, 0);
                    var box = new Rect2(c.X - size.Value.X / 2, c.Y - size.Value.Y / 2, size.Value.X, size.Value.Y);
                    if (!area.Area.Contains(box, ctx.Paper.LengthToleranceInternal))
                        OpContext.Warning(issues, ctx.FieldPath(args, "centre"), RevitIssueCodes.Invalid,
                            "the view's estimated box " + SheetPlacement.Text(ctx.Paper, box) + " leaves the drawable area " + SheetPlacement.Text(ctx.Paper, area.Area) +
                            " (" + area.How + "); viewports.layout can place it inside");
                }
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var placed = SheetPlacement.Place(doc, p.Sheet!, p.View!, ctx.Paper.PointToInternal(p.Centre, 0));
            result.Created.Add(ElementIds.ToWire(placed.Id));
            if (placed is Viewport vp)
            {
                if (p.TypeName != null)
                {
                    var type = p.Type;
                    if (type is null)
                    {
                        var f = Lookup.ByName(SheetPlacement.ViewportTypes(doc, vp) ?? new List<ElementType>(), p.TypeName, "viewport type");
                        if (!f.Ok) throw new OpFailedException(f.Code, f.Error!);
                        type = f.Value!;
                    }
                    if (vp.GetTypeId() != type.Id) vp.ChangeTypeId(type.Id);
                }
                if (p.DetailNumber != null)
                {
                    var prm = vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                    if (prm is null || prm.IsReadOnly || !prm.Set(p.DetailNumber))
                        throw new OpFailedException("parameter_value", "Revit refused detail number '" + p.DetailNumber + "'");
                }
                doc.Regenerate();
                var bc = vp.GetBoxCenter();
                var want = ctx.Paper.PointToInternal(p.Centre, bc.Z);
                if ((bc - want).GetLength() > 1e-9) vp.SetBoxCenter(want); // a type change can move the box
            }
            result.Outputs.Set("viewport", ElementIds.ToWire(placed.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Paper);
            var doc = ctx.Doc;
            string? id = result.Outputs.GetStringOrNull("viewport");
            var placed = id != null && ElementIds.TryParse(id, out var eid) ? doc.GetElement(eid) : null;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, placedCheck: false);
            if (placed is null || p?.Sheet is null || p.View is null)
            {
                check.Fail(placed is null ? "the viewport was not found after the op" : "could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message)));
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            rb.Set("id", id!);
            check.Text(rb, "sheet", (doc.GetElement(SheetPlacement.SheetOf(placed)) as ViewSheet)?.SheetNumber, p.Sheet.SheetNumber);
            check.Text(rb, "view", ElementIds.ToWire(SheetPlacement.ViewOf(placed)), ElementIds.ToWire(p.View.Id));
            var box = SheetPlacement.Box(doc, p.Sheet, placed);
            if (box is null) check.Fail("the placed view has no extent on the sheet");
            else
            {
                var b = box.Value;
                rb.Set("box", SheetPlacement.Json(ctx.Paper, b));
                check.Point(rb, "centre", new XYZ(b.Centre.X, b.Centre.Y, 0), p.Centre, "box centre");
                var area = TitleBlockArea.Derive(doc, p.Sheet);
                if (area != null)
                {
                    bool inside = area.Area.Contains(b, ctx.Paper.LengthToleranceInternal);
                    rb.Set("insideDrawableArea", inside);
                    if (!inside) ctx.Warn("viewport " + id + " " + SheetPlacement.Text(ctx.Paper, b) + " is not inside the drawable area " + SheetPlacement.Text(ctx.Paper, area.Area) + " (" + area.How + ")");
                }
            }
            if (placed is Viewport vp)
            {
                var t = doc.GetElement(vp.GetTypeId()) as ElementType;
                check.Text(rb, "viewportType", t?.Name, p.TypeName);
                check.Text(rb, "detailNumber", vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString(), p.DetailNumber);
                var label = SheetPlacement.Label(vp);
                if (label.HasValue) rb.Set("label", SheetPlacement.Json(ctx.Paper, label.Value));
            }
            rb.Set("state", "created");
            check.Apply(result);
        }
    }
}
