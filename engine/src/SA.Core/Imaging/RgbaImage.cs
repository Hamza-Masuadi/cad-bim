using System;

namespace SA.Core.Imaging
{
    /// <summary>An 8-bit RGBA image, rows top to bottom, four bytes per pixel.</summary>
    public sealed class RgbaImage
    {
        public RgbaImage(int width, int height, byte[]? pixels = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Image size must be positive (" + width + " x " + height + ").");
            if ((long)width * height > Png.MaxPixels)
                throw new ArgumentException("Image of " + width + " x " + height + " pixels is above the limit of " + Png.MaxPixels + " pixels.");
            Width = width;
            Height = height;
            int size = checked(width * height * 4);
            if (pixels != null && pixels.Length != size) throw new ArgumentException("Pixel buffer holds " + pixels.Length + " bytes; " + size + " expected.");
            Pixels = pixels ?? new byte[size];
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }
        public long PixelCount => (long)Width * Height;

        public static RgbaImage Filled(int width, int height, byte r, byte g, byte b, byte a = 255)
        {
            var img = new RgbaImage(width, height);
            var p = img.Pixels;
            for (int i = 0; i < p.Length; i += 4) { p[i] = r; p[i + 1] = g; p[i + 2] = b; p[i + 3] = a; }
            return img;
        }

        public void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
        {
            int i = (y * Width + x) * 4;
            Pixels[i] = r; Pixels[i + 1] = g; Pixels[i + 2] = b; Pixels[i + 3] = a;
        }

        /// <summary>Fills the rectangle (clipped to the image).</summary>
        public void FillRect(PixelRect rect, byte r, byte g, byte b, byte a = 255)
        {
            var c = rect.Clip(Width, Height);
            if (c is null) return;
            for (int y = c.Value.Y0; y < c.Value.Y1; y++)
                for (int x = c.Value.X0; x < c.Value.X1; x++) Set(x, y, r, g, b, a);
        }
    }

    /// <summary>A pixel rectangle, X0/Y0 inclusive and X1/Y1 exclusive, Y down.</summary>
    public readonly struct PixelRect : IEquatable<PixelRect>
    {
        public PixelRect(int x0, int y0, int x1, int y1)
        {
            X0 = Math.Min(x0, x1); Y0 = Math.Min(y0, y1); X1 = Math.Max(x0, x1); Y1 = Math.Max(y0, y1);
        }

        public int X0 { get; }
        public int Y0 { get; }
        public int X1 { get; }
        public int Y1 { get; }
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
        public long Area => (long)Width * Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public PixelRect Inflate(int by) => new PixelRect(X0 - by, Y0 - by, X1 + by, Y1 + by);

        /// <summary>This rectangle cut to a width x height image, or null when nothing of it is inside.</summary>
        public PixelRect? Clip(int width, int height)
        {
            int x0 = Math.Max(0, X0), y0 = Math.Max(0, Y0), x1 = Math.Min(width, X1), y1 = Math.Min(height, Y1);
            return x1 > x0 && y1 > y0 ? new PixelRect(x0, y0, x1, y1) : (PixelRect?)null;
        }

        public bool Equals(PixelRect o) => X0 == o.X0 && Y0 == o.Y0 && X1 == o.X1 && Y1 == o.Y1;
        public override bool Equals(object? obj) => obj is PixelRect o && Equals(o);
        public override int GetHashCode() => ((X0 * 397 ^ Y0) * 397 ^ X1) * 397 ^ Y1;
        public override string ToString() => "[" + X0 + "," + Y0 + " - " + X1 + "," + Y1 + ")";
    }
}
