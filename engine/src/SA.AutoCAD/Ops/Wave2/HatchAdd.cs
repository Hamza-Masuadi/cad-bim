using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// hatch.add: hatches in model space from closed loops. Point loops become polyline loops (vertices, bulges, the
    /// first vertex repeated to close) or, with drawBoundary, closed polylines that are used as entity loops; handle
    /// loops are entity loops. Each boundary is an External loop and its islands Default loops, with HatchStyle Normal,
    /// so islands stay unhatched. Pattern properties are set before SetHatchPattern (which builds the pattern from them),
    /// and EvaluateHatch runs last. Associative hatches need entity loops. Readback compares Hatch.Area with the area
    /// computed from the loops (exact for arcs) and fails on a difference over 1 %.
    /// </summary>
    internal sealed class HatchAdd : OpHandler<HatchAdd.Plan>
    {
        public override string Name => "hatch.add";

        internal sealed class LoopPlan
        {
            public List<Point2> Points = new List<Point2>();
            public List<double> Bulges = new List<double>();
            public ObjectId Entity;
            public PlanLoop Shape = null!;
            public ObjectId Drawn;
            /// <summary>World Z of an entity loop, in drawing units.</summary>
            public double? Z;
        }

        internal sealed class BoundaryPlan
        {
            public LoopPlan Outer = null!;
            public List<LoopPlan> Islands = new List<LoopPlan>();
            public double NetArea => Outer.Shape.Area - Islands.Sum(i => i.Shape.Area);
        }

        internal sealed class HatchPlan
        {
            public List<BoundaryPlan> Boundaries = new List<BoundaryPlan>();
            public string Handle = "";
            /// <summary>Plane of the hatch: the Z of its entity loops, else 0 (drawing units).</summary>
            public double Elevation;
            public double Expected => Boundaries.Sum(b => b.NetArea);
        }

        internal sealed class Plan
        {
            public List<BoundaryPlan> Boundaries = new List<BoundaryPlan>();
            public List<HatchPlan> Hatches = new List<HatchPlan>();
            public string Pattern = "";
            public HatchPatternType Type = HatchPatternType.PreDefined;
            public double Scale = 1, Angle, Spacing;
            public bool Double, Associative, DrawBoundary, Separate;
            public Point2? Origin;
            public string Layer = "", BoundaryLayer = "";
            public string DrawOrder = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan
            {
                Pattern = args.GetString("pattern").Trim(),
                Scale = args.GetNumber("scale"),
                Angle = Num.Rad(args.GetAngle("angle")),
                Double = args.GetBool("double"),
                Associative = args.GetBool("associative"),
                DrawBoundary = args.GetBool("drawBoundary"),
                Separate = args.GetBool("separate"),
                Origin = args.GetPoint2OrNull("origin"),
                DrawOrder = args.GetString("drawOrder"),
            };
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "hatches");
            p.BoundaryLayer = args.Has("boundaryLayer") ? DraftDb.Layer(ctx, args, issues, "boundaryLayer", "hatch boundaries") : p.Layer;

            if (string.Equals(p.Pattern, "_USER", StringComparison.OrdinalIgnoreCase))
            {
                p.Type = HatchPatternType.UserDefined;
                p.Pattern = "_USER";
                if (!args.Has("spacing")) OpContext.Error(issues, args, "spacing", "spacing_missing", "a user-defined pattern (_USER) needs spacing");
                else p.Spacing = args.GetLength("spacing");
            }
            else
            {
                p.Pattern = p.Pattern.ToUpperInvariant();
                if (args.Has("spacing")) OpContext.Error(issues, args, "spacing", "field_not_used", "spacing applies to the user-defined pattern _USER");
                if (args.GetBool("double")) OpContext.Error(issues, args, "double", "field_not_used", "double applies to the user-defined pattern _USER");
                if (p.Pattern != "SOLID")
                {
                    string file = Db.DefaultResourceFile(ctx.Database, ".pat");
                    string? path = Db.FindSupportFile(ctx.Database, file);
                    if (path is null || !ResourceFiles.PatternExists(path, p.Pattern))
                        OpContext.Error(issues, args, "pattern", IssueCodes.PatternMissing, "hatch pattern '" + p.Pattern + "' is not in " + (path ?? file) + "; give a pattern from it, SOLID, or _USER with spacing");
                }
            }

            var used = new HashSet<ObjectId>();
            bool deferred = false;
            foreach (var b in args.GetObjectList("boundaries"))
            {
                var bp = new BoundaryPlan { Outer = LoopFrom(ctx, b, issues, p, used, ref deferred)! };
                foreach (var isl in b.GetObjectList("islands"))
                {
                    var lp = LoopFrom(ctx, isl, issues, p, used, ref deferred);
                    if (lp is null) continue;
                    if (bp.Outer?.Shape?.Polygon is Polygon2 outer && lp.Shape.Polygon is Polygon2 inner && !PlanGeometry.Contains(outer, inner, ctx.LengthTolerance))
                        OpContext.Error(issues, isl, null, "island_outside", "the island is not inside its boundary");
                    foreach (var other in bp.Islands)
                        if (other.Shape.Polygon is Polygon2 o && lp.Shape.Polygon is Polygon2 n &&
                            (o.Locate(PlanGeometry.InteriorPoint(n), 0) != PointLocation.Outside || n.Locate(PlanGeometry.InteriorPoint(o), 0) != PointLocation.Outside))
                            OpContext.Error(issues, isl, null, "islands_overlap", "two islands of the same boundary overlap or nest; give each island once, directly inside its boundary");
                    bp.Islands.Add(lp);
                }
                if (bp.Outer != null) p.Boundaries.Add(bp);
            }
            if (deferred) return p;
            for (int i = 0; i < p.Boundaries.Count; i++)
                for (int j = i + 1; j < p.Boundaries.Count; j++)
                {
                    var a = p.Boundaries[i].Outer.Shape.Polygon; var c = p.Boundaries[j].Outer.Shape.Polygon;
                    if (a != null && c != null && (a.Locate(PlanGeometry.InteriorPoint(c), 0) != PointLocation.Outside || c.Locate(PlanGeometry.InteriorPoint(a), 0) != PointLocation.Outside))
                        OpContext.Error(issues, args, "boundaries", "boundaries_overlap", "boundaries " + i + " and " + j + " overlap or nest; give an inner loop as an island of the outer one");
                }
            if (p.Separate) p.Hatches = p.Boundaries.Select(b => new HatchPlan { Boundaries = { b } }).ToList();
            else p.Hatches.Add(new HatchPlan { Boundaries = p.Boundaries.ToList() });
            double zTol = ctx.Units.ToDrawing(ctx.LengthTolerance);
            foreach (var hp in p.Hatches)
            {
                var zs = hp.Boundaries.SelectMany(b => new[] { b.Outer }.Concat(b.Islands)).Where(l => l.Z.HasValue).Select(l => l.Z!.Value).ToList();
                if (zs.Count == 0) continue;
                hp.Elevation = zs[0];
                if (zs.Any(z => Math.Abs(z - zs[0]) > zTol))
                    OpContext.Error(issues, args, "boundaries", "loops_not_coplanar", "the entity loops of one hatch lie at different elevations (" + string.Join(", ", zs.Select(z => AcadValues.Round(ctx.Units.ToJob(z), 3)).Distinct()) + "); a hatch lies in one plane, so hatch each elevation separately");
            }
            return p;
        }

        private static LoopPlan? LoopFrom(OpContext ctx, OpArgs a, List<Issue> issues, Plan p, HashSet<ObjectId> used, ref bool deferred)
        {
            var lp = new LoopPlan();
            if (a.Has("handle"))
            {
                if (ctx.IsRef(a, "handle")) { deferred = true; return null; }
                string h = a.GetRefId("handle") ?? "";
                var id = ctx.IdOf(h);
                if (id.IsNull) { OpContext.Error(issues, a, "handle", "handle_missing", "handle " + h + " is not in the drawing"); return null; }
                if (!used.Add(id)) { OpContext.Error(issues, a, "handle", "loop_duplicate", "entity " + h + " is used as a loop twice"); return null; }
                string? problem = Db.Read(ctx, tr =>
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) return "is not an entity";
                    if (e.OwnerId != Db.ModelSpace(tr, ctx.Database, OpenMode.ForRead).ObjectId) return "is not in model space";
                    var shape = PlanGeometry.ClosedLoop(ctx, e);
                    if (shape is null) return "is not a closed polyline, circle, ellipse or spline lying flat in plan";
                    // Associative loops get a reactor added, which opens the boundary for write.
                    if (p.Associative && SheetDb.IsOnLockedLayer(tr, e)) return "is on a locked layer (" + e.Layer + "); an associative hatch has to attach to it";
                    try
                    {
                        var ext = e.GeometricExtents;
                        if (Math.Abs(ext.MaxPoint.Z - ext.MinPoint.Z) > ctx.Units.ToDrawing(ctx.LengthTolerance)) return "is not flat in plan";
                        lp.Z = ext.MinPoint.Z;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { return "has no extents"; }
                    lp.Shape = shape;
                    return null;
                });
                if (problem != null) { OpContext.Error(issues, a, "handle", "loop_invalid", "entity " + h + " " + problem); return null; }
                lp.Entity = id;
                return lp;
            }
            lp.Points = a.GetPoint2List("points");
            lp.Bulges = Num.List(a, "bulges");
            if (a.Has("bulges") && lp.Bulges.Count != lp.Points.Count)
                OpContext.Error(issues, a, "bulges", "bulges_count", "a loop of " + lp.Points.Count + " points has " + lp.Points.Count + " segments; give that many bulges");
            while (lp.Bulges.Count < lp.Points.Count) lp.Bulges.Add(0);
            if (lp.Points.Count >= 2 && lp.Points[0].DistanceTo(lp.Points[lp.Points.Count - 1]) <= ctx.LengthTolerance)
                OpContext.Error(issues, a, "points", "duplicate_point", "the last point repeats the first; loops close themselves");
            if (p.Associative && !p.DrawBoundary)
                OpContext.Error(issues, a, "points", "associative_needs_entities", "an associative hatch needs entity loops: give handles, or set drawBoundary to draw polylines for point loops");
            var pts = PlanGeometry.Tessellate(lp.Points, lp.Bulges, true);
            var poly = new Polygon2(pts);
            if (!poly.IsSimple(ctx.LengthTolerance)) OpContext.Error(issues, a, "points", "loop_self_intersecting", "the loop crosses itself");
            lp.Shape = new PlanLoop(pts, Math.Abs(PlanGeometry.SignedArea(lp.Points, lp.Bulges)));
            if (lp.Shape.Area <= ctx.LengthTolerance * ctx.LengthTolerance) OpContext.Error(issues, a, "points", "loop_empty", "the loop encloses no area");
            return lp;
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
            var drawn = new JsonArray();
            if (p.DrawBoundary)
            {
                foreach (var hp in p.Hatches)
                foreach (var lp in hp.Boundaries.SelectMany(b => new[] { b.Outer }.Concat(b.Islands)).Where(l => l.Entity.IsNull))
                {
                    var pl = new Polyline();
                    for (int i = 0; i < lp.Points.Count; i++) pl.AddVertexAt(i, u.ToDrawing2d(lp.Points[i]), lp.Bulges[i], 0, 0);
                    pl.Closed = true;
                    pl.SetDatabaseDefaults(db);
                    pl.Elevation = hp.Elevation;
                    pl.Layer = p.BoundaryLayer;
                    lp.Drawn = lp.Entity = Db.Append(ctx, ms, pl);
                    result.Created.Add(OpContext.HandleOf(lp.Drawn));
                    drawn.Add(OpContext.HandleOf(lp.Drawn));
                }
            }
            var handles = new JsonArray();
            foreach (var hp in p.Hatches)
            {
                var hatch = new Hatch();
                hatch.SetDatabaseDefaults(db);
                hatch.Layer = p.Layer;
                var hid = Db.Append(ctx, ms, hatch);
                hatch.Elevation = hp.Elevation;
                hatch.HatchStyle = HatchStyle.Normal;
                hatch.PatternAngle = p.Angle;
                if (p.Type == HatchPatternType.UserDefined)
                {
                    hatch.PatternSpace = u.ToDrawing(p.Spacing);
                    hatch.PatternDouble = p.Double;
                }
                else hatch.PatternScale = p.Scale;
                if (p.Origin.HasValue) hatch.Origin = u.ToDrawing2d(p.Origin.Value);
                hatch.SetHatchPattern(p.Type, p.Pattern);
                hatch.Associative = p.Associative;
                foreach (var b in hp.Boundaries)
                {
                    Append(ctx, hatch, b.Outer, HatchLoopTypes.External);
                    foreach (var isl in b.Islands) Append(ctx, hatch, isl, HatchLoopTypes.Default);
                }
                hatch.EvaluateHatch(true);
                hp.Handle = OpContext.HandleOf(hid);
                result.Created.Add(hp.Handle);
                handles.Add(hp.Handle);
                Order(tr, ms, hid, hp, p.DrawOrder);
            }
            result.Outputs.Add("handles", handles).Add("boundaryHandles", drawn);
            return p;
        }

        private static void Append(OpContext ctx, Hatch hatch, LoopPlan lp, HatchLoopTypes type)
        {
            if (!lp.Entity.IsNull)
            {
                hatch.AppendLoop(type, new ObjectIdCollection(new[] { lp.Entity }));
                return;
            }
            var pts = new Point2dCollection();
            var bulges = new DoubleCollection();
            foreach (var (pt, b) in lp.Points.Zip(lp.Bulges, (pt, b) => (pt, b))) { pts.Add(ctx.Units.ToDrawing2d(pt)); bulges.Add(b); }
            pts.Add(ctx.Units.ToDrawing2d(lp.Points[0]));
            bulges.Add(0);
            hatch.AppendLoop(type | HatchLoopTypes.Polyline, pts, bulges);
        }

        /// <summary>behindBoundary: below the boundary entity drawn first; back: below everything in model space.</summary>
        private static void Order(Transaction tr, BlockTableRecord ms, ObjectId hid, HatchPlan hp, string mode)
        {
            if (mode == "none") return;
            var boundaryIds = hp.Boundaries.SelectMany(b => new[] { b.Outer }.Concat(b.Islands)).Select(l => l.Entity).Where(id => !id.IsNull).ToList();
            if (mode == "behindBoundary" && boundaryIds.Count == 0) return;
            var dot = (DrawOrderTable)tr.GetObject(ms.DrawOrderTableId, OpenMode.ForWrite);
            var ids = new ObjectIdCollection(new[] { hid });
            if (mode == "back") { dot.MoveToBottom(ids); return; }
            var order = dot.GetFullDrawOrder(0);
            ObjectId target = ObjectId.Null;
            int best = int.MaxValue;
            foreach (var id in boundaryIds)
            {
                int at = order.IndexOf(id);
                if (at >= 0 && at < best) { best = at; target = id; }
            }
            if (!target.IsNull) dot.MoveBelow(ids, target);
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            double f = ctx.Units.ToJob(1.0);
            var mismatches = new List<string>();
            var areas = new JsonArray();
            var list = new JsonArray();
            double total = 0;
            foreach (var hp in p.Hatches)
            {
                var h = (Hatch)tr.GetObject(ctx.IdOf(hp.Handle), OpenMode.ForRead);
                double area = double.NaN;
                try { area = h.Area * f * f; }
                catch (Autodesk.AutoCAD.Runtime.Exception e) { mismatches.Add("the area of hatch " + hp.Handle + " could not be read: " + e.Message); }
                int loops = h.NumberOfLoops;
                int wantLoops = hp.Boundaries.Sum(b => 1 + b.Islands.Count);
                list.Add(new JsonObject().Add("handle", hp.Handle).Add("pattern", h.PatternName).Add("layer", h.Layer).Add("loops", loops)
                    .Add("associative", h.Associative).Add("area", double.IsNaN(area) ? (JsonValue)JsonValue.Null : AcadValues.Round(area, 6)).Add("expectedArea", AcadValues.Round(hp.Expected, 6)));
                if (!string.Equals(h.PatternName, p.Pattern, StringComparison.OrdinalIgnoreCase)) mismatches.Add("hatch " + hp.Handle + " has pattern " + h.PatternName);
                if (!string.Equals(h.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("hatch " + hp.Handle + " is on layer " + h.Layer);
                if (loops != wantLoops) mismatches.Add("hatch " + hp.Handle + " has " + loops + " loops, expected " + wantLoops);
                if (h.Associative != p.Associative) mismatches.Add("hatch " + hp.Handle + " associative reads back " + h.Associative);
                if (!double.IsNaN(area))
                {
                    if (area <= 0) mismatches.Add("hatch " + hp.Handle + " has no area (the loops did not evaluate)");
                    else if (Math.Abs(area - hp.Expected) > 0.01 * hp.Expected) mismatches.Add("hatch " + hp.Handle + " area " + AcadValues.Round(area, 3) + " differs from the loops' " + AcadValues.Round(hp.Expected, 3) + " by more than 1 %");
                    areas.Add(AcadValues.Round(area, 6));
                    total += area;
                }
                else areas.Add(JsonValue.Null);
            }
            result.Outputs.Add("areas", areas).Add("area", AcadValues.Round(total, 6));
            result.Readback.Add("hatches", list);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
