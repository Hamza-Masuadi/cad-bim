using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Checks;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// Table "xrefs" (data/checks/FACTS.md 1.5): one record per node of the host's xref graph
    /// (<c>Database.GetHostDwgXrefGraph(true)</c>, ghost nodes included) except the host. id = handle of the first insert
    /// (lowest handle) in the host for depth-1 references, absent for nested ones.
    /// Fields: name, depth (shortest number of edges from the host), isOverlay, path, pathType, status,
    /// containsReferences, and for depth 1 insertLayer and insertCount.
    /// Nothing is loaded, reloaded or resolved: the graph describes the xrefs as they are.
    /// </summary>
    internal sealed class XrefsCollector : IFactCollector
    {
        public string Table => "xrefs";

        public void Collect(FactContext ctx, FactTable table)
        {
            var db = ctx.Database;
            var tr = ctx.Transaction;
            var graph = db.GetHostDwgXrefGraph(true);
            var host = graph.HostDrawing;
            string hostName = host?.Name ?? "";

            // Breadth-first from the host gives the shortest depth of every reachable node. Nodes are keyed by name
            // because every call to Out() may return a new wrapper for the same node.
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (host != null)
            {
                var queue = new Queue<(GraphNode Node, int Depth)>();
                queue.Enqueue((host, 0));
                depth[hostName] = 0;
                while (queue.Count > 0)
                {
                    var (node, d) = queue.Dequeue();
                    for (int i = 0; i < node.NumOut; i++)
                    {
                        if (!(node.Out(i) is XrefGraphNode child)) continue;
                        if (depth.ContainsKey(child.Name)) continue;
                        depth[child.Name] = d + 1;
                        queue.Enqueue((child, d + 1));
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < graph.NumNodes; i++)
            {
                var node = graph.GetXrefNode(i);
                if (node is null) continue;
                if (host != null && string.Equals(node.Name, hostName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(node.Name)) continue;

                int? nodeDepth = depth.TryGetValue(node.Name, out int dd) ? dd : node.IsNested ? (int?)null : 1;
                var status = node.XrefStatus;
                string? insertId = null;
                string? insertLayer = null;
                int insertCount = 0;
                bool? overlay = null;
                string? path = null;

                var btrId = node.BlockTableRecordId;
                if (!btrId.IsNull && !btrId.IsErased)
                {
                    if (btrId.Database == db)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        overlay = btr.IsFromOverlayReference;
                        path = btr.PathName ?? "";
                        if (nodeDepth == 1)
                        {
                            ObjectId first = ObjectId.Null;
                            foreach (ObjectId refId in btr.GetBlockReferenceIds(true, false))
                            {
                                if (refId.IsErased) continue;
                                insertCount++;
                                if (first.IsNull || refId.Handle.Value < first.Handle.Value) first = refId;
                            }
                            if (!first.IsNull)
                            {
                                insertId = FactValues.Handle(first);
                                insertLayer = ((Entity)tr.GetObject(first, OpenMode.ForRead)).Layer;
                            }
                        }
                    }
                    else if (btrId.Database != null)
                    {
                        // Nested reference: its block lives in the referencing xref's database; read it there.
                        using (var otr = btrId.Database.TransactionManager.StartOpenCloseTransaction())
                        {
                            if (otr.GetObject(btrId, OpenMode.ForRead) is BlockTableRecord nb)
                            {
                                overlay = nb.IsFromOverlayReference;
                                path = nb.PathName ?? "";
                            }
                            otr.Commit();
                        }
                    }
                }

                var r = table.Add(nodeDepth == 1 ? insertId : null).Set("name", node.Name);
                if (nodeDepth.HasValue) r.Set("depth", nodeDepth.Value);
                if (overlay.HasValue) r.Set("isOverlay", overlay.Value);
                if (path != null) r.Set("path", path).Set("pathType", FactValues.PathType(path));
                r.Set("status", FactValues.XrefStatusWord(status))
                    .Set("containsReferences", status == XrefStatus.Resolved && node.NumOut > 0);
                if (nodeDepth == 1)
                {
                    r.Set("insertCount", insertCount);
                    if (insertLayer != null) r.Set("insertLayer", insertLayer);
                }
            }
        }
    }
}
