using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SA.Core.Checks;
using SA.Core.Imaging;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Standards;
using SA.Core.Validation;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>The PNG codec, the pixel diff, the visual verdict, verify.visual and the layout evaluators.</summary>
    public class VisualCheckTests
    {
        // ----- PNG files written by hand, to the PNG specification -----

        private static readonly int[,] Adam7 = { { 0, 0, 8, 8 }, { 4, 0, 8, 8 }, { 0, 4, 4, 8 }, { 2, 0, 4, 4 }, { 0, 2, 2, 4 }, { 1, 0, 2, 2 }, { 0, 1, 1, 2 } };

        private static int Channels(int type) => type == 0 || type == 3 ? 1 : type == 2 ? 3 : type == 4 ? 2 : 4;

        /// <summary>Sample value of channel <paramref name="c"/> at (x, y), within the depth's range.</summary>
        private static int Sample(int x, int y, int c, int depth) => (x * 7 + y * 13 + c * 29 + x * y) % (1 << depth);

        private static byte[] Chunk(string type, byte[] body)
        {
            var o = new MemoryStream();
            void U32(uint v) { o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }
            U32((uint)body.Length);
            var tb = Encoding.ASCII.GetBytes(type);
            o.Write(tb, 0, 4);
            o.Write(body, 0, body.Length);
            var crcInput = tb.Concat(body).ToArray();
            U32(Crc(crcInput));
            return o.ToArray();
        }

        private static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFFu;
        }

        private static byte[] Header(int w, int h, int depth, int type, int interlace) => new byte[]
        {
            (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h,
            (byte)depth, (byte)type, 0, 0, (byte)interlace,
        };

        /// <summary>Packed, filtered rows of a (sub)image; the filter cycles through all five types.</summary>
        private static void Rows(MemoryStream raw, int w, int h, int depth, int type, Func<int, int, int, int> sample)
        {
            int ch = Channels(type), bits = ch * depth, rowBytes = (w * bits + 7) / 8, bpp = Math.Max(1, bits / 8);
            var prev = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                var row = new byte[rowBytes];
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        int v = sample(x, y, c), i = x * ch + c;
                        if (depth == 16) { row[i * 2] = (byte)(v >> 8); row[i * 2 + 1] = (byte)v; }
                        else if (depth == 8) row[i] = (byte)v;
                        else
                        {
                            int perByte = 8 / depth;
                            row[i / perByte] |= (byte)(v << (8 - depth * (i % perByte + 1)));
                        }
                    }
                int filter = y % 5;
                var f = new byte[rowBytes];
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], cc = i >= bpp ? prev[i - bpp] : 0;
                    int pred = filter switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, cc),
                        _ => 0,
                    };
                    f[i] = (byte)(row[i] - pred);
                }
                raw.WriteByte((byte)filter);
                raw.Write(f, 0, rowBytes);
                prev = row;
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static byte[] Zlib(byte[] raw)
        {
            var o = new MemoryStream();
            using (var z = new ZLibStream(o, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            return o.ToArray();
        }

        private static byte[] MakePng(int w, int h, int depth, int type, bool interlace, byte[]? plte = null, byte[]? trns = null, int idatSplits = 1)
        {
            var raw = new MemoryStream();
            if (!interlace) Rows(raw, w, h, depth, type, (x, y, c) => Sample(x, y, c, depth));
            else
                for (int p = 0; p < 7; p++)
                {
                    int x0 = Adam7[p, 0], y0 = Adam7[p, 1], dx = Adam7[p, 2], dy = Adam7[p, 3];
                    int pw = w > x0 ? (w - x0 + dx - 1) / dx : 0, ph = h > y0 ? (h - y0 + dy - 1) / dy : 0;
                    if (pw == 0 || ph == 0) continue;
                    Rows(raw, pw, ph, depth, type, (x, y, c) => Sample(x0 + x * dx, y0 + y * dy, c, depth));
                }
            var z = Zlib(raw.ToArray());
            var o = new MemoryStream();
            o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            o.Write(Chunk("IHDR", Header(w, h, depth, type, interlace ? 1 : 0)));
            o.Write(Chunk("tEXt", Encoding.ASCII.GetBytes("Comment\0ancillary chunks are skipped")));
            if (plte != null) o.Write(Chunk("PLTE", plte));
            if (trns != null) o.Write(Chunk("tRNS", trns));
            int step = (z.Length + idatSplits - 1) / idatSplits;
            for (int i = 0; i < z.Length; i += step) o.Write(Chunk("IDAT", z.Skip(i).Take(step).ToArray()));
            o.Write(Chunk("IEND", Array.Empty<byte>()));
            return o.ToArray();
        }

        private static byte To8(int v, int depth) =>
            depth == 16 ? (byte)Math.Round(v * 255.0 / 65535) : (byte)(v * 255 / ((1 << depth) - 1));

        private static byte[] Palette(int entries)
        {
            var p = new byte[entries * 3];
            for (int i = 0; i < entries; i++) { p[i * 3] = (byte)(i * 17); p[i * 3 + 1] = (byte)(255 - i * 9); p[i * 3 + 2] = (byte)(i * 31); }
            return p;
        }

        private static void AssertPixel(RgbaImage img, int x, int y, byte r, byte g, byte b, byte a)
        {
            int o = (y * img.Width + x) * 4;
            var got = img.Pixels.Skip(o).Take(4).ToArray();
            Assert.True(got.SequenceEqual(new[] { r, g, b, a }), "pixel " + x + "," + y + ": got " + string.Join(",", got) + ", expected " + r + "," + g + "," + b + "," + a);
        }

        [Theory]
        [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 4)] [InlineData(0, 8)] [InlineData(0, 16)]
        [InlineData(2, 8)] [InlineData(2, 16)]
        [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 4)] [InlineData(3, 8)]
        [InlineData(4, 8)] [InlineData(4, 16)]
        [InlineData(6, 8)] [InlineData(6, 16)]
        public void PngDecodesEveryColourTypeAndDepthPlainAndInterlaced(int type, int depth)
        {
            foreach (bool interlace in new[] { false, true })
            {
                const int w = 13, h = 11;
                int entries = 1 << Math.Min(depth, 8);
                var plte = type == 3 ? Palette(entries) : null;
                var img = Png.Decode(MakePng(w, h, depth, type, interlace, plte, idatSplits: 3));
                Assert.Equal(w, img.Width);
                Assert.Equal(h, img.Height);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int s0 = Sample(x, y, 0, depth);
                        switch (type)
                        {
                            case 0: { byte g = To8(s0, depth); AssertPixel(img, x, y, g, g, g, 255); break; }
                            case 2: AssertPixel(img, x, y, To8(s0, depth), To8(Sample(x, y, 1, depth), depth), To8(Sample(x, y, 2, depth), depth), 255); break;
                            case 3: AssertPixel(img, x, y, plte![s0 * 3], plte[s0 * 3 + 1], plte[s0 * 3 + 2], 255); break;
                            case 4: { byte g = To8(s0, depth); AssertPixel(img, x, y, g, g, g, To8(Sample(x, y, 1, depth), depth)); break; }
                            default:
                                AssertPixel(img, x, y, To8(s0, depth), To8(Sample(x, y, 1, depth), depth), To8(Sample(x, y, 2, depth), depth), To8(Sample(x, y, 3, depth), depth));
                                break;
                        }
                    }
            }
        }

        [Fact]
        public void PngTransparencyChunksApply()
        {
            // Greyscale key colour: sample 5 (of 0..15) is transparent.
            var g = Png.Decode(MakePng(9, 7, 4, 0, false, trns: new byte[] { 0, 5 }));
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 9; x++)
                    Assert.Equal(Sample(x, y, 0, 4) == 5 ? 0 : 255, g.Pixels[(y * 9 + x) * 4 + 3]);

            // Truecolour key colour: the colour at (0, 0) is transparent wherever it occurs.
            var c = Png.Decode(MakePng(9, 7, 8, 2, true, trns: new byte[] { 0, (byte)Sample(0, 0, 0, 8), 0, (byte)Sample(0, 0, 1, 8), 0, (byte)Sample(0, 0, 2, 8) }));
            Assert.Equal(0, c.Pixels[3]);
            Assert.Equal(255, c.Pixels[(1 * 9 + 1) * 4 + 3]);

            // Palette alpha: entries 0-2 get alpha 10, 20, 30; later entries are opaque.
            var p = Png.Decode(MakePng(9, 7, 4, 3, false, Palette(16), new byte[] { 10, 20, 30 }));
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 9; x++)
                {
                    int idx = Sample(x, y, 0, 4);
                    Assert.Equal(idx < 3 ? 10 * (idx + 1) : 255, p.Pixels[(y * 9 + x) * 4 + 3]);
                }
        }

        [Fact]
        public void PngEncoderRoundTrips()
        {
            var rnd = new Random(7);
            foreach (var (w, h) in new[] { (1, 1), (37, 23), (300, 2) })
            {
                var img = new RgbaImage(w, h);
                rnd.NextBytes(img.Pixels);
                // Smooth areas too, so every filter has something to win.
                for (int x = 0; x < w; x++) img.Set(x, 0, (byte)x, (byte)(x * 2), (byte)(x * 3));
                var back = Png.Decode(Png.Encode(img));
                Assert.Equal(w, back.Width);
                Assert.Equal(h, back.Height);
                Assert.Equal(img.Pixels, back.Pixels);
            }

            string path = Path.Combine(Path.GetTempPath(), "sa-visual-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var flat = RgbaImage.Filled(64, 48, 255, 255, 255);
                Png.WriteFile(path, flat);
                Assert.Equal(flat.Pixels, Png.ReadFile(path).Pixels);
                // A flat image compresses well.
                Assert.True(new FileInfo(path).Length < 400, new FileInfo(path).Length + " bytes");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PngRejectsBadFiles()
        {
            var good = MakePng(8, 8, 8, 6, false);
            Assert.Equal(8, Png.Decode(good).Width);

            void Bad(byte[] data, string fragment)
            {
                var e = Assert.Throws<PngFormatException>(() => Png.Decode(data));
                Assert.Contains(fragment, e.Message);
            }

            Bad(new byte[] { 1, 2, 3 }, "shorter than the signature");
            Bad(Encoding.ASCII.GetBytes("GIF89a-not-a-png-at-all"), "signature");
            Bad(good.Take(good.Length - 20).ToArray(), "Truncated");

            var crc = (byte[])good.Clone();
            crc[8 + 8 + 2] ^= 0xFF;
            Bad(crc, "CRC mismatch");

            Bad(Rebuild(Header(4, 4, 4, 2, 0)), "colour type 2 with bit depth 4");
            Bad(Rebuild(Header(4, 4, 8, 6, 2)), "interlace method 2");
            Bad(Rebuild(Header(0, 4, 8, 6, 0)), "image size");
            Bad(Rebuild(Header(10000, 10000, 8, 6, 0)), "above the limit");

            // Indexed image without a palette, and a palette index past the palette's end.
            Bad(MakePng(4, 4, 8, 3, false), "without a PLTE");
            Bad(MakePng(4, 4, 8, 3, false, Palette(4)), "beyond the 4-entry palette");
            Bad(MakePng(4, 4, 8, 6, false, trns: new byte[] { 1 }), "already has alpha");

            // Image data that is short of what the header promises.
            var o = new MemoryStream();
            o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            o.Write(Chunk("IHDR", Header(16, 16, 8, 6, 0)));
            o.Write(Chunk("IDAT", Zlib(new byte[10])));
            o.Write(Chunk("IEND", Array.Empty<byte>()));
            Assert.Throws<PngFormatException>(() => Png.Decode(o.ToArray()));

            // No IEND.
            Bad(good.Take(good.Length - 12).ToArray(), "no IEND");
        }

        private static byte[] Rebuild(byte[] header)
        {
            var o = new MemoryStream();
            o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            o.Write(Chunk("IHDR", header));
            o.Write(Chunk("IDAT", Zlib(new byte[] { 0 })));
            o.Write(Chunk("IEND", Array.Empty<byte>()));
            return o.ToArray();
        }

        // ----- Pixel diff -----

        [Fact]
        public void IdenticalImagesHaveNoChange()
        {
            var a = RgbaImage.Filled(50, 40, 255, 255, 255);
            var d = PixelDiff.Compare(a, RgbaImage.Filled(50, 40, 255, 255, 255));
            Assert.Equal(0, d.ChangedPixels);
            Assert.Empty(d.Regions);
            Assert.NotNull(d.Image);
            Assert.Throws<ArgumentException>(() => PixelDiff.Compare(a, RgbaImage.Filled(40, 50, 255, 255, 255)));
        }

        [Fact]
        public void DiffSeparatesExpectedFromUnexpectedChange()
        {
            var before = RgbaImage.Filled(200, 100, 255, 255, 255);
            var after = RgbaImage.Filled(200, 100, 255, 255, 255);
            after.FillRect(new PixelRect(20, 20, 40, 30), 0, 0, 0);       // 200 px, expected
            after.FillRect(new PixelRect(150, 70, 160, 75), 255, 0, 0);   // 50 px, outside
            after.Set(100, 50, 250, 250, 250);                            // below the threshold

            var o = new DiffOptions { Padding = 4 };
            o.Expected.Add(new PixelRect(22, 22, 38, 28));
            var d = PixelDiff.Compare(before, after, o);
            Assert.Equal(250, d.ChangedPixels);
            Assert.Equal(50, d.UnexpectedPixels);
            Assert.Equal(new PixelRect(18, 18, 42, 32), d.ExpectedPadded.Single());
            Assert.Equal(2, d.RegionCount);
            // The region with unexpected change comes first.
            Assert.Equal(new PixelRect(150, 70, 160, 75), d.Regions[0].Rect);
            Assert.Equal(50, d.Regions[0].UnexpectedPixels);
            Assert.Equal(new PixelRect(20, 20, 40, 30), d.Regions[1].Rect);
            Assert.Equal(0, d.Regions[1].UnexpectedPixels);
            Assert.Equal(250.0 / 20000, d.ChangedFraction, 9);

            // Magenta marks unexpected change, red expected change.
            var img = d.Image!;
            AssertPixel(img, 155, 72, 210, 0, 210, 255);
            AssertPixel(img, 30, 25, 220, 30, 30, 255);
            // The expected rectangle is outlined green; unchanged white stays white.
            AssertPixel(img, 18, 25, 0, 160, 0, 255);
            AssertPixel(img, 100, 10, 255, 255, 255, 255);
        }

        [Fact]
        public void NearbyChangeJoinsOneRegionAndTransparencyIsSeenOverWhite()
        {
            var before = RgbaImage.Filled(100, 100, 255, 255, 255);
            var after = RgbaImage.Filled(100, 100, 255, 255, 255);
            after.FillRect(new PixelRect(10, 10, 12, 12), 0, 0, 0);
            after.FillRect(new PixelRect(16, 10, 18, 12), 0, 0, 0);
            after.FillRect(new PixelRect(80, 80, 82, 82), 0, 0, 0);
            // A fully transparent black pixel is white once composited: no change.
            after.Set(50, 50, 0, 0, 0, 0);
            var d = PixelDiff.Compare(before, after, new DiffOptions { GroupDistance = 8 });
            Assert.Equal(12, d.ChangedPixels);
            Assert.Equal(2, d.RegionCount);
            Assert.Contains(d.Regions, r => r.Rect.Equals(new PixelRect(10, 10, 18, 12)));

            var capped = PixelDiff.Compare(before, after, new DiffOptions { GroupDistance = 1, MaxRegions = 1 }, makeImage: false);
            Assert.Equal(3, capped.RegionCount);
            Assert.Single(capped.Regions);
            Assert.Null(capped.Image);
        }

        // ----- Frame -----

        [Fact]
        public void FrameProjectsAndUnprojectsWithYFlipped()
        {
            var f = new VisualFrame(new WorldBox(0, 0, 2000, 1000), reliable: true);
            var px = f.Project(new WorldBox(0, 900, 100, 1000), 200, 100)!.Value;
            Assert.Equal(new PixelRect(0, 0, 10, 10), px);
            var back = f.Unproject(new PixelRect(190, 90, 200, 100), 200, 100);
            Assert.Equal(1900, back.MinX, 6);
            Assert.Equal(0, back.MinY, 6);
            Assert.Equal(2000, back.MaxX, 6);
            Assert.Equal(100, back.MaxY, 6);
            Assert.Null(f.Project(new WorldBox(5000, 5000, 6000, 6000), 200, 100));

            // A frame narrower than the image is centred: 1000 x 1000 in 200 x 100 spans pixels 50-150.
            var sq = new VisualFrame(new WorldBox(0, 0, 1000, 1000), reliable: true);
            Assert.Equal(new PixelRect(50, 0, 150, 100), sq.Project(new WorldBox(0, 0, 1000, 1000), 200, 100));
            sq.CheckAspect(200, 100, 0.02);
            Assert.False(sq.Reliable);
            Assert.NotNull(sq.Note);
            f.CheckAspect(200, 100, 0.02);
            Assert.True(f.Reliable);
        }

        // ----- The verdict -----

        private sealed class Pair : IDisposable
        {
            public Pair()
            {
                Dir = Path.Combine(Path.GetTempPath(), "sa-visual-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Dir);
            }

            public string Dir { get; }

            public VisualInput Input(string specJson, RgbaImage? before, RgbaImage? after)
            {
                var input = new VisualInput(VisualSpec.Parse(JsonValue.Parse(specJson)), Dir);
                if (before != null) { input.BeforePath = Path.Combine(Dir, VisualCheck.BeforeFile); Png.WriteFile(input.BeforePath, before); }
                if (after != null) { input.AfterPath = Path.Combine(Dir, VisualCheck.AfterFile); Png.WriteFile(input.AfterPath, after); }
                return input;
            }

            public void Dispose()
            {
                foreach (var f in new[] { VisualCheck.BeforeFile, VisualCheck.AfterFile, VisualCheck.DiffFile })
                    File.Delete(Path.Combine(Dir, f));
                Directory.Delete(Dir);
            }
        }

        private static RgbaImage White() => RgbaImage.Filled(200, 100, 255, 255, 255);

        private static RgbaImage WithBox(PixelRect r)
        {
            var i = White();
            i.FillRect(r, 0, 0, 0);
            return i;
        }

        [Fact]
        public void ChangeInsideTheTouchedExtentsPasses()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""model"" }", White(), WithBox(new PixelRect(10, 10, 20, 20)));
            input.Frame = new VisualFrame(new WorldBox(0, 0, 2000, 1000), true);
            input.Expected.Add(new WorldBox(100, 800, 200, 900));   // pixels 10-20, 10-20
            input.CreatedVisible = 1;
            var o = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Pass, o.Check.Status);
            Assert.Equal(VisualCheck.CheckId, o.Check.Id);
            Assert.Equal(Severity.Warning, o.Check.Severity);
            Assert.Equal(new[] { "visual:before", "visual:after", "visual:diff" }, o.Artifacts.Select(a => a.Target));
            Assert.True(File.Exists(Path.Combine(p.Dir, VisualCheck.DiffFile)));
            Assert.Equal(100, o.Summary["changedPixels"]!.AsDouble());
            Assert.Equal(0, o.Summary["unexpectedPixels"]!.AsDouble());
            Assert.Equal("pass", o.Summary.GetStringOrNull("status"));

            var report = new Report("j1", JobStatus.Committed, RunMode.Commit);
            o.AddTo(report);
            Assert.Single(report.Checks);
            Assert.Equal(3, report.Artifacts.Count);
            Assert.Equal("pass", report.ToJson()["visual"]!.AsObject().GetStringOrNull("status"));
        }

        [Fact]
        public void ChangeOutsideTheTouchedExtentsFailsWhenTheFrameIsReliable()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""model"", ""padding"": 2 }", White(), WithBox(new PixelRect(150, 50, 170, 70)));
            input.Frame = new VisualFrame(new WorldBox(0, 0, 2000, 1000), true);
            input.Expected.Add(new WorldBox(100, 800, 200, 900));
            var o = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Fail, o.Check.Status);
            var f = Assert.Single(o.Check.Findings);
            Assert.Equal(400, f.Data["unexpectedPixels"]!.AsDouble());
            Assert.Equal(new JsonArray(150, 50, 170, 70).ToJson(), f.Data["box"]!.ToJson());
            // The box in drawing units: x 1500-1700, y flipped to 300-500.
            Assert.Equal(new JsonArray(1500.0, 300.0, 1700.0, 500.0).ToJson(), f.Data["worldBox"]!.ToJson());
            Assert.False(f.Data["review"]!.AsBool());
        }

        [Fact]
        public void UnplacedChangeIsReviewNotFail()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""activeView"" }", White(), WithBox(new PixelRect(150, 50, 170, 70)));
            input.Frame = new VisualFrame(new WorldBox(0, 0, 2000, 1000), true);
            input.AllProjected = false;
            var o = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Review, o.Check.Status);
            Assert.True(o.Check.IsManualReview);
            Assert.Contains("visual-diff.png", o.Check.ReviewInstructions);

            // No frame at all: also review.
            input = p.Input(@"{ ""target"": ""activeView"" }", White(), WithBox(new PixelRect(150, 50, 170, 70)));
            Assert.Equal(CheckStatus.Review, VisualCheck.Run(input).Check.Status);

            // A frame whose aspect does not match the image: review.
            input = p.Input(@"{ ""target"": ""activeView"" }", White(), WithBox(new PixelRect(150, 50, 170, 70)));
            input.Frame = new VisualFrame(new WorldBox(0, 0, 1000, 1000), true);
            var sq = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Review, sq.Check.Status);
            Assert.False(sq.Summary["frame"]!.AsObject()["reliable"]!.AsBool());
        }

        [Fact]
        public void ExpectChangeFalseFailsOnAnyChangeAboveTheLimit()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""activeView"", ""expectChange"": false, ""maxUnexpectedChange"": 0.001 }", White(), WithBox(new PixelRect(0, 0, 5, 5)));
            input.Expected.Add(new WorldBox(0, 0, 2000, 1000));
            Assert.Equal(CheckStatus.Fail, VisualCheck.Run(input).Check.Status);

            // 25 of 20000 pixels is 0.125%, under a 0.2% limit.
            input = p.Input(@"{ ""target"": ""activeView"", ""expectChange"": false, ""maxUnexpectedChange"": 0.002 }", White(), WithBox(new PixelRect(0, 0, 5, 5)));
            Assert.Equal(CheckStatus.Pass, VisualCheck.Run(input).Check.Status);
        }

        [Fact]
        public void NothingVisiblyChangedFailsWhenTheJobCreatedVisibleElements()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""activeView"" }", White(), White());
            input.CreatedVisible = 3;
            var o = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Fail, o.Check.Status);
            Assert.Contains("created 3 element(s)", o.Check.Message);

            input = p.Input(@"{ ""target"": ""activeView"", ""expectChange"": true }", White(), White());
            Assert.Equal(CheckStatus.Fail, VisualCheck.Run(input).Check.Status);

            // Created nothing visible, nothing changed: pass.
            input = p.Input(@"{ ""target"": ""activeView"" }", White(), White());
            Assert.Equal(CheckStatus.Pass, VisualCheck.Run(input).Check.Status);
        }

        [Fact]
        public void MissingOrUnusableCapturesAreIncomplete()
        {
            using var p = new Pair();
            var input = p.Input(@"{ ""target"": ""activeView"" }", White(), null);
            input.AfterError = "plot failed";
            var o = VisualCheck.Run(input);
            Assert.Equal(CheckStatus.Incomplete, o.Check.Status);
            Assert.Contains("plot failed", o.Check.IncompleteReason);
            Assert.Equal(new[] { "visual:before" }, o.Artifacts.Select(a => a.Target));

            input = p.Input(@"{ ""target"": ""activeView"" }", White(), RgbaImage.Filled(100, 100, 255, 255, 255));
            Assert.Contains("differ in size", VisualCheck.Run(input).Check.IncompleteReason);

            input = p.Input(@"{ ""target"": ""activeView"" }", White(), White());
            File.WriteAllText(input.AfterPath!, "not a png");
            Assert.Contains("could not be read as PNG", VisualCheck.Run(input).Check.IncompleteReason);

            input = p.Input(@"{ ""target"": ""activeView"" }", null, null);
            input.IncompleteReason = "a rehearsal cannot plot";
            Assert.Equal("a rehearsal cannot plot", VisualCheck.Run(input).Check.IncompleteReason);
        }

        // ----- verify.visual -----

        [Fact]
        public void VisualSpecValidates()
        {
            var s = VisualSpec.Parse(JsonValue.Parse(@"{ ""target"": ""layout:A101"", ""window"": [[0, 0], [100, 50]], ""maxUnexpectedChange"": 0.01, ""threshold"": 10, ""padding"": 0, ""expectChange"": null }"));
            Assert.Equal("layout:A101", s.Target);
            Assert.Equal(0.01, s.MaxUnexpectedChange);
            Assert.Equal(10, s.Threshold);
            Assert.Equal(0, s.Padding);
            Assert.Null(s.ExpectChange);
            Assert.Equal(VisualSpec.DefaultThreshold, VisualSpec.Parse(JsonValue.Parse(@"{ ""target"": ""activeView"" }")).Threshold);

            var issues = new List<Issue>();
            Assert.Null(VisualSpec.Validate(JsonValue.Parse(@"{ ""target"": """", ""pixelWidth"": 10, ""maxUnexpectedChange"": 2, ""threshold"": 300,
                ""padding"": -1, ""expectChange"": ""yes"", ""window"": [[0, 0], [0, 5]], ""colour"": 1 }"), "$.verify.visual", issues));
            Assert.Equal(new[] { "$.verify.visual.colour", "$.verify.visual.target", "$.verify.visual.window", "$.verify.visual.pixelWidth",
                "$.verify.visual.maxUnexpectedChange", "$.verify.visual.threshold", "$.verify.visual.padding", "$.verify.visual.expectChange" }, issues.Select(i => i.Path));
            issues.Clear();
            Assert.Null(VisualSpec.Validate(new JsonString("activeView"), "$.verify.visual", issues));
            Assert.Equal(IssueCodes.Type, Assert.Single(issues).Code);
            Assert.Null(VisualSpec.Validate(null, "$.verify.visual", issues));
        }

        [Fact]
        public void JobValidatorReadsVerifyVisual()
        {
            var j = (JsonObject)JsonValue.Parse(JobTests.GoodAcadJob);
            j["verify"]!.AsObject().Set("visual", JsonValue.Parse(@"{ ""target"": ""model"", ""maxUnexpectedChange"": 0.005 }"));
            var v = JobValidator.Validate(j.ToJson(), OpRegistry.Default);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal("model", v.Job!.Verify.Visual!.Target);
            Assert.Equal(0.005, v.Job.Verify.Visual.MaxUnexpectedChange);

            j["verify"]!.AsObject().Set("visual", JsonValue.Parse(@"{ ""threshold"": 1 }"));
            v = JobValidator.Validate(j.ToJson(), OpRegistry.Default);
            Assert.False(v.Ok);
            Assert.Contains(v.Issues, i => i.Path == "$.verify.visual.target" && i.Code == IssueCodes.Required);

            Assert.Null(JobValidator.Validate(JobTests.GoodAcadJob, OpRegistry.Default).Job!.Verify.Visual);
        }

        [Theory]
        [InlineData(@"{ ""jobId"": ""2026-11-28T09-00-00Z-notes-01"", ""title"": ""Ground floor notes"", ""app"": ""autocad"",
  ""document"": { ""path"": ""C:/proj/plan.dwg"", ""fingerprint"": ""abc"" }, ""units"": { ""length"": ""mm"" },
  ""ops"": [ { ""id"": ""t1"", ""op"": ""text.add"", ""args"": { ""text"": ""STORE"", ""position"": [2000, 1500], ""heightName"": ""body"", ""scale"": ""1:100"" } } ],
  ""verify"": { ""checks"": [""ac-049.annotation-overlap"", ""ac-050.job-not-in-viewport"", ""ac-051.mixed-viewport-scales""],
    ""visual"": { ""target"": ""model"", ""window"": [[0, 0], [12000, 8000]], ""maxUnexpectedChange"": 0.002, ""padding"": 24 } } }", "model")]
        [InlineData(@"{ ""jobId"": ""2026-11-28T09-05-00Z-notes-01"", ""title"": ""Plan note"", ""app"": ""revit"",
  ""document"": { ""path"": ""C:/proj/model.rvt"", ""fingerprint"": ""abc"" }, ""units"": { ""length"": ""mm"" },
  ""ops"": [ { ""id"": ""n1"", ""op"": ""text.note"", ""args"": { ""view"": ""Level 0"", ""text"": ""STORE"", ""position"": [2000, 1500] } } ],
  ""verify"": { ""checks"": [""rv-054.annotation-overlap"", ""rv-055.job-outside-crop"", ""rv-057.mixed-scales-on-sheet""],
    ""visual"": { ""target"": ""Level 0"", ""pixelWidth"": 2400, ""maxUnexpectedChange"": 0.002 } } }", "Level 0")]
        public void DocumentedExampleJobsAreValid(string json, string target)
        {
            var v = JobValidator.Validate(json, OpRegistry.Default);
            Assert.True(v.Ok, string.Join("\n", v.Issues));
            Assert.Equal(target, v.Job!.Verify.Visual!.Target);
        }

        // ----- Layout evaluators -----

        private static readonly CheckRunner Runner = new CheckRunner(EvaluatorRegistry.CreateNeutral());

        private static CheckResult Run(string evaluator, string paramsJson, Facts facts, ProjectStandard? std = null) =>
            Runner.Run(new CheckDefinition("t.check", CheckApp.Both, "Test", Severity.Warning, evaluator, (JsonObject)JsonValue.Parse(paramsJson)), facts, std ?? RealData.Us);

        private static Facts Annotations(bool jobScope)
        {
            var f = new Facts("autocad");
            if (jobScope) f.Meta.Add(LayoutEvaluators.JobScopeKey, true);
            var a = f.Table("annotations");
            void Box(string id, string layout, double x0, double y0, double x1, double y1, bool byJob = false) =>
                a.Add(id).Set("type", "TEXT").Set("layout", layout).Set("minX", x0).Set("minY", y0).Set("maxX", x1).Set("maxY", y1).Set("byJob", byJob);
            Box("A", "L1", 0, 0, 10, 2);
            Box("B", "L1", 8, 0, 18, 2, byJob: true);   // 2 x 2 over A: 0.2 of the smaller box
            Box("C", "L1", 17.5, 0, 30, 2);             // 0.5 x 2 over B: 0.05
            Box("D", "L2", 0, 0, 10, 2);                // same place as A, other layout
            Box("E", "L1", 100, 100, 110, 102);
            a.Add("F").Set("type", "TEXT").Set("layout", "L1").Set("minX", 0);   // no box: skipped
            return f;
        }

        [Fact]
        public void ExtentsOverlapReportsEachPairOnce()
        {
            var r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""groupBy"": [""layout""], ""minOverlap"": 0.1, ""labelField"": ""type"" }", Annotations(false));
            Assert.Equal(CheckStatus.Fail, r.Status);
            var f = Assert.Single(r.Findings);
            Assert.Equal("A", f.RecordId);
            Assert.Equal("B", f.Data.GetStringOrNull("otherId"));
            Assert.Equal("overlap", f.Data.GetStringOrNull("field"));
            Assert.Equal(0.2, f.Data["value"]!.AsDouble(), 6);

            // A lower threshold also catches B and C; without groups D collides with A and B.
            r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""minOverlap"": 0.01 }", Annotations(false));
            Assert.Equal(4, r.Findings.Count);
            Assert.Contains(r.Findings, x => x.RecordId == "A" && x.Data.GetStringOrNull("otherId") == "D" || x.RecordId == "D" && x.Data.GetStringOrNull("otherId") == "A");
        }

        [Fact]
        public void ExtentsOverlapAfterAJobOnlyReportsPairsTheJobTouched()
        {
            var r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""groupBy"": [""layout""], ""minOverlap"": 0.01 }", Annotations(true));
            Assert.Equal(2, r.Findings.Count);
            Assert.All(r.Findings, x => Assert.True(x.RecordId == "B" || x.Data.GetStringOrNull("otherId") == "B"));

            r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""groupBy"": [""layout""], ""minOverlap"": 0.01, ""jobOnly"": false }", Annotations(true));
            Assert.Equal(2, r.Findings.Count);
            r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""minOverlap"": 0.01 }", Annotations(true));
            Assert.Equal(3, r.Findings.Count);
            r = Run("extents_overlap", @"{ ""table"": ""annotations"", ""minOverlap"": 0.01, ""jobOnly"": false }", Annotations(true));
            Assert.Equal(4, r.Findings.Count);
        }

        private static ProjectStandard WithScaleRules(string rulesJson)
        {
            var raw = (JsonObject)JsonValue.Parse(RealData.Iso.Raw.ToJson());
            raw.Set("scaleRules", JsonValue.Parse(rulesJson));
            return StandardLoader.FromJson(raw);
        }

        private static Facts Views()
        {
            var f = new Facts("revit");
            var v = f.Table("views");
            void V(string id, string sheet, double scale, string type) => v.Add(id).Set("sheetNumber", sheet).Set("scale", scale).Set("viewType", type).Set("onSheet", true);
            V("1", "A-101", 100, "FloorPlan");
            V("2", "A-101", 100, "FloorPlan");
            V("3", "A-101", 50, "Section");
            V("4", "A-101", 20, "Legend");
            V("5", "A-102", 100, "FloorPlan");
            V("6", "A-102", 20, "DraftingView");
            V("7", "X-001", 100, "FloorPlan");
            V("8", "X-001", 5, "FloorPlan");
            v.Add("9").Set("sheetNumber", "A-103").Set("viewType", "ThreeD").Set("onSheet", true);
            return f;
        }

        [Fact]
        public void SheetScalesUniformFollowsTheStandardsScaleRules()
        {
            const string p = @"{ ""table"": ""views"", ""groupField"": ""sheetNumber"", ""scaleField"": ""scale"", ""typeField"": ""viewType"" }";
            // No rule: nothing to check.
            Assert.Equal(CheckStatus.Pass, Run("sheet_scales_uniform", p, Views(), RealData.Iso).Status);
            Assert.Null(RealData.Iso.ScaleRules);

            var std = WithScaleRules(@"{ ""oneScalePerSheet"": true, ""exemptViewTypes"": [""legend"", ""DraftingView""], ""exemptSheets"": ""X-.*"", ""description"": ""One scale per sheet"" }");
            Assert.True(std.ScaleRules!.OneScalePerSheet);
            Assert.Contains("Legend", std.ScaleRules.ExemptViewTypes);
            var r = Run("sheet_scales_uniform", p, Views(), std);
            var f = Assert.Single(r.Findings);
            Assert.Contains("A-101", f.Message);
            var scales = f.Data["scales"]!.AsArray();
            Assert.Equal(2, scales.Count);

            Assert.Equal(CheckStatus.Pass, Run("sheet_scales_uniform", p, Views(), WithScaleRules(@"{ ""oneScalePerSheet"": false }")).Status);
            Assert.ThrowsAny<FormatException>(() => WithScaleRules(@"{ ""oneScalePerSheet"": true, ""perSheet"": 1 }"));
        }

        [Fact]
        public void ScaleInStandardCanSkipRecordsWithoutAScale()
        {
            const string p = @"{ ""table"": ""views"", ""field"": ""scale"", ""tolerance"": 0.001 }";
            var strict = Run("scale_in_standard", p, Views(), RealData.Iso);
            Assert.Contains(strict.Findings, x => x.RecordId == "9");
            var lenient = Run("scale_in_standard", @"{ ""table"": ""views"", ""field"": ""scale"", ""tolerance"": 0.001, ""skipMissing"": true }", Views(), RealData.Iso);
            Assert.DoesNotContain(lenient.Findings, x => x.RecordId == "9");
        }
    }
}
