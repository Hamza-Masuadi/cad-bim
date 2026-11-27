using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using SA.AutoCAD.Host;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Capture
{
    /// <summary>
    /// Captures a layout or model space to PDF and/or PNG with the plot API (PlotFactory.CreatePublishEngine, documented as "an engine for plotting to a device (or to a file)"), never the PLOT command.
    ///
    /// Spec (capture method params, or one entry of a job's verify.capture):
    ///   target: "layout:&lt;name&gt;" | "model" (model space extents) | "activeView" / "activeLayout" (the current layout;
    ///           in model space, what the current view displays)
    ///   window: [[x1, y1], [x2, y2]] model-space window in DRAWING units (target "model" only)
    ///   format: "pdf" | "png" | "both" (default: from the out file's extension, else pdf)
    ///   out:    full output path (capture method); for jobs files go to the job's output folder
    ///   paperSize: canonical media name to use instead of the automatic choice
    ///
    /// Assumptions, all to confirm in AutoCAD (see the report):
    /// - The built-in plotter configurations "DWG To PDF.pc3" and "PublishToWeb PNG.pc3" exist under those names.
    /// - BACKGROUNDPLOT = 0 makes the engine run in the foreground; it is set for the capture and restored afterwards.
    /// - Plotting a layout requires it to be the current layout, so the engine switches to it and switches back. Change
    ///   counting is suspended around the switch and the plot, which leave the drawing as it was, so a capture does not
    ///   change the fingerprint; if switching back fails the counter is bumped. The switch may still set DBMOD (a save
    ///   prompt) and may appear in the undo list: to confirm in the app.
    /// - For a model window, DCS equals WCS (no view twist, world UCS); a twisted view gives a shifted window.
    /// - Media: PDF keeps the layout's paper if the PDF device offers it, else the first A3, else the first listed size,
    ///   and plots scaled to fit unless the layout's own paper was kept; PNG takes the largest pixel size the device lists.
    /// - No progress dialog is passed, so no UI appears. Plot stamps and plot styles follow the layout's settings.
    /// </summary>
    internal static class CaptureService
    {
        internal const string PdfDevice = "DWG To PDF.pc3";
        internal const string PngDevice = "PublishToWeb PNG.pc3";

        private enum TargetKind { Layout, ModelExtents, ModelWindow, Display, LayoutWindow }

        /// <summary>Null when the spec is acceptable, else the problem.</summary>
        internal static string? CheckSpec(JsonObject spec)
        {
            string target = (spec["target"] as JsonString)?.Value ?? "";
            if (target.Length == 0) return "target is required";
            if (!(target == "model" || target == "activeView" || target == "activeLayout" || target.StartsWith("layout:", StringComparison.Ordinal) && target.Length > 7))
                return "target must be \"layout:<name>\", \"model\", \"activeView\" or \"activeLayout\"";
            var fmt = (spec["format"] as JsonString)?.Value;
            if (fmt != null && fmt != "pdf" && fmt != "png" && fmt != "both") return "format must be pdf, png or both";
            if (spec["window"] != null)
            {
                if (target != "model") return "window is only valid with target \"model\"";
                if (!TryWindow(spec["window"], out _)) return "window must be [[x1, y1], [x2, y2]] with distinct corners";
            }
            return null;
        }

        private static bool TryWindow(JsonValue? v, out Extents2d window)
        {
            window = default;
            if (!(v is JsonArray a) || a.Count != 2) return false;
            if (!(a[0] is JsonArray p) || !(a[1] is JsonArray q) || p.Count != 2 || q.Count != 2) return false;
            if (!(p[0] is JsonNumber x1 && p[1] is JsonNumber y1 && q[0] is JsonNumber x2 && q[1] is JsonNumber y2)) return false;
            if (x1.Value == x2.Value || y1.Value == y2.Value) return false;
            window = new Extents2d(Math.Min(x1.Value, x2.Value), Math.Min(y1.Value, y2.Value), Math.Max(x1.Value, x2.Value), Math.Max(y1.Value, y2.Value));
            return true;
        }

        /// <summary>The capture method. Runs on the main thread.</summary>
        internal static JsonValue CaptureMethod(JsonObject p)
        {
            var problem = CheckSpec(p);
            if (problem != null) throw new EngineException(ErrorCodes.BadRequest, problem);
            if (!(p["out"] is JsonString outPath) || !Path.IsPathRooted(outPath.Value))
                throw new EngineException(ErrorCodes.BadRequest, "out must be a full path for the output file.");
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument
                ?? throw new EngineException(ErrorCodes.WrongDocument, "No drawing is open.");
            string dir = Path.GetDirectoryName(outPath.Value)!;
            string baseName = Path.GetFileNameWithoutExtension(outPath.Value);
            var spec = (JsonObject)p.DeepClone();
            if (spec["format"] is null)
            {
                string ext = Path.GetExtension(outPath.Value).ToLowerInvariant();
                spec.Set("format", ext == ".png" ? "png" : "pdf");
            }
            List<Artifact> arts;
            // A write lock is needed to switch layouts; no command names, so no labelled undo step is asked for.
            using (doc.LockDocument(DocumentLockMode.Write, null, null, false))
                arts = Capture(doc, spec, dir, baseName);
            return new JsonObject()
                .Add("artifacts", new JsonArray(arts.Select(a => (JsonValue)a.ToJson())))
                .Add("document", new JsonObject().Add("path", doc.Name ?? "").Add("fingerprint", DocumentIdentity.FingerprintOf(doc)));
        }

        /// <summary>Main thread, document locked by the caller. Writes outDir\baseName.pdf and/or .png.</summary>
        internal static List<Artifact> Capture(Document doc, JsonObject spec, string outDir, string baseName)
        {
            string? problem = CheckSpec(spec);
            if (problem != null) throw new EngineException(ErrorCodes.BadRequest, problem);
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new EngineException(ErrorCodes.Busy, "AutoCAD is already plotting.");
            string target = ((JsonString)spec["target"]!).Value;
            string fmt = (spec["format"] as JsonString)?.Value ?? "pdf";
            string? paperSize = (spec["paperSize"] as JsonString)?.Value;
            var formats = fmt == "both" ? new[] { "pdf", "png" } : new[] { fmt };
            Directory.CreateDirectory(outDir);

            string original = LayoutManager.Current.CurrentLayout;
            TargetKind kind;
            string layoutName;
            Extents2d window = default;
            if (target.StartsWith("layout:", StringComparison.Ordinal)) { kind = TargetKind.Layout; layoutName = target.Substring(7); }
            else if (target == "model")
            {
                layoutName = "Model";
                kind = TryWindow(spec["window"], out window) ? TargetKind.ModelWindow : TargetKind.ModelExtents;
            }
            else
            {
                layoutName = original;
                kind = string.Equals(original, "Model", StringComparison.OrdinalIgnoreCase) ? TargetKind.Display : TargetKind.Layout;
            }
            return PlotFiles(doc, layoutName, kind, window, formats, paperSize, outDir, baseName, target);
        }

        /// <summary>
        /// Main thread, document locked, no transaction open. Plots one PNG of <paramref name="layoutName"/>: the
        /// <paramref name="window"/> (in that layout's coordinates: WCS for Model, paper units for a layout) scaled to fit
        /// and centred, or what model space displays when <paramref name="window"/> is null. Used by verify.visual, whose
        /// before and after images must show exactly the same area.
        /// </summary>
        internal static void PlotPng(Document doc, string layoutName, Extents2d? window, string path)
        {
            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new EngineException(ErrorCodes.Busy, "AutoCAD is already plotting.");
            var kind = window.HasValue ? TargetKind.LayoutWindow : TargetKind.Display;
            string dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var arts = PlotFiles(doc, layoutName, kind, window ?? default, new[] { "png" }, null, dir, Path.GetFileNameWithoutExtension(path), "visual");
            if (arts.Count == 0) throw new InvalidOperationException("The plot wrote no file.");
        }

        private static List<Artifact> PlotFiles(Document doc, string layoutName, TargetKind kind, Extents2d window, string[] formats, string? paperSize,
            string outDir, string baseName, string target)
        {
            var db = doc.Database;
            var lm = LayoutManager.Current;
            string original = lm.CurrentLayout;
            var artifacts = new List<Artifact>();
            var tracker = DocumentIdentity.Track(doc);
            using (var sysVars = new SysVarScope())
            {
                bool switched = false;
                tracker.Suspend();
                try
                {
                    sysVars.Set("BACKGROUNDPLOT", 0);
                    ObjectId layoutId = lm.GetLayoutId(layoutName);
                    if (layoutId.IsNull) throw new EngineException(ErrorCodes.BadRequest, "There is no layout named '" + layoutName + "'.");
                    if (!string.Equals(original, layoutName, StringComparison.OrdinalIgnoreCase))
                    {
                        lm.CurrentLayout = layoutName;
                        switched = true;
                    }
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                        foreach (var f in formats)
                        {
                            string path = Path.Combine(outDir, baseName + "." + f);
                            if (File.Exists(path)) File.Delete(path);
                            PlotOne(doc, layout, layoutId, kind, window, f, paperSize, path);
                            var info = new FileInfo(path);
                            if (!info.Exists || info.Length == 0) throw new InvalidOperationException("The plot finished but '" + path + "' was not written.");
                            artifacts.Add(new Artifact(f, path, target));
                        }
                        tr.Commit();
                    }
                }
                finally
                {
                    if (switched)
                    {
                        try { lm.CurrentLayout = original; }
                        catch (Exception e) { Log.Error("Could not switch back to layout '" + original + "'.", e); tracker.ForceBump(); }
                    }
                    tracker.Resume();
                }
            }
            return artifacts;
        }

        private static void PlotOne(Document doc, Layout layout, ObjectId layoutId, TargetKind kind, Extents2d window, string format, string? paperSize, string path)
        {
            string device = format == "png" ? PngDevice : PdfDevice;
            using (var ps = new PlotSettings(layout.ModelType))
            {
                ps.CopyFrom(layout);
                var psv = PlotSettingsValidator.Current;
                psv.SetPlotConfigurationName(ps, device, null);
                psv.RefreshLists(ps);
                var media = psv.GetCanonicalMediaNameList(ps);
                string chosen = ChooseMedia(media, format, paperSize, layout.CanonicalMediaName, out bool keptLayoutPaper);
                psv.SetPlotConfigurationName(ps, device, chosen);

                switch (kind)
                {
                    case TargetKind.Layout: psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout); break;
                    case TargetKind.ModelExtents: psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents); break;
                    case TargetKind.Display: psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Display); break;
                    case TargetKind.ModelWindow:
                    case TargetKind.LayoutWindow:
                        // The window must be set before the plot type can be Window.
                        psv.SetPlotWindowArea(ps, window);
                        psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
                        break;
                }
                if (!(kind == TargetKind.Layout && keptLayoutPaper))
                {
                    psv.SetUseStandardScale(ps, true);
                    psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
                    psv.SetPlotCentered(ps, true);
                }

                using (var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps })
                using (var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled })
                using (var ppi = new PlotPageInfo())
                using (var pe = PlotFactory.CreatePublishEngine())
                {
                    piv.Validate(pi);
                    pe.BeginPlot(null, null);
                    pe.BeginDocument(pi, doc.Name, null, 1, true, path);
                    pe.BeginPage(ppi, pi, true, null);
                    pe.BeginGenerateGraphics(null);
                    pe.EndGenerateGraphics(null);
                    pe.EndPage(null);
                    pe.EndDocument(null);
                    pe.EndPlot(null);
                }
            }
        }

        private static readonly Regex Pixels = new Regex(@"\((\d+(?:\.\d+)?)_?x_?(\d+(?:\.\d+)?)_?Pixels\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string ChooseMedia(StringCollection list, string format, string? requested, string layoutMedia, out bool keptLayoutPaper)
        {
            keptLayoutPaper = false;
            var names = list.Cast<string>().ToList();
            if (names.Count == 0) throw new InvalidOperationException("The plotter configuration offers no paper sizes.");
            if (requested != null)
            {
                var hit = names.FirstOrDefault(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase));
                if (hit is null) throw new EngineException(ErrorCodes.BadRequest, "Paper size '" + requested + "' is not offered; available: " + string.Join(", ", names.Take(30)));
                return hit;
            }
            if (format == "png")
            {
                string best = names[0];
                double area = -1;
                foreach (var n in names)
                {
                    var m = Pixels.Match(n);
                    if (!m.Success) continue;
                    double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (a > area) { area = a; best = n; }
                }
                return best;
            }
            var same = names.FirstOrDefault(n => string.Equals(n, layoutMedia, StringComparison.OrdinalIgnoreCase));
            if (same != null) { keptLayoutPaper = true; return same; }
            return names.FirstOrDefault(n => n.IndexOf("A3", StringComparison.OrdinalIgnoreCase) >= 0) ?? names[0];
        }
    }
}
