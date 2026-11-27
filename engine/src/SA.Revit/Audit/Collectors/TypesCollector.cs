using System;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Checks;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "types" (FACTS.md 2.5): one record per FamilySymbol (loadable and in-place), WallType, FloorType,
    /// CeilingType, RoofType and DimensionType (spot dimension types included), id = type id. One pass over
    /// WhereElementIsElementType with class tests, so derived classes are not missed.
    ///
    /// Fields: name, familyName, category (BuiltInCategory name; OST_Dimensions for every DimensionType), isInPlace,
    /// isLoadable, instanceCount (elements whose GetTypeId is this type), ifcExportAs (IFC_EXPORT_ELEMENT_TYPE_AS, else a
    /// type parameter named "IfcExportAs"; absent when blank), and for DimensionType only: dimensionStyle (StyleType
    /// lower case), stepMm and unitSystem.
    ///
    /// stepMm: FormatOptions from GetUnitsFormatOptions(), or the project's Length format when UseDefault;
    /// ConvertToInternalUnits(Accuracy, unit) in millimetres. Only written when that unit is a Length unit (angular and
    /// slope types have none), and then unitSystem is imperial for feet, inches, feet-and-fractional-inches and
    /// fractional inches, else metric.
    /// </summary>
    internal sealed class TypesCollector : IFactCollector
    {
        public string Table => "types";

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            var use = AuditCache.TypeUse(ctx);
            var types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .Where(t => t is FamilySymbol || t is WallType || t is FloorType || t is CeilingType || t is RoofType || t is DimensionType)
                .Cast<ElementType>().ToList();
            if (types.Count > AuditCache.MaxRecordsPerTable)
            {
                ctx.Truncated(Table, "records", AuditCache.MaxRecordsPerTable, types.Count, "record limit");
                types = types.Take(AuditCache.MaxRecordsPerTable).ToList();
            }
            ForgeTypeId? projectLengthUnit = null;
            double projectAccuracy = 0;
            try
            {
                var pf = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                projectLengthUnit = pf.GetUnitTypeId();
                projectAccuracy = pf.Accuracy;
            }
            catch (Exception) { }

            foreach (var t in types)
            {
                var r = table.Add(ElementIds.ToWire(t.Id));
                r.Set("name", t.Name ?? "");
                r.Set("familyName", t.FamilyName ?? "");
                var dim = t as DimensionType;
                string? cat = dim != null ? "OST_Dimensions" : AuditCache.CategoryName(t.Category);
                if (cat != null) r.Set("category", cat);
                bool inPlace = false;
                if (t is FamilySymbol fs)
                {
                    try { inPlace = fs.Family?.IsInPlace ?? false; } catch (Exception) { ctx.Omitted(Table, "isInPlace"); }
                }
                r.Set("isInPlace", inPlace);
                r.Set("isLoadable", t is FamilySymbol && !inPlace);
                r.Set("instanceCount", use.TryGetValue(ElementIds.ToLong(t.Id), out int n) ? n : 0);
                string? ifc = AuditCache.Text(t, BuiltInParameter.IFC_EXPORT_ELEMENT_TYPE_AS) ?? NamedText(t, "IfcExportAs");
                if (ifc != null) r.Set("ifcExportAs", ifc);
                if (dim is null) continue;

                try { r.Set("dimensionStyle", dim.StyleType.ToString().ToLowerInvariant()); }
                catch (Exception) { ctx.Omitted(Table, "dimensionStyle"); }
                try
                {
                    var fo = dim.GetUnitsFormatOptions();
                    ForgeTypeId? unit;
                    double accuracy;
                    if (fo.UseDefault) { unit = projectLengthUnit; accuracy = projectAccuracy; }
                    else { unit = fo.GetUnitTypeId(); accuracy = fo.Accuracy; }
                    if (unit != null && AuditCache.IsLengthUnit(unit))
                    {
                        r.Set("stepMm", AuditCache.Mm(UnitUtils.ConvertToInternalUnits(accuracy, unit)));
                        r.Set("unitSystem", AuditCache.IsImperialUnit(unit) ? "imperial" : "metric");
                    }
                }
                catch (Exception) { ctx.Omitted(Table, "stepMm"); }
            }
        }

        private static string? NamedText(Element e, string name)
        {
            try
            {
                var p = e.LookupParameter(name);
                if (p is null || !p.HasValue) return null;
                string? s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                s = s?.Trim();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch (Exception) { return null; }
        }
    }
}
