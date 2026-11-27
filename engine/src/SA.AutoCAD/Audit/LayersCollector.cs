using System;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Audit.Collectors;
using SA.AutoCAD.Ops.Common;
using SA.Core.Checks;
using SA.Core.Json;

namespace SA.AutoCAD.Audit
{
    /// <summary>
    /// Table "layers" (data/checks/FACTS.md 1.2): one record per layer table record, xref-dependent layers included;
    /// id = layer handle.
    /// Contract fields: name, colorKey ("7" or "r,g,b"), colorIndex (ACI only), linetype, lineweight (mm or "default"),
    /// plottable (Defpoints always false), isFrozen, isOff, isLocked, isXrefDependent, entityCount (entities on the layer
    /// in model space, paper space and every non-xref block definition, counted by the shared scan), isUsed, description
    /// (absent when empty).
    /// Kept from the first version: color (ACI number or "#RRGGBB"), isTrueColor, plot, frozen, off, locked,
    /// transparency (percent or null), isDependent, isCurrent, isZero, isDefpoints.
    /// </summary>
    internal sealed class LayersCollector : IFactCollector
    {
        public string Table => "layers";

        public void Collect(FactContext ctx, FactTable table)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var counts = ctx.Scan.LayerCounts;
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                if (id.IsErased) continue;
                var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                string linetype = "";
                if (!l.LinetypeObjectId.IsNull && tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord ltr) linetype = ltr.Name;
                var tp = AcadValues.TransparencyPercent(l.Transparency);
                bool defpoints = string.Equals(l.Name, "Defpoints", StringComparison.OrdinalIgnoreCase);
                int count = counts.TryGetValue(id, out int n) ? n : 0;
                string colorKey = FactValues.LayerColorKey(l.Color, out int? aci);
                var r = table.Add(FactValues.Handle(id))
                    .Set("name", l.Name)
                    .Set("colorKey", colorKey)
                    .Set("linetype", linetype)
                    .Set("lineweight", AcadValues.LineWeightJson(l.LineWeight))
                    .Set("plottable", l.IsPlottable && !defpoints)
                    .Set("isFrozen", l.IsFrozen)
                    .Set("isOff", l.IsOff)
                    .Set("isLocked", l.IsLocked)
                    .Set("isXrefDependent", l.IsDependent)
                    .Set("entityCount", count)
                    .Set("isUsed", count > 0);
                if (aci.HasValue) r.Set("colorIndex", aci.Value);
                if (!string.IsNullOrEmpty(l.Description)) r.Set("description", l.Description);
                r.Set("color", AcadValues.ColorJson(l.Color))
                    .Set("isTrueColor", l.Color.ColorMethod == Autodesk.AutoCAD.Colors.ColorMethod.ByColor)
                    .Set("plot", l.IsPlottable)
                    .Set("frozen", l.IsFrozen)
                    .Set("off", l.IsOff)
                    .Set("locked", l.IsLocked)
                    .Set("transparency", tp.HasValue ? (JsonValue)tp.Value : JsonValue.Null)
                    .Set("isDependent", l.IsDependent)
                    .Set("isCurrent", id == db.Clayer)
                    .Set("isZero", l.Name == "0")
                    .Set("isDefpoints", defpoints);
            }
        }
    }
}
