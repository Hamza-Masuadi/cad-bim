using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;

namespace SA.Revit.Ops.FamilyCreation.Support
{
    /// <summary>
    /// Finds family templates (.rft). Revit's FamilyTemplatePath is one language folder (for example ...\Family
    /// Templates\English); its sibling folders hold the other languages, so both are searched, the configured one first.
    /// Hosted templates (wall, face, ceiling, floor, roof or line based) are never picked by category: a family.create
    /// family stands on its own.
    /// </summary>
    internal static class FamilyTemplates
    {
        private static readonly string[] Hosted = { "wall based", "face based", "ceiling based", "floor based", "roof based", "line based", "hosted", "adaptive", "pattern based" };

        /// <summary>Every .rft under the configured template folder, then under its sibling language folders.</summary>
        public static List<string> All(Application app)
        {
            var list = new List<string>();
            string? root = null;
            try { root = app.FamilyTemplatePath; }
            catch (Exception) { }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;
            list.AddRange(Files(root!));
            var parent = Directory.GetParent(root!.TrimEnd('\\', '/'));
            if (parent != null)
                foreach (var dir in SafeDirs(parent.FullName).Where(d => !string.Equals(d.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    list.AddRange(Files(dir));
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// The template for a category display name ("Furniture", "Plumbing Fixtures"): a file named after the category
        /// or its singular, metric ("Metric ...") first for metric jobs. Null when there is none.
        /// </summary>
        public static string? ForCategory(List<string> all, string categoryName, bool metric)
        {
            var names = new List<string> { categoryName };
            if (categoryName.EndsWith("s", StringComparison.OrdinalIgnoreCase)) names.Add(categoryName.Substring(0, categoryName.Length - 1));
            return Pick(all, names, metric);
        }

        /// <summary>A plain Generic Model template, metric first for metric jobs.</summary>
        public static string? GenericModel(List<string> all, bool metric) => Pick(all, new List<string> { "Generic Model" }, metric);

        private static string? Pick(List<string> all, List<string> names, bool metric)
        {
            var hits = all.Where(f => !IsHosted(f) && names.Any(n => Matches(f, n))).ToList();
            if (hits.Count == 0) return null;
            var preferred = hits.Where(f => IsMetric(f) == metric).ToList();
            return (preferred.Count > 0 ? preferred : hits)[0];
        }

        private static bool Matches(string file, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(file).Trim();
            if (stem.StartsWith("Metric ", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(7).Trim();
            return string.Equals(stem, name, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMetric(string file) => Path.GetFileName(file).StartsWith("Metric ", StringComparison.OrdinalIgnoreCase);

        private static bool IsHosted(string file)
        {
            string stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            return Hosted.Any(h => stem.Contains(h));
        }

        private static IEnumerable<string> Files(string dir)
        {
            try { return Directory.GetFiles(dir, "*.rft", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { return Enumerable.Empty<string>(); }
        }

        /// <summary>The templates found, for an error message.</summary>
        public static string Listing(List<string> all, int max = 25) =>
            all.Count == 0 ? "no templates were found under Revit's family template folder"
                : "templates found: " + string.Join(", ", all.Take(max).Select(Path.GetFileName)) + (all.Count > max ? " (and " + (all.Count - max) + " more)" : "");
    }
}
