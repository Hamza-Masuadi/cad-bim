using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Audit.Collectors;
using SA.Core.Checks;
using SA.Core.Json;

namespace SA.AutoCAD.Audit
{
    /// <summary>
    /// Table "jobPlacement" (data/checks/FACTS.md 1.9): in a job's verify step, one record per model-space entity the job
    /// created or modified; id = handle. Paper-space entities and entities no longer in the drawing are left out. In an
    /// audit there is no job: the table is empty and marked cut, so its checks report incomplete rather than pass.
    ///
    /// Fields: type, space ("model"), layout ("Model"), layer, shown, viewportsFull, viewportsPartial, viewportCount,
    /// layoutsShowing (string[]).
    /// shown: "noViewports" when the drawing has no floating viewport that is on (nothing to judge); "none" when the layer
    /// is off or frozen, or no viewport whose layer setting shows it holds any part of the entity's extents; "full" when
    /// one such viewport holds all of them; else "partial". Viewports with a non-rectangular clip are judged by their view
    /// rectangle. An entity whose extents cannot be read gets no shown field (counted in meta omittedFields).
    /// </summary>
    internal sealed class JobPlacementCollector : IFactCollector
    {
        public string Table => "jobPlacement";

        public void Collect(FactContext ctx, FactTable table)
        {
            if (ctx.JobScope is null)
            {
                ctx.Facts.MarkTruncated(Table, Facts.RecordsPart, 0, 0, "no job: these checks judge what a job created or modified");
                return;
            }
            var db = ctx.Database;
            var tr = ctx.Transaction;
            var modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            var windows = ViewportWindow.All(db, tr);
            var hidden = AnnotationsCollector.HiddenLayers(db, tr);
            foreach (string h in ctx.JobScope.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (table.Records.Count >= ctx.MaxRecords) { ctx.MarkTruncated(Table, table.Records.Count, ctx.JobScope.Count); break; }
                long value;
                try { value = Convert.ToInt64(h, 16); } catch (FormatException) { continue; } catch (OverflowException) { continue; }
                if (!db.TryGetObjectId(new Handle(value), out ObjectId id) || id.IsErased) continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || e.OwnerId != modelId) continue;
                var r = table.Add(h);
                string type = (id.ObjectClass.DxfName ?? "").ToUpperInvariant();
                r.Set("type", type.Length > 0 ? type : e.GetType().Name.ToUpperInvariant()).Set("space", "model").Set("layout", "Model").Set("layer", e.Layer);
                r.Set("viewportCount", windows.Count);
                if (windows.Count == 0) { r.Set("shown", "noViewports"); continue; }
                if (hidden.Contains(e.LayerId) || !e.Visible)
                {
                    r.Set("shown", "none").Set("viewportsFull", 0).Set("viewportsPartial", 0).Set("layoutsShowing", new JsonArray());
                    continue;
                }
                Extents3d ext;
                try { ext = e.GeometricExtents; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { ctx.Facts.MarkOmitted(Table, "shown"); continue; }
                int full = 0, partial = 0;
                var layouts = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var w in windows)
                {
                    if (!w.ShowsLayer(e.LayerId)) continue;
                    var p = w.Classify(ext);
                    if (p == Placement.None) continue;
                    if (p == Placement.Full) full++; else partial++;
                    layouts.Add(w.Layout);
                }
                r.Set("shown", full > 0 ? "full" : partial > 0 ? "partial" : "none")
                    .Set("viewportsFull", full).Set("viewportsPartial", partial).Set("layoutsShowing", JsonArray.FromStrings(layouts));
            }
        }
    }
}
