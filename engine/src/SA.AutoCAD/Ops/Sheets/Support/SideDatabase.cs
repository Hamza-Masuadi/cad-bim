using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace SA.AutoCAD.Ops.Sheets.Support
{
    /// <summary>What was read from a template's layout.</summary>
    internal sealed class TemplateLayoutInfo
    {
        public string LayoutName = "";
        public SheetSettings Settings = new SheetSettings();
        public int PaperEntities;
        public List<string> PageSetups = new List<string>();
    }

    /// <summary>
    /// Reads other drawings without opening them in the editor: a side database (new Database(false, true), then
    /// ReadDwgFile with FileOpenMode.OpenForReadAndAllShare), read through its own transaction, disposed at once. Nothing
    /// is cloned into the job's drawing; values are copied field by field.
    /// </summary>
    internal static class SideDatabase
    {
        internal static T Read<T>(string path, Func<Database, Transaction, T> read)
        {
            using (var side = new Database(false, true))
            {
                side.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                using (var tr = side.TransactionManager.StartOpenCloseTransaction())
                {
                    var v = read(side, tr);
                    tr.Commit();
                    return v;
                }
            }
        }

        /// <summary>The template's named layout (or its first paper layout by tab order), or null with the reason.</summary>
        internal static TemplateLayoutInfo? TemplateLayout(string path, string? layoutName, out string reason)
        {
            string why = "";
            var info = Read(path, (db, tr) =>
            {
                var layouts = SheetDb.PaperLayouts(tr, db);
                var names = layouts.Select(l => l.LayoutName).ToList();
                Layout? l = layoutName is null ? layouts.FirstOrDefault()
                    : layouts.FirstOrDefault(x => string.Equals(x.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase));
                if (l is null)
                {
                    why = layoutName is null ? "the template has no paper space layout" : "the template has no layout '" + layoutName + "' (it has " + string.Join(", ", names) + ")";
                    return null;
                }
                var r = new TemplateLayoutInfo { LayoutName = l.LayoutName, Settings = SheetSettings.From(l) };
                var btr = (BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                    if (!id.IsErased && id.ObjectClass.DxfName != "VIEWPORT") r.PaperEntities++;
                r.PageSetups = PageSetupNames(tr, db);
                return r;
            });
            reason = why;
            return info;
        }

        /// <summary>A named page setup (paper space only) from a drawing's plot settings dictionary.</summary>
        internal static SheetSettings? PageSetup(Transaction tr, Database db, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsErased || !string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (tr.GetObject(e.Value, OpenMode.ForRead) is PlotSettings ps && !ps.ModelType) return SheetSettings.From(ps);
            }
            return null;
        }

        /// <summary>The PlotSettings object id of a named paper space page setup in the job's drawing, or Null.</summary>
        internal static ObjectId PageSetupId(Transaction tr, Database db, string name)
        {
            var dict = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
            {
                if (e.Value.IsErased || !string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (tr.GetObject(e.Value, OpenMode.ForRead) is PlotSettings ps && !ps.ModelType) return e.Value;
            }
            return ObjectId.Null;
        }

        internal static List<string> PageSetupNames(Transaction tr, Database db)
        {
            var list = new List<string>();
            var dict = (DBDictionary)tr.GetObject(db.PlotSettingsDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict) if (!e.Value.IsErased) list.Add(e.Key);
            return list;
        }

        internal static SheetSettings? TemplatePageSetup(string path, string name) =>
            Read(path, (db, tr) => PageSetup(tr, db, name));

        /// <summary>INSUNITS of another drawing.</summary>
        internal static int Insunits(string path) => Read(path, (db, tr) => (int)db.Insunits);
    }
}
