using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Capture
{
    internal sealed class CaptureResult
    {
        public string Path = "";
        public string Kind = "";
        public long Bytes;
        public int? Width;
        public int? Height;

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("kind", Kind).Add("path", Path).Add("bytes", Bytes);
            if (Width.HasValue) o.Add("width", Width.Value);
            if (Height.HasValue) o.Add("height", Height.Value);
            return o;
        }
    }

    /// <summary>
    /// Image and PDF export of views and sheets. Neither changes the model and neither opens a transaction.
    ///
    /// Image: Document.ExportImage with ExportRange.SetOfViews, so the views are exported by id and the active view is
    /// never switched. Revit names image files itself ("&lt;prefix&gt; - &lt;view type&gt; - &lt;view name&gt;.png"), so each view is
    /// exported into a fresh scratch folder and the single file produced is moved to the requested path.
    /// PDF: Document.Export(folder, viewIds, PDFExportOptions) with Combine, so one file named options.FileName.
    /// Both are checked on disk afterwards: the file exists and is not empty, and for PNG and JPEG the pixel size is
    /// read from the file header.
    /// </summary>
    internal static class CaptureService
    {
        public const int MinPixels = 32;
        public const int MaxPixels = 15000;

        public static string? CannotExport(View v)
        {
            if (v.IsTemplate) return "view '" + v.Name + "' is a template";
            if (v is ViewSchedule) return "view '" + v.Name + "' is a schedule; image export needs a graphical view or sheet";
            bool printable;
            try { printable = v.CanBePrinted; } catch (Exception) { printable = false; }
            if (!printable) return "view '" + v.Name + "' (" + v.ViewType + ") cannot be printed or exported";
            return null;
        }

        public static CaptureResult ExportImage(Document doc, View view, string targetPath, bool jpeg, int pixelWidth, bool overwrite)
        {
            string folder = Path.GetDirectoryName(targetPath) ?? throw new ArgumentException("target has no folder");
            Directory.CreateDirectory(folder);
            if (File.Exists(targetPath) && !overwrite) throw new IOException(targetPath + " exists and overwrite is false");
            string scratch = Path.Combine(folder, ".sa-capture-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(scratch);
            try
            {
                var type = jpeg ? ImageFileType.JPEGLossless : ImageFileType.PNG;
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(scratch, "capture"),
                    HLRandWFViewsFileType = type,
                    ShadowViewsFileType = type,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = pixelWidth,
                    ImageResolution = ImageResolution.DPI_150,
                };
                opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                doc.ExportImage(opts);
                string ext = jpeg ? ".jpg" : ".png";
                var produced = Directory.GetFiles(scratch).Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                                                                     (jpeg && f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))).ToList();
                if (produced.Count == 0) throw new IOException("Revit reported no error but wrote no image for view '" + view.Name + "'");
                if (produced.Count > 1) throw new IOException("Revit wrote " + produced.Count + " images for one view; expected one");
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(produced[0], targetPath);
                return Inspect(targetPath, jpeg ? "jpg" : "png");
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }

        public static CaptureResult ExportPdf(Document doc, IList<ElementId> views, string targetPath, bool overwrite,
            string colour = "colour", string raster = "high", bool hideCrop = true, bool hideScope = true, bool hideRefPlanes = true)
        {
            string folder = Path.GetDirectoryName(targetPath) ?? throw new ArgumentException("target has no folder");
            Directory.CreateDirectory(folder);
            if (File.Exists(targetPath) && !overwrite) throw new IOException(targetPath + " exists and overwrite is false");
            string scratch = Path.Combine(folder, ".sa-capture-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(scratch);
            try
            {
                var opts = new PDFExportOptions
                {
                    Combine = true,
                    FileName = "capture",
                    HideCropBoundaries = hideCrop,
                    HideScopeBoxes = hideScope,
                    HideReferencePlane = hideRefPlanes,
                    ColorDepth = colour == "grayscale" ? ColorDepthType.GrayScale : colour == "black_line" ? ColorDepthType.BlackLine : ColorDepthType.Color,
                    RasterQuality = raster == "low" ? RasterQualityType.Low : raster == "medium" ? RasterQualityType.Medium :
                                    raster == "presentation" ? RasterQualityType.Presentation : RasterQualityType.High,
                    StopOnError = true,
                };
                bool ok = doc.Export(scratch, views, opts);
                var produced = Directory.GetFiles(scratch, "*.pdf");
                if (!ok) throw new IOException("Revit reported that exporting one or more views to PDF failed");
                if (produced.Length != 1) throw new IOException("Revit wrote " + produced.Length + " PDF files; expected one");
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(produced[0], targetPath);
                return Inspect(targetPath, "pdf");
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (Exception) { }
            }
        }

        public static CaptureResult Inspect(string path, string kind)
        {
            var r = new CaptureResult { Path = path, Kind = kind };
            var fi = new FileInfo(path);
            r.Bytes = fi.Exists ? fi.Length : 0;
            if (!fi.Exists || fi.Length == 0) return r;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var head = new byte[Math.Min(65536, (int)Math.Min(int.MaxValue, fi.Length))];
                    int n = fs.Read(head, 0, head.Length);
                    if (kind == "png") ReadPng(head, n, r);
                    else if (kind == "jpg") ReadJpeg(head, n, r);
                    else if (kind == "pdf" && !(n >= 5 && head[0] == '%' && head[1] == 'P' && head[2] == 'D' && head[3] == 'F')) r.Bytes = 0;
                }
            }
            catch (Exception) { }
            return r;
        }

        private static void ReadPng(byte[] b, int n, CaptureResult r)
        {
            // 8-byte signature, then the IHDR chunk: length(4) "IHDR"(4) width(4, big-endian) height(4).
            if (n < 24 || b[0] != 0x89 || b[1] != 'P' || b[2] != 'N' || b[3] != 'G' || b[12] != 'I' || b[13] != 'H' || b[14] != 'D' || b[15] != 'R') return;
            r.Width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            r.Height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        }

        private static void ReadJpeg(byte[] b, int n, CaptureResult r)
        {
            if (n < 4 || b[0] != 0xFF || b[1] != 0xD8) return;
            int i = 2;
            while (i + 9 < n)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                int len = (b[i + 2] << 8) | b[i + 3];
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    r.Height = (b[i + 5] << 8) | b[i + 6];
                    r.Width = (b[i + 7] << 8) | b[i + 8];
                    return;
                }
                i += 2 + len;
            }
        }

        public static string SafeFileName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var chars = s.Select(c => bad.Contains(c) || c == ':' || c == '\\' || c == '/' ? '_' : c).ToArray();
            string r = new string(chars).Trim().TrimEnd('.');
            return r.Length == 0 ? "view" : r;
        }

        public static string IdText(ElementId id) => ElementIds.ToWire(id);
    }
}
