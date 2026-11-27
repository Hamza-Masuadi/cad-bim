using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Jobs;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets.Support
{
    /// <summary>One block table record to walk: model space, a layout's paper space, or a block definition.</summary>
    internal sealed class SpaceInfo
    {
        internal SpaceInfo(ObjectId id, string kind, string name)
        {
            Id = id; Kind = kind; Name = name;
        }

        internal ObjectId Id { get; }
        /// <summary>"model", "paper" or "block".</summary>
        internal string Kind { get; }
        /// <summary>The layout name for model and paper, the block name for blocks.</summary>
        internal string Name { get; }
        internal bool IsBlock => Kind == "block";
    }

    /// <summary>Database lookups shared by the sheet, plot, xref and fix handlers.</summary>
    internal static class SheetDb
    {
        internal const string Defpoints = "Defpoints";

        internal static bool IsProtectedLayer(string name) =>
            string.Equals(name, "0", StringComparison.Ordinal) || string.Equals(name, Defpoints, StringComparison.OrdinalIgnoreCase);

        /// <summary>The layout object id for a layout name (case-insensitive), or Null.</summary>
        internal static ObjectId LayoutId(Transaction tr, Database db, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsErased) continue;
                if (!(tr.GetObject(e.Value, OpenMode.ForRead) is Layout l)) continue;
                if (string.Equals(l.LayoutName, name, StringComparison.OrdinalIgnoreCase)) return e.Value;
            }
            return ObjectId.Null;
        }

        /// <summary>Paper space layouts in tab order.</summary>
        internal static List<Layout> PaperLayouts(Transaction tr, Database db)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            var list = new List<Layout>();
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsErased) continue;
                if (tr.GetObject(e.Value, OpenMode.ForRead) is Layout l && !l.ModelType) list.Add(l);
            }
            return list.OrderBy(l => l.TabOrder).ToList();
        }

        /// <summary>True when the layout exists, or (while validating) an earlier op in the job creates it.</summary>
        internal static bool LayoutExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layout, name)) || Db.Read(ctx, tr => !LayoutId(tr, ctx.Database, name).IsNull);

        internal static bool BlockExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Block, name)) || Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.BlockTableId, name).IsNull);

        /// <summary>Viewports of a layout's paper space block other than the overall paper space viewport (the first one).</summary>
        internal static List<ObjectId> ModelViewports(Transaction tr, Layout layout)
        {
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var list = new List<ObjectId>();
            bool first = true;
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || id.ObjectClass.DxfName != "VIEWPORT") continue;
                if (first) { first = false; continue; }
                list.Add(id);
            }
            return list;
        }

        internal static bool HasAnyViewport(Transaction tr, Layout layout)
        {
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (!id.IsErased && id.ObjectClass.DxfName == "VIEWPORT") return true;
            return false;
        }

        /// <summary>
        /// A layout that has never been activated has no overall paper space viewport; the first viewport appended to it
        /// would become that viewport. Layout.Initialize establishes it ("establish a Paper Space viewport for the
        /// layout", AcDbMgd reference). Called before any model viewport is added.
        /// </summary>
        internal static void EnsureInitialized(Transaction tr, Layout layout)
        {
            if (HasAnyViewport(tr, layout)) return;
            if (!layout.IsWriteEnabled) layout.UpgradeOpen();
            layout.Initialize();
        }

        /// <summary>
        /// Block table records to walk. Xref and xref-dependent blocks are never included. Anonymous blocks (dimension,
        /// table and dynamic-block representations, which AutoCAD regenerates) only when <paramref name="includeAnonymous"/>.
        /// </summary>
        internal static List<SpaceInfo> Spaces(Transaction tr, Database db, string space, bool includeBlocks, bool includeAnonymous)
        {
            var list = new List<SpaceInfo>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsFromExternalReference || btr.IsDependent) continue;
                if (btr.IsLayout)
                {
                    bool model = string.Equals(btr.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase);
                    if (model && (space == "model" || space == "all")) list.Add(new SpaceInfo(id, "model", "Model"));
                    if (!model && (space == "paper" || space == "all"))
                    {
                        string name = btr.Name;
                        try { name = ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName; } catch (Exception) { }
                        list.Add(new SpaceInfo(id, "paper", name));
                    }
                    continue;
                }
                if (!includeBlocks) continue;
                if (btr.IsAnonymous && !includeAnonymous) continue;
                list.Add(new SpaceInfo(id, "block", btr.Name));
            }
            return list;
        }

        /// <summary>
        /// Opens an entity for write. Entities on locked layers need forceOpenOnLockedLayer (the four-argument
        /// Transaction.GetObject); only ops that are explicitly about re-layering or units use it.
        /// </summary>
        internal static Entity OpenForWrite(Transaction tr, ObjectId id, bool forceOnLockedLayer) =>
            (Entity)tr.GetObject(id, OpenMode.ForWrite, false, forceOnLockedLayer);

        internal static bool IsOnLockedLayer(Transaction tr, Entity e)
        {
            var ltr = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
            return ltr.IsLocked;
        }

        /// <summary>The effective block name of a reference (the dynamic definition's name for anonymous representations).</summary>
        internal static string EffectiveName(Transaction tr, BlockReference br)
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }

        /// <summary>* and ? wildcards (AutoCAD style, case-insensitive) as a regular expression.</summary>
        internal static Regex Wildcard(string pattern)
        {
            var sb = new StringBuilder("^");
            foreach (char ch in pattern)
            {
                if (ch == '*') sb.Append(".*");
                else if (ch == '?') sb.Append('.');
                else sb.Append(Regex.Escape(ch.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal static bool HasWildcard(string s) => s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0;

        /// <summary>Folder of the saved drawing, or null for an unsaved one.</summary>
        internal static string? DrawingFolder(OpContext ctx)
        {
            if (!ctx.Document.IsNamedDrawing) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(ctx.Document.Name)); }
            catch (Exception) { return null; }
        }

        internal static string DrawingBaseName(OpContext ctx)
        {
            try { return Path.GetFileNameWithoutExtension(ctx.Document.Name ?? "drawing"); }
            catch (Exception) { return "drawing"; }
        }

        /// <summary>
        /// A path from <paramref name="fromDir"/> to <paramref name="toFile"/> in the form AutoCAD stores relative xref
        /// paths (".\x.dwg", "..\a\x.dwg"), or null when the two are on different roots (relative paths are not available
        /// across drives or servers).
        /// </summary>
        internal static string? RelativePath(string fromDir, string toFile)
        {
            string a = Path.GetFullPath(fromDir).TrimEnd('\\', '/');
            string b = Path.GetFullPath(toFile);
            string ra = Path.GetPathRoot(a) ?? "", rb = Path.GetPathRoot(b) ?? "";
            if (ra.Length == 0 || !string.Equals(ra.TrimEnd('\\', '/'), rb.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
            var sa = a.Substring(ra.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = b.Substring(rb.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            while (common < sa.Length && common < sb.Length - 1 && string.Equals(sa[common], sb[common], StringComparison.OrdinalIgnoreCase)) common++;
            var parts = new List<string>();
            for (int i = common; i < sa.Length; i++) parts.Add("..");
            if (parts.Count == 0) parts.Add(".");
            for (int i = common; i < sb.Length; i++) parts.Add(sb[i]);
            return string.Join("\\", parts);
        }

        /// <summary>A stored xref path made absolute against the host drawing's folder (null when it cannot be).</summary>
        internal static string? AbsoluteFromStored(string stored, string? hostFolder)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            try
            {
                if (Path.IsPathRooted(stored) && !stored.StartsWith(".", StringComparison.Ordinal)) return Path.GetFullPath(stored);
                if (hostFolder is null) return null;
                return Path.GetFullPath(Path.Combine(hostFolder, stored));
            }
            catch (Exception) { return null; }
        }

        internal static bool FileExists(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path); }
            catch (Exception) { return false; }
        }

        /// <summary>Equal paths after normalising (case-insensitive, separators).</summary>
        internal static bool SamePath(string? a, string? b)
        {
            if (a is null || b is null) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>
    /// Which standard layer to use for viewports, xrefs and title blocks: the standard's role layers (viewportLayer,
    /// xrefLayer, titleBlockLayer) when it sets them. Otherwise they are found from the standard's layer list: viewports
    /// go on a non-plotting layer whose description mentions viewports (else the first non-plotting layer); xrefs on a
    /// layer whose description mentions xrefs or external references; title blocks on a layer matching the standard's
    /// "titleBlockLayer" pattern whose description mentions the title block.
    /// </summary>
    internal static class StandardLayers
    {
        internal static string? Viewport(ProjectStandard? s)
        {
            if (s is null) return null;
            if (s.ViewportLayer != null) return s.ViewportLayer;
            var np = s.Layers.Where(l => !l.Plot).ToList();
            return (np.FirstOrDefault(l => Mentions(l, "viewport")) ?? np.FirstOrDefault())?.Name;
        }

        internal static string? Xref(ProjectStandard? s)
        {
            if (s is null) return null;
            if (s.XrefLayer != null) return s.XrefLayer;
            return s.Layers.FirstOrDefault(l => Mentions(l, "xref") || Mentions(l, "external reference"))?.Name;
        }

        internal static string? TitleBlock(ProjectStandard? s)
        {
            if (s is null) return null;
            if (s.TitleBlockLayer != null) return s.TitleBlockLayer;
            Regex? rx = s.Patterns.TryGetValue("titleBlockLayer", out var r) ? r : null;
            var candidates = s.Layers.Where(l => rx is null || rx.IsMatch(l.Name)).ToList();
            return (candidates.FirstOrDefault(l => Mentions(l, "title block")) ?? (rx != null ? candidates.FirstOrDefault() : null))?.Name;
        }

        private static bool Mentions(LayerSpec l, string word) =>
            (l.Description ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Resolves the layer for an object kind: the job's layer field, else the standard's layer for the kind, else the
        /// current layer (with a warning). The layer must exist or be planned; a missing one is an error (layer.ensure
        /// creates layers, not this op). Returns null when an error was added.
        /// </summary>
        internal static string? Resolve(OpContext ctx, OpArgs args, string field, string? fromStandard, string kind, List<Issue> issues)
        {
            string? given = args.GetStringOrNull(field);
            if (given != null)
            {
                if (ctx.IsRef(args, field)) return given;
                if (!Db.LayerExists(ctx, given))
                {
                    OpContext.Error(issues, args, field, IssueCodes.LayerMissing, "layer '" + given + "' is not in the drawing; create it with layer.ensure first");
                    return null;
                }
                return given;
            }
            if (fromStandard != null)
            {
                if (!Db.LayerExists(ctx, fromStandard))
                {
                    OpContext.Error(issues, args, field, IssueCodes.LayerMissing, "the standard puts " + kind + " on layer '" + fromStandard + "', which is not in the drawing; create it with layer.ensure first or give " + field);
                    return null;
                }
                return fromStandard;
            }
            string cur = Db.CurrentLayerName(ctx);
            if (ctx.IsValidating)
                OpContext.Warning(issues, args, field, "layer_defaulted", "no " + field + " given and the standard names none for " + kind + "; the current layer '" + cur + "' is used");
            return cur;
        }
    }
}
