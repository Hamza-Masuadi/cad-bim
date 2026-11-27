using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs.Support
{
    /// <summary>A rule or filter value converted to the parameter's storage: exactly one of the fields is used.</summary>
    internal sealed class TypedValue
    {
        public StorageType Storage;
        public string? Text;
        public int Int;
        public double Double;
        public double Epsilon;
        public ElementId? Id;

        public override string ToString()
        {
            switch (Storage)
            {
                case StorageType.String: return "'" + Text + "'";
                case StorageType.Integer: return Int.ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return Double.ToString("R", CultureInfo.InvariantCulture);
                case StorageType.ElementId: return Id is null ? "none" : ElementIds.ToWire(Id);
                default: return "(none)";
            }
        }
    }

    /// <summary>
    /// Parameter names and values for rule-based view filters and schedule filters.
    ///
    /// Names: a parameter is matched by its display label (LabelUtils.GetLabelFor(BuiltInParameter) for built-in
    /// parameters, ParameterElement.Name for project and shared parameters) or by its BuiltInParameter enum name;
    /// exact first, then case-insensitive when unique. Values are converted to the parameter's storage type: text as
    /// given; integers and yes/no (true/false accepted); doubles by the parameter's spec in the job's convention
    /// (lengths in job units, angles in degrees, others in the project's display unit, strings with units parsed);
    /// element ids from an id or from the exact name of a level, phase or material.
    /// </summary>
    internal static class FilterValues
    {
        public static string Label(Document doc, ElementId paramId)
        {
            var bip = ElementIds.ToBuiltInParameter(paramId);
            if (bip.HasValue)
            {
                try { return LabelUtils.GetLabelFor(bip.Value); } catch (Exception) { return bip.Value.ToString(); }
            }
            return (doc.GetElement(paramId) as ParameterElement)?.Name ?? ElementIds.ToWire(paramId);
        }

        /// <summary>Finds a parameter id among <paramref name="candidates"/> by label or BuiltInParameter name.</summary>
        public static ElementId? Find(Document doc, ICollection<ElementId> candidates, string name, out string? error)
        {
            error = null;
            var labelled = candidates.Select(id => new { id, label = Label(doc, id), bip = ElementIds.ToBuiltInParameter(id) }).ToList();
            var exact = labelled.Where(x => string.Equals(x.label, name, StringComparison.Ordinal) || (x.bip.HasValue && x.bip.Value.ToString() == name)).ToList();
            if (exact.Count == 1) return exact[0].id;
            if (exact.Count > 1)
            {
                error = exact.Count + " parameters are labelled '" + name + "' (" + string.Join(", ", exact.Take(5).Select(x => x.bip?.ToString() ?? ("id " + ElementIds.ToWire(x.id)))) + "); use the BuiltInParameter name";
                return null;
            }
            var loose = labelled.Where(x => string.Equals(x.label, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count == 1) return loose[0].id;
            error = "no parameter '" + name + "' can be used here" + Lookup.Suggest(name, labelled.Select(x => x.label).Distinct().ToList());
            return null;
        }

        /// <summary>Storage type and spec of a parameter id, using a sample element when the id alone does not tell.</summary>
        public static void Describe(Document doc, ElementId paramId, Element? sample, out StorageType storage, out ForgeTypeId? spec)
        {
            storage = StorageType.None;
            spec = null;
            Parameter? onSample = null;
            var bip = ElementIds.ToBuiltInParameter(paramId);
            if (sample != null)
            {
                if (bip.HasValue) onSample = sample.get_Parameter(bip.Value);
                else if (doc.GetElement(paramId) is ParameterElement pe0) onSample = sample.get_Parameter(pe0.GetDefinition());
            }
            if (onSample != null)
            {
                storage = onSample.StorageType;
                spec = ParameterApi.Spec(onSample.Definition);
                return;
            }
            if (bip.HasValue)
            {
                try { storage = doc.get_TypeOfStorage(bip.Value); } catch (Exception) { storage = StorageType.None; }
                return;
            }
            if (doc.GetElement(paramId) is ParameterElement pe)
            {
                spec = ParameterApi.Spec(pe.GetDefinition());
                storage = StorageFromSpec(spec);
            }
        }

        public static StorageType StorageFromSpec(ForgeTypeId? spec)
        {
            if (spec is null || string.IsNullOrEmpty(spec.TypeId)) return StorageType.None;
            if (ParameterApi.SameSpec(spec, SpecTypeId.String.Text) || ParameterApi.SameSpec(spec, SpecTypeId.String.MultilineText) || ParameterApi.SameSpec(spec, SpecTypeId.String.Url))
                return StorageType.String;
            if (ParameterApi.SameSpec(spec, SpecTypeId.Int.Integer) || ParameterApi.SameSpec(spec, SpecTypeId.Boolean.YesNo)) return StorageType.Integer;
            if (ParameterApi.IsMeasurable(spec)) return StorageType.Double;
            if (ParameterApi.SameSpec(spec, SpecTypeId.Reference.Material)) return StorageType.ElementId;
            return StorageType.None;
        }

        /// <summary>A sample element of the categories that has the parameter: instances first, then types.</summary>
        public static Element? Sample(Document doc, ICollection<ElementId> categories, ElementId paramId)
        {
            if (categories.Count == 0) return null;
            var bip = ElementIds.ToBuiltInParameter(paramId);
            var pe = bip.HasValue ? null : doc.GetElement(paramId) as ParameterElement;
            foreach (bool types in new[] { false, true })
            {
                var col = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories));
                col = types ? col.WhereElementIsElementType() : col.WhereElementIsNotElementType();
                int n = 0;
                foreach (var e in col)
                {
                    if (++n > 500) break;
                    var p = bip.HasValue ? e.get_Parameter(bip.Value) : pe != null ? e.get_Parameter(pe.GetDefinition()) : null;
                    if (p != null) return e;
                }
            }
            return null;
        }

        /// <summary>Converts a job value to the storage type. False with an error message when it cannot.</summary>
        public static bool Convert(Document doc, RevitUnits units, StorageType storage, ForgeTypeId? spec, JsonValue value, out TypedValue result, out string error)
        {
            result = new TypedValue { Storage = storage };
            error = "";
            switch (storage)
            {
                case StorageType.String:
                    if (value is JsonString s) { result.Text = s.Value; return true; }
                    if (value is JsonNumber n0) { result.Text = n0.Raw; return true; }
                    if (value is JsonBool b0) { result.Text = b0.Value ? "Yes" : "No"; return true; }
                    error = "expected text";
                    return false;
                case StorageType.Integer:
                    if (value is JsonBool b) { result.Int = b.Value ? 1 : 0; return true; }
                    if (value is JsonNumber n && n.TryGetInt64(out long l) && l >= int.MinValue && l <= int.MaxValue) { result.Int = (int)l; return true; }
                    if (value is JsonString si && int.TryParse(si.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv)) { result.Int = iv; return true; }
                    error = "expected a whole number" + (ParameterApi.SameSpec(spec, SpecTypeId.Boolean.YesNo) ? " or true/false" : "");
                    return false;
                case StorageType.Double:
                    if (spec is null || string.IsNullOrEmpty(spec.TypeId))
                    {
                        error = "the parameter's unit cannot be told (no element of these categories carries it yet)";
                        return false;
                    }
                    if (!units.TryToInternal(spec, value, out double d, out string err)) { error = err; return false; }
                    result.Double = d;
                    result.Epsilon = units.InternalTolerance(spec, d);
                    return true;
                case StorageType.ElementId:
                    if (value is JsonNumber nid && nid.TryGetInt64(out long lid)) { result.Id = ElementIds.FromLong(lid); return true; }
                    if (value is JsonString sid)
                    {
                        if (ElementIds.TryParse(sid.Value, out var parsed) && doc.GetElement(parsed) != null) { result.Id = parsed; return true; }
                        var named = new List<Element>();
                        named.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Level)).Where(e => e.Name == sid.Value));
                        named.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Phase)).Where(e => e.Name == sid.Value));
                        named.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Material)).Where(e => e.Name == sid.Value));
                        if (named.Count == 1) { result.Id = named[0].Id; return true; }
                        error = named.Count > 1 ? "'" + sid.Value + "' names " + named.Count + " elements (level, phase or material); use an id" : "no level, phase or material named '" + sid.Value + "'; use an element id";
                        return false;
                    }
                    error = "expected an element id or a level, phase or material name";
                    return false;
                default:
                    error = "the parameter's storage type cannot be told";
                    return false;
            }
        }
    }
}
