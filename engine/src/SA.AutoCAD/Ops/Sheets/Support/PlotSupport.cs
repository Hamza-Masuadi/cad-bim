using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using SA.AutoCAD.Host;
using SA.Core.Json;

namespace SA.AutoCAD.Ops.Sheets.Support
{
    /// <summary>The plot settings that matter for a sheet, read from a layout or a page setup (in this or another database).</summary>
    internal sealed class SheetSettings
    {
        public string Device = "";
        public string Media = "";
        public PlotRotation Rotation = PlotRotation.Degrees000;
        public string StyleSheet = "";
        public PlotPaperUnit PaperUnits = PlotPaperUnit.Millimeters;
        public bool UseStandardScale = true;
        public StdScaleType StdScale = StdScaleType.StdScale1To1;
        public double ScaleNumerator = 1, ScaleDenominator = 1;
        public string Name = "";

        internal static SheetSettings From(PlotSettings ps)
        {
            var s = new SheetSettings
            {
                Device = ps.PlotConfigurationName ?? "",
                Media = ps.CanonicalMediaName ?? "",
                Rotation = ps.PlotRotation,
                StyleSheet = ps.CurrentStyleSheet ?? "",
                PaperUnits = ps.PlotPaperUnits,
                UseStandardScale = ps.UseStandardScale,
                StdScale = ps.StdScaleType,
                Name = ps.PlotSettingsName ?? "",
            };
            var cs = ps.CustomPrintScale;
            s.ScaleNumerator = cs.Numerator;
            s.ScaleDenominator = cs.Denominator;
            return s;
        }

        internal JsonObject ToJson() => new JsonObject()
            .Add("plotDevice", Device).Add("paperSize", Media).Add("rotation", Rotation.ToString())
            .Add("plotStyleTable", StyleSheet).Add("paperUnits", PaperUnits.ToString())
            .Add("standardScale", UseStandardScale ? StdScale.ToString() : "custom " + ScaleNumerator + ":" + ScaleDenominator);

        internal bool SameAs(SheetSettings o) =>
            string.Equals(Device, o.Device, StringComparison.OrdinalIgnoreCase) && string.Equals(Media, o.Media, StringComparison.OrdinalIgnoreCase) &&
            Rotation == o.Rotation && string.Equals(StyleSheet, o.StyleSheet, StringComparison.OrdinalIgnoreCase) &&
            UseStandardScale == o.UseStandardScale && (UseStandardScale ? StdScale == o.StdScale : Math.Abs(ScaleNumerator * o.ScaleDenominator - o.ScaleNumerator * ScaleDenominator) < 1e-9);
    }

    /// <summary>
    /// Plot settings validation and plotting through the plot API (PlotSettingsValidator, PlotInfoValidator,
    /// PlotFactory.CreatePublishEngine), mirroring the capture service. Background plotting is switched off through the
    /// job's system variable scope (BACKGROUNDPLOT = 0, restored when the job ends).
    /// </summary>
    internal static class PlotSupport
    {
        internal static List<string> Devices() =>
            PlotSettingsValidator.Current.GetPlotDeviceList().Cast<string>().ToList();

        internal static string? FindDevice(string name) =>
            Devices().FirstOrDefault(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Canonical media names the device offers (through a temporary, non-database PlotSettings).</summary>
        internal static List<string> MediaFor(string device)
        {
            using (var ps = new PlotSettings(false))
            {
                var psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(ps, device, null);
                psv.RefreshLists(ps);
                return psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
            }
        }

        internal static string? FindMedia(List<string> media, string name) =>
            media.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Null when the plot style table exists and matches the drawing's plot style mode, otherwise the problem.
        /// Database.PlotStyleMode is true for colour-dependent (CTB) drawings and false for named (STB) ones; a drawing
        /// cannot use the other kind without converting it (one-way), which this engine never does.
        /// </summary>
        internal static string? StyleProblem(Database db, string table)
        {
            string ext = Path.GetExtension(table).ToLowerInvariant();
            if (ext != ".ctb" && ext != ".stb") return "'" + table + "' is not a .ctb or .stb file";
            bool colour = db.PlotStyleMode;
            if (colour && ext == ".stb") return "the drawing uses colour-dependent plot styles (CTB); '" + table + "' is a named style table";
            if (!colour && ext == ".ctb") return "the drawing uses named plot styles (STB); '" + table + "' is a colour-dependent table";
            var list = PlotSettingsValidator.Current.GetPlotStyleSheetList().Cast<string>().ToList();
            if (!list.Any(s => string.Equals(s, table, StringComparison.OrdinalIgnoreCase)))
                return "plot style table '" + table + "' is not installed (available: " + string.Join(", ", list.Take(20)) + ")";
            return null;
        }

        internal static bool IsMillimetreMedia(string media) =>
            media.IndexOf("_MM)", StringComparison.OrdinalIgnoreCase) >= 0 || media.EndsWith("MM", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Applies sheet settings to a plot settings object opened for write (a Layout, or a non-database PlotSettings).
        /// Order: device and media, refresh lists, paper units, plot type Layout, rotation, scale, style table.
        /// Orientation: "landscape" means the sheet's long side horizontal; the media's natural shape is read from
        /// PlotPaperSize after choosing it, and the plot is rotated 90 degrees when the two disagree.
        /// Scale: the layout plots 1:1 in paper units; when the drawing unit is not the media unit (a metre drawing on
        /// a millimetre sheet) a custom scale of (media units per drawing unit) : 1 is set.
        /// </summary>
        internal static void ApplySheet(PlotSettings ps, string device, string media, string? orientation, PlotRotation? rotation,
            string? styleSheet, double mmPerDrawingUnit, SheetSettings? scaleFrom)
        {
            var psv = PlotSettingsValidator.Current;
            psv.SetPlotConfigurationName(ps, device, media);
            psv.RefreshLists(ps);
            bool mm = IsMillimetreMedia(media);
            psv.SetPlotPaperUnits(ps, mm ? PlotPaperUnit.Millimeters : PlotPaperUnit.Inches);
            psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
            if (orientation != null)
            {
                var size = ps.PlotPaperSize;
                bool naturalLandscape = size.X >= size.Y;
                bool wantLandscape = orientation == "landscape";
                psv.SetPlotRotation(ps, naturalLandscape == wantLandscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
            }
            else if (rotation.HasValue) psv.SetPlotRotation(ps, rotation.Value);

            if (scaleFrom != null && !scaleFrom.UseStandardScale)
            {
                psv.SetUseStandardScale(ps, false);
                psv.SetCustomPrintScale(ps, new CustomScale(scaleFrom.ScaleNumerator, scaleFrom.ScaleDenominator));
            }
            else if (scaleFrom != null && scaleFrom.StdScale != StdScaleType.StdScale1To1)
            {
                psv.SetUseStandardScale(ps, true);
                psv.SetStdScaleType(ps, scaleFrom.StdScale);
            }
            else
            {
                double mediaPerUnit = mmPerDrawingUnit / (mm ? 1.0 : 25.4);
                if (Math.Abs(mediaPerUnit - 1.0) < 1e-12)
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.StdScale1To1);
                }
                else
                {
                    psv.SetUseStandardScale(ps, false);
                    psv.SetCustomPrintScale(ps, new CustomScale(mediaPerUnit, 1.0));
                }
            }
            if (!string.IsNullOrEmpty(styleSheet)) psv.SetCurrentStyleSheet(ps, styleSheet);
        }

        /// <summary>Orientation of plotted output: the media size, swapped when rotated by 90 or 270 degrees.</summary>
        internal static string Orientation(PlotSettings ps)
        {
            var s = ps.PlotPaperSize;
            bool rotated = ps.PlotRotation == PlotRotation.Degrees090 || ps.PlotRotation == PlotRotation.Degrees270;
            double w = rotated ? s.Y : s.X, h = rotated ? s.X : s.Y;
            return w >= h ? "landscape" : "portrait";
        }

        /// <summary>One page of a plot: the layout and the settings to plot it with (a non-database copy).</summary>
        internal sealed class Page
        {
            public ObjectId LayoutId;
            public string LayoutName = "";
            public PlotSettings Settings = null!;
        }

        /// <summary>
        /// Plots pages into one file. "The layout referenced in plotInfo must be active" (PlotEngine.BeginPage, AcCoreMgd
        /// reference), so each page's layout is made current and the original layout is restored in a finally block.
        /// The layouts' own plot settings are never written: the settings travel as PlotInfo.OverrideSettings.
        /// </summary>
        internal static void PlotFile(Document doc, List<Page> pages, string path)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException("AutoCAD is already plotting.");
            var lm = LayoutManager.Current;
            string original = lm.CurrentLayout;
            // Plot info objects stay alive until the plot ends (the first one also describes the document), then are disposed.
            var plotObjects = new List<IDisposable>();
            try
            {
                using (var pe = PlotFactory.CreatePublishEngine())
                {
                    pe.BeginPlot(null, null);
                    for (int i = 0; i < pages.Count; i++)
                    {
                        var pg = pages[i];
                        if (!string.Equals(lm.CurrentLayout, pg.LayoutName, StringComparison.OrdinalIgnoreCase)) lm.CurrentLayout = pg.LayoutName;
                        var pi = new PlotInfo { Layout = pg.LayoutId, OverrideSettings = pg.Settings };
                        plotObjects.Add(pi);
                        var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
                        plotObjects.Add(piv);
                        piv.Validate(pi);
                        if (i == 0) pe.BeginDocument(pi, doc.Name, null, 1, true, path);
                        var ppi = new PlotPageInfo();
                        plotObjects.Add(ppi);
                        pe.BeginPage(ppi, pi, i == pages.Count - 1, null);
                        pe.BeginGenerateGraphics(null);
                        pe.EndGenerateGraphics(null);
                        pe.EndPage(null);
                    }
                    pe.EndDocument(null);
                    pe.EndPlot(null);
                }
            }
            finally
            {
                foreach (var o in plotObjects) o.Dispose();
                if (!string.Equals(lm.CurrentLayout, original, StringComparison.OrdinalIgnoreCase))
                {
                    try { lm.CurrentLayout = original; }
                    catch (Exception e) { Log.Error("Could not switch back to layout '" + original + "'.", e); }
                }
            }
        }

        /// <summary>True when the file exists, is non-empty and starts with the PDF signature.</summary>
        internal static bool IsPdf(string path, out long length)
        {
            length = 0;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length == 0) return false;
                length = fi.Length;
                using (var fs = File.OpenRead(path))
                {
                    var head = new byte[5];
                    int n = fs.Read(head, 0, 5);
                    return n == 5 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F' && head[4] == (byte)'-';
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
