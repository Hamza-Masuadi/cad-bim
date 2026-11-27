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
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// opening.place: inserts a door or window block in a straight double-line wall, writes its attributes, and cuts the
    /// wall: on each face the one LINE that spans the opening is shortened to the first jamb and a copy of it (same
    /// layer, properties and xdata) runs from the second jamb on; a jamb line (also a copy of a face line) closes each
    /// side across the wall.
    ///
    /// Host wall, in order: a wall.draw op of the same job ("$w" with wallId; its centrelines are rebuilt exactly as
    /// wall.draw builds them, so validation can check the fit before the wall exists); a handle of one face line (the
    /// nearest parallel line on its layer is the other face, the line's start is the wall start); or, without host, the
    /// point between the closest pair of parallel lines on one layer.
    ///
    /// Refused: an opening that runs past the wall's ends or into a junction (a face is not one continuous line across
    /// it, or another wall's line meets the face inside it), and one that overlaps another opening (planned earlier in
    /// the job, already cut, or a block placed by this op, found by its xdata).
    ///
    /// Block convention: X along the wall, the leaf swinging to +Y, the hinge at -X. Origin at the opening centre (block
    /// extents symmetric about X = 0) or at the hinge jamb (extents starting at X = 0). A block about 1 unit wide is
    /// unit-sized and scaled to the width (doors uniformly; windows in X, and in Y to the wall thickness when also 1 unit
    /// deep); other blocks get only the INSUNITS factor. Handing: hinge "end" mirrors X, swingSide "right" mirrors Y;
    /// both together are a 180 degree rotation. Mirrored attribute text is un-mirrored.
    /// Attributes: MARK (mark), WIDTH, HEIGHT, SILL (opening values in job units) when the block has those tags, then the
    /// attributes map. Xdata on the reference: CADBIM, "opening.place", kind, width (1041 distance, drawing units),
    /// opening centre (1011 world position, so it moves, rotates and scales with the block).
    /// </summary>
    internal sealed class OpeningPlace : OpHandler<OpeningPlace.OpeningPlan>
    {
        public override string Name => "opening.place";

        internal sealed class OpeningPlan
        {
            public string Kind = "door";
            public string Block = "";
            public HostWall? Wall;
            public bool WallPlanned;
            public bool WallHasHatch;
            public double Width, Centre, J1, J2;
            public double? Height, Sill;
            public bool HingeAtEnd, SwingRight, Cut;
            public string Layer = "";
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Point2 Insertion;
            public double Rotation;
            public double ScaleX = 1, ScaleY = 1;
            public string Origin = "centre";
            public string Handle = "";
            public List<KeyValuePair<string, Segment2>> Jambs = new List<KeyValuePair<string, Segment2>>();
            public List<string> CutLines = new List<string>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Plan(ctx, args, issues);
            if (p.Wall != null && p.Cut) DraftState.For(ctx).Openings.Add(new PlannedOpening { WallKey = p.Wall.Key, From = p.J1, To = p.J2, OpId = ctx.CurrentOp.Id });
        }

        private static OpeningPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new OpeningPlan
            {
                Kind = args.GetString("kind"),
                Block = args.GetString("block"),
                Width = args.GetLength("width"),
                Height = args.GetLengthOrNull("height"),
                Sill = args.GetLengthOrNull("sillHeight"),
                HingeAtEnd = args.GetString("hinge") == "end",
                SwingRight = args.GetString("swingSide") == "right",
                Cut = args.GetBool("cutWall"),
            };
            double tol = ctx.LengthTolerance;
            if (p.Kind == "window" && (args.Has("hinge") || args.Has("swingSide")))
                OpContext.Warning(issues, args, "hinge", "window_handing", "hinge and swingSide apply to doors; they are ignored for windows");
            if (p.Kind == "door" && args.Has("sillHeight")) OpContext.Warning(issues, args, "sillHeight", "door_sill", "sillHeight is recorded for windows only");
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", p.Kind + "s");

            if (!DraftDb.BlockExists(ctx, p.Block)) OpContext.Error(issues, args, "block", "block_missing", DraftDb.MissingBlock(ctx, p.Block));
            else
            {
                var given = BlockSupport.StringMap(args, "attributes");
                BlockSupport.CheckTags(ctx, args, "attributes", p.Block, given.Keys, issues);
                var tags = BlockSupport.TagsOf(ctx, p.Block) ?? new List<string>();
                string? mark = args.GetStringOrNull("mark");
                if (mark != null) { if (tags.Contains("MARK")) p.Attributes["MARK"] = mark; else OpContext.Warning(issues, args, "mark", "mark_no_attribute", "block '" + p.Block + "' has no MARK attribute; the mark is not recorded"); }
                else if (tags.Contains("MARK")) OpContext.Warning(issues, args, "mark", "mark_missing", "every " + p.Kind + " should have a unique mark");
                if (tags.Contains("WIDTH")) p.Attributes["WIDTH"] = DraftDb.FormatLength(p.Width, ctx.JobUnit);
                if (p.Height.HasValue && tags.Contains("HEIGHT")) p.Attributes["HEIGHT"] = DraftDb.FormatLength(p.Height.Value, ctx.JobUnit);
                if (p.Sill.HasValue && p.Kind == "window")
                {
                    string? sillTag = tags.Contains("SILL") ? "SILL" : tags.Contains("SILLHEIGHT") ? "SILLHEIGHT" : null;
                    if (sillTag != null) p.Attributes[sillTag] = DraftDb.FormatLength(p.Sill.Value, ctx.JobUnit);
                }
                if (p.Height.HasValue && !tags.Contains("HEIGHT") && tags.Count > 0) OpContext.Warning(issues, args, "height", "height_no_attribute", "block '" + p.Block + "' has no HEIGHT attribute; the height is not recorded");
                foreach (var kv in given) p.Attributes[kv.Key] = kv.Value;
            }

            if (!FindWall(ctx, args, p, issues)) return p;
            var w = p.Wall!;

            // Where along the wall.
            if (args.Has("offset"))
            {
                double off = args.GetLength("offset");
                p.Centre = args.GetString("measureTo") == "near_jamb" ? off + p.Width / 2 : off;
            }
            else
            {
                var pt = args.GetPoint2("point");
                double across = w.Across(pt);
                if (Math.Abs(across) > w.Thickness / 2 + tol)
                    OpContext.Error(issues, args, "point", "point_off_wall", "the point is " + AcadValues.Round(Math.Abs(across) - w.Thickness / 2, 3) + " outside the wall (" + w.Source + ")");
                p.Centre = w.Along(pt);
            }
            p.J1 = p.Centre - p.Width / 2;
            p.J2 = p.Centre + p.Width / 2;
            if (p.J1 < -tol || p.J2 > w.Length + tol)
            {
                OpContext.Error(issues, args, args.Has("offset") ? "offset" : "point", "opening_outside_wall", "the opening runs from " + AcadValues.Round(p.J1, 3) + " to " + AcadValues.Round(p.J2, 3) +
                    " along a wall " + AcadValues.Round(w.Length, 3) + " long (" + w.Source + ")");
                return p;
            }

            // Overlap with openings planned earlier in this job.
            if (ctx.IsValidating)
                foreach (var o in DraftState.For(ctx).Openings)
                    if (o.WallKey == w.Key && o.From < p.J2 - tol && o.To > p.J1 + tol)
                        OpContext.Error(issues, args, null, "opening_overlap", "the opening overlaps the one op '" + o.OpId + "' places in the same wall (" + AcadValues.Round(o.From, 3) + " to " + AcadValues.Round(o.To, 3) + ")");

            if (p.WallPlanned && ctx.IsValidating)
            {
                foreach (var side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                {
                    bool covered = w.PlannedSegments!.Any(s =>
                        Math.Abs((s.B - s.A).Normalised().Cross(w.Dir)) <= 1e-6 && Math.Abs(w.Across(s.A) - side) <= tol && Math.Abs(w.Across(s.B) - side) <= tol &&
                        Math.Min(w.Along(s.A), w.Along(s.B)) < p.J1 - tol && Math.Max(w.Along(s.A), w.Along(s.B)) > p.J2 + tol);
                    if (!covered)
                        OpContext.Error(issues, args, args.Has("offset") ? "offset" : "point", "opening_not_in_wall", "the opening from " + AcadValues.Round(p.J1, 3) + " to " + AcadValues.Round(p.J2, 3) +
                            " does not fit between the wall's junctions or ends on its " + (side > 0 ? "left" : "right") + " face (" + w.Source + ")");
                }
            }
            else
            {
                Db.Read(ctx, tr =>
                {
                    CheckDrawnWall(ctx, tr, args, p, issues);
                    return 0;
                });
            }

            // Block geometry: origin, unit-sized scaling, handing.
            if (!(ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Block, p.Block) && Db.Read(ctx, tr => DraftDb.BlockId(tr, ctx.Database, p.Block).IsNull)))
                Db.Read(ctx, tr =>
                {
                    var id = DraftDb.BlockId(tr, ctx.Database, p.Block);
                    if (id.IsNull) return 0;
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    Geometry(ctx, args, p, btr, BlockSupport.Extents(tr, btr), issues);
                    return 0;
                });
            return p;
        }

        private static bool FindWall(OpContext ctx, OpArgs args, OpeningPlan p, List<Issue> issues)
        {
            string? wallId = args.GetStringOrNull("wallId");
            if (args.Has("host"))
            {
                string? opId = WallGeometry.RefOpId(ctx, "host");
                var op = opId != null ? ctx.Job.FindOp(opId) : null;
                if (op != null && op.Op == "wall.draw")
                {
                    var walls = WallGeometry.FromWallOp(ctx, op, out string reason);
                    if (walls is null) { OpContext.Warning(issues, args, "host", IssueCodes.RefDeferred, reason); return false; }
                    if (walls.Count == 0) { OpContext.Error(issues, args, "host", "host_invalid", "wall.draw op '" + op.Id + "' draws no walls"); return false; }
                    HostWall? w = null;
                    if (wallId != null)
                    {
                        w = walls.FirstOrDefault(x => x.Key == op.Id + ":" + wallId);
                        if (w is null) { OpContext.Error(issues, args, "wallId", "wall_unknown", "wall.draw op '" + op.Id + "' has no wall '" + wallId + "'; its walls: " + WallGeometry.Ids(walls)); return false; }
                    }
                    else if (walls.Count == 1) w = walls[0];
                    else if (args.Has("point"))
                    {
                        var pt = args.GetPoint2("point");
                        w = walls.OrderBy(x => Math.Abs(x.Across(pt)) + Math.Max(0, -x.Along(pt)) + Math.Max(0, x.Along(pt) - x.Length)).First();
                    }
                    else { OpContext.Error(issues, args, "wallId", "wall_ambiguous", "wall.draw op '" + op.Id + "' draws several walls; give wallId (" + WallGeometry.Ids(walls) + ")"); return false; }
                    p.Wall = w;
                    p.WallPlanned = true;
                    p.WallHasHatch = op.Args["hatch"] != null;
                    return true;
                }
                if (ctx.IsRef(args, "host")) { OpContext.Warning(issues, args, "host", IssueCodes.RefDeferred, "the host is known only at execution"); return false; }
                if (wallId != null) OpContext.Warning(issues, args, "wallId", "wallid_ignored", "wallId applies to a wall.draw result of the same job; the host here is a line handle");
                string? handle = args.GetRefId("host");
                var id = handle != null ? ctx.IdOf(handle) : ObjectId.Null;
                if (id.IsNull) { OpContext.Error(issues, args, "host", "host_missing", "no object with handle " + handle + " in the drawing"); return false; }
                string why = "";
                p.Wall = Db.Read(ctx, tr => WallGeometry.FromLine(ctx, tr, id, out why));
                if (p.Wall is null) { OpContext.Error(issues, args, "host", "host_invalid", why); return false; }
                return true;
            }
            if (!args.Has("point")) { OpContext.Error(issues, args, "point", "position_missing", "give a point on the wall, or host with offset"); return false; }
            var at = args.GetPoint2("point");
            string reasonPt = "";
            p.Wall = Db.Read(ctx, tr => WallGeometry.FromPoint(ctx, tr, at, out reasonPt));
            if (p.Wall is null)
            {
                // A wall drawn earlier in this job is not in the drawing while validating.
                if (ctx.IsValidating && ctx.Job.Ops.Any(o => o.Index < ctx.CurrentOp.Index && o.Op == "wall.draw"))
                    OpContext.Warning(issues, args, "point", "host_deferred", "no wall is drawn at the point yet; it is looked for at execution (give host: \"$<wall op>\" to check now)");
                else OpContext.Error(issues, args, "point", "host_missing", reasonPt);
                return false;
            }
            return true;
        }

        /// <summary>Fit checks against the lines in the drawing: one continuous line per face across the opening, no wall meeting it inside, no opening block overlapping.</summary>
        private static void CheckDrawnWall(OpContext ctx, Transaction tr, OpArgs args, OpeningPlan p, List<Issue> issues)
        {
            var w = p.Wall!;
            double tol = ctx.LengthTolerance;
            string field = args.Has("offset") ? "offset" : "point";
            var faces = WallGeometry.FaceLines(ctx, tr, w);
            if (p.Cut)
                foreach (var side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                {
                    var onSide = faces.Where(f => Math.Abs(f.Side - side) <= tol).ToList();
                    if (!onSide.Any(f => f.Min < p.J1 - tol && f.Max > p.J2 + tol))
                    {
                        var touching = onSide.Where(f => f.Max > p.J1 && f.Min < p.J2).ToList();
                        OpContext.Error(issues, args, field, "opening_not_in_wall", "on the " + (side > 0 ? "left" : "right") + " face no single line on layer '" + w.Layer + "' spans " +
                            AcadValues.Round(p.J1, 3) + " to " + AcadValues.Round(p.J2, 3) + (touching.Count == 0 ? " (the face has a gap there: another opening or the wall's end)" :
                            " (the face is split at " + string.Join(", ", touching.Select(f => AcadValues.Round(f.Min, 3) + "-" + AcadValues.Round(f.Max, 3))) + ": a junction or another opening)") + "; " + w.Source);
                    }
                }
            var crossing = WallGeometry.CrossingLines(ctx, tr, w, p.J1, p.J2);
            if (crossing.Count > 0)
                OpContext.Error(issues, args, field, "opening_at_junction", "lines on the wall layer meet the wall face inside the opening (a wall joins there): " + string.Join(", ", crossing.Take(5).Select(c => OpContext.HandleOf(c.Id))));
            // Blocks placed by this op in the same wall.
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.IsErased || !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference)))) continue;
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var xd = br.GetXDataForApplication(Db.RegAppName);
                if (xd is null) continue;
                var vals = xd.AsArray();
                if (vals.Length < 4 || !(vals[1].Value is string tagText) || tagText != "opening.place") continue;
                double width = ctx.Units.ToJob(Convert.ToDouble(vals[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                // Current records hold the centre as a world position (1011, moved with the block); older ones as two reals.
                var centre = vals.Length > 4 && vals[4].Value is Point3d wc ? ctx.Units.ToJob(wc)
                    : vals.Length > 5 ? ctx.Units.ToJob(new Point3d(Convert.ToDouble(vals[4].Value, inv), Convert.ToDouble(vals[5].Value, inv), 0))
                    : ctx.Units.ToJob(br.Position);
                if (Math.Abs(w.Across(centre)) > w.Thickness / 2 + tol) continue;
                double s = w.Along(centre);
                if (s - width / 2 < p.J2 - tol && s + width / 2 > p.J1 + tol)
                    OpContext.Error(issues, args, field, "opening_overlap", "the opening overlaps " + OpContext.HandleOf(id) + " (" + BlockSupport.EffectiveName(tr, br) + ") already placed in this wall");
            }
        }

        private static void Geometry(OpContext ctx, OpArgs args, OpeningPlan p, BlockTableRecord btr, BlockBox? box, List<Issue> issues)
        {
            var w = p.Wall!;
            double unit = BlockSupport.UnitFactor(ctx.Database, btr);
            double widthDrawing = ctx.Units.ToDrawing(p.Width);
            double sx = unit, sy = unit;
            if (box is null) OpContext.Warning(issues, args, "block", "block_empty", "block '" + p.Block + "' has no geometry; it is inserted at the opening centre unscaled");
            else
            {
                var b = box.Value;
                bool unitSized = Math.Abs(b.Width - 1) <= 0.02;
                if (Math.Abs(b.MinX + b.MaxX) <= 0.1 * b.Width) p.Origin = "centre";
                else if (Math.Abs(b.MinX) <= 0.02 * b.Width) p.Origin = "jamb";
                else
                {
                    p.Origin = "centre";
                    OpContext.Warning(issues, args, "block", "block_origin", "block '" + p.Block + "' has its base point neither at the opening centre nor at a jamb (X from " +
                        AcadValues.Round(b.MinX, 3) + " to " + AcadValues.Round(b.MaxX, 3) + "); it is placed as if centred");
                }
                if (unitSized)
                {
                    sx = widthDrawing / b.Width;
                    if (p.Kind == "door") sy = sx;
                    else sy = Math.Abs(b.Height - 1) <= 0.02 ? ctx.Units.ToDrawing(w.Thickness) / b.Height : unit;
                }
                else if (Math.Abs(b.Width * unit - widthDrawing) > 0.02 * widthDrawing)
                    OpContext.Warning(issues, args, "width", "block_width", "block '" + p.Block + "' is " + AcadValues.Round(ctx.Units.ToJob(b.Width * unit), 3) + " wide and not unit-sized; it is inserted at its own size in a " +
                        AcadValues.Round(p.Width, 3) + " opening. Use a block of the right size, a unit-sized block or a dynamic block.");
            }
            if (btr.IsDynamicBlock) OpContext.Warning(issues, args, "block", "dynamic_opening", "block '" + p.Block + "' is dynamic; handing uses mirroring, set its own width or flip parameters with block.dynprops.set");
            double angle = Math.Atan2(w.Dir.Y, w.Dir.X);
            bool mx = p.Kind == "door" && p.HingeAtEnd, my = p.Kind == "door" && p.SwingRight;
            if (mx && my) { angle += Math.PI; mx = my = false; }
            p.Rotation = angle;
            p.ScaleX = mx ? -sx : sx;
            p.ScaleY = my ? -sy : sy;
            double insertAt = p.Origin == "jamb" ? (p.Kind == "door" && p.HingeAtEnd ? p.J2 : p.J1) : p.Centre;
            p.Insertion = w.At(insertAt);
        }

        protected override OpeningPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p.Wall is null) throw new InvalidOperationException("The host wall could not be found.");
            var w = p.Wall;
            var tr = ctx.Transaction;
            var db = ctx.Database;
            double tol = ctx.LengthTolerance;

            if (p.Cut)
            {
                var faces = WallGeometry.FaceLines(ctx, tr, w);
                FaceLine? template = null;
                foreach (var side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                {
                    var f = faces.First(x => Math.Abs(x.Side - side) <= tol && x.Min < p.J1 - tol && x.Max > p.J2 + tol);
                    var line = (Line)tr.GetObject(f.Id, OpenMode.ForWrite);
                    Point3d At(double s) => ctx.Units.ToDrawing(f.A + (f.B - f.A) * ((s - f.SA) / (f.SB - f.SA)));
                    var end = line.EndPoint;
                    var copy = (Line)line.Clone();
                    if (f.SA < f.SB) { line.EndPoint = At(p.J1); copy.StartPoint = At(p.J2); copy.EndPoint = end; }
                    else { line.EndPoint = At(p.J2); copy.StartPoint = At(p.J1); copy.EndPoint = end; }
                    var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
                    var cid = Db.Append(ctx, ms, copy);
                    result.Modified.Add(OpContext.HandleOf(f.Id));
                    result.Created.Add(OpContext.HandleOf(cid));
                    p.CutLines.Add(OpContext.HandleOf(f.Id));
                    p.CutLines.Add(OpContext.HandleOf(cid));
                    if (template is null) template = f;
                }
                var tline = (Line)tr.GetObject(template!.Id, OpenMode.ForRead);
                foreach (var s in new[] { p.J1, p.J2 })
                {
                    var jamb = (Line)tline.Clone();
                    var seg = new Segment2(w.At(s, w.Thickness / 2), w.At(s, -w.Thickness / 2));
                    jamb.StartPoint = ctx.Units.ToDrawing(seg.A);
                    jamb.EndPoint = ctx.Units.ToDrawing(seg.B);
                    var jid = Db.Append(ctx, Db.ModelSpace(tr, db, OpenMode.ForWrite), jamb);
                    string h = OpContext.HandleOf(jid);
                    result.Created.Add(h);
                    p.Jambs.Add(new KeyValuePair<string, Segment2>(h, seg));
                }
                if (p.WallHasHatch) ctx.Warn("The host wall is hatched; the hatch is not cut by the opening. Re-hatch the wall or remove the hatch in the opening.");
            }

            var bid = DraftDb.BlockId(tr, db, p.Block);
            if (bid.IsNull) throw new InvalidOperationException("Block '" + p.Block + "' is missing.");
            var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
            if (Anno.IsAnnotative(btr)) Anno.UseScale(ctx, Anno.CurrentName(db));
            var br = BlockSupport.Insert(ctx, bid, ctx.Units.ToDrawing(p.Insertion), p.Rotation, new Scale3d(p.ScaleX, p.ScaleY, 1), p.Layer, p.Attributes);
            Db.EnsureRegApp(ctx, Db.RegAppName);
            var c = ctx.Units.ToDrawing(w.At(p.Centre));
            br.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, Db.RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "opening.place"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, p.Kind),
                new TypedValue((int)DxfCode.ExtendedDataDist, ctx.Units.ToDrawing(p.Width)),
                new TypedValue((int)DxfCode.ExtendedDataWorldXCoordinate, new Point3d(c.X, c.Y, 0)));
            p.Handle = OpContext.HandleOf(br.ObjectId);
            result.Created.Add(p.Handle);
            result.Outputs.Add("block", p.Handle).Add("centre", DrawingUnits.PointJson(w.At(p.Centre)))
                .Add("jambs", new JsonArray(DrawingUnits.PointJson(w.At(p.J1)), DrawingUnits.PointJson(w.At(p.J2))));
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, OpeningPlan p)
        {
            var tr = ctx.Transaction;
            var w = p.Wall!;
            double tol = ctx.LengthTolerance;
            var mismatches = new List<string>();
            var id = ctx.IdOf(p.Handle);
            if (id.IsNull) return Mismatch(result, "The " + p.Kind + " block " + p.Handle + " is not in the drawing after the op.");
            var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
            var pos = ctx.Units.ToJob(br.Position);
            var attrs = BlockSupport.Read(tr, br);
            var attrJson = new JsonObject();
            foreach (var kv in attrs) attrJson.Set(kv.Key, kv.Value);
            if (pos.DistanceTo(p.Insertion) > tol) mismatches.Add("the block is at " + pos + ", expected " + p.Insertion);
            if (!SA.Core.Units.Tolerance.AngleEqual(br.Rotation * 180 / Math.PI, p.Rotation * 180 / Math.PI, 1e-6)) mismatches.Add("the block rotation reads back as " + br.Rotation * 180 / Math.PI);
            if (Math.Abs(br.ScaleFactors.X - p.ScaleX) > 1e-9 * Math.Max(1, Math.Abs(p.ScaleX)) || Math.Abs(br.ScaleFactors.Y - p.ScaleY) > 1e-9 * Math.Max(1, Math.Abs(p.ScaleY)))
                mismatches.Add("the block scale reads back as " + br.ScaleFactors.X + " x " + br.ScaleFactors.Y + ", expected " + p.ScaleX + " x " + p.ScaleY);
            if (!string.Equals(br.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the block is on layer '" + br.Layer + "'");
            foreach (var kv in p.Attributes)
                if (!attrs.TryGetValue(kv.Key.ToUpperInvariant(), out var v) || v != kv.Value) mismatches.Add("attribute " + kv.Key + " reads back as '" + (v ?? "(missing)") + "'");

            int faceGaps = 0;
            if (p.Cut)
            {
                var faces = WallGeometry.FaceLines(ctx, tr, w);
                foreach (var side in new[] { w.Thickness / 2, -w.Thickness / 2 })
                {
                    var onSide = faces.Where(f => Math.Abs(f.Side - side) <= tol).ToList();
                    string label = (side > 0 ? "left" : "right") + " face";
                    var inside = onSide.Where(f => f.Max > p.J1 + tol && f.Min < p.J2 - tol).ToList();
                    if (inside.Count > 0) mismatches.Add("the " + label + " still has a line across the opening: " + string.Join(", ", inside.Select(f => OpContext.HandleOf(f.Id))));
                    if (!onSide.Any(f => Math.Abs(f.Max - p.J1) <= tol)) mismatches.Add("no line on the " + label + " ends at the first jamb");
                    if (!onSide.Any(f => Math.Abs(f.Min - p.J2) <= tol)) mismatches.Add("no line on the " + label + " starts at the second jamb");
                    if (inside.Count == 0) faceGaps++;
                }
                foreach (var j in p.Jambs)
                {
                    var jid = ctx.IdOf(j.Key);
                    if (jid.IsNull || !(tr.GetObject(jid, OpenMode.ForRead) is Line l)) { mismatches.Add("jamb line " + j.Key + " is missing"); continue; }
                    var a = ctx.Units.ToJob(l.StartPoint);
                    var b = ctx.Units.ToJob(l.EndPoint);
                    if (a.DistanceTo(j.Value.A) > tol || b.DistanceTo(j.Value.B) > tol) mismatches.Add("jamb line " + j.Key + " reads back as " + a + " -> " + b);
                }
            }
            result.Readback.Add("block", p.Handle).Add("blockName", BlockSupport.EffectiveName(tr, br)).Add("position", DrawingUnits.PointJson(pos))
                .Add("rotation", AcadValues.Round(br.Rotation * 180 / Math.PI)).Add("scale", new JsonArray(AcadValues.Round(br.ScaleFactors.X), AcadValues.Round(br.ScaleFactors.Y)))
                .Add("blockOrigin", p.Origin).Add("layer", br.Layer).Add("attributes", attrJson)
                .Add("wall", new JsonObject().Add("source", w.Source).Add("layer", w.Layer).Add("thickness", AcadValues.Round(w.Thickness)).Add("length", AcadValues.Round(w.Length)))
                .Add("along", new JsonArray(AcadValues.Round(p.J1), AcadValues.Round(p.J2))).Add("cut", p.Cut).Add("facesOpen", faceGaps)
                .Add("cutLines", JsonArray.FromStrings(p.CutLines)).Add("jambLines", JsonArray.FromStrings(p.Jambs.Select(j => j.Key)));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
