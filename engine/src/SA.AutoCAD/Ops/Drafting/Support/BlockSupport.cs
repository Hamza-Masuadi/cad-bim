using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>Extents of a block definition's contents relative to its base point, in drawing units.</summary>
    internal readonly struct BlockBox
    {
        public BlockBox(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    /// <summary>
    /// Block references and their attributes. New references get one AttributeReference per non-constant attribute
    /// definition, made with SetAttributeFromBlock(definition, BlockTransform) as INSERT does, appended to the
    /// reference's AttributeCollection and registered with the job transaction.
    /// </summary>
    internal static class BlockSupport
    {
        /// <summary>Tags of the block's attribute definitions in order (constant ones included), upper case.</summary>
        internal static List<string> Tags(Transaction tr, BlockTableRecord btr) =>
            AttDefs(tr, btr, includeConstant: true).Select(a => a.Tag.ToUpperInvariant()).ToList();

        internal static List<AttributeDefinition> AttDefs(Transaction tr, BlockTableRecord btr, bool includeConstant)
        {
            var list = new List<AttributeDefinition>();
            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition ad && (includeConstant || !ad.Constant)) list.Add(ad);
            }
            return list;
        }

        /// <summary>Tags of a block that exists or is planned by an earlier op; null when unknown.</summary>
        internal static List<string>? TagsOf(OpContext ctx, string blockName)
        {
            if (ctx.IsValidating && DraftState.For(ctx).Blocks.TryGetValue(blockName, out var planned)) return planned.Tags.Select(t => t.ToUpperInvariant()).ToList();
            return Db.Read(ctx, tr =>
            {
                var id = DraftDb.BlockId(tr, ctx.Database, blockName);
                if (id.IsNull) return null;
                return Tags(tr, (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead));
            });
        }

        /// <summary>Validates attribute tags given in a map field against a block's tags.</summary>
        internal static void CheckTags(OpContext ctx, OpArgs args, string field, string blockName, IEnumerable<string> tags, List<Issue> issues)
        {
            var known = TagsOf(ctx, blockName);
            if (known is null) return;
            var constant = Db.Read(ctx, tr =>
            {
                var id = DraftDb.BlockId(tr, ctx.Database, blockName);
                if (id.IsNull) return new List<string>();
                return AttDefs(tr, (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead), true).Where(a => a.Constant).Select(a => a.Tag.ToUpperInvariant()).ToList();
            });
            foreach (var t in tags)
            {
                string u = t.ToUpperInvariant();
                if (!known.Contains(u))
                    OpContext.Error(issues, args, field, "attribute_unknown", "block '" + blockName + "' has no attribute '" + t + "'." +
                        (known.Count == 0 ? " It has no attributes." : " Its tags are: " + string.Join(", ", known) + "."));
                else if (constant.Contains(u))
                    OpContext.Error(issues, args, field, "attribute_constant", "attribute '" + t + "' of block '" + blockName + "' is constant and cannot be set per reference");
            }
        }

        /// <summary>
        /// Creates the attribute references of a new, database-resident block reference. Values are by tag
        /// (case-insensitive); tags without a value take the definition's default. Returns tag to handle.
        /// </summary>
        internal static Dictionary<string, string> AddAttributes(OpContext ctx, BlockReference br, BlockTableRecord btr, IDictionary<string, string> values)
        {
            var tr = ctx.Transaction;
            var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool mirrored = br.BlockTransform.GetDeterminant() < 0;
            foreach (var ad in AttDefs(tr, btr, includeConstant: false))
            {
                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                // A mirrored insertion mirrors attribute text too; INSERT keeps it readable (MIRRTEXT 0), so undo the mirroring.
                if (mirrored) { ar.IsMirroredInX = false; ar.IsMirroredInY = false; }
                string? value = null;
                foreach (var kv in values)
                    if (string.Equals(kv.Key, ad.Tag, StringComparison.OrdinalIgnoreCase)) value = kv.Value;
                var id = br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
                SetValue(ctx, ar, value ?? ad.TextString);
                created[ad.Tag] = OpContext.HandleOf(id);
            }
            return created;
        }

        /// <summary>Sets the text of an attribute reference opened for write (single-line or multiline).</summary>
        internal static void SetValue(OpContext ctx, AttributeReference ar, string value)
        {
            if (ar.IsMTextAttribute)
            {
                using (var mt = ar.MTextAttribute)
                {
                    mt.Contents = value;
                    ar.MTextAttribute = mt;
                }
                ar.UpdateMTextAttribute();
                return;
            }
            ar.TextString = value;
            // Justified attributes keep their alignment point; Position is recomputed (needs the object in the database).
            if (ar.HorizontalMode != TextHorizontalMode.TextLeft || ar.VerticalMode != TextVerticalMode.TextBase) ar.AdjustAlignment(ctx.Database);
        }

        /// <summary>The value an attribute reference shows.</summary>
        internal static string ValueOf(AttributeReference ar)
        {
            if (!ar.IsMTextAttribute) return ar.TextString;
            using (var mt = ar.MTextAttribute) return mt.Contents;
        }

        /// <summary>Attribute values of a reference by upper-case tag, constant definitions included.</summary>
        internal static Dictionary<string, string> Read(Transaction tr, BlockReference br)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in br.AttributeCollection)
            {
                if (id.IsErased) continue;
                var ar = (AttributeReference)tr.GetObject(id, OpenMode.ForRead);
                d[ar.Tag.ToUpperInvariant()] = ValueOf(ar);
            }
            var defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            foreach (var ad in AttDefs(tr, btr, includeConstant: true))
                if (ad.Constant && !d.ContainsKey(ad.Tag.ToUpperInvariant())) d[ad.Tag.ToUpperInvariant()] = ad.TextString;
            if (defId != br.BlockTableRecord)
            {
                var dyn = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                foreach (var ad in AttDefs(tr, dyn, includeConstant: true))
                    if (ad.Constant && !d.ContainsKey(ad.Tag.ToUpperInvariant())) d[ad.Tag.ToUpperInvariant()] = ad.TextString;
            }
            return d;
        }

        /// <summary>The block name a user sees: the dynamic block's name for anonymous dynamic references.</summary>
        internal static string EffectiveName(Transaction tr, BlockReference br)
        {
            var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
        }

        /// <summary>Extents of the definition's visible geometry relative to its origin (attribute definitions excluded); null when empty.</summary>
        internal static BlockBox? Extents(Transaction tr, BlockTableRecord btr)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;
            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e is null || e is AttributeDefinition || !e.Visible) continue;
                Extents3d ext;
                try { ext = e.GeometricExtents; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; } // entities without extents (empty text) are skipped
                minX = Math.Min(minX, ext.MinPoint.X); minY = Math.Min(minY, ext.MinPoint.Y);
                maxX = Math.Max(maxX, ext.MaxPoint.X); maxY = Math.Max(maxY, ext.MaxPoint.Y);
                any = true;
            }
            if (!any) return null;
            var o = btr.Origin;
            return new BlockBox(minX - o.X, minY - o.Y, maxX - o.X, maxY - o.Y);
        }

        /// <summary>
        /// The factor INSERT applies for the block's insertion units: block unit length / drawing unit length. 1 when either
        /// is unitless or unknown.
        /// </summary>
        internal static double UnitFactor(Database db, BlockTableRecord btr)
        {
            var from = DrawingUnits.MillimetresPer((int)btr.Units);
            var to = DrawingUnits.MillimetresPer((int)db.Insunits);
            if (!from.HasValue || !to.HasValue) return 1;
            return from.Value / to.Value;
        }

        /// <summary>The map field (tag to string value) as a dictionary.</summary>
        internal static Dictionary<string, string> StringMap(OpArgs args, string field)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in args.GetMap(field))
                d[kv.Key] = kv.Value is JsonString s ? s.Value : kv.Value.ToString();
            return d;
        }

        /// <summary>Block references of a definition placed directly in a layout (model or paper space), dynamic anonymous copies included.</summary>
        internal static List<ObjectId> References(Transaction tr, BlockTableRecord btr, out int nested)
        {
            nested = 0;
            var result = new List<ObjectId>();
            var defs = new List<BlockTableRecord> { btr };
            if (btr.IsDynamicBlock)
                foreach (ObjectId a in btr.GetAnonymousBlockIds())
                    if (!a.IsErased) defs.Add((BlockTableRecord)tr.GetObject(a, OpenMode.ForRead));
            foreach (var d in defs)
                foreach (ObjectId id in d.GetBlockReferenceIds(true, false))
                {
                    if (id.IsErased) continue;
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br is null) continue;
                    if (DraftDb.IsInLayout(tr, br)) { if (!result.Contains(id)) result.Add(id); }
                    else nested++;
                }
            return result;
        }

        /// <summary>
        /// Inserts a reference in model space with its attributes. Annotative blocks get CANNOSCALE as their scale (set by
        /// the caller) and Annotative = True after append. Returns the reference (open for write).
        /// </summary>
        internal static BlockReference Insert(OpContext ctx, ObjectId btrId, Point3d position, double rotation, Scale3d scale, string layer, IDictionary<string, string> values)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var br = new BlockReference(position, btrId);
            br.SetDatabaseDefaults(db);
            br.Layer = layer;
            br.Rotation = rotation;
            br.ScaleFactors = scale;
            Db.Append(ctx, ms, br);
            if (Anno.IsAnnotative(btr)) br.Annotative = AnnotativeStates.True;
            AddAttributes(ctx, br, btr, values);
            return br;
        }
    }
}
