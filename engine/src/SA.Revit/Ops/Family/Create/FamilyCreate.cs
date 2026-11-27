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

namespace SA.Revit.Ops.FamilyCreation
{
    /// <summary>
    /// family.create: build a new loadable family from a template, flex-test every type, save it and load it.
    ///
    /// Application.NewFamilyDocument and the family document's LoadFamily(project, options) run only while the project
    /// has no open transaction, so the op runs inside the job's transaction group but outside any transaction
    /// (<see cref="IOutsideTransactionOp"/>), alone in its job:
    /// <list type="number">
    /// <item>Validate: reads the recipe with Core's <see cref="FamilyRecipe"/> (the rules lint applies), picks the
    /// template, refuses an existing file or project family unless overwrite (declaring the family and its types when it
    /// will be reloaded). In preflight it then builds the family in a scratch document, flex-tests it and closes it
    /// unsaved, so a recipe Revit rejects fails before anything changes.</item>
    /// <item>Execute: builds and flex-tests again; any failed type, Revit warning or error stops the op. The family is
    /// saved into the op's staging folder (published at the final path only when the job commits) and loaded with
    /// explicit options: reload only with overwrite, existing types' values as parameterValues says. A rehearsal loads
    /// inside the job's transaction group, which is rolled back, and its staged file is deleted.</item>
    /// <item>Readback: the staged or published file, the loaded family, its types and their type values against what
    /// the family document held, and the flex results.</item>
    /// </list>
    /// The family document is closed on every path.
    /// </summary>
    public sealed class FamilyCreate : OpHandler, IOutsideTransactionOp
    {
        public override string Name => "family.create";

        private const string Stash = "_stash";

        private sealed class Plan
        {
            public FamilyRecipe Recipe = null!;
            public string Template = "";
            public bool GenericFallback;
            public BuiltInCategory? SetCategory;
            public string CategoryLabel = "";
            public string Final = "";
            public DateTime? FinalWrite;
            public long? FinalLength;
            public Family? Existing;
        }

        private sealed class Built
        {
            public List<Issue> Issues = new List<Issue>();
            public List<FlexOutcome> Flex = new List<FlexOutcome>();
            public FamilyBuilder? Builder;
            public List<string> Warnings = new List<string>();
        }

        // ------------------------------------------------------------------ plan

        private static Plan? MakePlan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var recipe = FamilyRecipe.Parse(args.Raw, ctx.Units.JobUnit, args.Path, issues);
            if (Kit.HasErrors(issues)) return null;
            var p = new Plan { Recipe = recipe };

            Category? cat = null;
            if (recipe.Category != null)
            {
                cat = Lookup.Category(doc, recipe.Category, out string? cerr);
                if (cat is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, cerr!); return null; }
                if (cat.CategoryType != CategoryType.Model || cat.Parent != null)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotAllowed, "'" + cat.Name + "' is not a top-level model category; a loadable family needs one (Furniture, Plumbing Fixtures, Casework...)");
                    return null;
                }
                var bic = ElementIds.ToBuiltInCategory(cat.Id);
                if (bic is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotAllowed, "'" + cat.Name + "' is not a built-in category"); return null; }
                p.CategoryLabel = cat.Name;
                p.SetCategory = bic;
            }

            bool metric = !Length.IsImperial(ctx.Units.JobUnit);
            if (recipe.Template != null)
            {
                if (!File.Exists(recipe.Template)) { OpContext.Error(issues, ctx.FieldPath(args, "template"), RevitIssueCodes.NotFound, "template " + recipe.Template + " does not exist"); return null; }
                p.Template = recipe.Template;
            }
            else
            {
                var all = FamilyTemplates.All(doc.Application);
                var found = FamilyTemplates.ForCategory(all, cat!.Name, metric);
                if (found != null)
                {
                    p.Template = found;
                    p.SetCategory = null;
                }
                else
                {
                    var generic = FamilyTemplates.GenericModel(all, metric);
                    if (generic is null)
                    {
                        OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, "no family template for '" + cat.Name + "' and no Generic Model template to fall back on (" +
                            FamilyTemplates.Listing(all) + "); give template as an absolute path");
                        return null;
                    }
                    p.Template = generic;
                    p.GenericFallback = true;
                    OpContext.Warning(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.Unchecked, "no family template for '" + cat.Name + "' was found; the family is made from " +
                        Path.GetFileName(generic) + " and its category changed to '" + cat.Name + "' (category-specific settings of a dedicated template are missing)");
                }
            }

            string dir = recipe.OutputDir ?? Path.Combine(ctx.OutputDir, "families");
            if (!Path.IsPathRooted(dir)) { OpContext.Error(issues, ctx.FieldPath(args, "outputDir"), RevitIssueCodes.Invalid, "outputDir must be an absolute path"); return null; }
            p.Final = Path.Combine(dir, recipe.Name + ".rfa");
            if (File.Exists(p.Final))
            {
                if (!recipe.Overwrite) { OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, p.Final + " exists; set overwrite true to replace it"); return null; }
                var fi = new FileInfo(p.Final);
                p.FinalWrite = fi.LastWriteTimeUtc;
                p.FinalLength = fi.Length;
            }

            var same = FamilyFiles.Named(doc, recipe.Name);
            if (same.Count > 0 && recipe.Load)
            {
                var fam = same[0];
                if (fam.Name != recipe.Name)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, "the project has family '" + fam.Name + "', which differs from '" + recipe.Name + "' only by case; use the same name or another one");
                    return null;
                }
                if (!recipe.Overwrite)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, "the project already has family '" + fam.Name + "'; set overwrite true to reload it (parameterValues says whose type values win), or choose another name");
                    return null;
                }
                if (fam.IsInPlace) { OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.NotAllowed, "'" + fam.Name + "' is an in-place family in the project; a loaded family cannot replace it"); return null; }
                if (cat != null && fam.FamilyCategory != null && fam.FamilyCategory.Id != cat.Id)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.Mismatch, "the project's family '" + fam.Name + "' is in category '" + fam.FamilyCategory.Name + "', not '" + cat.Name + "'; Revit cannot reload it across categories");
                    return null;
                }
                p.Existing = fam;
                ctx.DeclareModifies(fam.Id);
                foreach (var sid in fam.GetFamilySymbolIds()) ctx.DeclareModifies(sid);
            }
            else if (same.Count > 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "load"), RevitIssueCodes.Unchecked, "the project already has a family named '" + same[0].Name + "'; load is false, so it is left as it is");
            return p;
        }

        // ------------------------------------------------------------------ build in the family document

        private static Built Build(OpContext ctx, Document famDoc, Plan p)
        {
            var b = new Built();
            var collector = new FailureCollector();
            using (var t = new Transaction(famDoc, "SA family.create"))
            {
                if (t.Start() != TransactionStatus.Started) throw new OpFailedException("transaction_failed", "Revit refused to start a transaction in the family");
                collector.Install(t);
                var builder = new FamilyBuilder(famDoc, p.Recipe, ctx.Units, b.Issues, ctx.Op.ArgsPath);
                b.Builder = builder;
                builder.Build(p.SetCategory, p.CategoryLabel);
                if (Kit.HasErrors(b.Issues)) { t.RollBack(); return b; }
                var status = t.Commit();
                foreach (var w in collector.Warnings)
                    b.Issues.Add(new Issue(ctx.Op.ArgsPath, RevitIssueCodes.Invalid, "Revit warned while building the family: " + w.Text()));
                if (status != TransactionStatus.Committed || collector.Errors.Count > 0)
                {
                    b.Issues.Add(new Issue(ctx.Op.ArgsPath, RevitIssueCodes.Invalid, "Revit rolled the family back (" + status + "): " +
                        (collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Take(3).Select(x => x.Text())) : "no failure message")));
                    return b;
                }
            }
            if (Kit.HasErrors(b.Issues)) return b;

            var fm = famDoc.FamilyManager;
            var axes = new List<FlexAxis>();
            if (p.Recipe.Flex != null)
                foreach (var kv in p.Recipe.Flex.Axes())
                    if (FamilyDoc.Find(fm, kv.Value, out _) is FamilyParameter fp) axes.Add(new FlexAxis { Axis = kv.Key, Parameter = fp });
            double tol = ctx.Units.LengthToInternal(p.Recipe.Flex?.Tolerance ?? ctx.Units.LengthTolerance);
            b.Flex = FlexTest.Run(famDoc, p.Recipe.Types.Select(x => x.Name).ToList(), axes, tol, ctx.Units);
            foreach (var o in b.Flex.Where(o => !o.Ok))
                b.Issues.Add(new Issue(JsonPath.Key(ctx.Op.ArgsPath, "types"), RevitIssueCodes.Invalid, "type '" + o.Type + "' failed the flex test: " + string.Join("; ", o.Problems.Take(4))));

            using (var t = new Transaction(famDoc, "SA family.create first type"))
            {
                if (t.Start() == TransactionStatus.Started)
                {
                    var first = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name == p.Recipe.Types[0].Name);
                    if (first != null) fm.CurrentType = first;
                    t.Commit();
                }
            }
            return b;
        }

        private static Document Open(OpContext ctx, Plan p)
        {
            if (ctx.Doc.IsModifiable) throw new OpFailedException("transaction_open", "the project has an open transaction; Revit makes a family document only outside one");
            return FamilyFiles.New(ctx.Doc, p.Template);
        }

        // ------------------------------------------------------------------ handler

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = MakePlan(ctx, args, issues);
            if (p is null || !ctx.IsPreflight || ctx.Doc.IsModifiable) return;
            Document? famDoc = null;
            try
            {
                famDoc = Open(ctx, p);
                var b = Build(ctx, famDoc, p);
                issues.AddRange(b.Issues);
            }
            catch (Exception e) { OpContext.Error(issues, ctx.Op.ArgsPath, RevitIssueCodes.Invalid, "a trial build of the family failed: " + e.Message); }
            finally { FamilyFiles.Close(famDoc); }
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = MakePlan(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var r = p!.Recipe;
            var idsBefore = p.Existing?.GetFamilySymbolIds().ToList() ?? new List<ElementId>();
            var symbolsBefore = new HashSet<long>(idsBefore.Select(ElementIds.ToLong));
            var typesBefore = new HashSet<string>(idsBefore.Select(id => doc.GetElement(id)?.Name ?? ""), StringComparer.Ordinal);
            Document? famDoc = null;
            try
            {
                famDoc = Open(ctx, p);
                var b = Build(ctx, famDoc, p);
                foreach (var w in b.Issues.Where(i => !i.IsError)) ctx.Warn(w.Message);
                var errors = b.Issues.Where(i => i.IsError).ToList();
                if (errors.Count > 0)
                    throw new OpFailedException("family_build_failed", string.Join("; ", errors.Take(5).Select(i => i.Message)),
                        new JsonObject().Add("issues", Issue.ToJson(errors)).Add("flex", new JsonArray(b.Flex.Select(o => (JsonValue)o.Json))));
                if (famDoc.IsModifiable) throw new OpFailedException("transaction_open", "the family document still has an open transaction");

                string stage = ctx.StageFolder(Path.GetDirectoryName(p.Final)!);
                string staged = Path.Combine(stage, r.Name + ".rfa");
                FamilyFiles.SaveAs(famDoc, staged);
                ctx.StageFile(staged, p.Final, r.Overwrite, p.FinalWrite, p.FinalLength);

                var stash = new JsonObject()
                    .Add("template", p.Template).Add("genericFallback", p.GenericFallback)
                    .Add("familyValues", FamilyValues(famDoc, r, u))
                    .Add("flex", new JsonArray(b.Flex.Select(o => (JsonValue)o.Json)))
                    .Add("typesBefore", JsonArray.FromStrings(typesBefore))
                    .Add("createdMaterials", JsonArray.FromStrings(b.Builder?.CreatedMaterials ?? new List<string>()))
                    .Add("createdSubcategories", JsonArray.FromStrings(b.Builder?.CreatedSubcategories ?? new List<string>()))
                    .Add("counts", new JsonObject().Add("forms", b.Builder?.Forms.Count ?? 0).Add("dimensions", b.Builder?.Dimensions ?? 0)
                        .Add("locks", b.Builder?.Locks ?? 0).Add("symbolicLines", b.Builder?.Symbols ?? 0));

                result.Outputs.Add("familyName", r.Name);
                result.Outputs.Add("file", p.Final);
                result.Outputs.Add("types", JsonArray.FromStrings(r.Types.Select(t => t.Name)));
                result.Outputs.Add("flex", new JsonArray(b.Flex.Select(o => (JsonValue)o.Json)));
                if (!ctx.IsRehearsal) ctx.AddArtifact("family", p.Final, null);

                if (r.Load)
                {
                    var failures = new FailureCollector();
                    var loaded = FamilyFiles.Load(famDoc, doc, new ExplicitLoadOptions(r.Overwrite, r.ParameterValues == "family"), failures);
                    foreach (var w in failures.Warnings) ctx.Job.Report.Warnings.Add(new ReportWarning("app", w.Text(), ctx.Op.Id));
                    if (failures.Errors.Count > 0)
                        throw new OpFailedException("load_failed", "Revit refused to load the family: " + string.Join("; ", failures.Errors.Take(3).Select(x => x.Text())),
                            new JsonObject().Add("errors", new JsonArray(failures.Errors.Select(x => (JsonValue)x.ToJson()))));
                    if (loaded is null) throw new OpFailedException("load_failed", "Revit did not load family '" + r.Name + "' into the project");
                    result.Outputs.Add("family", Kit.Id(loaded.Id));
                    if (p.Existing != null) result.Modified.Add(Kit.Id(loaded.Id));
                    else result.Created.Add(Kit.Id(loaded.Id));
                    var ids = new JsonObject();
                    foreach (var sid in loaded.GetFamilySymbolIds())
                    {
                        if (symbolsBefore.Contains(ElementIds.ToLong(sid))) result.Modified.Add(Kit.Id(sid));
                        else result.Created.Add(Kit.Id(sid));
                        if (doc.GetElement(sid) is FamilySymbol fs) ids.Set(fs.Name, Kit.Id(sid));
                    }
                    result.Outputs.Add("typeIds", ids);
                }
                result.Readback.Add(Stash, stash);
            }
            finally { FamilyFiles.Close(famDoc); }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var stash = result.Readback[Stash] as JsonObject ?? new JsonObject();
            result.Readback.Remove(Stash);
            var rb = result.Readback;
            var issues = new List<Issue>();
            var recipe = FamilyRecipe.Parse(args.Raw, u.JobUnit, args.Path, issues);

            string final = result.Outputs.GetStringOrNull("file") ?? "";
            string actual = ctx.ActualPath(final);
            long bytes = File.Exists(actual) ? new FileInfo(actual).Length : 0;
            rb.Add("file", new JsonObject().Add("path", final).Add("bytes", bytes).Add("staged", !string.Equals(actual, final, StringComparison.OrdinalIgnoreCase)));
            if (bytes <= 0) check.Fail("the family file " + final + " is missing or empty");
            rb.Add("template", stash["template"]?.DeepClone() ?? JsonValue.Null);
            rb.Add("genericFallback", stash["genericFallback"]?.DeepClone() ?? false);
            rb.Add("built", stash["counts"]?.DeepClone() ?? new JsonObject());
            rb.Add("createdMaterials", stash["createdMaterials"]?.DeepClone() ?? new JsonArray());
            rb.Add("createdSubcategories", stash["createdSubcategories"]?.DeepClone() ?? new JsonArray());

            var flex = stash["flex"] as JsonArray ?? new JsonArray();
            rb.Add("flex", flex.DeepClone());
            if (flex.Count != recipe.Types.Count) check.Fail("the flex test covered " + flex.Count + " of " + recipe.Types.Count + " types");
            foreach (var o in flex.OfType<JsonObject>().Where(o => !(o["ok"] is JsonBool ok && ok.Value)))
                check.Fail("type '" + o.GetStringOrNull("type") + "' did not pass the flex test");

            var familyValues = stash["familyValues"] as JsonObject ?? new JsonObject();
            if (!recipe.Load)
            {
                rb.Add("loaded", false);
                rb.Add("types", familyValues.DeepClone());
                rb.Add("source", "the family document (load is false)");
                check.Apply(result);
                return;
            }

            string? idText = result.Outputs.GetStringOrNull("family");
            if (idText is null || !ElementIds.TryParse(idText, out var fid) || !(doc.GetElement(fid) is Family family))
            {
                check.Fail("the family is not in the project after the load");
                check.Apply(result);
                return;
            }
            rb.Add("loaded", true);
            rb.Add("family", new JsonObject().Add("id", idText).Add("name", family.Name).Add("category", family.FamilyCategory?.Name ?? ""));
            check.Text(rb, "familyName", family.Name, recipe.Name, "family name");
            if (recipe.Category != null && family.FamilyCategory != null && Lookup.Category(doc, recipe.Category, out _) is Category wantCat && wantCat.Id != family.FamilyCategory.Id)
                check.Fail("the family is in category '" + family.FamilyCategory.Name + "', not '" + wantCat.Name + "'");

            var symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToDictionary(s => s.Name, StringComparer.Ordinal);
            var before = new HashSet<string>((stash["typesBefore"] as JsonArray ?? new JsonArray()).OfType<JsonString>().Select(s => s.Value), StringComparer.Ordinal);
            bool keepProject = recipe.ParameterValues == "project";
            var types = new JsonObject();
            foreach (var rt in recipe.Types)
            {
                if (!symbols.TryGetValue(rt.Name, out var sym)) { check.Fail("type '" + rt.Name + "' is not in the project after the load"); continue; }
                var values = new JsonObject();
                var expected = familyValues[rt.Name] as JsonObject ?? new JsonObject();
                foreach (var prm in recipe.Parameters.Where(x => !x.Instance))
                {
                    var sp = ParamAccess.Find(sym, prm.Name, out _);
                    if (sp is null) { check.Fail("type '" + rt.Name + "' has no parameter '" + prm.Name + "' in the project"); continue; }
                    var got = FamilyDoc.Read(doc, sp, u);
                    values.Add(prm.Name, got);
                    var want = expected[prm.Name];
                    if (want is null || (keepProject && before.Contains(rt.Name))) continue;
                    if (!Same(got, want, prm.Spec == "length" ? u.LengthTolerance : 1e-6))
                        check.Fail("type '" + rt.Name + "': '" + prm.Name + "' is " + got.ToJson() + " in the project but " + want.ToJson() + " in the family");
                }
                types.Add(rt.Name, new JsonObject().Add("id", Kit.Id(sym.Id)).Add("values", values));
            }
            rb.Add("types", types);
            var extra = symbols.Keys.Where(n => !recipe.Types.Any(t => t.Name == n)).ToList();
            if (extra.Count > 0)
            {
                rb.Add("otherTypes", JsonArray.FromStrings(extra));
                ctx.Warn("the project's family also has type(s) the recipe does not list (kept from before the reload): " + string.Join(", ", extra.Take(10)));
            }
            rb.Add("source", "the project's family types, against the values the family document held");
            check.Apply(result);
        }

        // ------------------------------------------------------------------ reading

        /// <summary>Every declared parameter's value in every type, read from the family document.</summary>
        private static JsonObject FamilyValues(Document famDoc, FamilyRecipe r, RevitUnits u)
        {
            var fm = famDoc.FamilyManager;
            var o = new JsonObject();
            foreach (var rt in r.Types)
            {
                var ft = fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == rt.Name);
                if (ft is null) continue;
                var values = new JsonObject();
                foreach (var p in r.Parameters)
                    if (FamilyDoc.Find(fm, p.Name, out _) is FamilyParameter prm) values.Add(p.Name, FamilyDoc.Read(famDoc, ft, prm, u));
                o.Add(rt.Name, values);
            }
            return o;
        }

        private static bool Same(JsonValue a, JsonValue b, double tol)
        {
            if (a is JsonNumber x && b is JsonNumber y) return Math.Abs(x.Value - y.Value) <= Math.Max(tol, 1e-9 * Math.Max(Math.Abs(x.Value), Math.Abs(y.Value)));
            return JsonValue.DeepEquals(a, b);
        }
    }
}
