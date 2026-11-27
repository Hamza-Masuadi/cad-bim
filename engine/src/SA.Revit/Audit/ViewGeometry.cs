using System;
using Autodesk.Revit.DB;
using SA.Core.Imaging;

namespace SA.Revit.Audit
{
    internal enum Placement { None, Partial, Full }

    /// <summary>
    /// A view's crop region as a rectangle in crop coordinates (View.CropBox: x right, y up, z towards the viewer;
    /// BoundingBoxXYZ.Transform maps them to model coordinates). A non-rectangular crop is represented by this box, its
    /// bounding rectangle.
    /// </summary>
    internal sealed class CropFrame
    {
        private CropFrame(Transform toCrop, XYZ min, XYZ max) { _toCrop = toCrop; Min = min; Max = max; }

        private readonly Transform _toCrop;
        public XYZ Min { get; }
        public XYZ Max { get; }
        public WorldBox Box => new WorldBox(Min.X, Min.Y, Max.X, Max.Y);

        /// <summary>The view's active crop, or null when cropping is off or Revit gives no crop box.</summary>
        internal static CropFrame? Of(View v)
        {
            try
            {
                if (v.IsTemplate || !v.CropBoxActive) return null;
                var cb = v.CropBox;
                if (cb is null || !(cb.Max.X > cb.Min.X) || !(cb.Max.Y > cb.Min.Y)) return null;
                return new CropFrame(cb.Transform.Inverse, cb.Min, cb.Max);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { return null; }
        }

        /// <summary>The element box (any BoundingBoxXYZ, its own transform applied) as a rectangle in crop coordinates.</summary>
        public WorldBox ToCrop(BoundingBoxXYZ b)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            var t = b.Transform ?? Transform.Identity;
            for (int i = 0; i < 8; i++)
            {
                var p = new XYZ((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
                var c = _toCrop.OfPoint(t.OfPoint(p));
                x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y);
            }
            return new WorldBox(x0, y0, x1, y1);
        }

        public Placement Classify(BoundingBoxXYZ b)
        {
            var w = ToCrop(b);
            var f = Box;
            if (!f.Intersects(w)) return Placement.None;
            return f.Contains(w) ? Placement.Full : Placement.Partial;
        }
    }

    /// <summary>Projection of model points onto a view's plane (View.Origin, RightDirection, UpDirection), in paper millimetres.</summary>
    internal sealed class ViewPlane
    {
        private readonly XYZ _origin, _right, _up;
        private readonly double _mmPerFoot;

        internal ViewPlane(View v)
        {
            _origin = v.Origin;
            _right = v.RightDirection;
            _up = v.UpDirection;
            int scale = 1;
            try { if (!(v is ViewSheet) && v.Scale > 0) scale = v.Scale; } catch (Exception) { }
            _mmPerFoot = 304.8 / scale;
            AxisAligned = Aligned(_right) && Aligned(_up);
        }

        /// <summary>False when the view's axes are not model axes; boxes of model-axis-aligned bounding boxes are then larger than the element.</summary>
        public bool AxisAligned { get; }

        private static bool Aligned(XYZ d) =>
            Math.Abs(Math.Abs(d.X) - 1) < 1e-9 || Math.Abs(Math.Abs(d.Y) - 1) < 1e-9 || Math.Abs(Math.Abs(d.Z) - 1) < 1e-9;

        public (double U, double V) Point(XYZ p)
        {
            var d = p - _origin;
            return (d.DotProduct(_right) * _mmPerFoot, d.DotProduct(_up) * _mmPerFoot);
        }

        public WorldBox Box(BoundingBoxXYZ b)
        {
            double u0 = double.MaxValue, v0 = double.MaxValue, u1 = double.MinValue, v1 = double.MinValue;
            var t = b.Transform ?? Transform.Identity;
            for (int i = 0; i < 8; i++)
            {
                var (u, v) = Point(t.OfPoint(new XYZ((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z)));
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, v); u1 = Math.Max(u1, u); v1 = Math.Max(v1, v);
            }
            return new WorldBox(u0, v0, u1, v1);
        }

        /// <summary>Direction <paramref name="d"/> in the view plane is closer to the view's up than to its right.</summary>
        public bool MostlyVertical(XYZ d) => Math.Abs(d.DotProduct(_up)) > Math.Abs(d.DotProduct(_right));
    }
}
