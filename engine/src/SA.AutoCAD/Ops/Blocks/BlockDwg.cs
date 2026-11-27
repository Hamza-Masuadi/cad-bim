using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace SA.AutoCAD.Ops.Blocks
{
    /// <summary>What a block definition holds, in its own coordinates (not relative to the base point).</summary>
    internal sealed class DefinitionInfo
    {
        public Extents3d? Raw;
        public bool Has3d;
        /// <summary>Non-erased entities directly in the definition, attribute definitions included (what Wblock copies).</summary>
        public int Entities;
    }

    /// <summary>
    /// Block definitions and the DWG written for each: Database.Wblock(blockId) gives a new database whose model space
    /// holds the definition's entities; it is normalised (base point at the origin, the source's INSUNITS), saved, and
    /// disposed at once. Every Database made here is disposed in the same method.
    /// </summary>
    internal static class BlockDwg
    {
        private const int MaxDepth = 32;

        /// <summary>
        /// Extents of visible entities (attribute definitions excluded, as INSERT never draws them) and whether any entity,
        /// in nested blocks too, is a 3D solid, surface, mesh, body or 3D face.
        /// </summary>
        internal static DefinitionInfo Inspect(Transaction tr, BlockTableRecord btr, Dictionary<ObjectId, bool> has3dCache)
        {
            var info = new DefinitionInfo { Raw = Extents(tr, btr, out int count) };
            info.Entities = count;
            info.Has3d = Has3d(tr, btr, has3dCache, 0);
            return info;
        }

        private static Extents3d? Extents(Transaction tr, BlockTableRecord owner, out int count)
        {
            Extents3d? box = null;
            count = 0;
            foreach (ObjectId id in owner)
            {
                if (id.IsErased) continue;
                count++;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || e is AttributeDefinition || !e.Visible) continue;
                Extents3d ext;
                try { ext = e.GeometricExtents; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; } // entities without extents (empty text, rays) are skipped
                if (box is null) box = ext;
                else { var b = box.Value; b.AddExtents(ext); box = b; }
            }
            return box;
        }

        private static bool Has3d(Transaction tr, BlockTableRecord btr, Dictionary<ObjectId, bool> cache, int depth)
        {
            if (cache.TryGetValue(btr.ObjectId, out bool known)) return known;
            cache[btr.ObjectId] = false;
            bool found = false;
            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                var o = tr.GetObject(id, OpenMode.ForRead);
                if (o is Solid3d || o is Autodesk.AutoCAD.DatabaseServices.Surface || o is SubDMesh || o is PolyFaceMesh || o is PolygonMesh || o is Body || o is Face) { found = true; break; }
                if (o is BlockReference br && depth < MaxDepth && !br.BlockTableRecord.IsNull &&
                    Has3d(tr, (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead), cache, depth + 1)) { found = true; break; }
            }
            cache[btr.ObjectId] = found;
            return found;
        }

        /// <summary>
        /// Writes the definition to <paramref name="path"/>. The new drawing's model space is moved so the block's base
        /// point is at its origin, unless Wblock already put it there (decided by comparing extents), and INSBASE is the origin.
        /// </summary>
        internal static void Write(Database source, ObjectId btrId, Point3d basePoint, Extents3d? raw, string path)
        {
            using (var db = source.Wblock(btrId))
            {
                db.Insunits = source.Insunits;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = ModelSpace(tr, db);
                    var now = Extents(tr, ms, out _);
                    var shift = basePoint.GetAsVector();
                    bool untranslated = raw.HasValue && now.HasValue && Close(now.Value, raw.Value);
                    if (untranslated && !shift.IsZeroLength())
                    {
                        var m = Matrix3d.Displacement(shift.Negate());
                        foreach (ObjectId id in ms)
                        {
                            if (id.IsErased) continue;
                            if (tr.GetObject(id, OpenMode.ForWrite, false, true) is Entity e) e.TransformBy(m);
                        }
                    }
                    tr.Commit();
                }
                db.Insbase = Point3d.Origin;
                db.SaveAs(path, DwgVersion.Current);
            }
        }

        /// <summary>What a written DWG holds, read back from the file.</summary>
        internal sealed class DwgFacts
        {
            public int Insunits;
            public Point3d Insbase;
            public int Entities;
            public Extents3d? Extents;
        }

        internal static DwgFacts Read(string path)
        {
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var r = new DwgFacts { Insunits = (int)db.Insunits, Insbase = db.Insbase };
                    r.Extents = Extents(tr, ModelSpace(tr, db), out r.Entities);
                    tr.Commit();
                    return r;
                }
            }
        }

        private static BlockTableRecord ModelSpace(Transaction tr, Database db)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }

        /// <summary>Tolerance for comparing extents: one millionth of the box's size, at least 1e-6 drawing units.</summary>
        internal static double Tolerance(Extents3d e) => Math.Max(1e-6, e.MinPoint.DistanceTo(e.MaxPoint) * 1e-6);

        internal static bool Close(Extents3d a, Extents3d b)
        {
            double tol = Math.Max(Tolerance(a), Tolerance(b));
            return a.MinPoint.DistanceTo(b.MinPoint) <= tol && a.MaxPoint.DistanceTo(b.MaxPoint) <= tol;
        }
    }
}
