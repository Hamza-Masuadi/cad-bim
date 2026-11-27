using Autodesk.Revit.DB;

namespace SA.Revit.Compat
{
    /// <summary>
    /// Release differences used by the export ops.
    ///
    /// IFCVersion: the 2027 API marks IFCVersion.IFC4 and IFCVersion.IFC4x3 (the coordination-view variants) as obsolete
    /// in its documentation and adds IFC4x3RV and IFC4x3DTV. 2024 to 2026 have IFC4x3 but not IFC4x3RV. IFC4RV
    /// (reference view) exists in all four releases, so "ifc4" maps to it everywhere; "ifc2x3" maps to IFC2x3CV2
    /// (coordination view 2.0) everywhere. Source: RevitAPI.xml shipped with each reference package (F:IFCVersion.*).
    /// </summary>
    internal static class OutputCompat
    {
        internal static IFCVersion IfcVersion(string schemaArg)
        {
            switch (schemaArg)
            {
                case "ifc2x3": return IFCVersion.IFC2x3CV2;
                case "ifc4x3":
#if Y2027_OR_GREATER
                    return IFCVersion.IFC4x3RV;
#else
                    return IFCVersion.IFC4x3;
#endif
                default: return IFCVersion.IFC4RV;
            }
        }

        /// <summary>The IFCVersion member name used for an IFC version argument, for the op's readback.</summary>
        internal static string IfcVersionName(string schemaArg) => IfcVersion(schemaArg).ToString();
    }
}
