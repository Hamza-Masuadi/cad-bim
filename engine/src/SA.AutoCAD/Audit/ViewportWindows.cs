using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Audit.Collectors;
using SA.Core.Imaging;

namespace SA.AutoCAD.Audit
{
    internal enum Placement { None, Partial, Full }

    /// <summary>
    /// The model-space rectangle a floating viewport shows and where it sits on its layout. The rectangle is the
    /// viewport's view (ViewCenter, ViewHeight and the viewport's width/height ratio) in display coordinates; a
    /// non-rectangular clip is not followed, so such viewports are marked <see cref="Clipped"/>.
    /// </summary>
    internal sealed class ViewportWindow
    {
        private ViewportWindow() { }

        public ObjectId Id { get; private set; }
        public string Layout { get; private set; } = "";
        public bool Clipped { get; private set; }
        private Matrix3d _worldToDcs;
        private Point2d _center;
        private double _halfW, _halfH;
        private Point3d _paperCenter;
        private double _paperPerModel;
        private Viewport? _vp;

        /// <summary>Every floating viewport that is on, not perspective, with a positive size, on every layout.</summary>
        internal static List<ViewportWindow> All(Database db, Transaction tr)
        {
            var list = new List<ViewportWindow>();
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.ModelType || layout.BlockTableRecordId.IsNull || layout.BlockTableRecordId == modelId) continue;
                var overall = DrawingScan.FindOverallViewport(tr, layout);
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased || id == overall) continue;
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                    var w = From(vp, id, layout.LayoutName);
                    if (w != null) list.Add(w);
                }
            }
            return list;
        }

        private static ViewportWindow? From(Viewport vp, ObjectId id, string layout)
        {
            if (!vp.On || vp.PerspectiveOn || !(vp.Width > 0) || !(vp.Height > 0) || !(vp.ViewHeight > 0)) return null;
            var dir = vp.ViewDirection;
            if (dir.Length < 1e-12) return null;
            // Same display-coordinate construction as DrawingScan.AddViewArea.
            var dcsToWcs = Matrix3d.Rotation(-vp.TwistAngle, dir, vp.ViewTarget)
                * Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin)
                * Matrix3d.PlaneToWorld(dir);
            double halfH = vp.ViewHeight / 2.0;
            return new ViewportWindow
            {
                Id = id,
                Layout = layout,
                Clipped = vp.NonRectClipOn,
                _worldToDcs = dcsToWcs.Inverse(),
                _center = vp.ViewCenter,
                _halfH = halfH,
                _halfW = halfH * vp.Width / vp.Height,
                _paperCenter = vp.CenterPoint,
                _paperPerModel = vp.Height / vp.ViewHeight,
                _vp = vp,
            };
        }

        /// <summary>False when the layer is frozen in this viewport (VP Freeze).</summary>
        public bool ShowsLayer(ObjectId layerId) => _vp is null || !_vp.IsLayerFrozenInViewport(layerId);

        private (double X0, double Y0, double X1, double Y1) Dcs(Extents3d e)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            var a = e.MinPoint; var b = e.MaxPoint;
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d((i & 1) == 0 ? a.X : b.X, (i & 2) == 0 ? a.Y : b.Y, (i & 4) == 0 ? a.Z : b.Z).TransformBy(_worldToDcs);
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
            return (x0, y0, x1, y1);
        }

        /// <summary>How much of the WCS box the viewport's view rectangle holds.</summary>
        public Placement Classify(Extents3d e)
        {
            var (x0, y0, x1, y1) = Dcs(e);
            double l = _center.X - _halfW, r = _center.X + _halfW, bt = _center.Y - _halfH, t = _center.Y + _halfH;
            if (x1 < l || x0 > r || y1 < bt || y0 > t) return Placement.None;
            return x0 >= l && x1 <= r && y0 >= bt && y1 <= t ? Placement.Full : Placement.Partial;
        }

        /// <summary>The part of the WCS box this viewport shows, in paper units on its layout; null when it shows none.</summary>
        public WorldBox? ToPaper(Extents3d e)
        {
            var (x0, y0, x1, y1) = Dcs(e);
            x0 = Math.Max(x0, _center.X - _halfW); x1 = Math.Min(x1, _center.X + _halfW);
            y0 = Math.Max(y0, _center.Y - _halfH); y1 = Math.Min(y1, _center.Y + _halfH);
            if (x0 > x1 || y0 > y1) return null;
            double k = _paperPerModel;
            return new WorldBox(_paperCenter.X + (x0 - _center.X) * k, _paperCenter.Y + (y0 - _center.Y) * k,
                                _paperCenter.X + (x1 - _center.X) * k, _paperCenter.Y + (y1 - _center.Y) * k);
        }
    }
}
