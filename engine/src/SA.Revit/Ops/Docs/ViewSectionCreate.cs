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
    /// view.section.create: a building section from a cut line in plan, through ViewSection.CreateSection(doc,
    /// viewFamilyTypeId, BoundingBoxXYZ).
    ///
    /// Geometry convention (all in internal units, project coordinates):
    /// <list type="bullet">
    /// <item>d = unit vector from start to end in plan. The view looks to the LEFT of start-to-end: look = (-d.y, d.x, 0);
    /// with flip it looks the other way (look = -look).</item>
    /// <item>The section box transform: Origin = midpoint of the cut line at Z = 0; BasisY = +Z (up);
    /// BasisZ = -look (CreateSection documents "the view direction of the resulting section will be
    /// sectionBox.Transform.BasisZ", and View.ViewDirection is "the direction towards the viewer");
    /// BasisX = BasisY x BasisZ, which is d when not flipped. The basis is right-handed and orthonormal, as the
    /// method requires.</item>
    /// <item>Box Min = (-L/2, bottom, -depth), Max = (L/2, top, 0), where L is the cut line length and bottom/top
    /// are internal Z (job elevation + project base point Z). Local Z = 0 is the cut plane; local -depth lies
    /// <c>depth</c> ahead in the looking direction, so far clip = Max.Z - Min.Z = depth, as the method documents.</item>
    /// </list>
    /// Readback checks the view direction, the right direction, that the cut plane passes through the cut line
    /// (View.Origin projected on look), the crop width and height, the bottom elevation and the far clip offset.
    /// </summary>
    public sealed class ViewSectionCreate : OpHandler
    {
        public override string Name => "view.section.create";

        private sealed class Plan
        {
            public ViewSetup Setup = null!;
            public Point2 Start, End;
            public double Depth, Bottom, Top; // job units; bottom/top are elevations above the project base point
            public bool Flip;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan
            {
                Start = args.GetPoint2("start"), End = args.GetPoint2("end"),
                Depth = args.GetLength("depth"), Bottom = args.GetLength("bottom"), Top = args.GetLength("top"), Flip = args.GetBool("flip"),
            };
            p.Setup = ViewSetup.Build(ctx, args, ViewFamily.Section, ViewType.Section, null, issues, forReadback: forReadback);
            double len = ctx.Units.LengthToInternal((p.End - p.Start).Length);
            if (len <= doc.Application.ShortCurveTolerance)
                OpContext.Error(issues, ctx.FieldPath(args, "end"), RevitIssueCodes.Invalid, "the cut line is too short");
            if (!(p.Top > p.Bottom))
                OpContext.Error(issues, ctx.FieldPath(args, "top"), RevitIssueCodes.Invalid, "top must be above bottom");
            if (!(p.Depth > 0))
                OpContext.Error(issues, ctx.FieldPath(args, "depth"), RevitIssueCodes.Invalid, "depth must be positive");
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static void Frame(OpContext ctx, Plan p, out XYZ d, out XYZ look, out XYZ mid, out double len)
        {
            var s = ctx.Units.PointToInternal(p.Start, 0);
            var e = ctx.Units.PointToInternal(p.End, 0);
            len = (e - s).GetLength();
            d = (e - s).Normalize();
            look = new XYZ(-d.Y, d.X, 0);
            if (p.Flip) look = look.Negate();
            mid = (s + e) * 0.5;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            Frame(ctx, p, out var d, out var look, out var mid, out double len);
            double baseZ = DocsUtil.BaseZ(doc);
            var basisY = XYZ.BasisZ;
            var basisZ = look.Negate();
            var basisX = basisY.CrossProduct(basisZ).Normalize();
            var t = Transform.CreateTranslation(mid);
            t.BasisX = basisX;
            t.BasisY = basisY;
            t.BasisZ = basisZ;
            var box = new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(-len / 2, ctx.Units.LengthToInternal(p.Bottom) + baseZ, -ctx.Units.LengthToInternal(p.Depth)),
                Max = new XYZ(len / 2, ctx.Units.LengthToInternal(p.Top) + baseZ, 0),
            };
            var view = ViewSection.CreateSection(doc, p.Setup.Type!.Id, box);
            result.Created.Add(ElementIds.ToWire(view.Id));
            p.Setup.Apply(view);
            ViewSetup.Outputs(result, view);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result) as ViewSection;
            if (view is null) { check.Fail("the new section was not found after the op"); check.Apply(result); return; }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            var doc = ctx.Doc;
            p.Setup.Readback(ctx, view, rb, check);
            Frame(ctx, p, out var d, out var look, out var mid, out double len);
            double baseZ = DocsUtil.BaseZ(doc);
            if (view.ViewType != ViewType.Section) check.Fail("view type: read back " + view.ViewType + ", requested Section");
            var lookRead = view.ViewDirection.Negate();
            rb.Set("look", new JsonArray(RevitUnits.Num(lookRead.X), RevitUnits.Num(lookRead.Y), RevitUnits.Num(lookRead.Z)));
            if (lookRead.DotProduct(look) < 0.9999) check.Fail("looking direction differs from the request (read back " + lookRead + ", expected " + look + ")");
            var right = XYZ.BasisZ.CrossProduct(look.Negate());
            if (view.RightDirection.DotProduct(right) < 0.9999) check.Fail("right direction differs from the request (read back " + view.RightDirection + ")");
            double offPlane = Math.Abs((view.Origin - mid).DotProduct(look));
            rb.Set("cutPlaneOffset", ctx.Units.LengthJson(offPlane));
            if (offPlane > ctx.Units.LengthToleranceInternal) check.Fail("the cut plane is " + ctx.Units.LengthFromInternal(offPlane) + " off the cut line");
            var cb = view.CropBox;
            check.Length(rb, "width", cb.Max.X - cb.Min.X, (p.End - p.Start).Length, "crop width");
            check.Length(rb, "height", cb.Max.Y - cb.Min.Y, p.Top - p.Bottom, "crop height");
            var bottomWorld = cb.Transform.OfPoint(cb.Min).Z - baseZ;
            check.Length(rb, "bottom", bottomWorld, p.Bottom, "crop bottom elevation");
            var far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
            if (far != null && far.StorageType == StorageType.Double) check.Length(rb, "depth", far.AsDouble(), p.Depth, "far clip offset");
            else check.Length(rb, "depth", cb.Max.Z - cb.Min.Z, p.Depth, "far clip depth");
            check.Flag(rb, "cropActive", view.CropBoxActive, true);
            rb.Set("state", "created");
            check.Apply(result);
        }
    }
}
