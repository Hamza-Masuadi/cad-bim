using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// grid.draw: one LINE per grid line on the grid layer, and a bubble beyond each chosen end: a circle with the
    /// label as middle-centred single-line text (height half the bubble diameter), or the bubbleBlock with its LABEL
    /// attribute, scaled so its width equals the diameter (annotative bubble blocks keep scale 1 and follow the
    /// annotation scale). The line runs to the bubble's edge; the bubble's centre is one radius beyond the end.
    /// Orthogonal grids: vertical lines at the cumulative xSpacings (labels from xLabelStart, numbers by default),
    /// horizontal lines at the cumulative ySpacings (letters by default, skipping I and O), each extended by
    /// "extension" past the outer grids (default 1.5 bubble diameters), rotated about origin.
    /// Bubble diameter defaults to 10 mm plotted at the current annotation scale. Properties stay ByLayer: a linetype is
    /// set on the lines only when it differs from the layer's (with a warning, AC-003).
    /// </summary>
    internal sealed class GridDraw : OpHandler<GridDraw.GridPlan>
    {
        public override string Name => "grid.draw";

        internal sealed class GridLine
        {
            public string Label = "";
            public Point2 Start, End;
            public List<string> Handles = new List<string>();
            public string LineHandle = "";
            public List<KeyValuePair<string, Point2>> Bubbles = new List<KeyValuePair<string, Point2>>();
        }

        internal sealed class GridPlan
        {
            public List<GridLine> Lines = new List<GridLine>();
            public string Layer = "";
            public string? Linetype;
            public LinetypeDefinition? LinetypeDef;
            public bool SetLinetype;
            public double Diameter;
            public string? BubbleBlock;
            public bool BubbleBlockAnnotative;
            public double BubbleScale = 1;
            public string TextStyle = "";
            public bool AtStart, AtEnd;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Plan(ctx, args, issues);
            if (p.LinetypeDef != null) ctx.Plan.Add(PlannedNames.Linetype, p.Linetype!);
        }

        private static GridPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new GridPlan();
            string bubbles = args.GetString("bubbles");
            p.AtStart = bubbles != "end";
            p.AtEnd = bubbles != "start";
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "grid lines");
            p.Diameter = args.GetLengthOrNull("bubbleDiameter") ?? Anno.ModelFromPlotted(ctx, 10, LengthUnit.Millimetre);
            p.TextStyle = DraftDb.TextStyle(ctx, args, issues, "textStyle");

            p.Linetype = args.GetStringOrNull("linetype");
            if (p.Linetype != null)
            {
                string? problem = LinetypeSupport.Check(ctx, p.Linetype, out p.LinetypeDef);
                if (problem != null) OpContext.Error(issues, args, "linetype", IssueCodes.LinetypeMissing, problem);
                string layerLt = Db.Read(ctx, tr =>
                {
                    var id = Db.SymbolId(tr, ctx.Database.LayerTableId, p.Layer);
                    if (id.IsNull) return "";
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    return ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name;
                });
                p.SetLinetype = !string.Equals(layerLt, p.Linetype, StringComparison.OrdinalIgnoreCase);
                if (p.SetLinetype)
                    OpContext.Warning(issues, args, "linetype", "linetype_override", "layer '" + p.Layer + "' has linetype '" + layerLt + "'; setting '" + p.Linetype +
                        "' on each grid line overrides ByLayer (AC-003). Prefer a grid layer with that linetype.");
            }

            p.BubbleBlock = args.GetStringOrNull("bubbleBlock");
            if (p.BubbleBlock != null)
            {
                if (!DraftDb.BlockExists(ctx, p.BubbleBlock)) OpContext.Error(issues, args, "bubbleBlock", "block_missing", DraftDb.MissingBlock(ctx, p.BubbleBlock));
                else
                {
                    var tags = BlockSupport.TagsOf(ctx, p.BubbleBlock);
                    if (tags != null && !tags.Contains("LABEL"))
                        OpContext.Error(issues, args, "bubbleBlock", "attribute_unknown", "bubble block '" + p.BubbleBlock + "' has no LABEL attribute" + (tags.Count > 0 ? "; its tags: " + string.Join(", ", tags) : ""));
                    Db.Read(ctx, tr =>
                    {
                        var id = DraftDb.BlockId(tr, ctx.Database, p.BubbleBlock);
                        if (id.IsNull) return 0;
                        var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        p.BubbleBlockAnnotative = Anno.IsAnnotative(btr);
                        var box = BlockSupport.Extents(tr, btr);
                        if (!p.BubbleBlockAnnotative)
                        {
                            if (box is null || box.Value.Width <= 0) OpContext.Error(issues, args, "bubbleBlock", "block_empty", "bubble block '" + p.BubbleBlock + "' has no geometry to size it by");
                            else p.BubbleScale = ctx.Units.ToDrawing(p.Diameter) / box.Value.Width;
                        }
                        return 0;
                    });
                }
            }

            var labels = new List<string>();
            foreach (var l in args.GetObjectList("lines"))
            {
                var g = new GridLine { Label = l.GetString("label"), Start = l.GetPoint2("start"), End = l.GetPoint2("end") };
                if (g.Start.DistanceTo(g.End) <= ctx.LengthTolerance) OpContext.Error(issues, l, "end", "grid_zero", "grid line '" + g.Label + "' has no length");
                if (Labels.UsesIorO(g.Label)) OpContext.Warning(issues, l, "label", "grid_label_io", "grid letters normally skip I and O (they read as 1 and 0)");
                p.Lines.Add(g);
            }
            var o = args.GetObject("orthogonal");
            if (o != null)
            {
                var origin = o.GetPoint2("origin");
                var xs = o.GetLengthList("xSpacings");
                var ys = o.GetLengthList("ySpacings");
                double rot = o.GetAngle("rotation") * Math.PI / 180;
                double ext = o.GetLengthOrNull("extension") ?? 1.5 * p.Diameter;
                var xPos = new List<double> { 0 };
                foreach (var x in xs) xPos.Add(xPos[xPos.Count - 1] + x);
                var yPos = new List<double> { 0 };
                foreach (var y in ys) yPos.Add(yPos[yPos.Count - 1] + y);
                if (xs.Any(v => !(v > 0)) || ys.Any(v => !(v > 0))) OpContext.Error(issues, o, "xSpacings", "spacing_invalid", "spacings must be positive");
                if (!Labels.TrySequence(o.GetString("xLabelStart"), xPos.Count, out var xl, out string ex)) OpContext.Error(issues, o, "xLabelStart", "grid_label_invalid", ex);
                if (!Labels.TrySequence(o.GetString("yLabelStart"), yPos.Count, out var yl, out string ey)) OpContext.Error(issues, o, "yLabelStart", "grid_label_invalid", ey);
                Point2 R(double x, double y) => origin + new Point2(x, y).Rotate(rot);
                double y0 = -ext, y1 = yPos[yPos.Count - 1] + ext, x0 = -ext, x1 = xPos[xPos.Count - 1] + ext;
                for (int i = 0; i < xPos.Count && i < xl.Count; i++) p.Lines.Add(new GridLine { Label = xl[i], Start = R(xPos[i], y0), End = R(xPos[i], y1) });
                for (int i = 0; i < yPos.Count && i < yl.Count; i++) p.Lines.Add(new GridLine { Label = yl[i], Start = R(x0, yPos[i]), End = R(x1, yPos[i]) });
            }
            foreach (var dup in p.Lines.GroupBy(g => g.Label.Trim().ToUpperInvariant()).Where(g => g.Count() > 1))
                OpContext.Error(issues, args, "lines", "grid_label_duplicate", "grid label '" + dup.Key + "' is used " + dup.Count() + " times");
            foreach (var g in p.Lines)
            {
                var d = (g.End - g.Start).Normalised();
                double r = p.Diameter / 2;
                if (p.AtStart) g.Bubbles.Add(new KeyValuePair<string, Point2>("", g.Start - d * r));
                if (p.AtEnd) g.Bubbles.Add(new KeyValuePair<string, Point2>("", g.End + d * r));
            }
            return p;
        }

        protected override GridPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            ObjectId ltId = ObjectId.Null;
            if (p.Linetype != null && p.SetLinetype)
            {
                ltId = LinetypeSupport.Ensure(ctx, p.Linetype, p.LinetypeDef, out string? ltHandle);
                if (ltHandle != null) result.Created.Add(ltHandle);
            }
            var styleId = Db.SymbolId(tr, db.TextStyleTableId, p.TextStyle);
            ObjectId blockId = p.BubbleBlock != null ? DraftDb.BlockId(tr, db, p.BubbleBlock) : ObjectId.Null;
            if (p.BubbleBlockAnnotative) Anno.UseScale(ctx, Anno.CurrentName(db));
            var grids = new JsonArray();
            foreach (var g in p.Lines)
            {
                var line = new Line(ctx.Units.ToDrawing(g.Start), ctx.Units.ToDrawing(g.End));
                line.SetDatabaseDefaults(db);
                line.Layer = p.Layer;
                if (!ltId.IsNull) line.LinetypeId = ltId;
                g.LineHandle = OpContext.HandleOf(Db.Append(ctx, ms, line));
                g.Handles.Add(g.LineHandle);
                for (int b = 0; b < g.Bubbles.Count; b++)
                {
                    var c = g.Bubbles[b].Value;
                    string handle;
                    if (!blockId.IsNull)
                    {
                        var br = BlockSupport.Insert(ctx, blockId, ctx.Units.ToDrawing(c), 0, new Scale3d(p.BubbleScale), p.Layer,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "LABEL", g.Label } });
                        handle = OpContext.HandleOf(br.ObjectId);
                        g.Handles.Add(handle);
                    }
                    else
                    {
                        var circle = new Circle(ctx.Units.ToDrawing(c), Vector3d.ZAxis, ctx.Units.ToDrawing(p.Diameter / 2));
                        circle.SetDatabaseDefaults(db);
                        circle.Layer = p.Layer;
                        handle = OpContext.HandleOf(Db.Append(ctx, ms, circle));
                        g.Handles.Add(handle);
                        var text = new DBText();
                        text.SetDatabaseDefaults(db);
                        text.TextString = g.Label;
                        text.TextStyleId = styleId;
                        text.Height = ctx.Units.ToDrawing(p.Diameter / 2);
                        text.Layer = p.Layer;
                        text.Position = ctx.Units.ToDrawing(c);
                        text.HorizontalMode = TextHorizontalMode.TextCenter;
                        text.VerticalMode = TextVerticalMode.TextVerticalMid;
                        text.AlignmentPoint = ctx.Units.ToDrawing(c);
                        g.Handles.Add(OpContext.HandleOf(Db.Append(ctx, ms, text)));
                        text.AdjustAlignment(db);
                    }
                    g.Bubbles[b] = new KeyValuePair<string, Point2>(handle, c);
                }
                result.Created.AddRange(g.Handles);
                grids.Add(new JsonObject().Add("label", g.Label).Add("handles", JsonArray.FromStrings(g.Handles))
                    .Add("start", DrawingUnits.PointJson(g.Start)).Add("end", DrawingUnits.PointJson(g.End)));
            }
            result.Outputs.Add("grids", grids);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, GridPlan p)
        {
            var tr = ctx.Transaction;
            double tol = ctx.LengthTolerance;
            var mismatches = new List<string>();
            int bubbles = 0;
            foreach (var g in p.Lines)
            {
                var id = ctx.IdOf(g.LineHandle);
                if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Line line)) { mismatches.Add("grid line '" + g.Label + "' is missing"); continue; }
                var a = ctx.Units.ToJob(line.StartPoint);
                var b = ctx.Units.ToJob(line.EndPoint);
                if (a.DistanceTo(g.Start) > tol || b.DistanceTo(g.End) > tol) mismatches.Add("grid line '" + g.Label + "' reads back as " + a + " -> " + b);
                if (!string.Equals(line.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("grid line '" + g.Label + "' is on layer '" + line.Layer + "'");
                foreach (var kv in g.Bubbles)
                {
                    var bid = ctx.IdOf(kv.Key);
                    var obj = bid.IsNull ? null : tr.GetObject(bid, OpenMode.ForRead);
                    if (obj is Circle c)
                    {
                        if (ctx.Units.ToJob(c.Center).DistanceTo(kv.Value) > tol || Math.Abs(ctx.Units.ToJob(c.Radius) - p.Diameter / 2) > tol) mismatches.Add("bubble of '" + g.Label + "' reads back at the wrong place or size");
                    }
                    else if (obj is BlockReference br)
                    {
                        if (ctx.Units.ToJob(br.Position).DistanceTo(kv.Value) > tol) mismatches.Add("bubble block of '" + g.Label + "' reads back at the wrong place");
                        var attrs = BlockSupport.Read(tr, br);
                        if (!attrs.TryGetValue("LABEL", out var lab) || lab != g.Label) mismatches.Add("bubble block of '" + g.Label + "' has LABEL '" + lab + "'");
                    }
                    else { mismatches.Add("bubble of '" + g.Label + "' is missing"); continue; }
                    bubbles++;
                }
                foreach (var h in g.Handles)
                {
                    var tid = ctx.IdOf(h);
                    if (!tid.IsNull && tr.GetObject(tid, OpenMode.ForRead) is DBText t && t.TextString != g.Label) mismatches.Add("label text of '" + g.Label + "' reads back as '" + t.TextString + "'");
                }
            }
            result.Readback.Add("gridCount", p.Lines.Count).Add("bubbleCount", bubbles).Add("bubbleDiameter", AcadValues.Round(p.Diameter))
                .Add("labels", JsonArray.FromStrings(p.Lines.Select(l => l.Label))).Add("layer", p.Layer);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
