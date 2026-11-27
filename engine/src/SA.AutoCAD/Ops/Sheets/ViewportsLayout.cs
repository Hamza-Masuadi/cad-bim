using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// viewports.layout: each view's paper size is its model window divided by its scale ratio; Core's RectPacker places
    /// them in rows inside the drawable area; each placement becomes a viewport (same code as viewport.create).
    ///
    /// Drawable area: a given "area" is checked against the sheet (Layout.Limits) and against the extents of attributed
    /// block references on the layout; overlapping either is a warning, not a silent shrink. When "area" is omitted it is
    /// the layout's limits, and only on a layout with no attributed block reference: the space inside a title block's
    /// border cannot be told from its extents, so such a layout needs "area".
    ///
    /// Titles: a view's title is single-line text centred under the viewport, on the current layer, with the standard's
    /// "title" text height (else "body", else 3.5 mm plotted, all paper sizes), placed half a text height below the
    /// viewport. It is inside the packed gap only if gapY leaves room; a warning says when it does not.
    /// </summary>
    internal sealed class ViewportsLayout : OpHandler<ViewportsLayout.Plan>
    {
        public override string Name => "viewports.layout";

        internal sealed class ViewPlan
        {
            public ViewportSpec Spec = new ViewportSpec();
            public string? Title;
            public Rect2 Rect;
            public int Row;
            public ObjectId Id;
            public ObjectId TitleId;
        }

        internal sealed class Plan
        {
            public string Layout = "";
            public Rect2 Area;
            public List<ViewPlan> Placed = new List<ViewPlan>();
            public List<Unplaced> NotPlaced = new List<Unplaced>();
            public double TitleHeight;
            public string TitleLayer = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p is null || !ctx.IsValidating) return;
            if (p.NotPlaced.Count > 0 && !args.GetBool("failIfNotFit"))
                OpContext.Warning(issues, args, "views", "views_not_placed", "views that do not fit: " + string.Join(", ", p.NotPlaced.Select(u => u.Id + " (" + u.Reason + ")")));
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Layout = args.GetString("layout") };
            bool ok = true;
            bool planned = ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, p.Layout);
            if (!SheetDb.LayoutExists(ctx, p.Layout))
            {
                OpContext.Error(issues, args, "layout", "layout_missing", "there is no layout named '" + p.Layout + "'");
                ok = false;
            }
            bool areaDeferred = false;
            if (args.Has("area"))
            {
                var areaArgs = args.GetObject("area")!;
                var a = ctx.PaperPoint(areaArgs, "min");
                var b = ctx.PaperPoint(areaArgs, "max");
                if (!(b.X > a.X) || !(b.Y > a.Y))
                {
                    OpContext.Error(issues, args, "area", "area_invalid", "area.max must be above and to the right of area.min");
                    ok = false;
                }
                else p.Area = Rect2.FromCorners(a, b);
            }
            else
            {
                string reason = "";
                var derived = Db.Read(ctx, tr => DeriveArea(ctx, tr, p.Layout, out reason));
                if (derived.HasValue) p.Area = derived.Value;
                else if (planned) areaDeferred = true;
                else
                {
                    OpContext.Error(issues, args, "area", "area_required", "the drawable area cannot be derived: " + reason);
                    ok = false;
                }
            }
            var layer = ViewportSupport.ResolveLayer(ctx, args, issues);
            if (layer is null) ok = false;

            bool lockVp = args.GetBool("lock");
            if (!lockVp && ctx.IsValidating)
                OpContext.Warning(issues, args, "lock", "viewport_unlocked", "the viewports will be left unlocked; lock them after checking the scales");

            var views = new List<ViewPlan>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in args.GetObjectList("views"))
            {
                var vpn = new ViewPlan();
                var s = vpn.Spec;
                s.Id = v.GetString("id");
                if (!ids.Add(s.Id)) { OpContext.Error(issues, v, "id", "duplicate_view", "view id '" + s.Id + "' is used twice"); ok = false; continue; }
                var ratio = ViewportSupport.CheckScale(ctx, v, "scale", issues);
                ViewportSupport.CheckFrozen(ctx, v, "frozenLayers", issues);
                if (ratio is null) { ok = false; continue; }
                if (!ViewportSupport.Window(v.GetPoint2List("modelWindow"), out var c, out double mw, out double mh))
                {
                    OpContext.Error(issues, v, "modelWindow", "window_invalid", "the model window needs two corners with different x and y");
                    ok = false;
                    continue;
                }
                s.Ratio = ratio.Value;
                s.ScaleText = v.GetString("scale");
                s.ModelCentre = c;
                s.Width = mw / s.Ratio;
                s.Height = mh / s.Ratio;
                s.Lock = lockVp;
                s.Layer = layer ?? "";
                s.FrozenLayers = v.GetStringList("frozenLayers");
                vpn.Title = v.GetStringOrNull("title");
                views.Add(vpn);
            }
            if (!ok) return null;
            // The layout is created earlier in this job, so its limits are read at execution.
            if (areaDeferred) return p;

            var opt = new PackOptions
            {
                Margin = ctx.PaperLength(args, "margin"),
                GapX = ctx.PaperLength(args, "gapX"),
                GapY = ctx.PaperLength(args, "gapY"),
                RowAlignment = ParseRow(args.GetString("rowAlign")),
                ItemAlignment = ParseItem(args.GetString("itemAlign")),
            };
            var packed = RectPacker.Pack(p.Area, views.Select(v => new PackItem(v.Spec.Id, v.Spec.Width, v.Spec.Height)).ToList(), opt);
            foreach (var pl in packed.Placed)
            {
                var v = views.First(x => x.Spec.Id == pl.Id);
                v.Rect = pl.Rect;
                v.Row = pl.Row;
                v.Spec.PaperCentre = pl.Centre;
                p.Placed.Add(v);
            }
            p.NotPlaced.AddRange(packed.NotPlaced);
            if (p.NotPlaced.Count > 0 && args.GetBool("failIfNotFit"))
            {
                OpContext.Error(issues, args, "views", "views_do_not_fit", "views that do not fit in the area: " +
                    string.Join(", ", p.NotPlaced.Select(u => u.Id + " (" + u.Reason + ", " + Size(views.First(x => x.Spec.Id == u.Id).Spec) + ")")) +
                    "; use a smaller scale, a larger sheet, or set failIfNotFit false");
                return null;
            }

            // View titles.
            if (p.Placed.Any(v => v.Title != null))
            {
                p.TitleHeight = TitleHeight(ctx);
                p.TitleLayer = Db.CurrentLayerName(ctx);
                if (ctx.IsValidating)
                {
                    if (SheetDb.IsProtectedLayer(p.TitleLayer))
                        OpContext.Warning(issues, args, "views", "title_layer", "view titles go on the current layer '" + p.TitleLayer + "'; make an annotation layer current first");
                    if (opt.GapY < 2 * p.TitleHeight)
                        OpContext.Warning(issues, args, "gapY", "title_space", "gapY " + opt.GapY + " leaves less than two title heights (" + 2 * p.TitleHeight + ") under each row, so titles may touch the next row or leave the area");
                }
            }

            if (ctx.IsValidating && !planned && p.Placed.Count > 0)
                CheckAreaOnSheet(ctx, args, p, issues);
            return p;
        }

        /// <summary>
        /// The area when the job gives none: the layout's limits, but only on a layout without a title block, because the
        /// drawable part inside a title block's border cannot be told from its extents.
        /// </summary>
        private static Rect2? DeriveArea(OpContext ctx, Transaction tr, string layoutName, out string reason)
        {
            reason = "";
            var id = SheetDb.LayoutId(tr, ctx.Database, layoutName);
            if (id.IsNull) { reason = "layout '" + layoutName + "' does not exist"; return null; }
            var layout = (Layout)tr.GetObject(id, OpenMode.ForRead);
            var tbs = TitleBlocks.ReferencesOn(tr, ctx, layout);
            if (tbs.Count > 0)
            {
                reason = "layout '" + layoutName + "' has a title block ('" + tbs[0].Name + "') and the space inside its border cannot be told from its extents; give area";
                return null;
            }
            var sheet = ViewportSupport.SheetRect(ctx, layout);
            if (!sheet.HasValue) reason = "the limits of layout '" + layoutName + "' could not be read; give area";
            return sheet;
        }

        private static string Size(ViewportSpec s) =>
            "needs " + DrawingUnits.Round(s.Width) + " x " + DrawingUnits.Round(s.Height) + " at " + s.ScaleText;

        private static void CheckAreaOnSheet(OpContext ctx, OpArgs args, Plan p, List<Issue> issues)
        {
            Db.Read(ctx, tr =>
            {
                var id = SheetDb.LayoutId(tr, ctx.Database, p.Layout);
                if (id.IsNull) return 0;
                var layout = (Layout)tr.GetObject(id, OpenMode.ForRead);
                var sheet = ViewportSupport.SheetRect(ctx, layout);
                if (sheet.HasValue && !sheet.Value.Contains(p.Area, ctx.LengthTolerance))
                    OpContext.Warning(issues, args, "area", "area_off_sheet", "the area " + p.Area + " extends past the sheet limits " + sheet.Value);
                foreach (var tb in TitleBlocks.ReferencesOn(tr, ctx, layout))
                {
                    var ext = TitleBlocks.Extents(ctx, tb);
                    if (!ext.HasValue) continue;
                    // A title block usually encloses the area (border plus strip); only a strip or panel that cuts into
                    // the area without enclosing it is reported.
                    if (ext.Value.Overlaps(p.Area, ctx.LengthTolerance) && !ext.Value.Contains(p.Area, ctx.LengthTolerance))
                        OpContext.Warning(issues, args, "area", "area_over_titleblock", "the area overlaps title block '" + tb.Name + "' " + ext.Value);
                }
                return 0;
            });
        }

        private static double TitleHeight(OpContext ctx)
        {
            var std = ctx.Standard;
            var th = std?.FindTextHeight("title") ?? std?.FindTextHeight("body");
            double mm = th != null ? SA.Core.Units.Length.Convert(th.Plotted, th.Unit, SA.Core.Units.LengthUnit.Millimetre) : 3.5;
            return SA.Core.Units.Length.Convert(mm, SA.Core.Units.LengthUnit.Millimetre, ctx.JobUnit);
        }

        private static RowAlignment ParseRow(string s) => s == "centre" ? RowAlignment.Centre : s == "distribute" ? RowAlignment.Distribute : RowAlignment.Left;

        private static ItemAlignment ParseItem(string s) => s == "middle" ? ItemAlignment.Middle : s == "bottom" ? ItemAlignment.Bottom : ItemAlignment.Top;

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the views could not be planned.");
            var tr = ctx.Transaction;
            var layoutId = SheetDb.LayoutId(tr, ctx.Database, p.Layout);
            if (layoutId.IsNull) throw new InvalidOperationException("Layout '" + p.Layout + "' does not exist.");
            var placements = new JsonArray();
            foreach (var v in p.Placed)
            {
                v.Id = ViewportSupport.Create(ctx, layoutId, v.Spec);
                result.Created.Add(v.Spec.Handle);
                if (v.Title != null)
                {
                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
                    var t = new DBText();
                    t.SetDatabaseDefaults(ctx.Database);
                    t.TextString = v.Title;
                    t.Height = ctx.Units.ToDrawing(p.TitleHeight);
                    t.Layer = p.TitleLayer;
                    var at = ctx.Units.ToDrawing(new Point2(v.Rect.Centre.X, v.Rect.MinY - 1.5 * p.TitleHeight));
                    t.Position = at;
                    t.HorizontalMode = TextHorizontalMode.TextCenter;
                    t.VerticalMode = TextVerticalMode.TextBase;
                    t.AlignmentPoint = at;
                    v.TitleId = Db.Append(ctx, btr, t);
                    t.AdjustAlignment(ctx.Database);
                    result.Created.Add(OpContext.HandleOf(v.TitleId));
                }
                placements.Add(new JsonObject()
                    .Add("id", v.Spec.Id)
                    .Add("viewport", v.Spec.Handle)
                    .Add("centre", ctx.PaperOut(v.Rect.Centre))
                    .Add("min", new JsonArray(ctx.PaperOut(v.Rect.MinX), ctx.PaperOut(v.Rect.MinY)))
                    .Add("max", new JsonArray(ctx.PaperOut(v.Rect.MaxX), ctx.PaperOut(v.Rect.MaxY)))
                    .Add("row", v.Row));
            }
            var notPlaced = new JsonArray();
            foreach (var u in p.NotPlaced) notPlaced.Add(new JsonObject().Add("id", u.Id).Add("reason", u.Reason));
            result.Outputs.Add("placements", placements);
            result.Outputs.Add("notPlaced", notPlaced);
            if (p.NotPlaced.Count > 0) ctx.Warn("Not placed: " + string.Join(", ", p.NotPlaced.Select(u => u.Id + " (" + u.Reason + ")")) + ".");
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            bool ok = true;
            var list = new JsonArray();
            var rects = new List<KeyValuePair<string, Rect2>>();
            var tr = ctx.Transaction;
            foreach (var v in p.Placed)
            {
                var o = new JsonObject().Add("id", v.Spec.Id);
                if (!ViewportSupport.Check(ctx, v.Id, v.Spec, o, out string problem)) ok = Mismatch(result, problem);
                var vp = (Viewport)tr.GetObject(v.Id, OpenMode.ForRead);
                var c = ctx.Units.ToJob(vp.CenterPoint);
                double w = ctx.Units.ToJob(vp.Width), h = ctx.Units.ToJob(vp.Height);
                rects.Add(new KeyValuePair<string, Rect2>(v.Spec.Id, new Rect2(c.X - w / 2, c.Y - h / 2, w, h)));
                if (!v.TitleId.IsNull)
                {
                    var t = (DBText)tr.GetObject(v.TitleId, OpenMode.ForRead);
                    o.Add("title", t.TextString);
                    if (t.TextString != v.Title) ok = Mismatch(result, "view '" + v.Spec.Id + "': title text read back differs");
                }
                list.Add(o);
            }
            // Read-back geometry: inside the area and no two viewports overlapping.
            var inner = p.Area.Deflate(ctx.PaperLength(args, "margin"));
            foreach (var r in rects)
                if (!inner.Contains(r.Value, ctx.LengthTolerance)) ok = Mismatch(result, "viewport '" + r.Key + "' " + r.Value + " is not inside the drawable area " + inner);
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    if (rects[i].Value.Overlaps(rects[j].Value, ctx.LengthTolerance))
                        ok = Mismatch(result, "viewports '" + rects[i].Key + "' and '" + rects[j].Key + "' overlap");
            result.Readback.Add("viewports", list);
            result.Readback.Add("area", new JsonObject().Add("min", new JsonArray(ctx.PaperOut(p.Area.MinX), ctx.PaperOut(p.Area.MinY)))
                .Add("max", new JsonArray(ctx.PaperOut(p.Area.MaxX), ctx.PaperOut(p.Area.MaxY))).Add("unit", SA.Core.Units.Length.Symbol(ctx.PaperUnit)));
            result.Readback.Add("notPlacedCount", p.NotPlaced.Count);
            return ok;
        }
    }
}
