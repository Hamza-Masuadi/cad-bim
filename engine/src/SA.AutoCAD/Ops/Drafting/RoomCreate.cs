using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// room.create: a closed lightweight polyline for the room boundary, its area from Core's Polygon2, and a tag.
    /// Boundary: the given points; or the smallest closed outline loop of the referenced wall.draw result that contains
    /// the inside point (its inner face loop); or, without "from", the smallest region around the inside point formed by
    /// model-space lines, polylines and arcs on layers that are on and thawed (<see cref="PlanarFaces"/>; polyline bulges
    /// are taken as chords, arcs as chords of at most 10 degrees). Geometry inside block and xref inserts is not traced: an
    /// insert overlapping the traced boundary gives the warning room_boundary_inserts. The boundary must be simple, closed and of non-zero area (AC-028).
    /// Tag: the tagBlock with NAME, NUMBER and AREA attributes; without a tag block (the standard names none) an
    /// annotative multiline text "NAME\PNUMBER\PAREA" at the room-name height of the standard ("room", else 3.5 mm
    /// plotted), at the current annotation scale. AREA is written as "12.34 m²" (two decimals) or "133 ft²".
    /// The tag goes on the boundary's layer. Tag point: the centroid when it lies inside, else the middle of the widest
    /// horizontal span through the centroid's height.
    /// </summary>
    internal sealed class RoomCreate : OpHandler<RoomCreate.RoomPlan>
    {
        public override string Name => "room.create";

        internal sealed class RoomPlan
        {
            public List<Point2> Boundary = new List<Point2>();
            public string BoundarySource = "";
            public double Area, Perimeter;
            public string Name = "", Number = "", AreaText = "";
            public string Layer = "";
            public bool Tag;
            public string? TagBlock;
            public Point2 TagPoint;
            public double TagHeight;
            public string TextStyle = "";
            public string? HatchPattern;
            public double HatchScale = 1;
            public string? HatchLayer;
            public string BoundaryHandle = "", TagHandle = "", HatchHandle = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static RoomPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new RoomPlan
            {
                Name = args.GetString("name").Trim(),
                Number = (args.GetStringOrNull("number") ?? "").Trim(),
                Tag = args.GetBool("tag"),
                TagBlock = args.GetStringOrNull("tagBlock"),
            };
            if (p.Name.Length == 0) OpContext.Error(issues, args, "name", "room_name_empty", "every room needs a name");
            if (p.Number.Length == 0) OpContext.Warning(issues, args, "number", "room_number_missing", "every room should have a number (rooms-areas: name and number, no duplicates)");
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "room boundaries");
            double tol = Length.Convert(1, LengthUnit.Millimetre, ctx.JobUnit);

            if (args.Has("boundary"))
            {
                p.Boundary = PlanarFaces.Simplify(args.GetPoint2List("boundary"), tol);
                p.BoundarySource = "points";
            }
            else if (args.Has("inside"))
            {
                var inside = args.GetPoint2("inside");
                if (args.Has("from"))
                {
                    if (ctx.IsRef(args, "from"))
                    {
                        string? opId = WallGeometry.RefOpId(ctx, "from");
                        var op = opId != null ? ctx.Job.FindOp(opId) : null;
                        if (op != null)
                        {
                            var walls = WallGeometry.FromWallOp(ctx, op, out string reason);
                            if (walls is null) OpContext.Warning(issues, args, "from", IssueCodes.RefDeferred, reason + "; the loop is found at execution");
                        }
                        return p;
                    }
                    var loops = args.Get("from") is JsonObject res ? ((res["outputs"] as JsonObject)?["loops"] as JsonArray) : null;
                    if (loops is null) { OpContext.Error(issues, args, "from", "from_invalid", "'from' must be a wall.draw result (with outputs.loops)"); return p; }
                    List<Point2>? best = null;
                    double bestArea = double.MaxValue;
                    foreach (var l in loops.OfType<JsonArray>())
                    {
                        var pts = l.Select(v => OpArgs.ToPoint2(v, ctx.JobUnit)).ToList();
                        pts = PlanarFaces.Simplify(pts, tol);
                        if (pts.Count < 3) continue;
                        var poly = new Polygon2(pts);
                        if (poly.Locate(inside, 0) != PointLocation.Inside) continue;
                        if (poly.Area < bestArea) { bestArea = poly.Area; best = pts; }
                    }
                    if (best is null) { OpContext.Error(issues, args, "inside", "room_not_enclosed", "no closed wall loop of the referenced wall.draw contains the inside point"); return p; }
                    p.Boundary = best;
                    p.BoundarySource = "wall loop";
                }
                else
                {
                    var inserts = new List<KeyValuePair<string, Segment2>>();
                    var segs = Db.Read(ctx, tr => Segments(ctx, tr, inserts));
                    var face = PlanarFaces.Enclosing(segs, inside, tol, out string reason);
                    if (face is null)
                    {
                        OpContext.Error(issues, args, "inside", "room_not_enclosed", reason + (inserts.Count > 0 ? "; " + inserts.Count +
                            " block or xref inserts on visible layers were not traced" : ""));
                        return p;
                    }
                    double minX = face.Min(q => q.X), maxX = face.Max(q => q.X), minY = face.Min(q => q.Y), maxY = face.Max(q => q.Y);
                    var near = inserts.Where(kv => kv.Value.A.X <= maxX && kv.Value.B.X >= minX && kv.Value.A.Y <= maxY && kv.Value.B.Y >= minY).Select(kv => kv.Key).ToList();
                    if (near.Count > 0)
                        OpContext.Warning(issues, args, "inside", "room_boundary_inserts", "the traced boundary ignores geometry inside block or xref inserts that overlap it (" +
                            string.Join(", ", near.Take(5)) + (near.Count > 5 ? ", ..." : "") + "); check the room outline");
                    p.Boundary = face;
                    p.BoundarySource = "drawing lines";
                }
            }

            if (p.Boundary.Count < 3) { OpContext.Error(issues, args, "boundary", "room_boundary_invalid", "the boundary needs at least three distinct points"); return p; }
            var polygon = new Polygon2(p.Boundary);
            if (!polygon.IsSimple(tol)) OpContext.Error(issues, args, "boundary", "room_boundary_self_intersects", "the boundary crosses itself");
            if (!(polygon.Area > tol * tol)) OpContext.Error(issues, args, "boundary", "room_area_zero", "the boundary encloses no area");
            if (!polygon.IsCounterClockwise) { p.Boundary.Reverse(); polygon = new Polygon2(p.Boundary); }
            p.Area = polygon.Area;
            p.Perimeter = polygon.Perimeter;
            p.AreaText = FormatArea(p.Area, ctx.JobUnit, args.GetString("areaUnit"));
            p.TagPoint = args.GetPoint2OrNull("tagPoint") ?? InsidePoint(polygon);
            if (args.Has("tagPoint") && polygon.Locate(p.TagPoint, 0) == PointLocation.Outside)
                OpContext.Warning(issues, args, "tagPoint", "tag_outside", "the tag point lies outside the room");

            if (p.Tag)
            {
                if (p.TagBlock != null)
                {
                    if (!DraftDb.BlockExists(ctx, p.TagBlock)) OpContext.Error(issues, args, "tagBlock", "block_missing", DraftDb.MissingBlock(ctx, p.TagBlock));
                    else
                    {
                        var tags = BlockSupport.TagsOf(ctx, p.TagBlock);
                        if (tags != null)
                        {
                            var missing = new[] { "NAME", "NUMBER", "AREA" }.Where(t => !tags.Contains(t)).ToList();
                            if (missing.Contains("NAME")) OpContext.Error(issues, args, "tagBlock", "attribute_unknown", "tag block '" + p.TagBlock + "' has no NAME attribute; its tags: " + string.Join(", ", tags));
                            else if (missing.Count > 0) OpContext.Warning(issues, args, "tagBlock", "attribute_partial", "tag block '" + p.TagBlock + "' lacks " + string.Join(", ", missing));
                        }
                    }
                }
                else
                {
                    TextHeight? th = ctx.Standard?.FindTextHeight("room") ?? ctx.Standard?.FindTextHeight("roomName");
                    p.TextStyle = DraftDb.CurrentTextStyleName(ctx);
                    if (Anno.Current(ctx.Database) is null) OpContext.Error(issues, args, "tag", IssueCodes.AnnotationScaleMissing, "the text tag is annotative and needs a current annotation scale");
                    // Paper height in job-unit terms; converted to paper units when written.
                    p.TagHeight = th != null ? Length.Convert(th.Plotted, th.Unit, ctx.JobUnit) : Length.Convert(3.5, LengthUnit.Millimetre, ctx.JobUnit);
                }
            }

            var hatch = args.GetObject("hatch");
            if (hatch != null)
            {
                p.HatchPattern = hatch.GetString("pattern");
                p.HatchScale = hatch.GetNumber("scale");
                p.HatchLayer = hatch.Has("layer") ? DraftDb.Layer(ctx, hatch, issues, "layer", "hatches") : p.Layer;
                if (!string.Equals(p.HatchPattern, "SOLID", StringComparison.OrdinalIgnoreCase))
                {
                    string file = Db.DefaultResourceFile(ctx.Database, ".pat");
                    string? path = Db.FindSupportFile(ctx.Database, file);
                    if (path is null || !ResourceFiles.PatternExists(path, p.HatchPattern))
                        OpContext.Error(issues, hatch, "pattern", IssueCodes.PatternMissing, "hatch pattern '" + p.HatchPattern + "' is not in " + (path ?? file));
                }
            }
            return p;
        }

        /// <summary>
        /// Straight segments of model-space lines, polylines (bulges as chords) and arcs (as chords of at most 10 degrees) on
        /// visible layers, job units. Block and xref inserts on visible layers are not traced; their handles and extents
        /// (A = min, B = max) go to <paramref name="inserts"/>.
        /// </summary>
        private static List<Segment2> Segments(OpContext ctx, Transaction tr, List<KeyValuePair<string, Segment2>> inserts)
        {
            var list = new List<Segment2>();
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead);
            var visible = new Dictionary<ObjectId, bool>();
            foreach (ObjectId id in ms)
            {
                if (id.IsErased) continue;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent is BlockReference br)
                {
                    if (!visible.TryGetValue(br.LayerId, out bool shown))
                    {
                        var bl = (LayerTableRecord)tr.GetObject(br.LayerId, OpenMode.ForRead);
                        visible[br.LayerId] = shown = !bl.IsOff && !bl.IsFrozen;
                    }
                    if (!shown) continue;
                    try
                    {
                        var box = br.GeometricExtents;
                        inserts.Add(new KeyValuePair<string, Segment2>(OpContext.HandleOf(id), new Segment2(ctx.Units.ToJob(box.MinPoint), ctx.Units.ToJob(box.MaxPoint))));
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { } // an insert with no geometry has no extents
                    continue;
                }
                var e = ent as Curve;
                if (e is null) continue;
                if (!visible.TryGetValue(e.LayerId, out bool vis))
                {
                    var l = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
                    visible[e.LayerId] = vis = !l.IsOff && !l.IsFrozen;
                }
                if (!vis) continue;
                if (e is Line ln) list.Add(new Segment2(ctx.Units.ToJob(ln.StartPoint), ctx.Units.ToJob(ln.EndPoint)));
                else if (e is Polyline pl)
                {
                    int n = pl.NumberOfVertices;
                    int segs = pl.Closed ? n : n - 1;
                    for (int i = 0; i < segs; i++)
                        list.Add(new Segment2(ctx.Units.ToJob(pl.GetPoint2dAt(i)), ctx.Units.ToJob(pl.GetPoint2dAt((i + 1) % n))));
                }
                else if (e is Arc arc)
                {
                    double t0 = arc.StartParam, t1 = arc.EndParam;
                    int chords = Math.Max(1, (int)Math.Ceiling((t1 - t0) / (Math.PI / 18)));
                    var prev = ctx.Units.ToJob(arc.StartPoint);
                    for (int i = 1; i <= chords; i++)
                    {
                        var next = ctx.Units.ToJob(i == chords ? arc.EndPoint : arc.GetPointAtParameter(t0 + (t1 - t0) * i / chords));
                        list.Add(new Segment2(prev, next));
                        prev = next;
                    }
                }
            }
            return list;
        }

        internal static string FormatArea(double areaJob, LengthUnit unit, string areaUnit)
        {
            double mmPer = Length.MillimetresPer(unit);
            double mm2 = areaJob * mmPer * mmPer;
            if (areaUnit == "ft2") return Math.Round(mm2 / (304.8 * 304.8)).ToString("0", CultureInfo.InvariantCulture) + " ft²";
            return (mm2 / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " m²";
        }

        private static Point2 InsidePoint(Polygon2 poly)
        {
            var c = poly.Centroid;
            if (poly.Locate(c, 0) == PointLocation.Inside) return c;
            var xs = new List<double>();
            foreach (var e in poly.Edges())
            {
                if ((e.A.Y > c.Y) == (e.B.Y > c.Y)) continue;
                xs.Add(e.A.X + (c.Y - e.A.Y) * (e.B.X - e.A.X) / (e.B.Y - e.A.Y));
            }
            xs.Sort();
            double best = -1;
            var pt = c;
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > best) { best = xs[i + 1] - xs[i]; pt = new Point2((xs[i] + xs[i + 1]) / 2, c.Y); }
            return pt;
        }

        protected override RoomPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);

            var pl = new Polyline();
            for (int i = 0; i < p.Boundary.Count; i++) pl.AddVertexAt(i, ctx.Units.ToDrawing2d(p.Boundary[i]), 0, 0, 0);
            pl.Closed = true;
            pl.SetDatabaseDefaults(db);
            pl.Layer = p.Layer;
            p.BoundaryHandle = OpContext.HandleOf(Db.Append(ctx, ms, pl));
            result.Created.Add(p.BoundaryHandle);

            if (p.HatchPattern != null)
            {
                var hatch = new Hatch();
                hatch.SetDatabaseDefaults(db);
                hatch.Layer = p.HatchLayer ?? p.Layer;
                var hid = Db.Append(ctx, ms, hatch);
                hatch.PatternScale = p.HatchScale;
                hatch.SetHatchPattern(HatchPatternType.PreDefined, p.HatchPattern.ToUpperInvariant());
                var ids = new ObjectIdCollection { pl.ObjectId };
                hatch.Associative = true;
                hatch.AppendLoop(HatchLoopTypes.Outermost, ids);
                hatch.EvaluateHatch(true);
                p.HatchHandle = OpContext.HandleOf(hid);
                result.Created.Add(p.HatchHandle);
            }

            if (p.Tag)
            {
                if (p.TagBlock != null)
                {
                    var bid = DraftDb.BlockId(tr, db, p.TagBlock);
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                    if (Anno.IsAnnotative(btr)) Anno.UseScale(ctx, Anno.CurrentName(db));
                    double s = BlockSupport.UnitFactor(db, btr);
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "NAME", p.Name }, { "NUMBER", p.Number }, { "AREA", p.AreaText } };
                    var br = BlockSupport.Insert(ctx, bid, ctx.Units.ToDrawing(p.TagPoint), 0, new Scale3d(s), p.Layer, values);
                    p.TagHandle = OpContext.HandleOf(br.ObjectId);
                }
                else
                {
                    Anno.UseScale(ctx, Anno.CurrentName(db));
                    var mt = new MText();
                    mt.SetDatabaseDefaults(db);
                    mt.Location = ctx.Units.ToDrawing(p.TagPoint);
                    mt.Attachment = AttachmentPoint.MiddleCenter;
                    mt.Contents = TagText(p);
                    mt.TextStyleId = Db.SymbolId(tr, db.TextStyleTableId, p.TextStyle);
                    var cur = Anno.Current(db);
                    double dpp = cur is null ? 1 : Anno.DrawingPerPaper(cur);
                    mt.TextHeight = Anno.JobToPaper(ctx, p.TagHeight) * dpp;
                    mt.Layer = p.Layer;
                    var id = Db.Append(ctx, ms, mt);
                    mt.Annotative = AnnotativeStates.True;
                    p.TagHandle = OpContext.HandleOf(id);
                }
                result.Created.Add(p.TagHandle);
            }

            result.Outputs.Add("boundaryHandle", p.BoundaryHandle).Add("area", AcadValues.Round(p.Area)).Add("perimeter", AcadValues.Round(p.Perimeter))
                .Add("tagHandle", p.TagHandle.Length > 0 ? (JsonValue)p.TagHandle : JsonValue.Null);
            return p;
        }

        private static string TagText(RoomPlan p)
        {
            var parts = new List<string> { Escape(p.Name) };
            if (p.Number.Length > 0) parts.Add(Escape(p.Number));
            parts.Add(Escape(p.AreaText));
            return string.Join("\\P", parts);
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, RoomPlan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var id = ctx.IdOf(p.BoundaryHandle);
            if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Polyline pl)) return Mismatch(result, "The room boundary " + p.BoundaryHandle + " is missing.");
            var pts = new List<Point2>();
            for (int i = 0; i < pl.NumberOfVertices; i++) pts.Add(ctx.Units.ToJob(pl.GetPoint2dAt(i)));
            double f = ctx.Units.ToJob(1.0);
            double area = pl.Area * f * f;
            double perimeter = ctx.Units.ToJob(pl.Length);
            double coreArea = pts.Count >= 3 ? new Polygon2(pts).Area : 0;
            result.Readback.Add("boundary", new JsonArray(pts.Select(q => (JsonValue)DrawingUnits.PointJson(q))))
                .Add("closed", pl.Closed).Add("area", AcadValues.Round(area)).Add("perimeter", AcadValues.Round(perimeter)).Add("areaText", p.AreaText)
                .Add("layer", pl.Layer).Add("source", p.BoundarySource);
            double tol = ctx.LengthTolerance;
            if (!pl.Closed) mismatches.Add("the boundary polyline is not closed");
            if (pts.Count != p.Boundary.Count || pts.Where((q, i) => q.DistanceTo(p.Boundary[i]) > tol).Any()) mismatches.Add("the boundary vertices read back differently");
            if (!(area > 0)) mismatches.Add("the boundary has no area");
            if (Math.Abs(area - p.Area) > tol * Math.Max(1, p.Perimeter)) mismatches.Add("AutoCAD's area " + area + " differs from the computed " + p.Area);
            if (Math.Abs(coreArea - p.Area) > tol * Math.Max(1, p.Perimeter)) mismatches.Add("the area of the read-back vertices is " + coreArea);
            if (!string.Equals(pl.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the boundary is on layer '" + pl.Layer + "'");
            if (p.HatchHandle.Length > 0)
            {
                var hid = ctx.IdOf(p.HatchHandle);
                if (hid.IsNull || !(tr.GetObject(hid, OpenMode.ForRead) is Hatch h) || h.NumberOfLoops < 1) mismatches.Add("the room hatch is missing or has no loop");
            }
            if (p.Tag)
            {
                var tid = ctx.IdOf(p.TagHandle);
                var obj = tid.IsNull ? null : tr.GetObject(tid, OpenMode.ForRead);
                if (obj is BlockReference br)
                {
                    var a = BlockSupport.Read(tr, br);
                    foreach (var kv in new[] { ("NAME", p.Name), ("NUMBER", p.Number), ("AREA", p.AreaText) })
                        if (a.TryGetValue(kv.Item1, out var v) && v != kv.Item2) mismatches.Add("tag " + kv.Item1 + " reads back as '" + v + "'");
                    result.Readback.Add("tag", new JsonObject().Add("handle", p.TagHandle).Add("kind", "block").Add("position", DrawingUnits.PointJson(ctx.Units.ToJob(br.Position))));
                }
                else if (obj is MText mt)
                {
                    if (mt.Contents != TagText(p)) mismatches.Add("the tag text reads back as '" + mt.Contents + "'");
                    result.Readback.Add("tag", new JsonObject().Add("handle", p.TagHandle).Add("kind", "mtext").Add("annotative", Anno.IsAnnotative(mt))
                        .Add("position", DrawingUnits.PointJson(ctx.Units.ToJob(mt.Location))).Add("height", AcadValues.Round(ctx.Units.ToJob(mt.TextHeight))));
                }
                else mismatches.Add("the room tag is missing");
            }
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
