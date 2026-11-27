using System;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "links" (FACTS.md 2.6): one record per RevitLinkInstance and per ImportInstance, id = instance id.
    ///
    /// Fields: kind (revit, cad), name (the link or import type's name), isLinked (Revit links true; CAD
    /// ImportInstance.IsLinked), isPinned, pathType (linked only: ExternalFileUtils.GetExternalFileReference(doc,
    /// typeId).PathType lower case: absolute, relative, server; "cloud" when the reference's path is a cloud path;
    /// absent for imports, and absent when the type is not an external file reference), isWorkshared, worksetName
    /// (absent when not workshared), isViewSpecific (ImportInstance.ViewSpecific; false for Revit links).
    /// </summary>
    internal sealed class LinksCollector : IFactCollector
    {
        public string Table => "links";

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            bool workshared = doc.IsWorkshared;
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var r = table.Add(ElementIds.ToWire(li.Id));
                r.Set("kind", "revit");
                var typeId = li.GetTypeId();
                r.Set("name", doc.GetElement(typeId)?.Name ?? li.Name ?? "");
                r.Set("isLinked", true);
                r.Set("isPinned", li.Pinned);
                var pt = PathType(ctx, typeId);
                if (pt != null) r.Set("pathType", pt);
                r.Set("isWorkshared", workshared);
                var ws = AuditCache.WorksetName(ctx, li);
                if (ws != null) r.Set("worksetName", ws);
                r.Set("isViewSpecific", false);
            }
            foreach (var ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var r = table.Add(ElementIds.ToWire(ii.Id));
                r.Set("kind", "cad");
                var typeId = ii.GetTypeId();
                r.Set("name", doc.GetElement(typeId)?.Name ?? ii.Name ?? "");
                bool linked = ii.IsLinked;
                r.Set("isLinked", linked);
                r.Set("isPinned", ii.Pinned);
                if (linked)
                {
                    var pt = PathType(ctx, typeId);
                    if (pt != null) r.Set("pathType", pt);
                }
                r.Set("isWorkshared", workshared);
                var ws = AuditCache.WorksetName(ctx, ii);
                if (ws != null) r.Set("worksetName", ws);
                r.Set("isViewSpecific", ii.ViewSpecific);
            }
        }

        private static string? PathType(AuditContext ctx, ElementId typeId)
        {
            try
            {
                if (!ElementIds.IsValid(typeId) || !ExternalFileUtils.IsExternalFileReference(ctx.Doc, typeId)) { ctx.Omitted("links", "pathType"); return null; }
                var ext = ExternalFileUtils.GetExternalFileReference(ctx.Doc, typeId);
                try { if (ext.GetPath()?.CloudPath == true) return "cloud"; } catch (Exception) { }
                return ext.PathType.ToString().ToLowerInvariant();
            }
            catch (Exception) { ctx.Omitted("links", "pathType"); return null; }
        }
    }
}
