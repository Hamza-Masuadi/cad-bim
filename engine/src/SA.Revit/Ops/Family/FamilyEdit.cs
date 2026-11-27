using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Host;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FamilyEditing
{
    /// <summary>
    /// family.edit: change a loaded family's parameters, formulas, types and type values, load it back and read it back.
    ///
    /// Revit allows Document.EditFamily and the family document's LoadFamily(project, options) only while the project
    /// has no open transaction (RevitAPI.xml: "may not be called if the document is currently modifiable"; LoadFamily
    /// throws when "the target document is modifiable"). A transaction group does not make a document modifiable, so the
    /// op runs inside the job's group but outside any transaction (<see cref="IOutsideTransactionOp"/>), alone in its job:
    /// <list type="number">
    /// <item>Validate (preflight): finds the family, refuses in-place and non-editable ones, declares the family and every
    /// type for the ownership check, then opens a copy with EditFamily, makes every change in a family transaction that is
    /// rolled back, and closes the copy without saving. A bad name, value or formula fails validation before anything runs.</item>
    /// <item>Execute: reads the "before" values (type parameters from the project's types, the rest from the copy), makes
    /// the changes in a new copy and commits them there. A rehearsal reads everything back from the copy and closes it
    /// without loading, so the project is never touched. A commit loads the copy into the project with
    /// OnFamilyFound overwriteParameterValues true (otherwise the project keeps its old type values) and shared nested
    /// families kept as the project has them (FamilySource.Project), then closes the copy. Revit's failures during the
    /// load are taken through Application.FailuresProcessing: warnings are recorded, errors roll the load back. A
    /// request that changes nothing loads nothing and reports the family unchanged.</item>
    /// <item>Readback (commit): type parameter values from the project's types; instance parameter values (the
    /// defaults new instances get), formulas and added parameters from a fresh EditFamily copy of the loaded family. Every
    /// type that existed before must still exist. Type values the job did not ask for that changed are listed in
    /// otherChanges with a warning (formulas legitimately move them).</item>
    /// </list>
    /// The load is the only change to the project, so the job's group holds it as one undo step. Types are never renamed
    /// or deleted. Parameters that hold element references other than materials are refused (their ids in the family
    /// copy are not the project's).
    /// </summary>
    public sealed class FamilyEdit : OpHandler, IOutsideTransactionOp
    {
        public override string Name => "family.edit";

        private sealed class NewParam
        {
            public string Name = "";
            public string Spec = "";
            public string Group = "";
            public bool Instance;
            public string Path = "";
        }

        private sealed class TypeEdit
        {
            public string Name = "";
            public bool Create;
            public string? CopyFrom;
            public string Path = "";
            public bool InProject;
            public List<KeyValuePair<string, JsonValue>> Values = new List<KeyValuePair<string, JsonValue>>();
            public Dictionary<string, string> ValuePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private sealed class Plan
        {
            public Family Family = null!;
            public List<NewParam> Add = new List<NewParam>();
            public List<KeyValuePair<string, string?>> Formulas = new List<KeyValuePair<string, string?>>();
            public string FormulasPath = "";
            public List<TypeEdit> Types = new List<TypeEdit>();
            public Dictionary<string, FamilySymbol> Symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
        }

        private sealed class ReloadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }

        // ------------------------------------------------------------------ plan (project side)

        private static Family? FindFamily(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            string at = ctx.FieldPath(args, "family");
            string name = args.GetString("family").Trim();
            Category? cat = null;
            if (args.GetStringOrNull("category") is string cn)
            {
                cat = Lookup.Category(doc, cn, out string? cerr);
                if (cat is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, cerr!); return null; }
            }
            if (name.All(char.IsDigit) && ElementIds.TryParse(name, out var id))
            {
                if (doc.GetElement(id) is Family byId) return byId;
                OpContext.Error(issues, at, RevitIssueCodes.NotFound, "element " + name + " is not a loaded family");
                return null;
            }
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => cat is null || (f.FamilyCategory != null && f.FamilyCategory.Id == cat.Id)).ToList();
            var hits = families.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal)).ToList();
            if (hits.Count == 0) hits = families.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, hits.Count + " loaded families are named '" + name + "': " +
                    string.Join(", ", hits.Take(6).Select(f => (f.FamilyCategory?.Name ?? "?") + " (id " + Kit.Id(f.Id) + ")")) + "; give category or the family's id");
                return null;
            }
            var system = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .FirstOrDefault(t => !(t is FamilySymbol) && string.Equals(t.FamilyName, name, StringComparison.OrdinalIgnoreCase));
            if (system != null)
                OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "'" + system.FamilyName + "' is a system family (" + (system.Category?.Name ?? system.GetType().Name) +
                    "); Revit has no family document for it. Change its types with type.duplicate or param.set");
            else
                OpContext.Error(issues, at, RevitIssueCodes.NotFound, "no loaded family '" + name + "'" + Lookup.Suggest(name, families.Select(f => f.Name).ToList()) +
                    ". A family nested in another family is edited only when it is shared and so loaded in the project itself");
            return null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            if (ctx.Job.Job.Ops.Count != 1)
                OpContext.Error(issues, ctx.Op.Path + ".op", JobValidator.OpNotAlone, "family.edit must be the only op in its job");
            var family = FindFamily(ctx, args, issues);
            if (family is null) return null;
            string at = ctx.FieldPath(args, "family");
            if (family.IsInPlace) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "'" + family.Name + "' is an in-place family; family.edit edits loadable families only"); return null; }
            if (!family.IsEditable) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "Revit marks '" + family.Name + "' as not editable (an internal or non-saveable family)"); return null; }

            var p = new Plan { Family = family, FormulasPath = ctx.FieldPath(args, "formulas") };
            foreach (var sid in family.GetFamilySymbolIds())
                if (doc.GetElement(sid) is FamilySymbol s && !p.Symbols.ContainsKey(s.Name)) p.Symbols[s.Name] = s;

            var seenParams = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in args.GetObjectList("addParameters"))
            {
                var np = new NewParam { Name = a.GetString("name").Trim(), Spec = a.GetString("spec"), Group = a.GetString("group"), Instance = a.GetBool("instance"), Path = a.Path };
                if (np.Name.Length == 0 || Kit.BadTypeName(np.Name) is string bad && (bad.Contains("does not allow") || bad.Contains("spaces")))
                    OpContext.Error(issues, a.Path + ".name", RevitIssueCodes.Invalid, "parameter name '" + np.Name + "' is not usable");
                else if (!seenParams.Add(np.Name)) OpContext.Error(issues, a.Path + ".name", RevitIssueCodes.Invalid, "parameter '" + np.Name + "' is listed twice");
                if (np.Instance && np.Spec == "material")
                    OpContext.Warning(issues, a.Path + ".instance", RevitIssueCodes.Unchecked, "an instance material parameter's value is set per placed element; type values only set the default for new instances");
                p.Add.Add(np);
            }
            foreach (var kv in args.GetMap("formulas"))
                p.Formulas.Add(new KeyValuePair<string, string?>(kv.Key, kv.Value is JsonString fs && fs.Value.Trim().Length > 0 ? fs.Value : null));

            var all = args.GetMap("values");
            var listed = new Dictionary<string, TypeEdit>(StringComparer.Ordinal);
            var newNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in args.GetObjectList("types"))
            {
                var te = new TypeEdit { Name = t.GetString("name"), Create = t.GetBool("create"), CopyFrom = t.GetStringOrNull("copyFrom"), Path = t.Path };
                te.InProject = p.Symbols.ContainsKey(te.Name);
                if (listed.ContainsKey(te.Name)) { OpContext.Error(issues, t.Path + ".name", RevitIssueCodes.Invalid, "type '" + te.Name + "' is listed twice"); continue; }
                if (!te.InProject)
                {
                    var caseOnly = p.Symbols.Keys.FirstOrDefault(n => string.Equals(n, te.Name, StringComparison.OrdinalIgnoreCase));
                    if (caseOnly != null) OpContext.Error(issues, t.Path + ".name", RevitIssueCodes.Mismatch, "the family has a type '" + caseOnly + "' (names differ only by case); use that exact name");
                    else if (!te.Create) OpContext.Error(issues, t.Path + ".name", RevitIssueCodes.NotFound, "family '" + family.Name + "' has no type '" + te.Name + "'" +
                        Lookup.Suggest(te.Name, p.Symbols.Keys.ToList()) + "; set create:true to make it");
                    else if (Kit.BadTypeName(te.Name) is string badName) OpContext.Error(issues, t.Path + ".name", RevitIssueCodes.Invalid, badName);
                    if (te.CopyFrom != null && !p.Symbols.ContainsKey(te.CopyFrom) && !newNames.Contains(te.CopyFrom))
                        OpContext.Error(issues, t.Path + ".copyFrom", RevitIssueCodes.NotFound, "no type '" + te.CopyFrom + "' to copy" + Lookup.Suggest(te.CopyFrom, p.Symbols.Keys.ToList()));
                    newNames.Add(te.Name);
                }
                else if (te.CopyFrom != null)
                    OpContext.Warning(issues, t.Path + ".copyFrom", RevitIssueCodes.Unchecked, "type '" + te.Name + "' exists, so copyFrom is ignored");
                foreach (var kv in all) { te.Values.Add(kv); te.ValuePaths[kv.Key] = JsonPath.Key(ctx.FieldPath(args, "values"), kv.Key); }
                foreach (var kv in t.GetMap("values"))
                {
                    te.Values.RemoveAll(x => x.Key == kv.Key);
                    te.Values.Add(kv);
                    te.ValuePaths[kv.Key] = JsonPath.Key(t.Path + ".values", kv.Key);
                }
                listed[te.Name] = te;
                p.Types.Add(te);
            }
            if (all.Count > 0)
                foreach (var name in p.Symbols.Keys.OrderBy(n => n, StringComparer.Ordinal).Where(n => !listed.ContainsKey(n)))
                {
                    var te = new TypeEdit { Name = name, InProject = true, Path = ctx.FieldPath(args, "values") };
                    foreach (var kv in all) { te.Values.Add(kv); te.ValuePaths[kv.Key] = JsonPath.Key(ctx.FieldPath(args, "values"), kv.Key); }
                    p.Types.Add(te);
                }
            if (p.Symbols.Count == 0 && p.Types.All(t => !t.Create) && (p.Formulas.Count > 0 || p.Types.Count > 0))
                OpContext.Error(issues, at, RevitIssueCodes.Invalid, "'" + family.Name + "' has no types in the project; create one in types to set values or formulas");

            ctx.DeclareModifies(family.Id);
            foreach (var s in p.Symbols.Values) ctx.DeclareModifies(s.Id);
            return Kit.HasErrors(issues) ? null : p;
        }

        // ------------------------------------------------------------------ changes in the family copy

        /// <summary>Makes every change of the plan in the open family transaction. Problems become issues; nothing throws.</summary>
        private static void Apply(Document famDoc, Plan p, RevitUnits u, List<Issue> issues)
        {
            var fm = famDoc.FamilyManager;
            foreach (var np in p.Add)
            {
                var existing = FamilyDoc.Find(fm, np.Name, out _);
                var spec = FamilyDoc.SpecFor(np.Spec);
                if (existing != null)
                {
                    if (!ParameterApi.SameSpec(FamilyDoc.Spec(existing), spec) || existing.IsInstance != np.Instance)
                        OpContext.Error(issues, np.Path, RevitIssueCodes.Mismatch, "the family already has '" + np.Name + "' as a " + (existing.IsInstance ? "instance" : "type") + " " +
                            ParameterApi.SpecLabel(FamilyDoc.Spec(existing)) + " parameter; requested a " + (np.Instance ? "instance" : "type") + " " + ParameterApi.SpecLabel(spec) + " parameter");
                    continue;
                }
                var group = FamilyDoc.GroupFor(np.Group);
                if (!fm.IsUserAssignableParameterGroup(group)) { OpContext.Error(issues, np.Path + ".group", RevitIssueCodes.Invalid, "Revit does not allow new parameters in group '" + np.Group + "' in this family"); continue; }
                try
                {
                    if (fm.AddParameter(np.Name, group, spec, np.Instance) is null)
                        OpContext.Error(issues, np.Path, RevitIssueCodes.Invalid, "Revit did not add parameter '" + np.Name + "'");
                }
                catch (Exception e) { OpContext.Error(issues, np.Path, RevitIssueCodes.Invalid, "Revit refused parameter '" + np.Name + "': " + e.Message); }
            }

            if (p.Formulas.Count > 0 && fm.CurrentType is null && FamilyDoc.TypeNames(fm).Count > 0)
                fm.CurrentType = FamilyDoc.Type(fm, FamilyDoc.TypeNames(fm)[0]);
            foreach (var kv in p.Formulas)
            {
                string at = JsonPath.Key(p.FormulasPath, kv.Key);
                var prm = FamilyDoc.Find(fm, kv.Key, out string? err);
                if (prm is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, err!); continue; }
                if (FamilyDoc.Canonical(prm.Formula) == FamilyDoc.Canonical(kv.Value)) continue;
                if (kv.Value != null && !prm.CanAssignFormula) { OpContext.Error(issues, at, RevitIssueCodes.NotAllowed, "'" + kv.Key + "' cannot take a formula"); continue; }
                if (fm.CurrentType is null) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "the family has no type, and Revit sets formulas only with a current type"); continue; }
                try { fm.SetFormula(prm, kv.Value); }
                catch (Exception e) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "Revit refused the formula for '" + kv.Key + "': " + e.Message); }
            }

            foreach (var te in p.Types)
            {
                var ft = FamilyDoc.Type(fm, te.Name);
                if (ft is null)
                {
                    if (te.InProject) { OpContext.Error(issues, te.Path, RevitIssueCodes.NotFound, "type '" + te.Name + "' is in the project but not in the family Revit opened for editing"); continue; }
                    var names = FamilyDoc.TypeNames(fm);
                    var src = te.CopyFrom != null ? FamilyDoc.Type(fm, te.CopyFrom) : names.Count > 0 ? FamilyDoc.Type(fm, names[0]) : null;
                    try
                    {
                        if (src != null) fm.CurrentType = src;
                        ft = fm.NewType(te.Name);
                    }
                    catch (Exception e) { OpContext.Error(issues, te.Path + ".name", RevitIssueCodes.Invalid, "Revit did not create type '" + te.Name + "': " + e.Message); continue; }
                }
                else fm.CurrentType = ft;
                if (ft is null) continue;
                foreach (var kv in te.Values)
                {
                    string at = te.ValuePaths.TryGetValue(kv.Key, out var vp) ? vp : te.Path;
                    var prm = FamilyDoc.Find(fm, kv.Key, out string? err);
                    if (prm is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, err!); continue; }
                    if (prm.IsDeterminedByFormula) { OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "'" + kv.Key + "' is set by its formula (" + prm.Formula + "); remove the formula in formulas to set values"); continue; }
                    if (prm.IsReporting) { OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "'" + kv.Key + "' is a reporting parameter (measured from the geometry)"); continue; }
                    if (prm.IsReadOnly) { OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "'" + kv.Key + "' is read-only"); continue; }
                    if (!FamilyDoc.TryPrepare(famDoc, prm, kv.Value, u, out var pv, out string perr)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "'" + kv.Key + "' (type '" + te.Name + "'): " + perr); continue; }
                    if (FamilyDoc.Matches(ft, prm, pv, u)) continue;
                    try { FamilyDoc.Set(fm, prm, pv); }
                    catch (Exception e) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "Revit refused '" + kv.Key + "' = " + kv.Value.ToJson() + " in type '" + te.Name + "': " + e.Message); }
                }
            }
        }

        private static Document Open(OpContext ctx, Family family)
        {
            if (ctx.Doc.IsModifiable) throw new OpFailedException("transaction_open", "the project has an open transaction; Revit opens a family for editing only outside one");
            return ctx.Doc.EditFamily(family) ?? throw new OpFailedException("edit_failed", "Revit did not open family '" + family.Name + "' for editing");
        }

        private static void Close(Document famDoc) => OutsideDocuments.Close(famDoc);

        /// <summary>Runs the plan in a family transaction; commits it when <paramref name="keep"/>, else rolls it back.</summary>
        private static List<Issue> Edit(OpContext ctx, Document famDoc, Plan p, bool keep)
        {
            var issues = new List<Issue>();
            var collector = new FailureCollector();
            using (var t = new Transaction(famDoc, "SA family.edit"))
            {
                if (t.Start() != TransactionStatus.Started) throw new OpFailedException("transaction_failed", "Revit refused to start a transaction in the family");
                collector.Install(t);
                try { Apply(famDoc, p, ctx.Units, issues); }
                catch (Exception e) { issues.Add(new Issue(ctx.Op.ArgsPath, RevitIssueCodes.Invalid, "editing the family failed: " + e.GetType().Name + ": " + e.Message)); }
                if (!keep || issues.Any(i => i.IsError))
                {
                    t.RollBack();
                    return issues;
                }
                var status = t.Commit();
                foreach (var w in collector.Warnings) ctx.Job.Report.Warnings.Add(new ReportWarning("app", "in the family: " + w.Text(), ctx.Op.Id));
                if (status != TransactionStatus.Committed || collector.Errors.Count > 0)
                    issues.Add(new Issue(ctx.Op.ArgsPath, RevitIssueCodes.Invalid, "Revit rolled the family change back (" + status + "): " +
                        (collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Take(3).Select(x => x.Text())) : "no failure message")));
            }
            return issues;
        }

        // ------------------------------------------------------------------ handler

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p is null || !ctx.IsPreflight || ctx.Doc.IsModifiable) return;
            Document famDoc;
            try { famDoc = Open(ctx, p.Family); }
            catch (Exception e) { OpContext.Error(issues, ctx.FieldPath(args, "family"), RevitIssueCodes.Invalid, "Revit did not open '" + p.Family.Name + "' for editing: " + e.Message); return; }
            try
            {
                if (FamilyDoc.IsShared(famDoc))
                    OpContext.Warning(issues, ctx.FieldPath(args, "family"), RevitIssueCodes.Unchecked, "'" + p.Family.Name + "' is a shared family: the project holds one copy for every family that nests it, " +
                        "so this edit changes it for all of them; reloading one of those families later may ask which copy to keep");
                issues.AddRange(Edit(ctx, famDoc, p, keep: false));
            }
            finally { Close(famDoc); }
        }

        private const string Stash = "_stash";

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var family = p!.Family;
            var symbolsBefore = new HashSet<long>(family.GetFamilySymbolIds().Select(ElementIds.ToLong));
            var stash = new JsonObject().Add("otherBefore", AllTypeValues(doc, p, u));
            var famDoc = Open(ctx, family);
            try
            {
                bool shared = FamilyDoc.IsShared(famDoc);
                var before = Snapshot(doc, famDoc, p, u, project: true);
                var errors = Edit(ctx, famDoc, p, keep: true).Where(i => i.IsError).ToList();
                if (errors.Count > 0)
                    throw new OpFailedException("family_edit_failed", string.Join("; ", errors.Take(5).Select(i => i.Path + ": " + i.Message)), new JsonObject().Add("issues", Issue.ToJson(errors)));
                var afterInCopy = Snapshot(doc, famDoc, p, u, project: false);
                bool changed = !JsonValue.DeepEquals(Comparable(before), Comparable(afterInCopy)) || p.Types.Any(t => !t.InProject);
                stash.Add("before", before).Add("shared", shared).Add("changed", changed);

                result.Outputs.Add("family", Kit.Id(family.Id));
                if (ctx.IsRehearsal || !changed)
                {
                    var mismatches = new List<string>();
                    stash.Add("copy", ReadCopy(famDoc, p, u, mismatches)).Add("mismatches", JsonArray.FromStrings(mismatches));
                    if (!changed) result.Unchanged.Add(Kit.Id(family.Id));
                    else
                    {
                        result.Modified.Add(Kit.Id(family.Id));
                        foreach (var s in p.Symbols.Values) result.Modified.Add(Kit.Id(s.Id));
                    }
                    result.Outputs.Add("types", TypeMap(doc, family));
                    result.Readback.Add(Stash, stash);
                    return;
                }

                var failures = new FailureCollector();
                EventHandler<FailuresProcessingEventArgs> handler = (s, e) =>
                {
                    var a = e.GetFailuresAccessor();
                    if (!a.GetDocument().Equals(doc)) return;
                    var r = failures.PreprocessFailures(a);
                    if (r != FailureProcessingResult.Continue) e.SetProcessingResult(r);
                };
                Family loaded;
                doc.Application.FailuresProcessing += handler;
                try { loaded = famDoc.LoadFamily(doc, new ReloadOptions()); }
                finally { doc.Application.FailuresProcessing -= handler; }
                foreach (var w in failures.Warnings) ctx.Job.Report.Warnings.Add(new ReportWarning("app", w.Text(), ctx.Op.Id));
                if (failures.Errors.Count > 0)
                    throw new OpFailedException("load_failed", "Revit refused to load the edited family: " + string.Join("; ", failures.Errors.Take(3).Select(x => x.Text())),
                        new JsonObject().Add("errors", new JsonArray(failures.Errors.Select(x => (JsonValue)x.ToJson()))));
                if (loaded is null) throw new OpFailedException("load_failed", "Revit did not load the edited family back into the project");
                family = loaded;
                result.Outputs.Set("family", Kit.Id(family.Id));
                result.Modified.Add(Kit.Id(family.Id));
                foreach (var sid in family.GetFamilySymbolIds())
                {
                    if (symbolsBefore.Contains(ElementIds.ToLong(sid))) result.Modified.Add(Kit.Id(sid));
                    else result.Created.Add(Kit.Id(sid));
                }
                result.Outputs.Add("types", TypeMap(doc, family));
                result.Readback.Add(Stash, stash);
            }
            finally { Close(famDoc); }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var stash = result.Readback[Stash] as JsonObject ?? new JsonObject();
            result.Readback.Remove(Stash);
            string? idText = result.Outputs.GetStringOrNull("family");
            if (idText is null || !ElementIds.TryParse(idText, out var fid) || !(doc.GetElement(fid) is Family family))
            {
                check.Fail("the family is not in the project after the op");
                check.Apply(result);
                return;
            }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            if (p is null) { check.Fail("the request no longer matches the project: " + string.Join("; ", issues.Where(i => i.IsError).Take(3).Select(i => i.Message))); check.Apply(result); return; }
            bool loaded = !ctx.IsRehearsal && stash["changed"] is JsonBool ch && ch.Value;
            var rb = result.Readback;
            rb.Add("family", new JsonObject().Add("id", idText).Add("name", family.Name).Add("category", family.FamilyCategory?.Name ?? ""));
            rb.Add("loaded", loaded);
            rb.Add("shared", stash["shared"] is JsonBool sh && sh.Value);
            JsonObject after;
            if (!loaded)
            {
                after = stash["copy"] as JsonObject ?? new JsonObject();
                foreach (var m in (stash["mismatches"] as JsonArray ?? new JsonArray()).OfType<JsonString>()) check.Fail(m.Value);
                rb.Add("source", ctx.IsRehearsal ? "family copy (a rehearsal does not load the family)" : "family copy (nothing changed, so nothing was loaded)");
            }
            else
            {
                var mismatches = new List<string>();
                Document famDoc;
                try { famDoc = Open(ctx, family); }
                catch (Exception e) { check.Fail("could not reopen the loaded family to read it back: " + e.Message); check.Apply(result); return; }
                try { after = ReadLoaded(doc, famDoc, p, u, mismatches); }
                finally { Close(famDoc); }
                foreach (var m in mismatches) check.Fail(m);
                rb.Add("source", "project types (type parameters) and the loaded family (instance parameters, formulas)");
                var names = new HashSet<string>(family.GetFamilySymbolIds().Select(s => doc.GetElement(s)?.Name ?? ""), StringComparer.Ordinal);
                foreach (var gone in (stash["otherBefore"] as JsonObject ?? new JsonObject()).Keys.Where(n => !names.Contains(n))) check.Fail("type '" + gone + "' existed before the load and is gone after it");
                var other = OtherChanges(stash["otherBefore"] as JsonObject, AllTypeValues(doc, p, u), p);
                if (other.Count > 0)
                {
                    rb.Add("otherChanges", JsonArray.FromStrings(other.Take(100)));
                    ctx.Warn(other.Count + " type value(s) the job did not set changed with the load (formulas, or values the project held that the family did not): " + string.Join("; ", other.Take(5)));
                }
            }
            rb.Add("addedParameters", after["addedParameters"]?.DeepClone() ?? new JsonArray());
            rb.Add("formulas", Merge(stash["before"] as JsonObject, after, "formulas"));
            rb.Add("types", MergeTypes(stash["before"] as JsonObject, after, p, doc, family));
            check.Apply(result);
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// The requested items' current values: per type and parameter { value, source }, and formulas. With
        /// <paramref name="project"/> type parameters of types the project has are read from the project's types.
        /// </summary>
        private static JsonObject Snapshot(Document doc, Document famDoc, Plan p, RevitUnits u, bool project)
        {
            var fm = famDoc.FamilyManager;
            var types = new JsonObject();
            foreach (var te in p.Types)
            {
                var values = new JsonObject();
                var ft = FamilyDoc.Type(fm, te.Name);
                foreach (var kv in te.Values)
                {
                    var prm = FamilyDoc.Find(fm, kv.Key, out _);
                    JsonValue v = JsonValue.Null;
                    string source = "family";
                    if (prm != null && project && !prm.IsInstance && p.Symbols.TryGetValue(te.Name, out var sym) && ParamAccess.Find(sym, kv.Key, out _) is Parameter sp)
                    {
                        v = FamilyDoc.Read(doc, sp, u);
                        source = "project";
                    }
                    else if (prm != null && ft != null) v = FamilyDoc.Read(famDoc, ft, prm, u);
                    values.Add(kv.Key, new JsonObject().Add("value", v).Add("source", source));
                }
                types.Add(te.Name, new JsonObject().Add("exists", ft != null || te.InProject).Add("values", values));
            }
            var formulas = new JsonObject();
            foreach (var kv in p.Formulas)
            {
                var prm = FamilyDoc.Find(fm, kv.Key, out _);
                formulas.Add(kv.Key, prm?.Formula is string f ? (JsonValue)f : JsonValue.Null);
            }
            return new JsonObject().Add("types", types).Add("formulas", formulas);
        }

        /// <summary>The snapshot with sources dropped, so the project's and the copy's readings compare by value.</summary>
        private static JsonObject Comparable(JsonObject snap)
        {
            var o = new JsonObject();
            var types = new JsonObject();
            foreach (var t in (snap["types"] as JsonObject ?? new JsonObject()))
            {
                var vals = new JsonObject();
                foreach (var v in ((t.Value as JsonObject)?["values"] as JsonObject ?? new JsonObject()))
                    vals.Add(v.Key, (v.Value as JsonObject)?["value"]?.DeepClone() ?? JsonValue.Null);
                types.Add(t.Key, vals);
            }
            return o.Add("types", types).Add("formulas", snap["formulas"]?.DeepClone() ?? new JsonObject());
        }

        /// <summary>Rehearsal (or nothing changed): every requested item read from the edited copy, compared with the request.</summary>
        private static JsonObject ReadCopy(Document famDoc, Plan p, RevitUnits u, List<string> mismatches)
        {
            var fm = famDoc.FamilyManager;
            var types = new JsonObject();
            foreach (var te in p.Types)
            {
                var ft = FamilyDoc.Type(fm, te.Name);
                var values = new JsonObject();
                if (ft is null) { mismatches.Add("type '" + te.Name + "' is not in the edited family"); types.Add(te.Name, new JsonObject().Add("values", values)); continue; }
                foreach (var kv in te.Values)
                {
                    var prm = FamilyDoc.Find(fm, kv.Key, out string? err);
                    if (prm is null) { mismatches.Add(err ?? kv.Key); continue; }
                    values.Add(kv.Key, new JsonObject().Add("value", FamilyDoc.Read(famDoc, ft, prm, u)).Add("source", "family"));
                    if (!FamilyDoc.TryPrepare(famDoc, prm, kv.Value, u, out var pv, out string perr)) mismatches.Add("'" + kv.Key + "': " + perr);
                    else if (!FamilyDoc.Matches(ft, prm, pv, u))
                        mismatches.Add("type '" + te.Name + "': '" + kv.Key + "' reads " + FamilyDoc.Read(famDoc, ft, prm, u).ToJson() + ", requested " + kv.Value.ToJson());
                }
                types.Add(te.Name, new JsonObject().Add("values", values));
            }
            return new JsonObject().Add("types", types).Add("formulas", ReadFormulas(fm, p, mismatches)).Add("addedParameters", ReadAdded(fm, p, mismatches));
        }

        /// <summary>Commit: type parameters from the project's types, everything else from a fresh copy of the loaded family.</summary>
        private static JsonObject ReadLoaded(Document doc, Document famDoc, Plan p, RevitUnits u, List<string> mismatches)
        {
            var fm = famDoc.FamilyManager;
            var symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
            foreach (var sid in p.Family.GetFamilySymbolIds())
                if (doc.GetElement(sid) is FamilySymbol s && !symbols.ContainsKey(s.Name)) symbols[s.Name] = s;
            var types = new JsonObject();
            foreach (var te in p.Types)
            {
                var values = new JsonObject();
                var ft = FamilyDoc.Type(fm, te.Name);
                if (!symbols.TryGetValue(te.Name, out var sym)) { mismatches.Add("type '" + te.Name + "' is not in the project after the load"); types.Add(te.Name, new JsonObject().Add("values", values)); continue; }
                foreach (var kv in te.Values)
                {
                    var prm = FamilyDoc.Find(fm, kv.Key, out string? err);
                    if (prm is null) { mismatches.Add("after the load: " + err); continue; }
                    if (!FamilyDoc.TryPrepare(famDoc, prm, kv.Value, u, out var pv, out string perr)) { mismatches.Add("'" + kv.Key + "': " + perr); continue; }
                    if (!prm.IsInstance)
                    {
                        var sp = ParamAccess.Find(sym, kv.Key, out string? serr);
                        if (sp is null) { mismatches.Add("type '" + te.Name + "' in the project: " + serr); continue; }
                        var read = FamilyDoc.Read(doc, sp, u);
                        values.Add(kv.Key, new JsonObject().Add("value", read).Add("source", "project"));
                        if (!FamilyDoc.Matches(doc, sp, pv, u)) mismatches.Add("type '" + te.Name + "': '" + kv.Key + "' reads " + read.ToJson() + " in the project, requested " + kv.Value.ToJson());
                    }
                    else if (ft is null) mismatches.Add("type '" + te.Name + "' is not in the loaded family");
                    else
                    {
                        var read = FamilyDoc.Read(famDoc, ft, prm, u);
                        values.Add(kv.Key, new JsonObject().Add("value", read).Add("source", "family"));
                        if (!FamilyDoc.Matches(ft, prm, pv, u)) mismatches.Add("type '" + te.Name + "': instance parameter '" + kv.Key + "' reads " + read.ToJson() + " in the loaded family, requested " + kv.Value.ToJson());
                    }
                }
                types.Add(te.Name, new JsonObject().Add("id", Kit.Id(sym.Id)).Add("values", values));
            }
            return new JsonObject().Add("types", types).Add("formulas", ReadFormulas(fm, p, mismatches)).Add("addedParameters", ReadAdded(fm, p, mismatches));
        }

        private static JsonObject ReadFormulas(FamilyManager fm, Plan p, List<string> mismatches)
        {
            var o = new JsonObject();
            foreach (var kv in p.Formulas)
            {
                var prm = FamilyDoc.Find(fm, kv.Key, out string? err);
                if (prm is null) { mismatches.Add(err ?? kv.Key); continue; }
                o.Add(kv.Key, prm.Formula is string f ? (JsonValue)f : JsonValue.Null);
                if (FamilyDoc.Canonical(prm.Formula) != FamilyDoc.Canonical(kv.Value))
                    mismatches.Add("formula of '" + kv.Key + "' reads '" + (prm.Formula ?? "(none)") + "', requested '" + (kv.Value ?? "(none)") + "'");
            }
            return o;
        }

        private static JsonArray ReadAdded(FamilyManager fm, Plan p, List<string> mismatches)
        {
            var a = new JsonArray();
            foreach (var np in p.Add)
            {
                var prm = FamilyDoc.Find(fm, np.Name, out _);
                if (prm is null) { mismatches.Add("parameter '" + np.Name + "' is not in the family"); continue; }
                var spec = FamilyDoc.Spec(prm);
                a.Add(new JsonObject().Add("name", np.Name).Add("spec", ParameterApi.SpecLabel(spec)).Add("instance", prm.IsInstance).Add("group", ParameterApi.GroupLabel(prm.Definition)));
                if (!ParameterApi.SameSpec(spec, FamilyDoc.SpecFor(np.Spec)) || prm.IsInstance != np.Instance)
                    mismatches.Add("parameter '" + np.Name + "' reads as " + (prm.IsInstance ? "instance " : "type ") + ParameterApi.SpecLabel(spec) + ", requested " + (np.Instance ? "instance " : "type ") + np.Spec);
            }
            return a;
        }

        /// <summary>Every type parameter value of every project type of the family, keyed by type then parameter id.</summary>
        private static JsonObject AllTypeValues(Document doc, Plan p, RevitUnits u)
        {
            var o = new JsonObject();
            foreach (var sid in p.Family.GetFamilySymbolIds())
            {
                if (!(doc.GetElement(sid) is FamilySymbol s) || o.ContainsKey(s.Name)) continue;
                var values = new JsonObject();
                foreach (Parameter prm in s.Parameters)
                {
                    if (prm.Definition is null || prm.StorageType == StorageType.None) continue;
                    string key = ElementIds.ToWire(prm.Id);
                    if (values.ContainsKey(key)) continue;
                    values.Add(key, new JsonArray(prm.Definition.Name, FamilyDoc.Read(doc, prm, u)));
                }
                o.Add(s.Name, values);
            }
            return o;
        }

        private static List<string> OtherChanges(JsonObject? before, JsonObject after, Plan p)
        {
            var list = new List<string>();
            if (before is null) return list;
            foreach (var t in before)
            {
                var requested = new HashSet<string>(p.Types.Where(x => x.Name == t.Key).SelectMany(x => x.Values.Select(v => v.Key)), StringComparer.Ordinal);
                var now = after[t.Key] as JsonObject;
                if (now is null || !(t.Value is JsonObject was)) continue;
                foreach (var v in was)
                {
                    if (!(v.Value is JsonArray pair) || pair.Count < 2 || !(pair[0] is JsonString pname)) continue;
                    if (requested.Contains(pname.Value)) continue;
                    var nowPair = now[v.Key] as JsonArray;
                    var nowValue = nowPair != null && nowPair.Count > 1 ? nowPair[1] : JsonValue.Null;
                    if (!JsonValue.DeepEquals(pair[1], nowValue))
                        list.Add("type '" + t.Key + "': '" + pname.Value + "' was " + pair[1].ToJson() + ", now " + nowValue.ToJson());
                }
            }
            return list;
        }

        private static JsonObject Merge(JsonObject? before, JsonObject after, string key)
        {
            var o = new JsonObject();
            var b = before?[key] as JsonObject ?? new JsonObject();
            var a = after[key] as JsonObject ?? new JsonObject();
            foreach (var k in b.Keys.Concat(a.Keys).Distinct())
                o.Add(k, new JsonObject().Add("before", b[k]?.DeepClone() ?? JsonValue.Null).Add("after", a[k]?.DeepClone() ?? JsonValue.Null));
            return o;
        }

        private static JsonArray MergeTypes(JsonObject? before, JsonObject after, Plan p, Document doc, Family family)
        {
            var map = TypeMap(doc, family);
            var bt = before?["types"] as JsonObject ?? new JsonObject();
            var at = after["types"] as JsonObject ?? new JsonObject();
            var list = new JsonArray();
            foreach (var te in p.Types)
            {
                bool existed = (bt[te.Name] as JsonObject)?["exists"] is JsonBool ex ? ex.Value : te.InProject;
                var b = (bt[te.Name] as JsonObject)?["values"] as JsonObject ?? new JsonObject();
                var a = (at[te.Name] as JsonObject)?["values"] as JsonObject ?? new JsonObject();
                var values = new JsonObject();
                foreach (var kv in te.Values)
                {
                    var bv = b[kv.Key] as JsonObject;
                    var av = a[kv.Key] as JsonObject;
                    values.Add(kv.Key, new JsonObject()
                        .Add("before", existed ? bv?["value"]?.DeepClone() ?? JsonValue.Null : JsonValue.Null)
                        .Add("after", av?["value"]?.DeepClone() ?? JsonValue.Null)
                        .Add("requested", kv.Value.DeepClone())
                        .Add("source", av?["source"]?.DeepClone() ?? JsonValue.Null));
                }
                list.Add(new JsonObject().Add("name", te.Name).Add("id", map[te.Name]?.DeepClone() ?? JsonValue.Null)
                    .Add("state", existed ? "updated" : "created").Add("values", values));
            }
            return list;
        }

        private static JsonObject TypeMap(Document doc, Family family)
        {
            var o = new JsonObject();
            foreach (var sid in family.GetFamilySymbolIds())
                if (doc.GetElement(sid) is FamilySymbol s && !o.ContainsKey(s.Name)) o.Add(s.Name, Kit.Id(s.Id));
            return o;
        }
    }
}
