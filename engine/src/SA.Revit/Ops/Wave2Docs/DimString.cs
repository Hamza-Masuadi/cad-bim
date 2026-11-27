using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// dim.string: one string of linear dimensions in a plan, through the references a pick line crosses.
    ///
    /// <para><b>Direction.</b> The string measures along m: the start-end direction (aligned), or the view's right or
    /// up direction (horizontal, vertical), signed to point from start towards end. Only references perpendicular to
    /// m within 0.5 degree can sit in one string; others are skipped with a warning.</para>
    ///
    /// <para><b>References</b>, each with its position along m:
    /// walls (visible in the view, straight) whose location line the start-end segment crosses: the planar side faces
    /// from HostObjectUtils.GetSideFaces(Exterior / Interior) nearest the crossing, filtered by wallFaces;
    /// grids (straight, single-segment, line in the view) the segment crosses: new Reference(grid);
    /// doors and windows hosted by a wall whose location point lies on the segment within half the host wall's
    /// width: FamilyInstance.GetReferences(CenterLeftRight), or Left and Right for openings width (their positions
    /// are taken as the location point plus or minus half the Width parameter along HandOrientation). References
    /// closer than the length tolerance to one already picked are dropped, so a grid on a wall face is dimensioned
    /// once.</para>
    ///
    /// <para><b>Placement.</b> Document.Create.NewDimension(view, line, references[, type]) with the line through
    /// start moved offset to the left of m, from the first to the last reference, at the view level's elevation. The
    /// overall dimension (first and last reference, three or more references) is offset overallOffset, default
    /// offset plus 8 mm on paper times the view scale, on the same side. A dimension in the view with exactly the same
    /// references is warned about.</para>
    ///
    /// <para><b>Readback.</b> Each dimension exists in the view with one segment per gap, and each segment equals the
    /// gap between its references' positions within tolerance.</para>
    /// </summary>
    public sealed class DimString : OpHandler
    {
        public override string Name => "dim.string";

        private const double OverallGapMm = 8;
        private static readonly double Perp = Math.Sin(0.5 * Math.PI / 180);
        private static readonly double Parallel = Math.Cos(0.5 * Math.PI / 180);

        private sealed class Ref
        {
            public Reference Reference = null!;
            public double S;
            public string What = "";
        }

        private sealed class Plan
        {
            public ViewPlan View = null!;
            public DimensionType? Type;
            public XYZ Start = XYZ.Zero, M = XYZ.BasisX, N = XYZ.BasisY;
            public double Z, Offset, OverallOffset;
            public List<Ref> Refs = new List<Ref>();
            public bool Overall;
        }

        private static Line? GridLine(Grid g, View v)
        {
            try
            {
                foreach (var c in g.GetCurvesInView(DatumExtentType.ViewSpecific, v)) if (c is Line l) return l;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return g.Curve as Line;
        }

        /// <summary>Parameter t on a-b where the infinite line through c with direction dir crosses, and the distance along dir.</summary>
        private static bool Cross(XYZ a, XYZ b, XYZ c, XYZ dir, out double t, out double along)
        {
            t = along = 0;
            var ab = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            double den = ab.X * dir.Y - ab.Y * dir.X;
            if (Math.Abs(den) < 1e-12) return false;
            var ac = new XYZ(c.X - a.X, c.Y - a.Y, 0);
            t = (ac.X * dir.Y - ac.Y * dir.X) / den;
            along = (ac.X * ab.Y - ac.Y * ab.X) / den;
            return true;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan { Overall = args.GetBool("overall") };
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (args.GetStringOrNull("dimStyle") is string dt)
            {
                var linear = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>().Where(t => t.StyleType == DimensionStyleType.Linear);
                var f = Lookup.ByName(linear, dt, "linear dimension type");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "dimStyle"), f.Code, f.Error!);
                else p.Type = f.Value;
            }
            if (pending || v is null) return null;
            if (!(v is ViewPlan vp) || v.IsTemplate)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "dim.string works in plan views only; " + DocsUtil.Describe(v) + " is not one");
                return null;
            }
            p.View = vp;
            p.Z = vp.GenLevel?.ProjectElevation ?? 0;
            p.Start = u.PointToInternal(args.GetPoint2("start"), p.Z);
            var end = u.PointToInternal(args.GetPoint2("end"), p.Z);
            var d = end - p.Start;
            if (d.GetLength() <= u.LengthToleranceInternal)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "end"), RevitIssueCodes.Invalid, "start and end are the same point");
                return null;
            }
            d = d.Normalize();
            var right = DocsUtil.Flat(vp.RightDirection) ?? XYZ.BasisX;
            var up = DocsUtil.Flat(vp.UpDirection) ?? XYZ.BasisY;
            switch (args.GetString("orientation"))
            {
                case "horizontal": p.M = right.DotProduct(d) < 0 ? -right : right; break;
                case "vertical": p.M = up.DotProduct(d) < 0 ? -up : up; break;
                default: p.M = d; break;
            }
            if (Math.Abs(p.M.DotProduct(d)) < 1e-6)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "orientation"), RevitIssueCodes.Invalid, "the start-end line is perpendicular to the " + args.GetString("orientation") + " direction");
                return null;
            }
            p.N = new XYZ(-p.M.Y, p.M.X, 0);
            double scale = Math.Max(1, vp.Scale);
            p.Offset = u.LengthToInternal(args.GetLength("offset"));
            p.OverallOffset = args.GetLengthOrNull("overallOffset") is double oo ? u.LengthToInternal(oo)
                : p.Offset + Math.Sign(p.Offset == 0 ? 1 : p.Offset) * UnitUtils.ConvertToInternalUnits(OverallGapMm, UnitTypeId.Millimeters) * scale;

            var pick = new HashSet<string>(args.GetStringList("pick"));
            var skipped = new List<string>();
            var refs = new List<Ref>();
            double tol = u.LengthToleranceInternal;
            string faces = args.GetString("wallFaces");

            if (pick.Contains("walls"))
                foreach (var w in new FilteredElementCollector(doc, vp.Id).OfClass(typeof(Wall)).Cast<Wall>())
                {
                    if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line wl)) continue;
                    var a = wl.GetEndPoint(0);
                    var b = wl.GetEndPoint(1);
                    if (!Cross(p.Start, end, a, (b - a).Normalize(), out double t, out double along)) continue;
                    double len = (b - a).GetLength();
                    if (t < 0 || t > 1 || along < -w.Width / 2 || along > len + w.Width / 2) continue;
                    if (Math.Abs(wl.Direction.DotProduct(p.M)) > Perp) { skipped.Add("wall " + ElementIds.ToWire(w.Id) + " (not perpendicular to the string)"); continue; }
                    var at = p.Start + (end - p.Start) * t;
                    foreach (var shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                    {
                        if (faces == "exterior" && shell != ShellLayerType.Exterior || faces == "interior" && shell != ShellLayerType.Interior) continue;
                        IList<Reference> side;
                        try { side = HostObjectUtils.GetSideFaces(w, shell); }
                        catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                        Ref? best = null;
                        double bestDist = double.MaxValue;
                        foreach (var r in side)
                            if (w.GetGeometryObjectFromReference(r) is PlanarFace pf && Math.Abs(pf.FaceNormal.DotProduct(p.M)) > Parallel)
                            {
                                double dist = Math.Abs((pf.Origin - at).DotProduct(p.M));
                                if (dist < bestDist) { bestDist = dist; best = new Ref { Reference = r, S = (pf.Origin - p.Start).DotProduct(p.M), What = "wall " + ElementIds.ToWire(w.Id) + " " + (shell == ShellLayerType.Exterior ? "exterior" : "interior") + " face" }; }
                            }
                        if (best != null && bestDist <= w.Width + tol) refs.Add(best);
                        else skipped.Add("wall " + ElementIds.ToWire(w.Id) + " " + shell.ToString().ToLowerInvariant() + " face (not planar)");
                    }
                }

            if (pick.Contains("grids"))
                foreach (var g in new FilteredElementCollector(doc, vp.Id).OfClass(typeof(Grid)).Cast<Grid>())
                {
                    if (g.IsCurved || ElementIds.IsValid(MultiSegmentGrid.GetMultiSegementGridId(g))) continue;
                    var gl = GridLine(g, vp);
                    if (gl is null) continue;
                    var a = gl.GetEndPoint(0);
                    var b = gl.GetEndPoint(1);
                    var gd = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                    if (gd.GetLength() < 1e-9 || !Cross(p.Start, end, a, gd.Normalize(), out double t, out double along)) continue;
                    if (t < 0 || t > 1 || along < -tol || along > gd.GetLength() + tol) continue;
                    if (Math.Abs(gd.Normalize().DotProduct(p.M)) > Perp) { skipped.Add("grid '" + g.Name + "' (not perpendicular to the string)"); continue; }
                    refs.Add(new Ref { Reference = new Reference(g), S = (a - p.Start).DotProduct(p.M), What = "grid '" + g.Name + "'" });
                }

            foreach (var kind in new[] { "doors", "windows" })
            {
                if (!pick.Contains(kind)) continue;
                var bic = kind == "doors" ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows;
                foreach (var fi in new FilteredElementCollector(doc, vp.Id).OfCategory(bic).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    if (!(fi.Host is Wall host) || !(fi.Location is LocationPoint lp)) continue;
                    var at = new XYZ(lp.Point.X, lp.Point.Y, p.Z);
                    var rel = at - p.Start;
                    double t = rel.DotProduct(d) / (end - p.Start).GetLength();
                    double off = Math.Abs(rel.DotProduct(new XYZ(-d.Y, d.X, 0)));
                    if (t < 0 || t > 1 || off > host.Width / 2 + tol) continue;
                    var hand = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
                    if (hand.GetLength() < 1e-9 || Math.Abs(hand.Normalize().DotProduct(p.M)) < Parallel) { skipped.Add(kind.TrimEnd('s') + " " + ElementIds.ToWire(fi.Id) + " (not across the string)"); continue; }
                    hand = hand.Normalize();
                    double s0 = rel.DotProduct(p.M);
                    string what = kind.TrimEnd('s') + " " + ElementIds.ToWire(fi.Id);
                    if (args.GetString("openings") == "centre")
                    {
                        var r = fi.GetReferences(FamilyInstanceReferenceType.CenterLeftRight).FirstOrDefault();
                        if (r is null) { skipped.Add(what + " (its family has no centre left/right reference)"); continue; }
                        refs.Add(new Ref { Reference = r, S = s0, What = what + " centre" });
                    }
                    else
                    {
                        var wp = fi.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? fi.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                            ?? fi.get_Parameter(BuiltInParameter.WINDOW_WIDTH) ?? fi.Symbol?.get_Parameter(BuiltInParameter.WINDOW_WIDTH)
                            ?? fi.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) ?? fi.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                        var l = fi.GetReferences(FamilyInstanceReferenceType.Left).FirstOrDefault();
                        var rr = fi.GetReferences(FamilyInstanceReferenceType.Right).FirstOrDefault();
                        if (l is null || rr is null || wp is null || wp.StorageType != StorageType.Double) { skipped.Add(what + " (no Left and Right references or Width)"); continue; }
                        double half = wp.AsDouble() / 2 * Math.Sign(hand.DotProduct(p.M));
                        refs.Add(new Ref { Reference = l, S = s0 - half, What = what + " left" });
                        refs.Add(new Ref { Reference = rr, S = s0 + half, What = what + " right" });
                    }
                }
            }

            if (skipped.Count > 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "pick"), RevitIssueCodes.NotAllowed, "not dimensioned: " + string.Join(", ", skipped.Take(12)) + (skipped.Count > 12 ? " and " + (skipped.Count - 12) + " more" : ""));
            foreach (var r in refs.OrderBy(x => x.S))
                if (p.Refs.Count == 0 || r.S - p.Refs[p.Refs.Count - 1].S > tol) p.Refs.Add(r);
            if (p.Refs.Count < 2)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "pick"), RevitIssueCodes.NotFound, "the line crosses " + p.Refs.Count + " reference(s) of the picked kinds; a string needs two");
                return null;
            }
            var ids = new HashSet<string>(p.Refs.Select(r => r.Reference.ConvertToStableRepresentation(doc)));
            foreach (var dim in new FilteredElementCollector(doc, vp.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                var have = new HashSet<string>();
                foreach (Reference r in dim.References) have.Add(r.ConvertToStableRepresentation(doc));
                if (have.SetEquals(ids))
                    OpContext.Warning(issues, ctx.FieldPath(args, "start"), RevitIssueCodes.Exists, "dimension " + ElementIds.ToWire(dim.Id) + " already measures the same references in this view");
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static Line DimLine(Plan p, double offset, double s0, double s1)
        {
            var b = p.Start + p.N * offset;
            return Line.CreateBound(b + p.M * s0, b + p.M * s1);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var all = new ReferenceArray();
            foreach (var r in p.Refs) all.Append(r.Reference);
            var line = DimLine(p, p.Offset, p.Refs[0].S, p.Refs[p.Refs.Count - 1].S);
            var d1 = p.Type != null ? doc.Create.NewDimension(p.View, line, all, p.Type) : doc.Create.NewDimension(p.View, line, all);
            if (d1 is null) throw new OpFailedException("dimension_failed", "Revit did not create the dimension string");
            var ids = new List<string> { ElementIds.ToWire(d1.Id) };
            if (p.Overall && p.Refs.Count >= 3)
            {
                var ends = new ReferenceArray();
                ends.Append(p.Refs[0].Reference);
                ends.Append(p.Refs[p.Refs.Count - 1].Reference);
                var ol = DimLine(p, p.OverallOffset, p.Refs[0].S, p.Refs[p.Refs.Count - 1].S);
                var d2 = p.Type != null ? doc.Create.NewDimension(p.View, ol, ends, p.Type) : doc.Create.NewDimension(p.View, ol, ends);
                if (d2 is null) throw new OpFailedException("dimension_failed", "Revit did not create the overall dimension");
                ids.Add(ElementIds.ToWire(d2.Id));
            }
            else if (p.Overall) ctx.Warn("the string has two references, so it is already the overall dimension");
            result.Created.AddRange(ids);
            result.Outputs.Set("dimensions", JsonArray.FromStrings(ids));
            result.Outputs.Set("measurements", Values(ctx, d1));
        }

        private static List<double> Raw(Dimension d)
        {
            var values = new List<double>();
            if (d.NumberOfSegments <= 1) values.Add(d.Value ?? 0);
            else foreach (DimensionSegment s in d.Segments) values.Add(s.Value ?? 0);
            return values;
        }

        private static JsonArray Values(OpContext ctx, Dimension d) => new JsonArray(Raw(d).Select(x => ctx.Units.LengthJson(x)).ToArray());

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            double tol = ctx.Units.LengthToleranceInternal;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            var arr = new JsonArray();
            for (int k = 0; k < result.Created.Count; k++)
            {
                string idText = result.Created[k];
                var d = ElementIds.TryParse(idText, out var id) ? doc.GetElement(id) as Dimension : null;
                if (d is null) { check.Fail("dimension " + idText + " is missing after the op"); continue; }
                if (d.View?.Id != p?.View.Id && p != null) check.Fail("dimension " + idText + " is not in the requested view");
                var values = Raw(d);
                arr.Add(new JsonObject().Add("id", idText).Add("values", Values(ctx, d)));
                if (k == 0) result.Outputs.Set("measurements", Values(ctx, d));
                if (p is null) continue;
                var want = new List<double>();
                if (k == 0) for (int i = 1; i < p.Refs.Count; i++) want.Add(p.Refs[i].S - p.Refs[i - 1].S);
                else want.Add(p.Refs[p.Refs.Count - 1].S - p.Refs[0].S);
                if (values.Count != want.Count) { check.Fail("dimension " + idText + " has " + values.Count + " segment(s), expected " + want.Count); continue; }
                for (int i = 0; i < want.Count; i++)
                    if (Math.Abs(values[i] - want[i]) > tol)
                        check.Fail("dimension " + idText + " segment " + (i + 1) + " reads " + ctx.Units.LengthFromInternal(values[i]) + ", expected " + ctx.Units.LengthFromInternal(want[i]));
            }
            if (p is null) check.Fail("the references could not be found again after the op, so segment values could not be compared: " + string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            else result.Readback.Set("references", JsonArray.FromStrings(p.Refs.Select(r => r.What)));
            result.Readback.Set("dimensions", arr);
            check.Apply(result);
        }
    }
}
