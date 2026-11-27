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
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// view.callout.create: ViewSection.CreateCallout(doc, parentViewId, viewFamilyTypeId, corner1, corner2).
    ///
    /// <para>The API documents the two points as opposite corners of a rectangle aligned to the parent view, valid
    /// parents as floor, ceiling and structural plans, sections, elevations and detail views, and Detail types as
    /// allowed in every parent except ceiling plans; other types must share the parent's view family. Both rules are
    /// checked up front with ViewSection.IsParentViewValidForCallout and IsViewFamilyTypeValidForCallout.</para>
    ///
    /// <para>The type defaults to the first Detail view type, else (ceiling plans) the parent's own family. The rect is
    /// in the parent's frame (plans: project XY; vertical views: along the view's right, elevation above the base
    /// point) and becomes model points on the parent's plane. The parent view is not changed as an element (the
    /// callout symbol belongs to the new view), so nothing is declared.</para>
    ///
    /// <para>Readback: name, type, template and scale (ViewSetup), View.IsCallout and View.GetCalloutParentId, and the
    /// crop box extent against the requested rect in the parent's frame.</para>
    /// </summary>
    public sealed class ViewCalloutCreate : OpHandler
    {
        public override string Name => "view.callout.create";

        private sealed class Plan
        {
            public View Parent = null!;
            public ViewSetup Setup = null!;
            public Point2 Min, Max;
        }

        private static ViewFamily? FamilyOf(View v)
        {
            switch (v.ViewType)
            {
                case ViewType.FloorPlan: return ViewFamily.FloorPlan;
                case ViewType.CeilingPlan: return ViewFamily.CeilingPlan;
                case ViewType.EngineeringPlan: return ViewFamily.StructuralPlan;
                case ViewType.Section: return ViewFamily.Section;
                case ViewType.Elevation: return ViewFamily.Elevation;
                case ViewType.Detail: return ViewFamily.Detail;
                default: return null;
            }
        }

        private static ViewType CalloutViewType(ViewFamily f)
        {
            switch (f)
            {
                case ViewFamily.FloorPlan: return ViewType.FloorPlan;
                case ViewFamily.CeilingPlan: return ViewType.CeilingPlan;
                case ViewFamily.StructuralPlan: return ViewType.EngineeringPlan;
                case ViewFamily.Section: return ViewType.Section;
                case ViewFamily.Elevation: return ViewType.Elevation;
                default: return ViewType.Detail;
            }
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var rect = args.GetObject("rect")!;
            p.Min = rect.GetPoint2("min");
            p.Max = rect.GetPoint2("max");
            if (p.Max.X <= p.Min.X || p.Max.Y <= p.Min.Y)
                OpContext.Error(issues, ctx.FieldPath(args, "rect"), RevitIssueCodes.Invalid, "rect max must be above and to the right of min");
            var parent = DocsUtil.View(ctx, args.Get("parentView"), ctx.FieldPath(args, "parentView"), issues, out bool pending);
            if (pending || parent is null)
            {
                p.Setup = ViewSetup.Build(ctx, args, ViewFamily.Detail, ViewType.Detail, null, issues, forReadback: forReadback);
                return DocsUtil.HasErrors(issues) ? null : p;
            }
            var family = FamilyOf(parent);
            if (parent.IsTemplate || family is null || !ViewSection.IsParentViewValidForCallout(doc, parent.Id))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "parentView"), RevitIssueCodes.NotAllowed,
                    "callouts can be drawn in floor, ceiling and structural plans, sections, elevations and detail views; " + DocsUtil.Describe(parent) + " is not one Revit accepts");
                return null;
            }
            p.Parent = parent;
            ViewFamily typeFamily = ViewFamily.Detail;
            if (args.GetStringOrNull("viewFamilyType") is string tn)
            {
                var all = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .Where(t => t.ViewFamily == ViewFamily.Detail || t.ViewFamily == family.Value).ToList();
                var hit = tn.Contains(" : ") ? Lookup.ByName(all, tn, "callout view type", Lookup.FamilyTypeName) : Lookup.ByName(all, tn, "callout view type");
                if (!hit.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "viewFamilyType"), hit.Code, hit.Error!); return null; }
                typeFamily = hit.Value!.ViewFamily;
            }
            else if (parent.ViewType == ViewType.CeilingPlan) typeFamily = ViewFamily.CeilingPlan;
            else if (Lookup.FirstViewFamilyType(doc, ViewFamily.Detail) is null) typeFamily = family.Value;
            p.Setup = ViewSetup.Build(ctx, args, typeFamily, CalloutViewType(typeFamily), null, issues, forReadback: forReadback);
            if (p.Setup.Type != null && !ViewSection.IsViewFamilyTypeValidForCallout(doc, p.Setup.Type.Id, parent.Id))
                OpContext.Error(issues, ctx.FieldPath(args, "viewFamilyType"), RevitIssueCodes.NotAllowed, "Revit says view type '" + p.Setup.Type.Name + "' cannot be used for a callout in " + DocsUtil.Describe(parent));
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var a = W2.ToModel(ctx, p.Parent, p.Min);
            var b = W2.ToModel(ctx, p.Parent, p.Max);
            var view = ViewSection.CreateCallout(ctx.Doc, p.Parent.Id, p.Setup.Type!.Id, a, b) ?? throw new OpFailedException("create_failed", "Revit did not create the callout");
            result.Created.Add(ElementIds.ToWire(view.Id));
            p.Setup.Apply(view);
            ViewSetup.Outputs(result, view);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var view = ViewSetup.Created(ctx, result);
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (view is null || p is null)
            {
                check.Fail(view is null ? "the callout view was not found after the op" : "could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message)));
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            p.Setup.Readback(ctx, view, rb, check);
            check.Flag(rb, "callout", view.IsCallout, true);
            check.Text(rb, "parentView", ElementIds.ToWire(view.GetCalloutParentId()), ElementIds.ToWire(p.Parent.Id));
            var ext = W2.CropExtent(ctx, view, p.Parent);
            rb.Set("extent", W2.RectJson(ext));
            W2.CompareRect(check, "extent", ext, Rect2.FromCorners(p.Min, p.Max), W2.FrameUnits(ctx, p.Parent));
            check.Apply(result);
        }
    }
}
