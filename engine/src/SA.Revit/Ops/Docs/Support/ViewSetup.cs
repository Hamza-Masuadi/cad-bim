using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs.Support
{
    /// <summary>
    /// The fields every view-creating op shares (name, viewFamilyType, template, scale): resolved in Validate, applied
    /// to the new view in Apply, and read back after commit.
    ///
    /// Professional defaults (references/practice/views-templates-graphics.md, mistakes-revit.md):
    /// <list type="bullet">
    /// <item>No default names. Revit's "Section 1" / "Level 1(1)" names are RV-028. Plans default to the level's name
    /// (or "level - view type" when that is taken); sections, elevations and 3D views must be named by the job.</item>
    /// <item>A template must be of the right view type. When it controls the scale, a different requested scale is
    /// refused, because Revit would silently keep the template's.</item>
    /// <item>The scale must be a whole 1:X ratio and, when the job's standard lists scales, an approved one.</item>
    /// </list>
    /// </summary>
    internal sealed class ViewSetup
    {
        public string? Name;
        public ViewFamilyType? Type;
        public View? Template;
        public int? Scale;
        /// <summary>The scale the view will end up with when the template controls it.</summary>
        public int? TemplateScale;

        /// <param name="defaultName">Name to use when the job gives none; null means a name is required.</param>
        /// <param name="forReadback">
        /// After commit: resolve the requested values only. The name is then the job's explicit name (a default name
        /// is not compared) and uniqueness is not checked, because the new view itself now holds the name.
        /// </param>
        public static ViewSetup Build(OpContext ctx, OpArgs args, ViewFamily family, ViewType viewType, string? defaultName, List<Issue> issues,
            bool hasTypeField = true, bool hasScaleField = true, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var s = new ViewSetup();
            string? raw = args.GetStringOrNull("name");
            if (raw != null) s.Name = DocsUtil.CheckName(raw, "view", ctx.FieldPath(args, "name"), issues);
            else if (forReadback) s.Name = null;
            else if (defaultName != null) s.Name = defaultName;
            else OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid,
                "name is required: Revit's default names (\"Section 1\", \"{3D} 2\") pile up as unnamed views (RV-028)");
            if (raw != null && s.Name != null && !forReadback && ctx.Standard?.ViewNaming != null)
            {
                var vn = ctx.Standard.ParseViewName(s.Name);
                if (!vn.Valid) OpContext.Warning(issues, ctx.FieldPath(args, "name"), IssueCodes.NameInvalid, "view name does not follow the standard's view naming: " + vn.Explain());
            }
            if (s.Name != null && !forReadback)
            {
                var taken = DocsUtil.NameTaken(doc, s.Name, viewType);
                if (taken != null)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists,
                        "a " + viewType + " view named '" + taken.Name + "' already exists (id " + ElementIds.ToWire(taken.Id) + ")");
                    s.Name = null;
                }
            }
            s.Type = DocsUtil.ViewFamilyType(doc, family, hasTypeField ? args.GetStringOrNull("viewFamilyType") : null,
                hasTypeField ? ctx.FieldPath(args, "viewFamilyType") : args.Path, issues);
            if (hasScaleField) s.Scale = DocsUtil.Scale(ctx, args.GetStringOrNull("scale"), ctx.FieldPath(args, "scale"), issues);
            if (args.GetStringOrNull("template") is string tn)
            {
                s.Template = DocsUtil.Template(doc, tn, new[] { viewType }, ctx.FieldPath(args, "template"), issues);
                if (s.Template != null && DocsUtil.TemplateControlsScale(s.Template))
                {
                    s.TemplateScale = s.Template.Scale;
                    if (s.Scale.HasValue && s.Scale.Value != s.Template.Scale)
                        OpContext.Error(issues, ctx.FieldPath(args, "scale"), RevitIssueCodes.Invalid,
                            "template '" + s.Template.Name + "' controls the scale (1:" + s.Template.Scale + "); omit scale or use 1:" + s.Template.Scale);
                }
            }
            return s;
        }

        /// <summary>Applies scale, then template, then name to a view created in this op.</summary>
        public void Apply(View view)
        {
            if (Scale.HasValue && view.Scale != Scale.Value) view.Scale = Scale.Value;
            if (Template != null)
            {
                if (!view.IsValidViewTemplate(Template.Id))
                    throw new OpFailedException("template_invalid", "Revit says template '" + Template.Name + "' cannot be applied to view '" + view.Name + "'");
                view.ViewTemplateId = Template.Id;
            }
            if (Name != null && !string.Equals(view.Name, Name, StringComparison.Ordinal)) view.Name = Name;
        }

        public int? ExpectedScale => TemplateScale ?? Scale;

        /// <summary>Reads back id, name, type, template and scale, and fills the declared outputs check.</summary>
        public void Readback(OpContext ctx, View view, JsonObject rb, ReadbackChecker check)
        {
            var doc = ctx.Doc;
            rb.Set("id", ElementIds.ToWire(view.Id));
            check.Text(rb, "name", view.Name, Name);
            var vft = doc.GetElement(view.GetTypeId()) as ViewFamilyType;
            check.Text(rb, "viewFamilyType", vft?.Name, Type?.Name);
            var tpl = ElementIds.IsValid(view.ViewTemplateId) ? doc.GetElement(view.ViewTemplateId) as View : null;
            check.Text(rb, "template", tpl?.Name, Template?.Name);
            rb.Set("scale", "1:" + view.Scale);
            if (ExpectedScale.HasValue && view.Scale != ExpectedScale.Value) check.Fail("scale: read back 1:" + view.Scale + ", requested 1:" + ExpectedScale.Value);
        }

        /// <summary>Fills view and name outputs from the created view (ViewOutputs in the schema).</summary>
        public static void Outputs(OpResult result, View view)
        {
            result.Outputs.Set("view", ElementIds.ToWire(view.Id));
            result.Outputs.Set("name", view.Name);
        }

        /// <summary>The view an op created, from its outputs, or null.</summary>
        public static View? Created(OpContext ctx, OpResult result)
        {
            string? id = result.Outputs.GetStringOrNull("view");
            return id != null && ElementIds.TryParse(id, out var eid) ? ctx.Doc.GetElement(eid) as View : null;
        }
    }
}
