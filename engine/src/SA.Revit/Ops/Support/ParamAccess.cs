using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Ops.Support
{
    /// <summary>A parameter value converted and checked for its storage type, ready to set.</summary>
    internal sealed class PreparedValue
    {
        public StorageType Storage;
        public double Double;
        public int Integer;
        public string? Text;
        public ElementId? Id;
        public ForgeTypeId Spec = new ForgeTypeId();
        public JsonValue Requested = JsonValue.Null;
    }

    /// <summary>
    /// Finding, converting, setting and reading parameters, safely for every storage type.
    ///
    /// Names: a BuiltInParameter enum name (ALL_MODEL_MARK), a shared parameter GUID, or the parameter's display name.
    /// A display name that matches more than one parameter on the element is refused as ambiguous.
    /// Values: doubles are converted by the parameter's spec (see <see cref="RevitUnits"/>); yes/no parameters take
    /// true/false (or 0/1); integers must be integral; text takes a string (an integer literal is accepted and
    /// written as text); element-id parameters take an id, a reference to an earlier op, or null for "none".
    /// </summary>
    internal static class ParamAccess
    {
        public static Parameter? Find(Element e, string name, out string? error)
        {
            error = null;
            if (Enum.TryParse(name, false, out BuiltInParameter bip) && name.IndexOf('_') > 0 && !int.TryParse(name, out _))
            {
                var p = e.get_Parameter(bip);
                if (p is null) error = "element " + ElementIds.ToWire(e.Id) + " has no built-in parameter " + name;
                return p;
            }
            if (Guid.TryParse(name, out var guid))
            {
                var p = e.get_Parameter(guid);
                if (p is null) error = "element " + ElementIds.ToWire(e.Id) + " has no shared parameter " + name;
                return p;
            }
            var list = e.GetParameters(name);
            if (list.Count == 1) return list[0];
            if (list.Count > 1)
            {
                error = list.Count + " parameters on element " + ElementIds.ToWire(e.Id) + " are named '" + name +
                        "'; name it by built-in name or shared parameter GUID";
                return null;
            }
            var names = new List<string>();
            foreach (Parameter p in e.Parameters) if (p.Definition != null) names.Add(p.Definition.Name);
            error = "element " + ElementIds.ToWire(e.Id) + " (" + (e.Category?.Name ?? e.GetType().Name) + ") has no parameter '" + name + "'" +
                    Lookup.Suggest(name, names.Distinct().ToList());
            return null;
        }

        public static bool TryPrepare(Document doc, Parameter p, JsonValue value, RevitUnits units, out PreparedValue prepared, out string error)
        {
            prepared = new PreparedValue { Storage = p.StorageType, Requested = value };
            error = "";
            var spec = p.Definition is null ? new ForgeTypeId() : ParameterApi.Spec(p.Definition);
            prepared.Spec = spec;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (value is JsonBool) { error = "expected a number"; return false; }
                    if (!units.TryToInternal(spec, value, out prepared.Double, out error)) return false;
                    if (double.IsNaN(prepared.Double) || double.IsInfinity(prepared.Double)) { error = "not a finite number"; return false; }
                    return true;
                case StorageType.Integer:
                    if (ParameterApi.SameSpec(spec, SpecTypeId.Boolean.YesNo))
                    {
                        if (value is JsonBool b) { prepared.Integer = b.Value ? 1 : 0; return true; }
                        if (value is JsonNumber yn && yn.TryGetInt64(out long ynv) && (ynv == 0 || ynv == 1)) { prepared.Integer = (int)ynv; return true; }
                        error = "expected true or false";
                        return false;
                    }
                    if (value is JsonNumber n && n.TryGetInt64(out long l) && l >= int.MinValue && l <= int.MaxValue) { prepared.Integer = (int)l; return true; }
                    error = "expected an integer";
                    return false;
                case StorageType.String:
                    if (value is JsonString s) { prepared.Text = s.Value; return true; }
                    if (value is JsonNumber sn && sn.IsIntegerLiteral) { prepared.Text = sn.Raw; return true; }
                    if (value.IsNull) { prepared.Text = ""; return true; }
                    error = "expected text";
                    return false;
                case StorageType.ElementId:
                    if (value.IsNull) { prepared.Id = ElementId.InvalidElementId; return true; }
                    var idText = Refs.ElementId(value);
                    if (idText != null && ElementIds.TryParse(idText, out var id))
                    {
                        if (id != ElementId.InvalidElementId && doc.GetElement(id) is null && ElementIds.ToBuiltInCategory(id) is null)
                        {
                            error = "element " + idText + " does not exist";
                            return false;
                        }
                        prepared.Id = id;
                        return true;
                    }
                    error = "expected an element id";
                    return false;
                default:
                    error = "this parameter stores no value";
                    return false;
            }
        }

        /// <summary>Sets the prepared value. Returns false when Revit refused it.</summary>
        public static bool Apply(Parameter p, PreparedValue v)
        {
            switch (v.Storage)
            {
                case StorageType.Double: return p.Set(v.Double);
                case StorageType.Integer: return p.Set(v.Integer);
                case StorageType.String: return p.Set(v.Text ?? "");
                case StorageType.ElementId: return p.Set(v.Id ?? ElementId.InvalidElementId);
                default: return false;
            }
        }

        /// <summary>Reads the value in the job's convention: doubles by spec, yes/no as bool, ids as strings.</summary>
        public static JsonValue Read(Parameter p, RevitUnits units)
        {
            if (!p.HasValue && p.StorageType != StorageType.String) return JsonValue.Null;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    return RevitUnits.Num(units.FromInternal(p.Definition is null ? new ForgeTypeId() : ParameterApi.Spec(p.Definition), p.AsDouble()));
                case StorageType.Integer:
                    if (p.Definition != null && ParameterApi.SameSpec(ParameterApi.Spec(p.Definition), SpecTypeId.Boolean.YesNo)) return p.AsInteger() != 0;
                    return p.AsInteger();
                case StorageType.String:
                    return p.AsString() ?? "";
                case StorageType.ElementId:
                    var id = p.AsElementId();
                    return id is null || id == ElementId.InvalidElementId ? (JsonValue)JsonValue.Null : ElementIds.ToWire(id);
                default:
                    return JsonValue.Null;
            }
        }

        /// <summary>True when the parameter now holds the prepared value (doubles within tolerance).</summary>
        public static bool Matches(Parameter p, PreparedValue v, RevitUnits units)
        {
            switch (v.Storage)
            {
                case StorageType.Double:
                    double actual = p.AsDouble();
                    return Math.Abs(actual - v.Double) <= units.InternalTolerance(v.Spec, v.Double);
                case StorageType.Integer: return p.AsInteger() == v.Integer;
                case StorageType.String: return string.Equals(p.AsString() ?? "", v.Text ?? "", StringComparison.Ordinal);
                case StorageType.ElementId: return (p.AsElementId() ?? ElementId.InvalidElementId) == (v.Id ?? ElementId.InvalidElementId);
                default: return false;
            }
        }

        public static string Describe(PreparedValue v) =>
            v.Storage == StorageType.Double ? v.Double.ToString("R", CultureInfo.InvariantCulture) + " (internal)" :
            v.Storage == StorageType.Integer ? v.Integer.ToString(CultureInfo.InvariantCulture) :
            v.Storage == StorageType.String ? "'" + v.Text + "'" :
            v.Id is null ? "none" : ElementIds.ToWire(v.Id);

        /// <summary>
        /// Before an element exists, whether a parameter name is plausible for it: a built-in name, a GUID, a
        /// parameter bound to the category, or a parameter of an existing sample element. Null means "not known".
        /// </summary>
        public static bool? KnownFor(Document doc, BuiltInCategory category, string name, Element? sample)
        {
            if (Enum.TryParse(name, false, out BuiltInParameter _) && name.IndexOf('_') > 0) return true;
            if (Guid.TryParse(name, out _)) return true;
            if (sample != null) return sample.GetParameters(name).Count > 0;
            try
            {
                var it = doc.ParameterBindings.ForwardIterator();
                while (it.MoveNext())
                {
                    if (it.Key is Definition d && d.Name == name && it.Current is ElementBinding b)
                    {
                        foreach (Category c in b.Categories)
                            if (c.BuiltInCategory == category) return true;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
