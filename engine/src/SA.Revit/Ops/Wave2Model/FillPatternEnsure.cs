using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// fillpattern.ensure: model or drafting fill patterns built from explicit grids (FillPattern(name, target,
    /// orientation), FillGrid, SetFillGrids, FillPatternElement.Create).
    ///
    /// Layouts: stack = grids at 0 (offset height) and 90 (offset width); bond = a solid grid at 0 (offset height) and
    /// a grid at 90 with offset length/fraction, shift height and segments [height, -(fraction-1)*height]; lines = one
    /// grid; grids = raw grids. Model patterns take model lengths in job units and always use ToHost; drafting
    /// patterns take printed lengths in paper units (OpContext.Paper), converted to internal units the same way.
    ///
    /// Two conventions the API documents loosely are kept in one place each: <see cref="ApiAngle"/> (the FillGrid
    /// type summary says degrees, but the property gives no unit and every other API angle is radians, so radians are
    /// written) and <see cref="ApiSegments"/> (the type summary says positive numbers are dashes and negative numbers
    /// spaces, which is what is written). Both are on the confirm-in-the-app list.
    ///
    /// Existing pattern with the same name and target: compared grid by grid; a difference is refused unless update,
    /// which calls SetFillPattern. Its documented exceptions include "the name of the newFillPattern already exists",
    /// so an update may be refused for that reason alone; that becomes the error update_not_supported.
    /// </summary>
    public sealed class FillPatternEnsure : OpHandler
    {
        public override string Name => "fillpattern.ensure";

        private sealed class GridSpec
        {
            public double Angle, U, V, Offset, Shift;
            public List<double> Segments = new List<double>();
        }

        private sealed class Item
        {
            public string Name = "";
            public FillPatternTarget Target;
            public FillPatternHostOrientation Orientation;
            public List<GridSpec> Grids = new List<GridSpec>();
            public FillPatternElement? Existing;
            public bool Matches;
            public List<string> Differences = new List<string>();
        }

        private static readonly Dictionary<string, FillPatternHostOrientation> Orientations = new Dictionary<string, FillPatternHostOrientation>(StringComparer.Ordinal)
        {
            { "to_view", FillPatternHostOrientation.ToView }, { "to_host", FillPatternHostOrientation.ToHost }, { "as_text", FillPatternHostOrientation.AsText },
        };

        internal static double ApiAngle(RevitUnits u, double degrees) => u.AngleToInternal(degrees);

        internal static List<double> ApiSegments(IEnumerable<double> dashesAndGaps) => dashesAndGaps.ToList();

        private static FillPattern Pattern(Item it)
        {
            var fp = new FillPattern(it.Name, it.Target, it.Orientation);
            var grids = new List<FillGrid>();
            foreach (var g in it.Grids)
            {
                var fg = new FillGrid { Angle = g.Angle, Origin = new UV(g.U, g.V), Offset = g.Offset, Shift = g.Shift };
                fg.SetSegments(g.Segments);
                grids.Add(fg);
            }
            fp.SetFillGrids(grids);
            return fp;
        }

        private static List<Item>? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var items = Requested(ctx, args, issues);
            var specs = args.GetObjectList("patterns");
            for (int k = 0; k < items.Count; k++)
            {
                var it = items[k];
                var o = specs[k];
                it.Existing = FillPatternElement.GetFillPatternElementByName(doc, it.Target, it.Name);
                if (it.Existing is null) continue;
                Compare(it, it.Existing.GetFillPattern(), it.Target == FillPatternTarget.Drafting ? ctx.Paper : ctx.Units, it.Differences);
                it.Matches = it.Differences.Count == 0;
                if (it.Matches) continue;
                if (args.GetBool("update")) ctx.DeclareModifies(it.Existing.Id);
                else OpContext.Error(issues, o.Path + ".name", RevitIssueCodes.Mismatch, o.GetString("target") + " pattern '" + it.Name + "' exists and differs: " + string.Join("; ", it.Differences.Take(6)) + "; set update:true to change it");
            }
            return Kit.HasErrors(issues) ? null : items;
        }

        /// <summary>The patterns as requested, converted to internal units; no document lookups.</summary>
        private static List<Item> Requested(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in args.GetObjectList("patterns"))
            {
                var it = new Item { Name = o.GetString("name") };
                string target = o.GetString("target");
                bool drafting = target == "drafting";
                it.Target = drafting ? FillPatternTarget.Drafting : FillPatternTarget.Model;
                it.Orientation = drafting ? Orientations[o.GetStringOrNull("orientation") ?? "to_view"] : FillPatternHostOrientation.ToHost;
                if (!drafting && o.Has("orientation") && o.GetString("orientation") != "to_host")
                    OpContext.Warning(issues, o.Path + ".orientation", RevitIssueCodes.Invalid, "model patterns are always oriented to the host; orientation is ignored");
                if (string.IsNullOrWhiteSpace(it.Name) || it.Name != it.Name.Trim()) OpContext.Error(issues, o.Path + ".name", RevitIssueCodes.Invalid, "the name is empty or has leading or trailing spaces");
                else if (Kit.BadTypeName(it.Name) is string bad && bad.Contains("does not allow")) OpContext.Error(issues, o.Path + ".name", RevitIssueCodes.Invalid, bad);
                if (!seen.Add(it.Name)) OpContext.Error(issues, o.Path + ".name", RevitIssueCodes.Invalid, "pattern '" + it.Name + "' is listed twice; outputs are keyed by name, so give each pattern its own name");

                var units = drafting ? ctx.Paper : ctx.Units;
                double L(OpArgs a, string f) => units.LengthToInternal(OpArgs.ToLength(a.Get(f) ?? (JsonValue)0, units.JobUnit, a.Path + "." + f));
                double A(OpArgs a, string f) => ApiAngle(ctx.Units, a.GetAngle(f));
                if (o.GetObject("stack") is OpArgs st)
                {
                    it.Grids.Add(new GridSpec { Angle = ApiAngle(ctx.Units, 0), Offset = L(st, "height") });
                    it.Grids.Add(new GridSpec { Angle = ApiAngle(ctx.Units, 90), Offset = L(st, "width") });
                }
                else if (o.GetObject("bond") is OpArgs bd)
                {
                    double len = L(bd, "length"), h = L(bd, "height");
                    int n = bd.GetInt("fraction");
                    it.Grids.Add(new GridSpec { Angle = ApiAngle(ctx.Units, 0), Offset = h });
                    it.Grids.Add(new GridSpec { Angle = ApiAngle(ctx.Units, 90), Offset = len / n, Shift = h, Segments = ApiSegments(new[] { h, -(n - 1) * h }) });
                }
                else if (o.GetObject("lines") is OpArgs ln)
                    it.Grids.Add(new GridSpec { Angle = A(ln, "angle"), Offset = L(ln, "spacing") });
                else
                {
                    foreach (var g in o.GetObjectList("grids"))
                    {
                        var raw = g.Raw["segments"] as JsonArray ?? new JsonArray();
                        var segs = new List<double>();
                        for (int i = 0; i < raw.Count; i++)
                            segs.Add(units.LengthToInternal(OpArgs.ToLength(raw[i], units.JobUnit, JsonPath.Index(g.Path + ".segments", i))));
                        for (int i = 0; i < segs.Count; i++)
                            if (Math.Abs(segs[i]) < 1e-9 || (segs[i] > 0) != (i % 2 == 0))
                            {
                                OpContext.Error(issues, JsonPath.Index(g.Path + ".segments", i), RevitIssueCodes.Invalid, "segments alternate a dash (positive) and a gap (negative), starting with a dash; none may be 0");
                                break;
                            }
                        if (segs.Count % 2 == 1) OpContext.Error(issues, g.Path + ".segments", RevitIssueCodes.Invalid, "segments come in dash and gap pairs; the count must be even");
                        it.Grids.Add(new GridSpec { Angle = A(g, "angle"), U = L(g, "originU"), V = L(g, "originV"), Offset = L(g, "offset"), Shift = L(g, "shift"), Segments = ApiSegments(segs) });
                    }
                }
                foreach (var g in it.Grids)
                {
                    if (g.Offset <= 1e-9) OpContext.Error(issues, o.Path, RevitIssueCodes.Invalid, "every grid needs an offset greater than 0");
                    else if (!drafting && RevitUnits.ToMetres(g.Offset) < 0.02)
                        OpContext.Warning(issues, o.Path, RevitIssueCodes.Invalid, "a model pattern line spacing under 20 mm is mosaic scale and very dense on plans");
                }
                items.Add(it);
            }
            return items;
        }

        private static void Compare(Item it, FillPattern have, RevitUnits units, List<string> diffs)
        {
            double tol = units.LengthToleranceInternal;
            if (have.IsSolidFill) { diffs.Add("it is a solid fill"); return; }
            if (have.HostOrientation != it.Orientation) diffs.Add("orientation " + have.HostOrientation + ", requested " + it.Orientation);
            var grids = have.GetFillGrids();
            if (grids.Count != it.Grids.Count) { diffs.Add(grids.Count + " grid(s), requested " + it.Grids.Count); return; }
            for (int i = 0; i < grids.Count; i++)
            {
                var g = grids[i];
                var w = it.Grids[i];
                if (Math.Abs(g.Angle - w.Angle) > 1e-6) diffs.Add("grid " + i + " angle " + Kit.Num(g.Angle) + ", requested " + Kit.Num(w.Angle));
                if (Math.Abs(g.Offset - w.Offset) > tol) diffs.Add("grid " + i + " offset " + Kit.Num(units.LengthFromInternal(g.Offset)) + ", requested " + Kit.Num(units.LengthFromInternal(w.Offset)));
                if (Math.Abs(g.Shift - w.Shift) > tol) diffs.Add("grid " + i + " shift " + Kit.Num(units.LengthFromInternal(g.Shift)) + ", requested " + Kit.Num(units.LengthFromInternal(w.Shift)));
                if (Math.Abs(g.Origin.U - w.U) > tol || Math.Abs(g.Origin.V - w.V) > tol) diffs.Add("grid " + i + " origin differs");
                var segs = g.GetSegments();
                if (segs.Count != w.Segments.Count || segs.Where((s, k) => Math.Abs(s - w.Segments[k]) > tol).Any()) diffs.Add("grid " + i + " segments differ");
            }
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var items = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var map = new JsonObject();
            var states = new JsonObject();
            foreach (var it in items!)
            {
                string id;
                if (it.Existing != null && it.Matches)
                {
                    id = Kit.Id(it.Existing.Id);
                    result.Unchanged.Add(id);
                    states.Set(it.Name, "unchanged");
                }
                else if (it.Existing != null)
                {
                    try { it.Existing.SetFillPattern(Pattern(it)); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException first)
                    {
                        // SetFillPattern documents "the name already exists" as a refusal, which the element's own name
                        // may trigger: set the grids under a temporary name, then give the element its name back.
                        try
                        {
                            string name = it.Name;
                            it.Name = name + " (sa " + Guid.NewGuid().ToString("N").Substring(0, 8) + ")";
                            try { it.Existing.SetFillPattern(Pattern(it)); }
                            finally { it.Name = name; }
                            it.Existing.Name = name;
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException e)
                        {
                            throw new OpFailedException("update_not_supported", "Revit refused to update pattern '" + it.Name + "' in place: " + first.Message + " / " + e.Message +
                                ". Create it under a new name and point the materials at it with material.ensure (update:true).");
                        }
                    }
                    id = Kit.Id(it.Existing.Id);
                    result.Modified.Add(id);
                    states.Set(it.Name, "updated");
                }
                else
                {
                    FillPatternElement fpe;
                    try { fpe = FillPatternElement.Create(doc, Pattern(it)); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException e) { throw new OpFailedException("create_failed", "Revit refused pattern '" + it.Name + "': " + e.Message); }
                    id = Kit.Id(fpe.Id);
                    result.Created.Add(id);
                    states.Set(it.Name, "created");
                }
                map.Set(it.Name, id);
            }
            result.Outputs.Add("patterns", map);
            result.Readback.Add("state", states);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var map = result.Outputs["patterns"] as JsonObject ?? new JsonObject();
            var items = Requested(ctx, args, new List<Issue>());
            var list = new JsonArray();
            foreach (var o in args.GetObjectList("patterns"))
            {
                string name = o.GetString("name");
                bool drafting = o.GetString("target") == "drafting";
                var units = drafting ? ctx.Paper : ctx.Units;
                var rb = new JsonObject().Add("name", name);
                string? idText = map.GetStringOrNull(name);
                if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is FillPatternElement fpe)) { check.Fail("pattern '" + name + "' not found after the op"); continue; }
                rb.Add("id", idText);
                var fp = fpe.GetFillPattern();
                check.Text(rb, "target", fp.Target.ToString(), drafting ? FillPatternTarget.Drafting.ToString() : FillPatternTarget.Model.ToString(), "pattern '" + name + "' target");
                check.Text(rb, "name", fpe.Name, name, "pattern name");
                rb.Add("grids", fp.GridCount);
                var want = items.FirstOrDefault(i => i.Name == name && i.Target == fp.Target);
                if (want != null)
                {
                    var diffs = new List<string>();
                    Compare(want, fp, units, diffs);
                    foreach (var d in diffs) check.Fail("pattern '" + name + "': " + d);
                }
                list.Add(rb);
            }
            result.Readback.Add("patterns", list);
            check.Apply(result);
        }
    }
}
