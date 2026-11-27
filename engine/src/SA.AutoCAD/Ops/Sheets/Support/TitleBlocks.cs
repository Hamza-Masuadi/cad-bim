using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Geometry;
using SA.Core.Standards;

namespace SA.AutoCAD.Ops.Sheets.Support
{
    /// <summary>A block reference with attributes on a layout, as a title block candidate.</summary>
    internal sealed class TitleBlockRef
    {
        public ObjectId Id;
        public string Name = "";
        public string Layer = "";
        /// <summary>Tag (upper case) to attribute reference id.</summary>
        public Dictionary<string, ObjectId> Attributes = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finding title block references and mapping job keys to attribute tags through the standard's title block fields.
    /// A key may be the tag itself ("SHEET_NO") or a field's source ("sheet.number"); both resolve to the tag.
    /// </summary>
    internal static class TitleBlocks
    {
        internal static List<TitleBlockRef> ReferencesOn(Transaction tr, OpContext ctx, Layout layout)
        {
            var list = new List<TitleBlockRef>();
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                if (id.IsErased || id.ObjectClass.DxfName != "INSERT") continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br)) continue;
                if (br is MInsertBlock) continue;
                var t = new TitleBlockRef { Id = id, Name = SheetDb.EffectiveName(tr, br), Layer = br.Layer };
                var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (def.IsFromExternalReference) continue;
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    if (aid.IsErased) continue;
                    var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead);
                    if (!t.Attributes.ContainsKey(ar.Tag)) t.Attributes.Add(ar.Tag, aid);
                }
                if (t.Attributes.Count > 0) list.Add(t);
            }
            return list;
        }

        /// <summary>
        /// The title block on a layout: the reference of <paramref name="blockName"/> when given; otherwise the attributed
        /// reference sharing most tags with the standard's title block fields (or, without a standard, the one with most
        /// attributes). Null with a reason when there is none or it is ambiguous.
        /// </summary>
        internal static TitleBlockRef? Find(Transaction tr, OpContext ctx, Layout layout, string? blockName, out string reason)
        {
            reason = "";
            var refs = ReferencesOn(tr, ctx, layout);
            if (blockName != null)
            {
                var named = refs.Where(r => string.Equals(r.Name, blockName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 1) return named[0];
                reason = named.Count == 0 ? "no attributed reference of block '" + blockName + "'" : named.Count + " references of block '" + blockName + "'";
                return null;
            }
            if (refs.Count == 0) { reason = "no attributed block reference"; return null; }
            string? standardName = ctx.Standard?.TitleBlockName;
            if (standardName != null)
            {
                var named = refs.Where(r => string.Equals(r.Name, standardName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count == 1) return named[0];
            }
            var fields = ctx.Standard?.TitleBlockFields ?? new List<TitleBlockField>();
            Func<TitleBlockRef, int> score = fields.Count > 0
                ? (Func<TitleBlockRef, int>)(r => fields.Count(f => r.Attributes.ContainsKey(f.Name)))
                : r => r.Attributes.Count;
            var best = refs.OrderByDescending(score).ToList();
            int top = score(best[0]);
            if (fields.Count > 0 && top == 0) { reason = "no attributed reference carries any of the standard's title block tags"; return null; }
            if (best.Count > 1 && score(best[1]) == top)
            {
                reason = "several attributed references could be the title block (" + string.Join(", ", best.Where(r => score(r) == top).Select(r => r.Name)) + "); give block";
                return null;
            }
            return best[0];
        }

        /// <summary>The tag a job key names: a tag, or a standard field's source such as "sheet.number".</summary>
        internal static string TagFor(ProjectStandard? std, string key)
        {
            if (std != null)
            {
                var bySource = std.TitleBlockFields.FirstOrDefault(f => string.Equals(f.Source, key, StringComparison.OrdinalIgnoreCase));
                if (bySource != null) return bySource.Name;
            }
            return key;
        }

        /// <summary>The value of the attribute that holds a source ("sheet.number"), when the standard maps one and the block has it.</summary>
        internal static string? ValueOfSource(Transaction tr, ProjectStandard? std, TitleBlockRef? tb, string source)
        {
            if (std is null || tb is null) return null;
            var f = std.TitleBlockFields.FirstOrDefault(x => string.Equals(x.Source, source, StringComparison.OrdinalIgnoreCase));
            if (f is null || !tb.Attributes.TryGetValue(f.Name, out var id)) return null;
            string v = ((AttributeReference)tr.GetObject(id, OpenMode.ForRead)).TextString;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>The geometric extents of a reference in job units, when AutoCAD can compute them.</summary>
        internal static Rect2? Extents(OpContext ctx, TitleBlockRef tb)
        {
            try
            {
                var tr = ctx.IsValidating ? null : ctx.Transaction;
                Entity e;
                if (tr != null) e = (Entity)tr.GetObject(tb.Id, OpenMode.ForRead);
                else
                {
                    // Called from inside Db.Read during validation; the id's open-close transaction is the caller's.
                    using (var t = ctx.Database.TransactionManager.StartOpenCloseTransaction())
                    {
                        e = (Entity)t.GetObject(tb.Id, OpenMode.ForRead);
                        var ext0 = e.GeometricExtents;
                        t.Commit();
                        return Rect2.FromCorners(ctx.Units.ToJob(ext0.MinPoint), ctx.Units.ToJob(ext0.MaxPoint));
                    }
                }
                var ext = e.GeometricExtents;
                return Rect2.FromCorners(ctx.Units.ToJob(ext.MinPoint), ctx.Units.ToJob(ext.MaxPoint));
            }
            catch (Exception) { return null; }
        }
    }
}
