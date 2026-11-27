using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Blocks
{
    /// <summary>
    /// block.export (reports/FAMILY-CREATE-CONTRACT.md section 1): one DWG per chosen block table record, written with
    /// Database.Wblock(blockId), and placements.json (SA.Core.Families.Placements) listing the references. Read-only for
    /// the drawing: readback proves HANDSEED and the drawing fingerprint did not move. Files go to a staging folder and
    /// the runner puts them in place after the commit; a rehearsal writes to a temporary folder that is deleted.
    ///
    /// Selection walks model space (and paper space layouts when excludeLayouts is false). References nested inside
    /// other blocks, xrefs, xref-dependent blocks, MINSERT arrays and anonymous blocks that are not dynamic-block
    /// representations are never listed. A reference whose normal is neither +Z nor -Z is skipped with a reason.
    /// Lengths are in drawing units, not job units, as the contract requires.
    /// </summary>
    internal sealed class BlockExport : OpHandler<BlockExport.Plan>
    {
        public override string Name => "block.export";

        internal sealed class Entry
        {
            public ObjectId Btr;
            public string Name = "", Effective = "", File = "";
            public bool IsDynamic, Anonymous;
            public Point3d Origin;
            public DefinitionInfo Info = new DefinitionInfo();
            public List<PlacementReference> Refs = new List<PlacementReference>();
        }

        internal sealed class Plan
        {
            public List<Entry> Entries = new List<Entry>();
            public JsonArray Skipped = new JsonArray();
            public int Nested;
            public string Dir = "";
            public PlacementsFile File = new PlacementsFile();
            public string Stage = "";
            public string Handseed = "", Fingerprint = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            if (!args.GetBool("excludeXrefs"))
                OpContext.Error(issues, args, "excludeXrefs", "xref_not_exportable", "xref blocks cannot be exported; bind the xref first, or leave excludeXrefs true");
            if (args.Has("all") && !args.GetBool("all"))
                OpContext.Error(issues, args, "all", IssueCodes.InvalidValue, "all must be true; leave it out to select by blocks or layers");
            p.Dir = args.GetStringOrNull("outputDir") ?? DraftDbOutputDir(ctx);
            if (!Path.IsPathRooted(p.Dir))
            {
                OpContext.Error(issues, args, "outputDir", "output_dir_invalid", "outputDir must be a full path");
                return p;
            }
            if (!ctx.Units.IsSupported) return p;

            var names = args.GetStringList("blocks");
            var blocks = names.Select(SheetDb.Wildcard).ToList();
            var layers = Targets.Patterns(args, "layers");
            bool modelOnly = args.GetBool("excludeLayouts");
            var db = ctx.Database;

            Db.Read(ctx, tr =>
            {
                foreach (var n in names.Where(n => !SheetDb.HasWildcard(n)))
                {
                    var id = Db.SymbolId(tr, db.BlockTableId, n);
                    string? why = id.IsNull ? "block '" + n + "' is not in the drawing" : NotExportable((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead));
                    if (why != null) OpContext.Error(issues, args, "blocks", id.IsNull ? "block_missing" : "block_not_exportable", why);
                }

                var byBtr = new Dictionary<ObjectId, Entry>();
                var cache = new Dictionary<ObjectId, bool>();
                foreach (var s in SheetDb.Spaces(tr, db, modelOnly ? "model" : "all", false, false))
                {
                    var space = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        if (id.IsErased || id.ObjectClass.DxfName != "INSERT") continue;
                        if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br)) continue;
                        var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        if (def.IsFromExternalReference || def.IsDependent || def.IsLayout) continue;
                        bool dynamic = br.IsDynamicBlock;
                        string effective = dynamic ? ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name : def.Name;
                        if (effective.StartsWith("*", StringComparison.Ordinal)) continue;
                        if (blocks.Count > 0 && !Targets.AnyMatch(blocks, effective)) continue;
                        if (layers.Count > 0 && !Targets.AnyMatch(layers, br.Layer)) continue;
                        string handle = OpContext.HandleOf(id);
                        if (br is MInsertBlock) { p.Skipped.Add(Skip(effective, handle, "a multiple insert (MINSERT) array; explode or replace it with single references")); continue; }
                        var r = Reference(br, handle, s.Kind == "model" ? "model" : s.Name);
                        if (r is null) { p.Skipped.Add(Skip(effective, handle, "its normal is not parallel to Z, so it has no plan position and rotation")); continue; }
                        if (!byBtr.TryGetValue(def.ObjectId, out var e))
                        {
                            e = NewEntry(tr, def, effective, dynamic, cache);
                            byBtr.Add(def.ObjectId, e);
                            p.Entries.Add(e);
                        }
                        e.Refs.Add(r);
                    }
                }

                if (names.Count > 0 && layers.Count == 0)
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var listed = new HashSet<string>(p.Entries.Select(x => x.Effective), StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId id in bt)
                    {
                        if (id.IsErased) continue;
                        var def = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (NotExportable(def) != null || listed.Contains(def.Name) || !Targets.AnyMatch(blocks, def.Name)) continue;
                        p.Entries.Add(NewEntry(tr, def, def.Name, def.IsDynamicBlock, cache));
                    }
                }

                foreach (var s in SheetDb.Spaces(tr, db, "", true, true).Where(x => x.IsBlock))
                {
                    var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                        if (!id.IsErased && id.ObjectClass.DxfName == "INSERT" && tr.GetObject(id, OpenMode.ForRead) is BlockReference br)
                        {
                            var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            string eff = br.IsDynamicBlock ? ((BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)).Name : def.Name;
                            if (blocks.Count == 0 || Targets.AnyMatch(blocks, eff)) p.Nested++;
                        }
                }
                return 0;
            });

            foreach (var e in p.Entries.Where(x => x.Info.Raw is null).ToList())
            {
                p.Skipped.Add(new JsonObject().Add("name", e.Effective).Add("reason", "the definition has no visible geometry (" + e.Refs.Count + " references not listed)"));
                p.Entries.Remove(e);
            }
            NameEntries(p);

            var files = Placements.AssignFileNames(p.Entries.Select(e => e.Name));
            for (int i = 0; i < p.Entries.Count; i++)
            {
                if (files[i] is null)
                    OpContext.Error(issues, args, "blocks", "file_name_invalid", "block '" + p.Entries[i].Name + "' would be written as a Windows device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9); rename the block first");
                else p.Entries[i].File = files[i]!;
            }

            if (!args.GetBool("overwrite"))
            {
                var existing = p.Entries.Select(e => e.File).Where(f => f.Length > 0).Concat(new[] { PlacementsFile.DefaultFileName })
                    .Where(f => File.Exists(Path.Combine(p.Dir, f))).ToList();
                if (existing.Count > 0)
                    OpContext.Error(issues, args, "overwrite", "file_exists", existing.Count + " file(s) already exist in '" + p.Dir + "' (" + string.Join(", ", existing.Take(5)) + "); set overwrite true or use another outputDir");
            }
            if (p.Entries.Count == 0)
                OpContext.Error(issues, args, null, "nothing_to_export", "no block matches the selection" + (p.Nested > 0 ? " (" + p.Nested + " matching references are nested inside other blocks and are not listed)" : "") +
                    (p.Skipped.Count > 0 ? "; " + p.Skipped.Count + " were skipped (see the warnings)" : ""));
            int? expect = args.Has("expectCount") ? args.GetInt("expectCount") : (int?)null;
            if (expect.HasValue && expect.Value != p.Entries.Count)
                OpContext.Error(issues, args, "expectCount", "count_mismatch", p.Entries.Count + " blocks would be exported (" + string.Join(", ", p.Entries.Take(10).Select(e => e.Name)) + "), not " + expect.Value);
            if (ctx.IsValidating)
            {
                foreach (var s in p.Skipped.Take(20).Select(x => (JsonObject)x))
                    OpContext.Warning(issues, args, null, "skipped", (s.GetStringOrNull("handle") is string h ? "reference " + h + " of '" : "block '") + s.GetStringOrNull("name") + "' is left out: " + s.GetStringOrNull("reason"));
                if (p.Nested > 0)
                    OpContext.Warning(issues, args, null, "nested_not_listed", p.Nested + " matching references are nested inside other blocks; only references placed directly in the selected spaces are listed");
            }

            p.File.Source = new PlacementsSource
            {
                Drawing = Path.GetFileName(ctx.Document.Name ?? ""),
                Fingerprint = DocumentIdentity.FingerprintOf(ctx.Document),
                Units = Placements.SymbolFor(ctx.Units.Code) ?? DrawingUnits.NameOf(ctx.Units.Code),
                Insunits = ctx.Units.Code,
            };
            foreach (var e in p.Entries)
            {
                var raw = e.Info.Raw!.Value;
                p.File.Blocks.Add(new PlacementBlock
                {
                    Name = e.Name, EffectiveName = e.Effective, File = e.File, IsDynamic = e.IsDynamic,
                    Min = Vec(raw.MinPoint - e.Origin.GetAsVector()), Max = Vec(raw.MaxPoint - e.Origin.GetAsVector()),
                    Has3d = e.Info.Has3d, References = e.Refs,
                });
            }
            return p;
        }

        private static string DraftDbOutputDir(OpContext ctx) => SA.AutoCAD.Ops.Drafting.Support.DraftDb.OutputDir(ctx);

        /// <summary>Null when the definition can be exported by name, else the reason.</summary>
        private static string? NotExportable(BlockTableRecord def)
        {
            if (def.IsLayout) return "'" + def.Name + "' is a layout, not a block";
            if (def.IsFromExternalReference || def.IsDependent) return "'" + def.Name + "' is an xref or belongs to one; xrefs are never exported";
            if (def.IsAnonymous) return "'" + def.Name + "' is an anonymous block; name the dynamic block it represents instead";
            return null;
        }

        private static Entry NewEntry(Transaction tr, BlockTableRecord def, string effective, bool dynamic, Dictionary<ObjectId, bool> cache) =>
            new Entry
            {
                Btr = def.ObjectId, Name = def.Name, Effective = effective, IsDynamic = dynamic || def.IsDynamicBlock,
                Anonymous = def.IsAnonymous, Origin = def.Origin, Info = BlockDwg.Inspect(tr, def, cache),
            };

        /// <summary>Anonymous representations become "&lt;effectiveName&gt;__&lt;n&gt;", n from 1 per block in first-seen order, skipping names already used.</summary>
        private static void NameEntries(Plan p)
        {
            var used = new HashSet<string>(p.Entries.Where(e => !e.Anonymous).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in p.Entries.Where(e => e.Anonymous))
            {
                next.TryGetValue(e.Effective, out int n);
                string name;
                do name = Placements.AnonymousName(e.Effective, ++n); while (!used.Add(name));
                next[e.Effective] = n;
                e.Name = name;
            }
        }

        private static JsonObject Skip(string name, string handle, string reason) =>
            new JsonObject().Add("name", name).Add("handle", handle).Add("reason", reason);

        /// <summary>
        /// Position, rotation and signed scale in WCS, or null when the reference is not parallel to the XY plane. A
        /// reference with normal -Z is the same placement as one with normal +Z, rotation -r and scale (-x, y, -z).
        /// </summary>
        private static PlacementReference? Reference(BlockReference br, string handle, string space)
        {
            var n = br.Normal;
            if (Math.Abs(n.X) > 1e-9 || Math.Abs(n.Y) > 1e-9) return null;
            var s = br.ScaleFactors;
            bool flipped = n.Z < 0;
            double rot = Angle.ToDegrees(flipped ? -br.Rotation : br.Rotation);
            var scale = flipped ? new[] { -s.X, s.Y, -s.Z } : new[] { s.X, s.Y, s.Z };
            return new PlacementReference
            {
                Handle = handle, Space = space, Layer = br.Layer, Position = Vec(br.Position),
                RotationDeg = Degrees(rot), Scale = scale.Select(Round).ToArray(), Mirrored = Placements.IsMirrored(scale),
            };
        }

        private static double Degrees(double d)
        {
            double r = Round(((d % 360) + 360) % 360);
            return r >= 360 ? 0 : r;
        }

        // + 0.0 turns -0 into 0, so the file never shows "-0".
        private static double Round(double v) => DrawingUnits.Round(v) + 0.0;

        private static double[] Vec(Point3d p) => new[] { Round(p.X), Round(p.Y), Round(p.Z) };

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            p.Handseed = ctx.Database.Handseed.ToString();
            p.Fingerprint = DocumentIdentity.FingerprintOf(ctx.Document);
            bool overwrite = args.GetBool("overwrite");
            p.Stage = ctx.Staging.Folder(p.Dir, ctx.Mode, ctx.Job.JobId, ctx.CurrentOp.Id);
            foreach (var e in p.Entries)
            {
                string staged = Path.Combine(p.Stage, e.File);
                try { BlockDwg.Write(ctx.Database, e.Btr, e.Origin, e.Info.Raw, staged); }
                catch (Autodesk.AutoCAD.Runtime.Exception x)
                {
                    throw new InvalidOperationException("writing block '" + e.Name + "' to " + staged + " failed: " + x.ErrorStatus + " " + x.Message, x);
                }
                ctx.Staging.Add(staged, Path.Combine(p.Dir, e.File), overwrite, ctx.CurrentOp.Id);
            }
            string json = Path.Combine(p.Stage, PlacementsFile.DefaultFileName);
            Placements.WriteFile(p.File, json);
            string final = Path.Combine(p.Dir, PlacementsFile.DefaultFileName);
            ctx.Staging.Add(json, final, overwrite, ctx.CurrentOp.Id);

            result.Outputs.Add("count", p.Entries.Count).Add("references", p.Entries.Sum(e => e.Refs.Count))
                .Add("files", JsonArray.FromStrings(p.Entries.Select(e => e.File))).Add("placements", final)
                .Add("outputDir", p.Dir).Add("skipped", p.Skipped);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var mismatches = new List<string>();
            string handseed = ctx.Database.Handseed.ToString();
            string fingerprint = DocumentIdentity.FingerprintOf(ctx.Document);
            if (handseed != p.Handseed) mismatches.Add("the drawing's HANDSEED moved from " + p.Handseed + " to " + handseed + ": objects were added to the drawing");
            if (fingerprint != p.Fingerprint) mismatches.Add("the drawing fingerprint changed from " + p.Fingerprint + " to " + fingerprint + ": the export modified the drawing");

            var files = new JsonArray();
            foreach (var e in p.Entries)
            {
                string path = Path.Combine(p.Stage, e.File);
                if (!File.Exists(path)) { mismatches.Add(e.File + " was not written"); continue; }
                BlockDwg.DwgFacts f;
                try { f = BlockDwg.Read(path); }
                catch (Exception x) { mismatches.Add(e.File + " does not open: " + x.Message); continue; }
                var want = new Extents3d(e.Info.Raw!.Value.MinPoint - e.Origin.GetAsVector(), e.Info.Raw.Value.MaxPoint - e.Origin.GetAsVector());
                if (f.Insunits != ctx.Units.Code) mismatches.Add(e.File + " has INSUNITS " + f.Insunits + ", the drawing " + ctx.Units.Code);
                if (f.Insbase.DistanceTo(Point3d.Origin) > BlockDwg.Tolerance(want)) mismatches.Add(e.File + " has INSBASE " + f.Insbase + ", not the origin");
                if (f.Entities != e.Info.Entities) mismatches.Add(e.File + " holds " + f.Entities + " objects, the definition " + e.Info.Entities);
                if (f.Extents is null || !BlockDwg.Close(f.Extents.Value, want))
                    mismatches.Add(e.File + " extents " + (f.Extents?.ToString() ?? "(none)") + " differ from the definition's " + want + " (base point at the origin)");
                files.Add(new JsonObject().Add("file", e.File).Add("bytes", new System.IO.FileInfo(path).Length).Add("entities", f.Entities));
            }

            int refs = p.Entries.Sum(e => e.Refs.Count);
            try
            {
                var back = Placements.ReadFile(Path.Combine(p.Stage, PlacementsFile.DefaultFileName));
                foreach (var i in Placements.Validate(back, p.Stage)) mismatches.Add("placements.json: " + i.Path + " " + i.Message);
                if (back.Blocks.Count != p.Entries.Count) mismatches.Add("placements.json lists " + back.Blocks.Count + " blocks, expected " + p.Entries.Count);
                int backRefs = back.Blocks.Sum(b => b.References.Count);
                if (backRefs != refs) mismatches.Add("placements.json lists " + backRefs + " references, expected " + refs);
            }
            catch (Exception x) when (x is FormatException || x is IOException) { mismatches.Add("placements.json does not read back: " + x.Message); }

            result.Readback.Add("blocks", p.Entries.Count).Add("references", refs).Add("files", files)
                .Add("handseed", handseed).Add("drawingUnchanged", handseed == p.Handseed && fingerprint == p.Fingerprint)
                .Add("nestedSkipped", p.Nested).Add("skipped", p.Skipped.Count);
            if (ctx.Mode == RunMode.Rehearse) result.Readback.Add("rehearsalPath", p.Stage);
            else if (mismatches.Count == 0) ctx.Report.Artifacts.Add(new Artifact("json", Path.Combine(p.Dir, PlacementsFile.DefaultFileName), "block.export"));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
