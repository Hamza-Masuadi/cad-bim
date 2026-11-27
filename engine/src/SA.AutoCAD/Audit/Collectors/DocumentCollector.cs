using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Checks;
using SA.Core.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Audit.Collectors
{
    /// <summary>
    /// Table "document" (data/checks/FACTS.md 1.1): exactly one record, no id. Fields: insUnits, measurement, ltScale,
    /// psLtScale, msLtScale, tileMode, currentLayout, imageFrame, hasRasterImages, purgeableCount, purgeableByKind.
    /// Read-only: Database.Purge only filters the id list it is given; nothing is erased.
    /// </summary>
    internal sealed class DocumentCollector : IFactCollector
    {
        public string Table => "document";

        public void Collect(FactContext ctx, FactTable table)
        {
            var db = ctx.Database;
            var tr = ctx.Transaction;
            var scan = ctx.Scan;
            var r = table.Add();
            r.Set("insUnits", FactValues.UnitWord(db.Insunits))
                .Set("measurement", (int)db.Measurement)
                .Set("ltScale", db.Ltscale)
                .Set("psLtScale", db.Psltscale ? 1 : 0)
                .Set("msLtScale", db.MsLtScale ? 1 : 0)
                .Set("tileMode", db.TileMode ? 1 : 0)
                .Set("currentLayout", CurrentLayout(db, tr))
                .Set("hasRasterImages", scan.HasRasterImages);
            var frame = ImageFrame(ctx);
            if (frame.HasValue) r.Set("imageFrame", frame.Value);
            var byKind = PurgeableByKind(db, tr, ctx.KeepNames);
            int total = 0;
            var kinds = new JsonObject();
            foreach (var kv in byKind) { kinds.Add(kv.Key, kv.Value); total += kv.Value; }
            r.Set("purgeableCount", total).Set("purgeableByKind", kinds);
        }

        /// <summary>
        /// "Model" while model space is the current tab, else the layout whose block is *Paper_Space (the active
        /// layout's block). Read from the database, so it never depends on which document is active.
        /// </summary>
        private static string CurrentLayout(Database db, Transaction tr)
        {
            if (db.TileMode) return "Model";
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(BlockTableRecord.PaperSpace)) return "";
            var ps = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.PaperSpace], OpenMode.ForRead);
            if (ps.LayoutId.IsNull) return "";
            return ((Layout)tr.GetObject(ps.LayoutId, OpenMode.ForRead)).LayoutName;
        }

        /// <summary>
        /// IMAGEFRAME. For the active drawing the system variable; for another document the RasterVariables object of the
        /// ACAD_IMAGE_VARS dictionary entry, 1 (the default) when the drawing has none.
        /// </summary>
        private static int? ImageFrame(FactContext ctx)
        {
            var active = AcApp.DocumentManager.MdiActiveDocument;
            if (active != null && ReferenceEquals(active, ctx.Document))
            {
                object v = AcApp.GetSystemVariable("IMAGEFRAME");
                return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            var nod = (DBDictionary)ctx.Transaction.GetObject(ctx.Database.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains("ACAD_IMAGE_VARS")) return 1;
            if (!(ctx.Transaction.GetObject(nod.GetAt("ACAD_IMAGE_VARS"), OpenMode.ForRead) is RasterVariables rv)) return null;
            // The frame setting enum also has a "below" value; IMAGEFRAME only distinguishes off, shown and plotted, shown not plotted.
            string name = rv.ImageFrame.ToString();
            if (name.IndexOf("NoPlot", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (name.IndexOf("Off", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            return 1;
        }

        private const string Blocks = "blocks", Layers = "layers", Linetypes = "linetypes", TextStyles = "textstyles",
            DimStyles = "dimstyles", MlineStyles = "mlinestyles", TableStyles = "tablestyles", MLeaderStyles = "mleaderstyles", Materials = "materials",
            VisualStyles = "visualstyles", RegApps = "regapps";

        /// <summary>
        /// One purge pass (FACTS.md purgeableCount): candidates are the named objects PURGE could offer, without
        /// xref-dependent or unresolved records and without the objects PURGE never removes; Database.Purge keeps the
        /// ones nothing references. Anonymous blocks and shape-file text styles are not candidates (PURGE lists neither).
        /// Multileader styles count except the current one and Standard; materials except the current one (CMATERIAL),
        /// ByLayer, ByBlock and Global; visual styles except those marked InternalUseOnly and those a viewport table record
        /// uses (the current model viewport's style among them). Owner decision 16; whether this matches PURGE's own list
        /// is an in-app test item.
        /// </summary>
        private static Dictionary<string, int> PurgeableByKind(Database db, Transaction tr, ISet<string>? keep)
        {
            var kindOf = new Dictionary<ObjectId, string>();
            var ids = new ObjectIdCollection();
            void Add(ObjectId id, string kind) { if (!id.IsNull && !kindOf.ContainsKey(id)) { kindOf[id] = kind; ids.Add(id); } }

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (b.IsLayout || b.IsFromExternalReference || b.IsFromOverlayReference || b.IsDependent || b.IsAnonymous) continue;
                Add(id, Blocks);
            }
            foreach (var rec in Records(tr, db.LayerTableId))
            {
                if (rec.Id == db.Clayer || rec.Name == "0" || string.Equals(rec.Name, "Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
                Add(rec.Id, Layers);
            }
            foreach (var rec in Records(tr, db.LinetypeTableId))
            {
                if (rec.Id == db.Celtype || Is(rec.Name, "ByLayer") || Is(rec.Name, "ByBlock") || Is(rec.Name, "Continuous")) continue;
                Add(rec.Id, Linetypes);
            }
            foreach (var rec in Records(tr, db.TextStyleTableId))
            {
                if (rec.Id == db.Textstyle || Is(rec.Name, "Standard") || ((TextStyleTableRecord)rec).IsShapeFile) continue;
                Add(rec.Id, TextStyles);
            }
            foreach (var rec in Records(tr, db.DimStyleTableId))
            {
                if (rec.Id == db.Dimstyle || Is(rec.Name, "Standard")) continue;
                Add(rec.Id, DimStyles);
            }
            foreach (var rec in Records(tr, db.RegAppTableId))
            {
                if (Is(rec.Name, "ACAD")) continue;
                Add(rec.Id, RegApps);
            }
            AddDictionary(tr, db.MLStyleDictionaryId, db.CmlstyleID, MlineStyles, Add);
            AddDictionary(tr, db.TableStyleDictionaryId, db.Tablestyle, TableStyles, Add);
            AddDictionary(tr, db.MLeaderStyleDictionaryId, db.MLeaderstyle, MLeaderStyles, Add);
            AddMaterials(tr, db, Add);
            AddVisualStyles(tr, db, Add);

            var result = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [Blocks] = 0, [Layers] = 0, [Linetypes] = 0, [TextStyles] = 0, [DimStyles] = 0, [MlineStyles] = 0, [TableStyles] = 0, [MLeaderStyles] = 0, [Materials] = 0, [VisualStyles] = 0, [RegApps] = 0,
            };
            if (ids.Count == 0) return result;
            db.Purge(ids);
            foreach (ObjectId id in ids)
                if (kindOf.TryGetValue(id, out var kind) && (keep is null || !keep.Contains(NameOf(tr, id)))) result[kind]++;
            return result;
        }

        /// <summary>A symbol table record's name, or the key a named-object dictionary stores the object under.</summary>
        private static string NameOf(Transaction tr, ObjectId id)
        {
            var o = tr.GetObject(id, OpenMode.ForRead);
            if (o is SymbolTableRecord r) return r.Name;
            return o.OwnerId.IsNull ? "" : tr.GetObject(o.OwnerId, OpenMode.ForRead) is DBDictionary d ? d.NameAt(id) : "";
        }

        private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Records of a symbol table that are not xref-dependent (records of unresolved xrefs are dependent too).</summary>
        private static IEnumerable<SymbolTableRecord> Records(Transaction tr, ObjectId tableId)
        {
            var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (id.IsErased) continue;
                var rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (rec.IsDependent) continue;
                yield return rec;
            }
        }

        /// <summary>Material names PURGE never removes.</summary>
        internal static readonly string[] FixedMaterials = { "ByLayer", "ByBlock", "Global" };

        private static void AddMaterials(Transaction tr, Database db, Action<ObjectId, string> add)
        {
            if (db.MaterialDictionaryId.IsNull) return;
            var dict = (DBDictionary)tr.GetObject(db.MaterialDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsNull || e.Value.IsErased || e.Value == db.Cmaterial || FixedMaterials.Any(n => Is(n, e.Key))) continue;
                add(e.Value, Materials);
            }
        }

        private static void AddVisualStyles(Transaction tr, Database db, Action<ObjectId, string> add)
        {
            if (db.VisualStyleDictionaryId.IsNull) return;
            var inUse = new HashSet<ObjectId>();
            var vports = (SymbolTable)tr.GetObject(db.ViewportTableId, OpenMode.ForRead);
            foreach (ObjectId id in vports)
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead) is AbstractViewTableRecord v && !v.VisualStyleId.IsNull) inUse.Add(v.VisualStyleId);
            var dict = (DBDictionary)tr.GetObject(db.VisualStyleDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsNull || e.Value.IsErased || inUse.Contains(e.Value)) continue;
                if (!(tr.GetObject(e.Value, OpenMode.ForRead) is DBVisualStyle vs) || vs.InternalUseOnly) continue;
                add(e.Value, VisualStyles);
            }
        }

        private static void AddDictionary(Transaction tr, ObjectId dictId, ObjectId current, string kind, Action<ObjectId, string> add)
        {
            if (dictId.IsNull) return;
            var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsNull || e.Value.IsErased || e.Value == current || Is(e.Key, "Standard")) continue;
                add(e.Value, kind);
            }
        }
    }
}
