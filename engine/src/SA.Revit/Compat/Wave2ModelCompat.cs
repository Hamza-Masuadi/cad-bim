using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace SA.Revit.Compat
{
    /// <summary>
    /// Release differences for the wave-2 modelling, materials, links and purge ops.
    ///
    /// Material identity codes: 2024 and 2025 list BuiltInParameter.UNIFORMAT_CODE and OMNICLASS_CODE; 2026 and 2027 list
    /// ASSEMBLY_CODE and CLASSIFICATION_CODE instead (the older two are absent from those reference assemblies).
    /// </summary>
    internal static class Wave2ModelCompat
    {
        public static BuiltInParameter AssemblyCode =>
#if Y2026_OR_GREATER
            BuiltInParameter.ASSEMBLY_CODE;
#else
            BuiltInParameter.UNIFORMAT_CODE;
#endif

        public static BuiltInParameter ClassificationCode =>
#if Y2026_OR_GREATER
            BuiltInParameter.CLASSIFICATION_CODE;
#else
            BuiltInParameter.OMNICLASS_CODE;
#endif

        /// <summary>Document.GetUnusedElements: an empty category set means every category.</summary>
        public static ISet<ElementId> UnusedElements(Document doc, ISet<ElementId> categories) => doc.GetUnusedElements(categories);
    }
}
