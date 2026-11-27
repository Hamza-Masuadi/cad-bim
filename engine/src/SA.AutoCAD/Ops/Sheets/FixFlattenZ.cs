using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// fix.flattenZ: zeroes stray Z values on 2D entities in the world XY plane.
    ///
    /// Flattened: lines (each end's Z), lightweight polylines and 2D polylines (Elevation), circles, arcs, ellipses,
    /// points, single and multiline text, block references and hatches (moved down by their Z, with a displacement, so
    /// alignment points and attributes move together). Dimensions are judged by their definition points (per type) and
    /// their elevation: when all definition points share one Z the dimension is moved down by it; when they differ, each
    /// point's Z is set to 0 and the dimension block is recomputed. Dimension types without known definition points are
    /// skipped and reported.
    /// Skipped and reported: anything whose normal is not +Z (drawn in another plane, so genuinely 3D or deliberately
    /// tilted), 3D polylines, splines, 3D faces, solids, surfaces, regions, meshes and every other type; and entities on
    /// locked layers. 3D solids, surfaces and meshes are never changed: flattening them is not meaningful, so
    /// skip3dSolids false only changes how they are reported. Block references are moved, not their contents; with
    /// includeBlocks the definitions' own entities are flattened in place.
    /// </summary>
    internal sealed class FixFlattenZ : OpHandler<FixFlattenZ.Plan>
    {
        public override string Name => "fix.flattenZ";

        internal sealed class Plan
        {
            public List<ObjectId> Targets = new List<ObjectId>();
            public Dictionary<string, int> Skipped = new Dictionary<string, int>(StringComparer.Ordinal);
            public int SlopedLines;
            public int Before;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args);
            if (p.Skipped.Count > 0)
                OpContext.Warning(issues, args, null, "flatten_skipped", "entities with Z values that will be left alone: " + string.Join(", ", p.Skipped.Select(kv => kv.Key + " " + kv.Value)));
            if (p.SlopedLines > 0)
                OpContext.Warning(issues, args, null, "sloped_lines", p.SlopedLines + " lines have different Z at their two ends; both ends are set to 0");
        }

        private static Plan Build(OpContext ctx, OpArgs args)
        {
            var p = new Plan();
            double tol = ctx.Units.ToDrawing(args.GetLength("tolerance"));
            bool skipSolids = args.GetBool("skip3dSolids");
            Db.Read(ctx, tr =>
            {
                foreach (var s in SheetDb.Spaces(tr, ctx.Database, args.GetString("space"), args.GetBool("includeBlocks"), false))
                {
                    var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (e is Viewport) continue;
                        string? why = Classify(e, tol, skipSolids, out bool stray, out bool sloped);
                        if (!stray) continue;
                        if (why != null) { Count(p, why); continue; }
                        if (SheetDb.IsOnLockedLayer(tr, e)) { Count(p, "on locked layers"); continue; }
                        if (sloped) p.SlopedLines++;
                        p.Targets.Add(id);
                    }
                }
                return 0;
            });
            p.Before = p.Targets.Count;
            return p;
        }

        private static void Count(Plan p, string why) => p.Skipped[why] = p.Skipped.TryGetValue(why, out int n) ? n + 1 : 1;

        private static bool Off(double z, double tol) => Math.Abs(z) > tol;

        private static bool FlatNormal(Vector3d n) => n.IsParallelTo(Vector3d.ZAxis) && n.Z > 0;

        /// <summary>
        /// Sets <paramref name="stray"/> when the entity has a Z beyond the tolerance. Returns null when it can be
        /// flattened, else why it is skipped.
        /// </summary>
        private static string? Classify(Entity e, double tol, bool skipSolids, out bool stray, out bool sloped)
        {
            stray = false;
            sloped = false;
            switch (e)
            {
                case Line l:
                    stray = Off(l.StartPoint.Z, tol) || Off(l.EndPoint.Z, tol);
                    sloped = Math.Abs(l.StartPoint.Z - l.EndPoint.Z) > tol;
                    return null;
                case Polyline pl:
                    stray = Off(pl.Elevation, tol) || !FlatNormal(pl.Normal);
                    return FlatNormal(pl.Normal) ? null : "not in the XY plane";
                case Polyline2d p2:
                    stray = Off(p2.Elevation, tol) || !FlatNormal(p2.Normal);
                    return FlatNormal(p2.Normal) ? null : "not in the XY plane";
                case Circle c: return Planar(c.Center.Z, c.Normal, tol, out stray);
                case Arc a: return Planar(a.Center.Z, a.Normal, tol, out stray);
                case Ellipse el: return Planar(el.Center.Z, el.Normal, tol, out stray);
                case DBPoint pt: return Planar(pt.Position.Z, pt.Normal, tol, out stray);
                case DBText t: return Planar(t.Position.Z, t.Normal, tol, out stray);
                case MText mt: return Planar(mt.Location.Z, mt.Normal, tol, out stray);
                case BlockReference br: return Planar(br.Position.Z, br.Normal, tol, out stray);
                case Hatch h: return Planar(h.Elevation, h.Normal, tol, out stray);
                case Dimension d:
                {
                    var pts = DefPoints(d);
                    bool flat = FlatNormal(d.Normal);
                    stray = !flat || Off(d.Elevation, tol) || pts != null && pts.Any(q => Off(q.Z, tol));
                    if (!flat) return "not in the XY plane";
                    return pts is null ? "dimensions of type " + d.GetType().Name : null;
                }
                case Polyline3d _: stray = true; return "3D polylines";
                case Spline _: stray = true; return "splines";
                case Face _: stray = true; return "3D faces";
                case Solid3d _:
                case Autodesk.AutoCAD.DatabaseServices.Surface _:
                case SubDMesh _:
                case PolyFaceMesh _:
                case PolygonMesh _:
                    stray = true;
                    return skipSolids ? "3D solids, surfaces and meshes" : "3D solids, surfaces and meshes (never flattened)";
                case Region _: stray = true; return "regions";
                default:
                    return null;
            }
        }

        private static string? Planar(double z, Vector3d normal, double tol, out bool stray)
        {
            bool flat = FlatNormal(normal);
            stray = Off(z, tol) || !flat;
            return flat ? null : "not in the XY plane";
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var p = Build(ctx, args);
            var tr = ctx.Transaction;
            double tol = ctx.Units.ToDrawing(args.GetLength("tolerance"));
            foreach (var id in p.Targets)
            {
                var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                switch (e)
                {
                    case Line l:
                        l.StartPoint = new Point3d(l.StartPoint.X, l.StartPoint.Y, 0);
                        l.EndPoint = new Point3d(l.EndPoint.X, l.EndPoint.Y, 0);
                        break;
                    case Polyline pl: pl.Elevation = 0; break;
                    case Polyline2d p2: p2.Elevation = 0; break;
                    case Circle c: Drop(e, c.Center.Z); break;
                    case Arc a: Drop(e, a.Center.Z); break;
                    case Ellipse el: Drop(e, el.Center.Z); break;
                    case DBPoint pt: Drop(e, pt.Position.Z); break;
                    case DBText t: Drop(e, t.Position.Z); break;
                    case MText mt: Drop(e, mt.Location.Z); break;
                    case BlockReference br: Drop(e, br.Position.Z); break;
                    case Hatch h: h.Elevation = 0; break;
                    case Dimension d: FlattenDimension(d, tol); break;
                    default: throw new InvalidOperationException("Unexpected entity type " + e.GetType().Name + " in flatten plan.");
                }
                result.Modified.Add(OpContext.HandleOf(id));
            }
            result.Outputs.Add("count", p.Targets.Count);
            return p;
        }

        /// <summary>The WCS definition points of a dimension, or null for a type this op does not know.</summary>
        private static Point3d[]? DefPoints(Dimension d)
        {
            switch (d)
            {
                case RotatedDimension r: return new[] { r.XLine1Point, r.XLine2Point, r.DimLinePoint };
                case AlignedDimension a: return new[] { a.XLine1Point, a.XLine2Point, a.DimLinePoint };
                case Point3AngularDimension p3: return new[] { p3.CenterPoint, p3.XLine1Point, p3.XLine2Point, p3.ArcPoint };
                case LineAngularDimension2 l2: return new[] { l2.XLine1Start, l2.XLine1End, l2.XLine2Start, l2.XLine2End, l2.ArcPoint };
                case RadialDimensionLarge rl: return new[] { rl.Center, rl.ChordPoint, rl.OverrideCenter, rl.JogPoint };
                case RadialDimension rd: return new[] { rd.Center, rd.ChordPoint };
                case DiametricDimension dd: return new[] { dd.ChordPoint, dd.FarChordPoint };
                case OrdinateDimension od: return new[] { od.Origin, od.DefiningPoint, od.LeaderEndPoint };
                case ArcDimension ad: return new[] { ad.CenterPoint, ad.XLine1Point, ad.XLine2Point, ad.ArcPoint, ad.Leader1Point, ad.Leader2Point };
                default: return null;
            }
        }

        private static Point3d Flat(Point3d p) => new Point3d(p.X, p.Y, 0);

        private static void FlattenDimension(Dimension d, double tol)
        {
            var pts = DefPoints(d) ?? throw new InvalidOperationException("Dimension type " + d.GetType().Name + " has no known definition points.");
            double z0 = pts[0].Z;
            if (pts.All(q => Math.Abs(q.Z - z0) <= tol)) Drop(d, z0);
            else
            {
                switch (d)
                {
                    case RotatedDimension r: r.XLine1Point = Flat(r.XLine1Point); r.XLine2Point = Flat(r.XLine2Point); r.DimLinePoint = Flat(r.DimLinePoint); break;
                    case AlignedDimension a: a.XLine1Point = Flat(a.XLine1Point); a.XLine2Point = Flat(a.XLine2Point); a.DimLinePoint = Flat(a.DimLinePoint); break;
                    case Point3AngularDimension p3: p3.CenterPoint = Flat(p3.CenterPoint); p3.XLine1Point = Flat(p3.XLine1Point); p3.XLine2Point = Flat(p3.XLine2Point); p3.ArcPoint = Flat(p3.ArcPoint); break;
                    case LineAngularDimension2 l2: l2.XLine1Start = Flat(l2.XLine1Start); l2.XLine1End = Flat(l2.XLine1End); l2.XLine2Start = Flat(l2.XLine2Start); l2.XLine2End = Flat(l2.XLine2End); l2.ArcPoint = Flat(l2.ArcPoint); break;
                    case RadialDimensionLarge rl: rl.Center = Flat(rl.Center); rl.ChordPoint = Flat(rl.ChordPoint); rl.OverrideCenter = Flat(rl.OverrideCenter); rl.JogPoint = Flat(rl.JogPoint); break;
                    case RadialDimension rd: rd.Center = Flat(rd.Center); rd.ChordPoint = Flat(rd.ChordPoint); break;
                    case DiametricDimension dd: dd.ChordPoint = Flat(dd.ChordPoint); dd.FarChordPoint = Flat(dd.FarChordPoint); break;
                    case OrdinateDimension od: od.Origin = Flat(od.Origin); od.DefiningPoint = Flat(od.DefiningPoint); od.LeaderEndPoint = Flat(od.LeaderEndPoint); break;
                    case ArcDimension ad: ad.CenterPoint = Flat(ad.CenterPoint); ad.XLine1Point = Flat(ad.XLine1Point); ad.XLine2Point = Flat(ad.XLine2Point); ad.ArcPoint = Flat(ad.ArcPoint); ad.Leader1Point = Flat(ad.Leader1Point); ad.Leader2Point = Flat(ad.Leader2Point); break;
                }
            }
            if (d.Elevation != 0) d.Elevation = 0;
            d.RecomputeDimensionBlock(true);
        }

        private static void Drop(Entity e, double z)
        {
            if (z != 0) e.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -z)));
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var again = Build(ctx, args);
            var skipped = new JsonObject();
            foreach (var kv in p.Skipped) skipped.Add(kv.Key, kv.Value);
            result.Readback.Add("before", p.Before).Add("after", again.Targets.Count).Add("changed", p.Targets.Count)
                .Add("slopedLines", p.SlopedLines).Add("skipped", skipped);
            return again.Targets.Count == 0 || Mismatch(result, again.Targets.Count + " flattenable entities still have Z values after the fix");
        }
    }
}
