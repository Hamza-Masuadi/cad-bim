using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets.Support
{
    /// <summary>What one paper space viewport should be, in job units (paper coordinates are paper-space drawing coordinates expressed in the job unit).</summary>
    internal sealed class ViewportSpec
    {
        public string Id = "";
        public Point2 PaperCentre;
        public double Width, Height;
        public Point2 ModelCentre;
        public string ScaleText = "";
        /// <summary>Model length per paper length (100 for 1:100).</summary>
        public double Ratio;
        public bool Lock = true;
        public List<string> FrozenLayers = new List<string>();
        public double TwistDegrees;
        public string Layer = "";
        public string Handle = "";
        public string? AnnotationScale;
    }

    /// <summary>
    /// Creating and reading back paper space viewports.
    ///
    /// Order of calls (each step needs the previous one):
    /// 1. The layout gets its overall paper space viewport first (<see cref="SheetDb.EnsureInitialized"/>); otherwise the
    ///    first viewport appended to a never-activated layout becomes the overall one.
    /// 2. The new Viewport gets centre, width, height and layer, and is appended to the layout's block and added to the
    ///    transaction. AcDbViewport::setOn and the layer freeze calls fail with eNotInDatabase on a viewport that is not
    ///    database resident and owned by paper space (ObjectARX reference for setIsOn/setOff), so everything below is
    ///    done after the append.
    /// 3. View: ViewDirection +Z, ViewTarget = the model point (WCS), ViewCenter = (0, 0) in DCS so the target is at the
    ///    centre whatever the twist, TwistAngle, then CustomScale = 1 / ratio (paper units per model unit; paper and
    ///    model space share the drawing's units).
    /// 4. On = true. Active viewports are limited by MAXACTVP; a viewport over that limit reads back as off, which fails
    ///    the op rather than leaving a blank viewport.
    /// 5. Per-viewport frozen layers, then the annotation scale when the drawing's scale list has the same ratio
    ///    (Viewport.AnnotationScale; a scale missing from the list is not added, that is doc.setup's job, and a warning
    ///    says so), and Locked last.
    /// </summary>
    internal static class ViewportSupport
    {
        /// <summary>Checks a scale: parseable, and approved by the standard when it lists scales (AC-036). Returns the ratio or null.</summary>
        internal static double? CheckScale(OpContext ctx, OpArgs args, string field, List<Issue> issues)
        {
            if (ctx.IsRef(args, field)) return null;
            string text = args.GetString(field);
            if (!Scale.TryParseRatio(text, out double ratio))
            {
                OpContext.Error(issues, args, field, IssueCodes.ScaleInvalid, "'" + text + "' is not a scale (examples: 1:100, 1/4\" = 1'-0\")");
                return null;
            }
            var std = ctx.Standard;
            if (std != null && std.Scales.Count > 0 && !std.Scales.Any(s => Math.Abs(s.Ratio - ratio) <= 1e-9 * ratio))
                OpContext.Error(issues, args, field, "scale_not_approved", "scale " + text + " is not in the standard's approved list (" + string.Join(", ", std.Scales.Select(s => s.Name).Take(20)) + ")");
            return ratio;
        }

        /// <summary>Checks frozen layer names exist (or are planned) and are not the viewport's own layer concerns.</summary>
        internal static void CheckFrozen(OpContext ctx, OpArgs args, string field, List<Issue> issues)
        {
            if (ctx.IsRef(args, field)) return;
            foreach (var name in args.GetStringList(field))
                if (!Db.LayerExists(ctx, name))
                    OpContext.Error(issues, args, field, IssueCodes.LayerMissing, "frozen layer '" + name + "' is not in the drawing");
        }

        /// <summary>The viewport layer: the job's, else the standard's non-plotting viewport layer, else the current layer. Layer 0 and Defpoints are refused.</summary>
        internal static string? ResolveLayer(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            string? layer = StandardLayers.Resolve(ctx, args, "layer", StandardLayers.Viewport(ctx.Standard), "viewports", issues);
            if (layer is null) return null;
            if (string.Equals(layer, "0", StringComparison.Ordinal))
            {
                OpContext.Error(issues, args, "layer", "layer_not_allowed", "viewports are not placed on layer 0; use a non-plotting viewport layer");
                return null;
            }
            if (ctx.IsValidating && !ctx.IsRef(args, "layer") && Db.LayerExists(ctx, layer) && !ctx.Plan.Contains(PlannedNames.Layer, layer))
            {
                bool plots = Db.Read(ctx, tr =>
                {
                    var id = Db.SymbolId(tr, ctx.Database.LayerTableId, layer);
                    return !id.IsNull && ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).IsPlottable;
                });
                if (plots && !string.Equals(layer, SheetDb.Defpoints, StringComparison.OrdinalIgnoreCase))
                    OpContext.Warning(issues, args, "layer", "viewport_layer_plots", "viewport layer '" + layer + "' plots, so the viewport frames will print; use a non-plotting layer");
            }
            return layer;
        }

        /// <summary>Creates the viewport inside the job transaction (see the class remarks for the order). Returns its id.</summary>
        internal static ObjectId Create(OpContext ctx, ObjectId layoutId, ViewportSpec s)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            SheetDb.EnsureInitialized(tr, layout);
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

            var vp = new Viewport();
            vp.SetDatabaseDefaults(db);
            vp.CenterPoint = ctx.Units.ToDrawing(s.PaperCentre);
            vp.Width = ctx.Units.ToDrawing(s.Width);
            vp.Height = ctx.Units.ToDrawing(s.Height);
            vp.Layer = s.Layer;
            var id = Db.Append(ctx, btr, vp);

            vp.ViewDirection = Vector3d.ZAxis;
            vp.ViewTarget = ctx.Units.ToDrawing(s.ModelCentre);
            vp.ViewCenter = Point2d.Origin;
            vp.TwistAngle = s.TwistDegrees * Math.PI / 180.0;
            vp.CustomScale = 1.0 / s.Ratio;
            vp.On = true;

            if (s.FrozenLayers.Count > 0)
            {
                var ids = new ObjectIdCollection();
                foreach (var name in s.FrozenLayers)
                {
                    var lid = Db.SymbolId(tr, db.LayerTableId, name);
                    if (lid.IsNull) throw new InvalidOperationException("Layer '" + name + "' to freeze in the viewport does not exist.");
                    ids.Add(lid);
                }
                vp.FreezeLayersInViewport(ids.GetEnumerator());
            }

            var anno = AnnotationScales.Find(db, s.ScaleText);
            if (anno != null)
            {
                vp.AnnotationScale = anno;
                s.AnnotationScale = anno.Name;
            }
            else ctx.Warn("Viewport " + (s.Id.Length > 0 ? "'" + s.Id + "' " : "") + "at " + s.ScaleText + ": the drawing's annotation scale list has no scale with that ratio, so the viewport's annotation scale was left as it was. Add the scale with doc.setup to make annotative objects show at the viewport scale.");

            vp.Locked = s.Lock;
            s.Handle = OpContext.HandleOf(id);
            return id;
        }

        /// <summary>Reads a viewport back and compares it with the spec. Adds the read values to <paramref name="o"/>.</summary>
        internal static bool Check(OpContext ctx, ObjectId id, ViewportSpec s, JsonObject o, out string problem)
        {
            problem = "";
            var tr = ctx.Transaction;
            var vp = (Viewport)tr.GetObject(id, OpenMode.ForRead);
            double tol = ctx.LengthTolerance;
            var centre = ctx.Units.ToJob(vp.CenterPoint);
            double w = ctx.Units.ToJob(vp.Width), h = ctx.Units.ToJob(vp.Height);
            var target = ctx.Units.ToJob(vp.ViewTarget);
            double cs = vp.CustomScale;
            double ratio = cs > 0 ? 1.0 / cs : double.NaN;
            var frozen = new List<string>();
            foreach (ObjectId lid in vp.GetFrozenLayers())
                frozen.Add(((LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead)).Name);
            string anno = "";
            try { anno = vp.AnnotationScale?.Name ?? ""; } catch (Exception) { }
            o.Add("handle", OpContext.HandleOf(id))
             .Add("centre", ctx.PaperOut(centre))
             .Add("width", ctx.PaperOut(w)).Add("height", ctx.PaperOut(h))
             .Add("viewTarget", DrawingUnits.PointJson(target))
             .Add("scaleRatio", DrawingUnits.Round(ratio))
             .Add("on", vp.On).Add("locked", vp.Locked)
             .Add("layer", vp.Layer)
             .Add("twist", DrawingUnits.Round(vp.TwistAngle * 180.0 / Math.PI))
             .Add("frozenLayers", JsonArray.FromStrings(frozen))
             .Add("annotationScale", anno);

            var bad = new List<string>();
            if (Math.Abs(centre.X - s.PaperCentre.X) > tol || Math.Abs(centre.Y - s.PaperCentre.Y) > tol) bad.Add("centre");
            if (Math.Abs(w - s.Width) > tol || Math.Abs(h - s.Height) > tol) bad.Add("size");
            if (Math.Abs(target.X - s.ModelCentre.X) > tol || Math.Abs(target.Y - s.ModelCentre.Y) > tol) bad.Add("view target");
            if (double.IsNaN(ratio) || Math.Abs(ratio - s.Ratio) > 1e-9 * s.Ratio) bad.Add("scale (read " + ratio + ", expected " + s.Ratio + ")");
            if (!vp.On) bad.Add("on (the viewport is off; MAXACTVP may be reached)");
            if (vp.Locked != s.Lock) bad.Add("locked");
            if (!string.Equals(vp.Layer, s.Layer, StringComparison.OrdinalIgnoreCase)) bad.Add("layer");
            if (!SA.Core.Units.Tolerance.AngleEqual(vp.TwistAngle * 180.0 / Math.PI, s.TwistDegrees)) bad.Add("twist");
            foreach (var f in s.FrozenLayers)
                if (!frozen.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) bad.Add("frozen layer " + f);
            if (s.AnnotationScale != null && !string.Equals(anno, s.AnnotationScale, StringComparison.OrdinalIgnoreCase)) bad.Add("annotation scale");
            if (bad.Count > 0) problem = "viewport " + (s.Id.Length > 0 ? "'" + s.Id + "' " : "") + "read back differs: " + string.Join(", ", bad);
            return bad.Count == 0;
        }

        /// <summary>A model window [[x1,y1],[x2,y2]] as a centre and size (job units). False when degenerate.</summary>
        internal static bool Window(List<Point2> pts, out Point2 centre, out double width, out double height)
        {
            centre = default; width = height = 0;
            if (pts.Count != 2) return false;
            var r = Rect2.FromCorners(pts[0], pts[1]);
            if (!(r.Width > 0) || !(r.Height > 0)) return false;
            centre = r.Centre; width = r.Width; height = r.Height;
            return true;
        }

        /// <summary>The layout's sheet in paper-space drawing units (Layout.Limits), in job units, when it can be read.</summary>
        internal static Rect2? SheetRect(OpContext ctx, Layout layout)
        {
            try
            {
                var lim = layout.Limits;
                var a = ctx.Units.ToJob(lim.MinPoint);
                var b = ctx.Units.ToJob(lim.MaxPoint);
                var r = Rect2.FromCorners(a, b);
                return r.Width > 0 && r.Height > 0 ? r : (Rect2?)null;
            }
            catch (Exception) { return null; }
        }
    }
}
