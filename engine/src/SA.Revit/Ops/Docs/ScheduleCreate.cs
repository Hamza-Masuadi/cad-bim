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
    /// schedule.create (ensure): a schedule (ViewSchedule.CreateSchedule) of one category, or a multi-category one
    /// (category "Multi-Category": CreateSchedule with InvalidElementId, as documented), with fields in order, filters,
    /// sorting/grouping, totals, itemize, grand totals, phase, elements of linked files (includeLinks:
    /// ScheduleDefinition.IncludeLinkedFiles, refused where CanIncludeLinkedFiles is false) and an optional template.
    ///
    /// <para><b>Fields.</b> Only schedulable fields are used: each requested name is matched against
    /// ScheduleDefinition.GetSchedulableFields() by SchedulableField.GetName(doc) (exact, then case-insensitive;
    /// when an instance and a type field share a name the instance field wins). "Count" is the count field. The list
    /// of schedulable fields needs a schedule of the category, so Validate uses an existing schedule of that
    /// category (or the existing schedule of this name) when there is one, and reports every requested field that
    /// is not schedulable, with close matches. When the model has no schedule of the category yet, the check runs in
    /// Apply on the new schedule and fails the op listing every unschedulable field.</para>
    ///
    /// <para><b>Totals</b> are set with ScheduleField.DisplayType = Totals only where ScheduleField.CanTotal() is true; a
    /// total requested on a field that cannot total (text identifiers such as Mark) fails the op, because totals on
    /// identifiers are a mistake (schedules-tags.md section 2). <b>Filters</b> need a field of the schedule (hidden is
    /// fine) and use ScheduleFilter with the value converted by the field's spec (ScheduleField.GetSpecTypeId):
    /// substring operators need CanFilterBySubstring, has_value / has_no_value need CanFilterByValuePresence, the rest
    /// CanFilterByValue. <b>Sorting</b> uses ScheduleSortGroupField with header, footer and blank line.</para>
    ///
    /// <para><b>Phase.</b> Set through the schedule's VIEW_PHASE parameter. When omitted in a model with more than
    /// one phase, a warning says the schedule takes Revit's default phase (demolished items may show).</para>
    ///
    /// <para><b>Ensure.</b> A schedule with the name is compared (category, fields in order with headings, hidden,
    /// totals and widths when given, filters, sorting, itemize, grand totals, phase, template). A different category
    /// is always refused; other differences are refused unless update is true, in which case the definition is
    /// rebuilt (ClearSortGroupFields, ClearFilters, ClearFields, then everything added again).</para>
    ///
    /// <para><b>Readback.</b> Field names, headings, hidden flags and totals in order; filter and sort counts; itemize,
    /// grand totals, phase and template; <c>rowCount</c> = rows of the body section (TableData, SectionType.Body)
    /// minus the column-heading row when headings are shown; <c>elementCount</c> = elements the schedule lists
    /// (FilteredElementCollector on the schedule view).</para>
    /// </summary>
    public sealed class ScheduleCreate : OpHandler
    {
        public override string Name => "schedule.create";

        private static readonly Dictionary<string, ScheduleFilterType> Ops = new Dictionary<string, ScheduleFilterType>
        {
            { "equals", ScheduleFilterType.Equal }, { "not_equals", ScheduleFilterType.NotEqual },
            { "greater", ScheduleFilterType.GreaterThan }, { "greater_or_equal", ScheduleFilterType.GreaterThanOrEqual },
            { "less", ScheduleFilterType.LessThan }, { "less_or_equal", ScheduleFilterType.LessThanOrEqual },
            { "contains", ScheduleFilterType.Contains }, { "not_contains", ScheduleFilterType.NotContains },
            { "begins_with", ScheduleFilterType.BeginsWith }, { "ends_with", ScheduleFilterType.EndsWith },
            { "has_value", ScheduleFilterType.HasValue }, { "has_no_value", ScheduleFilterType.HasNoValue },
        };

        private sealed class FieldReq
        {
            public string Parameter = "";
            public string? Heading;
            public bool Hidden;
            public double? Width; // internal (paper feet)
            public bool Total;
            public SchedulableField? Field;
            public string Path = "";
        }

        private sealed class Plan
        {
            public string ScheduleName = "";
            public ElementId CategoryId = ElementId.InvalidElementId;
            public string CategoryName = MultiCategory;
            public List<FieldReq> Fields = new List<FieldReq>();
            public Phase? Phase;
            public View? Template;
            public ViewSchedule? Existing;
            public bool Update;
            public bool FieldsChecked;
        }

        private static List<SchedulableField> Schedulable(Document doc, ScheduleDefinition def) => def.GetSchedulableFields().ToList();

        private static string FieldName(Document doc, SchedulableField f)
        {
            try { return f.GetName(doc); } catch (Exception) { return f.ParameterId != null ? FilterValues.Label(doc, f.ParameterId) : f.FieldType.ToString(); }
        }

        private static SchedulableField? Match(Document doc, List<SchedulableField> all, string name, out string? error)
        {
            error = null;
            if (string.Equals(name, "Count", StringComparison.OrdinalIgnoreCase))
            {
                var c = all.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);
                if (c != null) return c;
            }
            var named = all.Select(f => new { f, n = FieldName(doc, f) }).ToList();
            var exact = named.Where(x => string.Equals(x.n, name, StringComparison.Ordinal)).ToList();
            if (exact.Count == 0) exact = named.Where(x => string.Equals(x.n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0].f;
            if (exact.Count > 1)
            {
                var inst = exact.Where(x => x.f.FieldType == ScheduleFieldType.Instance).ToList();
                if (inst.Count == 1) return inst[0].f;
                var typ = exact.Where(x => x.f.FieldType == ScheduleFieldType.ElementType).ToList();
                if (inst.Count == 0 && typ.Count == 1) return typ[0].f;
                error = exact.Count + " schedulable fields are named '" + name + "' (" + string.Join(", ", exact.Select(x => x.f.FieldType.ToString())) + ")";
                return null;
            }
            error = "'" + name + "' is not a schedulable field for this category" + Lookup.Suggest(name, named.Select(x => x.n).Distinct().ToList());
            return null;
        }

        private const string MultiCategory = "Multi-Category";

        private static bool IsMulti(string category)
        {
            string c = category.Trim().Trim('<', '>');
            return string.Equals(c, MultiCategory, StringComparison.OrdinalIgnoreCase) || string.Equals(c, "Multi Category", StringComparison.OrdinalIgnoreCase);
        }

        private static string CategoryName(Document doc, ElementId id) => ElementIds.IsValid(id) ? Category.GetCategory(doc, id)?.Name ?? ElementIds.ToWire(id) : MultiCategory;

        private static ViewSchedule? SampleSchedule(Document doc, ElementId cat, ViewSchedule? existing)
        {
            if (existing != null && existing.Definition.CategoryId == cat) return existing;
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule && s.Definition.CategoryId == cat);
        }

        /// <param name="fieldsFrom">Definition whose schedulable fields are used (Apply passes the new schedule's).</param>
        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, ScheduleDefinition? fieldsFrom, bool declare, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            string? name = DocsUtil.CheckName(args.GetString("name"), "schedule", ctx.FieldPath(args, "name"), issues);
            bool multi = IsMulti(args.GetString("category"));
            var cat = multi ? null : DocsUtil.Category(doc, args.GetString("category"), ctx.FieldPath(args, "category"), issues);
            if (name is null || (cat is null && !multi)) return null;
            p.ScheduleName = name;
            if (cat != null)
            {
                p.CategoryId = cat.Id;
                p.CategoryName = cat.Name;
                if (!ViewSchedule.IsValidCategoryForSchedule(cat.Id))
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.Invalid, "category '" + cat.Name + "' cannot be scheduled");
                    return null;
                }
            }
            p.Existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => !s.IsTemplate && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            var other = DocsUtil.NameTaken(doc, name, ViewType.Schedule, p.Existing?.Id);
            if (other != null && !forReadback) { OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, "a schedule view named '" + other.Name + "' already exists (id " + ElementIds.ToWire(other.Id) + ")"); return null; }

            if (args.GetStringOrNull("phase") is string ph)
            {
                var phases = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>();
                var f = Lookup.ByName(phases, ph, "phase");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "phase"), f.Code, f.Error!);
                else p.Phase = f.Value;
            }
            else if (!forReadback && doc.Phases.Size > 1 && p.Existing is null)
                OpContext.Warning(issues, ctx.FieldPath(args, "phase"), RevitIssueCodes.Unchecked,
                    "the model has " + doc.Phases.Size + " phases and no phase is given; the schedule takes Revit's default phase (set phase so demolished items do not appear)");
            if (args.GetStringOrNull("template") is string tn)
                p.Template = DocsUtil.Template(doc, tn, new[] { ViewType.Schedule }, ctx.FieldPath(args, "template"), issues);

            var fl = args.GetObjectList("fields");
            var source = fieldsFrom ?? SampleSchedule(doc, p.CategoryId, p.Existing)?.Definition;
            List<SchedulableField>? schedulable = source != null ? Schedulable(doc, source) : null;
            p.FieldsChecked = schedulable != null;
            var unschedulable = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fl)
            {
                var req = new FieldReq
                {
                    Parameter = f.GetString("parameter").Trim(),
                    Heading = f.GetStringOrNull("heading"),
                    Hidden = f.GetBool("hidden"),
                    Width = ctx.PaperLengthOrNull(f, "width") is double w ? ctx.Paper.LengthToInternal(w) : (double?)null,
                    Total = f.GetBool("total"),
                    Path = JsonPath.Key(f.Path, "parameter"),
                };
                if (!seen.Add(req.Parameter)) { OpContext.Error(issues, req.Path, RevitIssueCodes.Invalid, "field '" + req.Parameter + "' is listed twice"); continue; }
                if (schedulable != null)
                {
                    req.Field = Match(doc, schedulable, req.Parameter, out string? err);
                    if (req.Field is null) { unschedulable.Add(req.Parameter); OpContext.Error(issues, req.Path, RevitIssueCodes.NotFound, err!); }
                }
                p.Fields.Add(req);
            }
            if (unschedulable.Count > 1)
                OpContext.Error(issues, ctx.FieldPath(args, "fields"), RevitIssueCodes.NotFound, unschedulable.Count + " requested fields are not schedulable for " + p.CategoryName + ": " + string.Join(", ", unschedulable.Select(u => "'" + u + "'")));
            if (!p.FieldsChecked && !forReadback && fieldsFrom is null)
                OpContext.Warning(issues, ctx.FieldPath(args, "fields"), RevitIssueCodes.Unchecked,
                    "the model has no " + p.CategoryName + " schedule yet, so the fields are checked against the new schedule when the op runs");
            foreach (var f in args.GetObjectList("filters"))
                if (!p.Fields.Any(x => string.Equals(x.Parameter, f.GetString("field").Trim(), StringComparison.OrdinalIgnoreCase)))
                    OpContext.Error(issues, JsonPath.Key(f.Path, "field"), RevitIssueCodes.Invalid, "filter field '" + f.GetString("field") + "' must be one of the schedule's fields (add it with hidden:true)");
            foreach (var s in args.GetObjectList("sort"))
                if (!p.Fields.Any(x => string.Equals(x.Parameter, s.GetString("field").Trim(), StringComparison.OrdinalIgnoreCase)))
                    OpContext.Error(issues, JsonPath.Key(s.Path, "field"), RevitIssueCodes.Invalid, "sort field '" + s.GetString("field") + "' must be one of the schedule's fields");
            foreach (var f in p.Fields.Where(x => x.Total && x.Field != null && x.Field.FieldType == ScheduleFieldType.Count))
                OpContext.Warning(issues, f.Path, RevitIssueCodes.Invalid, "Count is totalled through grand totals or group footers; total:true on it has no separate effect");

            if (DocsUtil.HasErrors(issues)) return null;
            if (p.Existing != null && !forReadback && fieldsFrom is null)
            {
                if (p.Existing.Definition.CategoryId != p.CategoryId)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.Mismatch, "schedule '" + p.Existing.Name + "' exists for another category (" +
                        CategoryName(doc, p.Existing.Definition.CategoryId) + "); a schedule's category cannot be changed");
                    return null;
                }
                var diffs = Differences(ctx, args, p, p.Existing);
                if (diffs.Count > 0)
                {
                    if (args.GetBool("update")) { p.Update = true; if (declare) ctx.DeclareModifies(p.Existing.Id); }
                    else OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Mismatch,
                        "schedule '" + p.Existing.Name + "' exists and differs (" + string.Join("; ", diffs.Take(8)) + "); set update:true to rebuild it");
                }
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        // ----- Description of a schedule, for ensure and readback -----

        private static List<string> Differences(OpContext ctx, OpArgs args, Plan p, ViewSchedule s)
        {
            var doc = ctx.Doc;
            var d = new List<string>();
            var def = s.Definition;
            var fields = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).ToList();
            var names = fields.Select(f => f.GetName()).ToList();
            var wantNames = p.Fields.Select(f => f.Field != null ? FieldName(doc, f.Field) : f.Parameter).ToList();
            if (!names.SequenceEqual(wantNames, StringComparer.OrdinalIgnoreCase))
                d.Add("fields are " + string.Join(", ", names));
            else
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i];
                    var w = p.Fields[i];
                    if (w.Heading != null && !string.Equals(f.ColumnHeading, w.Heading, StringComparison.Ordinal)) d.Add("'" + names[i] + "' heading is '" + f.ColumnHeading + "'");
                    if (f.IsHidden != w.Hidden) d.Add("'" + names[i] + "' hidden is " + (f.IsHidden ? "true" : "false"));
                    bool totals = f.DisplayType == ScheduleFieldDisplayType.Totals;
                    if (w.Field?.FieldType != ScheduleFieldType.Count && totals != w.Total) d.Add("'" + names[i] + "' totals is " + (totals ? "on" : "off"));
                    if (w.Width.HasValue && Math.Abs(f.SheetColumnWidth - w.Width.Value) > ctx.Paper.LengthToleranceInternal)
                        d.Add("'" + names[i] + "' width is " + RevitUnits.Num(ctx.Paper.LengthFromInternal(f.SheetColumnWidth)).ToJson() + " " + SA.Core.Units.Length.Symbol(ctx.Paper.JobUnit));
                }
            }
            var haveFilters = FilterTexts(doc, def);
            var wantFilters = RequestedFilterTexts(ctx, args, def, out string? ferr);
            if (ferr != null) d.Add(ferr);
            else if (!haveFilters.SequenceEqual(wantFilters, StringComparer.Ordinal)) d.Add("filters are " + (haveFilters.Count == 0 ? "none" : string.Join(", ", haveFilters)));
            var haveSort = SortTexts(def);
            var wantSort = args.GetObjectList("sort").Select(x => SortText(x.GetString("field").Trim(), x.GetString("order") == "descending", x.GetBool("header"), x.GetBool("footer"), x.GetBool("blankLine"))).ToList();
            if (!haveSort.SequenceEqual(wantSort, StringComparer.OrdinalIgnoreCase)) d.Add("sorting is " + (haveSort.Count == 0 ? "none" : string.Join(", ", haveSort)));
            if (def.IsItemized != args.GetBool("itemize")) d.Add("itemize is " + (def.IsItemized ? "true" : "false"));
            if (def.IncludeLinkedFiles != args.GetBool("includeLinks")) d.Add("includeLinks is " + (def.IncludeLinkedFiles ? "true" : "false"));
            if (def.ShowGrandTotal != args.GetBool("grandTotals")) d.Add("grand totals is " + (def.ShowGrandTotal ? "on" : "off"));
            if (p.Phase != null)
            {
                var ph = s.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                if (ph != p.Phase.Id) d.Add("phase is '" + (ph != null && doc.GetElement(ph) is Phase cur ? cur.Name : "none") + "'");
            }
            if (p.Template != null && s.ViewTemplateId != p.Template.Id) d.Add("template is '" + (DocsUtil.TemplateOf(s)?.Name ?? "none") + "'");
            return d;
        }

        private static string SortText(string field, bool descending, bool header, bool footer, bool blank) =>
            field + (descending ? " desc" : " asc") + (header ? " header" : "") + (footer ? " footer" : "") + (blank ? " blank" : "");

        private static List<string> SortTexts(ScheduleDefinition def)
        {
            var list = new List<string>();
            for (int i = 0; i < def.GetSortGroupFieldCount(); i++)
            {
                var s = def.GetSortGroupField(i);
                list.Add(SortText(def.GetField(s.FieldId).GetName(), s.SortOrder == ScheduleSortOrder.Descending, s.ShowHeader, s.ShowFooter, s.ShowBlankLine));
            }
            return list;
        }

        private static List<string> FilterTexts(Document doc, ScheduleDefinition def)
        {
            var list = new List<string>();
            for (int i = 0; i < def.GetFilterCount(); i++)
            {
                var f = def.GetFilter(i);
                string v = f.IsStringValue ? "'" + f.GetStringValue() + "'" : f.IsIntegerValue ? f.GetIntegerValue().ToString(CultureInfo.InvariantCulture)
                    : f.IsDoubleValue ? Math.Round(f.GetDoubleValue(), 6).ToString("R", CultureInfo.InvariantCulture)
                    : f.IsElementIdValue ? ElementIds.ToWire(f.GetElementIdValue()) : "";
                list.Add(def.GetField(f.FieldId).GetName() + " " + f.FilterType + (v.Length > 0 ? " " + v : ""));
            }
            return list;
        }

        /// <summary>Converts the requested filters against the fields of <paramref name="def"/>; null error on success.</summary>
        private static List<ScheduleFilter> RequestedFilters(OpContext ctx, OpArgs args, ScheduleDefinition def, out string? error)
        {
            error = null;
            var doc = ctx.Doc;
            var list = new List<ScheduleFilter>();
            var fields = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).ToList();
            foreach (var f in args.GetObjectList("filters"))
            {
                string fname = f.GetString("field").Trim();
                var field = fields.FirstOrDefault(x => string.Equals(x.GetName(), fname, StringComparison.OrdinalIgnoreCase));
                if (field is null) { error = "filter field '" + fname + "' is not in the schedule"; return list; }
                string op = f.GetString("operator");
                var type = Ops[op];
                bool presence = op == "has_value" || op == "has_no_value";
                bool substring = op == "contains" || op == "not_contains" || op == "begins_with" || op == "ends_with";
                if (presence && !def.CanFilterByValuePresence(field.FieldId)) { error = "field '" + fname + "' cannot be filtered by " + op; return list; }
                if (substring && !def.CanFilterBySubstring(field.FieldId)) { error = "field '" + fname + "' cannot be filtered by " + op + " (text fields only)"; return list; }
                if (!presence && !substring && !def.CanFilterByValue(field.FieldId)) { error = "field '" + fname + "' cannot be filtered by value"; return list; }
                if (presence) { list.Add(new ScheduleFilter(field.FieldId, type)); continue; }
                var raw = f.Get("value");
                if (raw is null || raw.IsNull) { error = "filter on '" + fname + "' needs a value"; return list; }
                var spec = field.GetSpecTypeId();
                var storage = substring ? StorageType.String : FilterValues.StorageFromSpec(spec);
                if (storage == StorageType.None && field.ParameterId != null && ElementIds.ToBuiltInParameter(field.ParameterId) is BuiltInParameter bip)
                {
                    try { storage = doc.get_TypeOfStorage(bip); } catch (Exception) { }
                }
                if (storage == StorageType.None) storage = raw is JsonString ? StorageType.String : StorageType.Double;
                if (!FilterValues.Convert(doc, ctx.Units, storage, spec, raw, out var tv, out string verr)) { error = "filter on '" + fname + "': " + verr; return list; }
                switch (tv.Storage)
                {
                    case StorageType.String: list.Add(new ScheduleFilter(field.FieldId, type, tv.Text ?? "")); break;
                    case StorageType.Integer: list.Add(new ScheduleFilter(field.FieldId, type, tv.Int)); break;
                    case StorageType.Double: list.Add(new ScheduleFilter(field.FieldId, type, tv.Double)); break;
                    default: list.Add(new ScheduleFilter(field.FieldId, type, tv.Id ?? ElementId.InvalidElementId)); break;
                }
            }
            return list;
        }

        private static List<string> RequestedFilterTexts(OpContext ctx, OpArgs args, ScheduleDefinition def, out string? error)
        {
            var filters = RequestedFilters(ctx, args, def, out error);
            var list = new List<string>();
            foreach (var f in filters)
            {
                string v = f.IsStringValue ? "'" + f.GetStringValue() + "'" : f.IsIntegerValue ? f.GetIntegerValue().ToString(CultureInfo.InvariantCulture)
                    : f.IsDoubleValue ? Math.Round(f.GetDoubleValue(), 6).ToString("R", CultureInfo.InvariantCulture)
                    : f.IsElementIdValue ? ElementIds.ToWire(f.GetElementIdValue()) : "";
                list.Add(def.GetField(f.FieldId).GetName() + " " + f.FilterType + (v.Length > 0 ? " " + v : ""));
            }
            return list;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, null, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, null, false) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            ViewSchedule s;
            if (p.Existing != null && !p.Update)
            {
                result.Unchanged.Add(ElementIds.ToWire(p.Existing.Id));
                result.Outputs.Set("schedule", ElementIds.ToWire(p.Existing.Id));
                result.Outputs.Set("rowCount", RowCount(p.Existing));
                return;
            }
            if (p.Existing is null)
            {
                s = ViewSchedule.CreateSchedule(doc, p.CategoryId);
                s.Name = p.ScheduleName;
                result.Created.Add(ElementIds.ToWire(s.Id));
            }
            else
            {
                s = p.Existing;
                if (!string.Equals(s.Name, p.ScheduleName, StringComparison.Ordinal)) s.Name = p.ScheduleName;
                s.Definition.ClearSortGroupFields();
                s.Definition.ClearFilters();
                s.Definition.ClearFields();
                result.Modified.Add(ElementIds.ToWire(s.Id));
            }
            var def = s.Definition;
            // Resolve the fields against this schedule's own schedulable fields.
            var issues2 = new List<Issue>();
            var q = Build(ctx, args, issues2, def, false, forReadback: true) ?? throw DocsUtil.Invalid(issues2);
            foreach (var f in q.Fields)
            {
                var field = def.AddField(f.Field!);
                if (f.Heading != null) field.ColumnHeading = f.Heading;
                if (f.Hidden) field.IsHidden = true;
                if (f.Width.HasValue) field.SheetColumnWidth = f.Width.Value;
                if (f.Total && f.Field!.FieldType != ScheduleFieldType.Count)
                {
                    if (!field.CanTotal()) throw new OpFailedException("cannot_total", "field '" + f.Parameter + "' cannot be totalled (only numeric fields can)");
                    field.DisplayType = ScheduleFieldDisplayType.Totals;
                }
            }
            var filters = RequestedFilters(ctx, args, def, out string? ferr);
            if (ferr != null) throw new OpFailedException("invalid_filter", ferr);
            foreach (var f in filters) def.AddFilter(f);
            var fieldList = Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i)).ToList();
            foreach (var so in args.GetObjectList("sort"))
            {
                string fname = so.GetString("field").Trim();
                var req = q.Fields.FirstOrDefault(r => string.Equals(r.Parameter, fname, StringComparison.OrdinalIgnoreCase));
                string actual = req?.Field != null ? FieldName(doc, req.Field) : fname;
                var field = fieldList.FirstOrDefault(x => string.Equals(x.GetName(), actual, StringComparison.OrdinalIgnoreCase))
                    ?? throw new OpFailedException("invalid_sort", "sort field '" + fname + "' is not in the schedule");
                if (!def.CanSortByField(field.FieldId)) throw new OpFailedException("cannot_sort", "field '" + fname + "' cannot be used for sorting");
                var sg = new ScheduleSortGroupField(field.FieldId, so.GetString("order") == "descending" ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
                {
                    ShowHeader = so.GetBool("header"),
                    ShowFooter = so.GetBool("footer"),
                    ShowBlankLine = so.GetBool("blankLine"),
                };
                def.AddSortGroupField(sg);
            }
            def.IsItemized = args.GetBool("itemize");
            bool links = args.GetBool("includeLinks");
            if (def.IncludeLinkedFiles != links)
            {
                if (!def.CanIncludeLinkedFiles()) throw new OpFailedException("not_allowed", "a " + p.CategoryName + " schedule cannot include elements of linked files");
                def.IncludeLinkedFiles = links;
            }
            def.ShowGrandTotal = args.GetBool("grandTotals");
            if (p.Phase != null)
            {
                var ph = s.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (ph is null || ph.IsReadOnly || !ph.Set(p.Phase.Id)) throw new OpFailedException("parameter_value", "Revit refused phase '" + p.Phase.Name + "' for the schedule");
            }
            if (p.Template != null)
            {
                if (!s.IsValidViewTemplate(p.Template.Id)) throw new OpFailedException("template_invalid", "template '" + p.Template.Name + "' cannot be applied to this schedule");
                s.ViewTemplateId = p.Template.Id;
            }
            doc.Regenerate();
            result.Outputs.Set("schedule", ElementIds.ToWire(s.Id));
            result.Outputs.Set("rowCount", RowCount(s));
        }

        private static int RowCount(ViewSchedule s)
        {
            try
            {
                var body = s.GetTableData().GetSectionData(SectionType.Body);
                int rows = body.NumberOfRows;
                if (s.Definition.ShowHeaders) rows -= 1;
                return Math.Max(0, rows);
            }
            catch (Exception) { return 0; }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            string? id = result.Outputs.GetStringOrNull("schedule");
            var s = id != null && ElementIds.TryParse(id, out var eid) ? doc.GetElement(eid) as ViewSchedule : null;
            if (s is null) { check.Fail("the schedule was not found after the op"); check.Apply(result); return; }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, s.Definition, false, forReadback: true);
            if (p is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            rb.Set("id", id!);
            check.Text(rb, "name", s.Name, p.ScheduleName);
            check.Text(rb, "category", CategoryName(doc, s.Definition.CategoryId), p.CategoryName);
            var def = s.Definition;
            var fields = new JsonArray();
            for (int i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(i);
                fields.Add(new JsonObject().Add("name", f.GetName()).Add("heading", f.ColumnHeading).Add("hidden", f.IsHidden)
                    .Add("totals", f.DisplayType == ScheduleFieldDisplayType.Totals).Add("width", ctx.Paper.LengthJson(f.SheetColumnWidth)));
            }
            rb.Set("fields", fields);
            rb.Set("filters", JsonArray.FromStrings(FilterTexts(doc, def)));
            rb.Set("sort", JsonArray.FromStrings(SortTexts(def)));
            check.Flag(rb, "itemize", def.IsItemized, args.GetBool("itemize"));
            check.Flag(rb, "includeLinks", def.IncludeLinkedFiles, args.GetBool("includeLinks"));
            check.Flag(rb, "grandTotals", def.ShowGrandTotal, args.GetBool("grandTotals"));
            foreach (var d in Differences(ctx, args, p, s)) check.Fail(d);
            int rows = RowCount(s);
            rb.Set("rowCount", rows);
            int elements;
            try { elements = new FilteredElementCollector(doc, s.Id).WhereElementIsNotElementType().GetElementCount(); } catch (Exception) { elements = -1; }
            rb.Set("elementCount", elements);
            result.Outputs.Set("rowCount", rows);
            rb.Set("state", result.Created.Contains(id!) ? "created" : result.Modified.Contains(id!) ? "updated" : "unchanged");
            check.Apply(result);
        }
    }
}
