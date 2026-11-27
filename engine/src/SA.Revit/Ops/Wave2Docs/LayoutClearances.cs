using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// layout.clearances: read-only test of clearance zones against element footprints in a plan view. All plan
    /// geometry is in job units, project coordinates.
    ///
    /// <para><b>Zones</b> are convex polygons (ClearanceGeometry): rect, polygon (must be convex), doorSwing (the
    /// sector from the hinge, 5 degree steps; side right sweeps clockwise), route (one rectangle per centreline
    /// segment), doorClearance (a rectangle width wide, depth deep, from the wall face on the facing and/or back side
    /// of a wall-hosted door: the face is the host wall's exterior or interior side face plane from
    /// HostObjectUtils.GetSideFaces, else half the wall width from the door's point). circle is tested exactly.</para>
    ///
    /// <para><b>Obstacles</b> are the non-type elements of the against categories visible in the view
    /// (FilteredElementCollector(doc, view.Id)). Footprints come from the element's solids (Options.DetailLevel
    /// Fine, instance geometry): for walls the convex hull of each downward-facing planar face, so a door opening
    /// that reaches the floor stays open; for everything else the convex hull of each solid's edge points, so
    /// concave parts are over-covered (conservative). Solids wholly outside the minHeight..maxHeight band above the
    /// view's level (Level.ProjectElevation) are dropped. An element with no solid falls back to its bounding box in
    /// plan.</para>
    ///
    /// <para><b>Depth</b> is the minimum translation distance between the zone and a footprint part (separating
    /// axes; for circles, radius minus the distance from the centre to the polygon, or radius plus it when the centre is
    /// inside), the largest over the element's parts. Depths at or below tolerance (default 0.5 mm) are touching and
    /// are not reported. failOnIntrusion turns any intrusion into op failure (intrusion_found).</para>
    /// </summary>
    public sealed class LayoutClearances : OpHandler
    {
        public override string Name => "layout.clearances";
        public override bool ModifiesModel => false;

        private sealed class Zone
        {
            public string Id = "", Kind = "";
            public List<List<Point2>> Polys = new List<List<Point2>>();
            public Point2? Centre;
            public double Radius;
            public HashSet<long> Ignore = new HashSet<long>();
        }

        private sealed class Plan
        {
            public ViewPlan View = null!;
            public List<Zone> Zones = new List<Zone>();
            public List<Category> Categories = new List<Category>();
            public HashSet<long> Ignore = new HashSet<long>();
            public double? Low, High;
            public double Tolerance;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            var p = new Plan { Tolerance = args.GetLengthOrNull("tolerance") ?? u.LengthFromInternal(UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters)) };
            var names = args.GetStringList("against");
            for (int i = 0; i < names.Count; i++)
            {
                var c = DocsUtil.Category(doc, names[i], JsonPath.Index(ctx.FieldPath(args, "against"), i), issues);
                if (c != null) p.Categories.Add(c);
            }
            foreach (var id in Ids(ctx, args.Get("ignore"), ctx.FieldPath(args, "ignore"), issues, ref pending)) p.Ignore.Add(id);
            if (pending || v is null) return null;
            if (!(v is ViewPlan vp) || v.IsTemplate || vp.GenLevel is null)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "layout.clearances works in plan views with a level; " + DocsUtil.Describe(v) + " is not one");
                return null;
            }
            p.View = vp;
            double level = vp.GenLevel.ProjectElevation;
            if (args.GetLengthOrNull("minHeight") is double lo) p.Low = level + u.LengthToInternal(lo);
            if (args.GetLengthOrNull("maxHeight") is double hi) p.High = level + u.LengthToInternal(hi);
            if (p.Low.HasValue && p.High.HasValue && p.High <= p.Low)
                OpContext.Error(issues, ctx.FieldPath(args, "maxHeight"), RevitIssueCodes.Invalid, "maxHeight must be above minHeight");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var z in args.GetObjectList("zones"))
            {
                var zone = new Zone { Id = z.GetString("id"), Kind = z.GetString("kind") };
                if (!ids.Add(zone.Id)) OpContext.Error(issues, z.Path + ".id", RevitIssueCodes.Invalid, "zone id '" + zone.Id + "' is used twice");
                bool zp = false;
                foreach (var id in Ids(ctx, z.Get("ignore"), z.Path + ".ignore", issues, ref zp)) zone.Ignore.Add(id);
                if (zp) return null;
                if (ZoneShape(ctx, z, zone, issues)) p.Zones.Add(zone);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static IEnumerable<long> Ids(OpContext ctx, JsonValue? value, string path, List<Issue> issues, ref bool pending)
        {
            var list = new List<long>();
            if (!(value is JsonArray a)) return list;
            for (int i = 0; i < a.Count; i++)
            {
                var e = Support.W2.Element(ctx, a[i], JsonPath.Index(path, i), issues, out bool pd);
                pending |= pd;
                if (e != null) list.Add(ElementIds.ToLong(e.Id));
            }
            return list;
        }

        private static bool Need(OpArgs z, List<Issue> issues, params string[] fields)
        {
            bool ok = true;
            foreach (var f in fields)
                if (!z.Has(f)) { OpContext.Error(issues, z.Path + "." + f, RevitIssueCodes.Invalid, z.GetString("kind") + " zones need " + f); ok = false; }
            return ok;
        }

        private static bool ZoneShape(OpContext ctx, OpArgs z, Zone zone, List<Issue> issues)
        {
            double tol = ctx.Units.LengthTolerance;
            switch (zone.Kind)
            {
                case "circle":
                    if (!Need(z, issues, "centre", "radius")) return false;
                    zone.Centre = z.GetPoint2("centre");
                    zone.Radius = z.GetLength("radius");
                    return true;
                case "rect":
                {
                    if (!Need(z, issues, "min", "max")) return false;
                    var a = z.GetPoint2("min");
                    var b = z.GetPoint2("max");
                    if (b.X - a.X <= tol || b.Y - a.Y <= tol) { OpContext.Error(issues, z.Path + ".max", RevitIssueCodes.Invalid, "max must be above and right of min"); return false; }
                    zone.Polys.Add(ClearanceGeometry.Rect(a, b));
                    return true;
                }
                case "polygon":
                {
                    if (!Need(z, issues, "points")) return false;
                    var pts = z.GetPoint2List("points");
                    if (pts.Count < 3 || !ClearanceGeometry.IsConvex(pts, tol))
                    {
                        OpContext.Error(issues, z.Path + ".points", RevitIssueCodes.Invalid, "a polygon zone needs 3 or more points forming a convex outline; split a concave zone into convex ones");
                        return false;
                    }
                    zone.Polys.Add(ClearanceGeometry.ConvexHull(pts));
                    return true;
                }
                case "route":
                {
                    if (!Need(z, issues, "points", "width")) return false;
                    zone.Polys.AddRange(ClearanceGeometry.Route(z.GetPoint2List("points"), z.GetLength("width")));
                    if (zone.Polys.Count == 0) { OpContext.Error(issues, z.Path + ".points", RevitIssueCodes.Invalid, "the route centreline has no length"); return false; }
                    return true;
                }
                case "doorSwing":
                {
                    if (!Need(z, issues, "hinge", "leafWidth", "closedAngle")) return false;
                    double sweep = z.GetAngle("swing") * (z.GetString("side") == "right" ? -1 : 1);
                    zone.Polys.Add(ClearanceGeometry.Sector(z.GetPoint2("hinge"), z.GetLength("leafWidth"), z.GetAngle("closedAngle"), sweep));
                    return true;
                }
                case "doorClearance":
                    return DoorClearance(ctx, z, zone, issues);
            }
            return false;
        }

        private static bool DoorClearance(OpContext ctx, OpArgs z, Zone zone, List<Issue> issues)
        {
            if (!Need(z, issues, "door", "depth")) return false;
            var u = ctx.Units;
            var e = Support.W2.Element(ctx, z.Get("door"), z.Path + ".door", issues, out bool pending);
            if (pending || e is null) return false;
            if (!(e is FamilyInstance fi) || !(fi.Host is Wall wall) || !(fi.Location is LocationPoint lp))
            {
                OpContext.Error(issues, z.Path + ".door", RevitIssueCodes.Invalid, "element " + ElementIds.ToWire(e.Id) + " is not a door or window hosted by a wall");
                return false;
            }
            zone.Ignore.Add(ElementIds.ToLong(fi.Id));
            zone.Ignore.Add(ElementIds.ToLong(wall.Id));
            var facing = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
            var hand = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
            if (facing.GetLength() < 1e-9 || hand.GetLength() < 1e-9) { OpContext.Error(issues, z.Path + ".door", RevitIssueCodes.Invalid, "the door has no plan orientation"); return false; }
            facing = facing.Normalize();
            hand = hand.Normalize();
            var at = lp.Point;
            double? width = z.GetLengthOrNull("width");
            if (width is null)
            {
                var wp = fi.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? fi.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                    ?? fi.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) ?? fi.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                if (wp is null || wp.StorageType != StorageType.Double || wp.AsDouble() <= 0)
                {
                    OpContext.Error(issues, z.Path + ".width", RevitIssueCodes.Invalid, "the door has no Width parameter; give width");
                    return false;
                }
                width = u.LengthFromInternal(wp.AsDouble());
            }
            double depth = z.GetLength("depth");
            string faces = z.GetString("faces");
            foreach (int sign in new[] { 1, -1 })
            {
                if (faces == "facing" && sign < 0 || faces == "back" && sign > 0) continue;
                var n = facing * sign;
                double face = FaceDistance(wall, at, n) ?? wall.Width / 2;
                var c0 = at + n * face;
                var h = hand * u.LengthToInternal(width.Value / 2);
                var d = n * u.LengthToInternal(depth);
                zone.Polys.Add(ClearanceGeometry.ConvexHull(new[] { c0 - h, c0 + h, c0 + h + d, c0 - h + d }.Select(u.PointFromInternal)));
            }
            return true;
        }

        /// <summary>Distance from the point to the plane of the wall's side face lying in direction n, if planar.</summary>
        private static double? FaceDistance(Wall wall, XYZ at, XYZ n)
        {
            double? best = null;
            foreach (var shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(wall, shell); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                foreach (var r in refs)
                    if (wall.GetGeometryObjectFromReference(r) is PlanarFace pf)
                    {
                        double d = (pf.Origin - at).DotProduct(n);
                        if (d > 0 && Math.Abs(pf.FaceNormal.DotProduct(n)) > 0.99 && (best is null || d < best)) best = d;
                    }
            }
            return best;
        }

        private static List<List<Point2>> Footprint(OpContext ctx, Plan p, Element e)
        {
            var parts = new List<List<Point2>>();
            var opts = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            bool wall = e is Wall;
            foreach (var s in Solids(e.get_Geometry(opts)))
            {
                var pts = new List<XYZ>();
                foreach (Edge edge in s.Edges) pts.AddRange(edge.Tessellate());
                if (pts.Count == 0) continue;
                double lo = pts.Min(x => x.Z), hi = pts.Max(x => x.Z);
                if (p.Low.HasValue && hi <= p.Low.Value || p.High.HasValue && lo >= p.High.Value) continue;
                if (wall)
                {
                    bool any = false;
                    // Only the faces at the solid's base: the soffit over a door or window opening also faces down,
                    // and its hull would close the opening.
                    double baseTol = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.Z < -0.99 && pf.Origin.Z <= lo + baseTol)
                        {
                            var fp = new List<XYZ>();
                            foreach (EdgeArray loop in pf.EdgeLoops) foreach (Edge edge in loop) fp.AddRange(edge.Tessellate());
                            if (fp.Count >= 3) { parts.Add(ClearanceGeometry.ConvexHull(fp.Select(ctx.Units.PointFromInternal))); any = true; }
                        }
                    if (any) continue;
                }
                var hull = ClearanceGeometry.ConvexHull(pts.Select(ctx.Units.PointFromInternal));
                if (hull.Count >= 3) parts.Add(hull);
            }
            if (parts.Count == 0 && e.get_BoundingBox(p.View) is BoundingBoxXYZ bb)
            {
                if (p.Low.HasValue && bb.Max.Z <= p.Low.Value || p.High.HasValue && bb.Min.Z >= p.High.Value) return parts;
                var corners = new[] { bb.Min, new XYZ(bb.Max.X, bb.Min.Y, 0), bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0) }.Select(c => bb.Transform.OfPoint(c));
                var hull = ClearanceGeometry.ConvexHull(corners.Select(ctx.Units.PointFromInternal));
                if (hull.Count >= 3) parts.Add(hull);
            }
            return parts;
        }

        private static IEnumerable<Solid> Solids(GeometryElement? g)
        {
            if (g is null) yield break;
            foreach (var o in g)
            {
                if (o is Solid s && s.Volume > 0 && s.Edges.Size > 0) yield return s;
                else if (o is GeometryInstance gi) foreach (var x in Solids(gi.GetInstanceGeometry())) yield return x;
            }
        }

        private static double Depth(Zone z, List<Point2> part)
        {
            if (z.Centre is Point2 c) return ClearanceGeometry.CircleDepth(c, z.Radius, part);
            return z.Polys.Count == 0 ? 0 : z.Polys.Max(zp => ClearanceGeometry.PolygonDepth(zp, part));
        }

        private static Point2 Witness(Zone z, List<Point2> part)
        {
            var inside = new List<Point2>();
            if (z.Centre is Point2 c)
            {
                inside.AddRange(part.Where(x => x.DistanceTo(c) < z.Radius));
                if (inside.Count == 0) return c;
            }
            else
                foreach (var zp in z.Polys)
                {
                    var zpoly = new Polygon2(zp);
                    var ppoly = new Polygon2(part);
                    inside.AddRange(part.Where(x => zpoly.Locate(x, 0) == PointLocation.Inside));
                    inside.AddRange(zp.Where(x => ppoly.Locate(x, 0) == PointLocation.Inside));
                }
            var src = inside.Count > 0 ? inside : part;
            return new Point2(src.Average(x => x.X), src.Average(x => x.Y));
        }

        private static JsonArray PointJson(Point2 x) => new JsonArray(RevitUnits.Num(x.X), RevitUnits.Num(x.Y));

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var obstacles = new List<(Element E, List<List<Point2>> Parts)>();
            var seen = new HashSet<long>();
            foreach (var c in p.Categories)
                foreach (var e in new FilteredElementCollector(doc, p.View.Id).OfCategoryId(c.Id).WhereElementIsNotElementType())
                {
                    long id = ElementIds.ToLong(e.Id);
                    if (p.Ignore.Contains(id) || !seen.Add(id)) continue;
                    var parts = Footprint(ctx, p, e);
                    if (parts.Count > 0) obstacles.Add((e, parts));
                }

            var all = new List<JsonObject>();
            var zones = new JsonArray();
            foreach (var z in p.Zones)
            {
                var hits = new List<(double Depth, JsonObject Item)>();
                foreach (var (e, parts) in obstacles)
                {
                    if (z.Ignore.Contains(ElementIds.ToLong(e.Id))) continue;
                    double best = 0;
                    List<Point2>? worst = null;
                    foreach (var part in parts)
                    {
                        double d = Depth(z, part);
                        if (d > best) { best = d; worst = part; }
                    }
                    if (best <= p.Tolerance || worst is null) continue;
                    var type = doc.GetElement(e.GetTypeId()) as ElementType;
                    hits.Add((best, new JsonObject().Add("zone", z.Id).Add("element", ElementIds.ToWire(e.Id)).Add("category", e.Category?.Name ?? "")
                        .Add("name", type is null ? e.Name : type.FamilyName + ": " + type.Name).Add("depth", RevitUnits.Num(best)).Add("point", PointJson(Witness(z, worst)))));
                }
                hits = hits.OrderByDescending(h => h.Depth).ToList();
                all.AddRange(hits.Select(h => h.Item));
                var outline = z.Centre is Point2 cc
                    ? (JsonValue)new JsonObject().Add("centre", PointJson(cc)).Add("radius", RevitUnits.Num(z.Radius))
                    : new JsonArray(z.Polys.Select(poly => (JsonValue)new JsonArray(poly.Select(x => (JsonValue)PointJson(x)).ToArray())).ToArray());
                zones.Add(new JsonObject().Add("id", z.Id).Add("kind", z.Kind).Add("intrusions", hits.Count)
                    .Add("maxDepth", RevitUnits.Num(hits.Count > 0 ? hits[0].Depth : 0)).Add("outline", outline));
            }
            all = all.OrderByDescending(x => (x["depth"] as JsonNumber)?.Value ?? 0).ToList();
            result.Outputs.Set("count", all.Count);
            result.Outputs.Set("clear", all.Count == 0);
            result.Outputs.Set("intrusions", new JsonArray(all.Cast<JsonValue>().ToArray()));
            result.Outputs.Set("zones", zones);
            if (all.Count > 0 && args.GetBool("failOnIntrusion"))
                throw new OpFailedException("intrusion_found", all.Count + " intrusion(s); deepest: zone '" + all[0]["zone"]?.AsString() + "', element " + all[0]["element"]?.AsString());
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            int n = 0;
            foreach (var item in (result.Outputs["intrusions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                string id = item["element"]?.AsString() ?? "";
                if (!ElementIds.TryParse(id, out var eid) || ctx.Doc.GetElement(eid) is null) check.Fail("reported element " + id + " does not exist");
                n++;
            }
            result.Readback.Set("intrusions", n);
            result.Readback.Set("obstacleCategories", JsonArray.FromStrings(args.GetStringList("against")));
            check.Apply(result);
        }
    }
}
