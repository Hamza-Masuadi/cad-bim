using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace SA.AutoCAD.Ops.Common
{
    /// <summary>
    /// Database helpers for handlers. Reads use the job transaction while executing, and a short open-close transaction
    /// while validating (so validation never opens the job transaction and never writes).
    /// The *Exists helpers count names planned by earlier ops only while validating; during execution those ops have
    /// run, so only the database is consulted.
    /// </summary>
    internal static class Db
    {
        /// <summary>Runs a read with the job transaction when executing, otherwise with a throwaway open-close transaction.</summary>
        internal static T Read<T>(OpContext ctx, Func<Transaction, T> read)
        {
            if (!ctx.IsValidating) return read(ctx.Transaction);
            using (var tr = ctx.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var value = read(tr);
                tr.Commit();
                return value;
            }
        }

        internal static ObjectId SymbolId(Transaction tr, ObjectId tableId, string name)
        {
            var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            if (!table.Has(name)) return ObjectId.Null;
            var id = table[name];
            return id.IsErased ? ObjectId.Null : id;
        }

        internal static bool LayerExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layer, name)) || Read(ctx, tr => !SymbolId(tr, ctx.Database.LayerTableId, name).IsNull);

        internal static bool TextStyleExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.TextStyle, name)) || Read(ctx, tr => !SymbolId(tr, ctx.Database.TextStyleTableId, name).IsNull);

        internal static bool LinetypeExists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Linetype, name)) || Read(ctx, tr => !SymbolId(tr, ctx.Database.LinetypeTableId, name).IsNull);

        /// <summary>The current layer's name (CLAYER of this database).</summary>
        internal static string CurrentLayerName(OpContext ctx) =>
            Read(ctx, tr => ((LayerTableRecord)tr.GetObject(ctx.Database.Clayer, OpenMode.ForRead)).Name);

        internal static BlockTableRecord ModelSpace(Transaction tr, Database db, OpenMode mode)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], mode);
        }

        /// <summary>
        /// Appends a new entity to <paramref name="owner"/> (opened for write) and registers it with the job transaction.
        /// The entity gets the database defaults first, so anything not set explicitly follows the drawing (ByLayer etc.).
        /// </summary>
        internal static ObjectId Append(OpContext ctx, BlockTableRecord owner, Entity entity)
        {
            if (!owner.IsWriteEnabled) owner.UpgradeOpen();
            var id = owner.AppendEntity(entity);
            ctx.Transaction.AddNewlyCreatedDBObject(entity, true);
            return id;
        }

        /// <summary>Ensures a registered application name exists for xdata (created inside the job transaction).</summary>
        internal static void EnsureRegApp(OpContext ctx, string name)
        {
            var tr = ctx.Transaction;
            var rat = (RegAppTable)tr.GetObject(ctx.Database.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(name)) return;
            rat.UpgradeOpen();
            var rec = new RegAppTableRecord { Name = name };
            rat.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        /// <summary>The engine's xdata application name.</summary>
        internal const string RegAppName = "CADBIM";

        /// <summary>A support file found through AutoCAD's search paths, or null.</summary>
        internal static string? FindSupportFile(Database db, string fileName)
        {
            try
            {
                string found = HostApplicationServices.Current.FindFile(fileName, db, FindFileHint.Default);
                return string.IsNullOrEmpty(found) ? null : found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>acadiso.lin / acadiso.pat for metric drawings (MEASUREMENT 1), acad.lin / acad.pat for imperial.</summary>
        internal static string DefaultResourceFile(Database db, string extension) =>
            (db.Measurement == MeasurementValue.Metric ? "acadiso" : "acad") + extension;
    }

    /// <summary>A simple linetype definition read from a .lin file.</summary>
    internal sealed class LinetypeDefinition
    {
        internal LinetypeDefinition(string name, string description, List<double> dashes, bool complex)
        {
            Name = name; Description = description; Dashes = dashes; IsComplex = complex;
        }

        internal string Name { get; }
        internal string Description { get; }
        /// <summary>Positive = dash, negative = space, 0 = dot, in linetype definition units.</summary>
        internal List<double> Dashes { get; }
        /// <summary>Contains shape or text elements ([...]); not created by the reference handlers.</summary>
        internal bool IsComplex { get; }
    }

    /// <summary>
    /// Reads .lin and .pat files. Used so that "ensure" ops can create linetypes inside the job transaction instead of
    /// calling Database.LoadLineTypeFile, whose changes are not known to be rolled back when a rehearsal aborts.
    /// </summary>
    internal static class ResourceFiles
    {
        internal static LinetypeDefinition? FindLinetype(string linFile, string name)
        {
            string[] lines;
            try { lines = File.ReadAllLines(linFile); }
            catch (Exception) { return null; }
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (!l.StartsWith("*", StringComparison.Ordinal)) continue;
                int comma = l.IndexOf(',');
                string n = (comma < 0 ? l.Substring(1) : l.Substring(1, comma - 1)).Trim();
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
                string desc = comma < 0 ? "" : l.Substring(comma + 1).Trim();
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string d = lines[j].Trim();
                    if (d.Length == 0 || d.StartsWith(";;", StringComparison.Ordinal)) continue;
                    if (!d.StartsWith("A,", StringComparison.OrdinalIgnoreCase)) return null;
                    bool complex = d.IndexOf('[') >= 0;
                    var dashes = new List<double>();
                    if (!complex)
                    {
                        foreach (var part in d.Substring(2).Split(','))
                        {
                            string p = part.Trim();
                            if (p.Length == 0) continue;
                            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
                            dashes.Add(v);
                        }
                    }
                    return new LinetypeDefinition(n, desc, dashes, complex);
                }
                return null;
            }
            return null;
        }

        internal static bool PatternExists(string patFile, string name)
        {
            try
            {
                foreach (var raw in File.ReadLines(patFile))
                {
                    string l = raw.Trim();
                    if (!l.StartsWith("*", StringComparison.Ordinal)) continue;
                    int comma = l.IndexOf(',');
                    string n = (comma < 0 ? l.Substring(1) : l.Substring(1, comma - 1)).Trim();
                    if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
