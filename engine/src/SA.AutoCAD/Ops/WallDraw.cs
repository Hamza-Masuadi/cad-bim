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
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// wall.draw: centrelines (single walls and chains) go through Core's WallNetwork.Build in job units; the cleaned
    /// outline segments become LINE entities in model space on each wall's layer, optional hatches fill the wall pieces.
    ///
    /// Justification left/right means the given line is that face, looking from start to end: the centreline is offset by
    /// half the thickness towards the wall body. In a chain, consecutive offset lines are intersected so the offset chain
    /// stays connected (Core's ChainOffset.ToCentreline). Every network issue (duplicate, overlap, zero length, bad thickness, folded junction) fails
    /// validation: the network would otherwise skip the offending walls silently.
    ///
    /// wallType, when given, is stored as xdata on every line: app CADBIM, (1000 "wall.draw") (1000 wallId)
    /// (1000 wallType).
    /// </summary>
    internal sealed class WallDraw : OpHandler<WallDraw.WallPlan>
    {
        public override string Name => "wall.draw";

        internal sealed class WallSpec
        {
            public string Id = "";
            public string Path = "";
            public Point2 Start, End;
            public double Thickness;
            public string Layer = "";
            public string? WallType;
        }

        internal sealed class WallPlan
        {
            public List<WallSpec> Specs = new List<WallSpec>();
            public WallNetworkResult Network = null!;
            public bool CapOpenEnds;
            public string? HatchPattern;
            public double HatchScale = 1, HatchAngle;
            public string? HatchLayer;
            /// <summary>Created line handle to the segment it should hold (job units) and its layer.</summary>
            public List<KeyValuePair<string, KeyValuePair<Segment2, string>>> Lines = new List<KeyValuePair<string, KeyValuePair<Segment2, string>>>();
            public List<KeyValuePair<string, string>> Hatches = new List<KeyValuePair<string, string>>();
            public Dictionary<string, List<string>> HandlesByWall = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static WallPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plan = new WallPlan { CapOpenEnds = args.GetBool("capOpenEnds") };
            double? defaultThickness = args.GetLengthOrNull("thickness");
            string defaultLayer = args.GetStringOrNull("layer") ?? Db.CurrentLayerName(ctx);
            string justification = args.GetString("justification");
            var layersToCheck = new List<KeyValuePair<string, string>>();
            if (args.Has("layer")) layersToCheck.Add(new KeyValuePair<string, string>(JsonPath.Key(args.Path, "layer"), defaultLayer));

            int n = 0;
            foreach (var w in args.GetObjectList("walls"))
            {
                n++;
                var s = new WallSpec { Id = w.GetStringOrNull("id") ?? "w" + n, Path = w.Path, WallType = w.GetStringOrNull("wallType") };
                if (!Thickness(w, defaultThickness, issues, out s.Thickness)) continue;
                s.Layer = w.GetStringOrNull("layer") ?? defaultLayer;
                if (w.Has("layer")) layersToCheck.Add(new KeyValuePair<string, string>(JsonPath.Key(w.Path, "layer"), s.Layer));
                var line = Centreline(new List<Point2> { w.GetPoint2("start"), w.GetPoint2("end") }, false, s.Thickness, justification);
                s.Start = line[0];
                s.End = line[1];
                plan.Specs.Add(s);
            }

            int c = 0;
            foreach (var ch in args.GetObjectList("chains"))
            {
                c++;
                string prefix = ch.GetStringOrNull("idPrefix") ?? "c" + c + "_";
                if (!Thickness(ch, defaultThickness, issues, out double t)) continue;
                string layer = ch.GetStringOrNull("layer") ?? defaultLayer;
                if (ch.Has("layer")) layersToCheck.Add(new KeyValuePair<string, string>(JsonPath.Key(ch.Path, "layer"), layer));
                var pts = ch.GetPoint2List("points");
                bool closed = ch.GetBool("closed");
                if (closed && pts.Count < 3) { OpContext.Error(issues, ch, "points", IssueCodes.TooFew, "a closed chain needs at least three points"); continue; }
                var centre = Centreline(pts, closed, t, justification);
                int segs = closed ? centre.Count : centre.Count - 1;
                for (int i = 0; i < segs; i++)
                {
                    plan.Specs.Add(new WallSpec
                    {
                        Id = prefix + (i + 1), Path = ch.Path, Start = centre[i], End = centre[(i + 1) % centre.Count],
                        Thickness = t, Layer = layer, WallType = ch.GetStringOrNull("wallType"),
                    });
                }
            }

            foreach (var kv in layersToCheck)
                if (!Db.LayerExists(ctx, kv.Value))
                    issues.Add(new Issue(kv.Key, IssueCodes.LayerMissing, "layer '" + kv.Value + "' is not in the drawing; add it with layer.ensure first"));

            var hatch = args.GetObject("hatch");
            if (hatch != null)
            {
                plan.HatchPattern = hatch.GetString("pattern");
                plan.HatchScale = hatch.GetNumber("scale");
                plan.HatchAngle = hatch.GetAngle("angle");
                plan.HatchLayer = hatch.GetStringOrNull("layer");
                if (!string.Equals(plan.HatchPattern, "SOLID", StringComparison.OrdinalIgnoreCase))
                {
                    string file = Db.DefaultResourceFile(ctx.Database, ".pat");
                    string? path = Db.FindSupportFile(ctx.Database, file);
                    if (path is null || !ResourceFiles.PatternExists(path, plan.HatchPattern))
                        OpContext.Error(issues, hatch, "pattern", IssueCodes.PatternMissing, "hatch pattern '" + plan.HatchPattern + "' is not in " + (path ?? file));
                }
                if (plan.HatchLayer != null && !Db.LayerExists(ctx, plan.HatchLayer))
                    OpContext.Error(issues, hatch, "layer", IssueCodes.LayerMissing, "layer '" + plan.HatchLayer + "' is not in the drawing; add it with layer.ensure first");
            }

            var options = new WallNetworkOptions
            {
                Tolerance = args.GetLengthOrNull("tolerance") ?? Length.Convert(1, LengthUnit.Millimetre, ctx.JobUnit),
                CornerStyle = args.GetString("cornerStyle") == "butt" ? CornerStyle.Butt : CornerStyle.Mitre,
            };
            var inputs = plan.Specs.Select(s => new WallInput(s.Id, s.Start, s.End, s.Thickness)).ToList();
            plan.Network = WallNetwork.Build(inputs, options);
            foreach (var ni in plan.Network.Issues)
            {
                var spec = plan.Specs.FirstOrDefault(s => ni.WallIds.Contains(s.Id));
                issues.Add(new Issue(spec?.Path ?? args.Path, IssueCodes.WallPrefix + ni.Code, ni.Message));
            }
            return plan;
        }

        private static bool Thickness(OpArgs item, double? fallback, List<Issue> issues, out double thickness)
        {
            var t = item.GetLengthOrNull("thickness") ?? fallback;
            thickness = t ?? 0;
            if (t.HasValue && t.Value > 0) return true;
            OpContext.Error(issues, item, "thickness", IssueCodes.ThicknessMissing, "no thickness: give one here or in the op's thickness");
            return false;
        }

        private static List<Point2> Centreline(List<Point2> pts, bool closed, double thickness, string justification)
        {
            ChainOffset.TryParseJustification(justification, out var j);
            return ChainOffset.ToCentreline(pts, closed, thickness, j);
        }

        private static bool IsCap(Segment2 s, WallOutline w, IEnumerable<OpenEnd> ends, double tol)
        {
            foreach (var e in ends)
            {
                if (e.WallId != w.Id) continue;
                if (s.Midpoint.DistanceTo(e.Point) <= 2 * tol && Math.Abs(s.Length - w.Thickness) <= 2 * tol) return true;
            }
            return false;
        }

        protected override WallPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plan = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            double tol = args.GetLengthOrNull("tolerance") ?? Length.Convert(1, LengthUnit.Millimetre, ctx.JobUnit);
            if (plan.Specs.Any(s => s.WallType != null)) Db.EnsureRegApp(ctx, Db.RegAppName);

            foreach (var w in plan.Network.Walls)
            {
                var spec = plan.Specs.First(s => s.Id == w.Id);
                var handles = new List<string>();
                foreach (var seg in w.Segments)
                {
                    if (!plan.CapOpenEnds && IsCap(seg, w, plan.Network.OpenEnds, tol)) continue;
                    var line = new Line(ctx.Units.ToDrawing(seg.A), ctx.Units.ToDrawing(seg.B));
                    line.SetDatabaseDefaults(db);
                    line.Layer = spec.Layer;
                    var id = Db.Append(ctx, ms, line);
                    if (spec.WallType != null)
                    {
                        line.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, Db.RegAppName),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "wall.draw"),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, w.Id),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, spec.WallType));
                    }
                    string h = OpContext.HandleOf(id);
                    handles.Add(h);
                    result.Created.Add(h);
                    plan.Lines.Add(new KeyValuePair<string, KeyValuePair<Segment2, string>>(h, new KeyValuePair<Segment2, string>(seg, spec.Layer)));
                }
                if (plan.HatchPattern != null)
                {
                    foreach (var piece in w.Pieces)
                    {
                        var hatch = new Hatch();
                        hatch.SetDatabaseDefaults(db);
                        hatch.Layer = plan.HatchLayer ?? spec.Layer;
                        var id = Db.Append(ctx, ms, hatch);
                        hatch.PatternScale = plan.HatchScale;
                        hatch.PatternAngle = plan.HatchAngle * Math.PI / 180.0;
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, plan.HatchPattern.ToUpperInvariant());
                        hatch.Associative = false;
                        var verts = new Point2dCollection();
                        var bulges = new DoubleCollection();
                        foreach (var v in piece.Vertices) { verts.Add(ctx.Units.ToDrawing2d(v)); bulges.Add(0); }
                        verts.Add(ctx.Units.ToDrawing2d(piece.Vertices[0]));
                        bulges.Add(0);
                        hatch.AppendLoop(HatchLoopTypes.Polyline | HatchLoopTypes.External, verts, bulges);
                        hatch.EvaluateHatch(true);
                        string h = OpContext.HandleOf(id);
                        handles.Add(h);
                        result.Created.Add(h);
                        plan.Hatches.Add(new KeyValuePair<string, string>(h, hatch.Layer));
                    }
                }
                plan.HandlesByWall[w.Id] = handles;
            }

            var walls = new JsonArray();
            foreach (var w in plan.Network.Walls)
                walls.Add(new JsonObject()
                    .Add("id", w.Id)
                    .Add("handles", JsonArray.FromStrings(plan.HandlesByWall[w.Id]))
                    .Add("length", AcadValues.Round(w.Centreline.Length))
                    .Add("thickness", AcadValues.Round(w.Thickness))
                    .Add("layer", plan.Specs.First(s => s.Id == w.Id).Layer));
            var junctions = new JsonArray();
            foreach (var j in plan.Network.Junctions)
                junctions.Add(new JsonObject().Add("point", DrawingUnits.PointJson(j.Point)).Add("kind", j.Kind.ToString().ToLowerInvariant())
                    .Add("walls", JsonArray.FromStrings(j.WallIds)));
            var openEnds = new JsonArray();
            foreach (var e in plan.Network.OpenEnds)
                openEnds.Add(new JsonObject().Add("wall", e.WallId).Add("point", DrawingUnits.PointJson(e.Point)).Add("atStart", e.AtStart));
            var loops = new JsonArray();
            foreach (var l in plan.Network.Loops)
                loops.Add(new JsonArray(l.Points.Select(p => (JsonValue)DrawingUnits.PointJson(p))));
            result.Outputs.Add("walls", walls).Add("junctions", junctions).Add("openEnds", openEnds).Add("loops", loops);
            return plan;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, WallPlan plan)
        {
            var tr = ctx.Transaction;
            double tol = ctx.LengthTolerance;
            bool ok = true;
            double faceLength = 0;
            foreach (var kv in plan.Lines)
            {
                var id = ctx.IdOf(kv.Key);
                if (id.IsNull) { ok = Mismatch(result, "Line " + kv.Key + " is not in the drawing after the op."); continue; }
                if (!(tr.GetObject(id, OpenMode.ForRead) is Line line)) { ok = Mismatch(result, "Object " + kv.Key + " is not a line."); continue; }
                var a = ctx.Units.ToJob(line.StartPoint);
                var b = ctx.Units.ToJob(line.EndPoint);
                var want = kv.Value.Key;
                if (a.DistanceTo(want.A) > tol || b.DistanceTo(want.B) > tol)
                    ok = Mismatch(result, "Line " + kv.Key + " reads back as " + a + " -> " + b + ", expected " + want + ".");
                if (!string.Equals(line.Layer, kv.Value.Value, StringComparison.OrdinalIgnoreCase))
                    ok = Mismatch(result, "Line " + kv.Key + " is on layer '" + line.Layer + "', expected '" + kv.Value.Value + "'.");
                if (Math.Abs(line.StartPoint.Z) > 1e-9 || Math.Abs(line.EndPoint.Z) > 1e-9)
                    ok = Mismatch(result, "Line " + kv.Key + " is not at Z = 0.");
                faceLength += a.DistanceTo(b);
            }
            foreach (var kv in plan.Hatches)
            {
                var id = ctx.IdOf(kv.Key);
                if (id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is Hatch h)) { ok = Mismatch(result, "Hatch " + kv.Key + " is not in the drawing after the op."); continue; }
                if (h.NumberOfLoops < 1) ok = Mismatch(result, "Hatch " + kv.Key + " has no boundary loop.");
                if (!string.Equals(h.PatternName, plan.HatchPattern, StringComparison.OrdinalIgnoreCase))
                    ok = Mismatch(result, "Hatch " + kv.Key + " has pattern '" + h.PatternName + "'.");
            }
            var counts = new JsonObject();
            foreach (JunctionKind k in Enum.GetValues(typeof(JunctionKind)))
                counts.Add(k.ToString().ToLowerInvariant(), plan.Network.Junctions.Count(j => j.Kind == k));
            result.Readback
                .Add("wallCount", plan.Network.Walls.Count)
                .Add("lineCount", plan.Lines.Count)
                .Add("hatchCount", plan.Hatches.Count)
                .Add("centrelineLength", AcadValues.Round(plan.Network.Walls.Sum(w => w.Centreline.Length)))
                .Add("faceLength", AcadValues.Round(faceLength))
                .Add("junctions", counts)
                .Add("openEnds", plan.Network.OpenEnds.Count)
                .Add("loops", plan.Network.Loops.Count);
            if (plan.Network.Walls.Count != plan.Specs.Count)
                ok = Mismatch(result, plan.Specs.Count + " walls were requested but the network produced " + plan.Network.Walls.Count + ".");
            return ok;
        }
    }
}
