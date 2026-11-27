using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Ops.Support
{
    /// <summary>The outcome of a name lookup: the element, or a message that names close matches.</summary>
    internal sealed class Found<T> where T : Element
    {
        public Found(T? value, string? error, string code)
        {
            Value = value;
            Error = error;
            Code = code;
        }

        public T? Value { get; }
        public string? Error { get; }
        public string Code { get; }
        public bool Ok => Value != null;
    }

    /// <summary>
    /// Name lookups used by handlers. Matching is exact first, then case-insensitive when that is unique. A miss
    /// lists up to five close names so the agent can correct the job without another snapshot.
    /// </summary>
    internal static class Lookup
    {
        public static Found<T> ByName<T>(IEnumerable<T> candidates, string name, string what, Func<T, string>? display = null) where T : Element
        {
            display ??= e => e.Name;
            var list = candidates.ToList();
            var exact = list.Where(e => string.Equals(display(e), name, StringComparison.Ordinal)).ToList();
            if (exact.Count == 1) return new Found<T>(exact[0], null, "");
            if (exact.Count > 1)
                return new Found<T>(null, exact.Count + " " + what + "s are named '" + name + "' (ids " +
                    string.Join(", ", exact.Take(5).Select(e => ElementIds.ToWire(e.Id))) + "); use an id", Ops.RevitIssueCodes.Ambiguous);
            var loose = list.Where(e => string.Equals(display(e), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count == 1) return new Found<T>(loose[0], null, "");
            var names = list.Select(display).Distinct().ToList();
            return new Found<T>(null, "no " + what + " named '" + name + "'" + Suggest(name, names, list.Count), Ops.RevitIssueCodes.NotFound);
        }

        /// <summary>"; close matches: a, b, c" or the first few available names.</summary>
        public static string Suggest(string name, IList<string> available, int total = -1)
        {
            if (available.Count == 0) return "; there are none in this model";
            string n = name.ToLowerInvariant();
            var scored = available
                .Select(a => new { a, d = Distance(n, a.ToLowerInvariant()), sub = a.ToLowerInvariant().Contains(n) || n.Contains(a.ToLowerInvariant()) })
                .OrderBy(x => x.sub ? 0 : 1).ThenBy(x => x.d).ThenBy(x => x.a, StringComparer.Ordinal)
                .Where(x => x.sub || x.d <= Math.Max(3, n.Length / 2))
                .Take(5).Select(x => x.a).ToList();
            if (scored.Count > 0) return "; close matches: " + string.Join(", ", scored.Select(s => "'" + s + "'"));
            return "; available (" + (total < 0 ? available.Count : total) + "): " +
                   string.Join(", ", available.OrderBy(s => s, StringComparer.Ordinal).Take(8).Select(s => "'" + s + "'")) + (available.Count > 8 ? ", ..." : "");
        }

        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        public static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();

        public static Found<Level> Level(Document doc, string name) => ByName(Levels(doc), name, "level");

        /// <summary>An element type of class T by type name, or by "Family : Type".</summary>
        public static Found<T> Type<T>(Document doc, string name, string what) where T : ElementType
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
            if (name.Contains(" : "))
                return ByName(all, name, what, FamilyTypeName);
            return ByName(all, name, what);
        }

        /// <summary>A loadable family type in a category, by "Family : Type" or (when unique) by type name.</summary>
        public static Found<FamilySymbol> Symbol(Document doc, BuiltInCategory category, string name, string what)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().ToList();
            if (name.Contains(" : ")) return ByName(all, name, what, FamilyTypeName);
            var byType = ByName(all, name, what);
            if (byType.Ok || byType.Code == Ops.RevitIssueCodes.Ambiguous)
            {
                if (byType.Code == Ops.RevitIssueCodes.Ambiguous)
                    return new Found<FamilySymbol>(null, "type name '" + name + "' exists in several families; write it as \"Family : Type\"; candidates: " +
                        string.Join(", ", all.Where(s => s.Name == name).Select(FamilyTypeName).OrderBy(x => x, StringComparer.Ordinal).Select(x => "'" + x + "'")), Ops.RevitIssueCodes.Ambiguous);
                return byType;
            }
            return new Found<FamilySymbol>(null, "no " + what + " '" + name + "'" + Suggest(name, all.Select(FamilyTypeName).ToList()), Ops.RevitIssueCodes.NotFound);
        }

        public static string FamilyTypeName(ElementType t)
        {
            string fam = t.FamilyName ?? "";
            return fam.Length > 0 ? fam + " : " + t.Name : t.Name;
        }

        public static ViewFamilyType? FirstViewFamilyType(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(v => v.ViewFamily == family).OrderBy(v => v.Name, StringComparer.Ordinal).FirstOrDefault();

        public static ViewSheet? SheetByNumber(Document doc, string number) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// A view or sheet from a resolved ref value: an element id, a result object, "sheet:&lt;number&gt;", or a
        /// view's exact name (a sheet also matches its number).
        /// </summary>
        public static Found<View> View(Document doc, JsonValue? value)
        {
            string? text = value is JsonString s ? s.Value : null;
            string? id = SA.Core.Jobs.Refs.ElementId(value);
            if (id != null && ElementIds.TryParse(id, out var eid))
            {
                if (doc.GetElement(eid) is View v) return new Found<View>(v, null, "");
                if (text is null) return new Found<View>(null, "element " + id + " is not a view", Ops.RevitIssueCodes.NotFound);
            }
            if (text is null) return new Found<View>(null, "expected a view id, reference or name", Ops.RevitIssueCodes.Invalid);
            if (text.StartsWith("sheet:", StringComparison.OrdinalIgnoreCase))
            {
                var sh = SheetByNumber(doc, text.Substring(6).Trim());
                return sh != null ? new Found<View>(sh, null, "") : new Found<View>(null, "no sheet numbered '" + text.Substring(6).Trim() + "'", Ops.RevitIssueCodes.NotFound);
            }
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var byName = ByName(views, text, "view");
            if (byName.Ok) return byName;
            var sheet = SheetByNumber(doc, text);
            if (sheet != null) return new Found<View>(sheet, null, "");
            return byName;
        }

        /// <summary>A category by built-in name (OST_Doors) or display name (Doors).</summary>
        public static Category? Category(Document doc, string name, out string? error)
        {
            error = null;
            if (name.StartsWith("OST_", StringComparison.Ordinal) && Enum.TryParse(name, false, out BuiltInCategory bic))
            {
                try
                {
                    var c = Autodesk.Revit.DB.Category.GetCategory(doc, bic);
                    if (c != null) return c;
                }
                catch (Exception) { }
                error = "category " + name + " does not exist in this document";
                return null;
            }
            var all = new List<Category>();
            foreach (Category c in doc.Settings.Categories) all.Add(c);
            var hit = all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
                      ?? all.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            error = "no category '" + name + "'" + Suggest(name, all.Select(c => c.Name).ToList());
            return null;
        }

        public static string LevelName(Document doc, ElementId id) =>
            ElementIds.IsValid(id) && doc.GetElement(id) is Level l ? l.Name : "";
    }
}
