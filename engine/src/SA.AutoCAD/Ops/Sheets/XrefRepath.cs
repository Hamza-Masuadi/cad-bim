using System;
using System.Collections.Generic;
using System.IO;
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
    /// xref.repath: rewrites BlockTableRecord.PathName of external references. The target file is, in order: newPath
    /// (one named reference), the file the current stored path points at, or a file of the same name in one of the
    /// searchFolders. A reference whose file is found nowhere is reported as unresolved and left unchanged. Paths are
    /// stored relative to the host drawing (pathType relative; an unsaved host or another drive falls back to full
    /// with a warning) or in full.
    ///
    /// Reload: the schema's reload (default true) calls Database.ReloadXrefs for the changed references, in commit only.
    /// Reloading is not known to be undone by the transaction, so a rehearsal never reloads and says so. Nothing is bound.
    /// </summary>
    internal sealed class XrefRepath : OpHandler<XrefRepath.Plan>
    {
        public override string Name => "xref.repath";

        internal sealed class Item
        {
            public string Name = "";
            public ObjectId Id;
            public string Before = "";
            public string? Target;
            public string After = "";
            public bool Change;
            public string Status = "";
        }

        internal sealed class Plan
        {
            public List<Item> Items = new List<Item>();
            public bool Reloaded;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            string? host = SheetDb.DrawingFolder(ctx);
            string pathType = args.GetString("pathType");
            string? newPath = args.GetStringOrNull("newPath");
            var folders = args.GetStringList("searchFolders");
            foreach (var f in folders)
                if (!Path.IsPathRooted(f) || !Directory.Exists(f))
                    OpContext.Error(issues, args, "searchFolders", "folder_missing", "search folder '" + f + "' does not exist (give full paths)");
            bool named = args.Has("names");
            var names = args.GetStringList("names");
            if (newPath != null && names.Count != 1)
                OpContext.Error(issues, args, "newPath", "newpath_needs_one_name", "newPath applies to exactly one named reference");
            if (newPath != null)
            {
                string? full = Path.IsPathRooted(newPath) ? newPath : host != null ? Path.Combine(host, newPath) : null;
                if (full is null || !SheetDb.FileExists(full))
                    OpContext.Error(issues, args, "newPath", "xref_file_missing", "'" + newPath + "' does not exist; the path is not changed to a missing file");
                else newPath = Path.GetFullPath(full);
            }

            Db.Read(ctx, tr =>
            {
                var bt = (BlockTable)tr.GetObject(ctx.Database.BlockTableId, OpenMode.ForRead);
                var xrefs = new List<BlockTableRecord>();
                foreach (ObjectId id in bt)
                {
                    if (id.IsErased) continue;
                    var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (b.IsFromExternalReference && !b.IsDependent) xrefs.Add(b);
                }
                IEnumerable<BlockTableRecord> chosen;
                if (named)
                {
                    var list = new List<BlockTableRecord>();
                    foreach (var n in names)
                    {
                        var b = xrefs.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                        if (b is null) OpContext.Error(issues, args, "names", "xref_missing", "there is no external reference named '" + n + "'");
                        else list.Add(b);
                    }
                    chosen = list;
                }
                else chosen = xrefs.Where(x => x.XrefStatus != XrefStatus.Resolved || !SheetDb.FileExists(SheetDb.AbsoluteFromStored(x.PathName ?? "", host)));

                foreach (var b in chosen)
                {
                    var it = new Item { Name = b.Name, Id = b.ObjectId, Before = b.PathName ?? "" };
                    if (newPath != null) it.Target = newPath;
                    else
                    {
                        var abs = SheetDb.AbsoluteFromStored(it.Before, host);
                        if (SheetDb.FileExists(abs)) it.Target = abs;
                        else
                        {
                            string file = Path.GetFileName(it.Before.Replace('/', '\\').Split('\\').Last());
                            if (file.Length == 0) file = b.Name + ".dwg";
                            foreach (var f in folders)
                            {
                                var cand = Path.Combine(f, file);
                                if (SheetDb.FileExists(cand)) { it.Target = Path.GetFullPath(cand); break; }
                            }
                        }
                    }
                    if (it.Target is null)
                    {
                        it.Status = "unresolved";
                        it.After = it.Before;
                        if (ctx.IsValidating) OpContext.Warning(issues, args, null, "xref_unresolved", "reference '" + b.Name + "': no file found for '" + it.Before + "'; left unchanged");
                    }
                    else
                    {
                        string? stored = null;
                        if (pathType == "relative")
                        {
                            stored = host is null ? null : SheetDb.RelativePath(host, it.Target);
                            if (stored is null && ctx.IsValidating)
                                OpContext.Warning(issues, args, "pathType", "xref_path_full", "reference '" + b.Name + "': " + (host is null ? "the host drawing is not saved" : "the file is on another drive or server") + "; the path is stored in full");
                        }
                        it.After = stored ?? it.Target;
                        it.Change = !string.Equals(it.After, it.Before, StringComparison.OrdinalIgnoreCase);
                        it.Status = it.Change ? "repathed" : "unchanged";
                    }
                    p.Items.Add(it);
                }
                return 0;
            });
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var reload = new ObjectIdCollection();
            foreach (var it in p.Items)
            {
                if (!it.Change) { if (it.Status == "unchanged") result.Unchanged.Add(OpContext.HandleOf(it.Id)); continue; }
                var b = (BlockTableRecord)tr.GetObject(it.Id, OpenMode.ForWrite);
                b.PathName = it.After;
                result.Modified.Add(OpContext.HandleOf(it.Id));
                reload.Add(it.Id);
            }
            if (args.GetBool("reload") && reload.Count > 0)
            {
                if (ctx.Mode == RunMode.Commit)
                {
                    ctx.Database.ReloadXrefs(reload);
                    p.Reloaded = true;
                }
                else ctx.Warn("Rehearsal: the repathed references were not reloaded (reloading is not undone by a rollback); commit reloads them.");
            }
            var refs = new JsonArray();
            foreach (var it in p.Items)
                refs.Add(new JsonObject().Add("name", it.Name).Add("before", it.Before).Add("after", it.After).Add("resolved", it.Target != null));
            result.Outputs.Add("references", refs);
            var unresolved = p.Items.Where(i => i.Target is null).Select(i => i.Name).ToList();
            if (unresolved.Count > 0) ctx.Warn("Unresolved references left unchanged: " + string.Join(", ", unresolved) + ".");
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            bool ok = true;
            var tr = ctx.Transaction;
            var list = new JsonArray();
            foreach (var it in p.Items)
            {
                var b = (BlockTableRecord)tr.GetObject(it.Id, OpenMode.ForRead);
                string read = b.PathName ?? "";
                list.Add(new JsonObject().Add("name", it.Name).Add("storedPath", read).Add("status", it.Status)
                    .Add("fileFound", it.Target != null).Add("xrefStatus", b.XrefStatus.ToString()));
                if (!string.Equals(read, it.After, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "reference '" + it.Name + "' path reads back '" + read + "', expected '" + it.After + "'");
                if (p.Reloaded && it.Change && b.XrefStatus != XrefStatus.Resolved) ok = Mismatch(result, "reference '" + it.Name + "' is " + b.XrefStatus + " after reloading");
            }
            result.Readback.Add("references", list);
            result.Readback.Add("reloaded", p.Reloaded);
            result.Readback.Add("counts", new JsonObject()
                .Add("repathed", p.Items.Count(i => i.Change)).Add("unchanged", p.Items.Count(i => i.Status == "unchanged")).Add("unresolved", p.Items.Count(i => i.Target is null)));
            return ok;
        }
    }
}
