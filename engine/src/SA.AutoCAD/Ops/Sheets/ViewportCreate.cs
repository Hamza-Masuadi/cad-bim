using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// viewport.create: one paper space viewport at a standard scale (stored as the viewport's custom scale), centred on a
    /// model point, locked by default, with per-viewport frozen layers, on a non-plotting viewport layer. See
    /// <see cref="ViewportSupport"/> for the order of API calls.
    /// </summary>
    internal sealed class ViewportCreate : OpHandler<ViewportCreate.Plan>
    {
        public override string Name => "viewport.create";

        internal sealed class Plan
        {
            public ViewportSpec Spec = new ViewportSpec();
            public string Layout = "";
            public ObjectId Id;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            var s = p.Spec;
            bool ok = true;
            p.Layout = args.GetString("layout");
            if (!ctx.IsRef(args, "layout") && !SheetDb.LayoutExists(ctx, p.Layout))
            {
                OpContext.Error(issues, args, "layout", "layout_missing", "there is no layout named '" + p.Layout + "'");
                ok = false;
            }
            else if (string.Equals(p.Layout, "Model", StringComparison.OrdinalIgnoreCase))
            {
                OpContext.Error(issues, args, "layout", "layout_model", "viewports go on a paper space layout, not Model");
                ok = false;
            }
            var ratio = ViewportSupport.CheckScale(ctx, args, "scale", issues);
            if (ratio is null) ok = false;
            else { s.Ratio = ratio.Value; s.ScaleText = args.GetString("scale"); }
            ViewportSupport.CheckFrozen(ctx, args, "frozenLayers", issues);
            var layer = ViewportSupport.ResolveLayer(ctx, args, issues);
            if (layer is null) ok = false; else s.Layer = layer;
            if (!ok) return null;

            s.PaperCentre = ctx.PaperPoint(args, "paperCentre");
            s.Lock = args.GetBool("lock");
            s.TwistDegrees = args.GetAngle("twist");
            s.FrozenLayers = args.GetStringList("frozenLayers");
            if (!s.Lock && ctx.IsValidating)
                OpContext.Warning(issues, args, "lock", "viewport_unlocked", "the viewport will be left unlocked; a pan or zoom inside it changes its scale unnoticed (lock it after checking)");

            double? w = ctx.PaperLengthOrNull(args, "width"), h = ctx.PaperLengthOrNull(args, "height");
            if (args.Has("modelWindow"))
            {
                if (!ViewportSupport.Window(args.GetPoint2List("modelWindow"), out var c, out double mw, out double mh))
                {
                    OpContext.Error(issues, args, "modelWindow", "window_invalid", "the model window needs two corners with different x and y");
                    return null;
                }
                s.ModelCentre = c;
                s.Width = w ?? mw / s.Ratio;
                s.Height = h ?? mh / s.Ratio;
                if ((w.HasValue && w.Value + ctx.LengthTolerance < mw / s.Ratio) || (h.HasValue && h.Value + ctx.LengthTolerance < mh / s.Ratio))
                    OpContext.Warning(issues, args, "width", "window_cropped", "the given width/height is smaller than the model window at " + s.ScaleText + "; part of the window will not show");
            }
            else
            {
                s.ModelCentre = args.GetPoint2("modelCentre");
                s.Width = w ?? 0;
                s.Height = h ?? 0;
            }
            if (!(s.Width > 0) || !(s.Height > 0))
            {
                OpContext.Error(issues, args, "width", "size_missing", "the viewport needs a width and height (or a model window)");
                return null;
            }

            // Inside the sheet (Layout.Limits) when the layout already exists.
            if (ctx.IsValidating && !ctx.Plan.Contains(PlannedNames.Layout, p.Layout))
            {
                var sheet = Db.Read(ctx, tr =>
                {
                    var id = SheetDb.LayoutId(tr, ctx.Database, p.Layout);
                    return id.IsNull ? null : ViewportSupport.SheetRect(ctx, (Layout)tr.GetObject(id, OpenMode.ForRead));
                });
                var rect = new Rect2(s.PaperCentre.X - s.Width / 2, s.PaperCentre.Y - s.Height / 2, s.Width, s.Height);
                if (sheet.HasValue && !sheet.Value.Contains(rect, ctx.LengthTolerance))
                    OpContext.Warning(issues, args, "paperCentre", "viewport_off_sheet", "the viewport " + rect + " extends past the sheet limits " + sheet.Value);
            }
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the viewport could not be planned.");
            var layoutId = SheetDb.LayoutId(ctx.Transaction, ctx.Database, p.Layout);
            if (layoutId.IsNull) throw new InvalidOperationException("Layout '" + p.Layout + "' does not exist.");
            p.Id = ViewportSupport.Create(ctx, layoutId, p.Spec);
            result.Created.Add(p.Spec.Handle);
            result.Outputs.Add("viewport", p.Spec.Handle);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var o = new JsonObject();
            bool ok = ViewportSupport.Check(ctx, p.Id, p.Spec, o, out string problem);
            result.Readback.Add("viewport", o);
            result.Readback.Add("annotationScaleSet", p.Spec.AnnotationScale != null);
            result.Outputs.Add("scaleRatio", o["scaleRatio"] ?? JsonValue.Null);
            return ok || Mismatch(result, problem);
        }
    }
}
