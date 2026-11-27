using System;
using Autodesk.Revit.DB;

namespace SA.Revit.Compat
{
    /// <summary>
    /// Parameter group and spec access.
    ///
    /// Release notes: Definition.ParameterGroup (BuiltInParameterGroup) was replaced by Definition.GetGroupTypeId()
    /// (ForgeTypeId, GroupTypeId) in 2024 and removed in 2025 (the 2024 reference lists both, 2025-2027 only the
    /// ForgeTypeId member). Definition.GetDataType() (SpecTypeId) replaced ParameterType in 2022, which was removed in
    /// 2023. Only the ForgeTypeId members are used here, so all four releases compile the same code.
    /// </summary>
    internal static class ParameterApi
    {
        /// <summary>The parameter's spec (data type), or an empty ForgeTypeId when it has none.</summary>
        public static ForgeTypeId Spec(Definition definition) => definition.GetDataType() ?? new ForgeTypeId();

        public static ForgeTypeId Group(Definition definition) => definition.GetGroupTypeId() ?? new ForgeTypeId();

        public static string GroupLabel(Definition definition)
        {
            var g = Group(definition);
            if (string.IsNullOrEmpty(g.TypeId)) return "";
            try { return LabelUtils.GetLabelForGroup(g); }
            catch (Exception) { return g.TypeId; }
        }

        public static string SpecLabel(ForgeTypeId spec)
        {
            if (string.IsNullOrEmpty(spec.TypeId)) return "";
            try { return LabelUtils.GetLabelForSpec(spec); }
            catch (Exception) { return spec.TypeId; }
        }

        /// <summary>ForgeTypeId has no == operator in any release; Equals ignores the version suffix of the id.</summary>
        public static bool SameSpec(ForgeTypeId? a, ForgeTypeId? b) => a != null && b != null && a.Equals(b);

        public static bool IsMeasurable(ForgeTypeId spec)
        {
            if (string.IsNullOrEmpty(spec.TypeId)) return false;
            try { return UnitUtils.IsMeasurableSpec(spec); }
            catch (Exception) { return false; }
        }
    }
}
