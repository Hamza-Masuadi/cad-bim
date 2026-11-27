using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs.Support
{
    /// <summary>
    /// Lookups and checks shared by the documentation handlers (views, sheets, schedules, annotation). Everything
    /// here is read-only: none of it changes the document, so Validate can call all of it.
    /// </summary>
    internal static class DocsUtil
    {
        /// <summary>Characters Revit refuses in view, sheet and schedule names (the same list sheet.create uses).</summary>
        public static readonly char[] ForbiddenNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Throws the op's validation errors from inside Apply (the plan is rebuilt there and must still be valid).</summary>
        public static OpFailedException Invalid(List<Issue> issues) =>
            new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Take(8).Select(i => i.Path + ": " + i.Message)));

        public static bool HasErrors(List<Issue> issues) => issues.Any(i => i.IsError);

        /// <summary>Checks a name for emptiness and characters Revit refuses. Returns the trimmed name or null.</summary>
        public static string? CheckName(string? raw, string what, string path, List<Issue> issues)
        {
            if (raw is null) return null;
            string name = raw.Trim();
            if (name.Length == 0) { OpContext.Error(issues, path, RevitIssueCodes.Invalid, what + " name is empty"); return null; }
            int bad = name.IndexOfAny(ForbiddenNameChars);
            if (bad >= 0) { OpContext.Error(issues, path, RevitIssueCodes.Invalid, "'" + name[bad] + "' is not allowed in a " + what + " name"); return null; }
            return name;
        }

        /// <summary>
        /// A view (template or not) of the same view type that already uses this name, ignoring case, or null.
        /// Revit keeps view names unique per view type; the check is case-insensitive to be on the safe side.
        /// </summary>
        public static View? NameTaken(Document doc, string name, ViewType type, ElementId? except = null)
        {
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (except != null && v.Id == except) continue;
                if (v.ViewType != type) continue;
                if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }

        /// <summary>The project base point's internal Z. Job elevations are measured from it (see LevelEnsure).</summary>
        public static double BaseZ(Document doc)
        {
            try { return BasePoint.GetProjectBasePoint(doc).Position.Z; }
            catch (Exception) { return 0; }
        }

        /// <summary>A view family type of the given family, by name (or "Family : Type"), else the first one by name.</summary>
        public static ViewFamilyType? ViewFamilyType(Document doc, ViewFamily family, string? name, string path, List<Issue> issues)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(v => v.ViewFamily == family).OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
            if (name is null)
            {
                if (all.Count == 0) OpContext.Error(issues, path, RevitIssueCodes.NotFound, "the model has no " + family + " view type");
                return all.FirstOrDefault();
            }
            var hit = name.Contains(" : ") ? Lookup.ByName(all, name, family + " view type", Lookup.FamilyTypeName) : Lookup.ByName(all, name, family + " view type");
            if (!hit.Ok) { OpContext.Error(issues, path, hit.Code, hit.Error!); return null; }
            return hit.Value;
        }

        /// <summary>
        /// A view template by name. When <paramref name="types"/> is given, the template must be of one of those view
        /// types (a floor plan template for floor plans, and so on).
        /// </summary>
        public static View? Template(Document doc, string name, ViewType[]? types, string path, List<Issue> issues)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            var candidates = types is null ? all : all.Where(t => types.Contains(t.ViewType)).ToList();
            var hit = Lookup.ByName(candidates, name, "view template");
            if (hit.Ok) return hit.Value;
            var other = all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (other != null && types != null)
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "view template '" + other.Name + "' is a " + other.ViewType + " template; this view needs a " +
                    string.Join(" or ", types.Select(t => t.ToString())) + " template" + Lookup.Suggest(name, candidates.Select(c => c.Name).ToList()));
            else OpContext.Error(issues, path, hit.Code, hit.Error!);
            return null;
        }

        private static readonly BuiltInParameter[] ScaleParams =
            { BuiltInParameter.VIEW_SCALE, BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL };

        /// <summary>True when the template controls the view scale (the scale parameter is included and not unticked).</summary>
        public static bool TemplateControlsScale(View template) => TemplateControls(template, ScaleParams);

        /// <summary>True when the template controls any of these view parameters (included and not unticked).</summary>
        public static bool TemplateControls(View template, params BuiltInParameter[] parameters)
        {
            var controlled = new HashSet<long>(template.GetTemplateParameterIds().Select(ElementIds.ToLong));
            foreach (var id in template.GetNonControlledTemplateParameterIds()) controlled.Remove(ElementIds.ToLong(id));
            return parameters.Any(p => controlled.Contains(ElementIds.ToLong(new ElementId(p))));
        }

        /// <summary>The template controlling this view, or null.</summary>
        public static View? TemplateOf(View view) =>
            ElementIds.IsValid(view.ViewTemplateId) ? view.Document.GetElement(view.ViewTemplateId) as View : null;

        /// <summary>A view or a view template from a ref value (ids, references, view names, then template names).</summary>
        public static View? ViewOrTemplate(OpContext ctx, JsonValue? value, string path, List<Issue> issues, out bool pending)
        {
            pending = OpContext.IsPending(value);
            if (pending) return null;
            var f = Lookup.View(ctx.Doc, value);
            if (f.Ok) return f.Value;
            if (value is JsonString s)
            {
                var templates = new FilteredElementCollector(ctx.Doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
                var t = Lookup.ByName(templates, s.Value, "view template");
                if (t.Ok) return t.Value;
            }
            OpContext.Error(issues, path, f.Code, f.Error!);
            return null;
        }

        /// <summary>
        /// Parses a scale such as "1:100" or 1/4" = 1'-0" to Revit's integer 1/X ratio. Revit stores scales as whole
        /// numbers from 1 to 24,000 (ElevationMarker.CreateElevationMarker documents the range). When the job names a
        /// standard with a scale list, the scale must be on it ("only approved scales", sheets-sets.md section 6).
        /// </summary>
        public static int? Scale(OpContext ctx, string? text, string path, List<Issue> issues)
        {
            if (text is null) return null;
            if (!SA.Core.Standards.Scale.TryParseRatio(text, out double ratio))
            {
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "'" + text + "' is not a scale; write \"1:100\" or 1/4\" = 1'-0\"");
                return null;
            }
            double r = Math.Round(ratio);
            if (Math.Abs(ratio - r) > 1e-6 || r < 1 || r > 24000)
            {
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "scale " + text + " is 1:" + ratio.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) +
                    "; Revit view scales are whole numbers from 1:1 to 1:24000");
                return null;
            }
            var std = Standard(ctx);
            if (std != null && std.Scales.Count > 0 && !std.Scales.Any(s => Math.Abs(s.Ratio - r) < 1e-6))
            {
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "scale " + text + " is not in the standard's approved scales (" +
                    string.Join(", ", std.Scales.Take(12).Select(s => s.Name)) + ")");
                return null;
            }
            return (int)r;
        }

        /// <summary>
        /// The job's standard, as the runner loaded it before the job ran (a relative path resolves against the model's
        /// folder; a standard that cannot be loaded refuses the job), or null when the job names none.
        /// </summary>
        public static ProjectStandard? Standard(OpContext ctx) => ctx.Standard;

        /// <summary>
        /// A view from a ref field: id, reference, name or "sheet:&lt;number&gt;". Returns null and sets
        /// <paramref name="pending"/> when the reference is not resolved yet (preflight); returns null with an issue on a miss.
        /// </summary>
        public static View? View(OpContext ctx, JsonValue? value, string path, List<Issue> issues, out bool pending)
        {
            pending = OpContext.IsPending(value);
            if (pending) return null;
            var f = Lookup.View(ctx.Doc, value);
            if (!f.Ok) { OpContext.Error(issues, path, f.Code, f.Error!); return null; }
            return f.Value;
        }

        /// <summary>
        /// The views of a list-of-refs field, in order. Items can be ids, references, names, or (for a whole-list
        /// reference such as "$v1.views") nested arrays, which are flattened. Pending items are skipped and reported
        /// through <paramref name="anyPending"/>. Duplicates are refused.
        /// </summary>
        public static List<KeyValuePair<View, string>> Views(OpContext ctx, OpArgs args, string field, List<Issue> issues, out bool anyPending)
        {
            anyPending = false;
            var list = new List<KeyValuePair<View, string>>();
            var value = args.Get(field);
            if (value is null) return list;
            if (OpContext.IsPending(value) && !(value is JsonArray)) { anyPending = true; return list; }
            var seen = new HashSet<long>();
            bool pend = false;
            void Take(JsonValue item, string at)
            {
                if (OpContext.IsPending(item)) { pend = true; return; }
                if (item is JsonArray inner)
                {
                    for (int j = 0; j < inner.Count; j++) Take(inner[j], JsonPath.Index(at, j));
                    return;
                }
                if (item is JsonObject ro && !ro.ContainsKey("id") && ro["ids"] is JsonArray ids)
                {
                    for (int j = 0; j < ids.Count; j++) Take(ids[j], JsonPath.Index(at, j));
                    return;
                }
                var f = Lookup.View(ctx.Doc, item);
                if (!f.Ok) { OpContext.Error(issues, at, f.Code, f.Error!); return; }
                if (!seen.Add(ElementIds.ToLong(f.Value!.Id))) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "view " + Describe(f.Value!) + " is listed twice"); return; }
                list.Add(new KeyValuePair<View, string>(f.Value!, at));
            }
            var arr = value as JsonArray ?? new JsonArray(value);
            for (int i = 0; i < arr.Count; i++) Take(arr[i], JsonPath.Index(JsonPath.Key(args.Path, field), i));
            anyPending = pend;
            return list;
        }

        /// <summary>A sheet from an id, a reference, "sheet:&lt;number&gt;" or a bare sheet number.</summary>
        public static ViewSheet? Sheet(OpContext ctx, JsonValue? value, string path, List<Issue> issues, out bool pending)
        {
            pending = OpContext.IsPending(value);
            if (pending) return null;
            var doc = ctx.Doc;
            string? id = Refs.ElementId(value);
            if (id != null && ElementIds.TryParse(id, out var eid) && doc.GetElement(eid) is Element e)
            {
                if (e is ViewSheet s0) return Usable(s0, path, issues);
                if (!(value is JsonString))
                {
                    OpContext.Error(issues, path, RevitIssueCodes.Invalid, "element " + id + " is a " + (e.Category?.Name ?? e.GetType().Name) + ", not a sheet");
                    return null;
                }
            }
            if (value is JsonString str)
            {
                string t = str.Value.Trim();
                if (t.StartsWith("sheet:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(6).Trim();
                var s = Lookup.SheetByNumber(doc, t);
                if (s != null) return Usable(s, path, issues);
                var numbers = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(x => x.SheetNumber).ToList();
                OpContext.Error(issues, path, RevitIssueCodes.NotFound, "no sheet numbered '" + t + "'" + Lookup.Suggest(t, numbers));
                return null;
            }
            OpContext.Error(issues, path, RevitIssueCodes.Invalid, "expected a sheet id, a reference or a sheet number");
            return null;
        }

        private static ViewSheet? Usable(ViewSheet s, string path, List<Issue> issues)
        {
            if (s.IsPlaceholder)
            {
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "sheet '" + s.SheetNumber + "' is a placeholder sheet; views cannot be placed on it");
                return null;
            }
            return s;
        }

        /// <summary>Sheets the view is placed on (viewports, and schedule instances for schedules).</summary>
        public static List<ViewSheet> SheetsShowing(Document doc, View view)
        {
            var ids = new HashSet<long>();
            if (view is ViewSchedule)
            {
                foreach (var si in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                    if (si.ScheduleId == view.Id) ids.Add(ElementIds.ToLong(si.OwnerViewId));
            }
            else
            {
                foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                    if (vp.ViewId == view.Id) ids.Add(ElementIds.ToLong(vp.SheetId));
            }
            return ids.Select(i => doc.GetElement(ElementIds.FromLong(i)) as ViewSheet).Where(s => s != null).Cast<ViewSheet>().ToList();
        }

        /// <summary>A category by built-in or display name, with an issue on a miss.</summary>
        public static Category? Category(Document doc, string name, string path, List<Issue> issues)
        {
            var c = Lookup.Category(doc, name, out string? err);
            if (c is null) OpContext.Error(issues, path, RevitIssueCodes.NotFound, err!);
            return c;
        }

        public static string Describe(View v) => "'" + v.Name + "' (" + v.ViewType + ", id " + ElementIds.ToWire(v.Id) + ")";

        /// <summary>True for view types that show model geometry in 2D and accept annotation (tags, dimensions, notes).</summary>
        public static bool IsAnnotatable2D(View v) =>
            v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.EngineeringPlan ||
            v.ViewType == ViewType.AreaPlan || v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Detail;

        /// <summary>Plan-like views (the view plane is horizontal).</summary>
        public static bool IsPlan(View v) => v is ViewPlan;

        public static JsonArray Xy(double x, double y) => new JsonArray(RevitUnits.Num(x), RevitUnits.Num(y));

        /// <summary>Unit vector in plan from an XYZ (Z dropped), or null when it is vertical.</summary>
        public static XYZ? Flat(XYZ v)
        {
            var f = new XYZ(v.X, v.Y, 0);
            return f.GetLength() < 1e-9 ? null : f.Normalize();
        }

        /// <summary>Signed angle in radians from a to b about +Z (both horizontal unit vectors).</summary>
        public static double SignedAngle(XYZ a, XYZ b) => Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
    }
}
