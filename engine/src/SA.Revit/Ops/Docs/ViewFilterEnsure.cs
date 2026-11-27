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

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// view.filter.ensure (ensure): a rule-based view filter (ParameterFilterElement) with graphic overrides, added to
    /// views or view templates.
    ///
    /// <para>Rules: each rule's parameter must be filterable for ALL the categories
    /// (ParameterFilterUtilities.GetFilterableParametersInCommon). Values are converted by the parameter's storage and
    /// spec (<see cref="FilterValues"/>). Rules are built with ParameterFilterRuleFactory, using only the overloads
    /// present in 2024-2027 (the string overloads without the case-sensitivity flag, which were removed in 2026).
    /// Each rule becomes an ElementParameterFilter; several are combined with LogicalAndFilter or LogicalOrFilter.
    /// The result is checked with ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement and
    /// AllRuleParametersApplicable before anything changes.</para>
    ///
    /// <para>Ensure: an existing filter with the name is compared by category set and by a canonical text of its rule
    /// tree (operators, parameter ids and values, AND/OR flattened and sorted); a difference is refused unless
    /// update is true. A selection filter with the same name is always refused. Adding the filter to a view or
    /// template where it is not applied is the op's purpose and is done; changing the visibility or overrides of a
    /// filter already applied there needs update. Names describe what the filter does
    /// (views-templates-graphics.md section 3): a name like "Filter 1" gets a warning.</para>
    ///
    /// <para>Targets: views must allow graphic overrides (View.AreGraphicsOverridesAllowed). A view whose template
    /// controls filters (VIS_GRAPHICS_FILTERS) is refused with a message to target the template instead, because Revit
    /// would ignore the view's own filter settings. Surface colour uses the first solid drafting fill pattern.</para>
    /// </summary>
    public sealed class ViewFilterEnsure : OpHandler
    {
        public override string Name => "view.filter.ensure";

        private sealed class Overrides
        {
            public bool Given;
            public bool Visible = true;
            public bool Halftone;
            public Color? LineColor;
            public int? LineWeight;
            public Color? SurfaceColor;
            public int? Transparency;
        }

        private sealed class Target
        {
            public View View = null!;
            public bool Add;
            public bool Change;
        }

        private sealed class Plan
        {
            public string FilterName = "";
            public List<ElementId> Categories = new List<ElementId>();
            public ElementFilter Filter = null!;
            public string Canonical = "";
            public ParameterFilterElement? Existing;
            public bool UpdateFilter;
            public Overrides Over = new Overrides();
            public List<Target> Targets = new List<Target>();
            public ElementId SolidFill = ElementId.InvalidElementId;
        }

        private static Color? ParseColor(string? text, string path, List<Issue> issues)
        {
            if (text is null) return null;
            var parts = text.Split(',');
            if (parts.Length == 3 && parts.All(x => byte.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                return new Color(byte.Parse(parts[0].Trim(), CultureInfo.InvariantCulture), byte.Parse(parts[1].Trim(), CultureInfo.InvariantCulture), byte.Parse(parts[2].Trim(), CultureInfo.InvariantCulture));
            OpContext.Error(issues, path, RevitIssueCodes.Invalid, "colour '" + text + "' must be \"r,g,b\" with values 0-255");
            return null;
        }

        private static FilterRule? Rule(Document doc, ElementId pid, string op, TypedValue? v, out string error)
        {
            error = "";
            if (op == "has_value") return ParameterFilterRuleFactory.CreateHasValueParameterRule(pid);
            if (op == "has_no_value") return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(pid);
            if (v is null) { error = "a value is required for " + op; return null; }
            bool text = v.Storage == StorageType.String;
            switch (op)
            {
                case "contains": case "not_contains": case "begins_with": case "ends_with":
                    if (!text) { error = op + " works on text parameters only"; return null; }
                    return op == "contains" ? ParameterFilterRuleFactory.CreateContainsRule(pid, v.Text!)
                         : op == "not_contains" ? ParameterFilterRuleFactory.CreateNotContainsRule(pid, v.Text!)
                         : op == "begins_with" ? ParameterFilterRuleFactory.CreateBeginsWithRule(pid, v.Text!)
                         : ParameterFilterRuleFactory.CreateEndsWithRule(pid, v.Text!);
            }
            switch (v.Storage)
            {
                case StorageType.String:
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, v.Text!);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, v.Text!);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, v.Text!);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, v.Text!);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, v.Text!);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, v.Text!);
                    }
                    break;
                case StorageType.Integer:
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, v.Int);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, v.Int);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, v.Int);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, v.Int);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, v.Int);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, v.Int);
                    }
                    break;
                case StorageType.Double:
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, v.Double, v.Epsilon);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, v.Double, v.Epsilon);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, v.Double, v.Epsilon);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, v.Double, v.Epsilon);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, v.Double, v.Epsilon);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, v.Double, v.Epsilon);
                    }
                    break;
                case StorageType.ElementId:
                    switch (op)
                    {
                        case "equals": return ParameterFilterRuleFactory.CreateEqualsRule(pid, v.Id!);
                        case "not_equals": return ParameterFilterRuleFactory.CreateNotEqualsRule(pid, v.Id!);
                        case "greater": return ParameterFilterRuleFactory.CreateGreaterRule(pid, v.Id!);
                        case "greater_or_equal": return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, v.Id!);
                        case "less": return ParameterFilterRuleFactory.CreateLessRule(pid, v.Id!);
                        case "less_or_equal": return ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, v.Id!);
                    }
                    break;
            }
            error = "operator " + op + " is not supported for this parameter";
            return null;
        }

        // ----- Canonical text of a filter tree, for comparing an existing filter with the request -----

        internal static string Canonical(ElementFilter? f)
        {
            if (f is null) return "none";
            return string.Join(" | ", Terms(f, "and"));
        }

        private static List<string> Terms(ElementFilter f, string within)
        {
            string inv = f.Inverted ? "not " : "";
            if (f is ElementLogicalFilter lf)
            {
                string op = f is LogicalOrFilter ? "or" : "and";
                var kids = new List<string>();
                foreach (var c in lf.GetFilters()) kids.AddRange(Terms(c, op));
                kids.Sort(StringComparer.Ordinal);
                if (op == within && inv.Length == 0) return kids;
                return new List<string> { inv + op + "(" + string.Join(", ", kids) + ")" };
            }
            if (f is ElementParameterFilter epf)
            {
                var rules = epf.GetRules().Select(RuleText).OrderBy(s => s, StringComparer.Ordinal).ToList();
                if (rules.Count == 1 && inv.Length == 0) return rules;
                if (within == "and" && inv.Length == 0) return rules;
                return new List<string> { inv + "and(" + string.Join(", ", rules) + ")" };
            }
            return new List<string> { inv + f.GetType().Name };
        }

        private static string RuleText(FilterRule r)
        {
            string pid = ElementIds.ToWire(r.GetRuleParameter());
            switch (r)
            {
                case FilterInverseRule inv: return "not(" + RuleText(inv.GetInnerRule()) + ")";
                case HasValueFilterRule _: return "hasValue(" + pid + ")";
                case HasNoValueFilterRule _: return "hasNoValue(" + pid + ")";
                case FilterStringRule s: return "str(" + pid + " " + s.GetEvaluator().GetType().Name + " '" + s.RuleString + "')";
                case FilterDoubleRule d: return "num(" + pid + " " + d.GetEvaluator().GetType().Name + " " + Math.Round(d.RuleValue, 6).ToString("R", CultureInfo.InvariantCulture) + ")";
                case FilterIntegerRule i: return "int(" + pid + " " + i.GetEvaluator().GetType().Name + " " + i.RuleValue.ToString(CultureInfo.InvariantCulture) + ")";
                case FilterElementIdRule e: return "id(" + pid + " " + e.GetEvaluator().GetType().Name + " " + ElementIds.ToWire(e.RuleValue) + ")";
                default: return r.GetType().Name + "(" + pid + ")";
            }
        }

        // ----- Plan -----

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            string? name = DocsUtil.CheckName(args.GetString("name"), "filter", ctx.FieldPath(args, "name"), issues);
            if (name is null) return null;
            p.FilterName = name;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^\s*filter\s*\d*\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                OpContext.Warning(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid, "name filters for what they select, e.g. \"WALL - Fire Rating 1HR\"");

            var filterable = new HashSet<long>(ParameterFilterUtilities.GetAllFilterableCategories().Select(ElementIds.ToLong));
            var cats = args.GetStringList("categories");
            for (int i = 0; i < cats.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "categories"), i);
                var c = DocsUtil.Category(doc, cats[i], at, issues);
                if (c is null) continue;
                if (!filterable.Contains(ElementIds.ToLong(c.Id))) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "category '" + c.Name + "' cannot be used in view filters"); continue; }
                if (!p.Categories.Contains(c.Id)) p.Categories.Add(c.Id);
            }
            if (DocsUtil.HasErrors(issues)) return null;

            var common = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, p.Categories);
            var filters = new List<ElementFilter>();
            var rules = args.GetObjectList("rules");
            foreach (var r in rules)
            {
                string pname = r.GetString("parameter").Trim();
                string op = r.GetString("operator");
                var pid = FilterValues.Find(doc, common, pname, out string? perr);
                if (pid is null)
                {
                    OpContext.Error(issues, JsonPath.Key(r.Path, "parameter"), RevitIssueCodes.NotFound, (perr ?? pname) + " (it must be filterable for every listed category)");
                    continue;
                }
                TypedValue? tv = null;
                if (op != "has_value" && op != "has_no_value")
                {
                    var raw = r.Get("value");
                    if (raw is null || raw.IsNull) { OpContext.Error(issues, JsonPath.Key(r.Path, "value"), RevitIssueCodes.Invalid, "operator " + op + " needs a value"); continue; }
                    if (OpContext.IsPending(raw)) continue;
                    var sample = FilterValues.Sample(doc, p.Categories, pid);
                    FilterValues.Describe(doc, pid, sample, out var storage, out var spec);
                    if (!FilterValues.Convert(doc, ctx.Units, storage, spec, raw, out var conv, out string verr))
                    {
                        OpContext.Error(issues, JsonPath.Key(r.Path, "value"), RevitIssueCodes.Invalid, "parameter '" + FilterValues.Label(doc, pid) + "': " + verr);
                        continue;
                    }
                    tv = conv;
                }
                var rule = Rule(doc, pid, op, tv, out string rerr);
                if (rule is null) { OpContext.Error(issues, JsonPath.Key(r.Path, "operator"), RevitIssueCodes.Invalid, "parameter '" + FilterValues.Label(doc, pid) + "': " + rerr); continue; }
                filters.Add(new ElementParameterFilter(rule));
            }
            if (DocsUtil.HasErrors(issues) || filters.Count == 0) return null;
            p.Filter = filters.Count == 1 ? filters[0]
                : args.GetString("logic") == "or" ? new LogicalOrFilter(filters) : (ElementFilter)new LogicalAndFilter(filters);
            if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(doc, new HashSet<ElementId>(p.Categories), p.Filter))
                OpContext.Error(issues, ctx.FieldPath(args, "rules"), RevitIssueCodes.Invalid, "Revit does not accept these rules for a view filter on these categories");
            else if (!ParameterFilterElement.AllRuleParametersApplicable(doc, p.Categories, p.Filter))
                OpContext.Error(issues, ctx.FieldPath(args, "rules"), RevitIssueCodes.Invalid, "a rule parameter does not apply to every listed category");
            p.Canonical = Canonical(p.Filter);

            // Overrides.
            var o = args.GetObject("overrides");
            if (o != null)
            {
                p.Over.Given = true;
                p.Over.Visible = o.GetBool("visible");
                p.Over.Halftone = o.GetBool("halftone");
                p.Over.LineColor = ParseColor(o.GetStringOrNull("lineColor"), JsonPath.Key(o.Path, "lineColor"), issues);
                p.Over.LineWeight = o.Has("lineWeight") ? o.GetInt("lineWeight") : (int?)null;
                p.Over.SurfaceColor = ParseColor(o.GetStringOrNull("surfaceColor"), JsonPath.Key(o.Path, "surfaceColor"), issues);
                p.Over.Transparency = o.Has("transparency") ? o.GetInt("transparency") : (int?)null;
                if (p.Over.SurfaceColor != null)
                {
                    var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .FirstOrDefault(fp => { var f = fp.GetFillPattern(); return f.IsSolidFill && f.Target == FillPatternTarget.Drafting; });
                    if (solid is null) OpContext.Error(issues, JsonPath.Key(o.Path, "surfaceColor"), RevitIssueCodes.NotFound, "the model has no solid drafting fill pattern to colour surfaces with");
                    else p.SolidFill = solid.Id;
                }
            }

            // Existing filter of that name.
            bool update = args.GetBool("update");
            var sameName = new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sameName != null && !(sameName is ParameterFilterElement))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, "'" + sameName.Name + "' is a selection filter; choose another name");
                return null;
            }
            p.Existing = sameName as ParameterFilterElement;
            if (p.Existing != null)
            {
                var diffs = new List<string>();
                var have = new HashSet<long>(p.Existing.GetCategories().Select(ElementIds.ToLong));
                if (!have.SetEquals(p.Categories.Select(ElementIds.ToLong)))
                    diffs.Add("categories are " + string.Join(", ", p.Existing.GetCategories().Select(id => Category.GetCategory(doc, id)?.Name ?? ElementIds.ToWire(id))));
                string existing = Canonical(p.Existing.GetElementFilter());
                if (!string.Equals(existing, p.Canonical, StringComparison.Ordinal)) diffs.Add("rules are " + existing);
                if (!string.Equals(p.Existing.Name, name, StringComparison.Ordinal)) diffs.Add("name is '" + p.Existing.Name + "'");
                if (diffs.Count > 0)
                {
                    if (!update)
                        OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Mismatch,
                            "filter '" + p.Existing.Name + "' exists and differs (" + string.Join("; ", diffs) + "); set update:true to change it");
                    else { p.UpdateFilter = true; if (declare) ctx.DeclareModifies(p.Existing.Id); }
                }
            }

            // Targets.
            var targets = args.Get("applyTo") as JsonArray ?? new JsonArray();
            for (int i = 0; i < targets.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "applyTo"), i);
                var v = DocsUtil.ViewOrTemplate(ctx, targets[i], at, issues, out bool pending);
                if (pending || v is null) continue;
                if (v is ViewSheet || v is ViewSchedule || !v.AreGraphicsOverridesAllowed())
                {
                    OpContext.Error(issues, at, RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " does not take graphic overrides or filters");
                    continue;
                }
                var tpl = v.IsTemplate ? null : DocsUtil.TemplateOf(v);
                if (tpl != null && DocsUtil.TemplateControls(tpl, BuiltInParameter.VIS_GRAPHICS_FILTERS))
                {
                    OpContext.Error(issues, at, RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " gets its filters from template '" + tpl.Name + "'; apply the filter to the template instead");
                    continue;
                }
                if (p.Targets.Any(t => t.View.Id == v.Id)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " is listed twice"); continue; }
                var target = new Target { View = v };
                if (p.Existing != null && v.IsFilterApplied(p.Existing.Id))
                {
                    var diff = OverrideDifferences(v, p.Existing.Id, p.Over);
                    if (diff.Count > 0)
                    {
                        if (!update) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "filter '" + name + "' is applied to " + DocsUtil.Describe(v) + " with different graphics (" + string.Join("; ", diff) + "); set update:true to change them");
                        else target.Change = true;
                    }
                }
                else target.Add = true;
                if (declare && (target.Add || target.Change)) ctx.DeclareModifies(v.Id);
                p.Targets.Add(target);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static List<string> OverrideDifferences(View v, ElementId filterId, Overrides want)
        {
            var d = new List<string>();
            if (!want.Given) return d;
            bool vis = v.GetFilterVisibility(filterId);
            if (vis != want.Visible) d.Add("visible is " + (vis ? "true" : "false"));
            var o = v.GetFilterOverrides(filterId);
            if (o.Halftone != want.Halftone) d.Add("halftone is " + (o.Halftone ? "true" : "false"));
            if (want.LineColor != null && !SameColor(o.ProjectionLineColor, want.LineColor)) d.Add("line colour is " + ColorText(o.ProjectionLineColor));
            if (want.LineWeight.HasValue && o.ProjectionLineWeight != want.LineWeight.Value) d.Add("line weight is " + o.ProjectionLineWeight);
            if (want.SurfaceColor != null && !SameColor(o.SurfaceForegroundPatternColor, want.SurfaceColor)) d.Add("surface colour is " + ColorText(o.SurfaceForegroundPatternColor));
            if (want.Transparency.HasValue && o.Transparency != want.Transparency.Value) d.Add("transparency is " + o.Transparency);
            return d;
        }

        private static bool SameColor(Color? a, Color? b) =>
            a != null && b != null && a.IsValid && b.IsValid && a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

        private static string ColorText(Color? c) => c is null || !c.IsValid ? "none" : c.Red + "," + c.Green + "," + c.Blue;

        private static OverrideGraphicSettings Settings(Plan p)
        {
            var o = new OverrideGraphicSettings();
            o.SetHalftone(p.Over.Halftone);
            if (p.Over.LineColor != null) o.SetProjectionLineColor(p.Over.LineColor);
            if (p.Over.LineWeight.HasValue) o.SetProjectionLineWeight(p.Over.LineWeight.Value);
            if (p.Over.SurfaceColor != null)
            {
                o.SetSurfaceForegroundPatternId(p.SolidFill);
                o.SetSurfaceForegroundPatternColor(p.Over.SurfaceColor);
                o.SetSurfaceForegroundPatternVisible(true);
            }
            if (p.Over.Transparency.HasValue) o.SetSurfaceTransparency(p.Over.Transparency.Value);
            return o;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            ParameterFilterElement filter;
            if (p.Existing is null)
            {
                filter = ParameterFilterElement.Create(doc, p.FilterName, p.Categories, p.Filter);
                result.Created.Add(ElementIds.ToWire(filter.Id));
            }
            else
            {
                filter = p.Existing;
                if (p.UpdateFilter)
                {
                    if (!string.Equals(filter.Name, p.FilterName, StringComparison.Ordinal)) filter.Name = p.FilterName;
                    filter.SetCategories(p.Categories);
                    filter.SetElementFilter(p.Filter);
                    result.Modified.Add(ElementIds.ToWire(filter.Id));
                }
                else result.Unchanged.Add(ElementIds.ToWire(filter.Id));
            }
            var settings = Settings(p);
            foreach (var t in p.Targets)
            {
                if (t.Add)
                {
                    t.View.AddFilter(filter.Id);
                    t.View.SetFilterVisibility(filter.Id, p.Over.Visible);
                    if (p.Over.Given) t.View.SetFilterOverrides(filter.Id, settings);
                    result.Modified.Add(ElementIds.ToWire(t.View.Id));
                }
                else if (t.Change)
                {
                    t.View.SetFilterVisibility(filter.Id, p.Over.Visible);
                    t.View.SetFilterOverrides(filter.Id, settings);
                    result.Modified.Add(ElementIds.ToWire(t.View.Id));
                }
                else result.Unchanged.Add(ElementIds.ToWire(t.View.Id));
            }
            result.Outputs.Set("filter", ElementIds.ToWire(filter.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            string? id = result.Outputs.GetStringOrNull("filter");
            var filter = id != null && ElementIds.TryParse(id, out var eid) ? doc.GetElement(eid) as ParameterFilterElement : null;
            if (filter is null) { check.Fail("the filter was not found after the op"); check.Apply(result); return; }
            var rb = result.Readback;
            rb.Set("id", id!);
            check.Text(rb, "name", filter.Name, args.GetString("name").Trim());
            var cats = args.GetStringList("categories").Select(c => Lookup.Category(doc, c, out _)).Where(c => c != null).Select(c => ElementIds.ToLong(c!.Id)).ToList();
            var have = filter.GetCategories().Select(ElementIds.ToLong).ToList();
            rb.Set("categories", JsonArray.FromStrings(filter.GetCategories().Select(c => Category.GetCategory(doc, c)?.Name ?? ElementIds.ToWire(c))));
            if (!new HashSet<long>(have).SetEquals(cats)) check.Fail("categories differ from the request");
            // Rebuild the requested rule tree (no changes) and compare canonical text.
            var issues = new List<Issue>();
            var want = BuildRulesOnly(ctx, args, issues);
            string got = Canonical(filter.GetElementFilter());
            rb.Set("rules", got);
            if (want is null) check.Fail("could not rebuild the requested rules: " + string.Join("; ", issues.Select(i => i.Message)));
            else if (!string.Equals(got, want, StringComparison.Ordinal)) check.Fail("rules read back as " + got + ", requested " + want);
            var views = new JsonArray();
            var over = new Overrides();
            var o = args.GetObject("overrides");
            if (o != null)
            {
                over.Given = true; over.Visible = o.GetBool("visible"); over.Halftone = o.GetBool("halftone");
                over.LineColor = ParseColor(o.GetStringOrNull("lineColor"), "", new List<Issue>());
                over.LineWeight = o.Has("lineWeight") ? o.GetInt("lineWeight") : (int?)null;
                over.SurfaceColor = ParseColor(o.GetStringOrNull("surfaceColor"), "", new List<Issue>());
                over.Transparency = o.Has("transparency") ? o.GetInt("transparency") : (int?)null;
            }
            foreach (var t in (args.Get("applyTo") as JsonArray ?? new JsonArray()))
            {
                var v = DocsUtil.ViewOrTemplate(ctx, t, "", new List<Issue>(), out _);
                if (v is null) { check.Fail("an applyTo view could not be found after the op"); continue; }
                var rec = new JsonObject().Add("id", ElementIds.ToWire(v.Id)).Add("name", v.Name);
                bool applied = v.IsFilterApplied(filter.Id);
                check.Flag(rec, "applied", applied, true, "filter on " + v.Name);
                if (applied)
                {
                    rec.Add("visible", v.GetFilterVisibility(filter.Id));
                    if (!over.Given && !v.GetFilterVisibility(filter.Id) && result.Modified.Contains(ElementIds.ToWire(v.Id))) check.Fail("filter on " + v.Name + " is hidden");
                    foreach (var d in OverrideDifferences(v, filter.Id, over)) check.Fail("filter on " + v.Name + ": " + d);
                }
                rec.Add("state", result.Modified.Contains(ElementIds.ToWire(v.Id)) ? "updated" : "unchanged");
                views.Add(rec);
            }
            rb.Set("applyTo", views);
            rb.Set("state", result.Created.Contains(id!) ? "created" : result.Modified.Contains(id!) ? "updated" : "unchanged");
            check.Apply(result);
        }

        /// <summary>The canonical text of the requested rules, built without touching the document.</summary>
        private static string? BuildRulesOnly(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var cats = new List<ElementId>();
            foreach (var c in args.GetStringList("categories"))
            {
                var cat = Lookup.Category(doc, c, out _);
                if (cat != null && !cats.Contains(cat.Id)) cats.Add(cat.Id);
            }
            var common = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, cats);
            var filters = new List<ElementFilter>();
            foreach (var r in args.GetObjectList("rules"))
            {
                var pid = FilterValues.Find(doc, common, r.GetString("parameter").Trim(), out string? perr);
                if (pid is null) { issues.Add(new Issue(r.Path, RevitIssueCodes.NotFound, perr ?? "")); return null; }
                string op = r.GetString("operator");
                TypedValue? tv = null;
                if (op != "has_value" && op != "has_no_value")
                {
                    FilterValues.Describe(doc, pid, FilterValues.Sample(doc, cats, pid), out var storage, out var spec);
                    if (!FilterValues.Convert(doc, ctx.Units, storage, spec, r.Get("value") ?? JsonValue.Null, out var conv, out string verr)) { issues.Add(new Issue(r.Path, RevitIssueCodes.Invalid, verr)); return null; }
                    tv = conv;
                }
                var rule = Rule(doc, pid, op, tv, out string rerr);
                if (rule is null) { issues.Add(new Issue(r.Path, RevitIssueCodes.Invalid, rerr)); return null; }
                filters.Add(new ElementParameterFilter(rule));
            }
            if (filters.Count == 0) return null;
            ElementFilter f = filters.Count == 1 ? filters[0] : args.GetString("logic") == "or" ? new LogicalOrFilter(filters) : (ElementFilter)new LogicalAndFilter(filters);
            return Canonical(f);
        }
    }
}
