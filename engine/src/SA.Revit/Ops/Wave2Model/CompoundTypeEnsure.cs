using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// compoundtype.ensure: a floor, ceiling or roof type with a compound layer build-up, top to bottom
    /// (CompoundStructure.GetLayers documents that order for roofs, floors and ceilings).
    ///
    /// Follows walltype.ensure: a new type is duplicated from basedOn (or the first type of the kind, by name, that has
    /// a layered, not vertically compound, structure and is not a foundation slab) and given
    /// CreateSimpleCompoundStructure with the requested layers; the shell counts come from the core flags (no core
    /// flag: the structure layers become the core, with a warning); a variable layer is set with VariableLayerIndex
    /// after CanLayerBeVariable; the single core structure layer carries the structural material. The structure is
    /// checked with CompoundStructure.IsValid in Validate. Missing materials are created plainly and reported.
    ///
    /// An existing type with the name is compared layer by layer and by its variable layer; a difference is refused
    /// unless update, which changes every instance of the type (their count is warned) and declares the type.
    /// </summary>
    public sealed class CompoundTypeEnsure : OpHandler
    {
        public override string Name => "compoundtype.ensure";

        internal static readonly Dictionary<string, MaterialFunctionAssignment> LayerFunctions = new Dictionary<string, MaterialFunctionAssignment>(StringComparer.Ordinal)
        {
            { "structure", MaterialFunctionAssignment.Structure }, { "substrate", MaterialFunctionAssignment.Substrate },
            { "thermal", MaterialFunctionAssignment.Insulation }, { "membrane", MaterialFunctionAssignment.Membrane },
            { "finish1", MaterialFunctionAssignment.Finish1 }, { "finish2", MaterialFunctionAssignment.Finish2 },
        };

        private sealed class LayerSpec
        {
            public MaterialFunctionAssignment Function;
            public string MaterialName = "";
            public Material? Material;
            public double Thickness;
            public bool Core, Variable;
        }

        private sealed class Plan
        {
            public string Name = "";
            public HostObjAttributes? Existing;
            public HostObjAttributes? Source;
            public List<LayerSpec> Layers = new List<LayerSpec>();
            public int TopShells, BottomShells;
            public int Variable = -1;
            public bool Matches;
            public List<string> Differences = new List<string>();
        }

        internal static Type ClassFor(string kind) => kind == "floor" ? typeof(FloorType) : kind == "ceiling" ? typeof(CeilingType) : typeof(RoofType);

        internal static List<HostObjAttributes> TypesOf(Document doc, string kind) =>
            new FilteredElementCollector(doc).OfClass(ClassFor(kind)).Cast<HostObjAttributes>().ToList();

        internal static Material? FindMaterial(Document doc, string name, out bool ambiguous)
        {
            ambiguous = false;
            var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
            var exact = all.Where(m => string.Equals(m.Name, name, StringComparison.Ordinal)).ToList();
            if (exact.Count == 1) return exact[0];
            var loose = all.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count == 1) return loose[0];
            ambiguous = exact.Count > 1 || loose.Count > 1;
            return null;
        }

        private static bool Layered(HostObjAttributes t)
        {
            CompoundStructure? cs;
            try { cs = t.GetCompoundStructure(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
            if (cs is null || cs.LayerCount == 0 || cs.IsVerticallyCompound) return false;
            return !(t is FloorType ft && ft.IsFoundationSlab);
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            string kind = args.GetString("kind");
            var p = new Plan { Name = args.GetString("name") };
            string at = ctx.FieldPath(args, "name");
            if (Kit.BadTypeName(p.Name) is string bad) OpContext.Error(issues, at, RevitIssueCodes.Invalid, bad);
            var types = TypesOf(doc, kind);
            var same = types.Where(t => string.Equals(t.Name, p.Name, StringComparison.Ordinal)).ToList();
            if (same.Count > 1) OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, same.Count + " " + kind + " types are named '" + p.Name + "'");
            else if (same.Count == 1)
            {
                if (!Layered(same[0])) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "'" + p.Name + "' exists as a " + kind + " type without a simple layered structure");
                else p.Existing = same[0];
            }
            else if (types.FirstOrDefault(t => string.Equals(t.Name, p.Name, StringComparison.OrdinalIgnoreCase)) is HostObjAttributes caseOnly)
                OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "a " + kind + " type named '" + caseOnly.Name + "' exists (names differ only by case); use that exact name");

            if (args.GetStringOrNull("basedOn") is string based)
            {
                var t = Lookup.ByName(types, based, kind + " type");
                if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), t.Code, t.Error!);
                else if (!Layered(t.Value!)) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), RevitIssueCodes.Invalid, "'" + based + "' has no simple layered structure to start from");
                else p.Source = t.Value;
            }
            else p.Source = types.Where(Layered).OrderBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
            if (p.Existing is null && p.Source is null) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), RevitIssueCodes.NotFound, "the model has no layered " + kind + " type to duplicate");

            double minLayer = CompoundStructure.GetMinimumLayerThickness();
            var layers = args.GetObjectList("layers");
            foreach (var l in layers)
            {
                var spec = new LayerSpec
                {
                    Function = LayerFunctions[l.GetString("function")],
                    MaterialName = l.GetString("material").Trim(),
                    Thickness = u.LengthToInternal(l.GetLength("thickness")),
                    Core = l.GetBool("core"),
                    Variable = l.GetBool("variable"),
                };
                if (spec.Function == MaterialFunctionAssignment.Membrane)
                {
                    if (spec.Thickness > 0) OpContext.Error(issues, l.Path + ".thickness", RevitIssueCodes.Invalid, "a membrane layer must have thickness 0");
                    spec.Thickness = 0;
                }
                else if (spec.Thickness < minLayer - 1e-9)
                    OpContext.Error(issues, l.Path + ".thickness", RevitIssueCodes.Invalid, "layer thickness " + Kit.Num(l.GetLength("thickness")) + " " + Length.Symbol(u.JobUnit) +
                        " is below Revit's minimum of " + Kit.Num(u.LengthFromInternal(minLayer)) + " " + Length.Symbol(u.JobUnit) + "; only membranes may be 0");
                if (spec.MaterialName.Length == 0) OpContext.Error(issues, l.Path + ".material", RevitIssueCodes.Invalid, "material name is empty");
                else
                {
                    spec.Material = FindMaterial(doc, spec.MaterialName, out bool amb);
                    if (amb) OpContext.Error(issues, l.Path + ".material", RevitIssueCodes.Ambiguous, "several materials are named '" + spec.MaterialName + "'");
                    else if (spec.Material is null)
                    {
                        if (Kit.BadTypeName(spec.MaterialName) is string mb && mb.Contains("does not allow")) OpContext.Error(issues, l.Path + ".material", RevitIssueCodes.Invalid, mb);
                        else OpContext.Warning(issues, l.Path + ".material", RevitIssueCodes.NotFound, "material '" + spec.MaterialName + "' does not exist; it will be created with default appearance");
                    }
                }
                p.Layers.Add(spec);
            }

            var variable = Enumerable.Range(0, p.Layers.Count).Where(i => p.Layers[i].Variable).ToList();
            if (variable.Count > 1) OpContext.Error(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Invalid, "only one layer can be variable (found layers " + string.Join(", ", variable) + ")");
            else if (variable.Count == 1)
            {
                if (kind == "ceiling") OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "layers"), variable[0]) + ".variable", RevitIssueCodes.Invalid, "a variable layer applies to floors and roofs");
                p.Variable = variable[0];
            }

            var core = Enumerable.Range(0, p.Layers.Count).Where(i => p.Layers[i].Core).ToList();
            if (core.Count == 0)
            {
                core = Enumerable.Range(0, p.Layers.Count).Where(i => p.Layers[i].Function == MaterialFunctionAssignment.Structure).ToList();
                if (core.Count == 0) OpContext.Error(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Invalid, "no layer is marked core and there is no structure layer; mark the core layers (core: true)");
                else OpContext.Warning(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Unchecked, "no layer is marked core; the structure layer(s) " + string.Join(", ", core) + " are used as the core");
            }
            if (core.Count > 0)
            {
                if (core[core.Count - 1] - core[0] + 1 != core.Count)
                    OpContext.Error(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Invalid, "core layers must be contiguous (found layers " + string.Join(", ", core) + ")");
                foreach (int i in core)
                {
                    p.Layers[i].Core = true;
                    if (p.Layers[i].Function == MaterialFunctionAssignment.Membrane)
                        OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "layers"), i), RevitIssueCodes.Invalid, "a membrane layer cannot be in the core");
                }
                p.TopShells = core[0];
                p.BottomShells = p.Layers.Count - 1 - core[core.Count - 1];
            }
            if (Kit.HasErrors(issues)) return null;

            try
            {
                var cs = Structure(p, null);
                if (p.Variable >= 0 && !cs.CanLayerBeVariable(p.Variable))
                {
                    OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "layers"), p.Variable) + ".variable", RevitIssueCodes.Invalid, "Revit does not allow layer " + p.Variable + " to be variable");
                    return null;
                }
                if (!cs.IsValid(doc, out IDictionary<int, CompoundStructureError> errs, out IDictionary<int, int> _))
                {
                    var parts = errs.Select(kv => (kv.Key < 0 ? "structure" : "layer " + kv.Key) + ": " + kv.Value).ToList();
                    OpContext.Error(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Invalid, "Revit rejects this build-up: " + (parts.Count > 0 ? string.Join("; ", parts) : "no detail given"));
                    return null;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException e)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "layers"), RevitIssueCodes.Invalid, "Revit rejects this build-up: " + e.Message);
                return null;
            }

            if (p.Existing != null)
            {
                Compare(doc, p, u);
                p.Matches = p.Differences.Count == 0;
                if (!p.Matches)
                {
                    if (args.GetBool("update"))
                    {
                        ctx.DeclareModifies(p.Existing.Id);
                        int count = new FilteredElementCollector(doc).WhereElementIsNotElementType().OfCategoryId(p.Existing.Category.Id).Count(e => e.GetTypeId() == p.Existing.Id);
                        if (count > 0) OpContext.Warning(issues, at, RevitIssueCodes.Mismatch, "updating '" + p.Name + "' changes all " + count + " " + kind + "(s) of this type");
                    }
                    else OpContext.Error(issues, at, RevitIssueCodes.Mismatch, kind + " type '" + p.Name + "' exists and differs: " + string.Join("; ", p.Differences.Take(6)) + "; set update:true to change it");
                }
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        private static CompoundStructure Structure(Plan p, IDictionary<int, ElementId>? created)
        {
            var list = new List<CompoundStructureLayer>();
            for (int i = 0; i < p.Layers.Count; i++)
            {
                var l = p.Layers[i];
                var mat = l.Material?.Id ?? (created != null && created.TryGetValue(i, out var cid) ? cid : ElementId.InvalidElementId);
                list.Add(new CompoundStructureLayer(l.Thickness, l.Function, mat));
            }
            var cs = CompoundStructure.CreateSimpleCompoundStructure(list);
            cs.SetNumberOfShellLayers(ShellLayerType.Exterior, p.TopShells);
            cs.SetNumberOfShellLayers(ShellLayerType.Interior, p.BottomShells);
            if (p.Variable >= 0) cs.VariableLayerIndex = p.Variable;
            var structural = Enumerable.Range(0, p.Layers.Count).Where(i => p.Layers[i].Core && p.Layers[i].Function == MaterialFunctionAssignment.Structure).ToList();
            if (structural.Count == 1) cs.StructuralMaterialIndex = structural[0];
            return cs;
        }

        private static void Compare(Document doc, Plan p, RevitUnits u)
        {
            var cs = p.Existing!.GetCompoundStructure();
            if (cs is null) { p.Differences.Add("it has no compound structure"); return; }
            var layers = cs.GetLayers();
            if (layers.Count != p.Layers.Count) { p.Differences.Add(layers.Count + " layers, requested " + p.Layers.Count); return; }
            for (int i = 0; i < layers.Count; i++)
            {
                var have = layers[i];
                var want = p.Layers[i];
                if (have.Function != want.Function) p.Differences.Add("layer " + i + " function " + have.Function + ", requested " + want.Function);
                string mat = doc.GetElement(have.MaterialId)?.Name ?? "(by category)";
                if (!string.Equals(mat, want.Material?.Name ?? want.MaterialName, StringComparison.Ordinal)) p.Differences.Add("layer " + i + " material '" + mat + "', requested '" + want.MaterialName + "'");
                if (Math.Abs(have.Width - want.Thickness) > u.LengthToleranceInternal)
                    p.Differences.Add("layer " + i + " thickness " + Kit.Num(u.LengthFromInternal(have.Width)) + ", requested " + Kit.Num(u.LengthFromInternal(want.Thickness)));
                bool isCore = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex();
                if (isCore != want.Core) p.Differences.Add("layer " + i + (isCore ? " is" : " is not") + " in the core");
            }
            if (cs.VariableLayerIndex != p.Variable) p.Differences.Add("variable layer " + cs.VariableLayerIndex + ", requested " + p.Variable);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            if (p!.Existing != null && p.Matches)
            {
                result.Unchanged.Add(Kit.Id(p.Existing.Id));
                result.Outputs.Add("type", Kit.Id(p.Existing.Id));
                return;
            }
            var created = new Dictionary<int, ElementId>();
            var byName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < p.Layers.Count; i++)
            {
                var l = p.Layers[i];
                if (l.Material != null) continue;
                if (!byName.TryGetValue(l.MaterialName, out var mid))
                {
                    mid = Material.Create(doc, l.MaterialName);
                    byName[l.MaterialName] = mid;
                    result.Created.Add(Kit.Id(mid));
                }
                created[i] = mid;
            }
            HostObjAttributes type;
            if (p.Existing != null)
            {
                type = p.Existing;
                result.Modified.Add(Kit.Id(type.Id));
            }
            else
            {
                type = p.Source!.Duplicate(p.Name) as HostObjAttributes ?? throw new OpFailedException("create_failed", "duplicating '" + p.Source.Name + "' did not give a " + args.GetString("kind") + " type");
                result.Created.Insert(0, Kit.Id(type.Id));
            }
            type.SetCompoundStructure(Structure(p, created));
            result.Outputs.Add("type", Kit.Id(type.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string kind = args.GetString("kind");
            string? idText = result.Outputs.GetStringOrNull("type");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is HostObjAttributes t) || !ClassFor(kind).IsInstanceOfType(t))
            {
                check.Fail(kind + " type '" + args.GetString("name") + "' not found after the op");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            check.Text(rb, "name", t.Name, args.GetString("name"));
            var cs = t.GetCompoundStructure();
            var spec = args.GetObjectList("layers");
            var layers = new JsonArray();
            if (cs is null) check.Fail("the type has no compound structure");
            else
            {
                var have = cs.GetLayers();
                if (have.Count != spec.Count) check.Fail(have.Count + " layer(s) read back, requested " + spec.Count);
                bool anyCore = spec.Any(s => s.GetBool("core"));
                for (int i = 0; i < have.Count; i++)
                {
                    var l = have[i];
                    var o = new JsonObject().Add("index", i);
                    bool isCore = i >= cs.GetFirstCoreLayerIndex() && i <= cs.GetLastCoreLayerIndex();
                    if (i < spec.Count)
                    {
                        var s = spec[i];
                        check.Text(o, "function", l.Function.ToString(), LayerFunctions[s.GetString("function")].ToString(), "layer " + i + " function");
                        check.Text(o, "material", doc.GetElement(l.MaterialId)?.Name, FindMaterial(doc, s.GetString("material").Trim(), out _)?.Name ?? s.GetString("material").Trim(), "layer " + i + " material");
                        check.Length(o, "thickness", l.Width, s.GetString("function") == "membrane" ? 0 : s.GetLength("thickness"), "layer " + i + " thickness");
                        check.Flag(o, "core", isCore, anyCore ? s.GetBool("core") : s.GetString("function") == "structure", "layer " + i + " core");
                        check.Flag(o, "variable", cs.VariableLayerIndex == i, s.GetBool("variable"), "layer " + i + " variable");
                    }
                    layers.Add(o);
                }
                double expected = spec.Sum(s => s.GetString("function") == "membrane" ? 0 : s.GetLength("thickness"));
                check.Length(rb, "thickness", cs.GetWidth(), expected, "total thickness");
                result.Outputs.Set("thickness", u.LengthJson(cs.GetWidth()));
            }
            rb.Add("layers", layers);
            rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
            int materials = result.Created.Count(c => c != idText);
            if (materials > 0) rb.Add("materialsCreated", materials);
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            check.Apply(result);
        }
    }
}
