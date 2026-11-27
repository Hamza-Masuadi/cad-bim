using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// draw.entities: lines, lightweight polylines with bulges, arcs, circles and points in model space, in world XY at
    /// elevation 0. Colour, linetype and lineweight are left ByLayer (the drawing defaults); only the layer is set. A
    /// three-point arc is converted to centre, radius and counter-clockwise angles (start and end swap when the three
    /// points turn clockwise, so the arc still passes through the middle point).
    /// </summary>
    internal sealed class DrawEntities : OpHandler<DrawEntities.Plan>
    {
        public override string Name => "draw.entities";

        internal sealed class Item
        {
            public string? Id;
            public string Kind = "";
            public string Layer = "";
            public List<Point2> Points = new List<Point2>();
            public List<double> Bulges = new List<double>();
            public bool Closed;
            public Point2 Center;
            public double Radius;
            /// <summary>Radians, counter-clockwise.</summary>
            public double Start, End;
            public string Handle = "";
        }

        internal sealed class Plan
        {
            public List<Item> Items = new List<Item>();
        }

        private static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>
        {
            { "line", new[] { "points" } },
            { "polyline", new[] { "points", "bulges", "closed" } },
            { "arc", new[] { "points", "center", "radius", "startAngle", "endAngle" } },
            { "circle", new[] { "center", "radius" } },
            { "point", new[] { "points" } },
        };

        private static readonly string[] Geometry = { "points", "bulges", "closed", "center", "radius", "startAngle", "endAngle" };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            string opLayer = DraftDb.Layer(ctx, args, issues, "layer", "the entities");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            double tol = ctx.LengthTolerance;
            foreach (var e in args.GetObjectList("entities"))
            {
                var it = new Item { Id = e.GetStringOrNull("id"), Kind = e.GetString("kind") };
                if (it.Id != null && !ids.Add(it.Id)) OpContext.Error(issues, e, "id", "id_duplicate", "id '" + it.Id + "' is used twice");
                it.Layer = e.Has("layer") ? DraftDb.Layer(ctx, e, issues, "layer", "the entity") : opLayer;
                foreach (var f in Geometry)
                    if (f != "closed" && e.Has(f) && !Allowed[it.Kind].Contains(f))
                        OpContext.Error(issues, e, f, "field_not_used", f + " does not apply to a " + it.Kind);
                if (e.Has("closed") && e.GetBool("closed") && it.Kind != "polyline")
                    OpContext.Error(issues, e, "closed", "field_not_used", "closed applies to polylines only");
                it.Points = e.GetPoint2List("points");
                it.Bulges = e.Has("bulges") ? Num.List(e, "bulges") : new List<double>();
                it.Closed = e.GetBool("closed");
                switch (it.Kind)
                {
                    case "line":
                        if (it.Points.Count != 2) OpContext.Error(issues, e, "points", "points_count", "a line needs exactly 2 points");
                        else if (it.Points[0].DistanceTo(it.Points[1]) <= tol) OpContext.Error(issues, e, "points", "zero_length", "the line's points coincide");
                        break;
                    case "point":
                        if (it.Points.Count != 1) OpContext.Error(issues, e, "points", "points_count", "a point needs exactly 1 point");
                        break;
                    case "polyline":
                    {
                        if (it.Points.Count < 2) { OpContext.Error(issues, e, "points", "points_count", "a polyline needs 2 or more points"); break; }
                        int segs = it.Closed ? it.Points.Count : it.Points.Count - 1;
                        if (e.Has("bulges") && it.Bulges.Count != segs)
                            OpContext.Error(issues, e, "bulges", "bulges_count", "give one bulge per segment: " + segs + " for " + it.Points.Count + (it.Closed ? " closed" : " open") + " points, not " + it.Bulges.Count);
                        for (int i = 0; i + 1 < it.Points.Count; i++)
                            if (it.Points[i].DistanceTo(it.Points[i + 1]) <= tol) { OpContext.Error(issues, e, "points", "duplicate_point", "points " + i + " and " + (i + 1) + " coincide"); break; }
                        if (it.Closed && it.Points.Count >= 2 && it.Points[0].DistanceTo(it.Points[it.Points.Count - 1]) <= tol)
                            OpContext.Error(issues, e, "points", "duplicate_point", "the last point repeats the first; a closed polyline closes itself");
                        if (it.Bulges.Any(b => double.IsNaN(b) || double.IsInfinity(b) || Math.Abs(b) > 1e6))
                            OpContext.Error(issues, e, "bulges", "bulge_invalid", "bulges must be finite numbers");
                        while (it.Bulges.Count < segs) it.Bulges.Add(0);
                        break;
                    }
                    case "circle":
                        if (!e.Has("center") || !e.Has("radius")) { OpContext.Error(issues, e, null, "field_missing", "a circle needs center and radius"); break; }
                        it.Center = e.GetPoint2("center");
                        it.Radius = e.GetLength("radius");
                        break;
                    case "arc":
                        ArcPlan(e, it, tol, issues);
                        break;
                }
                p.Items.Add(it);
            }
            return p;
        }

        private static void ArcPlan(OpArgs e, Item it, double tol, List<Issue> issues)
        {
            bool byPoints = e.Has("points");
            bool byCentre = e.Has("center") || e.Has("radius") || e.Has("startAngle") || e.Has("endAngle");
            if (byPoints == byCentre)
            {
                OpContext.Error(issues, e, null, "arc_ambiguous", "give an arc either 3 points (start, on the arc, end) or center, radius, startAngle and endAngle");
                return;
            }
            if (byCentre)
            {
                if (!e.Has("center") || !e.Has("radius") || !e.Has("startAngle") || !e.Has("endAngle"))
                {
                    OpContext.Error(issues, e, null, "field_missing", "an arc by centre needs center, radius, startAngle and endAngle");
                    return;
                }
                it.Center = e.GetPoint2("center");
                it.Radius = e.GetLength("radius");
                it.Start = Num.Wrap(Num.Rad(e.GetAngle("startAngle")));
                it.End = Num.Wrap(Num.Rad(e.GetAngle("endAngle")));
                if (Num.AngleGap(it.Start, it.End) < 1e-9) OpContext.Error(issues, e, "endAngle", "arc_zero", "startAngle and endAngle are the same; use a circle");
                return;
            }
            if (it.Points.Count != 3) { OpContext.Error(issues, e, "points", "points_count", "a three-point arc needs exactly 3 points"); return; }
            var a = it.Points[0]; var m = it.Points[1]; var b = it.Points[2];
            double cross = (m - a).Cross(b - a);
            if (Math.Abs(cross) <= tol * Math.Max(a.DistanceTo(b), tol))
            {
                OpContext.Error(issues, e, "points", "points_collinear", "the three arc points are in a line");
                return;
            }
            double d = 2 * (a.X * (m.Y - b.Y) + m.X * (b.Y - a.Y) + b.X * (a.Y - m.Y));
            double a2 = a.LengthSquared, m2 = m.LengthSquared, b2 = b.LengthSquared;
            it.Center = new Point2((a2 * (m.Y - b.Y) + m2 * (b.Y - a.Y) + b2 * (a.Y - m.Y)) / d, (a2 * (b.X - m.X) + m2 * (a.X - b.X) + b2 * (m.X - a.X)) / d);
            it.Radius = it.Center.DistanceTo(a);
            double sa = Math.Atan2(a.Y - it.Center.Y, a.X - it.Center.X), sb = Math.Atan2(b.Y - it.Center.Y, b.X - it.Center.X);
            if (cross > 0) { it.Start = Num.Wrap(sa); it.End = Num.Wrap(sb); }
            else { it.Start = Num.Wrap(sb); it.End = Num.Wrap(sa); }
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var u = ctx.Units;
            var handles = new JsonArray();
            var idMap = new JsonObject();
            foreach (var it in p.Items)
            {
                Entity ent;
                switch (it.Kind)
                {
                    case "line":
                        ent = new Line(u.ToDrawing(it.Points[0]), u.ToDrawing(it.Points[1]));
                        break;
                    case "point":
                        ent = new DBPoint(u.ToDrawing(it.Points[0]));
                        break;
                    case "circle":
                        ent = new Circle(u.ToDrawing(it.Center), Vector3d.ZAxis, u.ToDrawing(it.Radius));
                        break;
                    case "arc":
                        ent = new Arc(u.ToDrawing(it.Center), u.ToDrawing(it.Radius), it.Start, it.End);
                        break;
                    default:
                    {
                        var pl = new Polyline();
                        for (int i = 0; i < it.Points.Count; i++) pl.AddVertexAt(i, u.ToDrawing2d(it.Points[i]), i < it.Bulges.Count ? it.Bulges[i] : 0, 0, 0);
                        pl.Closed = it.Closed;
                        ent = pl;
                        break;
                    }
                }
                ent.SetDatabaseDefaults(db);
                ent.Layer = it.Layer;
                var id = Db.Append(ctx, ms, ent);
                it.Handle = OpContext.HandleOf(id);
                result.Created.Add(it.Handle);
                handles.Add(it.Handle);
                if (it.Id != null) idMap.Add(it.Id, it.Handle);
            }
            result.Outputs.Add("handles", handles).Add("ids", idMap);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var u = ctx.Units;
            double tol = ctx.LengthTolerance;
            var mismatches = new List<string>();
            var list = new JsonArray();
            foreach (var it in p.Items)
            {
                var ent = (Entity)tr.GetObject(ctx.IdOf(it.Handle), OpenMode.ForRead);
                var rb = new JsonObject().Add("handle", it.Handle).Add("type", ent.GetType().Name).Add("layer", ent.Layer);
                string who = it.Handle + " (" + it.Kind + ")";
                if (!string.Equals(ent.Layer, it.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add(who + " is on layer " + ent.Layer);
                switch (ent)
                {
                    case Line ln:
                        var s = u.ToJob(ln.StartPoint); var e = u.ToJob(ln.EndPoint);
                        rb.Add("points", Num.Points(new[] { s, e }));
                        if (s.DistanceTo(it.Points[0]) > tol || e.DistanceTo(it.Points[1]) > tol) mismatches.Add(who + " end points read back differently");
                        break;
                    case DBPoint pt:
                        var at = u.ToJob(pt.Position);
                        rb.Add("position", DrawingUnits.PointJson(at));
                        if (at.DistanceTo(it.Points[0]) > tol) mismatches.Add(who + " position reads back differently");
                        break;
                    case Arc arc:
                        var ac = u.ToJob(arc.Center); double ar = u.ToJob(arc.Radius);
                        rb.Add("center", DrawingUnits.PointJson(ac)).Add("radius", AcadValues.Round(ar))
                          .Add("startAngle", AcadValues.Round(Num.Deg(arc.StartAngle))).Add("endAngle", AcadValues.Round(Num.Deg(arc.EndAngle)));
                        if (ac.DistanceTo(it.Center) > tol || Math.Abs(ar - it.Radius) > tol || Num.AngleGap(arc.StartAngle, it.Start) > 1e-7 || Num.AngleGap(arc.EndAngle, it.End) > 1e-7)
                            mismatches.Add(who + " centre, radius or angles read back differently");
                        break;
                    case Circle c:
                        var cc = u.ToJob(c.Center); double cr = u.ToJob(c.Radius);
                        rb.Add("center", DrawingUnits.PointJson(cc)).Add("radius", AcadValues.Round(cr));
                        if (cc.DistanceTo(it.Center) > tol || Math.Abs(cr - it.Radius) > tol) mismatches.Add(who + " centre or radius reads back differently");
                        break;
                    case Polyline pl:
                        rb.Add("vertices", pl.NumberOfVertices).Add("closed", pl.Closed);
                        if (pl.NumberOfVertices != it.Points.Count || pl.Closed != it.Closed) { mismatches.Add(who + " has " + pl.NumberOfVertices + " vertices, closed " + pl.Closed); break; }
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            if (u.ToJob(pl.GetPoint2dAt(i)).DistanceTo(it.Points[i]) > tol) { mismatches.Add(who + " vertex " + i + " reads back differently"); break; }
                            double want = i < it.Bulges.Count ? it.Bulges[i] : 0;
                            if (Math.Abs(pl.GetBulgeAt(i) - want) > 1e-9) { mismatches.Add(who + " bulge " + i + " reads back as " + pl.GetBulgeAt(i)); break; }
                        }
                        break;
                    default:
                        mismatches.Add(who + " reads back as " + ent.GetType().Name);
                        break;
                }
                list.Add(rb);
            }
            result.Readback.Add("entities", list);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
