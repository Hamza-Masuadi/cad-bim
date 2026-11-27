using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// view.drafting.create: ViewDrafting.Create(doc, viewFamilyTypeId), then scale, template and name (ViewSetup).
    ///
    /// <para><b>Legends.</b> The API has no legend constructor, so <c>legendFrom</c> duplicates an existing legend
    /// with View.Duplicate(ViewDuplicateOption.Duplicate), or WithDetailing when copyContent is true, after
    /// View.CanViewBeDuplicated. The source must be a legend (ViewType.Legend). A name already used by a legend is
    /// refused (view names are unique per view type).</para>
    /// </summary>
    public sealed class ViewDraftingCreate : OpHandler
    {
        public override string Name => "view.drafting.create";

        private sealed class Plan
        {
            public ViewSetup Setup = null!;
            public View? Legend;
            public ViewDuplicateOption Option = ViewDuplicateOption.Duplicate;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var p = new Plan();
            bool pending = false;
            if (args.Has("legendFrom"))
            {
                var src = DocsUtil.View(ctx, args.Get("legendFrom"), ctx.FieldPath(args, "legendFrom"), issues, out pending);
                if (src != null && (src.ViewType != ViewType.Legend || src.IsTemplate))
                    OpContext.Error(issues, ctx.FieldPath(args, "legendFrom"), RevitIssueCodes.Invalid, DocsUtil.Describe(src) + " is not a legend");
                else p.Legend = src;
                p.Option = args.GetBool("copyContent") ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate;
                if (p.Legend != null && !forReadback && !p.Legend.CanViewBeDuplicated(p.Option))
                    OpContext.Error(issues, ctx.FieldPath(args, "legendFrom"), RevitIssueCodes.NotAllowed, "Revit says " + DocsUtil.Describe(p.Legend) + " cannot be duplicated " + (args.GetBool("copyContent") ? "with its content" : ""));
                p.Setup = W2.Setup(ctx, args, ViewType.Legend, null, issues, forReadback);
            }
            else
            {
                if (args.GetBool("copyContent"))
                    OpContext.Warning(issues, ctx.FieldPath(args, "copyContent"), RevitIssueCodes.Invalid, "copyContent only applies with legendFrom; ignored");
                p.Setup = ViewSetup.Build(ctx, args, ViewFamily.Drafting, ViewType.DraftingView, null, issues, forReadback: forReadback);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            View view;
            if (p.Legend != null)
            {
                var id = p.Legend.Duplicate(p.Option);
                view = ctx.Doc.GetElement(id) as View ?? throw new OpFailedException("create_failed", "Revit did not duplicate the legend");
            }
            else view = ViewDrafting.Create(ctx.Doc, p.Setup.Type!.Id);
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
                check.Fail(view is null ? "the new view was not found after the op" : "could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message)));
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            p.Setup.Readback(ctx, view, rb, check);
            var want = p.Legend != null ? ViewType.Legend : ViewType.DraftingView;
            rb.Set("viewType", view.ViewType.ToString());
            if (view.ViewType != want) check.Fail("viewType: read back " + view.ViewType + ", expected " + want);
            if (p.Legend != null) rb.Set("duplicatedFrom", ElementIds.ToWire(p.Legend.Id));
            check.Apply(result);
        }
    }
}
