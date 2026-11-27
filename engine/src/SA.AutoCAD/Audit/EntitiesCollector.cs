using SA.Core.Checks;

namespace SA.AutoCAD.Audit
{
    /// <summary>
    /// Table "entities" (data/checks/FACTS.md 1.3): one record per entity owned directly by model space or by a layout's
    /// paper space; id = handle. Left out: each layout's overall paper-space viewport, attributes, entities inside block
    /// definitions and inside xref databases (an insert is one record).
    /// Every record: type (DXF name), space, layout, layer, color, linetype, lineweight, isText, isPolyline, isFlat (2D
    /// types only). By type: TEXT/MTEXT textHeight, textScaleRatio, scaleSource, textStyle, isAnnotative; LINE x1, y1,
    /// x2, y2, offOrthoDeg; LWPOLYLINE and 2D POLYLINE isClosed, hasWidth; DIMENSION textOverride, dimStyle, dimLfac,
    /// measurement; HATCH isAssociative, patternName, patternScale; INSERT blockName, isXref; VIEWPORT isLocked,
    /// vpScaleRatio. Kept from the first version: colorByLayer, linetypeByLayer, lineweightByLayer,
    /// transparencyByLayer, z, zEnd, flat, block. Coordinates and heights are drawing units.
    /// The records are built by the shared <see cref="Collectors.DrawingScan"/>, so each entity is opened once per audit.
    /// </summary>
    internal sealed class EntitiesCollector : IFactCollector
    {
        public string Table => "entities";

        public void Collect(FactContext ctx, FactTable table)
        {
            var scan = ctx.Scan;
            table.Records.AddRange(scan.Entities);
            if (scan.EntityTotal > scan.Entities.Count) ctx.MarkTruncated(Table, scan.Entities.Count, scan.EntityTotal);
            if (scan.FieldErrors > 0) ctx.Facts.Meta.Set("entities.fieldErrors", scan.FieldErrors);
        }
    }
}
