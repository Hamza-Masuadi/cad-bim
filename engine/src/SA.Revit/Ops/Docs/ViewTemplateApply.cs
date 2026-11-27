using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// viewtemplate.apply: assigns a view template (View.ViewTemplateId), so the view stays linked to it. This is the
    /// professional practice for documentation views (views-templates-graphics.md section 2): assignment, not a
    /// one-off "apply template properties".
    ///
    /// Validate checks that the template exists and that each view accepts it (View.IsValidViewTemplate, which is
    /// documented to check compatibility with the view). Templates, sheets and schedules given as views are refused
    /// unless the template is a schedule template. Views already using the template are reported unchanged; the rest
    /// are declared as modified.
    /// </summary>
    public sealed class ViewTemplateApply : OpHandler
    {
        public override string Name => "viewtemplate.apply";

        private sealed class Plan
        {
            public View Template = null!;
            public List<View> Views = new List<View>();
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var t = DocsUtil.Template(doc, args.GetString("template").Trim(), null, ctx.FieldPath(args, "template"), issues);
            var views = DocsUtil.Views(ctx, args, "views", issues, out _);
            if (t is null) return null;
            p.Template = t;
            foreach (var kv in views)
            {
                var v = kv.Key;
                if (v.IsTemplate) { OpContext.Error(issues, kv.Value, RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " is itself a template"); continue; }
                if (v is ViewSheet) { OpContext.Error(issues, kv.Value, RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " is a sheet; sheets take no view template"); continue; }
                if (!v.IsValidViewTemplate(t.Id))
                {
                    OpContext.Error(issues, kv.Value, RevitIssueCodes.Invalid, "template '" + t.Name + "' (" + t.ViewType + ") cannot be assigned to view " + DocsUtil.Describe(v));
                    continue;
                }
                p.Views.Add(v);
                if (declare && v.ViewTemplateId != t.Id) ctx.DeclareModifies(v.Id);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            foreach (var v in p.Views)
            {
                if (v.ViewTemplateId == p.Template.Id) { result.Unchanged.Add(ElementIds.ToWire(v.Id)); continue; }
                v.ViewTemplateId = p.Template.Id;
                result.Modified.Add(ElementIds.ToWire(v.Id));
            }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var arr = new JsonArray();
            foreach (var v in p.Views)
            {
                var rec = new JsonObject().Add("id", ElementIds.ToWire(v.Id)).Add("name", v.Name);
                var tpl = ElementIds.IsValid(v.ViewTemplateId) ? ctx.Doc.GetElement(v.ViewTemplateId) as View : null;
                check.Text(rec, "template", tpl?.Name, p.Template.Name, "view " + v.Name + " template");
                if (v.ViewTemplateId != p.Template.Id) check.Fail("view " + v.Name + " does not use template id " + ElementIds.ToWire(p.Template.Id));
                rec.Add("state", result.Modified.Contains(ElementIds.ToWire(v.Id)) ? "updated" : "unchanged");
                arr.Add(rec);
            }
            result.Readback.Set("template", ElementIds.ToWire(p.Template.Id));
            result.Readback.Set("views", arr);
            check.Apply(result);
        }
    }
}
