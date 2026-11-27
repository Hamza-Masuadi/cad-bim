using System;
using System.Collections.Generic;
using System.Linq;

namespace SA.Core.Imaging
{
    public sealed class DiffOptions
    {
        /// <summary>A pixel changed when any channel, composited over white, differs by more than this (0-255).</summary>
        public int Threshold { get; set; } = 32;
        /// <summary>Changed pixels closer than about this many pixels are grouped into one region.</summary>
        public int GroupDistance { get; set; } = 8;
        /// <summary>Most regions reported; the largest are kept.</summary>
        public int MaxRegions { get; set; } = 50;
        /// <summary>Pixel rectangles where change is expected (unpadded).</summary>
        public List<PixelRect> Expected { get; } = new List<PixelRect>();
        /// <summary>Pixels added around each expected rectangle.</summary>
        public int Padding { get; set; } = 16;
    }

    /// <summary>One group of changed pixels.</summary>
    public sealed class ChangeRegion
    {
        public ChangeRegion(PixelRect rect, long pixels, long unexpectedPixels)
        {
            Rect = rect; Pixels = pixels; UnexpectedPixels = unexpectedPixels;
        }

        /// <summary>Tight bounds of the changed pixels.</summary>
        public PixelRect Rect { get; }
        public long Pixels { get; }
        /// <summary>Changed pixels of this region outside every padded expected rectangle.</summary>
        public long UnexpectedPixels { get; }
    }

    public sealed class DiffResult
    {
        internal DiffResult(int width, int height) { Width = width; Height = height; }

        public int Width { get; }
        public int Height { get; }
        public long ChangedPixels { get; internal set; }
        public long UnexpectedPixels { get; internal set; }
        public double ChangedFraction => ChangedPixels / (double)((long)Width * Height);
        public double UnexpectedFraction => UnexpectedPixels / (double)((long)Width * Height);
        /// <summary>Regions, largest first, at most <see cref="DiffOptions.MaxRegions"/>.</summary>
        public List<ChangeRegion> Regions { get; } = new List<ChangeRegion>();
        /// <summary>Regions found before the cap.</summary>
        public int RegionCount { get; internal set; }
        /// <summary>The padded expected rectangles, clipped to the image.</summary>
        public List<PixelRect> ExpectedPadded { get; } = new List<PixelRect>();
        /// <summary>
        /// The after image faded to light grey, changed pixels inside the expected region in red, changed pixels outside it
        /// in magenta, expected rectangles outlined green and regions with unexpected change outlined magenta.
        /// </summary>
        public RgbaImage? Image { get; internal set; }
    }

    /// <summary>Compares two captures of the same view pixel by pixel.</summary>
    public static class PixelDiff
    {
        public static DiffResult Compare(RgbaImage before, RgbaImage after, DiffOptions? options = null, bool makeImage = true)
        {
            if (before is null) throw new ArgumentNullException(nameof(before));
            if (after is null) throw new ArgumentNullException(nameof(after));
            if (before.Width != after.Width || before.Height != after.Height)
                throw new ArgumentException("The images differ in size: " + before.Width + " x " + before.Height + " and " + after.Width + " x " + after.Height + ".");
            var o = options ?? new DiffOptions();
            if (o.Threshold < 0 || o.Threshold > 255) throw new ArgumentException("Threshold must be 0 to 255.");
            int w = before.Width, h = before.Height;
            var result = new DiffResult(w, h);

            // Expected mask from a 2D difference array: cost is rectangles + pixels whatever the rectangles' sizes.
            var expected = new bool[(long)w * h];
            if (o.Expected.Count > 0)
            {
                var acc = new int[(w + 1) * (h + 1)];
                foreach (var r in o.Expected)
                {
                    var c = r.Inflate(Math.Max(0, o.Padding)).Clip(w, h);
                    if (c is null) continue;
                    result.ExpectedPadded.Add(c.Value);
                    acc[c.Value.Y0 * (w + 1) + c.Value.X0]++;
                    acc[c.Value.Y0 * (w + 1) + c.Value.X1]--;
                    acc[c.Value.Y1 * (w + 1) + c.Value.X0]--;
                    acc[c.Value.Y1 * (w + 1) + c.Value.X1]++;
                }
                for (int y = 0; y <= h; y++)
                    for (int x = 0; x <= w; x++)
                    {
                        int i = y * (w + 1) + x;
                        if (x > 0) acc[i] += acc[i - 1];
                        if (y > 0) acc[i] += acc[i - (w + 1)];
                        if (x > 0 && y > 0) acc[i] -= acc[i - (w + 1) - 1];
                        if (x < w && y < h) expected[(long)y * w + x] = acc[i] > 0;
                    }
            }

            var changed = new bool[(long)w * h];
            var a = before.Pixels;
            var b = after.Pixels;
            for (long p = 0; p < changed.LongLength; p++)
            {
                long i = p * 4;
                int d = Math.Max(Math.Abs(Over(a, i, 0) - Over(b, i, 0)), Math.Max(Math.Abs(Over(a, i, 1) - Over(b, i, 1)), Math.Abs(Over(a, i, 2) - Over(b, i, 2))));
                if (d <= o.Threshold) continue;
                changed[p] = true;
                result.ChangedPixels++;
                if (!expected[p]) result.UnexpectedPixels++;
            }

            Group(result, changed, expected, w, h, Math.Max(1, o.GroupDistance), Math.Max(1, o.MaxRegions));
            if (makeImage) result.Image = Draw(after, changed, expected, result);
            return result;
        }

        /// <summary>Channel <paramref name="c"/> of the pixel at byte <paramref name="i"/>, composited over white.</summary>
        private static int Over(byte[] px, long i, int c)
        {
            int alpha = px[i + 3];
            return (px[i + c] * alpha + 255 * (255 - alpha) + 127) / 255;
        }

        /// <summary>
        /// Regions: changed pixels are binned into cells of <paramref name="cell"/> pixels; 8-connected cells form one
        /// region, so marks closer than about one cell join. Each region keeps the tight bounds of its pixels.
        /// </summary>
        private static void Group(DiffResult result, bool[] changed, bool[] expected, int w, int h, int cell, int max)
        {
            if (result.ChangedPixels == 0) return;
            int cw = (w + cell - 1) / cell, ch = (h + cell - 1) / cell;
            var count = new int[cw * ch];
            var unexpected = new int[cw * ch];
            var minX = new int[cw * ch]; var minY = new int[cw * ch]; var maxX = new int[cw * ch]; var maxY = new int[cw * ch];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long p = (long)y * w + x;
                    if (!changed[p]) continue;
                    int c = (y / cell) * cw + x / cell;
                    if (count[c] == 0) { minX[c] = x; minY[c] = y; maxX[c] = x; maxY[c] = y; }
                    else
                    {
                        if (x < minX[c]) minX[c] = x;
                        if (x > maxX[c]) maxX[c] = x;
                        if (y < minY[c]) minY[c] = y;
                        if (y > maxY[c]) maxY[c] = y;
                    }
                    count[c]++;
                    if (!expected[p]) unexpected[c]++;
                }
            var seen = new bool[cw * ch];
            var regions = new List<ChangeRegion>();
            var stack = new Stack<int>();
            for (int start = 0; start < count.Length; start++)
            {
                if (count[start] == 0 || seen[start]) continue;
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                long pixels = 0, outside = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    pixels += count[c];
                    outside += unexpected[c];
                    x0 = Math.Min(x0, minX[c]); y0 = Math.Min(y0, minY[c]); x1 = Math.Max(x1, maxX[c]); y1 = Math.Max(y1, maxY[c]);
                    int cx = c % cw, cy = c / cw;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                            int n = ny * cw + nx;
                            if (count[n] == 0 || seen[n]) continue;
                            seen[n] = true;
                            stack.Push(n);
                        }
                }
                regions.Add(new ChangeRegion(new PixelRect(x0, y0, x1 + 1, y1 + 1), pixels, outside));
            }
            result.RegionCount = regions.Count;
            result.Regions.AddRange(regions.OrderByDescending(r => r.UnexpectedPixels > 0).ThenByDescending(r => r.Pixels).Take(max));
        }

        private static RgbaImage Draw(RgbaImage after, bool[] changed, bool[] expected, DiffResult r)
        {
            int w = after.Width, h = after.Height;
            var img = new RgbaImage(w, h);
            var src = after.Pixels;
            var dst = img.Pixels;
            for (long p = 0; p < changed.LongLength; p++)
            {
                long i = p * 4;
                if (changed[p])
                {
                    if (expected[p]) { dst[i] = 220; dst[i + 1] = 30; dst[i + 2] = 30; }
                    else { dst[i] = 210; dst[i + 1] = 0; dst[i + 2] = 210; }
                }
                else
                {
                    int lum = (Over(src, i, 0) * 299 + Over(src, i, 1) * 587 + Over(src, i, 2) * 114) / 1000;
                    byte v = (byte)(255 - (255 - lum) * 3 / 10);
                    dst[i] = v; dst[i + 1] = v; dst[i + 2] = v;
                }
                dst[i + 3] = 255;
            }
            foreach (var e in r.ExpectedPadded) Outline(img, e, 0, 160, 0, 1);
            foreach (var g in r.Regions.Where(g => g.UnexpectedPixels > 0)) Outline(img, g.Rect.Inflate(3), 210, 0, 210, 2);
            return img;
        }

        private static void Outline(RgbaImage img, PixelRect rect, byte cr, byte cg, byte cb, int thickness)
        {
            for (int t = 0; t < thickness; t++)
            {
                var q = rect.Inflate(-t);
                if (q.IsEmpty) return;
                img.FillRect(new PixelRect(q.X0, q.Y0, q.X1, q.Y0 + 1), cr, cg, cb);
                img.FillRect(new PixelRect(q.X0, q.Y1 - 1, q.X1, q.Y1), cr, cg, cb);
                img.FillRect(new PixelRect(q.X0, q.Y0, q.X0 + 1, q.Y1), cr, cg, cb);
                img.FillRect(new PixelRect(q.X1 - 1, q.Y0, q.X1, q.Y1), cr, cg, cb);
            }
        }
    }
}
