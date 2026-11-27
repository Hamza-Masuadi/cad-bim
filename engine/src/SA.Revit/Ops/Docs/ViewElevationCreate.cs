using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// view.elevation.create: an elevation marker at a plan point and one elevation view on it.
    ///
    /// API: ElevationMarker.CreateElevationMarker(doc, viewFamilyTypeId, origin, initialViewScale), then
    /// marker.CreateElevation(doc, planViewId, index). The API says only that the "view direction is determined by the
    /// index"; it does not say which index faces which way. So the handler does not assume a mapping: it creates the
    /// view at index 0, regenerates, reads the view's looking direction (-View.ViewDirection), and rotates the marker
    /// about a vertical axis through its origin (ElementTransformUtils.RotateElement) by the signed angle to the
    /// requested direction. Readback then checks the looking direction within 0.01 degree.
    ///
    /// Direction convention: <c>direction</c> is the way the view LOOKS, in project coordinates: north = +Y,
    /// east = +X. A view that looks north shows the building's south face (what drawings usually call the
    /// "South Elevation"). True north (the survey point's angle) is not used.
    ///
    /// The marker sits at the plan view's level elevation. The view's scale is the requested scale, else the plan
    /// view's scale. The marker element and the view are both reported as created.
    /// </summary>
    public sealed class ViewElevationCreate : OpHandler
    {
        public override string Name => "view.elevation.create";

        private sealed class Plan
        {
            public ViewSetup Setup = null!;
            public ViewPlan? PlanView;
            public Point2 Point;
            public XYZ Look = XYZ.BasisY;
        }

        internal static XYZ Direction(string d)
        {
            switch (d)
            {
                case "north": return XYZ.BasisY;
                case "south": return XYZ.BasisY.Negate();
                case "east": return XYZ.BasisX;
                default: return XYZ.BasisX.Negate();
            }
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var p = new Plan { Point = args.GetPoint2("point"), Look = Direction(args.GetString("direction")) };
            p.Setup = ViewSetup.Build(ctx, args, ViewFamily.Elevation, ViewType.Elevation, null, issues, forReadback: forReadback);
            var v = DocsUtil.View(ctx, args.Get("planView"), ctx.FieldPath(args, "planView"), issues, out bool pending);
            if (!pending && v != null)
            {
                if (!(v is ViewPlan vp) || v.IsTemplate)
                    OpContext.Error(issues, ctx.FieldPath(args, "planView"), RevitIssueCodes.Invalid, "planView " + DocsUtil.Describe(v) + " is not a plan view; elevation markers are placed in plans");
                else p.PlanView = vp;
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            if (p.PlanView is null) throw new OpFailedException("validation_failed", "planView did not resolve to a plan view");
            var doc = ctx.Doc;
            double z = p.PlanView.GenLevel?.ProjectElevation ?? 0;
            var origin = ctx.Units.PointToInternal(p.Point, z);
            int scale = p.Setup.Scale ?? p.PlanView.Scale;
            var marker = ElevationMarker.CreateElevationMarker(doc, p.Setup.Type!.Id, origin, scale);
            result.Created.Add(ElementIds.ToWire(marker.Id));
            var view = marker.CreateElevation(doc, p.PlanView.Id, 0);
            result.Created.Add(ElementIds.ToWire(view.Id));
            doc.Regenerate();
            var actual = DocsUtil.Flat(view.ViewDirection.Negate()) ?? throw new OpFailedException("elevation_direction", "the new elevation does not look horizontally");
            double angle = DocsUtil.SignedAngle(actual, p.Look);
            if (Math.Abs(angle) > 1e-9)
            {
                ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), angle);
                doc.Regenerate();
            }
            p.Setup.Apply(view);
            ViewSetup.Outputs(result, view);
            result.Outputs.Set("name", view.Name);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result) as ViewSection;
            if (view is null) { check.Fail("the new elevation was not found after the op"); check.Apply(result); return; }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            p.Setup.Readback(ctx, view, rb, check);
            if (view.ViewType != ViewType.Elevation) check.Fail("view type: read back " + view.ViewType + ", requested Elevation");
            var look = DocsUtil.Flat(view.ViewDirection.Negate());
            if (look is null) check.Fail("the elevation does not look horizontally");
            else
            {
                rb.Set("look", DocsUtil.Xy(look.X, look.Y));
                double deg = Math.Abs(DocsUtil.SignedAngle(look, p.Look)) * 180 / Math.PI;
                if (deg > 0.01) check.Fail("looking direction is " + deg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " degrees off " + args.GetString("direction"));
            }
            check.Text(rb, "direction", args.GetString("direction"), null);
            var marker = result.Created.Select(id => ElementIds.TryParse(id, out var e) ? ctx.Doc.GetElement(e) as ElevationMarker : null).FirstOrDefault(m => m != null);
            if (marker is null) check.Fail("the elevation marker was not found after the op");
            else
            {
                rb.Set("marker", ElementIds.ToWire(marker.Id));
                var bb = p.PlanView != null ? marker.get_BoundingBox(p.PlanView) : null;
                if (bb != null)
                {
                    // The marker symbol's extent is not documented to be centred on its origin, so the check is
                    // "the requested point lies inside the marker's extent in the plan", not equality.
                    check.Point(rb, "markerCentre", (bb.Min + bb.Max) * 0.5, null);
                    var q = ctx.Units.PointToInternal(p.Point, 0);
                    double tol = ctx.Units.LengthToleranceInternal;
                    if (q.X < bb.Min.X - tol || q.X > bb.Max.X + tol || q.Y < bb.Min.Y - tol || q.Y > bb.Max.Y + tol)
                        check.Fail("the marker's extent in the plan does not contain the requested point");
                }
                else check.Fail("the marker has no extent in the plan view, so its position could not be read back");
            }
            rb.Set("state", "created");
            check.Apply(result);
        }
    }
}
