using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Host;
using SA.Revit.Ops.FamilyCreation.Support;
using SA.Revit.Ops.FamilyEditing;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FromCad
{
    /// <summary>
    /// family.fromcad: one loadable family per block of a placements.json (block.export's output), built by
    /// <see cref="CadFamilyBuilder"/> in a family document of its own, flex-tested, saved as .rfa in a stage folder
    /// (published to outputDir only when the job commits) and loaded with the family document's
    /// LoadFamily(project, IFamilyLoadOptions). Like family.edit it runs outside the job's transactions, because
    /// Revit loads a family document into a project only while the project has no open transaction; the job's
    /// transaction group still holds the load, so a rehearsal rolls it back. Family documents are always closed.
    /// The family name comes from the saved file name, so the .rfa is saved before it is loaded.
    /// </summary>
    public sealed class FamilyFromCad : OpHandler, IOutsideTransactionOp
    {
        public override string Name => "family.fromcad";

        private const string Stash = "_stash";

        private static readonly FieldSpec OverrideSpec =
            SA.Core.Jobs.Ops.RevitOpsCadFamilies.All().First(s => s.Name == "family.fromcad").Args.FindField("overrides")!.Item!;

        private sealed class Item
        {
            public PlacementBlock Block = null!;
            public string Path = "";
            public string FamilyName = "";
            public Category Category = null!;
            public string Template = "";
            public bool GenericTemplate;
            public FamilySpec Spec = new FamilySpec();
            public Family? Existing;
            public string FinalFile = "";
        }

        private sealed class Plan
        {
            public string OutputDir = "";
            public bool Overwrite;
            public List<Item> Items = new List<Item>();
        }

        // ------------------------------------------------------------------ plan

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool readback)
        {
            var doc = ctx.Doc;
            if (!readback && ctx.Job.Job.Ops.Count != 1)
                OpContext.Error(issues, ctx.Op.Path + ".op", JobValidator.OpNotAlone, "family.fromcad must be the only op in its job");
            if (doc.IsFamilyDocument)
            {
                OpContext.Error(issues, ctx.Op.ArgsPath, RevitIssueCodes.FamilyDocument, "family.fromcad loads families into a project; this document is a family");
                return null;
            }
            if (!OptionalFunctionalityUtils.IsDWGImportLinkAvailable())
            {
                OpContext.Error(issues, ctx.Op.ArgsPath, RevitIssueCodes.NotAllowed, "DWG import is not available in this Revit installation");
                return null;
            }
            if (ctx.IsPending(args, "file")) return null;
            string fileAt = ctx.FieldPath(args, "file");
            string file = args.GetString("file");
            var f = CadFamilies.Load(file, fileAt, issues);
            if (f is null) return null;
            string folder = Path.GetDirectoryName(file)!;
            var unit = f.Source.LengthUnit!.Value;
            double k = UnitUtils.ConvertToInternalUnits(1, RevitUnits.ForJobUnit(unit));
            double tol = ctx.Units.LengthToleranceInternal;

            var p = new Plan { Overwrite = args.GetBool("overwrite") };
            string outAt = ctx.FieldPath(args, "outputDir");
            p.OutputDir = args.GetStringOrNull("outputDir") ?? Path.Combine(ctx.OutputDir, "families");
            if (!Path.IsPathRooted(p.OutputDir)) OpContext.Error(issues, outAt, RevitIssueCodes.Invalid, "give a full folder path, not '" + p.OutputDir + "'");

            string blocksAt = ctx.FieldPath(args, "blocks");
            var names = args.Has("blocks") ? args.GetStringList("blocks") : f.Blocks.Select(b => b.Name).ToList();
            var selected = new List<PlacementBlock>();
            for (int i = 0; i < names.Count; i++)
            {
                var b = f.Find(names[i]);
                if (b is null) OpContext.Error(issues, JsonPath.Index(blocksAt, i), RevitIssueCodes.NotFound, "no block '" + names[i] + "' in " + Path.GetFileName(file) + Lookup.Suggest(names[i], f.Blocks.Select(x => x.Name).ToList()));
                else if (selected.Contains(b)) OpContext.Error(issues, JsonPath.Index(blocksAt, i), RevitIssueCodes.Invalid, "block '" + b.Name + "' is listed twice");
                else selected.Add(b);
            }

            string ovAt = ctx.FieldPath(args, "overrides");
            var overrides = new Dictionary<PlacementBlock, OpArgs>();
            foreach (var kv in args.GetMap("overrides"))
            {
                string at = JsonPath.Key(ovAt, kv.Key);
                var b = selected.FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (b is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "block '" + kv.Key + "' is not among the blocks this op makes families for"); continue; }
                if (kv.Value is JsonObject o) overrides[b] = new OpArgs(OverrideSpec, o, args.Unit, at);
            }

            List<string>? templates = null;
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool metric = ctx.Units.JobUnit != LengthUnit.Inch && ctx.Units.JobUnit != LengthUnit.Foot;
            foreach (var b in selected)
            {
                overrides.TryGetValue(b, out var o);
                string at = o?.Path ?? blocksAt;
                string? S(string key) => o?.GetStringOrNull(key) ?? args.GetStringOrNull(key);
                var it = new Item { Block = b, Path = at };

                it.FamilyName = CadFamilies.FamilyName(b.Name, args.GetStringOrNull("familyPrefix"), o?.GetStringOrNull("familyName"));
                if (CadFamilies.FamilyNameProblem(it.FamilyName) is string np) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "block '" + b.Name + "': " + np + "; give overrides." + b.Name + ".familyName"); continue; }
                if (seen.TryGetValue(it.FamilyName, out var other)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "blocks '" + other + "' and '" + b.Name + "' would both make family '" + it.FamilyName + "'"); continue; }
                seen[it.FamilyName] = b.Name;

                string catName = S("category")!;
                var cat = Lookup.Category(doc, catName, out string? cerr);
                if (cat is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "block '" + b.Name + "': " + cerr); continue; }
                if (cat.CategoryType != CategoryType.Model || cat.Parent != null || cat.IsTagCategory)
                { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "block '" + b.Name + "': '" + cat.Name + "' is not a model category a loadable family can have"); continue; }
                it.Category = cat;

                string typeName = S("typeName")!.Trim();
                if (typeName.Length == 0 || Kit.BadTypeName(typeName) is string bad && bad.Contains("does not allow"))
                { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "block '" + b.Name + "': type name '" + typeName + "' is not usable"); continue; }

                if (S("template") is string tpl)
                {
                    if (!Path.IsPathRooted(tpl) || !File.Exists(tpl) || !tpl.EndsWith(".rft", StringComparison.OrdinalIgnoreCase))
                    { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "block '" + b.Name + "': template '" + tpl + "' is not an existing .rft file (give its full path)"); continue; }
                    it.Template = tpl;
                }
                else
                {
                    templates ??= FamilyTemplates.All(doc.Application);
                    var found = FamilyTemplates.ForCategory(templates, cat.Name, metric);
                    if (found is null && CadFamilies.EnglishTemplateStem(cat.BuiltInCategory.ToString()) is string en) found = FamilyTemplates.ForCategory(templates, en, metric);
                    if (found is null) { found = FamilyTemplates.GenericModel(templates, metric); it.GenericTemplate = found != null; }
                    if (found is null)
                    {
                        OpContext.Error(issues, at, RevitIssueCodes.NotFound, "block '" + b.Name + "': no family template for '" + cat.Name + "' and no Generic Model template in " +
                            SafeTemplatePath(doc) + "; " + FamilyTemplates.Listing(templates, 12) + ". Give template with a full path");
                        continue;
                    }
                    it.Template = found;
                }

                string body = o?.GetStringOrNull("body") ?? args.GetString("body");
                string symbol = o?.GetStringOrNull("symbol") ?? args.GetString("symbol");
                var s = new FamilySpec
                {
                    Block = b.Name, Source = f.Source.Drawing, Dwg = Path.Combine(folder, b.File), Unit = ImportUnitFor(unit),
                    Body = body, Symbol = symbol, TypeName = typeName, Subcategory = args.GetString("subcategory").Trim(),
                    MaterialParameter = args.GetString("materialParameter").Trim(), BodyInPlan = args.GetBool("bodyInPlan"),
                    SetCategory = cat.BuiltInCategory == BuiltInCategory.INVALID ? (BuiltInCategory?)null : cat.BuiltInCategory,
                    MinX = b.Min[0] * k, MinY = b.Min[1] * k, MaxX = b.Max[0] * k, MaxY = b.Max[1] * k,
                };
                if (body == "box")
                {
                    if (s.MaxX - s.MinX <= tol || s.MaxY - s.MinY <= tol)
                    { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "block '" + b.Name + "' has no plan extent for a box (" + Kit.Num((b.Max[0] - b.Min[0])) + " x " + Kit.Num(b.Max[1] - b.Min[1]) + " " + f.Source.Units + "); use body \"none\""); continue; }
                    double? h = o?.GetLengthOrNull("height") ?? args.GetLengthOrNull("height");
                    if (h.HasValue) s.Height = ctx.Units.LengthToInternal(h.Value);
                    else if ((b.Max[2] - b.Min[2]) * k > tol) s.Height = (b.Max[2] - b.Min[2]) * k;
                    else { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "block '" + b.Name + "' is flat; give height (or overrides." + b.Name + ".height) for its box"); continue; }
                }
                if (body == "import3d" && !b.Has3d)
                { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "block '" + b.Name + "' has no 3D objects (has3d false); use body \"box\" or \"none\""); continue; }
                if (s.Subcategory.Length == 0 || Kit.BadTypeName(s.Subcategory) is string sb && sb.Contains("does not allow"))
                { OpContext.Error(issues, ctx.FieldPath(args, "subcategory"), RevitIssueCodes.Invalid, "subcategory name '" + s.Subcategory + "' is not usable"); return null; }
                if (body == "box" && s.MaterialParameter.Length == 0)
                { OpContext.Error(issues, ctx.FieldPath(args, "materialParameter"), RevitIssueCodes.Invalid, "give a parameter name"); return null; }
                if (body == "box" && !BoxRecipe(ctx, it.FamilyName, cat.Name, s, at, issues)) continue;
                it.Spec = s;

                var existing = families.Where(x => string.Equals(x.Name, it.FamilyName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (existing.Count > 0 && !readback)
                {
                    var e = existing[0];
                    if (!args.GetBool("reload")) { OpContext.Error(issues, at, RevitIssueCodes.Exists, "the project already has a family '" + e.Name + "' (id " + Kit.Id(e.Id) + "); set reload true to load the new one over it, or give another familyPrefix or familyName"); continue; }
                    if (e.IsInPlace || !e.IsEditable) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "family '" + e.Name + "' is in-place or not editable, so it cannot be reloaded"); continue; }
                    if (e.FamilyCategory is null || e.FamilyCategory.Id != cat.Id)
                    { OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "family '" + e.Name + "' is " + (e.FamilyCategory?.Name ?? "of no category") + ", not " + cat.Name + "; Revit cannot reload it with another category"); continue; }
                    it.Existing = e;
                }

                it.FinalFile = Path.Combine(p.OutputDir, it.FamilyName + ".rfa");
                if (!readback && File.Exists(it.FinalFile) && !p.Overwrite)
                { OpContext.Error(issues, at, RevitIssueCodes.Exists, it.FinalFile + " exists; set overwrite true to replace it"); continue; }
                p.Items.Add(it);
            }
            if (selected.Count == 0 && !issues.Any(i => i.IsError))
                OpContext.Error(issues, blocksAt, RevitIssueCodes.Invalid, Path.GetFileName(file) + " has no blocks to make families for");
            return p;
        }

        /// <summary>The box body as family.create args, read with family.create's rules; false when they reject it.</summary>
        private static bool BoxRecipe(OpContext ctx, string familyName, string category, FamilySpec s, string at, List<Issue> issues)
        {
            var u = ctx.Units;
            var box = new CadBoxBlock
            {
                FamilyName = familyName, Category = category, TypeName = s.TypeName, Block = s.Block, Source = s.Source, MaterialParameter = s.MaterialParameter,
                BodyInPlan = s.BodyInPlan, MinX = u.LengthFromInternal(s.MinX), MinY = u.LengthFromInternal(s.MinY), MaxX = u.LengthFromInternal(s.MaxX),
                MaxY = u.LengthFromInternal(s.MaxY), Height = u.LengthFromInternal(s.Height),
            };
            var found = new List<Issue>();
            s.Recipe = FamilyRecipe.Parse(CadBoxRecipe.Args(box, u.JobUnit), u.JobUnit, at, found);
            foreach (var i in found)
            {
                string msg = "block '" + s.Block + "': the box family: " + i.Message;
                if (i.IsError) OpContext.Error(issues, at, i.Code, msg);
                else OpContext.Warning(issues, at, i.Code, msg);
            }
            return !found.Any(i => i.IsError);
        }

        private static string SafeTemplatePath(Document doc)
        {
            try { return doc.Application.FamilyTemplatePath; }
            catch (Exception) { return "Revit's family template folder"; }
        }

        private static ImportUnit ImportUnitFor(LengthUnit u)
        {
            switch (u)
            {
                case LengthUnit.Millimetre: return ImportUnit.Millimeter;
                case LengthUnit.Centimetre: return ImportUnit.Centimeter;
                case LengthUnit.Metre: return ImportUnit.Meter;
                case LengthUnit.Inch: return ImportUnit.Inch;
                default: return ImportUnit.Foot;
            }
        }

        // ------------------------------------------------------------------ handler

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues, false);
            if (p is null) return;
            foreach (var it in p.Items)
            {
                if (it.Existing is null) continue;
                ctx.DeclareModifies(it.Existing.Id);
                foreach (var sid in it.Existing.GetFamilySymbolIds()) ctx.DeclareModifies(sid);
            }
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false);
            Kit.Require(issues);
            var doc = ctx.Doc;
            if (doc.IsModifiable) throw new OpFailedException("transaction_open", "the project has an open transaction; Revit loads a family document only outside one");
            string stage = ctx.StageFolder(p!.OutputDir);
            var familiesOut = new JsonObject();
            var typesOut = new JsonObject();
            var files = new List<string>();
            var flex = new JsonArray();
            var stashBlocks = new JsonArray();
            foreach (var it in p.Items)
            {
                var before = it.Existing is null ? new HashSet<long>() : new HashSet<long>(it.Existing.GetFamilySymbolIds().Select(ElementIds.ToLong));
                Document famDoc;
                try { famDoc = FamilyFiles.New(doc, it.Template); }
                catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("template_unusable", "Revit did not open template " + it.Template + " for block '" + it.Block.Name + "': " + e.Message); }
                Family loaded;
                FamilyBuilt built;
                string staged = Path.Combine(stage, it.FamilyName + ".rfa");
                try
                {
                    built = CadFamilyBuilder.Build(ctx, famDoc, it.Spec);
                    var entry = new JsonObject().Add("block", it.Block.Name).Add("ok", built.FlexOk);
                    foreach (var kv in built.Flex) if (kv.Key != "ok") entry.Add(kv.Key, kv.Value);
                    if (!built.FlexOk)
                        throw new OpFailedException("flex_failed", "the family for block '" + it.Block.Name + "' does not flex: " +
                            string.Join("; ", FlexProblems(built.Flex).Take(4)), new JsonObject().Add("flex", entry));
                    FamilyFiles.SaveAs(famDoc, staged);
                    loaded = Load(ctx, famDoc, it);
                    entry.Add("family", Kit.Id(loaded.Id));
                    flex.Add(entry);
                }
                finally { FamilyFiles.Close(famDoc); }
                if (!ctx.IsRehearsal) ctx.StageFile(staged, it.FinalFile, p.Overwrite);

                FamilySymbol? type = null;
                foreach (var sid in loaded.GetFamilySymbolIds())
                {
                    if (doc.GetElement(sid) is FamilySymbol fs && string.Equals(fs.Name, it.Spec.TypeName, StringComparison.Ordinal)) type = fs;
                    if (before.Contains(ElementIds.ToLong(sid))) result.Modified.Add(Kit.Id(sid));
                    else result.Created.Add(Kit.Id(sid));
                }
                if (it.Existing is null) result.Created.Add(Kit.Id(loaded.Id));
                else result.Modified.Add(Kit.Id(loaded.Id));
                familiesOut.Add(it.Block.Name, Kit.Id(loaded.Id));
                typesOut.Add(it.Block.Name, type is null ? (JsonValue)JsonValue.Null : Kit.Id(type.Id));
                files.Add(it.FinalFile);
                stashBlocks.Add(new JsonObject().Add("block", it.Block.Name).Add("index", p.Items.IndexOf(it)).Add("family", Kit.Id(loaded.Id))
                    .Add("staged", staged).Add("symbol", built.Symbol).Add("flexOk", built.FlexOk));
            }
            // Created first: a bare reference to this op resolves to the first created id, which should be a family.
            var families = result.Created.Where(id => ElementIds.TryParse(id, out var e) && doc.GetElement(e) is Family).ToList();
            result.Created.RemoveAll(families.Contains);
            result.Created.InsertRange(0, families);
            result.Outputs.Add("families", familiesOut);
            result.Outputs.Add("types", typesOut);
            result.Outputs.Add("files", JsonArray.FromStrings(files));
            result.Outputs.Add("flex", flex);
            result.Readback.Add(Stash, new JsonObject().Add("blocks", stashBlocks));
        }

        private static IEnumerable<string> FlexProblems(JsonObject flex)
        {
            if (!(flex["cases"] is JsonArray cases)) yield break;
            foreach (var c in cases.OfType<JsonObject>())
                if (c["problems"] is JsonArray ps)
                    foreach (var x in ps.OfType<JsonString>()) yield return c.GetStringOrNull("name") + ": " + x.Value;
        }

        private static Family Load(OpContext ctx, Document famDoc, Item it)
        {
            var failures = new FailureCollector();
            Family? loaded;
            try { loaded = FamilyFiles.Load(famDoc, ctx.Doc, new ExplicitLoadOptions(replace: true, familyValues: true), failures); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("load_failed", "Revit refused to load the family for block '" + it.Block.Name + "': " + e.Message); }
            foreach (var w in failures.Warnings) ctx.Job.Report.Warnings.Add(new ReportWarning("app", "loading the family for block '" + it.Block.Name + "': " + w.Text(), ctx.Op.Id));
            if (failures.Errors.Count > 0)
                throw new OpFailedException("load_failed", "Revit refused to load the family for block '" + it.Block.Name + "': " + string.Join("; ", failures.Errors.Take(3).Select(x => x.Text())),
                    new JsonObject().Add("errors", new JsonArray(failures.Errors.Select(x => (JsonValue)x.ToJson()))));
            return loaded ?? throw new OpFailedException("load_failed", "Revit did not load the family for block '" + it.Block.Name + "' into the project");
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var stash = result.Readback[Stash] as JsonObject ?? new JsonObject();
            result.Readback.Remove(Stash);
            var p = Build(ctx, args, new List<Issue>(), true);
            var blocks = new JsonArray();
            foreach (var sb in (stash["blocks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                string name = sb.GetStringOrNull("block") ?? "?";
                var rb = new JsonObject().Add("block", name);
                blocks.Add(rb);
                var it = p?.Items.FirstOrDefault(x => x.Block.Name == name);
                if (it is null) { check.Fail("block '" + name + "': the op's plan cannot be rebuilt to compare with"); continue; }
                if (!(ElementIds.TryParse(sb.GetStringOrNull("family") ?? "", out var fid) && doc.GetElement(fid) is Family family))
                { check.Fail("block '" + name + "': the family is not in the project after the op"); continue; }
                check.Text(rb, "family", family.Name, it.FamilyName, "block '" + name + "' family name");
                check.Text(rb, "category", family.FamilyCategory?.Name, it.Category.Name, "block '" + name + "' category");
                var type = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault(t => t.Name == it.Spec.TypeName);
                if (type is null) { check.Fail("block '" + name + "': the family has no type '" + it.Spec.TypeName + "'"); continue; }
                rb.Add("type", Kit.Id(type.Id));
                check.Text(rb, "cadBlock", type.LookupParameter(CadFamilies.BlockParameter)?.AsString(), it.Block.Name, "block '" + name + "' " + CadFamilies.BlockParameter);
                if (it.Spec.Body == "box")
                {
                    Size(check, rb, type, CadFamilyBuilder.Width, it.Spec.MaxX - it.Spec.MinX, u, name);
                    Size(check, rb, type, CadFamilyBuilder.Depth, it.Spec.MaxY - it.Spec.MinY, u, name);
                    Size(check, rb, type, CadFamilyBuilder.Height, it.Spec.Height, u, name);
                    check.Flag(rb, "materialParameter", type.LookupParameter(it.Spec.MaterialParameter) != null, true, "block '" + name + "' parameter " + it.Spec.MaterialParameter);
                }
                string staged = sb.GetStringOrNull("staged") ?? "";
                string file = ctx.IsRehearsal ? staged : ctx.ActualPath(it.FinalFile);
                check.Flag(rb, "fileWritten", File.Exists(file), true, "block '" + name + "' .rfa at " + file);
                check.Flag(rb, "flexOk", sb["flexOk"] is JsonBool fb && fb.Value, true, "block '" + name + "' flex test");
                if (sb["symbol"] is JsonObject sym) rb.Add("symbol", sym);
            }
            result.Readback.Add("families", blocks);
            check.Apply(result);
        }

        private static void Size(ReadbackChecker check, JsonObject into, FamilySymbol type, string param, double expectedInternal, RevitUnits u, string block)
        {
            var prm = type.LookupParameter(param);
            if (prm is null || prm.StorageType != StorageType.Double) { check.Fail("block '" + block + "': type has no length parameter " + param); return; }
            check.Length(into, param, prm.AsDouble(), u.LengthFromInternal(expectedInternal), "block '" + block + "' " + param);
        }
    }
}
