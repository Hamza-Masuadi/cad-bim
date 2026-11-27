using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// revcloud.add: the managed API has no revision cloud object, so the cloud is what REVCLOUD leaves behind: a closed
    /// lightweight polyline of arcs. The outline is made counter-clockwise (and grown by offset); each side is divided
    /// into equal chords no longer than arcLength, and every segment gets the same positive bulge (an arc of 120
    /// degrees), which on a counter-clockwise outline bulges outward. The tag is the tag block (inserted at the current
    /// annotation scale when it is not annotative, so it must be drawn at plotted size in paper units) with its
    /// attribute set, or a triangle with centred single-line text, both sized from the current annotation scale.
    /// </summary>
    internal sealed class RevcloudAdd : OpHandler<RevcloudAdd.Plan>
    {
        public override string Name => "revcloud.add";

        /// <summary>tan(120 degrees / 4).</summary>
        private static readonly double Bulge = Math.Tan(Math.PI / 6);

        internal sealed class Plan
        {
            public List<Point2> Vertices = new List<Point2>();
            public string Layer = "";
            public string? TagText, TagBlock, TagAttribute;
            public string TagLayer = "";
            public Point2 TagAt;
            public double TagHeight;
            public string Cloud = "";
            public List<string> TagHandles = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Layer = DraftDb.Layer(ctx, args, issues, "layer", "revision clouds") };
            double tol = ctx.LengthTolerance;
            List<Point2> outline;
            if (args.GetObject("rect") is OpArgs r)
            {
                var a = r.GetPoint2("min"); var b = r.GetPoint2("max");
                if (b.X - a.X <= tol || b.Y - a.Y <= tol) { OpContext.Error(issues, r, "max", "rect_invalid", "max must be above and to the right of min"); return p; }
                outline = new List<Point2> { a, new Point2(b.X, a.Y), b, new Point2(a.X, b.Y) };
            }
            else if (args.Has("handle"))
            {
                if (ctx.IsRef(args, "handle")) return p;
                string h = args.GetRefId("handle") ?? "";
                var id = ctx.IdOf(h);
                List<Point2>? pts = null;
                string? problem = id.IsNull ? "is not in the drawing" : Db.Read(ctx, tr =>
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Polyline pl)) return "is not a lightweight polyline";
                    if (!PlanGeometry.IsPlan(pl.Normal)) return "does not lie flat in plan";
                    PlanGeometry.Vertices(ctx, pl, out var v, out _);
                    if (!pl.Closed && !(v.Count > 2 && v[0].DistanceTo(v[v.Count - 1]) <= tol)) return "is not closed";
                    if (!pl.Closed) v.RemoveAt(v.Count - 1);
                    pts = v;
                    return null;
                });
                if (problem != null) { OpContext.Error(issues, args, "handle", "outline_invalid", "polyline " + h + " " + problem); return p; }
                outline = pts!;
            }
            else outline = args.GetPoint2List("points");
            if (outline.Count < 3) { OpContext.Error(issues, args, null, "outline_invalid", "the outline needs 3 or more points"); return p; }
            var poly = new Polygon2(outline);
            if (!poly.IsSimple(tol) || poly.Area <= tol * tol) { OpContext.Error(issues, args, null, "outline_invalid", "the outline crosses itself or encloses no area"); return p; }

            var ccw = PlanGeometry.Grow(outline, args.GetLength("offset"));
            double arc = args.GetLengthOrNull("arcLength") ?? Anno.ModelFromPlotted(ctx, 10, LengthUnit.Millimetre);
            if (arc <= tol) { OpContext.Error(issues, args, "arcLength", "arc_too_small", "arcLength must be larger than the drawing tolerance"); return p; }
            for (int i = 0; i < ccw.Count; i++)
            {
                var a = ccw[i]; var b = ccw[(i + 1) % ccw.Count];
                double len = a.DistanceTo(b);
                if (len <= tol) continue;
                int n = Math.Max(1, (int)Math.Round(len / arc));
                for (int k = 0; k < n; k++) p.Vertices.Add(a + (b - a) * ((double)k / n));
            }
            if (p.Vertices.Count > 5000) { OpContext.Error(issues, args, "arcLength", "too_many_arcs", "the cloud would have " + p.Vertices.Count + " arcs; give a larger arcLength"); return p; }

            if (args.GetObject("tag") is OpArgs tag)
            {
                p.TagText = tag.GetString("text");
                p.TagLayer = tag.Has("layer") ? DraftDb.Layer(ctx, tag, issues, "layer", "revision tags") : p.Layer;
                p.TagBlock = tag.GetStringOrNull("block");
                p.TagAttribute = tag.GetString("attribute").ToUpperInvariant();
                double plottedMm = tag.Has("plottedHeight") ? Length.Convert(ctx.PaperLength(tag, "plottedHeight"), ctx.JobUnit, LengthUnit.Millimetre) : 2.5;
                p.TagHeight = Anno.ModelFromPlotted(ctx, plottedMm, LengthUnit.Millimetre);
                var max = new Point2(ccw.Max(q => q.X), ccw.Max(q => q.Y));
                p.TagAt = tag.GetPoint2OrNull("position") ?? max + new Point2(1, 1) * (arc + 2 * p.TagHeight);
                if (p.TagBlock != null && !ctx.IsRef(tag, "block"))
                {
                    if (!DraftDb.BlockExists(ctx, p.TagBlock)) OpContext.Error(issues, tag, "block", "block_missing", DraftDb.MissingBlock(ctx, p.TagBlock));
                    else if (BlockSupport.TagsOf(ctx, p.TagBlock) is List<string> tags && !tags.Contains(p.TagAttribute))
                        OpContext.Error(issues, tag, "attribute", "attribute_unknown", "block '" + p.TagBlock + "' has no attribute " + p.TagAttribute + "; its tags: " + string.Join(", ", tags));
                }
                else if (tag.Has("attribute") && p.TagBlock is null)
                    OpContext.Error(issues, tag, "attribute", "field_not_used", "attribute applies with block");
            }
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var u = ctx.Units;
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var pl = new Polyline();
            for (int i = 0; i < p.Vertices.Count; i++) pl.AddVertexAt(i, u.ToDrawing2d(p.Vertices[i]), Bulge, 0, 0);
            pl.Closed = true;
            pl.SetDatabaseDefaults(db);
            pl.Layer = p.Layer;
            p.Cloud = OpContext.HandleOf(Db.Append(ctx, ms, pl));
            result.Created.Add(p.Cloud);

            if (p.TagText != null)
            {
                if (p.TagBlock != null)
                {
                    var btrId = DraftDb.BlockId(tr, db, p.TagBlock);
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    double s = 1;
                    if (!Anno.IsAnnotative(btr) && Anno.Current(db) is AnnotationScale cur)
                    {
                        double dpp = Anno.DrawingPerPaper(cur);
                        if (!double.IsNaN(dpp) && dpp > 0) s = dpp;
                    }
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { p.TagAttribute!, p.TagText } };
                    var br = BlockSupport.Insert(ctx, btrId, u.ToDrawing(p.TagAt), 0, new Scale3d(s), p.TagLayer, values);
                    p.TagHandles.Add(OpContext.HandleOf(br.ObjectId));
                }
                else
                {
                    double h = p.TagHeight;
                    double radius = 1.8 * h;
                    var tri = new Polyline();
                    for (int k = 0; k < 3; k++)
                    {
                        double ang = Math.PI / 2 + k * 2 * Math.PI / 3;
                        tri.AddVertexAt(k, u.ToDrawing2d(p.TagAt + new Point2(Math.Cos(ang), Math.Sin(ang)) * radius), 0, 0, 0);
                    }
                    tri.Closed = true;
                    tri.SetDatabaseDefaults(db);
                    tri.Layer = p.TagLayer;
                    p.TagHandles.Add(OpContext.HandleOf(Db.Append(ctx, ms, tri)));
                    var text = new DBText();
                    text.SetDatabaseDefaults(db);
                    text.Layer = p.TagLayer;
                    text.TextString = p.TagText;
                    text.Height = u.ToDrawing(h);
                    text.Justify = AttachmentPoint.MiddleCenter;
                    var at = u.ToDrawing(p.TagAt + new Point2(0, -0.25 * radius));
                    text.Position = at;
                    text.AlignmentPoint = at;
                    var tid = Db.Append(ctx, ms, text);
                    text.AdjustAlignment(db);
                    p.TagHandles.Add(OpContext.HandleOf(tid));
                }
                foreach (var h in p.TagHandles) result.Created.Add(h);
            }
            result.Outputs.Add("cloud", p.Cloud).Add("arcCount", p.Vertices.Count).Add("tagHandles", JsonArray.FromStrings(p.TagHandles));
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var pl = (Polyline)tr.GetObject(ctx.IdOf(p.Cloud), OpenMode.ForRead);
            double tol = ctx.LengthTolerance;
            if (!pl.Closed || pl.NumberOfVertices != p.Vertices.Count) mismatches.Add("the cloud has " + pl.NumberOfVertices + " vertices (closed " + pl.Closed + "), expected " + p.Vertices.Count);
            else
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    if (Math.Abs(pl.GetBulgeAt(i) - Bulge) > 1e-9 || ctx.Units.ToJob(pl.GetPoint2dAt(i)).DistanceTo(p.Vertices[i]) > tol)
                    { mismatches.Add("cloud vertex " + i + " reads back differently"); break; }
            if (!string.Equals(pl.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the cloud is on layer " + pl.Layer);
            string? tagRead = null;
            foreach (var h in p.TagHandles)
            {
                var o = tr.GetObject(ctx.IdOf(h), OpenMode.ForRead);
                if (o is BlockReference br) BlockSupport.Read(tr, br).TryGetValue(p.TagAttribute!, out tagRead);
                else if (o is DBText t) tagRead = t.TextString;
            }
            if (p.TagText != null && tagRead != p.TagText) mismatches.Add("the tag reads '" + (tagRead ?? "(nothing)") + "', expected '" + p.TagText + "'");
            result.Readback.Add("cloud", p.Cloud).Add("arcs", pl.NumberOfVertices).Add("layer", pl.Layer).Add("tag", tagRead != null ? (JsonValue)tagRead : JsonValue.Null);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
