using System;
using SA.Core.Json;

namespace SA.Core.Imaging
{
    /// <summary>A rectangle in the target's own 2D coordinates (x right, y up).</summary>
    public readonly struct WorldBox
    {
        public WorldBox(double minX, double minY, double maxX, double maxY)
        {
            MinX = Math.Min(minX, maxX); MinY = Math.Min(minY, maxY); MaxX = Math.Max(minX, maxX); MaxY = Math.Max(minY, maxY);
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        public bool Intersects(WorldBox o) => MinX <= o.MaxX && o.MinX <= MaxX && MinY <= o.MaxY && o.MinY <= MaxY;
        public bool Contains(WorldBox o) => o.MinX >= MinX && o.MaxX <= MaxX && o.MinY >= MinY && o.MaxY <= MaxY;

        public WorldBox Union(WorldBox o) => new WorldBox(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

        public JsonArray ToJson() => new JsonArray(MinX, MinY, MaxX, MaxY);
    }

    /// <summary>
    /// The part of the target a capture shows: <see cref="Box"/> in the target's 2D coordinates, drawn into the image
    /// scaled uniformly to fit and centred (how both engines export: Revit fits the view to the pixel width, AutoCAD
    /// plots a window scaled to fit and centred). Y is flipped: image rows go down.
    /// </summary>
    public sealed class VisualFrame
    {
        public VisualFrame(WorldBox box, bool reliable, string? note = null)
        {
            if (!(box.Width > 0) || !(box.Height > 0)) throw new ArgumentException("A frame needs a positive width and height.");
            Box = box;
            Reliable = reliable;
            Note = note;
        }

        public WorldBox Box { get; }

        /// <summary>
        /// False when the engine cannot be sure the image shows exactly <see cref="Box"/> (for example a crop region whose
        /// shape it could not read). Change outside the expected region is then review, never fail.
        /// </summary>
        public bool Reliable { get; private set; }

        /// <summary>Why the frame is not reliable, or how it was found.</summary>
        public string? Note { get; private set; }

        /// <summary>Relative difference between the frame's and the image's width/height ratios.</summary>
        public double AspectMismatch(int width, int height) => Math.Abs((Box.Width / Box.Height) / (width / (double)height) - 1);

        /// <summary>
        /// For exporters that fit the frame to the whole image: when the ratios differ by more than <paramref name="tolerance"/>
        /// the image shows something else (annotation beyond a crop, a margin), so the frame is marked unreliable.
        /// </summary>
        public void CheckAspect(int width, int height, double tolerance)
        {
            double m = AspectMismatch(width, height);
            if (m <= tolerance) return;
            Reliable = false;
            Note = (Note != null ? Note + "; " : "") + "the image's width/height ratio differs from the frame's by " +
                   (m * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%, so the image does not show exactly the frame";
        }

        /// <summary>The pixel rectangle <paramref name="b"/> covers in a width x height image; null when it is wholly outside.</summary>
        public PixelRect? Project(WorldBox b, int width, int height)
        {
            double scale = Math.Min(width / Box.Width, height / Box.Height);
            double ox = (width - Box.Width * scale) / 2, oy = (height - Box.Height * scale) / 2;
            double x0 = ox + (b.MinX - Box.MinX) * scale, x1 = ox + (b.MaxX - Box.MinX) * scale;
            double y0 = height - (oy + (b.MaxY - Box.MinY) * scale), y1 = height - (oy + (b.MinY - Box.MinY) * scale);
            var r = new PixelRect((int)Math.Floor(Clamp(x0, width)), (int)Math.Floor(Clamp(y0, height)), (int)Math.Ceiling(Clamp(x1, width)), (int)Math.Ceiling(Clamp(y1, height)));
            if (x1 < 0 || y1 < 0 || x0 > width || y0 > height) return null;
            if (r.IsEmpty) r = new PixelRect(r.X0, r.Y0, Math.Max(r.X1, r.X0 + 1), Math.Max(r.Y1, r.Y0 + 1));
            return r.Clip(width, height);
        }

        /// <summary>The frame-coordinate rectangle a pixel rectangle covers (inverse of <see cref="Project"/>).</summary>
        public WorldBox Unproject(PixelRect r, int width, int height)
        {
            double scale = Math.Min(width / Box.Width, height / Box.Height);
            double ox = (width - Box.Width * scale) / 2, oy = (height - Box.Height * scale) / 2;
            return new WorldBox(Box.MinX + (r.X0 - ox) / scale, Box.MinY + (height - r.Y1 - oy) / scale,
                                Box.MinX + (r.X1 - ox) / scale, Box.MinY + (height - r.Y0 - oy) / scale);
        }

        private static double Clamp(double v, int max) => v < -1 ? -1 : v > max + 1 ? max + 1 : v;

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("box", Box.ToJson()).Add("reliable", Reliable);
            if (Note != null) o.Add("note", Note);
            return o;
        }
    }
}
