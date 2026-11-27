using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// layerstate.ensure: LayerStateManager can only save the drawing's current layer settings, so the requested
    /// differences are set on the layers inside the job transaction, the state is saved (SaveLayerState with no
    /// viewport), and the layers are put back unless restore is true (then they keep the values, which is the drawing in
    /// that state; RestoreLayerState is never called). Update exports the old state to a .las file in the job's output
    /// folder, deletes it and saves the new one. The state's record in the layer states dictionary is reported as
    /// created or modified so the rehearsal leak check covers it. An existing state with update false is left alone and
    /// cannot be restored by this op.
    ///
    /// A rehearsal never deletes or overwrites a saved state: whether Transaction.Abort undoes LayerStateManager
    /// changes is not documented, so it saves the new state under a temporary name, reads that back and deletes it.
    /// </summary>
    internal sealed class LayerStateEnsure : OpHandler<LayerStateEnsure.Plan>
    {
        public override string Name => "layerstate.ensure";

        internal sealed class Change
        {
            public ObjectId Layer;
            public string Name = "";
            public bool? On, Frozen, Locked, Plot;
            public Color? Color;
            public ObjectId Linetype;
            public LineWeight? LineWeight;
            public int? Transparency;
        }

        internal sealed class Saved
        {
            public bool Off, Frozen, Locked, Plot;
            public Color Color = null!;
            public ObjectId Linetype;
            public LineWeight LineWeight;
            public Transparency Transparency;
        }

        internal sealed class Plan
        {
            public string Name = "";
            public string? Description;
            public LayerStateMasks Mask;
            public bool Restore, Update, Exists;
            public string Action = "";
            public List<Change> Changes = new List<Change>();
            public string? Backup, BackupWritten, TempDir;
            /// <summary>The state actually written: Name in a commit, a temporary name in a rehearsal.</summary>
            public string Saved = "";
            public string RecordHandle = "";
            public int LayerCount;
        }

        private static readonly Dictionary<string, LayerStateMasks> Masks = new Dictionary<string, LayerStateMasks>
        {
            { "on", LayerStateMasks.On }, { "frozen", LayerStateMasks.Frozen }, { "locked", LayerStateMasks.Locked }, { "plot", LayerStateMasks.Plot },
            { "color", LayerStateMasks.Color }, { "linetype", LayerStateMasks.LineType }, { "lineweight", LayerStateMasks.LineWeight },
            { "transparency", LayerStateMasks.Transparency }, { "plotStyle", LayerStateMasks.PlotStyle },
        };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan
            {
                Name = args.GetString("name").Trim(),
                Description = args.GetStringOrNull("description"),
                Restore = args.GetBool("restore"),
                Update = args.GetBool("update"),
            };
            if (!AcadValues.IsValidSymbolName(p.Name, out string nameErr)) OpContext.Error(issues, args, "name", "name_invalid", nameErr);
            p.Mask = LayerStateMasks.None;
            foreach (var prop in args.GetStringList("properties")) p.Mask |= Masks[prop];
            p.Exists = Db.Read(ctx, tr => ctx.Database.LayerStateManager.HasLayerState(p.Name));
            if (p.Exists && !p.Update && p.Restore)
                OpContext.Error(issues, args, "restore", "restore_unchanged", "layer state '" + p.Name + "' exists and update is false, so this op leaves it alone; restore it in AutoCAD, or set update true to rewrite and restore it");
            if (p.Exists && p.Update)
            {
                string backupName = Safe(p.Name) + ".backup.las";
                if (OutputFile.IsReservedDeviceName(backupName)) backupName = "_" + backupName;
                p.Backup = Path.Combine(DraftDb.OutputDir(ctx), backupName);
                if (File.Exists(p.Backup) && ctx.IsValidating)
                    OpContext.Warning(issues, args, "update", "backup_replaced", "the backup file '" + p.Backup + "' exists and will be replaced");
            }

            string current = Db.CurrentLayerName(ctx);
            var byLayer = new Dictionary<ObjectId, Change>();
            Db.Read(ctx, tr =>
            {
                var lt = (LayerTable)tr.GetObject(ctx.Database.LayerTableId, OpenMode.ForRead);
                var all = new List<LayerTableRecord>();
                foreach (ObjectId id in lt) if (!id.IsErased) all.Add((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead));
                foreach (var l in args.GetObjectList("layers"))
                {
                    string pattern = l.GetString("name");
                    var rx = SheetDb.Wildcard(pattern);
                    var hits = all.Where(x => rx.IsMatch(x.Name)).ToList();
                    if (hits.Count == 0) { OpContext.Error(issues, l, "name", IssueCodes.LayerMissing, "no layer matches '" + pattern + "'"); continue; }
                    Color? colour = null;
                    if (l.Has("color"))
                    {
                        if (AcadValues.TryParseColor(l.Get("color"), false, out var c, out string err)) colour = c;
                        else OpContext.Error(issues, l, "color", IssueCodes.ColorInvalid, err);
                    }
                    var ltId = ObjectId.Null;
                    if (l.GetStringOrNull("linetype") is string ltName)
                    {
                        ltId = Db.SymbolId(tr, ctx.Database.LinetypeTableId, ltName);
                        if (ltId.IsNull) OpContext.Error(issues, l, "linetype", IssueCodes.LinetypeMissing, "linetype '" + ltName + "' is not loaded; load it with linetype.ensure or layer.ensure first");
                    }
                    LineWeight? lw = null;
                    if (l.Has("lineweight"))
                    {
                        if (AcadValues.TryParseLineWeight(l.Get("lineweight"), true, out var w, out string err)) lw = w;
                        else OpContext.Error(issues, l, "lineweight", IssueCodes.LineweightInvalid, err);
                    }
                    bool? frozen = l.Has("frozen") ? l.GetBool("frozen") : (bool?)null;
                    foreach (var layer in hits)
                    {
                        if (frozen == true && string.Equals(layer.Name, current, StringComparison.OrdinalIgnoreCase))
                        {
                            OpContext.Error(issues, l, "frozen", "layer_current", "layer '" + layer.Name + "' is the current layer and cannot be frozen; make another layer current first");
                            continue;
                        }
                        if (!byLayer.TryGetValue(layer.ObjectId, out var ch)) byLayer[layer.ObjectId] = ch = new Change { Layer = layer.ObjectId, Name = layer.Name };
                        if (l.Has("on")) ch.On = l.GetBool("on");
                        if (frozen.HasValue) ch.Frozen = frozen;
                        if (l.Has("locked")) ch.Locked = l.GetBool("locked");
                        if (l.Has("plot")) ch.Plot = l.GetBool("plot");
                        if (colour != null) ch.Color = colour;
                        if (!ltId.IsNull) ch.Linetype = ltId;
                        if (lw.HasValue) ch.LineWeight = lw;
                        if (l.Has("transparency")) ch.Transparency = l.GetInt("transparency");
                    }
                }
                return 0;
            });
            p.Changes = byLayer.Values.ToList();
            if (ctx.IsValidating && p.Exists && !p.Update)
                OpContext.Warning(issues, args, "update", "exists", "layer state '" + p.Name + "' exists and is left unchanged (update is false)");
            return p;
        }

        private static string Safe(string s) => new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

        private static Saved Snapshot(LayerTableRecord l) => new Saved
        {
            Off = l.IsOff, Frozen = l.IsFrozen, Locked = l.IsLocked, Plot = l.IsPlottable, Color = l.Color,
            Linetype = l.LinetypeObjectId, LineWeight = l.LineWeight, Transparency = l.Transparency,
        };

        private static void Put(LayerTableRecord l, Change c)
        {
            if (c.On.HasValue) l.IsOff = !c.On.Value;
            if (c.Frozen.HasValue) l.IsFrozen = c.Frozen.Value;
            if (c.Locked.HasValue) l.IsLocked = c.Locked.Value;
            if (c.Plot.HasValue) l.IsPlottable = c.Plot.Value;
            if (c.Color != null) l.Color = c.Color;
            if (!c.Linetype.IsNull) l.LinetypeObjectId = c.Linetype;
            if (c.LineWeight.HasValue) l.LineWeight = c.LineWeight.Value;
            if (c.Transparency.HasValue) l.Transparency = AcadValues.ToTransparency(c.Transparency.Value);
        }

        private static void Put(LayerTableRecord l, Saved s)
        {
            if (l.IsOff != s.Off) l.IsOff = s.Off;
            if (l.IsFrozen != s.Frozen) l.IsFrozen = s.Frozen;
            if (l.IsLocked != s.Locked) l.IsLocked = s.Locked;
            if (l.IsPlottable != s.Plot) l.IsPlottable = s.Plot;
            l.Color = s.Color;
            l.LinetypeObjectId = s.Linetype;
            l.LineWeight = s.LineWeight;
            l.Transparency = s.Transparency;
        }

        private static ObjectId Record(Transaction tr, LayerStateManager lsm, string name)
        {
            var dict = lsm.LayerStatesDictionaryId(false);
            return dict.IsNull ? ObjectId.Null : DraftDb.DictionaryEntry(tr, dict, name);
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var lsm = ctx.Database.LayerStateManager;
            p.Saved = p.Name;
            if (p.Exists && !p.Update)
            {
                p.Action = "unchanged";
                var rec = Record(tr, lsm, p.Name);
                if (!rec.IsNull) { p.RecordHandle = OpContext.HandleOf(rec); result.Unchanged.Add(p.RecordHandle); }
                result.Outputs.Add("state", p.Name).Add("layerCount", LayerCount(lsm, p.Name)).Add("backup", JsonValue.Null);
                return p;
            }
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            if (rehearse)
            {
                p.Saved = "SA rehearsal " + Guid.NewGuid().ToString("N");
                if (lsm.HasLayerState(p.Saved)) throw new InvalidOperationException("The temporary layer state name '" + p.Saved + "' is taken.");
            }
            string? oldDescription = null;
            if (p.Exists)
            {
                oldDescription = lsm.GetLayerStateDescription(p.Name);
                p.BackupWritten = p.Backup;
                if (rehearse)
                {
                    p.TempDir = Path.Combine(Path.GetTempPath(), "SA-rehearse", Safe(ctx.Job.JobId), Safe(ctx.CurrentOp.Id));
                    if (Directory.Exists(p.TempDir)) Directory.Delete(p.TempDir, true);
                    p.BackupWritten = Path.Combine(p.TempDir, Path.GetFileName(p.Backup!));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(p.BackupWritten)!);
                if (File.Exists(p.BackupWritten)) File.Delete(p.BackupWritten);
                lsm.ExportLayerState(p.Name, p.BackupWritten);
                if (!rehearse) lsm.DeleteLayerState(p.Name);
            }

            var saved = new Dictionary<ObjectId, Saved>();
            foreach (var c in p.Changes)
            {
                var l = (LayerTableRecord)tr.GetObject(c.Layer, OpenMode.ForWrite);
                saved[c.Layer] = Snapshot(l);
                Put(l, c);
            }
            lsm.SaveLayerState(p.Saved, p.Mask, ObjectId.Null);
            string? description = p.Description ?? oldDescription;
            if (description != null) lsm.SetLayerStateDescription(p.Saved, description);
            foreach (var c in p.Changes)
            {
                if (p.Restore) { result.Modified.Add(OpContext.HandleOf(c.Layer)); continue; }
                Put((LayerTableRecord)tr.GetObject(c.Layer, OpenMode.ForWrite), saved[c.Layer]);
            }

            var record = Record(tr, lsm, p.Saved);
            p.RecordHandle = record.IsNull ? "" : OpContext.HandleOf(record);
            p.Action = p.Exists ? "updated" : "created";
            if (p.RecordHandle.Length > 0)
            {
                if (p.Exists && !rehearse) result.Modified.Add(p.RecordHandle);
                else result.Created.Add(p.RecordHandle);
            }
            result.Outputs.Add("state", p.Name).Add("layerCount", LayerCount(lsm, p.Saved)).Add("backup", p.Backup != null ? (JsonValue)p.Backup : JsonValue.Null);
            return p;
        }

        private static int LayerCount(LayerStateManager lsm, string name)
        {
            int n = 0;
            var layers = lsm.GetLayerStateLayers(name, false);
            if (layers != null) foreach (var _ in layers) n++;
            return n;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var lsm = ctx.Database.LayerStateManager;
            string s = p.Saved.Length > 0 ? p.Saved : p.Name;
            try
            {
                var tr = ctx.Transaction;
                var mismatches = new List<string>();
                if (!lsm.HasLayerState(s)) { mismatches.Add("layer state '" + s + "' is not in the drawing"); }
                else
                {
                    p.LayerCount = LayerCount(lsm, s);
                    var mask = lsm.GetLayerStateMask(s);
                    result.Readback.Add("state", p.Name).Add("action", p.Action).Add("layerCount", p.LayerCount).Add("mask", mask.ToString())
                        .Add("description", lsm.GetLayerStateDescription(s) ?? "").Add("record", p.RecordHandle);
                    if (s != p.Name) result.Readback.Add("rehearsalState", s);
                    if (p.Action != "unchanged")
                    {
                        if ((mask & p.Mask) != p.Mask) mismatches.Add("the state's mask reads " + mask + ", expected " + p.Mask);
                        if (p.Description != null && lsm.GetLayerStateDescription(s) != p.Description) mismatches.Add("the description reads back differently");
                        if (p.RecordHandle.Length == 0) mismatches.Add("the state's record was not found in the layer states dictionary");
                        if (p.Restore)
                        {
                            int bad = 0;
                            foreach (var c in p.Changes)
                            {
                                var l = (LayerTableRecord)tr.GetObject(c.Layer, OpenMode.ForRead);
                                if ((c.On.HasValue && l.IsOff == c.On.Value) || (c.Frozen.HasValue && l.IsFrozen != c.Frozen.Value) ||
                                    (c.Locked.HasValue && l.IsLocked != c.Locked.Value) || (c.Plot.HasValue && l.IsPlottable != c.Plot.Value) ||
                                    (c.Color != null && !AcadValues.SameColor(l.Color, c.Color)) || (!c.Linetype.IsNull && l.LinetypeObjectId != c.Linetype) ||
                                    (c.LineWeight.HasValue && l.LineWeight != c.LineWeight.Value) ||
                                    (c.Transparency.HasValue && AcadValues.TransparencyPercent(l.Transparency) != c.Transparency.Value)) bad++;
                            }
                            if (bad > 0) mismatches.Add(bad + " layers do not show the state's settings after restore");
                            bool same = lsm.CompareLayerStateToDb(s, ObjectId.Null);
                            result.Readback.Add("drawingMatchesState", same);
                            if (!same) ctx.Warn("AutoCAD reports that the drawing's layers differ from layer state '" + p.Name + "' after restore.");
                        }
                    }
                    if (p.BackupWritten != null)
                    {
                        bool ok = File.Exists(p.BackupWritten);
                        result.Readback.Add("backupWritten", ok);
                        if (!ok) mismatches.Add("the backup " + p.BackupWritten + " was not written");
                    }
                }
                foreach (var m in mismatches) Mismatch(result, m);
                return mismatches.Count == 0;
            }
            finally
            {
                if (s != p.Name)
                {
                    try { if (lsm.HasLayerState(s)) lsm.DeleteLayerState(s); }
                    catch (Exception e) { ctx.Warn("Could not remove the rehearsal's temporary layer state '" + s + "': " + e.Message); }
                }
                if (p.TempDir != null)
                {
                    try { Directory.Delete(p.TempDir, true); }
                    catch (IOException e) { ctx.Warn("Could not remove the rehearsal folder '" + p.TempDir + "': " + e.Message); }
                    catch (UnauthorizedAccessException e) { ctx.Warn("Could not remove the rehearsal folder '" + p.TempDir + "': " + e.Message); }
                }
            }
        }
    }
}
