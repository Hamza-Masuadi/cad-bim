using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SA.Core.Json;

namespace SA.Core.Standards
{
    /// <summary>
    /// <c>scaleRules</c>: how scales may be combined on a sheet. Optional; a standard without it sets no such rule, and
    /// the checks that read it report nothing.
    /// </summary>
    public sealed class ScaleRules
    {
        public ScaleRules(bool oneScalePerSheet, IEnumerable<string>? exemptViewTypes, Regex? exemptSheets, string? description)
        {
            OneScalePerSheet = oneScalePerSheet;
            if (exemptViewTypes != null) ExemptViewTypes.UnionWith(exemptViewTypes);
            ExemptSheets = exemptSheets;
            Description = description;
        }

        /// <summary>Every view or viewport on one sheet or layout must share one scale (<c>oneScalePerSheet</c>).</summary>
        public bool OneScalePerSheet { get; }

        /// <summary>
        /// View types left out of the comparison, compared case-insensitively with the facts' type field (Revit ViewType
        /// names such as "Legend" or "DraftingView"; AutoCAD has none).
        /// </summary>
        public HashSet<string> ExemptViewTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sheets (or layouts) whose number or name matches are left out (<c>exemptSheets</c>, a regular expression).</summary>
        public Regex? ExemptSheets { get; }

        public string? Description { get; }

        internal static ScaleRules FromJson(JsonCursor c)
        {
            c.RejectUnknownKeys("oneScalePerSheet", "exemptViewTypes", "exemptSheets", "description");
            var types = new List<string>();
            var t = c.Optional("exemptViewTypes");
            if (t != null)
                foreach (var i in t.Items())
                {
                    string s = i.AsString();
                    if (s.Trim().Length == 0) throw i.Fail("view types must not be empty");
                    types.Add(s);
                }
            var ex = c.Optional("exemptSheets");
            return new ScaleRules(c.GetBool("oneScalePerSheet", false), types, ex is null ? null : NamingUtil.Compile(ex), c.GetStringOrNull("description"));
        }
    }
}
