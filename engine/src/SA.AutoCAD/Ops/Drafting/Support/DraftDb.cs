using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Jobs;
using SA.Core.Protocol;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>
    /// Look-ups shared by the drafting handlers: symbol tables and dictionaries by name, close-match suggestions for
    /// helpful errors, layer checks with the professional defaults (nothing on Defpoints, nothing new on layer 0), and
    /// the job's output folder. Reads go through <see cref="Db.Read"/>, so they are safe while validating.
    /// </summary>
    internal static class DraftDb
    {
        internal static List<string> SymbolNames(Transaction tr, ObjectId tableId)
        {
            var names = new List<string>();
            var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                if (id.IsErased) continue;
                names.Add(((SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name);
            }
            return names;
        }

        internal static List<string> DictionaryNames(Transaction tr, ObjectId dictId)
        {
            var names = new List<string>();
            if (dictId.IsNull) return names;
            var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict) names.Add(e.Key);
            return names;
        }

        /// <summary>A dictionary entry by name (case-insensitive), or Null.</summary>
        internal static ObjectId DictionaryEntry(Transaction tr, ObjectId dictId, string name)
        {
            if (dictId.IsNull) return ObjectId.Null;
            var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in dict)
                if (string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase) && !e.Value.IsErased) return e.Value;
            return ObjectId.Null;
        }

        /// <summary>" Did you mean 'A' or 'B'?" or " Available: a, b, c." (at most 12), for error messages.</summary>
        internal static string Suggest(IEnumerable<string> names, string wanted)
        {
            var all = names.Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("*", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0) return " The drawing has none.";
            string w = wanted.ToUpperInvariant();
            var close = all
                .Select(n => new { n, d = Distance(n.ToUpperInvariant(), w) })
                .Where(x => x.d <= Math.Max(2, w.Length / 3) || x.n.ToUpperInvariant().Contains(w) || (w.Length >= 3 && w.Contains(x.n.ToUpperInvariant())))
                .OrderBy(x => x.d).Take(5).Select(x => "'" + x.n + "'").ToList();
            if (close.Count > 0) return " Did you mean " + string.Join(" or ", close) + "?";
            var some = all.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            return " Available: " + string.Join(", ", some) + (all.Count > some.Count ? ", ... (" + all.Count + " in all)" : "") + ".";
        }

        private static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// The layer an op draws on: the given one (which must exist or be planned), else the current layer. Refuses
        /// Defpoints (it never plots) and warns when objects would land on layer 0 (AC-001). Returns the name.
        /// </summary>
        internal static string Layer(OpContext ctx, OpArgs args, List<Issue> issues, string field = "layer", string what = "objects")
        {
            string name = args.GetStringOrNull(field) ?? Db.CurrentLayerName(ctx);
            if (args.Has(field) && ctx.IsRef(args, field)) return name;
            if (args.Has(field) && !Db.LayerExists(ctx, name))
            {
                string hint = Db.Read(ctx, tr => Suggest(SymbolNames(tr, ctx.Database.LayerTableId), name));
                OpContext.Error(issues, args, field, IssueCodes.LayerMissing, "layer '" + name + "' is not in the drawing; add it with layer.ensure first." + hint);
                return name;
            }
            if (string.Equals(name, "Defpoints", StringComparison.OrdinalIgnoreCase))
                OpContext.Error(issues, args, field, "layer_defpoints", "Defpoints never plots; put " + what + " on a standard layer (AC-002)");
            else if (name == "0")
                OpContext.Warning(issues, args, field, "layer_zero", (args.Has(field) ? "layer 0" : "the current layer is 0, so") + " holds " + what +
                    "; layer 0 is for block contents only (AC-001). Give a standard layer.");
            if (!ctx.IsValidating || !ctx.Plan.Contains(PlannedNames.Layer, name))
            {
                var state = Db.Read(ctx, tr =>
                {
                    var id = Db.SymbolId(tr, ctx.Database.LayerTableId, name);
                    if (id.IsNull) return "";
                    var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    return l.IsLocked ? "locked" : l.IsFrozen ? "frozen" : "";
                });
                if (state == "locked")
                    OpContext.Error(issues, args, field, "layer_locked", "layer '" + name + "' is locked; objects cannot be added to or changed on it");
                else if (state == "frozen")
                    OpContext.Warning(issues, args, field, IssueCodes.LayerFrozen, "layer '" + name + "' is frozen; what this op draws will not be visible");
            }
            return name;
        }

        /// <summary>True when the layer of an existing entity is locked (opening it for write would fail).</summary>
        internal static bool IsOnLockedLayer(Transaction tr, Entity e)
        {
            if (e.LayerId.IsNull) return false;
            return ((LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead)).IsLocked;
        }

        internal static ObjectId BlockId(Transaction tr, Database db, string name) => Db.SymbolId(tr, db.BlockTableId, name);

        internal static bool BlockExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Block, name)) || Db.Read(ctx, tr => !BlockId(tr, ctx.Database, name).IsNull);

        /// <summary>Error text for a missing block, with close matches.</summary>
        internal static string MissingBlock(OpContext ctx, string name) =>
            "block '" + name + "' is not defined in the drawing; define it with block.define first (the standard names no block library)." +
            Db.Read(ctx, tr => Suggest(SymbolNames(tr, ctx.Database.BlockTableId).Where(n => !n.StartsWith("*", StringComparison.Ordinal)), name));

        internal static bool DimStyleExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.DimStyle, name)) || Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.DimStyleTableId, name).IsNull);

        internal static string CurrentTextStyleName(OpContext ctx) =>
            Db.Read(ctx, tr => ((TextStyleTableRecord)tr.GetObject(ctx.Database.Textstyle, OpenMode.ForRead)).Name);

        /// <summary>Checks a text style name (given or current); returns the name.</summary>
        internal static string TextStyle(OpContext ctx, OpArgs args, List<Issue> issues, string field)
        {
            string name = args.GetStringOrNull(field) ?? CurrentTextStyleName(ctx);
            if (args.Has(field) && !ctx.IsRef(args, field) && !Db.TextStyleExists(ctx, name))
            {
                string hint = Db.Read(ctx, tr => Suggest(SymbolNames(tr, ctx.Database.TextStyleTableId), name));
                OpContext.Error(issues, args, field, IssueCodes.TextStyleMissing, "text style '" + name + "' is not in the drawing; add it with textstyle.ensure first." + hint);
            }
            return name;
        }

        /// <summary>The folder for files a job writes: the job's outputDir, else %LOCALAPPDATA%\CadBim\jobs\&lt;jobId&gt;.</summary>
        internal static string OutputDir(OpContext ctx) =>
            string.IsNullOrWhiteSpace(ctx.Job.OutputDir) ? SaPaths.JobOutput(ctx.Job.JobId) : ctx.Job.OutputDir!;

        /// <summary>A length value written into an attribute: whole millimetres, 3 decimals for metres, feet-inches for imperial.</summary>
        internal static string FormatLength(double value, LengthUnit unit)
        {
            switch (unit)
            {
                case LengthUnit.Millimetre: return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
                case LengthUnit.Centimetre: return Trim(Math.Round(value, 1).ToString("0.0", CultureInfo.InvariantCulture));
                case LengthUnit.Metre: return Math.Round(value, 3).ToString("0.000", CultureInfo.InvariantCulture);
                default: return Length.Format(value, unit, LengthStyle.Architectural, 2, 2);
            }
        }

        internal static string Trim(string s)
        {
            if (s.IndexOf('.') < 0) return s;
            s = s.TrimEnd('0');
            return s.EndsWith(".", StringComparison.Ordinal) ? s.Substring(0, s.Length - 1) : s;
        }

        /// <summary>True when a name is a plain file name (no folders, no "..").</summary>
        internal static bool IsPlainFileName(string name) =>
            name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name != "." && name != "..";

        /// <summary>Every layout's block table record (model space first), for "space: all" searches.</summary>
        internal static List<ObjectId> LayoutBlocks(Transaction tr, Database db, bool includePaper)
        {
            var list = new List<ObjectId>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            list.Add(bt[BlockTableRecord.ModelSpace]);
            if (!includePaper) return list;
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry e in layouts)
            {
                var layout = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                if (!list.Contains(layout.BlockTableRecordId)) list.Add(layout.BlockTableRecordId);
            }
            return list;
        }

        /// <summary>The block table record that owns an entity, when it is a layout (model or paper space).</summary>
        internal static bool IsInLayout(Transaction tr, Entity e)
        {
            if (e.OwnerId.IsNull) return false;
            return tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord owner && owner.IsLayout;
        }

        internal static string LayoutName(Transaction tr, ObjectId btrId)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if (!btr.IsLayout) return btr.Name;
            if (btr.LayoutId.IsNull) return btr.Name;
            return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
        }
    }
}
