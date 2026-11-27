using System.Collections.Generic;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>AutoCAD block ops that hand blocks to Revit (reports/FAMILY-CREATE-CONTRACT.md section 1).</summary>
    public static class AutoCadOpsBlocks
    {
        public static IEnumerable<OpSchema> All()
        {
            yield return new OpSchema("block.export",
                AppKind.AutoCad,
                "Write each chosen block definition to its own DWG (origin at the block's base point, the drawing's INSUNITS) and a placements.json (format cadbim.placements/1) listing every reference with position, rotation, scale and layer, for family.fromcad and family.place in Revit. The drawing is not changed. Alone in its job; files are put in place only when the job commits.",
                new[]
                {
                    C.StringList("blocks", "Block names (effective names; * and ? wildcards allowed). A dynamic block gives one entry per representation actually referenced. A named block with no reference is exported with an empty references list."),
                    C.StringList("layers", "Only references on these layers (wildcards allowed), and only blocks that have one. With blocks, both must match."),
                    F.Bool("all", "Every named block with a reference in model space (and in layouts when excludeLayouts is false). Only true is accepted."),
                    F.Bool("excludeXrefs", "Xrefs and blocks that belong to an xref are never exported; false is refused.").Def(true),
                    F.Bool("excludeLayouts", "Only references in model space. False also lists references in paper space layouts, with the layout name as space.").Def(true),
                    F.Integer("expectCount", "When given, the op fails unless exactly this many blocks (DWG files) would be written.").Range(1, null),
                    C.OutputDir(),
                    C.Overwrite(),
                },
                new[] { K.AtLeastOne("blocks", "layers", "all"), K.AtMostOne("blocks", "all"), K.AtMostOne("layers", "all") },
                new[]
                {
                    F.Integer("count", "Blocks exported (DWG files)."),
                    F.Integer("references", "References listed in placements.json."),
                    F.List("files", "DWG file names in placements order.", C.Str()),
                    F.String("placements", "Full path of placements.json. A rehearsal writes everything to a temporary folder instead, never into outputDir."),
                    F.String("outputDir", "The folder the files go to."),
                    F.List("skipped", "{ name, handle, reason } per block (no handle) or reference left out: no visible geometry, a MINSERT array, a reference not parallel to the XY plane.", F.Of(FieldType.Object)),
                },
                false, false, false);
        }
    }
}
