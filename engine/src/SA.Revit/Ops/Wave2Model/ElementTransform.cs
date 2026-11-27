using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// element.transform: move, copy, rotate or mirror elements in plan, with ElementTransformUtils.
    ///
    /// move: MoveElements by [vector, dz]. copy: CopyElements(doc, ids, doc, translation, null) once per copy, each one
    /// vector further; that overload rehosts hosted elements, which the translation-only overload is documented not to
    /// do. rotate: RotateElements about the vertical through centre. mirror: MirrorElements about the vertical plane
    /// through axis, with mirrorCopies = keepOriginal; CanMirrorElements is checked first.
    ///
    /// Refused: pinned elements (the move is documented to fail), group members (transform the group), element types,
    /// view-specific elements. Declared as modified for move, rotate and mirror in place: the elements, the hosts of
    /// hosted instances, and the inserts of moved hosts (they move with them). Copies declare the hosts of hosted
    /// instances, which receive new inserts. Walls joined to a moved wall change too; that side effect is not declared.
    ///
    /// Readback: the location point or the location line's ends of every element, against the transformed positions
    /// worked out before the change (ends compared in either order, because a mirrored line can come back reversed).
    /// For copies, each original with a location must have, among the new ids of its copy, an element of the same type
    /// at the expected position. Elements with no location point or line are counted, not checked.
    /// </summary>
    public sealed class ElementTransform : OpHandler
    {
        public override string Name => "element.transform";

        private sealed class Plan
        {
            public string Action = "";
            public List<Element> Elements = new List<Element>();
            public XYZ Vector = XYZ.Zero;
            public int Copies = 1;
            public XYZ Centre = XYZ.Zero;
            public double Angle;
            public Plane? Mirror;
            public bool KeepOriginal;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan { Action = args.GetString("action") };
            var seen = new HashSet<long>();
            foreach (var kv in Kit.IdList(ctx, args, "elements", out _))
            {
                if (!ElementIds.TryParse(kv.Value, out var id) || !(doc.GetElement(id) is Element e)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist"); continue; }
                if (!seen.Add(ElementIds.ToLong(id))) continue;
                if (e is ElementType) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is a type, not a placed element");
                else if (e.Pinned) OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, "element " + kv.Value + " is pinned; unpin it first");
                else if (ElementIds.IsValid(e.GroupId)) OpContext.Error(issues, kv.Key, RevitIssueCodes.NotAllowed, "element " + kv.Value + " is in group " + Kit.Id(e.GroupId) + "; transform the group instead");
                else if (ElementIds.IsValid(e.OwnerViewId)) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is view-specific; this op transforms model elements");
                else p.Elements.Add(e);
            }

            void Need(string field, string why)
            {
                if (!args.Has(field)) OpContext.Error(issues, ctx.FieldPath(args, field), RevitIssueCodes.Invalid, why);
            }
            switch (p.Action)
            {
                case "move":
                case "copy":
                    Need("vector", p.Action + " needs vector");
                    if (args.Has("vector")) p.Vector = u.PointToInternal(args.GetPoint2("vector"), u.LengthToInternal(args.GetLength("dz")));
                    if (args.Has("vector") && p.Vector.GetLength() < u.LengthToleranceInternal) OpContext.Error(issues, ctx.FieldPath(args, "vector"), RevitIssueCodes.Invalid, "the displacement is zero");
                    p.Copies = p.Action == "copy" ? args.GetInt("copies") : 1;
                    break;
                case "rotate":
                    Need("centre", "rotate needs centre");
                    Need("angle", "rotate needs angle");
                    if (args.Has("centre")) p.Centre = u.PointToInternal(args.GetPoint2("centre"), 0);
                    if (args.Has("angle")) p.Angle = u.AngleToInternal(args.GetAngle("angle"));
                    if (args.Has("angle") && Math.Abs(p.Angle) < 1e-9) OpContext.Error(issues, ctx.FieldPath(args, "angle"), RevitIssueCodes.Invalid, "the angle is zero");
                    break;
                case "mirror":
                    Need("axis", "mirror needs axis");
                    p.KeepOriginal = args.GetBool("keepOriginal");
                    if (args.GetObject("axis") is OpArgs ax)
                    {
                        var a = u.PointToInternal(ax.GetPoint2("start"), 0);
                        var b = u.PointToInternal(ax.GetPoint2("end"), 0);
                        var d = b - a;
                        if (d.GetLength() < u.LengthToleranceInternal) OpContext.Error(issues, ax.Path, RevitIssueCodes.Invalid, "the axis start and end are the same point");
                        else p.Mirror = Plane.CreateByNormalAndOrigin(new XYZ(d.Y, -d.X, 0).Normalize(), a);
                    }
                    if (p.Elements.Count > 0 && p.Mirror != null && !ElementTransformUtils.CanMirrorElements(doc, p.Elements.Select(e => e.Id).ToList()))
                        OpContext.Error(issues, ctx.FieldPath(args, "elements"), RevitIssueCodes.NotAllowed, "Revit cannot mirror this set of elements");
                    break;
            }
            foreach (var f in new[] { "vector", "centre", "axis" })
            {
                bool used = (f == "vector" && (p.Action == "move" || p.Action == "copy")) || (f == "centre" && p.Action == "rotate") || (f == "axis" && p.Action == "mirror");
                if (args.Has(f) && !used) OpContext.Warning(issues, ctx.FieldPath(args, f), RevitIssueCodes.Invalid, f + " is not used by " + p.Action + "; it is ignored");
            }

            bool inPlace = p.Action == "move" || p.Action == "rotate" || (p.Action == "mirror" && !p.KeepOriginal);
            var listed = new HashSet<long>(p.Elements.Select(e => ElementIds.ToLong(e.Id)));
            foreach (var e in p.Elements)
            {
                if (inPlace) ctx.DeclareModifies(e.Id);
                if (e is FamilyInstance fi && fi.Host != null)
                {
                    ctx.DeclareModifies(fi.Host.Id);
                    if (!inPlace && !listed.Contains(ElementIds.ToLong(fi.Host.Id)))
                        OpContext.Warning(issues, ctx.FieldPath(args, "elements"), RevitIssueCodes.Unchecked, "element " + Kit.Id(e.Id) + " is hosted by " + Kit.Id(fi.Host.Id) + "; Revit picks the host of each copy, which is checked only by position");
                }
                if (inPlace && e is HostObject ho)
                    foreach (var ins in ho.FindInserts(true, true, true, true)) ctx.DeclareModifies(ins);
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static bool Loc(Element e, out XYZ a, out XYZ? b)
        {
            a = XYZ.Zero;
            b = null;
            try
            {
                switch (e.Location)
                {
                    case LocationPoint lp: a = lp.Point; return true;
                    case LocationCurve lc: a = lc.Curve.GetEndPoint(0); b = lc.Curve.GetEndPoint(1); return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return false;
        }

        private static JsonObject Expect(RevitUnits u, Element e, Transform t, bool facing = false)
        {
            var o = new JsonObject().Add("id", Kit.Id(e.Id)).Add("type", Kit.Id(e.GetTypeId()));
            if (Loc(e, out var a, out var b))
            {
                o.Add("a", u.PointJson(t.OfPoint(a)));
                if (b != null) o.Add("b", u.PointJson(t.OfPoint(b)));
            }
            if (facing && e is FamilyInstance fi && e.Location is LocationPoint)
            {
                var f = t.OfVector(fi.FacingOrientation);
                o.Add("facing", new JsonArray(RevitUnits.Num(f.X), RevitUnits.Num(f.Y)));
            }
            return o;
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var ids = p!.Elements.Select(e => e.Id).ToList();
            var outIds = new List<string>();
            var expected = new JsonArray();
            var copies = new JsonArray();
            try
            {
                switch (p.Action)
                {
                    case "move":
                        foreach (var e in p.Elements) expected.Add(Expect(u, e, Transform.CreateTranslation(p.Vector)));
                        ElementTransformUtils.MoveElements(doc, ids, p.Vector);
                        break;
                    case "rotate":
                        foreach (var e in p.Elements) expected.Add(Expect(u, e, Transform.CreateRotationAtPoint(XYZ.BasisZ, p.Angle, p.Centre), facing: true));
                        ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(p.Centre, p.Centre + XYZ.BasisZ), p.Angle);
                        break;
                    case "mirror":
                        foreach (var e in p.Elements) expected.Add(Expect(u, e, Transform.CreateReflection(p.Mirror!)));
                        var mirrored = ElementTransformUtils.MirrorElements(doc, ids, p.Mirror!, p.KeepOriginal);
                        if (p.KeepOriginal)
                        {
                            if (mirrored is null || mirrored.Count == 0) throw new OpFailedException("transform_failed", "Revit made no mirrored copies");
                            copies.Add(Ids(mirrored.Select(Kit.Id)));
                            outIds.AddRange(mirrored.Select(Kit.Id));
                        }
                        break;
                    case "copy":
                        foreach (var e in p.Elements) expected.Add(Expect(u, e, Transform.Identity));
                        for (int k = 1; k <= p.Copies; k++)
                        {
                            var made = ElementTransformUtils.CopyElements(doc, ids, doc, Transform.CreateTranslation(p.Vector.Multiply(k)), null);
                            if (made is null || made.Count == 0) throw new OpFailedException("transform_failed", "Revit made no elements for copy " + k);
                            copies.Add(Ids(made.Select(Kit.Id)));
                            outIds.AddRange(made.Select(Kit.Id));
                        }
                        break;
                }
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException e)
            {
                throw new OpFailedException("transform_failed", "Revit refused the " + p.Action + ": " + e.Message);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException e)
            {
                throw new OpFailedException("transform_failed", "Revit could not " + p.Action + " the elements: " + e.Message);
            }
            bool inPlace = outIds.Count == 0;
            if (inPlace)
            {
                outIds.AddRange(ids.Select(Kit.Id));
                result.Modified.AddRange(outIds);
            }
            else result.Created.AddRange(outIds);
            result.Outputs.Add("elements", Ids(outIds));
            result.Outputs.Add("count", outIds.Count);
            result.Readback.Add("expected", expected);
            if (copies.Count > 0) result.Readback.Add("copies", copies);
        }

        private static bool Same(Point2 a, Point2 b, double tol) => (a - b).Length <= tol;

        private static bool Matches(RevitUnits u, Element e, JsonObject want, Point2 shift, bool eitherOrder)
        {
            if (!Loc(e, out var a, out var b) || !(want["a"] is JsonValue wa)) return false;
            double tol = u.LengthTolerance;
            var pa = u.PointFromInternal(a);
            var ea = OpArgs.ToPoint2(wa, u.JobUnit) + shift;
            if (b is null) return !want.ContainsKey("b") && Same(pa, ea, tol);
            if (!(want["b"] is JsonValue wb)) return false;
            var pb = u.PointFromInternal(b);
            var eb = OpArgs.ToPoint2(wb, u.JobUnit) + shift;
            return (Same(pa, ea, tol) && Same(pb, eb, tol)) || (eitherOrder && Same(pa, eb, tol) && Same(pb, ea, tol));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string action = args.GetString("action");
            var expected = (result.Readback["expected"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
            var copies = (result.Readback["copies"] as JsonArray ?? new JsonArray()).OfType<JsonArray>().ToList();
            int unchecked_ = expected.Count(o => !o.ContainsKey("a"));
            bool mirror = action == "mirror";
            if (copies.Count == 0)
            {
                foreach (var want in expected.Where(o => o.ContainsKey("a")))
                {
                    string id = want.GetStringOrNull("id") ?? "";
                    if (!ElementIds.TryParse(id, out var eid) || !(doc.GetElement(eid) is Element e)) { check.Fail("element " + id + " no longer exists"); continue; }
                    if (!Matches(u, e, want, new Point2(0, 0), mirror)) check.Fail("element " + id + " is not at the expected position after " + action);
                    if (want["facing"] is JsonArray wf && wf.Count == 2 && wf[0] is JsonNumber fx && wf[1] is JsonNumber fy && e is FamilyInstance fi)
                    {
                        var have = fi.FacingOrientation;
                        double cross = have.X * fy.Value - have.Y * fx.Value, dot = have.X * fx.Value + have.Y * fy.Value;
                        if (Math.Abs(Math.Atan2(cross, dot)) > 1e-3) check.Fail("element " + id + " does not face the expected direction after " + action);
                    }
                }
            }
            else
            {
                var vector = action == "copy" ? args.GetPoint2("vector") : new Point2(0, 0);
                for (int k = 0; k < copies.Count; k++)
                {
                    var made = copies[k].OfType<JsonString>()
                        .Select(s => ElementIds.TryParse(s.Value, out var id) ? doc.GetElement(id) : null)
                        .Where(e => e != null).Cast<Element>().ToList();
                    if (made.Count < copies[k].Count) check.Fail((copies[k].Count - made.Count) + " element(s) of copy " + (k + 1) + " no longer exist");
                    var shift = new Point2(vector.X * (k + 1), vector.Y * (k + 1));
                    int missing = 0;
                    foreach (var want in expected.Where(o => o.ContainsKey("a")))
                    {
                        string type = want.GetStringOrNull("type") ?? "";
                        if (!made.Any(e => Kit.Id(e.GetTypeId()) == type && Matches(u, e, want, shift, mirror))) missing++;
                    }
                    if (missing > 0) check.Fail(missing + " element(s) have no copy of the same type at the expected position in copy " + (k + 1));
                }
                result.Readback.Add("copiesMade", copies.Count);
            }
            result.Readback.Add("positionsChecked", expected.Count - unchecked_);
            if (unchecked_ > 0) result.Readback.Add("withoutLocation", unchecked_);
            check.Apply(result);
        }
    }
}
