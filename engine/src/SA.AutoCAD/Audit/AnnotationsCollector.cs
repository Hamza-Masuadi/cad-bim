using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Audit.Collectors;
using SA.Core.Checks;

namespace SA.AutoCAD.Audit
{
    /// <summary>
    /// Table "annotations" (data/checks/FACTS.md 1.8): one record per piece of annotation text that shows, in model space
    /// and every layout's paper space; id = handle of the entity whose text it is.
    ///
    /// Fields: type (TEXT, MTEXT, ATTRIB, DIMENSION, MULTILEADER), space, layout, layer, minX, minY, maxX, maxY (the text's
    /// box in WCS for model space and in paper units for a layout), ownerId (ATTRIB: handle of the block insert), byJob
    /// (in a job's verify step: the entity, or the insert that owns the attribute, was created or modified by the job).
    ///
    /// Boxes: TEXT GeometricExtents; MTEXT and multiline attributes MText.GetBoundingPoints (the text as displayed, not the
    /// column width); single-line attributes GeometricExtents; DIMENSION the union of the TEXT and MTEXT that
    /// Entity.Explode returns (the dimension's own text block; no text, no record); MULTILEADER with MText content the
    /// bounding points of MLeader.MText. Left out: entities and attributes that do not show (Entity.Visible false,
    /// AttributeReference.Invisible, layer off or frozen), the overall paper-space viewport, attribute definitions.
    /// </summary>
    internal sealed class AnnotationsCollector : IFactCollector
    {
        public string Table => "annotations";

        public void Collect(FactContext ctx, FactTable table)
        {
            var db = ctx.Database;
            var tr = ctx.Transaction;
            var hidden = HiddenLayers(db, tr);
            int total = 0, failed = 0;
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.BlockTableRecordId.IsNull) continue;
                bool model = layout.ModelType || layout.BlockTableRecordId == modelId;
                var overall = model ? ObjectId.Null : DrawingScan.FindOverallViewport(tr, layout);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                string space = model ? "model" : "paper";
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased || id == overall) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!e.Visible || hidden.Contains(e.LayerId)) continue;
                    try
                    {
                        foreach (var (rid, type, layer, box, owner) in Boxes(e, id, tr, hidden))
                        {
                            total++;
                            if (table.Records.Count >= ctx.MaxRecords) continue;
                            var r = table.Add(rid);
                            r.Set("type", type).Set("space", space).Set("layout", layout.LayoutName).Set("layer", layer)
                                .Set("minX", box.MinPoint.X).Set("minY", box.MinPoint.Y).Set("maxX", box.MaxPoint.X).Set("maxY", box.MaxPoint.Y);
                            if (owner != null) r.Set("ownerId", owner);
                            if (ctx.JobScope != null) r.Set("byJob", ctx.JobScope.Contains(rid) || (owner != null && ctx.JobScope.Contains(owner)));
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { failed++; }
                    catch (InvalidOperationException) { failed++; }
                }
            }
            if (total > table.Records.Count) ctx.MarkTruncated(Table, table.Records.Count, total);
            if (failed > 0) ctx.Facts.MarkTruncated(Table, Facts.RecordsPart, total, total + failed, failed + " annotation(s) whose text box could not be read");
        }

        internal static HashSet<ObjectId> HiddenLayers(Database db, Transaction tr)
        {
            var set = new HashSet<ObjectId>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                if (id.IsErased) continue;
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (l.IsFrozen || l.IsOff) set.Add(id);
            }
            return set;
        }

        private static IEnumerable<(string Id, string Type, string Layer, Extents3d Box, string? Owner)> Boxes(Entity e, ObjectId id, Transaction tr, HashSet<ObjectId> hidden)
        {
            string handle = FactValues.Handle(id);
            switch (e)
            {
                case AttributeDefinition _:
                    yield break;
                case DBText t:
                    if (!string.IsNullOrWhiteSpace(t.TextString)) yield return (handle, "TEXT", t.Layer, t.GeometricExtents, null);
                    yield break;
                case MText m:
                    var mb = Bounds(m);
                    if (mb.HasValue) yield return (handle, "MTEXT", m.Layer, mb.Value, null);
                    yield break;
                case BlockReference br:
                    foreach (ObjectId aId in br.AttributeCollection)
                    {
                        if (aId.IsErased) continue;
                        if (!(tr.GetObject(aId, OpenMode.ForRead) is AttributeReference a)) continue;
                        if (a.Invisible || !a.Visible || hidden.Contains(a.LayerId) || string.IsNullOrWhiteSpace(a.TextString)) continue;
                        Extents3d? ab;
                        if (a.IsMTextAttribute)
                        {
                            using (var mt = a.MTextAttribute) ab = Bounds(mt);
                        }
                        else ab = a.GeometricExtents;
                        if (ab.HasValue) yield return (FactValues.Handle(aId), "ATTRIB", a.Layer, ab.Value, handle);
                    }
                    yield break;
                case Dimension d:
                    var db = DimensionText(d);
                    if (db.HasValue) yield return (handle, "DIMENSION", d.Layer, db.Value, null);
                    yield break;
                case MLeader ml:
                    if (ml.ContentType != ContentType.MTextContent) yield break;
                    Extents3d? lb = null;
                    using (var mt = ml.MText) if (mt != null) lb = Bounds(mt);
                    if (lb.HasValue) yield return (handle, "MULTILEADER", ml.Layer, lb.Value, null);
                    yield break;
            }
        }

        /// <summary>The displayed text's corners (MText.GetBoundingPoints), as a box; null for empty text.</summary>
        internal static Extents3d? Bounds(MText m)
        {
            if (string.IsNullOrWhiteSpace(m.Text)) return null;
            var pts = m.GetBoundingPoints();
            if (pts is null || pts.Count == 0) return null;
            var ext = new Extents3d(pts[0], pts[0]);
            foreach (Point3d p in pts) ext.AddPoint(p);
            return ext;
        }

        private static Extents3d? DimensionText(Dimension d)
        {
            var parts = new DBObjectCollection();
            Extents3d? box = null;
            try
            {
                d.Explode(parts);
                foreach (DBObject o in parts)
                {
                    Extents3d? b = o is MText m ? Bounds(m) : o is DBText t && !string.IsNullOrWhiteSpace(t.TextString) ? t.GeometricExtents : (Extents3d?)null;
                    if (!b.HasValue) continue;
                    if (box.HasValue) { var u = box.Value; u.AddExtents(b.Value); box = u; }
                    else box = b;
                }
            }
            finally
            {
                foreach (DBObject o in parts) o.Dispose();
            }
            return box;
        }
    }
}
