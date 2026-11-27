using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// type.duplicate: ElementType.Duplicate(newName), then type parameters.
    ///
    /// The source is "Family : Type" or a type name, narrowed by category; an ambiguous name is refused with the
    /// candidates' categories. The new name is checked against Revit's forbidden characters and the placeholder and
    /// copy names the practice notes list (RV-027). Names are unique per family for loadable types and per class and
    /// category for system types, which is where the existing type is looked for.
    ///
    /// Ensure: an existing type with the new name whose requested parameters already hold the values is reported
    /// unchanged; one that differs is refused unless update is true (then it is declared and changed, which changes
    /// every instance of the type). Every parameter is resolved, checked for read-only and converted before anything
    /// changes. Readback reads the name, family, category and every parameter from the type.
    /// </summary>
    public sealed class TypeDuplicate : OpHandler
    {
        public override string Name => "type.duplicate";

        private sealed class Plan
        {
            public ElementType Source = null!;
            public ElementType? Existing;
            public string NewName = "";
            public bool Matches;
            public List<string> Differences = new List<string>();
        }

        private static List<ElementType> Candidates(Document doc, string source, Category? category)
        {
            var all = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>();
            if (category != null) all = all.Where(t => t.Category != null && t.Category.Id == category.Id);
            var list = all.ToList();
            var exact = list.Where(t => source.Contains(" : ") ? Lookup.FamilyTypeName(t) == source : t.Name == source).ToList();
            if (exact.Count > 0) return exact;
            return list.Where(t => string.Equals(source.Contains(" : ") ? Lookup.FamilyTypeName(t) : t.Name, source, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Types that share a name space with t: the same family for loadable types, else the same class, family name and category.</summary>
        private static IEnumerable<ElementType> Siblings(Document doc, ElementType t)
        {
            if (t is FamilySymbol fs) return fs.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<ElementType>();
            // Filter by the exact .NET class in memory: OfClass refuses some API classes.
            var cls = t.GetType();
            return new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .Where(o => o.GetType() == cls && o.FamilyName == t.FamilyName && (o.Category?.Id ?? ElementId.InvalidElementId) == (t.Category?.Id ?? ElementId.InvalidElementId));
        }

        /// <summary>
        /// A system type of the same class but another family (Curtain Wall next to Basic Wall) with the name, any case,
        /// or null. Types of the plain ElementType class are compared within their category only, since that class
        /// holds unrelated kinds. Loadable types (FamilySymbol) are named per family and never clash here.
        /// </summary>
        private static ElementType? OtherFamilyClash(Document doc, ElementType source, string name)
        {
            if (source is FamilySymbol) return null;
            var cls = source.GetType();
            var cat = source.Category?.Id ?? ElementId.InvalidElementId;
            return new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                .FirstOrDefault(o => o.GetType() == cls && o.FamilyName != source.FamilyName &&
                                     (cls != typeof(ElementType) || (o.Category?.Id ?? ElementId.InvalidElementId) == cat) &&
                                     string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool afterCommit = false)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan { NewName = args.GetString("newName") };
            Category? cat = null;
            if (args.GetStringOrNull("category") is string cn)
            {
                cat = Lookup.Category(doc, cn, out string? cerr);
                if (cat is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, cerr!); return null; }
            }
            string source = args.GetString("source");
            var cands = Candidates(doc, source, cat);
            if (cands.Count == 0)
            {
                var names = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
                    .Where(t => cat is null || (t.Category != null && t.Category.Id == cat.Id)).Select(Lookup.FamilyTypeName).Distinct().ToList();
                OpContext.Error(issues, ctx.FieldPath(args, "source"), RevitIssueCodes.NotFound, "no type '" + source + "'" + Lookup.Suggest(source, names));
                return null;
            }
            if (cands.Count > 1)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "source"), RevitIssueCodes.Ambiguous, cands.Count + " types match '" + source + "': " +
                    string.Join(", ", cands.Take(6).Select(t => Lookup.FamilyTypeName(t) + " (" + (t.Category?.Name ?? t.GetType().Name) + ", id " + Kit.Id(t.Id) + ")")) + "; give category or \"Family : Type\"");
                return null;
            }
            p.Source = cands[0];
            if (!afterCommit && Kit.BadTypeName(p.NewName) is string bad) OpContext.Error(issues, ctx.FieldPath(args, "newName"), RevitIssueCodes.Invalid, bad);
            var sibs = Siblings(doc, p.Source).ToList();
            p.Existing = sibs.FirstOrDefault(t => string.Equals(t.Name, p.NewName, StringComparison.Ordinal));
            if (p.Existing is null)
            {
                var caseOnly = sibs.FirstOrDefault(t => string.Equals(t.Name, p.NewName, StringComparison.OrdinalIgnoreCase));
                if (caseOnly != null) OpContext.Error(issues, ctx.FieldPath(args, "newName"), RevitIssueCodes.Mismatch, "a type named '" + caseOnly.Name + "' exists (names differ only by case); use that exact name");
                else if (OtherFamilyClash(doc, p.Source, p.NewName) is ElementType clash)
                    OpContext.Error(issues, ctx.FieldPath(args, "newName"), RevitIssueCodes.Exists,
                        "'" + Lookup.FamilyTypeName(clash) + "' already uses the name '" + clash.Name + "'; system types of one kind share one set of names, so choose another newName");
            }
            if (p.Existing != null && p.Existing.Id == p.Source.Id && !afterCommit)
                OpContext.Error(issues, ctx.FieldPath(args, "newName"), RevitIssueCodes.Invalid, "newName is the source type's own name");

            var target = (Element?)p.Existing ?? p.Source;
            foreach (var kv in args.GetMap("parameters"))
            {
                string at = JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key);
                if (OpContext.IsPending(kv.Value)) continue;
                var prm = ParamAccess.Find(target, kv.Key, out string? err);
                if (prm is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, err!); continue; }
                if (prm.IsReadOnly) { OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "type parameter '" + kv.Key + "' is read-only"); continue; }
                if (!ParamAccess.TryPrepare(doc, prm, kv.Value, u, out var pv, out string perr)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "'" + kv.Key + "': " + perr); continue; }
                if (p.Existing != null && !ParamAccess.Matches(prm, pv, u))
                    p.Differences.Add("'" + kv.Key + "' is " + ParamAccess.Read(prm, u).ToJson() + ", requested " + kv.Value.ToJson());
            }
            if (p.Existing != null && !afterCommit)
            {
                p.Matches = p.Differences.Count == 0;
                if (!p.Matches)
                {
                    if (args.GetBool("update")) ctx.DeclareModifies(p.Existing.Id);
                    else OpContext.Error(issues, ctx.FieldPath(args, "newName"), RevitIssueCodes.Mismatch, "type '" + Lookup.FamilyTypeName(p.Existing) + "' exists and differs: " + string.Join("; ", p.Differences.Take(6)) + "; set update:true to change it");
                }
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            ElementType type;
            if (p!.Existing != null)
            {
                type = p.Existing;
                if (p.Matches) { result.Unchanged.Add(Kit.Id(type.Id)); result.Outputs.Add("type", Kit.Id(type.Id)); return; }
                result.Modified.Add(Kit.Id(type.Id));
            }
            else
            {
                type = p.Source.Duplicate(p.NewName) as ElementType ?? throw new OpFailedException("create_failed", "duplicating '" + Lookup.FamilyTypeName(p.Source) + "' gave no type");
                result.Created.Add(Kit.Id(type.Id));
            }
            Kit.SetParameters(doc, type, args.GetMap("parameters"), ctx.Units, "type '" + p.NewName + "'");
            result.Outputs.Add("type", Kit.Id(type.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("type");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is ElementType t))
            {
                check.Fail("type '" + args.GetString("newName") + "' not found after the op");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            check.Text(rb, "name", t.Name, args.GetString("newName"), "type name");
            var p = Build(ctx, args, new List<Issue>(), afterCommit: true);
            check.Text(rb, "family", t.FamilyName, p?.Source.FamilyName, "type family");
            check.Text(rb, "category", t.Category?.Name, p?.Source.Category?.Name, "type category");
            if (p != null) rb.Add("source", Kit.Id(p.Source.Id));
            Kit.CheckParameters(doc, t, args.GetMap("parameters"), u, check, rb, "type '" + t.Name + "'");
            rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            check.Apply(result);
        }
    }
}
