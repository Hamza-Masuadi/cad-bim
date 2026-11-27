using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
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
    /// colorfill.apply: ensure a colour fill scheme and apply it to a view.
    ///
    /// <para><b>API.</b> Schemes are ColorFillScheme elements of the category (CategoryId; for areas also AreaSchemeId of
    /// the view's area scheme). A missing scheme is made with ColorFillScheme.Duplicate(name) of basedOn (checked with
    /// IsValidSchemeName). ParameterDefinition takes an id from GetSupportedParameterIds, matched by its label; the API
    /// documents that setting it removes every entry. Colours: entries from GetEntries are matched by value (text,
    /// integer, element name) or caption; a match gets Color set through UpdateEntry (after CanUpdateEntry); a text
    /// value with no entry gets AddEntry of a new ColorFillSchemeEntry(StorageType.String) with the fill pattern of an
    /// existing entry, else the solid fill. View: View.SetColorFillSchemeId(categoryId, schemeId) after
    /// CanApplyColorFillScheme and SupportedColorFillCategoryIds. Legend: ColorFillLegend.Create(doc, viewId,
    /// categoryId, origin), the origin on the view plane; an existing legend of the category in the view is kept.</para>
    ///
    /// <para><b>Ensure.</b> An existing scheme whose parameter, title or listed colours differ is an existing_differs
    /// error unless update is true; with update it is declared and changed. Range schemes (IsByRange) take no
    /// colours here. The view is declared when its scheme for the category changes; a template that controls the
    /// colour scheme (VIEW_SCHEMA_SETTING_FOR_BUILDING) refuses the op.</para>
    /// </summary>
    public sealed class ColorFillApply : OpHandler
    {
        public override string Name => "colorfill.apply";

        private sealed class Plan
        {
            public View View = null!;
            public Category Category = null!;
            public ColorFillScheme? Existing;
            public ColorFillScheme Base = null!;
            public string SchemeName = "";
            public ElementId? Parameter;
            public string? ParameterLabel;
            public string? Title;
            public List<KeyValuePair<string, Color>> Colors = new List<KeyValuePair<string, Color>>();
            public List<string> Differences = new List<string>();
            public bool ViewChanges;
            public Autodesk.Revit.DB.XYZ? LegendAt;
            public ColorFillLegend? ExistingLegend;
        }

        private static readonly ViewType[] Allowed = { ViewType.FloorPlan, ViewType.AreaPlan, ViewType.Section, ViewType.CeilingPlan, ViewType.EngineeringPlan };

        internal static string EntryKey(Document doc, ColorFillSchemeEntry e)
        {
            switch (e.StorageType)
            {
                case StorageType.String: return e.GetStringValue() ?? "";
                case StorageType.Integer: return e.GetIntegerValue().ToString(CultureInfo.InvariantCulture);
                case StorageType.ElementId: return doc.GetElement(e.GetElementIdValue())?.Name ?? "";
                default: return e.Caption ?? "";
            }
        }

        private static ColorFillSchemeEntry? FindEntry(Document doc, IList<ColorFillSchemeEntry> entries, string value) =>
            entries.FirstOrDefault(e => string.Equals(EntryKey(doc, e), value, StringComparison.Ordinal))
            ?? entries.FirstOrDefault(e => string.Equals(e.Caption, value, StringComparison.Ordinal))
            ?? entries.FirstOrDefault(e => string.Equals(EntryKey(doc, e), value, StringComparison.OrdinalIgnoreCase));

        private static bool SameColor(Color a, Color b) => a.IsValid && b.IsValid && a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            var cat = DocsUtil.Category(doc, args.GetString("category"), ctx.FieldPath(args, "category"), issues);
            if (pending || v is null || cat is null) return null;
            if (v.IsTemplate || !Allowed.Contains(v.ViewType))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "colour fill schemes apply to plans and sections; not to " + DocsUtil.Describe(v));
                return null;
            }
            if (!v.SupportedColorFillCategoryIds().Contains(cat.Id))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotAllowed, DocsUtil.Describe(v) + " cannot be coloured by " + cat.Name);
                return null;
            }
            if (DocsUtil.TemplateOf(v) is View tpl && DocsUtil.TemplateControls(tpl, BuiltInParameter.VIEW_SCHEMA_SETTING_FOR_BUILDING))
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "template '" + tpl.Name + "' controls the colour scheme of " + DocsUtil.Describe(v));
            var p = new Plan { View = v, Category = cat, SchemeName = args.GetString("scheme").Trim(), Title = args.GetStringOrNull("title") };
            var areaScheme = (v as ViewPlan)?.AreaScheme?.Id;
            bool areas = ElementIds.ToBuiltInCategory(cat.Id) == BuiltInCategory.OST_Areas;
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>()
                .Where(s => s.CategoryId == cat.Id && (!areas || areaScheme is null || s.AreaSchemeId == areaScheme)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            if (schemes.Count == 0)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, "the model has no colour scheme for " + cat.Name + (areas ? " in this area scheme" : "") + " to start from");
                return null;
            }
            var hit = schemes.FirstOrDefault(s => string.Equals(s.Name, p.SchemeName, StringComparison.Ordinal));
            p.Existing = hit;
            if (hit != null) p.Base = hit;
            else
            {
                if (args.GetStringOrNull("basedOn") is string bn)
                {
                    var b = Lookup.ByName(schemes, bn, "colour scheme");
                    if (!b.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), b.Code, b.Error!); return null; }
                    p.Base = b.Value!;
                }
                else p.Base = schemes[0];
                if (!p.Base.IsValidSchemeName(p.SchemeName))
                    OpContext.Error(issues, ctx.FieldPath(args, "scheme"), RevitIssueCodes.Invalid, "Revit refuses '" + p.SchemeName + "' as a new colour scheme name");
                if (!args.Has("parameter"))
                    OpContext.Error(issues, ctx.FieldPath(args, "parameter"), RevitIssueCodes.Invalid, "scheme '" + p.SchemeName + "' does not exist; give parameter to create it");
            }
            if (args.GetStringOrNull("parameter") is string pn)
            {
                var ids = p.Base.GetSupportedParameterIds();
                var named = ids.Select(id => new { id, n = FilterValues.Label(doc, id) }).ToList();
                var m = named.Where(x => string.Equals(x.n, pn, StringComparison.Ordinal)).ToList();
                if (m.Count == 0) m = named.Where(x => string.Equals(x.n, pn, StringComparison.OrdinalIgnoreCase)).ToList();
                if (m.Count != 1)
                    OpContext.Error(issues, ctx.FieldPath(args, "parameter"), m.Count == 0 ? RevitIssueCodes.NotFound : RevitIssueCodes.Ambiguous,
                        (m.Count == 0 ? "'" + pn + "' is not a parameter colour schemes of " + cat.Name + " can use" : "'" + pn + "' matches " + m.Count + " parameters") +
                        Lookup.Suggest(pn, named.Select(x => x.n).Distinct().ToList()));
                else
                {
                    p.Parameter = m[0].id;
                    p.ParameterLabel = m[0].n;
                }
            }
            foreach (var kv in args.GetMap("colors"))
            {
                string path = JsonPath.Key(ctx.FieldPath(args, "colors"), kv.Key);
                if (!(kv.Value is JsonString cs) || !W2.TryColor(cs.Value, out byte r, out byte g, out byte b))
                {
                    OpContext.Error(issues, path, RevitIssueCodes.Invalid, "a colour is \"r,g,b\" (0-255) or \"#RRGGBB\"");
                    continue;
                }
                p.Colors.Add(new KeyValuePair<string, Color>(kv.Key, new Color(r, g, b)));
            }
            if (p.Colors.Count > 0 && p.Base.IsByRange)
                OpContext.Error(issues, ctx.FieldPath(args, "colors"), RevitIssueCodes.NotAllowed, "scheme '" + p.Base.Name + "' colours by range; set its colours in Revit");
            if (p.Existing != null)
            {
                var s = p.Existing;
                if (p.Parameter != null && s.ParameterDefinition != p.Parameter)
                    p.Differences.Add("parameter is '" + FilterValues.Label(doc, s.ParameterDefinition) + "', requested '" + p.ParameterLabel + "' (changing it clears the scheme's entries)");
                if (p.Title != null && !string.Equals(s.Title, p.Title, StringComparison.Ordinal))
                    p.Differences.Add("title is '" + s.Title + "', requested '" + p.Title + "'");
                bool clears = p.Parameter != null && s.ParameterDefinition != p.Parameter;
                var entries = s.GetEntries();
                foreach (var c in p.Colors)
                {
                    var e = clears ? null : FindEntry(doc, entries, c.Key);
                    if (e is null) p.Differences.Add("no entry for '" + c.Key + "'");
                    else if (!SameColor(e.Color, c.Value)) p.Differences.Add("'" + c.Key + "' is " + W2.ColorText(e.Color) + ", requested " + W2.ColorText(c.Value));
                }
                if (p.Differences.Count > 0 && !args.GetBool("update"))
                    OpContext.Error(issues, ctx.FieldPath(args, "scheme"), RevitIssueCodes.Mismatch, "scheme '" + s.Name + "' differs: " + string.Join("; ", p.Differences) + ". Set update:true to change it");
                if (!clears && s.StorageType != StorageType.String)
                    foreach (var c in p.Colors)
                        if (FindEntry(doc, entries, c.Key) is null)
                            OpContext.Error(issues, JsonPath.Key(ctx.FieldPath(args, "colors"), c.Key), RevitIssueCodes.NotFound, "scheme '" + s.Name + "' has no entry '" + c.Key + "' and only text entries can be added");
                if (!v.CanApplyColorFillScheme(cat.Id, s.Id))
                    OpContext.Error(issues, ctx.FieldPath(args, "scheme"), RevitIssueCodes.NotAllowed, "Revit says scheme '" + s.Name + "' cannot be applied to " + DocsUtil.Describe(v));
            }
            p.ViewChanges = p.Existing is null || v.GetColorFillSchemeId(cat.Id) != p.Existing.Id;
            if (args.Has("legendAt"))
            {
                p.ExistingLegend = new FilteredElementCollector(doc, v.Id).OfClass(typeof(ColorFillLegend)).Cast<ColorFillLegend>()
                    .FirstOrDefault(l => l.ColorFillCategoryId == cat.Id);
                if (p.ExistingLegend is null) p.LegendAt = W2.ToModel(ctx, v, args.GetPoint2("legendAt"));
            }
            if (declare && !DocsUtil.HasErrors(issues))
            {
                if (p.ViewChanges) ctx.DeclareModifies(v.Id);
                if (p.Existing != null && p.Differences.Count > 0) ctx.DeclareModifies(p.Existing.Id);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        private static ElementId SolidFill(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id ?? ElementId.InvalidElementId;

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            ColorFillScheme scheme;
            bool created = false;
            if (p.Existing is null)
            {
                var id = p.Base.Duplicate(p.SchemeName);
                scheme = doc.GetElement(id) as ColorFillScheme ?? throw new OpFailedException("create_failed", "Revit did not copy scheme '" + p.Base.Name + "'");
                created = true;
            }
            else scheme = p.Existing;
            bool changed = false;
            if (p.Parameter != null && scheme.ParameterDefinition != p.Parameter)
            {
                if (!scheme.IsValidParameterDefinitionId(p.Parameter)) throw new OpFailedException("parameter_invalid", "Revit says '" + p.ParameterLabel + "' cannot colour scheme '" + scheme.Name + "'");
                scheme.ParameterDefinition = p.Parameter;
                changed = true;
                doc.Regenerate();
            }
            if (p.Title != null && !string.Equals(scheme.Title, p.Title, StringComparison.Ordinal)) { scheme.Title = p.Title; changed = true; }
            if (p.Colors.Count > 0)
            {
                var entries = scheme.GetEntries();
                var pattern = entries.Select(e => e.FillPatternId).FirstOrDefault(ElementIds.IsValid) ?? SolidFill(doc);
                foreach (var c in p.Colors)
                {
                    var e = FindEntry(doc, entries, c.Key);
                    if (e != null)
                    {
                        if (SameColor(e.Color, c.Value)) continue;
                        e.Color = c.Value;
                        if (!scheme.CanUpdateEntry(e)) throw new OpFailedException("entry_refused", "Revit refuses to update the entry '" + c.Key + "' of scheme '" + scheme.Name + "'");
                        scheme.UpdateEntry(e);
                    }
                    else
                    {
                        if (scheme.StorageType != StorageType.String) throw new OpFailedException("entry_missing", "scheme '" + scheme.Name + "' has no entry '" + c.Key + "', and only text entries can be added");
                        var ne = new ColorFillSchemeEntry(StorageType.String) { Color = c.Value, FillPatternId = pattern };
                        ne.SetStringValue(c.Key);
                        var consistency = scheme.IsEntryConsistentWithScheme(ne);
                        if (consistency != EntryAndSchemeConsistency.Consistent)
                            throw new OpFailedException("entry_refused", "Revit refuses a new entry '" + c.Key + "' in scheme '" + scheme.Name + "': " + consistency);
                        scheme.AddEntry(ne);
                    }
                    changed = true;
                }
            }
            if (created) result.Created.Add(ElementIds.ToWire(scheme.Id));
            else if (changed) result.Modified.Add(ElementIds.ToWire(scheme.Id));
            else result.Unchanged.Add(ElementIds.ToWire(scheme.Id));
            if (p.View.GetColorFillSchemeId(p.Category.Id) != scheme.Id)
            {
                if (!p.View.CanApplyColorFillScheme(p.Category.Id, scheme.Id)) throw new OpFailedException("scheme_refused", "Revit says scheme '" + scheme.Name + "' cannot be applied to " + DocsUtil.Describe(p.View));
                p.View.SetColorFillSchemeId(p.Category.Id, scheme.Id);
                result.Modified.Add(ElementIds.ToWire(p.View.Id));
            }
            JsonValue legend = JsonValue.Null;
            if (p.ExistingLegend != null)
            {
                legend = ElementIds.ToWire(p.ExistingLegend.Id);
                result.Unchanged.Add(ElementIds.ToWire(p.ExistingLegend.Id));
            }
            else if (p.LegendAt != null)
            {
                var l = ColorFillLegend.Create(doc, p.View.Id, p.Category.Id, p.LegendAt) ?? throw new OpFailedException("create_failed", "Revit did not create the colour fill legend");
                result.Created.Add(ElementIds.ToWire(l.Id));
                legend = ElementIds.ToWire(l.Id);
            }
            result.Outputs.Set("scheme", ElementIds.ToWire(scheme.Id));
            result.Outputs.Set("legend", legend);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            string? sid = result.Outputs.GetStringOrNull("scheme");
            var scheme = sid != null && ElementIds.TryParse(sid, out var id) ? doc.GetElement(id) as ColorFillScheme : null;
            var issues = new List<Issue>();
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out _);
            var cat = DocsUtil.Category(doc, args.GetString("category"), ctx.FieldPath(args, "category"), issues);
            if (scheme is null || v is null || cat is null) { check.Fail(scheme is null ? "the colour scheme was not found after the op" : "could not resolve the request again"); check.Apply(result); return; }
            var rb = result.Readback;
            check.Text(rb, "scheme", scheme.Name, args.GetString("scheme").Trim());
            check.Text(rb, "viewScheme", ElementIds.ToWire(v.GetColorFillSchemeId(cat.Id)), ElementIds.ToWire(scheme.Id));
            string label = FilterValues.Label(doc, scheme.ParameterDefinition);
            rb.Set("parameter", label);
            if (args.GetStringOrNull("parameter") is string pn && !string.Equals(label, pn, StringComparison.OrdinalIgnoreCase))
                check.Fail("parameter: read back '" + label + "', requested '" + pn + "'");
            check.Text(rb, "title", scheme.Title, args.GetStringOrNull("title"));
            var entries = scheme.GetEntries();
            var colors = new JsonObject();
            foreach (var kv in args.GetMap("colors"))
            {
                var e = FindEntry(doc, entries, kv.Key);
                colors.Set(kv.Key, e is null ? (JsonValue)JsonValue.Null : W2.ColorText(e.Color));
                if (e is null) { check.Fail("no entry '" + kv.Key + "' in scheme '" + scheme.Name + "'"); continue; }
                if (kv.Value is JsonString cs && W2.TryColor(cs.Value, out byte r, out byte g, out byte b) && !SameColor(e.Color, new Color(r, g, b)))
                    check.Fail("'" + kv.Key + "': read back " + W2.ColorText(e.Color) + ", requested " + r + "," + g + "," + b);
            }
            rb.Set("colors", colors);
            rb.Set("entries", entries.Count);
            if (args.Has("legendAt"))
            {
                string? lid = result.Outputs.GetStringOrNull("legend");
                var l = lid != null && ElementIds.TryParse(lid, out var lId) ? doc.GetElement(lId) as ColorFillLegend : null;
                if (l is null) check.Fail("the colour fill legend was not found after the op");
                else
                {
                    rb.Set("legend", lid!);
                    if (l.OwnerViewId != v.Id) check.Fail("the legend is not in " + DocsUtil.Describe(v));
                }
            }
            check.Apply(result);
        }
    }
}
