using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FamilyEditing
{
    /// <summary>
    /// Family parameters and family type values in a family document opened with Document.EditFamily.
    ///
    /// Names: a built-in parameter name (FamilyManager.get_Parameter(BuiltInParameter)), a shared parameter GUID
    /// (get_Parameter(Guid)) or a display name, which must match exactly one parameter of FamilyManager.GetParameters().
    /// Values follow the job convention of <see cref="RevitUnits"/> with the project's units (never the family's), so
    /// a value means the same in the family and in the project. Material parameters take the name of a material in
    /// the family document, because element ids in the family document are not the project's.
    /// </summary>
    internal static class FamilyDoc
    {
        public static FamilyParameter? Find(FamilyManager fm, string name, out string? error)
        {
            error = null;
            if (Enum.TryParse(name, false, out BuiltInParameter bip) && name.IndexOf('_') > 0 && !int.TryParse(name, out _))
            {
                var p = fm.get_Parameter(bip);
                if (p is null) error = "the family has no built-in parameter " + name;
                return p;
            }
            if (Guid.TryParse(name, out var guid))
            {
                var p = fm.get_Parameter(guid);
                if (p is null) error = "the family has no shared parameter " + name;
                return p;
            }
            var all = fm.GetParameters();
            var matches = all.Where(p => p.Definition != null && string.Equals(p.Definition.Name, name, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                error = matches.Count + " family parameters are named '" + name + "'; name it by built-in name or shared parameter GUID";
                return null;
            }
            error = "the family has no parameter '" + name + "'" + Lookup.Suggest(name, all.Where(p => p.Definition != null).Select(p => p.Definition.Name).Distinct().ToList());
            return null;
        }

        public static ForgeTypeId Spec(FamilyParameter p) => p.Definition is null ? new ForgeTypeId() : ParameterApi.Spec(p.Definition);

        public static bool IsMaterial(ForgeTypeId spec) => ParameterApi.SameSpec(spec, SpecTypeId.Reference.Material);

        public static FamilyType? Type(FamilyManager fm, string name)
        {
            foreach (FamilyType t in fm.Types)
                if (string.Equals(t.Name, name, StringComparison.Ordinal)) return t;
            return null;
        }

        public static List<string> TypeNames(FamilyManager fm)
        {
            var list = new List<string>();
            foreach (FamilyType t in fm.Types) list.Add(t.Name);
            return list;
        }

        public static Material? MaterialByName(Document doc, string name) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

        /// <summary>Checks and converts a requested value for a family parameter (see the class notes).</summary>
        public static bool TryPrepare(Document famDoc, FamilyParameter p, JsonValue value, RevitUnits units, out PreparedValue prepared, out string error)
        {
            var spec = Spec(p);
            prepared = new PreparedValue { Storage = p.StorageType, Requested = value, Spec = spec };
            error = "";
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (value is JsonBool || value.IsNull) { error = "expected a number"; return false; }
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
                    if (!IsMaterial(spec))
                    {
                        error = "only material parameters among element parameters can be set by family.edit (this one is " + (ParameterApi.SpecLabel(spec).Length > 0 ? ParameterApi.SpecLabel(spec) : "an element reference") + ")";
                        return false;
                    }
                    if (value.IsNull) { prepared.Id = ElementId.InvalidElementId; prepared.Text = ""; return true; }
                    if (!(value is JsonString ms) || ms.Value.Trim().Length == 0) { error = "expected the name of a material in the family"; return false; }
                    var m = MaterialByName(famDoc, ms.Value);
                    if (m is null)
                    {
                        var names = new FilteredElementCollector(famDoc).OfClass(typeof(Material)).Select(x => x.Name).ToList();
                        error = "the family has no material '" + ms.Value + "'" + Lookup.Suggest(ms.Value, names);
                        return false;
                    }
                    prepared.Id = m.Id;
                    prepared.Text = m.Name;
                    return true;
                default:
                    error = "this parameter stores no value";
                    return false;
            }
        }

        /// <summary>Sets the value in the current type. Revit's exceptions propagate; the caller turns them into issues.</summary>
        public static void Set(FamilyManager fm, FamilyParameter p, PreparedValue v)
        {
            switch (v.Storage)
            {
                case StorageType.Double: fm.Set(p, v.Double); break;
                case StorageType.Integer: fm.Set(p, v.Integer); break;
                case StorageType.String: fm.Set(p, v.Text ?? ""); break;
                case StorageType.ElementId: fm.Set(p, v.Id ?? ElementId.InvalidElementId); break;
            }
        }

        /// <summary>A family type's value in the job convention; materials by name.</summary>
        public static JsonValue Read(Document famDoc, FamilyType t, FamilyParameter p, RevitUnits units)
        {
            if (!t.HasValue(p)) return p.StorageType == StorageType.String ? (JsonValue)"" : JsonValue.Null;
            var spec = Spec(p);
            switch (p.StorageType)
            {
                case StorageType.Double:
                    var d = t.AsDouble(p);
                    return d.HasValue ? RevitUnits.Num(units.FromInternal(spec, d.Value)) : JsonValue.Null;
                case StorageType.Integer:
                    var i = t.AsInteger(p);
                    if (!i.HasValue) return JsonValue.Null;
                    if (ParameterApi.SameSpec(spec, SpecTypeId.Boolean.YesNo)) return i.Value != 0;
                    return i.Value;
                case StorageType.String:
                    return t.AsString(p) ?? "";
                case StorageType.ElementId:
                    var id = t.AsElementId(p);
                    if (id is null || id == ElementId.InvalidElementId) return JsonValue.Null;
                    return IsMaterial(spec) ? (JsonValue)(famDoc.GetElement(id)?.Name ?? ElementIds.ToWire(id)) : ElementIds.ToWire(id);
                default:
                    return JsonValue.Null;
            }
        }

        /// <summary>True when the family type holds the prepared value (doubles within the spec's tolerance).</summary>
        public static bool Matches(FamilyType t, FamilyParameter p, PreparedValue v, RevitUnits units)
        {
            if (!t.HasValue(p)) return v.Storage == StorageType.String ? (v.Text ?? "").Length == 0 : v.Storage == StorageType.ElementId && !ElementIds.IsValid(v.Id);
            switch (v.Storage)
            {
                case StorageType.Double:
                    var d = t.AsDouble(p);
                    return d.HasValue && Math.Abs(d.Value - v.Double) <= units.InternalTolerance(v.Spec, v.Double);
                case StorageType.Integer: return t.AsInteger(p) == v.Integer;
                case StorageType.String: return string.Equals(t.AsString(p) ?? "", v.Text ?? "", StringComparison.Ordinal);
                case StorageType.ElementId: return (t.AsElementId(p) ?? ElementId.InvalidElementId) == (v.Id ?? ElementId.InvalidElementId);
                default: return false;
            }
        }

        /// <summary>A project type's value in the job convention; materials by name, like <see cref="Read(Document, FamilyType, FamilyParameter, RevitUnits)"/>.</summary>
        public static JsonValue Read(Document doc, Parameter p, RevitUnits units)
        {
            if (p.StorageType == StorageType.ElementId && p.Definition != null && IsMaterial(ParameterApi.Spec(p.Definition)))
            {
                var id = p.AsElementId();
                if (id is null || id == ElementId.InvalidElementId) return JsonValue.Null;
                return doc.GetElement(id)?.Name ?? ElementIds.ToWire(id);
            }
            return ParamAccess.Read(p, units);
        }

        /// <summary>
        /// True when a project type parameter holds the value prepared against the family parameter. Materials are
        /// compared by name: the project's material and the family's are different elements.
        /// </summary>
        public static bool Matches(Document doc, Parameter p, PreparedValue v, RevitUnits units)
        {
            if (v.Storage == StorageType.ElementId)
            {
                var id = p.AsElementId();
                string actual = id is null || id == ElementId.InvalidElementId ? "" : doc.GetElement(id)?.Name ?? "";
                return string.Equals(actual, v.Text ?? "", StringComparison.Ordinal);
            }
            return ParamAccess.Matches(p, v, units);
        }

        /// <summary>Formula text with white space removed, for comparing a request with what Revit stores.</summary>
        public static string Canonical(string? formula) =>
            new string((formula ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

        public static ForgeTypeId SpecFor(string name)
        {
            switch (name)
            {
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle;
                case "slope": return SpecTypeId.Slope;
                case "number": return SpecTypeId.Number;
                case "integer": return SpecTypeId.Int.Integer;
                case "currency": return SpecTypeId.Currency;
                case "url": return SpecTypeId.String.Url;
                case "yesNo": return SpecTypeId.Boolean.YesNo;
                case "material": return SpecTypeId.Reference.Material;
                default: return SpecTypeId.String.Text;
            }
        }

        public static ForgeTypeId GroupFor(string name)
        {
            switch (name)
            {
                case "dimensions": return GroupTypeId.Geometry;
                case "identityData": return GroupTypeId.IdentityData;
                case "constraints": return GroupTypeId.Constraints;
                case "materials": return GroupTypeId.Materials;
                case "text": return GroupTypeId.Text;
                case "graphics": return GroupTypeId.Graphics;
                case "general": return GroupTypeId.General;
                case "construction": return GroupTypeId.Construction;
                case "visibility": return GroupTypeId.Visibility;
                case "phasing": return GroupTypeId.Phasing;
                default: return GroupTypeId.Data;
            }
        }

        /// <summary>The family's own Shared setting (BuiltInParameter.FAMILY_SHARED on the family document's owner family).</summary>
        public static bool IsShared(Document famDoc)
        {
            try { return famDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1; }
            catch (Exception) { return false; }
        }
    }
}
