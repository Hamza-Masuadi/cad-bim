using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace SA.AutoCAD.Ops.Common
{
    /// <summary>
    /// Finding and creating linetypes for ops that reference them ("loaded if missing"). Only simple linetypes (dashes,
    /// spaces and dots) are created here, as LinetypeTableRecords inside the job transaction, so a rehearsal rolls them
    /// back. Complex linetypes (shapes or text) must already be in the drawing, or be loaded by linetype.ensure.
    /// </summary>
    internal static class LinetypeSupport
    {
        internal static bool IsBuiltIn(string name) =>
            string.Equals(name, "Continuous", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ByLayer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ByBlock", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Null when the linetype exists, is planned, or can be created from the default .lin file; otherwise the reason.
        /// <paramref name="definition"/> is set when it must be created.
        /// </summary>
        internal static string? Check(OpContext ctx, string name, out LinetypeDefinition? definition)
        {
            definition = null;
            if (IsBuiltIn(name) || Db.LinetypeExists(ctx, name)) return null;
            string file = Db.DefaultResourceFile(ctx.Database, ".lin");
            string? path = Db.FindSupportFile(ctx.Database, file);
            if (path is null) return "linetype '" + name + "' is not in the drawing and " + file + " was not found on the support path";
            var def = ResourceFiles.FindLinetype(path, name);
            if (def is null) return "linetype '" + name + "' is not in the drawing or in " + path;
            if (def.IsComplex) return "linetype '" + name + "' contains shapes or text; load it first with linetype.ensure";
            definition = def;
            return null;
        }

        /// <summary>The linetype id, creating a simple linetype from <paramref name="definition"/> when needed. Returns the new handle via <paramref name="createdHandle"/>.</summary>
        internal static ObjectId Ensure(OpContext ctx, string name, LinetypeDefinition? definition, out string? createdHandle)
        {
            createdHandle = null;
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var existing = Db.SymbolId(tr, db.LinetypeTableId, name);
            if (!existing.IsNull) return existing;
            if (definition is null)
                throw new InvalidOperationException("Linetype '" + name + "' is missing and no definition was resolved during validation.");
            var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForWrite);
            var rec = new LinetypeTableRecord
            {
                Name = definition.Name,
                AsciiDescription = definition.Description,
                PatternLength = definition.Dashes.Sum(d => Math.Abs(d)),
                NumDashes = definition.Dashes.Count,
            };
            for (int i = 0; i < definition.Dashes.Count; i++) rec.SetDashLengthAt(i, definition.Dashes[i]);
            var id = ltt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            createdHandle = OpContext.HandleOf(id);
            return id;
        }
    }
}
