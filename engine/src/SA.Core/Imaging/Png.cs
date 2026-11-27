using System;
using System.IO;
using System.IO.Compression;

namespace SA.Core.Imaging
{
    /// <summary>Thrown for a file that is not a PNG this decoder reads; the message says what is wrong.</summary>
    public sealed class PngFormatException : FormatException
    {
        public PngFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// PNG decoding and encoding with only the BCL (DeflateStream for the zlib payload).
    ///
    /// Decodes every colour type and bit depth of the PNG specification (greyscale 1/2/4/8/16, truecolour 8/16, indexed
    /// 1/2/4/8, greyscale with alpha 8/16, truecolour with alpha 8/16), with or without Adam7 interlacing, and tRNS
    /// transparency. Chunk CRCs and the zlib Adler-32 are verified. Unknown critical chunks, a missing palette, a preset
    /// zlib dictionary and truncated data are refused with a <see cref="PngFormatException"/>. Gamma, colour profiles
    /// and other ancillary chunks are ignored: the comparison works on stored sample values.
    /// Encodes 8-bit RGBA, not interlaced, choosing a filter per row.
    /// </summary>
    public static class Png
    {
        /// <summary>Largest image (width x height) accepted, so three images of a comparison stay under about 0.8 GB.</summary>
        public const long MaxPixels = 64000000;

        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static RgbaImage ReadFile(string path) => Decode(File.ReadAllBytes(path));

        public static void WriteFile(string path, RgbaImage image)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Encode(image));
        }

        // ------------------------------------------------------------------ decode

        public static RgbaImage Decode(byte[] data)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 8) throw new PngFormatException("Not a PNG file: " + data.Length + " bytes, shorter than the signature.");
            for (int i = 0; i < 8; i++)
                if (data[i] != Signature[i]) throw new PngFormatException("Not a PNG file: the 8-byte signature is wrong.");

            int pos = 8;
            bool haveHeader = false, haveEnd = false;
            int width = 0, height = 0, bitDepth = 0, colourType = 0, interlace = 0;
            byte[]? palette = null;
            byte[]? paletteAlpha = null;
            int[]? keyColour = null;
            var idat = new MemoryStream();
            while (pos < data.Length)
            {
                if (pos + 8 > data.Length) throw new PngFormatException("Truncated PNG: a chunk header is cut off at byte " + pos + ".");
                uint length = ReadU32(data, pos);
                if (length > int.MaxValue || pos + 12 + (long)length > data.Length)
                    throw new PngFormatException("Truncated PNG: a chunk at byte " + pos + " claims " + length + " bytes.");
                string type = ChunkType(data, pos + 4);
                int start = pos + 8, len = (int)length;
                uint crc = ReadU32(data, start + len);
                if (Crc32.Compute(data, pos + 4, len + 4) != crc) throw new PngFormatException("Corrupt PNG: CRC mismatch in chunk '" + type + "' at byte " + pos + ".");
                pos = start + len + 4;

                if (!haveHeader && type != "IHDR") throw new PngFormatException("Invalid PNG: the first chunk is '" + type + "', not IHDR.");
                switch (type)
                {
                    case "IHDR":
                        if (haveHeader) throw new PngFormatException("Invalid PNG: two IHDR chunks.");
                        if (len != 13) throw new PngFormatException("Invalid PNG: IHDR is " + len + " bytes, not 13.");
                        uint w = ReadU32(data, start), h = ReadU32(data, start + 4);
                        bitDepth = data[start + 8];
                        colourType = data[start + 9];
                        int compression = data[start + 10], filter = data[start + 11];
                        interlace = data[start + 12];
                        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new PngFormatException("Invalid PNG: image size " + w + " x " + h + ".");
                        if ((long)w * h > MaxPixels) throw new PngFormatException("PNG of " + w + " x " + h + " pixels is above the limit of " + MaxPixels + " pixels.");
                        width = (int)w; height = (int)h;
                        if (!ValidDepth(colourType, bitDepth))
                            throw new PngFormatException("Unsupported PNG: colour type " + colourType + " with bit depth " + bitDepth + " is not a combination the PNG specification defines.");
                        if (compression != 0) throw new PngFormatException("Unsupported PNG: compression method " + compression + ".");
                        if (filter != 0) throw new PngFormatException("Unsupported PNG: filter method " + filter + ".");
                        if (interlace > 1) throw new PngFormatException("Unsupported PNG: interlace method " + interlace + ".");
                        haveHeader = true;
                        break;
                    case "PLTE":
                        if (len == 0 || len % 3 != 0 || len / 3 > 256) throw new PngFormatException("Invalid PNG: PLTE of " + len + " bytes.");
                        palette = new byte[len];
                        Buffer.BlockCopy(data, start, palette, 0, len);
                        break;
                    case "tRNS":
                        if (colourType == 3)
                        {
                            paletteAlpha = new byte[len];
                            Buffer.BlockCopy(data, start, paletteAlpha, 0, len);
                        }
                        else if (colourType == 0 && len == 2) keyColour = new[] { ReadU16(data, start) };
                        else if (colourType == 2 && len == 6) keyColour = new[] { ReadU16(data, start), ReadU16(data, start + 2), ReadU16(data, start + 4) };
                        else if (colourType == 4 || colourType == 6) throw new PngFormatException("Invalid PNG: tRNS with colour type " + colourType + ", which already has alpha.");
                        else throw new PngFormatException("Invalid PNG: tRNS of " + len + " bytes for colour type " + colourType + ".");
                        break;
                    case "IDAT":
                        idat.Write(data, start, len);
                        break;
                    case "IEND":
                        haveEnd = true;
                        break;
                    default:
                        if (char.IsUpper(type[0])) throw new PngFormatException("Unsupported PNG: unknown critical chunk '" + type + "'.");
                        break;
                }
                if (haveEnd) break;
            }
            if (!haveHeader) throw new PngFormatException("Invalid PNG: no IHDR chunk.");
            if (!haveEnd) throw new PngFormatException("Truncated PNG: no IEND chunk.");
            if (idat.Length == 0) throw new PngFormatException("Invalid PNG: no image data (IDAT).");
            if (colourType == 3 && palette is null) throw new PngFormatException("Invalid PNG: an indexed-colour image without a PLTE chunk.");

            int channels = Channels(colourType);
            int bitsPerPixel = channels * bitDepth;
            long rawSize = RawSize(width, height, bitsPerPixel, interlace == 1);
            byte[] raw = Inflate(idat.ToArray(), rawSize);

            var image = new RgbaImage(width, height);
            var ctx = new Expand(colourType, bitDepth, palette, paletteAlpha, keyColour, image);
            if (interlace == 0) Unfilter(raw, 0, width, height, bitsPerPixel, ctx, 0, 0, 1, 1);
            else
            {
                int offset = 0;
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = PassSize(width, Adam7[pass, 0], Adam7[pass, 2]);
                    int ph = PassSize(height, Adam7[pass, 1], Adam7[pass, 3]);
                    if (pw == 0 || ph == 0) continue;
                    offset = Unfilter(raw, offset, pw, ph, bitsPerPixel, ctx, Adam7[pass, 0], Adam7[pass, 1], Adam7[pass, 2], Adam7[pass, 3]);
                }
            }
            return image;
        }

        /// <summary>Adam7 passes: x start, y start, x step, y step.</summary>
        private static readonly int[,] Adam7 = { { 0, 0, 8, 8 }, { 4, 0, 8, 8 }, { 0, 4, 4, 8 }, { 2, 0, 4, 4 }, { 0, 2, 2, 4 }, { 1, 0, 2, 2 }, { 0, 1, 1, 2 } };

        private static int PassSize(int size, int start, int step) => size <= start ? 0 : (size - start + step - 1) / step;

        private static bool ValidDepth(int colourType, int depth)
        {
            switch (colourType)
            {
                case 0: return depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16;
                case 3: return depth == 1 || depth == 2 || depth == 4 || depth == 8;
                case 2: case 4: case 6: return depth == 8 || depth == 16;
                default: return false;
            }
        }

        private static int Channels(int colourType) => colourType == 2 ? 3 : colourType == 4 ? 2 : colourType == 6 ? 4 : 1;

        private static long RowBytes(int width, int bitsPerPixel) => ((long)width * bitsPerPixel + 7) / 8;

        private static long RawSize(int width, int height, int bitsPerPixel, bool interlaced)
        {
            if (!interlaced) return height * (1 + RowBytes(width, bitsPerPixel));
            long total = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = PassSize(width, Adam7[pass, 0], Adam7[pass, 2]);
                int ph = PassSize(height, Adam7[pass, 1], Adam7[pass, 3]);
                if (pw > 0 && ph > 0) total += ph * (1 + RowBytes(pw, bitsPerPixel));
            }
            return total;
        }

        private static byte[] Inflate(byte[] zlib, long expected)
        {
            if (zlib.Length < 6) throw new PngFormatException("Corrupt PNG: the zlib stream is " + zlib.Length + " bytes.");
            int cmf = zlib[0], flg = zlib[1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7) throw new PngFormatException("Corrupt PNG: the zlib stream does not use deflate.");
            if (((cmf << 8) | flg) % 31 != 0) throw new PngFormatException("Corrupt PNG: bad zlib header check bits.");
            if ((flg & 0x20) != 0) throw new PngFormatException("Unsupported PNG: the zlib stream uses a preset dictionary.");
            if (expected > int.MaxValue) throw new PngFormatException("PNG data of " + expected + " bytes is too large.");
            var raw = new byte[expected];
            int read = 0;
            try
            {
                using (var ds = new DeflateStream(new MemoryStream(zlib, 2, zlib.Length - 2), CompressionMode.Decompress))
                {
                    while (read < raw.Length)
                    {
                        int n = ds.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
            }
            catch (InvalidDataException e) { throw new PngFormatException("Corrupt PNG: the compressed image data is damaged (" + e.Message + ")."); }
            if (read < raw.Length) throw new PngFormatException("Truncated PNG: " + read + " of " + raw.Length + " bytes of image data.");
            uint adler = ReadU32(zlib, zlib.Length - 4);
            if (Adler32(raw) != adler) throw new PngFormatException("Corrupt PNG: the zlib Adler-32 checksum does not match the image data.");
            return raw;
        }

        /// <summary>Unfilters one image or pass starting at <paramref name="offset"/> and writes its pixels; returns the offset after it.</summary>
        private static int Unfilter(byte[] raw, int offset, int w, int h, int bitsPerPixel, Expand ctx, int x0, int y0, int dx, int dy)
        {
            int rowBytes = (int)RowBytes(w, bitsPerPixel);
            int bpp = Math.Max(1, bitsPerPixel / 8);
            var prev = new byte[rowBytes];
            var cur = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                int filter = raw[offset];
                Buffer.BlockCopy(raw, offset + 1, cur, 0, rowBytes);
                offset += 1 + rowBytes;
                switch (filter)
                {
                    case 0: break;
                    case 1: for (int i = bpp; i < rowBytes; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]); break;
                    case 2: for (int i = 0; i < rowBytes; i++) cur[i] = (byte)(cur[i] + prev[i]); break;
                    case 3:
                        for (int i = 0; i < rowBytes; i++) cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                        break;
                    case 4:
                        for (int i = 0; i < rowBytes; i++)
                            cur[i] = (byte)(cur[i] + Paeth(i >= bpp ? cur[i - bpp] : (byte)0, prev[i], i >= bpp ? prev[i - bpp] : (byte)0));
                        break;
                    default: throw new PngFormatException("Corrupt PNG: unknown row filter " + filter + ".");
                }
                ctx.Row(cur, w, y0 + y * dy, x0, dx);
                var t = prev; prev = cur; cur = t;
            }
            return offset;
        }

        private static byte Paeth(byte a, byte b, byte c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        /// <summary>Turns unfiltered rows of any colour type and depth into RGBA8 pixels.</summary>
        private sealed class Expand
        {
            private readonly int _type, _depth;
            private readonly byte[]? _palette, _paletteAlpha;
            private readonly int[]? _key;
            private readonly RgbaImage _img;

            public Expand(int type, int depth, byte[]? palette, byte[]? paletteAlpha, int[]? key, RgbaImage img)
            {
                _type = type; _depth = depth; _palette = palette; _paletteAlpha = paletteAlpha; _key = key; _img = img;
            }

            private int Sample(byte[] row, int index)
            {
                switch (_depth)
                {
                    case 16: return (row[index * 2] << 8) | row[index * 2 + 1];
                    case 8: return row[index];
                    default:
                        int perByte = 8 / _depth;
                        int b = row[index / perByte];
                        int shift = 8 - _depth * (index % perByte + 1);
                        return (b >> shift) & ((1 << _depth) - 1);
                }
            }

            private byte To8(int v) => _depth == 16 ? (byte)((v * 255 + 32767) / 65535) : _depth == 8 ? (byte)v : (byte)(v * 255 / ((1 << _depth) - 1));

            public void Row(byte[] row, int w, int y, int x0, int dx)
            {
                var px = _img.Pixels;
                int stride = _img.Width;
                for (int i = 0; i < w; i++)
                {
                    int o = (y * stride + x0 + i * dx) * 4;
                    switch (_type)
                    {
                        case 0:
                        {
                            int g = Sample(row, i);
                            byte g8 = To8(g);
                            px[o] = px[o + 1] = px[o + 2] = g8;
                            px[o + 3] = _key != null && g == _key[0] ? (byte)0 : (byte)255;
                            break;
                        }
                        case 2:
                        {
                            int r = Sample(row, i * 3), g = Sample(row, i * 3 + 1), b = Sample(row, i * 3 + 2);
                            px[o] = To8(r); px[o + 1] = To8(g); px[o + 2] = To8(b);
                            px[o + 3] = _key != null && r == _key[0] && g == _key[1] && b == _key[2] ? (byte)0 : (byte)255;
                            break;
                        }
                        case 3:
                        {
                            int idx = Sample(row, i);
                            if (idx * 3 + 2 >= _palette!.Length) throw new PngFormatException("Corrupt PNG: palette index " + idx + " is beyond the " + _palette.Length / 3 + "-entry palette.");
                            px[o] = _palette[idx * 3]; px[o + 1] = _palette[idx * 3 + 1]; px[o + 2] = _palette[idx * 3 + 2];
                            px[o + 3] = _paletteAlpha != null && idx < _paletteAlpha.Length ? _paletteAlpha[idx] : (byte)255;
                            break;
                        }
                        case 4:
                        {
                            byte g8 = To8(Sample(row, i * 2));
                            px[o] = px[o + 1] = px[o + 2] = g8;
                            px[o + 3] = To8(Sample(row, i * 2 + 1));
                            break;
                        }
                        default:
                            px[o] = To8(Sample(row, i * 4)); px[o + 1] = To8(Sample(row, i * 4 + 1));
                            px[o + 2] = To8(Sample(row, i * 4 + 2)); px[o + 3] = To8(Sample(row, i * 4 + 3));
                            break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ encode

        /// <summary>8-bit RGBA PNG (colour type 6), not interlaced; each row uses the filter with the smallest sum of absolute values.</summary>
        public static byte[] Encode(RgbaImage image)
        {
            if (image is null) throw new ArgumentNullException(nameof(image));
            int w = image.Width, h = image.Height, rowBytes = w * 4;
            var src = image.Pixels;
            var filtered = new byte[(long)h * (rowBytes + 1)];
            var candidate = new byte[5][];
            for (int f = 0; f < 5; f++) candidate[f] = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                int row = y * rowBytes, up = (y - 1) * rowBytes;
                long best = long.MaxValue;
                int bestFilter = 0;
                for (int f = 0; f < 5; f++)
                {
                    var c = candidate[f];
                    long sum = 0;
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int x = src[row + i];
                        int a = i >= 4 ? src[row + i - 4] : 0;
                        int b = y > 0 ? src[up + i] : 0;
                        int cc = i >= 4 && y > 0 ? src[up + i - 4] : 0;
                        int v = f == 0 ? x : f == 1 ? x - a : f == 2 ? x - b : f == 3 ? x - ((a + b) >> 1) : x - Paeth((byte)a, (byte)b, (byte)cc);
                        c[i] = (byte)v;
                        int s = (sbyte)c[i];
                        sum += s < 0 ? -s : s;
                    }
                    if (sum < best) { best = sum; bestFilter = f; }
                }
                long o = (long)y * (rowBytes + 1);
                filtered[o] = (byte)bestFilter;
                Buffer.BlockCopy(candidate[bestFilter], 0, filtered, (int)o + 1, rowBytes);
            }

            var zlib = new MemoryStream();
            zlib.WriteByte(0x78);
            zlib.WriteByte(0x9C);
            using (var ds = new DeflateStream(zlib, CompressionLevel.Optimal, true)) ds.Write(filtered, 0, filtered.Length);
            WriteU32(zlib, Adler32(filtered));

            var png = new MemoryStream();
            png.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            PutU32(ihdr, 0, (uint)w);
            PutU32(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(png, "IHDR", ihdr, ihdr.Length);
            WriteChunk(png, "IDAT", zlib.GetBuffer(), (int)zlib.Length);
            WriteChunk(png, "IEND", new byte[0], 0);
            return png.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int length)
        {
            var head = new byte[8];
            PutU32(head, 0, (uint)length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, 0, length);
            uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, head, 4, 4), data, 0, length) ^ 0xFFFFFFFFu;
            WriteU32(s, crc);
        }

        // ------------------------------------------------------------------ helpers

        private static uint ReadU32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        private static int ReadU16(byte[] b, int i) => b[i] << 8 | b[i + 1];

        private static string ChunkType(byte[] b, int i)
        {
            var c = new char[4];
            for (int k = 0; k < 4; k++)
            {
                byte x = b[i + k];
                if (!(x >= 65 && x <= 90 || x >= 97 && x <= 122)) throw new PngFormatException("Corrupt PNG: chunk type at byte " + i + " is not four letters.");
                c[k] = (char)x;
            }
            return new string(c);
        }

        private static void PutU32(byte[] b, int i, uint v)
        {
            b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
        }

        private static void WriteU32(Stream s, uint v)
        {
            var b = new byte[4];
            PutU32(b, 0, v);
            s.Write(b, 0, 4);
        }

        internal static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(5552, data.Length - i);
                for (int k = 0; k < n; k++) { a += data[i + k]; b += a; }
                a %= Mod; b %= Mod;
                i += n;
            }
            return (b << 16) | a;
        }

        private static class Crc32
        {
            private static readonly uint[] Table = Build();

            private static uint[] Build()
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                return t;
            }

            public static uint Update(uint crc, byte[] data, int offset, int length)
            {
                for (int i = 0; i < length; i++) crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
                return crc;
            }

            public static uint Compute(byte[] data, int offset, int length) => Update(0xFFFFFFFFu, data, offset, length) ^ 0xFFFFFFFFu;
        }
    }
}
