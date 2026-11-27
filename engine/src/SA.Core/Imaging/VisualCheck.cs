using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SA.Core.Checks;
using SA.Core.Jobs;
using SA.Core.Json;

namespace SA.Core.Imaging
{
    /// <summary>What an engine hands to <see cref="VisualCheck.Run"/> after capturing the target before and after the job.</summary>
    public sealed class VisualInput
    {
        public VisualInput(VisualSpec spec, string outputDir)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            OutputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
        }

        public VisualSpec Spec { get; }
        /// <summary>The job's output folder; the diff image is written here.</summary>
        public string OutputDir { get; }

        /// <summary>The before capture (a PNG file), or null when it could not be made (see <see cref="BeforeError"/>).</summary>
        public string? BeforePath { get; set; }
        public string? BeforeError { get; set; }
        public string? AfterPath { get; set; }
        public string? AfterError { get; set; }

        /// <summary>What the after capture shows, in the target's 2D coordinates; null when the engine could not tell.</summary>
        public VisualFrame? Frame { get; set; }

        /// <summary>Extents (frame coordinates) of every element the job created or modified, before and after where known.</summary>
        public List<WorldBox> Expected { get; } = new List<WorldBox>();

        /// <summary>
        /// False when some element the job touched has no extents in <see cref="Expected"/> (deleted or modified elements whose
        /// earlier extents were not read, elements without geometry). Change outside the expected region is then review.
        /// </summary>
        public bool AllProjected { get; set; } = true;

        /// <summary>Elements the job created that should show in the capture (inside the frame, on a visible layer or category).</summary>
        public int CreatedVisible { get; set; }

        /// <summary>When set, the comparison cannot be judged (for example a rehearsal that rolls back before the after capture).</summary>
        public string? IncompleteReason { get; set; }

        /// <summary>Things the engine wants the reader to know (frame found from the crop box, elements skipped...).</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    public sealed class VisualOutcome
    {
        internal VisualOutcome(CheckResult check, JsonObject summary)
        {
            Check = check; Summary = summary;
        }

        public CheckResult Check { get; }
        public List<Artifact> Artifacts { get; } = new List<Artifact>();
        /// <summary>Written to the report as top-level "visual".</summary>
        public JsonObject Summary { get; }
        public DiffResult? Diff { get; internal set; }

        /// <summary>Adds the check, the artifacts and the "visual" summary to <paramref name="report"/>.</summary>
        public void AddTo(Report report)
        {
            report.Checks.Add(Check);
            report.Artifacts.AddRange(Artifacts);
            report.Extra.Set(VisualCheck.ReportKey, Summary);
        }
    }

    /// <summary>
    /// Judges a before/after pair: fail when nothing visibly changed although the job created visible elements, or when more
    /// than <see cref="VisualSpec.MaxUnexpectedChange"/> of the image changed outside the padded extents of the elements the
    /// job touched; review when the change outside cannot be placed for sure; incomplete when the pair could not be compared.
    /// </summary>
    public static class VisualCheck
    {
        public const string CheckId = "visual.diff";
        public const string Title = "What changed on screen matches what the job touched";
        public const string ReportKey = "visual";
        public const string BeforeFile = "visual-before.png";
        public const string AfterFile = "visual-after.png";
        public const string DiffFile = "visual-diff.png";
        public const double AspectTolerance = 0.02;

        public static VisualOutcome Run(VisualInput input)
        {
            if (input is null) throw new ArgumentNullException(nameof(input));
            var spec = input.Spec;
            var summary = new JsonObject().Add("target", spec.Target)
                .Add("threshold", spec.Threshold).Add("padding", spec.Padding).Add("maxUnexpectedChange", spec.MaxUnexpectedChange);
            if (spec.ExpectChange.HasValue) summary.Add("expectChange", spec.ExpectChange.Value);
            if (input.Frame != null) summary.Add("frame", input.Frame.ToJson());
            summary.Add("touchedExtents", input.Expected.Count).Add("allTouchedPlaced", input.AllProjected).Add("createdVisible", input.CreatedVisible);
            if (input.Notes.Count > 0) summary.Add("notes", JsonArray.FromStrings(input.Notes));

            var artifacts = new List<Artifact>();
            if (input.BeforePath != null && File.Exists(input.BeforePath)) artifacts.Add(new Artifact("png", input.BeforePath, "visual:before"));
            if (input.AfterPath != null && File.Exists(input.AfterPath)) artifacts.Add(new Artifact("png", input.AfterPath, "visual:after"));

            VisualOutcome Incomplete(string why)
            {
                summary.Add("status", "incomplete").Add("reason", why);
                var r = new CheckResult(CheckId, CheckStatus.Incomplete, why) { Incomplete = true, IncompleteReason = why };
                return Finish(r, summary, artifacts, null);
            }

            if (input.IncompleteReason != null) return Incomplete(input.IncompleteReason);
            if (input.BeforePath is null || !File.Exists(input.BeforePath))
                return Incomplete("the before capture of '" + spec.Target + "' could not be made" + (input.BeforeError != null ? ": " + input.BeforeError : ""));
            if (input.AfterPath is null || !File.Exists(input.AfterPath))
                return Incomplete("the after capture of '" + spec.Target + "' could not be made" + (input.AfterError != null ? ": " + input.AfterError : ""));

            RgbaImage before, after;
            try
            {
                before = Png.ReadFile(input.BeforePath);
                after = Png.ReadFile(input.AfterPath);
            }
            catch (Exception e) when (e is PngFormatException || e is IOException || e is UnauthorizedAccessException)
            {
                return Incomplete("a capture could not be read as PNG: " + e.Message);
            }
            if (before.Width != after.Width || before.Height != after.Height)
                return Incomplete("the before and after captures differ in size (" + before.Width + " x " + before.Height + " and " +
                                  after.Width + " x " + after.Height + "), so the view or its crop changed and pixels cannot be compared");

            int w = after.Width, h = after.Height;
            var frame = input.Frame;
            frame?.CheckAspect(w, h, AspectTolerance);
            if (frame != null) summary.Set("frame", frame.ToJson());

            bool nothingMayChange = spec.ExpectChange == false;
            var options = new DiffOptions { Threshold = spec.Threshold, Padding = spec.Padding };
            int placed = 0;
            if (frame != null && !nothingMayChange)
                foreach (var b in input.Expected)
                {
                    var px = frame.Project(b, w, h);
                    if (px is null) continue;
                    options.Expected.Add(px.Value);
                    placed++;
                }

            var diff = PixelDiff.Compare(before, after, options);
            string diffPath = Path.Combine(input.OutputDir, DiffFile);
            try
            {
                Png.WriteFile(diffPath, diff.Image!);
                artifacts.Add(new Artifact("png", diffPath, "visual:diff"));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                summary.Add("diffImageError", e.Message);
            }

            summary.Add("width", w).Add("height", h).Add("touchedInImage", placed)
                .Add("changedPixels", diff.ChangedPixels).Add("changedFraction", Round(diff.ChangedFraction))
                .Add("unexpectedPixels", diff.UnexpectedPixels).Add("unexpectedFraction", Round(diff.UnexpectedFraction))
                .Add("expectedRegions", new JsonArray(diff.ExpectedPadded.Select(r => (JsonValue)Rect(r))))
                .Add("regionCount", diff.RegionCount)
                .Add("regions", new JsonArray(diff.Regions.Select(g => (JsonValue)Region(g, frame, w, h))));

            bool expectChange = spec.ExpectChange ?? input.CreatedVisible > 0;
            CheckResult result;
            if (expectChange && diff.ChangedPixels == 0)
            {
                string why = spec.ExpectChange == true
                    ? "nothing visibly changed in '" + spec.Target + "' although verify.visual.expectChange is true"
                    : "nothing visibly changed in '" + spec.Target + "' although the job created " + input.CreatedVisible + " element(s) that should show there";
                result = new CheckResult(CheckId, CheckStatus.Fail, why);
                result.Findings.Add(new Finding(why + " (wrong layer or view, hidden, outside the crop, or drawn in a colour the capture does not show).", null, null,
                    new JsonObject().Add("field", "changedPixels").Add("value", 0).Add("createdVisible", input.CreatedVisible)));
            }
            else if (diff.UnexpectedFraction > spec.MaxUnexpectedChange)
            {
                string amount = Percent(diff.UnexpectedFraction) + " of the image changed outside the expected region (limit " + Percent(spec.MaxUnexpectedChange) + ")";
                string? doubt = nothingMayChange ? null
                    : frame is null ? "the engine could not tell which part of the target the image shows"
                    : !frame.Reliable ? frame.Note ?? "the image may not show exactly the frame"
                    : !input.AllProjected ? "the extents of some elements the job touched are unknown (deleted, or modified without earlier extents)"
                    : null;
                if (doubt is null)
                    result = new CheckResult(CheckId, CheckStatus.Fail, amount + (nothingMayChange ? "; verify.visual.expectChange is false, so every change is unexpected" : ""));
                else
                {
                    result = new CheckResult(CheckId, CheckStatus.Review, amount + ", but " + doubt + "; look at the diff image")
                    {
                        IsManualReview = true,
                        ReviewInstructions = "Open " + DiffFile + ": magenta is change outside the expected region (green boxes), red is change inside it. Decide whether the magenta change is the job's.",
                    };
                }
                foreach (var g in diff.Regions.Where(g => g.UnexpectedPixels > 0))
                    result.Findings.Add(new Finding(
                        g.UnexpectedPixels + " pixel(s) changed outside the expected region in " + g.Rect + " (image pixels" + WorldText(g.Rect, frame, w, h) + ")",
                        null, null, Region(g, frame, w, h).Add("field", "unexpectedPixels").Add("value", g.UnexpectedPixels)
                            .Add("review", result.Status == CheckStatus.Review)));
                if (diff.RegionCount > diff.Regions.Count)
                    result.Findings.Add(new Finding((diff.RegionCount - diff.Regions.Count) + " smaller changed region(s) are not listed."));
            }
            else
            {
                result = new CheckResult(CheckId, CheckStatus.Pass,
                    Percent(diff.ChangedFraction) + " of the image changed, " + Percent(diff.UnexpectedFraction) + " outside the expected region");
            }
            summary.Add("status", CheckResult.StatusToWire(result.Status));
            var outcome = Finish(result, summary, artifacts, diff);
            return outcome;
        }

        private static VisualOutcome Finish(CheckResult r, JsonObject summary, List<Artifact> artifacts, DiffResult? diff)
        {
            r.Title = Title;
            r.Severity = Severity.Warning;
            summary.Add("files", new JsonArray(artifacts.Select(a => (JsonValue)new JsonObject().Add("target", a.Target!).Add("path", a.Path))));
            var o = new VisualOutcome(r, summary) { Diff = diff };
            o.Artifacts.AddRange(artifacts);
            return o;
        }

        private static JsonObject Region(ChangeRegion g, VisualFrame? frame, int w, int h)
        {
            var o = new JsonObject().Add("box", Rect(g.Rect)).Add("pixels", g.Pixels).Add("unexpectedPixels", g.UnexpectedPixels);
            if (frame != null) o.Add("worldBox", frame.Unproject(g.Rect, w, h).ToJson());
            return o;
        }

        private static string WorldText(PixelRect r, VisualFrame? frame, int w, int h)
        {
            if (frame is null) return "";
            var b = frame.Unproject(r, w, h);
            return "; about " + N(b.MinX) + "," + N(b.MinY) + " to " + N(b.MaxX) + "," + N(b.MaxY) + " in the target's coordinates";
        }

        private static JsonArray Rect(PixelRect r) => new JsonArray(r.X0, r.Y0, r.X1, r.Y1);

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string Percent(double f) => (f * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";

        private static double Round(double v) => Math.Round(v, 6);
    }
}
