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
    /// view.3d.create: View3D.CreateIsometric or View3D.CreatePerspective with the first ThreeDimensional view family
    /// type, then an optional camera (View3D.SetOrientation(new ViewOrientation3D(eye, up, forward))) and section box
    /// (View3D.SetSectionBox).
    ///
    /// Coordinates: eye, target and section box corners are [x, y, z] with x, y in project coordinates and z an
    /// elevation above the project base point (internal Z = job z + project base point Z), like every other
    /// elevation in a job. Up is world +Z made perpendicular to the forward direction; when looking straight up or
    /// down, up is +Y. The schema has no viewFamilyType or scale field, so neither is set.
    ///
    /// A name is required: the default "{3D}" belongs to each user's default 3D view.
    /// </summary>
    public sealed class View3DCreate : OpHandler
    {
        public override string Name => "view.3d.create";

        private sealed class Plan
        {
            public ViewSetup Setup = null!;
            public bool Perspective;
            public XYZ? Eye, Target;
            public XYZ? BoxMin, BoxMax;
        }

        private static XYZ P3(OpContext ctx, Point3 p, double baseZ) =>
            new XYZ(ctx.Units.LengthToInternal(p.X), ctx.Units.LengthToInternal(p.Y), ctx.Units.LengthToInternal(p.Z) + baseZ);

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            double baseZ = DocsUtil.BaseZ(doc);
            var p = new Plan { Perspective = args.GetBool("perspective") };
            p.Setup = ViewSetup.Build(ctx, args, ViewFamily.ThreeDimensional, ViewType.ThreeD, null, issues, hasTypeField: false, hasScaleField: false, forReadback: forReadback);
            var eye = args.GetPoint3OrNull("eye");
            var target = args.GetPoint3OrNull("target");
            if (eye.HasValue && target.HasValue)
            {
                p.Eye = P3(ctx, eye.Value, baseZ);
                p.Target = P3(ctx, target.Value, baseZ);
                if ((p.Target - p.Eye).GetLength() < ctx.Units.LengthToleranceInternal * 10)
                    OpContext.Error(issues, ctx.FieldPath(args, "target"), RevitIssueCodes.Invalid, "eye and target are the same point");
            }
            var sb = args.GetObject("sectionBox");
            if (sb != null)
            {
                var a = P3(ctx, sb.GetPoint3("min"), baseZ);
                var b = P3(ctx, sb.GetPoint3("max"), baseZ);
                if (!(b.X > a.X && b.Y > a.Y && b.Z > a.Z))
                    OpContext.Error(issues, ctx.FieldPath(args, "sectionBox"), RevitIssueCodes.Invalid, "sectionBox.max must be greater than sectionBox.min on every axis");
                p.BoxMin = a;
                p.BoxMax = b;
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        internal static XYZ UpFor(XYZ forward)
        {
            var up = XYZ.BasisZ - forward * forward.DotProduct(XYZ.BasisZ);
            return up.GetLength() < 1e-6 ? XYZ.BasisY : up.Normalize();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var view = p.Perspective ? View3D.CreatePerspective(doc, p.Setup.Type!.Id) : View3D.CreateIsometric(doc, p.Setup.Type!.Id);
            result.Created.Add(ElementIds.ToWire(view.Id));
            p.Setup.Apply(view);
            if (p.Eye != null && p.Target != null)
            {
                var forward = (p.Target - p.Eye).Normalize();
                view.SetOrientation(new ViewOrientation3D(p.Eye, UpFor(forward), forward));
            }
            if (p.BoxMin != null && p.BoxMax != null)
            {
                view.SetSectionBox(new BoundingBoxXYZ { Min = p.BoxMin, Max = p.BoxMax });
                view.IsSectionBoxActive = true;
            }
            ViewSetup.Outputs(result, view);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result) as View3D;
            if (view is null) { check.Fail("the new 3D view was not found after the op"); check.Apply(result); return; }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            double baseZ = DocsUtil.BaseZ(ctx.Doc);
            p.Setup.Readback(ctx, view, rb, check);
            check.Flag(rb, "perspective", view.IsPerspective, p.Perspective);
            if (p.Eye != null && p.Target != null)
            {
                var o = view.GetOrientation();
                rb.Set("eye", Xyz(ctx, o.EyePosition, baseZ));
                var forward = (p.Target - p.Eye).Normalize();
                if (o.ForwardDirection.DotProduct(forward) < 0.9999) check.Fail("camera forward direction differs from eye-to-target");
                if (p.Perspective && (o.EyePosition - p.Eye).GetLength() > ctx.Units.LengthToleranceInternal)
                    check.Fail("camera eye: read back " + Xyz(ctx, o.EyePosition, baseZ).ToJson() + ", requested " + Xyz(ctx, p.Eye, baseZ).ToJson());
            }
            if (p.BoxMin != null && p.BoxMax != null)
            {
                check.Flag(rb, "sectionBoxActive", view.IsSectionBoxActive, true);
                var sb = view.GetSectionBox();
                var a = sb.Transform.OfPoint(sb.Min);
                var b = sb.Transform.OfPoint(sb.Max);
                var min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
                var max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
                rb.Set("sectionBox", new JsonObject().Add("min", Xyz(ctx, min, baseZ)).Add("max", Xyz(ctx, max, baseZ)));
                double tol = ctx.Units.LengthToleranceInternal;
                if ((min - p.BoxMin).GetLength() > tol || (max - p.BoxMax).GetLength() > tol)
                    check.Fail("section box: read back " + Xyz(ctx, min, baseZ).ToJson() + " - " + Xyz(ctx, max, baseZ).ToJson() + ", requested " +
                               Xyz(ctx, p.BoxMin, baseZ).ToJson() + " - " + Xyz(ctx, p.BoxMax, baseZ).ToJson());
            }
            rb.Set("state", "created");
            check.Apply(result);
        }

        private static JsonArray Xyz(OpContext ctx, XYZ p, double baseZ) =>
            new JsonArray(ctx.Units.LengthJson(p.X), ctx.Units.LengthJson(p.Y), ctx.Units.LengthJson(p.Z - baseZ));
    }
}
