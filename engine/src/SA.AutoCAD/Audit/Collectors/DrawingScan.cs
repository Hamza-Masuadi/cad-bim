using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using SA.AutoCAD.Ops.Common;
using SA.Core.Checks;
using SA.Core.Json;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// One read-only pass over every block table record of the drawing (model space, every layout's paper space and every
    /// non-xref block definition), opening each entity once. It builds the "entities" records and the counts the other
    /// tables need (entities per layer and per block, text and dimension styles in use, raster images, viewports per
    /// layout). Run lazily through <see cref="FactContext.Scan"/>, shared by every collector of one audit.
    /// Nothing is opened for write; no layout or space is switched.
    /// </summary>
    internal sealed class DrawingScan
    {
        internal sealed class BlockStats
        {
            public int EntityCount;
            public int HardPropertyEntities;
            public int NonZeroLayerEntities;
        }

        internal sealed class LayoutInfo
        {
            public ObjectId LayoutId;
            public ObjectId BlockId;
            public string Name = "";
            public bool IsModel;
            public int TabOrder;
            /// <summary>25.4 when the layout's PlotPaperUnits is Inches, else 1 (millimetres).</summary>
            public double PaperUnitMm = 1.0;
            public ObjectId OverallViewport = ObjectId.Null;
            public int ViewportCount;
            public string? PaperSize;
            public string? StyleSheet;
        }

        /// <summary>The model-space rectangle a floating viewport shows, in that viewport's display coordinates.</summary>
        private sealed class ViewArea
        {
            public double Ratio;
            public Matrix3d WorldToDcs;
            public Point2d Center;
            public double HalfWidth;
            public double HalfHeight;

            public bool Contains(Point3d wcs)
            {
                var p = wcs.TransformBy(WorldToDcs);
                return Math.Abs(p.X - Center.X) <= HalfWidth && Math.Abs(p.Y - Center.Y) <= HalfHeight;
            }
        }

        private sealed class PendingText
        {
            public PendingText(FactRecord record, Point3d position) { Record = record; Position = position; }
            public FactRecord Record { get; set; }
            public Point3d Position { get; }
        }

        private static readonly HashSet<string> Omit3dTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "3DSOLID", "REGION", "BODY", "SURFACE", "PLANESURFACE", "EXTRUDEDSURFACE", "LOFTEDSURFACE", "REVOLVEDSURFACE",
            "SWEPTSURFACE", "NURBSURFACE", "MESH", "ACDBPOINTCLOUD", "ACDBPOINTCLOUDEX",
        };

        private readonly Database _db;
        private readonly Transaction _tr;
        private readonly int _maxRecords;
        private readonly Func<string, bool>? _want;
        private readonly List<ViewArea> _views = new List<ViewArea>();
        private readonly List<PendingText> _pending = new List<PendingText>();
        private readonly Dictionary<ObjectId, (string Name, bool IsXref)> _blockNames = new Dictionary<ObjectId, (string, bool)>();
        private readonly List<AnnotationScale> _scales = new List<AnnotationScale>();
        private AnnotationScale? _currentScale;

        /// <summary>Kept in every entity record, for finding locations, whatever the checks read.</summary>
        private static readonly HashSet<string> LocationFields = new HashSet<string>(StringComparer.Ordinal) { "type", "space", "layout", "layer", "fieldError" };

        private DrawingScan(Database db, Transaction tr, int maxRecords, Func<string, bool>? want)
        {
            _db = db;
            _tr = tr;
            _maxRecords = maxRecords;
            _want = want;
            ModelUnitMm = FactValues.ModelUnitMm(db);
            Imperial = (int)db.Measurement == 0;
        }

        /// <summary>Records for the "entities" table, at most MaxRecords; <see cref="EntityTotal"/> counts all of them.</summary>
        public List<FactRecord> Entities { get; } = new List<FactRecord>();
        public int EntityTotal { get; private set; }
        /// <summary>Entities per layer id in model space, paper space and every non-xref block definition.</summary>
        public Dictionary<ObjectId, int> LayerCounts { get; } = new Dictionary<ObjectId, int>();
        /// <summary>Per block definition (not layouts); xref blocks are present with zero counts.</summary>
        public Dictionary<ObjectId, BlockStats> Blocks { get; } = new Dictionary<ObjectId, BlockStats>();
        public HashSet<ObjectId> TextStylesUsed { get; } = new HashSet<ObjectId>();
        public HashSet<ObjectId> DimStylesUsed { get; } = new HashSet<ObjectId>();
        public bool HasRasterImages { get; private set; }
        /// <summary>Layouts in tab order, Model first.</summary>
        public List<LayoutInfo> Layouts { get; } = new List<LayoutInfo>();
        public double? ModelUnitMm { get; }
        public bool Imperial { get; }
        /// <summary>Entities whose type-specific fields could not be read (the record keeps its common fields).</summary>
        public int FieldErrors { get; private set; }

        /// <summary>
        /// Runs the pass. Entity records stop at <paramref name="maxRecords"/> (0 builds none; the counts are always
        /// complete). <paramref name="want"/> picks the entity fields to keep, null for all; fields nobody reads that are
        /// costly to compute (flatness, polyline widths, text scales) are then not computed.
        /// </summary>
        internal static DrawingScan Run(Database db, Transaction tr, int maxRecords, Func<string, bool>? want = null)
        {
            var s = new DrawingScan(db, tr, maxRecords, want);
            s.Execute();
            return s;
        }

        private void Execute()
        {
            ReadScales();
            ReadLayouts();
            var blockTable = (BlockTable)_tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            foreach (var info in Layouts) ScanLayout(info);
            foreach (ObjectId btrId in blockTable)
            {
                if (btrId.IsErased) continue;
                var btr = (BlockTableRecord)_tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsLayout) continue;
                var stats = new BlockStats();
                Blocks[btrId] = stats;
                if (btr.IsFromExternalReference || btr.IsFromOverlayReference) continue;
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    if (!(_tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    CountUse(e);
                    stats.EntityCount++;
                    if (FactValues.HasHardProperty(e)) stats.HardPropertyEntities++;
                    if (e.Layer != "0") stats.NonZeroLayerEntities++;
                }
            }
            ResolveModelTextScales();
        }

        // ------------------------------------------------------------------ setup

        private void ReadScales()
        {
            var coll = AnnotationScales.Collection(_db);
            if (coll != null)
                foreach (ObjectContext c in coll)
                    if (c is AnnotationScale a) _scales.Add(a);
            try { _currentScale = _db.Cannoscale; } catch (Autodesk.AutoCAD.Runtime.Exception) { _currentScale = null; }
        }

        private void ReadLayouts()
        {
            var dict = (DBDictionary)_tr.GetObject(_db.LayoutDictionaryId, OpenMode.ForRead);
            var modelId = SymbolUtilityServices.GetBlockModelSpaceId(_db);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (!(_tr.GetObject(entry.Value, OpenMode.ForRead) is Layout l)) continue;
                var info = new LayoutInfo
                {
                    LayoutId = entry.Value,
                    BlockId = l.BlockTableRecordId,
                    Name = l.LayoutName,
                    IsModel = l.ModelType || l.BlockTableRecordId == modelId,
                    TabOrder = l.TabOrder,
                    PaperUnitMm = l.PlotPaperUnits == PlotPaperUnit.Inches ? 25.4 : 1.0,
                };
                info.PaperSize = l.CanonicalMediaName ?? "";
                string sheet = l.CurrentStyleSheet ?? "";
                if (sheet.Length > 0) info.StyleSheet = sheet;
                if (!info.IsModel) info.OverallViewport = FindOverallViewport(l);
                Layouts.Add(info);
            }
            Layouts.Sort((a, b) => a.IsModel != b.IsModel ? (a.IsModel ? -1 : 1) : a.TabOrder.CompareTo(b.TabOrder));
        }

        /// <summary>
        /// The layout's overall paper-space viewport. Viewport.Number is 1 for it only while its layout is current (other
        /// layouts report no number), so: the viewport numbered 1; else the first id of Layout.GetViewports(); else the
        /// first viewport in the layout block's entity order.
        /// </summary>
        private ObjectId FindOverallViewport(Layout layout) => FindOverallViewport(_tr, layout);

        /// <summary>See the instance overload; shared with the snapshot so both agree on which viewport is the sheet's own.</summary>
        internal static ObjectId FindOverallViewport(Transaction tr, Layout layout)
        {
            if (layout.BlockTableRecordId.IsNull) return ObjectId.Null;
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var vpClass = RXObject.GetClass(typeof(Viewport));
            var viewports = new List<ObjectId>();
            foreach (ObjectId id in btr)
                if (!id.IsErased && id.ObjectClass.IsDerivedFrom(vpClass)) viewports.Add(id);
            if (viewports.Count == 0) return ObjectId.Null;
            foreach (var id in viewports)
                if (tr.GetObject(id, OpenMode.ForRead) is Viewport vp && vp.Number == 1) return id;
            try
            {
                var listed = layout.GetViewports();
                if (listed != null && listed.Count > 0 && viewports.Contains(listed[0])) return listed[0];
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            return viewports[0];
        }

        // ------------------------------------------------------------------ layouts

        private void ScanLayout(LayoutInfo info)
        {
            if (info.BlockId.IsNull) return;
            var btr = (BlockTableRecord)_tr.GetObject(info.BlockId, OpenMode.ForRead);
            string space = info.IsModel ? "model" : "paper";
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || id == info.OverallViewport) continue;
                if (!(_tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                CountUse(e);
                if (e is RasterImage) HasRasterImages = true;
                if (e is Viewport vp && !info.IsModel)
                {
                    info.ViewportCount++;
                    AddViewArea(vp, info);
                }
                EntityTotal++;
                if (Entities.Count >= _maxRecords) continue;
                Entities.Add(BuildRecord(e, id, space, info));
            }
        }

        private void CountUse(Entity e)
        {
            var layerId = e.LayerId;
            LayerCounts[layerId] = LayerCounts.TryGetValue(layerId, out int n) ? n + 1 : 1;
            switch (e)
            {
                case DBText t: TextStylesUsed.Add(t.TextStyleId); break;          // TEXT and ATTDEF
                case MText m: TextStylesUsed.Add(m.TextStyleId); break;
                case MLeader ml: TextStylesUsed.Add(ml.TextStyleId); break;
                case Dimension d:
                    DimStylesUsed.Add(d.DimensionStyle);
                    TextStylesUsed.Add(d.TextStyleId);
                    break;
                case Leader ld: DimStylesUsed.Add(ld.DimensionStyle); break;
                case FeatureControlFrame f: DimStylesUsed.Add(f.DimensionStyle); break;
                case Table tb: AddTableStyles(tb); break;
                case BlockReference br:
                    foreach (ObjectId aId in br.AttributeCollection)
                    {
                        if (aId.IsErased) continue;
                        if (_tr.GetObject(aId, OpenMode.ForRead) is AttributeReference a) TextStylesUsed.Add(a.TextStyleId);
                    }
                    break;
            }
        }

        private void AddTableStyles(Table tb)
        {
            int rows = tb.Rows.Count, cols = tb.Columns.Count;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var id = tb.Cells[r, c].TextStyleId;
                    if (id.HasValue && !id.Value.IsNull) TextStylesUsed.Add(id.Value);
                }
        }

        private void AddViewArea(Viewport vp, LayoutInfo info)
        {
            if (!ModelUnitMm.HasValue) return;
            var ratio = FactValues.ViewportRatio(vp.CustomScale, ModelUnitMm.Value, info.PaperUnitMm);
            if (!ratio.HasValue || !(vp.Height > 0) || !(vp.ViewHeight > 0)) return;
            var dir = vp.ViewDirection;
            if (dir.Length < 1e-12) return;
            // Display coordinates to world: plane of the view direction, moved to the target, then the twist about it.
            var dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, dir, vp.ViewTarget)
                * Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin)
                * Matrix3d.PlaneToWorld(dir);
            double halfH = vp.ViewHeight / 2.0;
            _views.Add(new ViewArea
            {
                Ratio = ratio.Value,
                WorldToDcs = dcsToWcs.Inverse(),
                Center = vp.ViewCenter,
                HalfHeight = halfH,
                HalfWidth = halfH * vp.Width / vp.Height,
            });
        }

        // ------------------------------------------------------------------ entity records

        private bool Want(string field) => _want is null || _want(field);

        private FactRecord BuildRecord(Entity e, ObjectId id, string space, LayoutInfo info)
        {
            var r = BuildFullRecord(e, id, space, info);
            if (_want is null) return r;
            // A fresh record, so its storage is sized for the kept fields only.
            var lean = new FactRecord(r.Id);
            foreach (var kv in r.Fields)
                if (LocationFields.Contains(kv.Key) || _want(kv.Key)) lean.Set(kv.Key, kv.Value);
            if (_pending.Count > 0 && ReferenceEquals(_pending[_pending.Count - 1].Record, r)) _pending[_pending.Count - 1].Record = lean;
            return lean;
        }

        private FactRecord BuildFullRecord(Entity e, ObjectId id, string space, LayoutInfo info)
        {
            string type = (id.ObjectClass.DxfName ?? "").ToUpperInvariant();
            if (type.Length == 0) type = e.GetType().Name.ToUpperInvariant();
            var r = new FactRecord(FactValues.Handle(id));
            r.Set("type", type)
                .Set("space", space)
                .Set("layout", info.Name)
                .Set("layer", e.Layer)
                .Set("color", FactValues.EntityColor(e.Color))
                .Set("linetype", FactValues.EntityLinetype(e.Linetype))
                .Set("lineweight", FactValues.LineWeightValue(e.LineWeight))
                .Set("isText", type == "TEXT" || type == "MTEXT")
                .Set("isPolyline", e is Polyline || e is Polyline2d);
            // Fields kept from the first version of this table.
            r.Set("colorByLayer", e.Color.IsByLayer)
                .Set("linetypeByLayer", string.Equals(e.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
                .Set("lineweightByLayer", e.LineWeight == LineWeight.ByLayer);
            bool tpByLayer;
            try { tpByLayer = e.Transparency.IsByLayer; } catch (Autodesk.AutoCAD.Runtime.Exception) { tpByLayer = true; }
            r.Set("transparencyByLayer", tpByLayer);

            try
            {
                if (Want("isFlat"))
                {
                    var flat = IsFlat(e, type);
                    if (flat.HasValue) r.Set("isFlat", flat.Value);
                }
                if (Want("z") || Want("zEnd") || Want("flat")) LegacyZ(e, r);
                AddTypeFields(e, type, r, space, info);
            }
            catch (System.Exception)
            {
                // Recorded, not hidden: the record is marked and Facts.Meta["entities.fieldErrors"] counts them.
                FieldErrors++;
                r.Set("fieldError", true);
            }
            return r;
        }

        private void AddTypeFields(Entity e, string type, FactRecord r, string space, LayoutInfo info)
        {
            switch (e)
            {
                case Line l when type == "LINE":
                {
                    Point3d a = l.StartPoint, b = l.EndPoint;
                    bool swap = a.X > b.X || (a.X == b.X && a.Y > b.Y);
                    if (swap) { var t = a; a = b; b = t; }
                    r.Set("x1", a.X).Set("y1", a.Y).Set("x2", b.X).Set("y2", b.Y);
                    var off = FactValues.OffOrthoDegrees(b.X - a.X, b.Y - a.Y);
                    if (off.HasValue) r.Set("offOrthoDeg", off.Value);
                    break;
                }
                case Polyline p:
                    r.Set("isClosed", p.Closed);
                    if (Want("hasWidth")) r.Set("hasWidth", HasWidth(p));
                    break;
                case Polyline2d p2:
                    r.Set("isClosed", p2.Closed);
                    if (Want("hasWidth")) r.Set("hasWidth", HasWidth(p2));
                    break;
                case Dimension d:
                    r.Set("textOverride", d.DimensionText ?? "")
                        .Set("dimStyle", d.DimensionStyleName ?? "")
                        .Set("dimLfac", d.Dimlfac);
                    if (Want("measurement"))
                    {
                        try { r.Set("measurement", d.Measurement); }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { FieldErrors++; }
                    }
                    break;
                case Hatch h:
                    r.Set("isAssociative", h.Associative)
                        .Set("patternName", (h.PatternName ?? "").ToUpperInvariant())
                        .Set("patternScale", h.PatternScale);
                    break;
                case BlockReference br when type == "INSERT":
                {
                    var effective = BlockInfo(br.DynamicBlockTableRecord.IsNull ? br.BlockTableRecord : br.DynamicBlockTableRecord);
                    var own = BlockInfo(br.BlockTableRecord);
                    r.Set("blockName", effective.Name).Set("isXref", own.IsXref);
                    r.Set("block", effective.Name);
                    break;
                }
                case Viewport vp:
                {
                    r.Set("isLocked", vp.Locked);
                    if (ModelUnitMm.HasValue)
                    {
                        var ratio = FactValues.ViewportRatio(vp.CustomScale, ModelUnitMm.Value, info.PaperUnitMm);
                        if (ratio.HasValue) r.Set("vpScaleRatio", ratio.Value);
                    }
                    break;
                }
                case DBText t when type == "TEXT":
                    TextFields(r, t.Height, t.TextStyleName, t.Annotative == AnnotativeStates.True, t, t.Position, space);
                    break;
                case MText m when type == "MTEXT":
                    TextFields(r, m.TextHeight, m.TextStyleName, m.Annotative == AnnotativeStates.True, m, m.Location, space);
                    break;
            }
        }

        private void TextFields(FactRecord r, double height, string? style, bool annotative, Entity e, Point3d position, string space)
        {
            r.Set("textStyle", style ?? "").Set("isAnnotative", annotative);
            if (space == "paper")
            {
                r.Set("textHeight", height).Set("textScaleRatio", 1.0).Set("scaleSource", "paper");
                return;
            }
            if (annotative)
            {
                r.Set("textScaleRatio", 1.0).Set("scaleSource", "annotative");
                if (!Want("textHeight")) return;
                var scale = RepresentedScale(e);
                if (scale != null && scale.DrawingUnits > 0 && scale.PaperUnits > 0)
                    r.Set("textHeight", height * scale.PaperUnits / scale.DrawingUnits);
                return;
            }
            r.Set("textHeight", height);
            if (Want("textScaleRatio") || Want("scaleSource")) _pending.Add(new PendingText(r, position));
        }

        /// <summary>
        /// The annotation scale whose representation the entity's own height describes: the current annotation scale
        /// (CANNOSCALE) when the object carries it, else its only scale. Null when that cannot be told.
        /// </summary>
        private AnnotationScale? RepresentedScale(Entity e)
        {
            var carried = new List<AnnotationScale>();
            foreach (var s in _scales)
            {
                bool has;
                try { has = e.HasContext(s); } catch (Autodesk.AutoCAD.Runtime.Exception) { has = false; }
                if (has) carried.Add(s);
            }
            if (_currentScale != null)
            {
                var current = carried.FirstOrDefault(s => string.Equals(s.Name, _currentScale.Name, StringComparison.OrdinalIgnoreCase));
                if (current != null) return current;
            }
            return carried.Count == 1 ? carried[0] : null;
        }

        private void ResolveModelTextScales()
        {
            double? annoRatio = null;
            if (_currentScale != null && _currentScale.PaperUnits > 0 && _currentScale.DrawingUnits > 0 && ModelUnitMm.HasValue)
                annoRatio = _currentScale.DrawingUnits / _currentScale.PaperUnits * (ModelUnitMm.Value / (Imperial ? 25.4 : 1.0));
            foreach (var p in _pending)
            {
                double? found = null;
                bool conflict = false;
                foreach (var v in _views)
                {
                    if (!v.Contains(p.Position)) continue;
                    if (!found.HasValue) found = v.Ratio;
                    else if (Math.Abs(found.Value - v.Ratio) > 1e-6 * Math.Max(found.Value, v.Ratio)) { conflict = true; break; }
                }
                if (found.HasValue && !conflict)
                    p.Record.Set("textScaleRatio", found.Value).Set("scaleSource", "viewport");
                else if (annoRatio.HasValue)
                    p.Record.Set("textScaleRatio", annoRatio.Value).Set("scaleSource", "annoscale");
            }
        }

        private (string Name, bool IsXref) BlockInfo(ObjectId btrId)
        {
            if (btrId.IsNull) return ("", false);
            if (_blockNames.TryGetValue(btrId, out var known)) return known;
            var b = (BlockTableRecord)_tr.GetObject(btrId, OpenMode.ForRead);
            var info = (b.Name, b.IsFromExternalReference || b.IsFromOverlayReference);
            _blockNames[btrId] = info;
            return info;
        }

        private static bool HasWidth(Polyline p)
        {
            try { if (p.ConstantWidth > 0) return true; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { }
            for (int i = 0; i < p.NumberOfVertices; i++)
                if (p.GetStartWidthAt(i) > 0 || p.GetEndWidthAt(i) > 0) return true;
            return false;
        }

        private bool HasWidth(Polyline2d p)
        {
            if (p.DefaultStartWidth > 0 || p.DefaultEndWidth > 0) return true;
            foreach (ObjectId vId in p)
            {
                if (vId.IsErased) continue;
                if (_tr.GetObject(vId, OpenMode.ForRead) is Vertex2d v && (v.StartWidth > 0 || v.EndWidth > 0)) return true;
            }
            return false;
        }

        /// <summary>
        /// FACTS.md isFlat: every defining point within 1e-6 of Z = 0 and, where the entity has a normal, the normal is
        /// (0,0,1) within 1e-9. Null (field omitted) for 3D types: solids, surfaces, meshes, regions, 3D polylines,
        /// point clouds. Types without a specific rule use their bounding box.
        /// </summary>
        private bool? IsFlat(Entity e, string type)
        {
            if (Omit3dTypes.Contains(type)) return null;
            switch (e)
            {
                case Polyline3d _: return null;
                case PolyFaceMesh _: return null;
                case PolygonMesh _: return null;
                case Solid3d _: return null;
                case Region _: return null;
                case Line l: return FactValues.ZOk(l.StartPoint) && FactValues.ZOk(l.EndPoint) && FactValues.NormalOk(l.Normal);
                case Arc a: return FactValues.ZOk(a.Center) && FactValues.NormalOk(a.Normal);
                case Circle c: return FactValues.ZOk(c.Center) && FactValues.NormalOk(c.Normal);
                case Ellipse el: return FactValues.ZOk(el.Center) && FactValues.ZOk(el.StartPoint) && FactValues.ZOk(el.EndPoint) && FactValues.NormalOk(el.Normal);
                case Polyline p: return FactValues.ZOk(p.Elevation) && FactValues.NormalOk(p.Normal);
                case Polyline2d p2: return FactValues.ZOk(p2.Elevation) && FactValues.NormalOk(p2.Normal);
                case Spline s:
                {
                    for (int i = 0; i < s.NumControlPoints; i++) if (!FactValues.ZOk(s.GetControlPointAt(i))) return false;
                    for (int i = 0; i < s.NumFitPoints; i++) if (!FactValues.ZOk(s.GetFitPointAt(i))) return false;
                    return true;
                }
                case DBText t: return FactValues.ZOk(t.Position) && FactValues.ZOk(t.AlignmentPoint) && FactValues.NormalOk(t.Normal);
                case MText m: return FactValues.ZOk(m.Location) && FactValues.NormalOk(m.Normal);
                case BlockReference br: return FactValues.ZOk(br.Position) && FactValues.NormalOk(br.Normal);
                case Hatch h: return FactValues.ZOk(h.Elevation) && FactValues.NormalOk(h.Normal);
                case DBPoint pt: return FactValues.ZOk(pt.Position) && FactValues.NormalOk(pt.Normal);
                case RotatedDimension rd:
                    return FactValues.ZOk(rd.XLine1Point) && FactValues.ZOk(rd.XLine2Point) && FactValues.ZOk(rd.DimLinePoint) && FactValues.NormalOk(rd.Normal);
                case AlignedDimension ad:
                    return FactValues.ZOk(ad.XLine1Point) && FactValues.ZOk(ad.XLine2Point) && FactValues.ZOk(ad.DimLinePoint) && FactValues.NormalOk(ad.Normal);
                case Dimension d: return FactValues.ZOk(d.Elevation) && FactValues.ZOk(d.TextPosition) && FactValues.NormalOk(d.Normal);
                case Leader ld:
                {
                    for (int i = 0; i < ld.NumVertices; i++) if (!FactValues.ZOk(ld.VertexAt(i))) return false;
                    return FactValues.NormalOk(ld.Normal);
                }
                case Solid sd:
                {
                    for (short i = 0; i < 4; i++) if (!FactValues.ZOk(sd.GetPointAt(i))) return false;
                    return FactValues.NormalOk(sd.Normal);
                }
                case Face f:
                {
                    for (short i = 0; i < 4; i++) if (!FactValues.ZOk(f.GetVertexAt(i))) return false;
                    return true;
                }
                case Viewport vp: return FactValues.ZOk(vp.CenterPoint);
                case Ray ray: return FactValues.ZOk(ray.BasePoint) && FactValues.ZOk(ray.UnitDir.Z);
                case Xline xl: return FactValues.ZOk(xl.BasePoint) && FactValues.ZOk(xl.UnitDir.Z);
                case RasterImage ri:
                {
                    var o = ri.Orientation;
                    return FactValues.ZOk(o.Origin) && FactValues.ZOk(o.Xaxis.Z) && FactValues.ZOk(o.Yaxis.Z);
                }
                default:
                {
                    var b = e.Bounds;
                    if (!b.HasValue) return null;
                    return FactValues.ZOk(b.Value.MinPoint) && FactValues.ZOk(b.Value.MaxPoint);
                }
            }
        }

        /// <summary>The first version's z / zEnd / flat fields (exact zero), kept for readers of that version.</summary>
        private static void LegacyZ(Entity e, FactRecord r)
        {
            double? z = null, zEnd = null;
            switch (e)
            {
                case Line l: z = l.StartPoint.Z; zEnd = l.EndPoint.Z; break;
                case Polyline p: z = p.Elevation; break;
                case Polyline2d p2: z = p2.Elevation; break;
                case Arc a: z = a.Center.Z; break;
                case Circle c: z = c.Center.Z; break;
                case Ellipse el: z = el.Center.Z; break;
                case DBText t: z = t.Position.Z; break;
                case MText m: z = m.Location.Z; break;
                case BlockReference br: z = br.Position.Z; break;
                case Hatch h: z = h.Elevation; break;
                case DBPoint pt: z = pt.Position.Z; break;
                case Dimension d: z = d.Elevation; break;
            }
            if (!z.HasValue) return;
            r.Set("z", z.Value);
            if (zEnd.HasValue) r.Set("zEnd", zEnd.Value);
            r.Set("flat", z.Value == 0 && (!zEnd.HasValue || zEnd.Value == 0));
        }
    }
}
