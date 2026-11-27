using SA.Core.Checks;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// Table "layouts" (data/checks/FACTS.md 1.7): one record per paper-space layout (Model is left out), in tab order;
    /// id = layout handle. Fields: name, tabOrder, viewportCount (viewports in the layout block other than the overall
    /// paper viewport), paperSize (CanonicalMediaName), plotStyleTable (CurrentStyleSheet, absent when none).
    /// Layouts are read from the layout dictionary; the current layout is never changed.
    /// </summary>
    internal sealed class LayoutsCollector : IFactCollector
    {
        public string Table => "layouts";

        public void Collect(FactContext ctx, FactTable table)
        {
            foreach (var l in ctx.Scan.Layouts)
            {
                if (l.IsModel) continue;
                var r = table.Add(FactValues.Handle(l.LayoutId))
                    .Set("name", l.Name)
                    .Set("tabOrder", l.TabOrder)
                    .Set("viewportCount", l.ViewportCount);
                if (l.PaperSize != null) r.Set("paperSize", l.PaperSize);
                if (l.StyleSheet != null) r.Set("plotStyleTable", l.StyleSheet);
            }
        }
    }
}
