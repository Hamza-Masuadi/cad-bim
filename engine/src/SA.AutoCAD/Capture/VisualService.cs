using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Audit;
using SA.AutoCAD.Host;
using SA.Core.Checks;
using SA.Core.Imaging;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Capture
{
    /// <summary>
    /// verify.visual in AutoCAD: one PNG of the target before the job's transaction starts and one after the commit, both
    /// plotted as the same window scaled to fit and centred, so pixel (x, y) is the same drawing point in both. Plotting
    /// never happens inside an open transaction (owner decision 14), so a rehearsal, which rolls back inside its
    /// transaction, has no after image and reports incomplete.
    ///
    /// Window per target: "model" verify.visual.window, else the model extents (Extmin/Extmax) before the job grown by 5%
    /// each side; "layout:NAME" the layout's limits; "activeView"/"activeLayout" the current layout's limits, or in model
    /// space the current view when it looks straight down with no twist (otherwise what model space displays, compared
    /// without a frame, so change outside the touched elements is review).
    /// Expected region: extents of the entities the job created or modified, after the job; on a layout, model-space
    /// entities are projected through each viewport that shows their layer. Earlier extents of modified and deleted
    /// entities are not known, so with any of them change outside the region is review, never fail.
    /// </summary>
    internal static class VisualService
    {
        internal sealed class State
        {
            internal State(VisualInput input) { Input = input; }
            internal VisualInput Input { get; }
            internal string Layout = "Model";
            internal Extents2d? Window;
            internal long Ms;
        }

        private const double ModelPadding = 0.05;

        /// <summary>Main thread, document locked, before the job's transaction. Never throws.</summary>
        internal static State? Before(Document doc, Job job, RunMode mode)
        {
            var spec = job.Verify.Visual;
            if (spec is null) return null;
            var sw = Stopwatch.StartNew();
            var state = new State(new VisualInput(spec, job.OutputDir ?? SaPaths.JobOutput(job.JobId)));
            var input = state.Input;
            try
            {
                if (mode == RunMode.Rehearse)
                {
                    input.IncompleteReason = "AutoCAD cannot plot inside the rehearsal's transaction, which rolls back before any after image could be made; run the job in commit mode to compare";
                    return state;
                }
                string? problem = Resolve(doc, spec, state);
                if (problem != null) { input.IncompleteReason = problem; return state; }
                string path = Path.Combine(input.OutputDir, VisualCheck.BeforeFile);
                try
                {
                    CaptureService.PlotPng(doc, state.Layout, state.Window, path);
                    input.BeforePath = path;
                }
                catch (Exception e)
                {
                    Log.Error("verify.visual: the before capture failed.", e);
                    input.BeforeError = e.Message;
                }
            }
            catch (Exception e)
            {
                Log.Error("verify.visual: setting up the comparison failed.", e);
                input.IncompleteReason = "setting up the comparison failed: " + e.Message;
            }
            state.Ms = sw.ElapsedMilliseconds;
            return state;
        }

        /// <summary>Main thread, document locked, after the job (committed or not). Adds check visual.diff to the report. Never throws.</summary>
        internal static void After(Document doc, Report report, State? state, bool committed)
        {
            if (state is null) return;
            var sw = Stopwatch.StartNew();
            var input = state.Input;
            try
            {
                if (input.IncompleteReason is null && !committed)
                    input.IncompleteReason = "the job did not commit, so there is no change to compare";
                if (input.IncompleteReason is null && input.BeforePath != null)
                {
                    string path = Path.Combine(input.OutputDir, VisualCheck.AfterFile);
                    try
                    {
                        CaptureService.PlotPng(doc, state.Layout, state.Window, path);
                        input.AfterPath = path;
                    }
                    catch (Exception e)
                    {
                        Log.Error("verify.visual: the after capture failed.", e);
                        input.AfterError = e.Message;
                    }
                    Expected(doc, report, state);
                }
            }
            catch (Exception e)
            {
                Log.Error("verify.visual: the comparison failed.", e);
                input.IncompleteReason ??= "the comparison failed: " + e.Message;
            }
            VisualOutcome outcome;
            try { outcome = VisualCheck.Run(input); }
            catch (Exception e)
            {
                Log.Error("verify.visual: judging the images failed.", e);
                var r = new CheckResult(VisualCheck.CheckId, CheckStatus.Error, e.GetType().Name + ": " + e.Message) { Title = VisualCheck.Title, Severity = Severity.Warning };
                report.Checks.Add(r);
                return;
            }
            try
            {
                outcome.AddTo(report);
                var status = outcome.Check.Status;
                if (status != CheckStatus.Pass)
                    report.Warnings.Add(new ReportWarning("engine", "VISUAL CHECK " + CheckResult.StatusToWire(status).ToUpperInvariant() + ": " + (outcome.Check.Message ?? ""))
                        { Code = "visual_" + CheckResult.StatusToWire(status), Severity = status == CheckStatus.Fail ? "warning" : "info" });
                report.TimingMs["visual"] = state.Ms + sw.ElapsedMilliseconds;
            }
            catch (Exception e)
            {
                Log.Error("verify.visual: adding the result to the report failed.", e);
            }
        }

        private static string? Resolve(Document doc, VisualSpec spec, State state)
        {
            var db = doc.Database;
            string target = spec.Target;
            string current = LayoutManager.Current.CurrentLayout;
            if (target == "activeView" || target == "activeLayout")
            {
                if (!string.Equals(current, "Model", StringComparison.OrdinalIgnoreCase)) return LayoutLimits(doc, current, state);
                state.Layout = "Model";
                var view = doc.Editor.GetCurrentView();
                bool plan = !view.PerspectiveEnabled && Math.Abs(view.ViewTwist) < 1e-9 && view.ViewDirection.IsParallelTo(Vector3d.ZAxis) && view.ViewDirection.Z > 0;
                if (plan && view.Width > 0 && view.Height > 0)
                {
                    double cx = view.Target.X + view.CenterPoint.X, cy = view.Target.Y + view.CenterPoint.Y;
                    state.Window = new Extents2d(cx - view.Width / 2, cy - view.Height / 2, cx + view.Width / 2, cy + view.Height / 2);
                    state.Input.Notes.Add("frame: the current model view (plan, no twist)");
                }
                else state.Input.Notes.Add("the current model view is not a plan view without twist, so the display is plotted as it is and change cannot be placed");
                return null;
            }
            if (target == "model")
            {
                state.Layout = "Model";
                if (spec.Raw["window"] is JsonArray w && w.Count == 2 && w[0] is JsonArray p && w[1] is JsonArray q &&
                    p[0] is JsonNumber x1 && p[1] is JsonNumber y1 && q[0] is JsonNumber x2 && q[1] is JsonNumber y2)
                {
                    state.Window = new Extents2d(Math.Min(x1.Value, x2.Value), Math.Min(y1.Value, y2.Value), Math.Max(x1.Value, x2.Value), Math.Max(y1.Value, y2.Value));
                    state.Input.Notes.Add("frame: verify.visual.window");
                    return null;
                }
                var min = db.Extmin; var max = db.Extmax;
                if (!(max.X > min.X) || !(max.Y > min.Y) || Math.Abs(max.X - min.X) > 1e20)
                    return "model space has no extents yet (empty or not regenerated); give verify.visual.window";
                double px = (max.X - min.X) * ModelPadding, py = (max.Y - min.Y) * ModelPadding;
                state.Window = new Extents2d(min.X - px, min.Y - py, max.X + px, max.Y + py);
                state.Input.Notes.Add("frame: model extents before the job, grown by 5% each side; anything the job draws outside it is not in the images");
                return null;
            }
            if (target.StartsWith("layout:", StringComparison.Ordinal) && target.Length > 7) return LayoutLimits(doc, target.Substring(7), state);
            return "target '" + target + "' is not one of \"model\", \"layout:<name>\", \"activeView\" or \"activeLayout\"";
        }

        private static string? LayoutLimits(Document doc, string name, State state)
        {
            var id = LayoutManager.Current.GetLayoutId(name);
            if (id.IsNull) return "there is no layout named '" + name + "'";
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var layout = (Layout)tr.GetObject(id, OpenMode.ForRead);
                var lim = layout.Limits;
                tr.Commit();
                if (!(lim.MaxPoint.X > lim.MinPoint.X) || !(lim.MaxPoint.Y > lim.MinPoint.Y)) return "layout '" + name + "' has no paper limits to frame the images";
                state.Layout = layout.LayoutName;
                state.Window = lim;
                state.Input.Notes.Add("frame: the limits of layout '" + layout.LayoutName + "' in paper units");
            }
            return null;
        }

        private static void Expected(Document doc, Report report, State state)
        {
            var input = state.Input;
            if (state.Window is null) { input.AllProjected = false; return; }
            var win = state.Window.Value;
            var frameBox = new WorldBox(win.MinPoint.X, win.MinPoint.Y, win.MaxPoint.X, win.MaxPoint.Y);
            input.Frame = new VisualFrame(frameBox, true, "plotted window, scaled to fit and centred");

            var deleted = new HashSet<string>(report.Results.SelectMany(r => r.Deleted), StringComparer.OrdinalIgnoreCase);
            var created = new HashSet<string>(report.Results.SelectMany(r => r.Created), StringComparer.OrdinalIgnoreCase);
            var touched = report.Results.SelectMany(r => r.Created.Concat(r.Modified)).Where(h => !deleted.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (deleted.Count > 0 || report.Results.Any(r => r.Modified.Count > 0))
            {
                input.AllProjected = false;
                input.Notes.Add("the job modified or deleted entities whose earlier extents are not known");
            }
            var db = doc.Database;
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                bool onModel = string.Equals(state.Layout, "Model", StringComparison.OrdinalIgnoreCase);
                ObjectId layoutBlock = ObjectId.Null;
                List<ViewportWindow> windows = new List<ViewportWindow>();
                if (!onModel)
                {
                    var lid = LayoutManager.Current.GetLayoutId(state.Layout);
                    if (!lid.IsNull) layoutBlock = ((Layout)tr.GetObject(lid, OpenMode.ForRead)).BlockTableRecordId;
                    windows = ViewportWindow.All(db, tr).Where(w => string.Equals(w.Layout, state.Layout, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                var hidden = AnnotationsCollector.HiddenLayers(db, tr);
                foreach (string h in touched)
                {
                    try
                    {
                        if (!db.TryGetObjectId(new Handle(Convert.ToInt64(h, 16)), out ObjectId id) || id.IsErased) continue;
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                        bool visible = e.Visible && !hidden.Contains(e.LayerId);
                        var boxes = new List<WorldBox>();
                        if (e.OwnerId == modelId && onModel) boxes.Add(Flat(e.GeometricExtents));
                        else if (e.OwnerId == modelId)
                        {
                            var ext = e.GeometricExtents;
                            foreach (var w in windows)
                            {
                                if (!w.ShowsLayer(e.LayerId)) continue;
                                var b = w.ToPaper(ext);
                                if (b.HasValue) boxes.Add(b.Value);
                            }
                        }
                        else if (!onModel && e.OwnerId == layoutBlock) boxes.Add(Flat(e.GeometricExtents));
                        else continue;
                        input.Expected.AddRange(boxes);
                        if (visible && created.Contains(h) && boxes.Any(b => b.Intersects(frameBox))) input.CreatedVisible++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { input.AllProjected = false; }
                    catch (FormatException) { }
                }
                tr.Commit();
            }
        }

        private static WorldBox Flat(Extents3d e) => new WorldBox(e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y);
    }
}
