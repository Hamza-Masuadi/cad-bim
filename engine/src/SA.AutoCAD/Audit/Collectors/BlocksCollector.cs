using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Checks;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// Table "blocks" (data/checks/FACTS.md 1.4): one record per block definition except layout blocks; id = block table
    /// record handle. Fields: name, isXref, isAnonymous, isDynamic, entityCount, hardPropertyEntities,
    /// nonZeroLayerEntities, referenceCount, units; plus isXrefDependent (a block that came in with an xref, "XREF|NAME"),
    /// which the contract does not list. Entity counts come from the shared scan and are 0 for xrefs.
    /// </summary>
    internal sealed class BlocksCollector : IFactCollector
    {
        public string Table => "blocks";

        public void Collect(FactContext ctx, FactTable table)
        {
            var tr = ctx.Transaction;
            var scan = ctx.Scan;
            var bt = (BlockTable)tr.GetObject(ctx.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (b.IsLayout) continue;
                bool xref = b.IsFromExternalReference || b.IsFromOverlayReference;
                scan.Blocks.TryGetValue(id, out var stats);
                table.Add(FactValues.Handle(id))
                    .Set("name", b.Name)
                    .Set("isXref", xref)
                    .Set("isAnonymous", b.IsAnonymous)
                    .Set("isDynamic", b.IsDynamicBlock)
                    .Set("isXrefDependent", b.IsDependent)
                    .Set("entityCount", xref || stats is null ? 0 : stats.EntityCount)
                    .Set("hardPropertyEntities", xref || stats is null ? 0 : stats.HardPropertyEntities)
                    .Set("nonZeroLayerEntities", xref || stats is null ? 0 : stats.NonZeroLayerEntities)
                    .Set("referenceCount", b.GetBlockReferenceIds(true, false).Count)
                    .Set("units", FactValues.UnitWord(b.Units));
            }
        }
    }
}
