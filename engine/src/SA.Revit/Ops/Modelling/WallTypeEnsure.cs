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

namespace SA.Revit.Ops.Modelling
{
    /// <summary>
    /// walltype.ensure: a basic wall type with a compound layer build-up, exterior to interior.
    ///
    /// New types are duplicated from basedOn (or the first basic wall type by name) and given the requested
    /// structure. The structure is built and checked with CompoundStructure.IsValid during Validate, so Revit's own
    /// rules (layer priorities rising towards the finish faces, membranes of zero thickness, the minimum layer
    /// thickness, at least one core layer) refuse a bad build-up before anything changes. On top of Revit's rules:
    /// core layers must be contiguous; a membrane may not be in the core; only shell layers may wrap; when no layer is
    /// marked core, the structure layers become the core (with a warning); placeholder or copy names are refused
    /// (RV-027). Missing materials are created plainly and reported as created.
    ///
    /// An existing type with the name is compared layer by layer (function, material, thickness, core, wrapping) and
    /// by wall function; a difference is refused unless update is true. Updating a type changes every wall of that
    /// type (their count is reported as a warning); those walls are not declared, because their elements are not
    /// edited, only the shared type.
    /// </summary>
    public sealed class WallTypeEnsure : OpHandler
    {
        public override string Name => "walltype.ensure";

        private static readonly Dictionary<string, WallFunction> Functions = new Dictionary<string, WallFunction>(StringComparer.Ordinal)
        {
            { "exterior", WallFunction.Exterior }, { "interior", WallFunction.Interior }, { "foundation", WallFunction.Foundation },
            { "retaining", WallFunction.Retaining }, { "soffit", WallFunction.Soffit }, { "core_shaft", WallFunction.Coreshaft },
        };

        private static readonly Dictionary<string, MaterialFunctionAssignment> LayerFunctions = new Dictionary<string, MaterialFunctionAssignment>(StringComparer.Ordinal)
        {
            { "structure", MaterialFunctionAssignment.Structure }, { "substrate", MaterialFunctionAssignment.Substrate },
            { "thermal", MaterialFunctionAssignment.Insulation }, { "membrane", MaterialFunctionAssignment.Membrane },
            { "finish1", MaterialFunctionAssignment.Finish1 }, { "finish2", MaterialFunctionAssignment.Finish2 },
        };

        private static readonly Dictionary<string, OpeningWrappingCondition> InsertWrap = new Dictionary<string, OpeningWrappingCondition>(StringComparer.Ordinal)
        {
            { "none", OpeningWrappingCondition.None }, { "exterior", OpeningWrappingCondition.Exterior },
            { "interior", OpeningWrappingCondition.Interior }, { "both", OpeningWrappingCondition.ExteriorAndInterior },
        };

        private static readonly Dictionary<string, EndCapCondition> EndWrap = new Dictionary<string, EndCapCondition>(StringComparer.Ordinal)
        {
            { "none", EndCapCondition.None }, { "exterior", EndCapCondition.Exterior }, { "interior", EndCapCondition.Interior },
        };

        private sealed class LayerSpec
        {
            public MaterialFunctionAssignment Function;
            public string FunctionName = "";
            public string MaterialName = "";
            public Material? Material;
            public double Thickness;   // internal
            public bool Core, Wraps;
        }

        private sealed class Plan
        {
            public string Name = "";
            public WallType? Existing;
            public WallType? Source;
            public WallFunction Function;
            public List<LayerSpec> Layers = new List<LayerSpec>();
            public int ExteriorShells, InteriorShells;
            public OpeningWrappingCondition Inserts;
            public EndCapCondition Ends;
            public bool Matches;
            public List<string> Differences = new List<string>();
        }

        private static Material? FindMaterial(Document doc, string name, out bool ambiguous)
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

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan { Name = args.GetString("name") };
            string at = ctx.FieldPath(args, "name");
            if (Kit.BadTypeName(p.Name) is string bad) OpContext.Error(issues, at, RevitIssueCodes.Invalid, bad);
            var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            var same = wallTypes.Where(t => string.Equals(t.Name, p.Name, StringComparison.Ordinal)).ToList();
            if (same.Count > 1) OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, same.Count + " wall types are named '" + p.Name + "'");
            else if (same.Count == 1)
            {
                if (same[0].Kind != WallKind.Basic) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "'" + p.Name + "' exists as a " + same[0].Kind + " wall type, not a basic wall");
                else p.Existing = same[0];
            }
            else
            {
                var caseOnly = wallTypes.FirstOrDefault(t => string.Equals(t.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (caseOnly != null) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "a wall type named '" + caseOnly.Name + "' exists (names differ only by case); use that exact name");
            }

            if (args.GetStringOrNull("basedOn") is string based)
            {
                var t = Lookup.Type<WallType>(doc, based, "wall type");
                if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), t.Code, t.Error!);
                else if (t.Value!.Kind != WallKind.Basic) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), RevitIssueCodes.Invalid, "'" + based + "' is a " + t.Value.Kind + " wall type; basedOn must be a basic wall");
                else p.Source = t.Value;
            }
            else p.Source = wallTypes.Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
            if (p.Existing is null && p.Source is null) OpContext.Error(issues, ctx.FieldPath(args, "basedOn"), RevitIssueCodes.NotFound, "the model has no basic wall type to duplicate");

            p.Function = Functions[args.GetString("function")];
            p.Inserts = InsertWrap[args.GetString("wrapAtInserts")];
            p.Ends = EndWrap[args.GetString("wrapAtEnds")];

            double minLayer = CompoundStructure.GetMinimumLayerThickness();
            var layers = args.GetObjectList("layers");
            foreach (var l in layers)
            {
                var spec = new LayerSpec
                {
                    FunctionName = l.GetString("function"),
                    MaterialName = l.GetString("material").Trim(),
                    Thickness = u.LengthToInternal(l.GetLength("thickness")),
                    Core = l.GetBool("core"),
                    Wraps = l.GetBool("wraps"),
                };
                spec.Function = LayerFunctions[spec.FunctionName];
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

            // Core: the flagged layers, else the structure layers. They must be contiguous and contain no membrane.
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
                    if (p.Layers[i].Wraps)
                        OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "layers"), i) + ".wraps", RevitIssueCodes.Invalid, "only shell (non-core) layers wrap; core layers always run through");
                }
                p.ExteriorShells = core[0];
                p.InteriorShells = p.Layers.Count - 1 - core[core.Count - 1];
            }
            if (Kit.HasErrors(issues)) return null;

            // Revit's own consistency check on the structure as it will be assigned.
            try
            {
                var cs = Structure(p, null);
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
                        int walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Count(w => w.GetTypeId() == p.Existing.Id);
                        if (walls > 0) OpContext.Warning(issues, at, RevitIssueCodes.Mismatch, "updating '" + p.Name + "' changes all " + walls + " wall(s) of this type");
                    }
                    else OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "wall type '" + p.Name + "' exists and differs: " + string.Join("; ", p.Differences.Take(6)) + "; set update:true to change it");
                }
            }
            return Kit.HasErrors(issues) ? null : p;
        }

        /// <summary>The requested compound structure; materials not yet created are left unassigned (by category).</summary>
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
            cs.SetNumberOfShellLayers(ShellLayerType.Exterior, p.ExteriorShells);
            cs.SetNumberOfShellLayers(ShellLayerType.Interior, p.InteriorShells);
            for (int i = 0; i < p.Layers.Count; i++)
                if (!p.Layers[i].Core) cs.SetParticipatesInWrapping(i, p.Layers[i].Wraps);
            cs.OpeningWrapping = p.Inserts;
            cs.EndCap = p.Ends;
            // The single structure layer in the core carries the structural material, as Revit's rules expect.
            var structural = Enumerable.Range(0, p.Layers.Count).Where(i => p.Layers[i].Core && p.Layers[i].Function == MaterialFunctionAssignment.Structure).ToList();
            if (structural.Count == 1) cs.StructuralMaterialIndex = structural[0];
            return cs;
        }

        private static void Compare(Document doc, Plan p, RevitUnits u)
        {
            var t = p.Existing!;
            if (t.Function != p.Function) p.Differences.Add("function is " + t.Function + ", requested " + p.Function);
            var cs = t.GetCompoundStructure();
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
                if (!isCore && cs.ParticipatesInWrapping(i) != want.Wraps) p.Differences.Add("layer " + i + " wrapping " + cs.ParticipatesInWrapping(i));
            }
            if (cs.OpeningWrapping != p.Inserts) p.Differences.Add("wrap at inserts " + cs.OpeningWrapping + ", requested " + p.Inserts);
            if (cs.EndCap != p.Ends) p.Differences.Add("wrap at ends " + cs.EndCap + ", requested " + p.Ends);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            WallType type;
            if (p!.Existing != null && p.Matches)
            {
                result.Unchanged.Add(Kit.Id(p.Existing.Id));
                result.Outputs.Add("type", Kit.Id(p.Existing.Id));
                return;
            }
            // Materials first: one per distinct missing name.
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
            if (p.Existing != null)
            {
                type = p.Existing;
                result.Modified.Add(Kit.Id(type.Id));
            }
            else
            {
                type = p.Source!.Duplicate(p.Name) as WallType ?? throw new OpFailedException("create_failed", "duplicating '" + p.Source.Name + "' did not give a wall type");
                result.Created.Insert(0, Kit.Id(type.Id));
            }
            if (type.Function != p.Function) type.Function = p.Function;
            type.SetCompoundStructure(Structure(p, created));
            result.Outputs.Add("type", Kit.Id(type.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("type");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is WallType t))
            {
                check.Fail("wall type '" + args.GetString("name") + "' not found after the op");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            check.Text(rb, "name", t.Name, args.GetString("name"));
            check.Text(rb, "kind", t.Kind.ToString(), WallKind.Basic.ToString());
            check.Text(rb, "function", t.Function.ToString(), Functions[args.GetString("function")].ToString());
            var cs = t.GetCompoundStructure();
            var layers = new JsonArray();
            var spec = args.GetObjectList("layers");
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
                        bool wantCore = anyCore ? s.GetBool("core") : s.GetString("function") == "structure";
                        check.Flag(o, "core", isCore, wantCore, "layer " + i + " core");
                        if (!isCore) check.Flag(o, "wraps", cs.ParticipatesInWrapping(i), s.GetBool("wraps"), "layer " + i + " wraps");
                    }
                    layers.Add(o);
                }
                rb.Add("wrapAtInserts", cs.OpeningWrapping.ToString());
                rb.Add("wrapAtEnds", cs.EndCap.ToString());
                if (cs.OpeningWrapping != InsertWrap[args.GetString("wrapAtInserts")]) check.Fail("wrap at inserts reads " + cs.OpeningWrapping);
                if (cs.EndCap != EndWrap[args.GetString("wrapAtEnds")]) check.Fail("wrap at ends reads " + cs.EndCap);
                double expected = spec.Sum(s => s.GetString("function") == "membrane" ? 0 : s.GetLength("thickness"));
                check.Length(rb, "width", t.Width, expected, "total width");
            }
            rb.Add("layers", layers);
            rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
            int materials = result.Created.Count(c => c != idText);
            if (materials > 0) rb.Add("materialsCreated", materials);
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            result.Outputs.Set("width", u.LengthJson(t.Width));
            check.Apply(result);
        }
    }
}
