using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Core.Imaging;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Revit.Audit;
using SA.Revit.Compat;
using SA.Revit.Host;
using SA.Revit.Ops;
using SA.Revit.Ops.Support;

namespace SA.Revit.Capture
{
    /// <summary>
    /// verify.visual in Revit: one PNG of the target view before the transaction group starts and one after the last op
    /// and the verify checks, inside the group, so rehearsals compare too (the after image is taken before the rollback).
    /// Both are Document.ExportImage with the same pixel width, fitted to the view's extent.
    ///
    /// Frame: a sheet's outline (View.Outline, sheet feet); a view's crop box in crop coordinates when its crop is active.
    /// The frame is unreliable for a non-rectangular crop and whenever the image's width/height ratio is not the
    /// frame's (annotation outside the crop, or an annotation crop wider than the model crop); an uncropped view has no
    /// frame. Without a reliable frame, change outside the expected region is review, never fail.
    /// Expected region: boxes, before (elements the job declared it modifies or deletes, read before anything ran) and
    /// after (elements it created or modified), of the elements this view shows: view-specific elements of this view and
    /// model elements; on a sheet only the sheet's own elements are placed, so a job that changes model elements shown
    /// through viewports is judged review.
    /// </summary>
    internal static class VisualService
    {
        internal sealed class State
        {
            internal State(VisualInput input) { Input = input; }
            internal VisualInput Input { get; }
            internal View? View;
            internal int PixelWidth = 2000;
            internal CropFrame? Crop;
            internal bool Sheet;
            internal bool Done;
            internal long Ms;
        }

        /// <summary>Before the transaction group. <paramref name="changing"/>: existing elements the ops declared they modify or delete. Never throws.</summary>
        internal static State? Before(JobContext jc, IEnumerable<long> changing)
        {
            var spec = jc.Job.Verify.Visual;
            if (spec is null) return null;
            var sw = Stopwatch.StartNew();
            var state = new State(new VisualInput(spec, jc.OutputDir));
            var input = state.Input;
            try
            {
                var doc = jc.Doc;
                var view = spec.Target == "activeView" ? doc.ActiveView : Lookup.View(doc, spec.Raw["target"]).Value;
                if (view is null) { input.IncompleteReason = "view '" + spec.Target + "' was not found"; return state; }
                string? why = CaptureService.CannotExport(view);
                if (why != null) { input.IncompleteReason = why; return state; }
                state.View = view;
                if (spec.Raw["pixelWidth"] is JsonNumber pw && pw.TryGetInt64(out long px)) state.PixelWidth = (int)Math.Max(CaptureService.MinPixels, Math.Min(CaptureService.MaxPixels, px));
                Frame(view, state);
                foreach (long id in changing)
                {
                    var e = doc.GetElement(ElementIds.FromLong(id));
                    if (e is null) continue;
                    var b = Place(e, state);
                    if (b.HasValue) input.Expected.Add(b.Value);
                    else if (Shows(e, view)) input.AllProjected = false;
                }
                string path = Path.Combine(input.OutputDir, VisualCheck.BeforeFile);
                try
                {
                    var res = CaptureService.ExportImage(doc, view, path, false, state.PixelWidth, true);
                    input.BeforePath = res.Path;
                }
                catch (Exception e)
                {
                    Log.Error("verify.visual: the before capture failed", e);
                    input.BeforeError = e.Message;
                }
            }
            catch (Exception e)
            {
                Log.Error("verify.visual: setting up the comparison failed", e);
                input.IncompleteReason = "setting up the comparison failed: " + e.Message;
            }
            state.Ms = sw.ElapsedMilliseconds;
            return state;
        }

        /// <summary>After the last op and the verify checks, inside the group, no transaction open. Adds visual.diff. Never throws.</summary>
        internal static void After(JobContext jc, State? state)
        {
            if (state is null || state.Done) return;
            state.Done = true;
            var sw = Stopwatch.StartNew();
            var input = state.Input;
            try
            {
                if (input.IncompleteReason is null && input.BeforePath != null && state.View != null)
                {
                    string path = Path.Combine(input.OutputDir, VisualCheck.AfterFile);
                    try
                    {
                        var res = CaptureService.ExportImage(jc.Doc, state.View, path, false, state.PixelWidth, true);
                        input.AfterPath = res.Path;
                    }
                    catch (Exception e)
                    {
                        Log.Error("verify.visual: the after capture failed", e);
                        input.AfterError = e.Message;
                    }
                    Expected(jc, state);
                }
            }
            catch (Exception e)
            {
                Log.Error("verify.visual: the comparison failed", e);
                input.IncompleteReason ??= "the comparison failed: " + e.Message;
            }
            Judge(jc.Report, state, sw.ElapsedMilliseconds);
        }

        /// <summary>After the job, whatever happened: when the after image was never taken, reports visual.diff incomplete. Never throws.</summary>
        internal static void Finish(JobContext jc, State? state)
        {
            if (state is null || state.Done) return;
            state.Done = true;
            state.Input.IncompleteReason ??= "the job stopped before the after image could be taken";
            Judge(jc.Report, state, 0);
        }

        private static void Judge(Report report, State state, long ms)
        {
            VisualOutcome outcome;
            try { outcome = VisualCheck.Run(state.Input); }
            catch (Exception e)
            {
                Log.Error("verify.visual: judging the images failed", e);
                report.Checks.Add(new CheckResult(VisualCheck.CheckId, CheckStatus.Error, e.GetType().Name + ": " + e.Message) { Title = VisualCheck.Title, Severity = Severity.Warning });
                return;
            }
            outcome.AddTo(report);
            var status = outcome.Check.Status;
            if (status != CheckStatus.Pass)
                report.Warnings.Add(new ReportWarning("engine", "VISUAL CHECK " + CheckResult.StatusToWire(status).ToUpperInvariant() + ": " + (outcome.Check.Message ?? ""))
                    { Code = "visual_" + CheckResult.StatusToWire(status), Severity = status == CheckStatus.Fail ? "warning" : "info" });
            report.TimingMs["visual"] = state.Ms + ms;
        }

        private static void Frame(View view, State state)
        {
            var input = state.Input;
            if (view is ViewSheet)
            {
                var o = view.Outline;
                if (o != null && o.Max.U > o.Min.U && o.Max.V > o.Min.V)
                {
                    state.Sheet = true;
                    input.Frame = new VisualFrame(new WorldBox(o.Min.U, o.Min.V, o.Max.U, o.Max.V), true, "the sheet's outline");
                }
                else input.Notes.Add("the sheet has no outline, so change cannot be placed");
                return;
            }
            var crop = CropFrame.Of(view);
            if (crop is null) { input.Notes.Add("the view is not cropped, so change cannot be placed in the image"); return; }
            state.Crop = crop;
            bool shaped = false;
            try { shaped = view.GetCropRegionShapeManager()?.ShapeSet ?? false; } catch (Exception) { }
            input.Frame = shaped
                ? new VisualFrame(crop.Box, false, "the crop region is not a rectangle; its bounding rectangle is used")
                : new VisualFrame(crop.Box, true, "the view's crop region");
        }

        /// <summary>The element's box in frame coordinates, or null when it is not drawn in the target or has no box.</summary>
        private static WorldBox? Place(Element e, State state)
        {
            var view = state.View!;
            if (e is ElementType || e is View) return null;
            bool viewSpecific = e.ViewSpecific;
            if (viewSpecific && e.OwnerViewId != view.Id) return null;
            if (state.Sheet)
            {
                if (!viewSpecific) return null;
                var sb = e.get_BoundingBox(view);
                return sb is null ? (WorldBox?)null : new WorldBox(sb.Min.X, sb.Min.Y, sb.Max.X, sb.Max.Y);
            }
            if (state.Crop is null) return null;
            var bb = e.get_BoundingBox(viewSpecific ? view : null);
            return bb is null ? (WorldBox?)null : state.Crop.ToCrop(bb);
        }

        /// <summary>The element could be drawn in the view: owned by it, or a model element.</summary>
        private static bool Shows(Element e, View view) =>
            !(e is ElementType) && !(e is View) && e.Category != null && (!e.ViewSpecific || e.OwnerViewId == view.Id);

        private static void Expected(JobContext jc, State state)
        {
            var doc = jc.Doc;
            var view = state.View!;
            var input = state.Input;
            var report = jc.Report;
            var deleted = new HashSet<long>();
            foreach (var r in report.Results)
                foreach (var d in r.Deleted)
                    if (ElementIds.TryParse(d, out var did)) deleted.Add(ElementIds.ToLong(did));
            var touched = new HashSet<long>(jc.Created);
            foreach (var r in report.Results)
                foreach (var m in r.Created.Concat(r.Modified))
                    if (ElementIds.TryParse(m, out var mid)) touched.Add(ElementIds.ToLong(mid));
            touched.ExceptWith(deleted);

            var inView = new HashSet<long>();
            var created = new HashSet<long>(jc.Created);
            if (created.Count > 0)
            {
                try
                {
                    foreach (var id in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds())
                        if (created.Contains(ElementIds.ToLong(id))) inView.Add(ElementIds.ToLong(id));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { input.Notes.Add("the elements visible in the view could not be listed"); }
            }

            foreach (long id in touched)
            {
                var e = doc.GetElement(ElementIds.FromLong(id));
                if (e is null) continue;
                bool hidden;
                try { hidden = e.IsHidden(view) || (e.Category != null && view.GetCategoryHidden(e.Category.Id)); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { hidden = false; }
                WorldBox? b = null;
                try { b = Place(e, state); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                if (b.HasValue) input.Expected.Add(b.Value);
                else if (Shows(e, view) && !hidden && input.Frame != null) input.AllProjected = false;
                if (created.Contains(id) && inView.Contains(id) && !hidden && (input.Frame is null || !b.HasValue || b.Value.Intersects(input.Frame.Box)))
                    input.CreatedVisible++;
            }
        }
    }
}
