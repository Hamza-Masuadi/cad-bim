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
    /// viewports.layout: places several views on one sheet in aligned rows inside the drawable area, using Core's
    /// RectPacker (rows from the top, left to right, in the order given).
    ///
    /// <para><b>Drawable area.</b> <c>area</c> when the job gives it (sheet coordinates, paper units); otherwise derived
    /// from the title block by <see cref="TitleBlockArea"/> (inner border, less a right-hand or bottom title strip,
    /// detected from the title block's own long lines). When no line can be found the fallback is the title block
    /// extents less 15 mm of paper on each side, with a warning that the title strip was not excluded. A sheet with
    /// no title block and no <c>area</c> is refused. The job's <c>margin</c> is then kept clear inside that area.</para>
    ///
    /// <para><b>Sizes.</b> Validate estimates each view's paper size from View.Outline ("the bounds of the view in paper
    /// space", i.e. the crop or annotation crop divided by the view scale) and runs the packer, so views that cannot
    /// fit are reported before anything changes; with failIfNotFit (default) that is a validation error. In Apply
    /// the real footprints are measured: every view is placed at the area centre inside a SubTransaction, the
    /// document is regenerated, each footprint is the union of the viewport box (GetBoxOutline) and its title label
    /// (GetLabelOutline, so titles do not collide), and the SubTransaction is rolled back. The packer runs again on
    /// the measured footprints, and only then are the viewports created, each at its packed position.</para>
    ///
    /// <para><b>Readback.</b> Every viewport's box outline is read from the sheet and must lie inside the drawable
    /// area less the margin, no two boxes may overlap, and each box centre must be where the packer put it. Labels
    /// that overlap another view are reported as warnings. Views that did not fit are listed in
    /// <c>notPlaced</c> (reason larger_than_area or no_space) and must not be on the sheet.</para>
    ///
    /// <para><b>One sheet per view.</b> A non-legend view already on any sheet is refused (Revit allows one sheet per
    /// view); a schedule may go on several sheets but once per sheet; a legend may go on several sheets.</para>
    /// </summary>
    public sealed class ViewportsLayout : OpHandler
    {
        public override string Name => "viewports.layout";

        private sealed class Plan
        {
            public ViewSheet Sheet = null!;
            public List<View> Views = new List<View>();
            public Rect2 Area;
            public bool AreaGiven;
            public string AreaHow = "";
            public PackOptions Options = new PackOptions();
            public bool FailIfNotFit;
            public string? TypeName;
            public ElementType? Type;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool placedCheck = true)
        {
            var doc = ctx.Doc;
            var sheet = DocsUtil.Sheet(ctx, args.Get("sheet"), ctx.FieldPath(args, "sheet"), issues, out bool sheetPending);
            var views = DocsUtil.Views(ctx, args, "views", issues, out bool viewsPending);
            var p = new Plan { FailIfNotFit = args.GetBool("failIfNotFit"), TypeName = args.GetStringOrNull("viewportType")?.Trim() };
            p.Options.Margin = ctx.Paper.LengthToInternal(ctx.PaperLength(args, "margin"));
            p.Options.GapX = ctx.Paper.LengthToInternal(ctx.PaperLength(args, "gapX"));
            p.Options.GapY = ctx.Paper.LengthToInternal(ctx.PaperLength(args, "gapY"));
            switch (args.GetString("rowAlign"))
            {
                case "centre": p.Options.RowAlignment = RowAlignment.Centre; break;
                case "distribute": p.Options.RowAlignment = RowAlignment.Distribute; break;
                default: p.Options.RowAlignment = RowAlignment.Left; break;
            }
            switch (args.GetString("itemAlign"))
            {
                case "middle": p.Options.ItemAlignment = ItemAlignment.Middle; break;
                case "bottom": p.Options.ItemAlignment = ItemAlignment.Bottom; break;
                default: p.Options.ItemAlignment = ItemAlignment.Top; break;
            }
            if (p.TypeName != null)
            {
                var types = SheetPlacement.ViewportTypes(doc);
                if (types is null) OpContext.Warning(issues, ctx.FieldPath(args, "viewportType"), RevitIssueCodes.Unchecked, "the model has no viewport yet, so viewport type '" + p.TypeName + "' is checked after placing");
                else
                {
                    var hit = Lookup.ByName(types, p.TypeName, "viewport type");
                    if (!hit.Ok) OpContext.Error(issues, ctx.FieldPath(args, "viewportType"), hit.Code, hit.Error!);
                    else p.Type = hit.Value;
                }
            }
            if (sheet is null) return null;
            p.Sheet = sheet;

            var area = args.GetObject("area");
            var derived = TitleBlockArea.Derive(doc, sheet);
            if (area != null)
            {
                var a = ctx.PaperPoint(area, "min");
                var b = ctx.PaperPoint(area, "max");
                if (!(b.X > a.X) || !(b.Y > a.Y)) { OpContext.Error(issues, ctx.FieldPath(args, "area"), RevitIssueCodes.Invalid, "area.max must be above and to the right of area.min"); return null; }
                var lo = ctx.Paper.PointToInternal(a, 0);
                var hi = ctx.Paper.PointToInternal(b, 0);
                p.Area = new Rect2(lo.X, lo.Y, hi.X - lo.X, hi.Y - lo.Y);
                p.AreaGiven = true;
                p.AreaHow = "given by the job";
                if (derived != null && !derived.Extents.Contains(p.Area, ctx.Paper.LengthToleranceInternal))
                    OpContext.Warning(issues, ctx.FieldPath(args, "area"), RevitIssueCodes.Invalid, "area " + SheetPlacement.Text(ctx.Paper, p.Area) + " extends past the title block " + SheetPlacement.Text(ctx.Paper, derived.Extents));
            }
            else if (derived is null)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "area"), RevitIssueCodes.Invalid, "sheet " + sheet.SheetNumber + " has no title block, so no drawable area can be derived; pass area");
                return null;
            }
            else
            {
                p.Area = derived.Area;
                p.AreaHow = derived.How;
                if (derived.Fallback)
                    OpContext.Warning(issues, ctx.FieldPath(args, "area"), RevitIssueCodes.Unchecked,
                        "drawable area estimated (" + derived.How + "): the title strip may not be excluded; pass area to be sure");
            }
            if (p.Area.Width <= 2 * p.Options.Margin || p.Area.Height <= 2 * p.Options.Margin)
                OpContext.Error(issues, ctx.FieldPath(args, "margin"), RevitIssueCodes.Invalid, "the margin leaves no room inside the drawable area " + SheetPlacement.Text(ctx.Paper, p.Area));

            foreach (var kv in views)
            {
                if (placedCheck && !SheetPlacement.CanPlace(doc, sheet, kv.Key, out string why))
                {
                    OpContext.Error(issues, kv.Value, RevitIssueCodes.NotAllowed, "view " + DocsUtil.Describe(kv.Key) + " cannot go on sheet " + sheet.SheetNumber + ": " + why);
                    continue;
                }
                p.Views.Add(kv.Key);
            }
            if (placedCheck && !viewsPending && !DocsUtil.HasErrors(issues))
            {
                // Pre-check with estimated sizes, so a layout that cannot fit is refused before anything changes.
                var items = new List<PackItem>();
                var unknown = new List<string>();
                foreach (var v in p.Views)
                {
                    var s = SheetPlacement.EstimateSize(v);
                    if (s.HasValue) items.Add(new PackItem(ElementIds.ToWire(v.Id), s.Value.X, s.Value.Y));
                    else unknown.Add(v.Name);
                }
                if (unknown.Count > 0)
                    OpContext.Warning(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Unchecked, "size not known before placing for " + string.Join(", ", unknown.Select(n => "'" + n + "'")) + "; measured when the op runs");
                var pre = RectPacker.Pack(p.Area, items, p.Options);
                if (pre.NotPlaced.Count > 0)
                {
                    string msg = pre.NotPlaced.Count + " view(s) do not fit in the drawable area " + SheetPlacement.Text(ctx.Paper, p.Area) + " (" + p.AreaHow + "): " +
                                 string.Join(", ", pre.NotPlaced.Select(u => "'" + p.Views.First(v => ElementIds.ToWire(v.Id) == u.Id).Name + "' " + u.Reason));
                    if (p.FailIfNotFit) OpContext.Error(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Invalid, msg + "; use another sheet, a smaller scale or failIfNotFit:false");
                    else OpContext.Warning(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Invalid, msg + "; they will be listed in notPlaced");
                }
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private sealed class Measured
        {
            public View View = null!;
            public double W, H;
            /// <summary>Box centre minus footprint centre.</summary>
            public double Dx, Dy;
        }

        private static ElementType? TypeFor(Document doc, Plan p, Viewport vp)
        {
            if (p.TypeName is null) return null;
            if (p.Type != null) return p.Type;
            var f = Lookup.ByName(SheetPlacement.ViewportTypes(doc, vp) ?? new List<ElementType>(), p.TypeName, "viewport type");
            if (!f.Ok) throw new OpFailedException(f.Code, f.Error!);
            p.Type = f.Value;
            return p.Type;
        }

        private static void Style(Document doc, Plan p, Element placed)
        {
            if (placed is Viewport vp && TypeFor(doc, p, vp) is ElementType t && vp.GetTypeId() != t.Id)
            {
                var c = vp.GetBoxCenter();
                vp.ChangeTypeId(t.Id);
                doc.Regenerate();
                if ((vp.GetBoxCenter() - c).GetLength() > 1e-9) { vp.SetBoxCenter(c); doc.Regenerate(); }
            }
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var centre = new XYZ(p.Area.Centre.X, p.Area.Centre.Y, 0);

            // 1. Measure real footprints (box + label) in a SubTransaction that is always rolled back.
            var measured = new List<Measured>();
            using (var st = new SubTransaction(doc))
            {
                st.Start();
                try
                {
                    foreach (var v in p.Views)
                    {
                        var placed = SheetPlacement.Place(doc, p.Sheet, v, centre);
                        Style(doc, p, placed);
                        var box = SheetPlacement.Box(doc, p.Sheet, placed) ?? throw new OpFailedException("layout_measure", "view '" + v.Name + "' has no extent on the sheet");
                        var label = SheetPlacement.Label(placed);
                        var foot = label.HasValue ? SheetPlacement.Union(box, label.Value) : box;
                        measured.Add(new Measured { View = v, W = foot.Width, H = foot.Height, Dx = box.Centre.X - foot.Centre.X, Dy = box.Centre.Y - foot.Centre.Y });
                    }
                }
                finally { if (st.HasStarted() && !st.HasEnded()) st.RollBack(); }
            }
            doc.Regenerate();

            // 2. Pack the measured footprints.
            var items = measured.Select(m => new PackItem(ElementIds.ToWire(m.View.Id), Math.Max(m.W, 1e-6), Math.Max(m.H, 1e-6))).ToList();
            var pack = RectPacker.Pack(p.Area, items, p.Options);
            var notPlaced = new JsonArray();
            foreach (var u in pack.NotPlaced) notPlaced.Add(new JsonObject().Add("id", u.Id).Add("reason", u.Reason));
            if (pack.NotPlaced.Count > 0 && p.FailIfNotFit)
                throw new OpFailedException("does_not_fit", pack.NotPlaced.Count + " view(s) do not fit in the drawable area " + SheetPlacement.Text(ctx.Paper, p.Area) +
                    " once measured: " + string.Join(", ", pack.NotPlaced.Select(u => "'" + measured.First(m => ElementIds.ToWire(m.View.Id) == u.Id).View.Name + "' " + u.Reason)),
                    new JsonObject().Add("notPlaced", notPlaced));

            // 3. Place each view with its box where the packer put its footprint.
            var placements = new JsonArray();
            foreach (var pl in pack.Placed)
            {
                var m = measured.First(x => ElementIds.ToWire(x.View.Id) == pl.Id);
                var boxCentre = new XYZ(pl.Centre.X + m.Dx, pl.Centre.Y + m.Dy, 0);
                var placed = SheetPlacement.Place(doc, p.Sheet, m.View, boxCentre);
                Style(doc, p, placed);
                result.Created.Add(ElementIds.ToWire(placed.Id));
                var box = SheetPlacement.Box(doc, p.Sheet, placed) ?? new Rect2(boxCentre.X, boxCentre.Y, 0, 0);
                placements.Add(new JsonObject()
                    .Add("id", pl.Id)
                    .Add("viewport", ElementIds.ToWire(placed.Id))
                    .Add("centre", DocsUtil.Xy(ctx.Paper.LengthFromInternal(boxCentre.X), ctx.Paper.LengthFromInternal(boxCentre.Y)))
                    .Add("min", DocsUtil.Xy(ctx.Paper.LengthFromInternal(box.MinX), ctx.Paper.LengthFromInternal(box.MinY)))
                    .Add("max", DocsUtil.Xy(ctx.Paper.LengthFromInternal(box.MaxX), ctx.Paper.LengthFromInternal(box.MaxY)))
                    .Add("row", pl.Row));
            }
            foreach (var u in pack.NotPlaced) ctx.Warn("view " + u.Id + " was not placed on sheet " + p.Sheet.SheetNumber + ": " + u.Reason);
            result.Outputs.Set("placements", placements);
            result.Outputs.Set("notPlaced", notPlaced);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Paper);
            var doc = ctx.Doc;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, placedCheck: false);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            var inner = p.Area.Deflate(p.Options.Margin);
            rb.Set("sheet", p.Sheet.SheetNumber);
            rb.Set("area", SheetPlacement.Json(ctx.Paper, p.Area));
            rb.Set("areaSource", p.AreaHow);
            double tol = ctx.Paper.LengthToleranceInternal;
            var boxes = new List<KeyValuePair<string, Rect2>>();
            var labels = new List<KeyValuePair<string, Rect2>>();
            var read = new JsonArray();
            var placements = result.Outputs["placements"] as JsonArray ?? new JsonArray();
            foreach (var item in placements.OfType<JsonObject>())
            {
                string viewId = item.GetStringOrNull("id") ?? "";
                string vpId = item.GetStringOrNull("viewport") ?? "";
                var placed = ElementIds.TryParse(vpId, out var e) ? doc.GetElement(e) : null;
                if (placed is null) { check.Fail("viewport " + vpId + " for view " + viewId + " is missing after the op"); continue; }
                if (ElementIds.ToWire(SheetPlacement.ViewOf(placed)) != viewId) check.Fail("viewport " + vpId + " shows view " + ElementIds.ToWire(SheetPlacement.ViewOf(placed)) + ", expected " + viewId);
                if (SheetPlacement.SheetOf(placed) != p.Sheet.Id) check.Fail("viewport " + vpId + " is not on sheet " + p.Sheet.SheetNumber);
                var box = SheetPlacement.Box(doc, p.Sheet, placed);
                if (box is null) { check.Fail("viewport " + vpId + " has no box on the sheet"); continue; }
                var b = box.Value;
                var rec = new JsonObject().Add("id", viewId).Add("viewport", vpId).Add("box", SheetPlacement.Json(ctx.Paper, b));
                if (!inner.Contains(b, tol))
                    check.Fail("viewport " + vpId + " box " + SheetPlacement.Text(ctx.Paper, b) + " is not inside the drawable area " + SheetPlacement.Text(ctx.Paper, inner));
                if (item["centre"] is JsonArray c && c.Count == 2)
                {
                    var want = new SA.Core.Geometry.Point2(c[0].AsDouble(), c[1].AsDouble());
                    check.Point(rec, "centre", new XYZ(b.Centre.X, b.Centre.Y, 0), want, "viewport " + vpId + " centre");
                }
                boxes.Add(new KeyValuePair<string, Rect2>(vpId, b));
                var lab = SheetPlacement.Label(placed);
                if (lab.HasValue) labels.Add(new KeyValuePair<string, Rect2>(vpId, lab.Value));
                read.Add(rec);
            }
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                    if (boxes[i].Value.Overlaps(boxes[j].Value, tol))
                        check.Fail("viewports " + boxes[i].Key + " and " + boxes[j].Key + " overlap");
            foreach (var l in labels)
                foreach (var b in boxes)
                    if (b.Key != l.Key && l.Value.Overlaps(b.Value, tol))
                        ctx.Warn("the title of viewport " + l.Key + " overlaps viewport " + b.Key);
            if (result.Outputs["notPlaced"] is JsonArray np)
            {
                foreach (var u in np.OfType<JsonObject>())
                {
                    string vid = u.GetStringOrNull("id") ?? "";
                    if (ElementIds.TryParse(vid, out var veid) && doc.GetElement(veid) is View v && DocsUtil.SheetsShowing(doc, v).Any(s => s.Id == p.Sheet.Id))
                        check.Fail("view " + vid + " is listed as not placed but is on sheet " + p.Sheet.SheetNumber);
                }
                rb.Set("notPlaced", np.Count);
            }
            rb.Set("placements", read);
            check.Apply(result);
        }
    }
}
