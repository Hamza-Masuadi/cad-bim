using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "warnings" (FACTS.md 2.2): one record per persistent warning (Document.GetWarnings), no record id (a
    /// warning is not an element).
    /// Fields: description (GetDescriptionText, as Revit writes it), failureDefinitionId (GUID string, "D" format),
    /// severity (GetSeverity lower case: warning, error, documentcorruption), elementIds (string[]: GetFailingElements
    /// then GetAdditionalElements), elementCount (int, length of elementIds).
    /// </summary>
    internal sealed class WarningsCollector : IFactCollector
    {
        public string Table => "warnings";

        public void Collect(AuditContext ctx, FactTable table)
        {
            var all = AuditCache.Warnings(ctx);
            int n = 0;
            foreach (var w in all)
            {
                if (n++ >= AuditCache.MaxRecordsPerTable)
                {
                    ctx.Truncated(Table, "records", AuditCache.MaxRecordsPerTable, all.Count, "record limit");
                    break;
                }
                var ids = ElementIds.ToWire(w.GetFailingElements().Concat(w.GetAdditionalElements()));
                var r = table.Add();
                r.Set("description", w.GetDescriptionText() ?? "");
                r.Set("failureDefinitionId", w.GetFailureDefinitionId().Guid.ToString("D"));
                r.Set("severity", w.GetSeverity().ToString().ToLowerInvariant());
                r.Set("elementIds", JsonArray.FromStrings(ids));
                r.Set("elementCount", ids.Count);
            }
        }
    }
}
