using System.Collections.Generic;
using SA.Core.Families;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;

namespace SA.Core.Jobs.Ops
{
    /// <summary>Revit families made from the AutoCAD blocks that block.export wrote (placements.json and one DWG per block).</summary>
    public static class RevitOpsCadFamilies
    {
        private const string Body =
            "3D body: box (the block's extents rectangle extruded to height, with Width, Depth and Height parameters), none (plan symbol only), " +
            "or import3d (the block's DWG imported as 3D; only for blocks with 3D geometry).";

        private const string Symbol =
            "Plan symbol: import (the block's DWG imported into the family's plan view, view-specific) or lines (that import turned into " +
            "symbolic lines in the symbol subcategory, then removed; text and fills are lost).";

        public static IEnumerable<OpSchema> All()
        {
            yield return new OpSchema("family.fromcad", AppKind.Revit,
                "Make one loadable family per AutoCAD block from the folder block.export wrote, and load them into the project. " +
                "Each family has the block's DWG as its plan symbol at the block's base point (the family origin), in a symbol subcategory; " +
                "a 3D body (a box from the block's extents with Width, Depth and Height parameters that flex and a material parameter, hidden " +
                "in plan unless bodyInPlan; or none; or the DWG imported in 3D); and the type parameters \"" + CadFamilies.BlockParameter +
                "\" and \"" + CadFamilies.SourceParameter + "\" that family.place uses to find it. Every family is flex-tested, saved as .rfa " +
                "(published only on commit) and loaded. Must be alone in its job; a rehearsal builds, tests, saves to a temporary folder and " +
                "loads, then rolls everything back. A family name that already exists in the project is refused unless reload is true.",
                new[]
                {
                    F.String("file", "Full path of placements.json. The block DWGs must be in the same folder.").Req(),
                    C.StringList("blocks", "Block names (as in placements.json) to make families for. Default: every block in the file."),
                    F.String("familyPrefix", "Text put before each block name to make the family name, e.g. \"CAD-\". Otherwise the family name is the block name."),
                    F.String("category", "Family category: built-in name (OST_Furniture) or display name (Furniture). Model categories only.").Def("OST_Furniture"),
                    F.Length("height", "Height of the box body. Required for body box unless the block has 3D extents (their height is used then).").Positive(),
                    F.Enum("body", Body, CadFamilies.Bodies).Def("box"),
                    F.Enum("symbol", Symbol, CadFamilies.Symbols).Def("import"),
                    F.String("template", "Full path of a family template (.rft) for every family. Default: the category's template from Revit's family template folder, else a Generic Model template with the category set."),
                    F.String("typeName", "Name of each family's one type.").Def("Standard"),
                    F.Map("overrides", "Block name to settings for that block only; each field falls back to the one above.",
                        F.OfObject("",
                            F.String("familyName", "Family name for this block (instead of prefix + block name)."),
                            F.String("category", "Family category."),
                            F.Length("height", "Height of the box body.").Positive(),
                            F.Enum("body", "3D body.", CadFamilies.Bodies),
                            F.Enum("symbol", "Plan symbol.", CadFamilies.Symbols),
                            F.String("template", "Full path of a family template."),
                            F.String("typeName", "Type name."))),
                    F.String("subcategory", "Subcategory of the family category for the plan symbol.").Def("CAD Symbol"),
                    F.String("materialParameter", "Name of the box body's type material parameter.").Def("Body Material"),
                    F.Bool("bodyInPlan", "Show the box body in plan and ceiling plan views too. When false plan views show only the CAD symbol.").Def(false),
                    F.String("outputDir", "Folder for the .rfa files. Defaults to the 'families' folder in the job's output folder."),
                    C.Overwrite(),
                    F.Bool("reload", "Load over a family of the same name that is already in the project (its types take the new values). When false such a family is refused.").Def(false),
                },
                null,
                new[]
                {
                    F.Map("families", "Block name to the loaded family's id."),
                    F.Map("types", "Block name to the id of the family's type in the project."),
                    C.StringList("files", "Final paths of the .rfa files, in block order."),
                    F.List("flex", "Per family: { block, family, type, ok, cases: [ { name, expected, measured, ok } ] }.", F.Of(FieldType.Object)),
                },
                ensure: false, destructive: false, requiresAllowScript: false);
        }
    }
}
