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

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// material.assign (Revit): one material, "none" to clear, onto exactly one target.
    ///
    /// layer: CompoundStructure.SetMaterialId on a wall, floor, ceiling or roof type, then SetCompoundStructure; the
    /// layer is given by index (0 = exterior of walls, top of the others) or by function, taking the matching layer
    /// nearest the given side. parameter: a material parameter (StorageType.ElementId, data type
    /// SpecTypeId.Reference.Material) of the elements or of their types; without a name the only material parameter is
    /// used, or MATERIAL_ID_PARAM when it is one of several. category: Category.Material of a category or subcategory
    /// (annotation categories are refused, as documented). paint: Document.Paint (RemovePaint for "none") on the host
    /// faces from HostObjectUtils.
    ///
    /// Type-wide targets (layer, type parameter) count the instances of each changed type, and category counts the
    /// elements of the category (of its parent for a subcategory); they pass only when the count is at most
    /// maxInstances, or with confirmTypeWide. With target type, types that ownership "skip" left out are skipped. SetMaterialId does not check that the id is a material,
    /// so the material is always resolved by name first. A material that an earlier material.ensure op of the same job
    /// creates is accepted in preflight with a warning.
    ///
    /// Declared as modified: the changed types, elements, or painted hosts; for a category, its graphics style
    /// (OpContext.DeclareModifiesCategory), which stands in for the category in the ownership pre-flight.
    /// </summary>
    public sealed class MaterialAssign : OpHandler
    {
        public override string Name => "material.assign";

        private sealed class Plan
        {
            public ElementId Material = ElementId.InvalidElementId;
            public string Target = "";
            public HostObjAttributes? Type;
            public int LayerIndex = -1;
            public List<Parameter> Parameters = new List<Parameter>();
            public Category? Category;
            public List<KeyValuePair<HostObject, string>> Faces = new List<KeyValuePair<HostObject, string>>();
            public int Instances;
            public int Unchanged;
        }

        private static IList<Reference> FaceRefs(HostObject h, string face)
        {
            switch (face)
            {
                case "top": return HostObjectUtils.GetTopFaces(h);
                case "bottom": return HostObjectUtils.GetBottomFaces(h);
                case "exterior": return HostObjectUtils.GetSideFaces(h, ShellLayerType.Exterior);
                default: return HostObjectUtils.GetSideFaces(h, ShellLayerType.Interior);
            }
        }

        private static IEnumerable<Face> Faces(HostObject h, string face) =>
            FaceRefs(h, face).Select(r => h.GetGeometryObjectFromReference(r) as Face).Where(f => f != null).Cast<Face>();

        private static int InstancesOf(Document doc, ElementType t) =>
            t.Category is null ? 0 : new FilteredElementCollector(doc).OfCategoryId(t.Category.Id).WhereElementIsNotElementType().Count(e => e.GetTypeId() == t.Id);

        private static bool IsMaterialParam(Parameter p)
        {
            if (p.StorageType != StorageType.ElementId) return false;
            try { return p.Definition?.GetDataType() == SpecTypeId.Reference.Material; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        private static Parameter? MaterialParam(Element holder, string? name, out string error)
        {
            error = "";
            if (name != null)
            {
                var p = ParamAccess.Find(holder, name, out string? err);
                if (p is null) { error = err ?? "no parameter '" + name + "'"; return null; }
                if (!IsMaterialParam(p)) { error = "parameter '" + name + "' is not a material parameter"; return null; }
                return p;
            }
            var all = holder.Parameters.Cast<Parameter>().Where(IsMaterialParam).ToList();
            if (all.Count == 1) return all[0];
            if (all.Count == 0) { error = "element " + Kit.Id(holder.Id) + " has no material parameter"; return null; }
            var main = holder.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (main != null && all.Any(p => p.Id == main.Id)) return main;
            error = "element " + Kit.Id(holder.Id) + " has " + all.Count + " material parameters (" + string.Join(", ", all.Select(p => "'" + p.Definition.Name + "'").Take(6)) + "); name one";
            return null;
        }

        private static ElementId? ResolveMaterial(OpContext ctx, OpArgs args, List<Issue> issues, out bool pending)
        {
            pending = false;
            string name = args.GetString("material");
            if (string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
            var m = CompoundTypeEnsure.FindMaterial(ctx.Doc, name, out bool amb);
            if (m != null) return m.Id;
            if (!amb && ctx.IsPreflight && Wave2Support.EarlierOpCreates(ctx, "material.ensure", "materials", name))
            {
                pending = true;
                OpContext.Warning(issues, ctx.FieldPath(args, "material"), RevitIssueCodes.Unchecked, "material '" + name + "' is created by an earlier op of this job; checked when the op runs");
                return null;
            }
            OpContext.Error(issues, ctx.FieldPath(args, "material"), amb ? RevitIssueCodes.Ambiguous : RevitIssueCodes.NotFound,
                amb ? "several materials are named '" + name + "'" : "no material named '" + name + "'" + Lookup.Suggest(name, MaterialEnsure.Materials(ctx.Doc).Select(x => x.Name).ToList()));
            return null;
        }

        private static void Gate(OpArgs args, OpContext ctx, int instances, string what, List<Issue> issues)
        {
            if (instances == 0 || args.GetBool("confirmTypeWide")) return;
            int? max = args.Has("maxInstances") ? args.GetInt("maxInstances") : (int?)null;
            if (max.HasValue && instances <= max.Value) return;
            OpContext.Error(issues, ctx.FieldPath(args, "confirmTypeWide"), RevitIssueCodes.NotAllowed,
                "changing " + what + " affects " + instances + " instance(s)" + (max.HasValue ? ", more than maxInstances " + max.Value : "") + "; set confirmTypeWide:true or raise maxInstances");
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var mat = ResolveMaterial(ctx, args, issues, out bool matPending);
            if (mat is null && !matPending) return null;
            p.Material = mat ?? ElementId.InvalidElementId;

            if (args.GetObject("layer") is OpArgs lay)
            {
                p.Target = "layer";
                string tname = lay.GetString("type");
                var kinds = lay.GetStringOrNull("kind") is string k ? new[] { k } : new[] { "wall", "floor", "ceiling", "roof" };
                var candidates = kinds.SelectMany(kk => kk == "wall" ? new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<HostObjAttributes>() : CompoundTypeEnsure.TypesOf(doc, kk)).ToList();
                var t = Lookup.ByName(candidates, tname, "type");
                if (!t.Ok) { OpContext.Error(issues, lay.Path + ".type", t.Code, t.Error! + (t.Code == RevitIssueCodes.Ambiguous ? "; give kind" : "")); return null; }
                p.Type = t.Value!;
                var cs = p.Type.GetCompoundStructure();
                if (cs is null || cs.LayerCount == 0) { OpContext.Error(issues, lay.Path + ".type", RevitIssueCodes.Invalid, "type '" + p.Type.Name + "' has no layers"); return null; }
                if (lay.Has("index"))
                {
                    p.LayerIndex = lay.GetInt("index");
                    if (p.LayerIndex >= cs.LayerCount) { OpContext.Error(issues, lay.Path + ".index", RevitIssueCodes.Invalid, "type '" + p.Type.Name + "' has " + cs.LayerCount + " layer(s); index " + p.LayerIndex + " is out of range"); return null; }
                }
                else
                {
                    var f = CompoundTypeEnsure.LayerFunctions[lay.GetString("function")];
                    var hits = Enumerable.Range(0, cs.LayerCount).Where(i => cs.GetLayerFunction(i) == f).ToList();
                    if (hits.Count == 0) { OpContext.Error(issues, lay.Path + ".function", RevitIssueCodes.NotFound, "type '" + p.Type.Name + "' has no " + lay.GetString("function") + " layer"); return null; }
                    string side = lay.GetString("side");
                    p.LayerIndex = side == "exterior" || side == "top" ? hits.First() : hits.Last();
                }
                if (cs.GetMaterialId(p.LayerIndex) == p.Material && !matPending) p.Unchanged = 1;
                else
                {
                    p.Instances = InstancesOf(doc, p.Type);
                    Gate(args, ctx, p.Instances, "layer " + p.LayerIndex + " of type '" + p.Type.Name + "'", issues);
                    ctx.DeclareModifies(p.Type.Id);
                }
            }
            else if (args.GetObject("parameter") is OpArgs par)
            {
                p.Target = "parameter";
                var elements = ElementSelect.Resolve(ctx, par, "elements", "filter", issues, out bool pending);
                if (!pending && elements.Count == 0 && !Kit.HasErrors(issues)) OpContext.Error(issues, par.Path, RevitIssueCodes.NotFound, "no elements selected");
                bool onType = par.GetString("target") == "type";
                var holders = new Dictionary<long, Element>();
                foreach (var e in elements)
                {
                    var h = onType ? doc.GetElement(e.GetTypeId()) : e;
                    if (h is null) { OpContext.Error(issues, par.Path, RevitIssueCodes.NotFound, "element " + Kit.Id(e.Id) + " has no type"); continue; }
                    if (onType && ctx.IsLeftOut(h.Id)) continue;
                    holders[ElementIds.ToLong(h.Id)] = h;
                }
                foreach (var h in holders.Values)
                {
                    var prm = MaterialParam(h, par.GetStringOrNull("parameter"), out string err);
                    if (prm is null) { OpContext.Error(issues, par.Path + ".parameter", RevitIssueCodes.NotFound, err); continue; }
                    if (prm.IsReadOnly) { OpContext.Error(issues, par.Path + ".parameter", RevitIssueCodes.ReadOnly, "parameter '" + prm.Definition.Name + "' of " + Kit.Id(h.Id) + " is read-only"); continue; }
                    if (prm.AsElementId() == p.Material && !matPending) { p.Unchanged++; continue; }
                    p.Parameters.Add(prm);
                    ctx.DeclareModifies(h.Id);
                    if (onType && h is ElementType et) p.Instances += InstancesOf(doc, et);
                }
                if (onType) Gate(args, ctx, p.Instances, p.Parameters.Count + " type(s)", issues);
            }
            else if (args.GetObject("category") is OpArgs cat)
            {
                p.Target = "category";
                var c = Lookup.Category(doc, cat.GetString("category"), out string? cerr);
                if (c is null) { OpContext.Error(issues, cat.Path + ".category", RevitIssueCodes.NotFound, cerr!); return null; }
                if (cat.GetStringOrNull("subcategory") is string sn)
                {
                    var subs = c.SubCategories.Cast<Category>().ToList();
                    var sc = subs.FirstOrDefault(s => string.Equals(s.Name, sn, StringComparison.Ordinal)) ?? subs.FirstOrDefault(s => string.Equals(s.Name, sn, StringComparison.OrdinalIgnoreCase));
                    if (sc is null) { OpContext.Error(issues, cat.Path + ".subcategory", RevitIssueCodes.NotFound, "category '" + c.Name + "' has no subcategory '" + sn + "'" + Lookup.Suggest(sn, subs.Select(s => s.Name).ToList())); return null; }
                    c = sc;
                }
                if (c.CategoryType == CategoryType.Annotation) { OpContext.Error(issues, cat.Path + ".category", RevitIssueCodes.NotAllowed, "'" + c.Name + "' is an annotation category; Revit does not give those a material"); return null; }
                p.Category = c;
                if ((c.Material?.Id ?? ElementId.InvalidElementId) == p.Material && !matPending) p.Unchanged = 1;
                else
                {
                    // Every element of the category that shows its material "By Category" changes; count the category's
                    // elements (the parent's, for a subcategory) as the upper bound.
                    var counted = c.Parent ?? c;
                    p.Instances = new FilteredElementCollector(doc).OfCategoryId(counted.Id).WhereElementIsNotElementType().GetElementCount();
                    Gate(args, ctx, p.Instances, "the material of category '" + c.Name + "'", issues);
                    if (!ctx.DeclareModifiesCategory(c))
                        OpContext.Warning(issues, cat.Path + ".category", RevitIssueCodes.Unchecked, "category '" + c.Name + "' has no graphics style to check ownership against; Revit checks it when the change is saved");
                }
            }
            else if (args.GetObject("paint") is OpArgs pt)
            {
                p.Target = "paint";
                var faces = pt.GetStringList("faces").Distinct().ToList();
                foreach (var kv in Kit.IdList(ctx, pt, "elements", out _))
                {
                    if (!ElementIds.TryParse(kv.Value, out var id) || !(doc.GetElement(id) is Element e)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist"); continue; }
                    if (!(e is Wall || e is Floor || e is Ceiling || e is RoofBase)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is not a wall, floor, ceiling or roof"); continue; }
                    var h = (HostObject)e;
                    foreach (var f in faces)
                    {
                        bool side = f == "exterior" || f == "interior";
                        if (side != (h is Wall)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, (h is Wall ? "walls have exterior and interior faces, not " : "floors, ceilings and roofs have top and bottom faces, not ") + f); continue; }
                        int count;
                        try { count = Faces(h, f).Count(); }
                        catch (Autodesk.Revit.Exceptions.ApplicationException ex) { OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "the " + f + " faces of " + kv.Value + " cannot be read: " + ex.Message); continue; }
                        if (count == 0) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " has no " + f + " face"); continue; }
                        p.Faces.Add(new KeyValuePair<HostObject, string>(h, f));
                    }
                    ctx.DeclareModifies(h.Id);
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
            int count = 0;
            switch (p!.Target)
            {
                case "layer":
                    if (p.Unchanged > 0) { result.Unchanged.Add(Kit.Id(p.Type!.Id)); break; }
                    var cs = p.Type!.GetCompoundStructure();
                    cs.SetMaterialId(p.LayerIndex, p.Material);
                    p.Type.SetCompoundStructure(cs);
                    result.Modified.Add(Kit.Id(p.Type.Id));
                    count = 1;
                    break;
                case "parameter":
                    foreach (var prm in p.Parameters)
                    {
                        if (!prm.Set(p.Material)) throw new OpFailedException("parameter_value", "Revit refused the material for '" + prm.Definition.Name + "' on " + Kit.Id(prm.Element.Id));
                        result.Modified.Add(Kit.Id(prm.Element.Id));
                        count++;
                    }
                    break;
                case "category":
                    if (p.Unchanged > 0) break;
                    try { p.Category!.Material = ElementIds.IsValid(p.Material) ? doc.GetElement(p.Material) as Material : null; }
                    catch (Autodesk.Revit.Exceptions.ArgumentException e) { throw new OpFailedException("category_refused", "Revit refused the material of category '" + p.Category!.Name + "': " + e.Message); }
                    count = 1;
                    break;
                case "paint":
                    foreach (var kv in p.Faces)
                        foreach (var face in Faces(kv.Key, kv.Value))
                        {
                            try
                            {
                                if (ElementIds.IsValid(p.Material)) doc.Paint(kv.Key.Id, face, p.Material);
                                else if (doc.IsPainted(kv.Key.Id, face)) doc.RemovePaint(kv.Key.Id, face);
                                else continue;
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException e) { throw new OpFailedException("paint_refused", "Revit refused to paint the " + kv.Value + " face of " + Kit.Id(kv.Key.Id) + ": " + e.Message); }
                            count++;
                        }
                    foreach (var id in p.Faces.Select(f => Kit.Id(f.Key.Id)).Distinct()) result.Modified.Add(id);
                    break;
            }
            result.Outputs.Add("count", count);
            result.Outputs.Add("instancesAffected", p.Instances);
            result.Readback.Add("target", p.Target);
            if (p.Unchanged > 0) result.Readback.Add("alreadySet", p.Unchanged);
            if (p.Type != null) result.Readback.Add("layer", new JsonObject().Add("type", Kit.Id(p.Type.Id)).Add("index", p.LayerIndex));
            if (p.Parameters.Count > 0)
                result.Readback.Add("parameters", new JsonArray(p.Parameters.Select(x => (JsonValue)new JsonObject().Add("element", Kit.Id(x.Element.Id)).Add("parameter", x.Definition.Name))));
            if (p.Category != null) result.Readback.Add("category", new JsonObject().Add("name", p.Category.Name).Add("parent", p.Category.Parent?.Name ?? ""));
            if (p.Faces.Count > 0)
                result.Readback.Add("faces", new JsonArray(p.Faces.Select(f => (JsonValue)new JsonObject().Add("element", Kit.Id(f.Key.Id)).Add("face", f.Value))));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            string name = args.GetString("material");
            var want = string.Equals(name, "none", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId : CompoundTypeEnsure.FindMaterial(doc, name, out _)?.Id;
            string Name(ElementId id) => ElementIds.IsValid(id) ? doc.GetElement(id)?.Name ?? Kit.Id(id) : "none";
            if (want is null) { check.Fail("material '" + name + "' not found after the op"); check.Apply(result); return; }
            var items = new JsonArray();
            if (result.Readback["layer"] is JsonObject lay && ElementIds.TryParse(lay.GetStringOrNull("type"), out var tid) && doc.GetElement(tid) is HostObjAttributes t)
            {
                int index = lay["index"] is JsonNumber n ? (int)n.Value : -1;
                var cs = t.GetCompoundStructure();
                var have = cs != null && index >= 0 && index < cs.LayerCount ? cs.GetMaterialId(index) : ElementId.InvalidElementId;
                items.Add(new JsonObject().Add("type", t.Name).Add("index", index).Add("material", Name(have)));
                if (have != want) check.Fail("layer " + index + " of '" + t.Name + "' reads '" + Name(have) + "', requested '" + name + "'");
            }
            foreach (var o in (result.Readback["parameters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (!ElementIds.TryParse(o.GetStringOrNull("element"), out var eid) || !(doc.GetElement(eid) is Element e)) { check.Fail("element " + o.GetStringOrNull("element") + " no longer exists"); continue; }
                var prm = ParamAccess.Find(e, o.GetStringOrNull("parameter") ?? "", out _);
                var have = prm?.AsElementId() ?? ElementId.InvalidElementId;
                items.Add(new JsonObject().Add("element", Kit.Id(e.Id)).Add("parameter", o.GetStringOrNull("parameter") ?? "").Add("material", Name(have)));
                if (have != want) check.Fail("'" + o.GetStringOrNull("parameter") + "' of " + Kit.Id(e.Id) + " reads '" + Name(have) + "', requested '" + name + "'");
            }
            if (args.GetObject("category") is OpArgs cat && Lookup.Category(doc, cat.GetString("category"), out _) is Category c)
            {
                if (cat.GetStringOrNull("subcategory") is string sn)
                    c = c.SubCategories.Cast<Category>().FirstOrDefault(s => string.Equals(s.Name, sn, StringComparison.OrdinalIgnoreCase)) ?? c;
                var have = c.Material?.Id ?? ElementId.InvalidElementId;
                items.Add(new JsonObject().Add("category", c.Name).Add("material", Name(have)));
                if (have != want) check.Fail("category '" + c.Name + "' material reads '" + Name(have) + "', requested '" + name + "'");
            }
            int painted = 0;
            foreach (var o in (result.Readback["faces"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (!ElementIds.TryParse(o.GetStringOrNull("element"), out var eid) || !(doc.GetElement(eid) is HostObject h)) { check.Fail("element " + o.GetStringOrNull("element") + " no longer exists"); continue; }
                string f = o.GetStringOrNull("face") ?? "";
                foreach (var face in Faces(h, f))
                {
                    var have = doc.IsPainted(h.Id, face) ? doc.GetPaintedMaterial(h.Id, face) : ElementId.InvalidElementId;
                    if (have != want) check.Fail("the " + f + " face of " + Kit.Id(h.Id) + " reads '" + Name(have) + "', requested '" + name + "'");
                    else painted++;
                }
            }
            if (painted > 0) result.Readback.Add("facesChecked", painted);
            result.Readback.Add("items", items);
            check.Apply(result);
        }
    }
}
