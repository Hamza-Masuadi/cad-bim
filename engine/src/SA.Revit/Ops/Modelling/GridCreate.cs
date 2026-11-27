using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// grid.create: straight grids from explicit lines, from an orthogonal definition, or both.
    ///
    /// Orthogonal grids: vertical grids are numbered from xLabelStart left to right and drawn bottom to top;
    /// horizontal grids are lettered from yLabelStart bottom to top and drawn right to left, so the grid head (Revit
    /// puts it at the end point by default) sits at the top and at the left, the usual convention. Letter sequences
    /// skip the letters in skipLetters, by default I and O, which read as 1 and 0 on drawings (an empty list uses every
    /// letter); the start labels are used as given. Without "extension" the grids run 2000 mm (converted to the
    /// job unit) past the outer grids. The whole set is rotated about the origin.
    ///
    /// Refused: a name that already exists (Revit grid names are unique), two grids with the same name in the job,
    /// and a grid lying on an existing grid line. Readback compares every grid's name, end points and type.
    /// </summary>
    public sealed class GridCreate : OpHandler
    {
        public override string Name => "grid.create";

        private sealed class GridLine
        {
            public string Label = "";
            public Point2 Start, End;
            public string Path = "";
        }

        private sealed class Plan
        {
            public List<GridLine> Lines = new List<GridLine>();
            public GridType? Type;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            foreach (var l in args.GetObjectList("lines"))
                p.Lines.Add(new GridLine { Label = l.GetString("label").Trim(), Start = l.GetPoint2("start"), End = l.GetPoint2("end"), Path = l.Path });

            var o = args.GetObject("orthogonal");
            if (o != null) Orthogonal(ctx, o, p, issues);

            if (args.GetStringOrNull("gridType") is string gt)
            {
                var t = Lookup.Type<GridType>(doc, gt, "grid type");
                if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "gridType"), t.Code, t.Error!);
                else p.Type = t.Value;
            }

            var existing = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            var names = new Dictionary<string, Grid>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in existing) names[g.Name] = g;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double shortest = doc.Application.ShortCurveTolerance;
            double tolInternal = u.LengthToleranceInternal;
            foreach (var g in p.Lines)
            {
                if (g.Label.Length == 0) { OpContext.Error(issues, g.Path + ".label", RevitIssueCodes.Invalid, "grid name is empty"); continue; }
                if (Kit.BadTypeName(g.Label) is string bad && bad.Contains("Revit does not allow"))
                    OpContext.Error(issues, g.Path + ".label", RevitIssueCodes.Invalid, "grid '" + g.Label + "': " + bad);
                if (!seen.Add(g.Label)) OpContext.Error(issues, g.Path + ".label", RevitIssueCodes.Invalid, "grid '" + g.Label + "' is listed twice in this op");
                if (names.TryGetValue(g.Label, out var clash))
                    OpContext.Error(issues, g.Path + ".label", RevitIssueCodes.Exists, "a grid named '" + clash.Name + "' already exists (id " + Kit.Id(clash.Id) + "); grid names must be unique");
                double len = u.LengthToInternal(g.Start.DistanceTo(g.End));
                if (len <= shortest) { OpContext.Error(issues, g.Path, RevitIssueCodes.Invalid, "grid '" + g.Label + "' is too short for Revit"); continue; }
                if ((Kit.CoordinateError(g.Start, u) ?? Kit.CoordinateError(g.End, u)) is string far) OpContext.Error(issues, g.Path, RevitIssueCodes.Invalid, "grid '" + g.Label + "': " + far);
                // A new grid on top of an existing grid line duplicates it.
                var a = u.PointToInternal(g.Start, 0);
                var b = u.PointToInternal(g.End, 0);
                foreach (var e in existing)
                {
                    if (!(e.Curve is Line el)) continue;
                    var ea = new XYZ(el.GetEndPoint(0).X, el.GetEndPoint(0).Y, 0);
                    var eb = new XYZ(el.GetEndPoint(1).X, el.GetEndPoint(1).Y, 0);
                    var unbound = Line.CreateUnbound(ea, (eb - ea).Normalize());
                    if (unbound.Distance(a) <= tolInternal && unbound.Distance(b) <= tolInternal)
                    {
                        OpContext.Error(issues, g.Path, RevitIssueCodes.Exists, "grid '" + g.Label + "' lies on the line of existing grid '" + e.Name + "' (id " + Kit.Id(e.Id) + ")");
                        break;
                    }
                }
            }
            if (p.Lines.Count == 0) OpContext.Error(issues, args.Path, RevitIssueCodes.Invalid, "no grids to create");
            return Kit.HasErrors(issues) ? null : p;
        }

        private static void Orthogonal(OpContext ctx, OpArgs o, Plan p, List<Issue> issues)
        {
            var u = ctx.Units;
            var origin = o.GetPoint2("origin");
            var xs = o.GetLengthList("xSpacings");
            var ys = o.GetLengthList("ySpacings");
            double ext = o.GetLengthOrNull("extension") ?? SA.Core.Units.Length.Convert(2000, SA.Core.Units.LengthUnit.Millimetre, u.JobUnit);
            double rot = u.AngleToInternal(o.GetAngle("rotation"));
            if (xs.Any(s => s <= 0)) { OpContext.Error(issues, o.Path + ".xSpacings", RevitIssueCodes.Invalid, "spacings must be greater than zero"); return; }
            if (ys.Any(s => s <= 0)) { OpContext.Error(issues, o.Path + ".ySpacings", RevitIssueCodes.Invalid, "spacings must be greater than zero"); return; }
            double width = xs.Sum(), depth = ys.Sum();
            string xl = o.GetString("xLabelStart").Trim(), yl = o.GetString("yLabelStart").Trim();
            if (xl.Length == 0 || yl.Length == 0) { OpContext.Error(issues, o.Path, RevitIssueCodes.Invalid, "label starts must not be empty"); return; }
            var skip = new HashSet<char>();
            foreach (var s in o.GetStringList("skipLetters"))
            {
                string t = s.Trim();
                if (t.Length != 1 || !(char.ToUpperInvariant(t[0]) >= 'A' && char.ToUpperInvariant(t[0]) <= 'Z'))
                { OpContext.Error(issues, o.Path + ".skipLetters", RevitIssueCodes.Invalid, "'" + s + "' is not a single letter A-Z"); return; }
                skip.Add(char.ToUpperInvariant(t[0]));
            }
            if (skip.Count == 26) { OpContext.Error(issues, o.Path + ".skipLetters", RevitIssueCodes.Invalid, "skipLetters leaves no letter to name grids with"); return; }
            Point2 R(double x, double y) => origin + new Point2(x, y).Rotate(rot);
            double x0 = 0;
            string label = xl;
            for (int i = 0; i <= xs.Count; i++)
            {
                p.Lines.Add(new GridLine { Label = label, Start = R(x0, -ext), End = R(x0, depth + ext), Path = o.Path + ".xSpacings" });
                if (i < xs.Count) { x0 += xs[i]; label = Next(label, skip); }
            }
            double y0 = 0;
            label = yl;
            for (int j = 0; j <= ys.Count; j++)
            {
                p.Lines.Add(new GridLine { Label = label, Start = R(width + ext, y0), End = R(-ext, y0), Path = o.Path + ".ySpacings" });
                if (j < ys.Count) { y0 += ys[j]; label = Next(label, skip); }
            }
        }

        /// <summary>
        /// The next grid name: "1" to "2", "01" to "02", "A" to "B", "H" to "J" (with I skipped), "Z" to "AA", "A1" to
        /// "A2". Letters in <paramref name="skip"/> (upper case; it must leave at least one letter) are never produced.
        /// </summary>
        internal static string Next(string label, ISet<char> skip)
        {
            var m = Regex.Match(label, @"^(.*?)(\d+)$");
            if (m.Success)
            {
                string digits = m.Groups[2].Value;
                long n = long.Parse(digits, CultureInfo.InvariantCulture) + 1;
                return m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
            }
            m = Regex.Match(label, @"^(.*?)([A-Za-z]+)$");
            if (!m.Success) return label + "1";
            var chars = m.Groups[2].Value.ToCharArray();
            bool lower = char.IsLower(chars[chars.Length - 1]);
            char first = 'A';
            while (skip.Contains(first)) first++;
            int k = chars.Length - 1;
            while (true)
            {
                char c = char.ToUpperInvariant(chars[k]);
                bool carry = false;
                do
                {
                    if (c == 'Z') { c = 'A'; carry = true; }
                    else c = (char)(c + 1);
                } while (skip.Contains(c));
                chars[k] = lower ? char.ToLowerInvariant(c) : c;
                if (!carry) break;
                if (k == 0) return m.Groups[1].Value + (lower ? char.ToLowerInvariant(first) : first) + new string(chars);
                k--;
            }
            return m.Groups[1].Value + new string(chars);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var map = new JsonObject();
            foreach (var g in p!.Lines)
            {
                var grid = Grid.Create(doc, Line.CreateBound(u.PointToInternal(g.Start, 0), u.PointToInternal(g.End, 0)));
                grid.Name = g.Label;
                if (p.Type != null && grid.GetTypeId() != p.Type.Id) grid.ChangeTypeId(p.Type.Id);
                string id = Kit.Id(grid.Id);
                result.Created.Add(id);
                map.Add(g.Label, id);
            }
            result.Outputs.Add("grids", map);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            // After commit the names exist, so Build would report them as clashes; rebuild the requested lines without the checks.
            var wanted = Requested(ctx, args);
            var map = result.Outputs["grids"] as JsonObject ?? new JsonObject();
            var arr = new JsonArray();
            foreach (var g in wanted)
            {
                string? idText = map.GetStringOrNull(g.Label);
                if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Grid grid))
                {
                    check.Fail("grid '" + g.Label + "' not found after the op");
                    continue;
                }
                var rb = new JsonObject().Add("id", idText);
                check.Text(rb, "name", grid.Name, g.Label, "grid '" + g.Label + "' name");
                if (grid.Curve is Line line)
                {
                    check.Point(rb, "start", line.GetEndPoint(0), g.Start, "grid '" + g.Label + "' start");
                    check.Point(rb, "end", line.GetEndPoint(1), g.End, "grid '" + g.Label + "' end");
                }
                else check.Fail("grid '" + g.Label + "' is not straight");
                var t = doc.GetElement(grid.GetTypeId());
                check.Text(rb, "type", t?.Name, args.GetStringOrNull("gridType") is string gt ? Lookup.Type<GridType>(doc, gt, "grid type").Value?.Name : null, "grid '" + g.Label + "' type");
                arr.Add(rb);
            }
            if (arr.Count != wanted.Count) check.Fail(arr.Count + " grid(s) read back, expected " + wanted.Count);
            result.Readback.Add("count", arr.Count);
            result.Readback.Add("grids", arr);
            check.Apply(result);
        }

        private static List<GridLine> Requested(OpContext ctx, OpArgs args)
        {
            var p = new Plan();
            foreach (var l in args.GetObjectList("lines"))
                p.Lines.Add(new GridLine { Label = l.GetString("label").Trim(), Start = l.GetPoint2("start"), End = l.GetPoint2("end"), Path = l.Path });
            var o = args.GetObject("orthogonal");
            if (o != null) Orthogonal(ctx, o, p, new List<Issue>());
            return p.Lines;
        }
    }
}
