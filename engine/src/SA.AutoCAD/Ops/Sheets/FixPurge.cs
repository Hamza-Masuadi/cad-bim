using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// fix.purge: erases unreferenced named objects inside the job transaction. Each pass collects candidates of the
    /// requested kinds, asks Database.Purge(ObjectIdCollection) which of them have no hard references, and erases
    /// those; with repeat, passes continue until one erases nothing (an erased block can free a layer). At most 20
    /// passes.
    ///
    /// "all" means blocks, layers, linetypes, text styles, dimension styles, multiline styles, table styles, multileader
    /// styles and materials; as with the PURGE command, registered applications are purged only when "regapps" is listed.
    /// Visual styles are counted by the audit but never purged here: built-in styles that are not marked internal cannot
    /// be told from user styles, and erasing one removes it from every visual style list.
    ///
    /// Never purged: names in keep; every name the standard relies on (ProjectStandard.NamesToKeep: its purge.keep list,
    /// layers and their linetypes, text and dimension styles, role layers and title block name); layer 0, Defpoints, the current layer, linetype, text style, dimension style, multiline style,
    /// table style, multileader style and material; Continuous, ByLayer, ByBlock; the ByLayer, ByBlock and Global
    /// materials; "Standard" styles; the ACAD regapp; layouts, xrefs, anything
    /// xref-dependent; anonymous blocks (AutoCAD manages those).
    /// </summary>
    internal sealed class FixPurge : OpHandler<FixPurge.Plan>
    {
        public override string Name => "fix.purge";

        private static readonly string[] AllKinds = { "blocks", "layers", "linetypes", "textstyles", "dimstyles", "mlinestyles", "tablestyles", "mleaderstyles", "materials" };

        internal sealed class Plan
        {
            public List<string> Kinds = new List<string>();
            public HashSet<string> Keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<string>> Purged = new Dictionary<string, List<string>>();
            public Dictionary<string, int> Before = new Dictionary<string, int>();
            public Dictionary<string, List<ObjectId>> Erased = new Dictionary<string, List<ObjectId>>();
            public int Passes;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args);
            // Preview: what a first pass would find (later passes can find more).
            var preview = Db.Read(ctx, tr =>
            {
                var counts = new List<string>();
                foreach (var k in p.Kinds)
                {
                    var ids = Candidates(tr, ctx.Database, k, p.Keep);
                    var col = new ObjectIdCollection(ids.Select(x => x.Key).ToArray());
                    if (col.Count > 0) ctx.Database.Purge(col);
                    if (col.Count > 0) counts.Add(k + " " + col.Count);
                }
                return counts;
            });
            if (preview.Count > 0)
                OpContext.Warning(issues, args, null, "purge_preview", "a first pass would purge " + string.Join(", ", preview));
        }

        private static Plan Build(OpContext ctx, OpArgs args)
        {
            var p = new Plan();
            var kinds = args.GetStringList("kinds");
            foreach (var k in kinds)
            {
                if (k == "all") { foreach (var a in AllKinds) if (!p.Kinds.Contains(a)) p.Kinds.Add(a); }
                else if (!p.Kinds.Contains(k)) p.Kinds.Add(k);
            }
            foreach (var n in args.GetStringList("keep")) p.Keep.Add(n);
            if (ctx.Standard != null) p.Keep.UnionWith(ctx.Standard.NamesToKeep());
            foreach (var k in p.Kinds) { p.Purged[k] = new List<string>(); p.Erased[k] = new List<ObjectId>(); }
            return p;
        }

        /// <summary>Candidates of one kind (id and name), after every exclusion in the class remarks.</summary>
        private static List<KeyValuePair<ObjectId, string>> Candidates(Transaction tr, Database db, string kind, HashSet<string> keep)
        {
            var list = new List<KeyValuePair<ObjectId, string>>();
            void Table(ObjectId tableId, Func<SymbolTableRecord, bool> skip)
            {
                var t = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
                foreach (ObjectId id in t)
                {
                    if (id.IsErased) continue;
                    var r = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (r.IsDependent || keep.Contains(r.Name) || skip(r)) continue;
                    list.Add(new KeyValuePair<ObjectId, string>(id, r.Name));
                }
            }
            void Dict(ObjectId dictId, ObjectId current, params string[] fixedNames)
            {
                if (dictId.IsNull) return;
                var d = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in d)
                {
                    if (e.Value.IsErased || e.Value == current || keep.Contains(e.Key)) continue;
                    if (fixedNames.Any(n => string.Equals(e.Key, n, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new KeyValuePair<ObjectId, string>(e.Value, e.Key));
                }
            }
            switch (kind)
            {
                case "blocks":
                    Table(db.BlockTableId, r =>
                    {
                        var b = (BlockTableRecord)r;
                        return b.IsLayout || b.IsAnonymous || b.IsFromExternalReference;
                    });
                    break;
                case "layers":
                    Table(db.LayerTableId, r => SheetDb.IsProtectedLayer(r.Name) || r.ObjectId == db.Clayer);
                    break;
                case "linetypes":
                    Table(db.LinetypeTableId, r => r.ObjectId == db.Celtype || r.ObjectId == db.ContinuousLinetype || r.ObjectId == db.ByLayerLinetype || r.ObjectId == db.ByBlockLinetype);
                    break;
                case "textstyles":
                    Table(db.TextStyleTableId, r => r.ObjectId == db.Textstyle || string.Equals(r.Name, "Standard", StringComparison.OrdinalIgnoreCase) || ((TextStyleTableRecord)r).IsShapeFile);
                    break;
                case "dimstyles":
                    Table(db.DimStyleTableId, r => r.ObjectId == db.Dimstyle || string.Equals(r.Name, "Standard", StringComparison.OrdinalIgnoreCase));
                    break;
                case "regapps":
                    Table(db.RegAppTableId, r => string.Equals(r.Name, "ACAD", StringComparison.OrdinalIgnoreCase));
                    break;
                case "mlinestyles":
                    Dict(db.MLStyleDictionaryId, db.CmlstyleID, "Standard");
                    break;
                case "tablestyles":
                    Dict(db.TableStyleDictionaryId, db.Tablestyle, "Standard");
                    break;
                case "mleaderstyles":
                    Dict(db.MLeaderStyleDictionaryId, db.MLeaderstyle, "Standard");
                    break;
                case "materials":
                    Dict(db.MaterialDictionaryId, db.Cmaterial, Audit.Collectors.DocumentCollector.FixedMaterials);
                    break;
            }
            return list;
        }

        private static int CountKind(Transaction tr, Database db, string kind)
        {
            switch (kind)
            {
                case "blocks": return CountTable(tr, db.BlockTableId, r => !((BlockTableRecord)r).IsLayout);
                case "layers": return CountTable(tr, db.LayerTableId, r => true);
                case "linetypes": return CountTable(tr, db.LinetypeTableId, r => true);
                case "textstyles": return CountTable(tr, db.TextStyleTableId, r => true);
                case "dimstyles": return CountTable(tr, db.DimStyleTableId, r => true);
                case "regapps": return CountTable(tr, db.RegAppTableId, r => true);
                case "mlinestyles": return CountDict(tr, db.MLStyleDictionaryId);
                case "tablestyles": return CountDict(tr, db.TableStyleDictionaryId);
                case "mleaderstyles": return CountDict(tr, db.MLeaderStyleDictionaryId);
                case "materials": return CountDict(tr, db.MaterialDictionaryId);
                default: return 0;
            }
        }

        private static int CountTable(Transaction tr, ObjectId tableId, Func<SymbolTableRecord, bool> include)
        {
            int n = 0;
            var t = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            foreach (ObjectId id in t)
                if (!id.IsErased && include((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead))) n++;
            return n;
        }

        private static int CountDict(Transaction tr, ObjectId dictId)
        {
            if (dictId.IsNull) return 0;
            int n = 0;
            foreach (DBDictionaryEntry e in (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead)) if (!e.Value.IsErased) n++;
            return n;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var p = Build(ctx, args);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var k in p.Kinds) p.Before[k] = CountKind(tr, db, k);
            bool repeat = args.GetBool("repeat");
            for (p.Passes = 1; p.Passes <= 20; p.Passes++)
            {
                int erased = 0;
                foreach (var k in p.Kinds)
                {
                    var cands = Candidates(tr, db, k, p.Keep);
                    if (cands.Count == 0) continue;
                    var col = new ObjectIdCollection(cands.Select(x => x.Key).ToArray());
                    db.Purge(col);
                    foreach (ObjectId id in col)
                    {
                        var obj = tr.GetObject(id, OpenMode.ForWrite);
                        obj.Erase();
                        p.Purged[k].Add(cands.First(c => c.Key == id).Value);
                        p.Erased[k].Add(id);
                        result.Deleted.Add(OpContext.HandleOf(id));
                        erased++;
                    }
                }
                if (erased == 0 || !repeat) break;
            }
            var purged = new JsonObject();
            foreach (var k in p.Kinds) purged.Add(k, JsonArray.FromStrings(p.Purged[k]));
            result.Outputs.Add("purged", purged);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            bool ok = true;
            var before = new JsonObject();
            var after = new JsonObject();
            var counts = new JsonObject();
            foreach (var k in p.Kinds)
            {
                int now = CountKind(tr, db, k);
                before.Add(k, p.Before[k]);
                after.Add(k, now);
                counts.Add(k, p.Purged[k].Count);
                foreach (var id in p.Erased[k])
                    if (!id.IsErased) ok = Mismatch(result, k + ": an object purged in this op is not erased");
                if (now != p.Before[k] - p.Purged[k].Count) ok = Mismatch(result, k + ": " + now + " remain, expected " + (p.Before[k] - p.Purged[k].Count));
                foreach (var n in p.Purged[k])
                    if (p.Keep.Contains(n)) ok = Mismatch(result, k + ": '" + n + "' was purged although it is kept");
            }
            result.Readback.Add("before", before).Add("after", after).Add("counts", counts).Add("passes", Math.Min(p.Passes, 20))
                .Add("kept", JsonArray.FromStrings(p.Keep.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(200)));
            return ok;
        }
    }
}
