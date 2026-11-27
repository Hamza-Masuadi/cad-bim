using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// material.ensure (Revit): materials with identity data, shading, surface and cut patterns, a render appearance
    /// and shared thermal or structural assets.
    ///
    /// New materials: Material.Create, or basedOn.Duplicate(name). Identity values go into the material's own
    /// parameters (ALL_MODEL_DESCRIPTION, _MANUFACTURER, _MODEL, _MARK, _URL, _COST, _TYPE_COMMENTS, KEYNOTE_PARAM, and the
    /// assembly and classification codes from Compat); a parameter that is missing or read-only is skipped and
    /// reported in readback.skipped, not failed. class and category are Material.MaterialClass and MaterialCategory.
    /// Shading: Color, Transparency, UseRenderAppearanceForShading (new materials: false unless given). Patterns: the
    /// four pattern ids and colours; only the surface foreground accepts a model pattern.
    ///
    /// Appearance (each material gets its own asset named like it unless shareAsset):
    /// from = an AppearanceAssetElement by name, else the asset of a material of that name, else a library asset
    /// (Application.GetAssets(AssetType.Appearance), by Name then Title). A document asset is used as is with shareAsset,
    /// otherwise AppearanceAssetElement.Duplicate(materialName); a library asset is AppearanceAssetElement.Create(doc,
    /// materialName, asset). Without from, colour and texture edit the material's current asset, after duplicating it
    /// when other materials use it; a material with no asset (Material.Create makes none) starts from a library Generic
    /// appearance with no image. An existing asset that other materials also use is edited only with shareAsset, and
    /// is then declared as modified. Edits run in an AppearanceAssetEditScope (Generic.GenericDiffuse colour; its
    /// connected UnifiedBitmap for the image, added with AddConnectedAsset("UnifiedBitmapSchema") only when
    /// IsValidSchemaIdentifier accepts that string, which the API docs do not list) and Commit inside the op's
    /// transaction; any failure cancels the scope and fails the op.
    ///
    /// An existing material is compared on the fields given (appearance: colour, image path, and the asset when
    /// shareAsset); a difference is refused unless update. Patterns and materials that an earlier fillpattern.ensure
    /// op of the same job creates are accepted in preflight with a warning.
    /// </summary>
    public sealed class MaterialEnsure : OpHandler
    {
        public override string Name => "material.ensure";

        private enum Appear { None, Share, Duplicate, Library, Edit }

        private sealed class Slot
        {
            public string Key = "";
            public ElementId? Pattern;
            public bool PatternPending;
            public Color? Color;
        }

        private sealed class Item
        {
            public OpArgs Spec = null!;
            public string Name = "";
            public Material? Existing;
            public Material? BasedOn;
            public List<KeyValuePair<string, JsonValue>> Identity = new List<KeyValuePair<string, JsonValue>>();
            public List<Slot> Slots = new List<Slot>();
            public Appear Appearance;
            public AppearanceAssetElement? AssetSource;
            public Asset? LibraryAsset;
            public Color? DiffuseColor;
            public OpArgs? Texture;
            public ElementId? Thermal, Structural;
            public bool Matches;
            public List<string> Differences = new List<string>();
        }

        private static BuiltInParameter? IdentityParam(string key)
        {
            switch (key)
            {
                case "description": return BuiltInParameter.ALL_MODEL_DESCRIPTION;
                case "manufacturer": return BuiltInParameter.ALL_MODEL_MANUFACTURER;
                case "model": return BuiltInParameter.ALL_MODEL_MODEL;
                case "mark": return BuiltInParameter.ALL_MODEL_MARK;
                case "keynote": return BuiltInParameter.KEYNOTE_PARAM;
                case "url": return BuiltInParameter.ALL_MODEL_URL;
                case "typeComments": return BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
                case "cost": return BuiltInParameter.ALL_MODEL_COST;
                case "assemblyCode": return Wave2ModelCompat.AssemblyCode;
                case "classificationCode": return Wave2ModelCompat.ClassificationCode;
                default: return null;
            }
        }

        private static JsonValue ReadIdentity(Material m, string key)
        {
            if (key == "class") return m.MaterialClass ?? "";
            if (key == "category") return m.MaterialCategory ?? "";
            var p = m.get_Parameter(IdentityParam(key)!.Value);
            if (p is null) return JsonValue.Null;
            if (p.StorageType == StorageType.Double) return RevitUnits.Num(p.AsDouble());
            return p.AsString() ?? "";
        }

        private static bool SameIdentity(JsonValue have, JsonValue want) =>
            want is JsonNumber wn ? have is JsonNumber hn && Math.Abs(hn.Value - wn.Value) < 1e-6
                                  : have is JsonString hs && want is JsonString ws && string.Equals(hs.Value, ws.Value, StringComparison.Ordinal);

        private static ElementId GetPattern(Material m, string key)
        {
            switch (key)
            {
                case "surface.foreground": return m.SurfaceForegroundPatternId;
                case "surface.background": return m.SurfaceBackgroundPatternId;
                case "cut.foreground": return m.CutForegroundPatternId;
                default: return m.CutBackgroundPatternId;
            }
        }

        private static Color GetPatternColor(Material m, string key)
        {
            switch (key)
            {
                case "surface.foreground": return m.SurfaceForegroundPatternColor;
                case "surface.background": return m.SurfaceBackgroundPatternColor;
                case "cut.foreground": return m.CutForegroundPatternColor;
                default: return m.CutBackgroundPatternColor;
            }
        }

        private static void SetSlot(Material m, Slot s)
        {
            switch (s.Key)
            {
                case "surface.foreground":
                    if (s.Pattern != null) m.SurfaceForegroundPatternId = s.Pattern;
                    if (s.Color != null) m.SurfaceForegroundPatternColor = s.Color;
                    break;
                case "surface.background":
                    if (s.Pattern != null) m.SurfaceBackgroundPatternId = s.Pattern;
                    if (s.Color != null) m.SurfaceBackgroundPatternColor = s.Color;
                    break;
                case "cut.foreground":
                    if (s.Pattern != null) m.CutForegroundPatternId = s.Pattern;
                    if (s.Color != null) m.CutForegroundPatternColor = s.Color;
                    break;
                default:
                    if (s.Pattern != null) m.CutBackgroundPatternId = s.Pattern;
                    if (s.Color != null) m.CutBackgroundPatternColor = s.Color;
                    break;
            }
        }

        private static bool SameColor(Color? a, Color? b) => a != null && b != null && a.IsValid && b.IsValid && a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;

        private static string? LocalPathProblem(string path)
        {
            string norm = path.Replace('/', '\\');
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (norm.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (profile.Length > 0 && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)))
                return "the texture '" + path + "' is inside a user profile; other machines will not find it. Use a shared texture folder, or set allowLocalPath:true";
            return null;
        }

        internal static List<Material> Materials(Document doc) => new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();

        private static AppearanceAssetElement? AssetOf(Document doc, Material? m) =>
            m != null && ElementIds.IsValid(m.AppearanceAssetId) ? doc.GetElement(m.AppearanceAssetId) as AppearanceAssetElement : null;

        private static Color? DiffuseOf(AppearanceAssetElement? a)
        {
            try { return (a?.GetRenderingAsset()?.FindByName(Generic.GenericDiffuse) as AssetPropertyDoubleArray4d)?.GetValueAsColor(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static string? BitmapOf(AppearanceAssetElement? a)
        {
            try
            {
                var bmp = a?.GetRenderingAsset()?.FindByName(Generic.GenericDiffuse)?.GetSingleConnectedAsset();
                return (bmp?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString)?.Value;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        }

        private static bool SamePath(string? stored, string wanted)
        {
            if (stored is null) return false;
            string a = stored.Replace('/', '\\').Trim(), b = wanted.Replace('/', '\\').Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return !Path.IsPathRooted(a) && b.EndsWith("\\" + a.TrimStart('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static List<Item>? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var all = Materials(doc);
            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool update = args.GetBool("update");
            foreach (var o in args.GetObjectList("materials"))
            {
                var it = new Item { Spec = o, Name = o.GetString("name") };
                string at = o.Path + ".name";
                if (Kit.BadTypeName(it.Name) is string bad && (bad.Contains("does not allow") || bad.Contains("empty") || bad.Contains("spaces"))) OpContext.Error(issues, at, RevitIssueCodes.Invalid, bad);
                if (!seen.Add(it.Name)) OpContext.Error(issues, at, RevitIssueCodes.Invalid, "material '" + it.Name + "' is listed twice");
                var same = all.Where(m => string.Equals(m.Name, it.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count > 1) OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, same.Count + " materials are named '" + it.Name + "' (case aside)");
                else if (same.Count == 1)
                {
                    if (same[0].Name != it.Name) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "a material named '" + same[0].Name + "' exists (names differ only by case); use that exact name");
                    else it.Existing = same[0];
                }
                if (o.GetStringOrNull("basedOn") is string based)
                {
                    var b = Lookup.ByName(all, based, "material");
                    if (!b.Ok) OpContext.Error(issues, o.Path + ".basedOn", b.Code, b.Error!);
                    else it.BasedOn = b.Value;
                }

                if (o.GetObject("identity") is OpArgs id)
                    foreach (var kv in id.Raw) if (!kv.Value.IsNull) it.Identity.Add(new KeyValuePair<string, JsonValue>(kv.Key, kv.Value));

                if (o.GetObject("shading") is OpArgs sh && sh.GetStringOrNull("color") is string sc && Wave2Support.Rgb(sc) is null)
                    OpContext.Error(issues, sh.Path + ".color", RevitIssueCodes.Invalid, "colour channels must be 0-255");

                foreach (var (group, slot) in new[] { ("surface", "foreground"), ("surface", "background"), ("cut", "foreground"), ("cut", "background") })
                {
                    if (!(o.GetObject(group) is OpArgs g) || !(g.GetObject(slot) is OpArgs ps)) continue;
                    var s = new Slot { Key = group + "." + slot };
                    string pname = ps.GetString("pattern");
                    var target = ps.Has("target") && ps.GetString("target") == "model" ? FillPatternTarget.Model : FillPatternTarget.Drafting;
                    if (string.Equals(pname, "none", StringComparison.OrdinalIgnoreCase)) s.Pattern = ElementId.InvalidElementId;
                    else
                    {
                        var fpe = Wave2Support.Pattern(doc, pname, target, out string perr);
                        if (fpe != null) s.Pattern = fpe.Id;
                        else if (ctx.IsPreflight && Wave2Support.EarlierOpCreates(ctx, "fillpattern.ensure", "patterns", pname,
                                     x => x["target"] is JsonString t && t.Value == (target == FillPatternTarget.Model ? "model" : "drafting")))
                        {
                            s.PatternPending = true;
                            OpContext.Warning(issues, ps.Path + ".pattern", RevitIssueCodes.Unchecked, "pattern '" + pname + "' is created by an earlier op of this job; checked when the op runs");
                        }
                        else OpContext.Error(issues, ps.Path + ".pattern", RevitIssueCodes.NotFound, perr);
                    }
                    if (ps.GetStringOrNull("color") is string pc)
                    {
                        s.Color = Wave2Support.Rgb(pc);
                        if (s.Color is null) OpContext.Error(issues, ps.Path + ".color", RevitIssueCodes.Invalid, "colour channels must be 0-255");
                    }
                    it.Slots.Add(s);
                }

                if (o.GetObject("appearance") is OpArgs ap) PlanAppearance(ctx, it, ap, all, issues);

                foreach (var (field, aspect) in new[] { ("thermalAsset", "thermal"), ("structuralAsset", "structural") })
                {
                    if (!(o.GetStringOrNull(field) is string an)) continue;
                    var sets = new FilteredElementCollector(doc).OfClass(typeof(PropertySetElement)).Cast<PropertySetElement>().ToList();
                    var hit = Lookup.ByName(sets, an, aspect + " asset");
                    ElementId? aid = hit.Value?.Id;
                    if (aid is null && Lookup.ByName(all, an, "material").Value is Material src)
                        aid = aspect == "thermal" ? src.ThermalAssetId : src.StructuralAssetId;
                    if (aid is null || !ElementIds.IsValid(aid)) OpContext.Error(issues, o.Path + "." + field, RevitIssueCodes.NotFound, "no " + aspect + " asset or material with one named '" + an + "'");
                    else if (aspect == "thermal") it.Thermal = aid; else it.Structural = aid;
                }

                if (it.Existing != null && !Kit.HasErrors(issues))
                {
                    Compare(doc, it);
                    it.Matches = it.Differences.Count == 0;
                    if (!it.Matches)
                    {
                        if (update) ctx.DeclareModifies(it.Existing.Id);
                        else OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "material '" + it.Name + "' exists and differs: " + string.Join("; ", it.Differences.Take(6)) + "; set update:true to change it");
                    }
                }
                else if (it.Existing is null && !Material.IsNameUnique(doc, it.Name))
                    OpContext.Error(issues, at, RevitIssueCodes.Exists, "Revit reports the name '" + it.Name + "' as not unique among materials");
                // An existing asset edited in place is an element change of its own (new materials edit it too).
                bool changes = it.Existing is null || (!it.Matches && update);
                if (changes && (it.Appearance == Appear.Edit || it.Appearance == Appear.Share) && (it.DiffuseColor != null || it.Texture != null) && it.AssetSource != null)
                    ctx.DeclareModifies(it.AssetSource.Id);
                items.Add(it);
            }
            return Kit.HasErrors(issues) ? null : items;
        }

        private static void PlanAppearance(OpContext ctx, Item it, OpArgs ap, List<Material> all, List<Issue> issues)
        {
            var doc = ctx.Doc;
            bool share = ap.GetBool("shareAsset");
            if (ap.GetStringOrNull("color") is string c)
            {
                it.DiffuseColor = Wave2Support.Rgb(c);
                if (it.DiffuseColor is null) OpContext.Error(issues, ap.Path + ".color", RevitIssueCodes.Invalid, "colour channels must be 0-255");
            }
            if (ap.GetObject("texture") is OpArgs tx)
            {
                it.Texture = tx;
                string path = tx.GetString("path");
                if (!Path.IsPathRooted(path)) OpContext.Error(issues, tx.Path + ".path", RevitIssueCodes.Invalid, "the texture path must be absolute");
                else if (!File.Exists(path)) OpContext.Error(issues, tx.Path + ".path", RevitIssueCodes.NotFound, "the texture file " + path + " does not exist on this machine");
                else if (!ap.GetBool("allowLocalPath") && LocalPathProblem(path) is string lp) OpContext.Error(issues, tx.Path + ".path", RevitIssueCodes.NotAllowed, lp);
            }
            string own = it.Name;
            var ownAsset = AppearanceAssetElement.GetAppearanceAssetElementByName(doc, own);
            string? from = ap.GetStringOrNull("from");
            if (from != null && !share && ownAsset != null && it.Existing != null && it.Existing.AppearanceAssetId == ownAsset.Id)
            {
                it.AssetSource = ownAsset;
                it.Appearance = Appear.Edit;
            }
            else if (from != null)
            {
                var a = AppearanceAssetElement.GetAppearanceAssetElementByName(doc, from) ?? AssetOf(doc, Lookup.ByName(all, from, "material").Value);
                if (a != null)
                {
                    it.AssetSource = a;
                    if (share) it.Appearance = Appear.Share;
                    else if (ownAsset != null && ownAsset.Id == a.Id) it.Appearance = Appear.Edit;
                    else it.Appearance = Appear.Duplicate;
                }
                else
                {
                    var lib = doc.Application.GetAssets(AssetType.Appearance);
                    it.LibraryAsset = lib.FirstOrDefault(x => string.Equals(x.Name, from, StringComparison.OrdinalIgnoreCase))
                                      ?? lib.FirstOrDefault(x => string.Equals(x.Title, from, StringComparison.OrdinalIgnoreCase));
                    if (it.LibraryAsset is null) { OpContext.Error(issues, ap.Path + ".from", RevitIssueCodes.NotFound, "no appearance asset, material or library appearance named '" + from + "'"); return; }
                    it.Appearance = Appear.Library;
                }
            }
            else if (it.DiffuseColor != null || it.Texture != null)
            {
                var cur = AssetOf(doc, it.Existing ?? it.BasedOn);
                if (cur is null)
                {
                    // Material.Create gives a material without an appearance asset: start from a plain library Generic one.
                    it.LibraryAsset = GenericLibraryAsset(doc);
                    if (it.LibraryAsset is null) { OpContext.Error(issues, ap.Path, RevitIssueCodes.Invalid, "material '" + it.Name + "' has no appearance asset to edit and no plain Generic library appearance was found; give appearance.from"); return; }
                    it.Appearance = Appear.Library;
                }
                else
                {
                    it.AssetSource = cur;
                    int users = all.Count(m => m.AppearanceAssetId == cur.Id && (it.Existing is null || m.Id != it.Existing.Id));
                    it.Appearance = it.Existing is null || (users > 0 && !share) ? Appear.Duplicate : Appear.Edit;
                }
            }
            if ((it.Appearance == Appear.Duplicate || it.Appearance == Appear.Library) && ownAsset != null)
                OpContext.Error(issues, ap.Path, RevitIssueCodes.Exists, "an appearance asset named '" + own + "' exists (id " + Kit.Id(ownAsset.Id) + "); give it as from, or rename it");
            if ((it.Appearance == Appear.Edit || it.Appearance == Appear.Share) && (it.DiffuseColor != null || it.Texture != null) && it.AssetSource != null)
            {
                var src = it.AssetSource;
                int others = all.Count(m => m.AppearanceAssetId == src.Id && (it.Existing is null || m.Id != it.Existing.Id));
                if (others > 0 && !share)
                    OpContext.Error(issues, ap.Path, RevitIssueCodes.NotAllowed, "the appearance asset '" + src.Name + "' is also used by " + others + " other material(s); editing its colour or image would change them too. Set shareAsset:true to edit it for all of them, or rename that asset so this material gets its own copy");
                else if (others > 0)
                    OpContext.Warning(issues, ap.Path, RevitIssueCodes.Mismatch, "the appearance asset '" + src.Name + "' is shared with " + others + " other material(s); shareAsset edits it for all of them");
            }
            if (it.DiffuseColor != null && it.Texture is null)
            {
                Asset? start = it.LibraryAsset;
                try { start ??= it.AssetSource?.GetRenderingAsset(); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                bool image;
                try { image = start?.FindByName(Generic.GenericDiffuse)?.GetSingleConnectedAsset() != null; } catch (Autodesk.Revit.Exceptions.ApplicationException) { image = false; }
                if (image)
                    OpContext.Warning(issues, ap.Path + ".color", RevitIssueCodes.Unchecked, "the appearance shows an image in its diffuse slot, which hides the colour; the colour is still written and read back");
            }
        }

        /// <summary>A library Generic appearance whose diffuse colour has no image connected, preferring one named Generic.</summary>
        private static Asset? GenericLibraryAsset(Document doc)
        {
            bool Plain(Asset a)
            {
                try { return a.FindByName(Generic.GenericDiffuse) is AssetPropertyDoubleArray4d d && d.GetSingleConnectedAsset() is null; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
            }
            var lib = doc.Application.GetAssets(AssetType.Appearance);
            return lib.FirstOrDefault(a => (string.Equals(a.Name, "Generic", StringComparison.OrdinalIgnoreCase) || string.Equals(a.Title, "Generic", StringComparison.OrdinalIgnoreCase)) && Plain(a))
                   ?? lib.FirstOrDefault(Plain);
        }

        private static void Compare(Document doc, Item it)
        {
            var m = it.Existing!;
            var d = it.Differences;
            foreach (var kv in it.Identity)
            {
                if (kv.Key != "class" && kv.Key != "category" && m.get_Parameter(IdentityParam(kv.Key)!.Value) is null) continue;
                var have = ReadIdentity(m, kv.Key);
                if (!SameIdentity(have, kv.Value)) d.Add(kv.Key + " is " + have.ToJson() + ", requested " + kv.Value.ToJson());
            }
            if (it.Spec.GetObject("shading") is OpArgs sh)
            {
                if (sh.GetStringOrNull("color") is string c && !SameColor(m.Color, Wave2Support.Rgb(c))) d.Add("shading colour " + Wave2Support.RgbText(m.Color) + ", requested " + c);
                if (sh.Has("transparency") && m.Transparency != sh.GetInt("transparency")) d.Add("transparency " + m.Transparency + ", requested " + sh.GetInt("transparency"));
                if (sh.Has("useRenderAppearance") && m.UseRenderAppearanceForShading != sh.GetBool("useRenderAppearance")) d.Add("use render appearance " + m.UseRenderAppearanceForShading);
            }
            foreach (var s in it.Slots)
            {
                if (s.PatternPending) { d.Add(s.Key + " pattern is created by this job"); continue; }
                if (s.Pattern != null && GetPattern(m, s.Key) != s.Pattern) d.Add(s.Key + " pattern differs");
                if (s.Color != null && !SameColor(GetPatternColor(m, s.Key), s.Color)) d.Add(s.Key + " colour " + Wave2Support.RgbText(GetPatternColor(m, s.Key)));
            }
            var asset = AssetOf(doc, m);
            if (it.Appearance == Appear.Share && (asset is null || asset.Id != it.AssetSource!.Id)) d.Add("appearance asset is '" + (asset?.Name ?? "none") + "', requested '" + it.AssetSource!.Name + "'");
            if ((it.Appearance == Appear.Duplicate || it.Appearance == Appear.Library) && it.Spec.GetObject("appearance")?.GetStringOrNull("from") != null)
                d.Add("appearance asset is '" + (asset?.Name ?? "none") + "', requested a copy of '" + (it.AssetSource?.Name ?? it.LibraryAsset?.Name) + "'");
            if (it.DiffuseColor != null && !SameColor(DiffuseOf(asset), it.DiffuseColor)) d.Add("appearance colour " + Wave2Support.RgbText(DiffuseOf(asset)));
            if (it.Texture != null && !SamePath(BitmapOf(asset), it.Texture.GetString("path"))) d.Add("appearance image '" + (BitmapOf(asset) ?? "none") + "'");
            if (it.Thermal != null && m.ThermalAssetId != it.Thermal) d.Add("thermal asset differs");
            if (it.Structural != null && m.StructuralAssetId != it.Structural) d.Add("structural asset differs");
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var items = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            var map = new JsonObject();
            var assets = new JsonObject();
            var states = new JsonObject();
            var skipped = new JsonArray();
            foreach (var it in items!)
            {
                Material m;
                if (it.Existing != null && it.Matches)
                {
                    m = it.Existing;
                    result.Unchanged.Add(Kit.Id(m.Id));
                    states.Set(it.Name, "unchanged");
                }
                else
                {
                    if (it.Existing != null)
                    {
                        m = it.Existing;
                        result.Modified.Add(Kit.Id(m.Id));
                        states.Set(it.Name, "updated");
                    }
                    else
                    {
                        m = it.BasedOn != null ? it.BasedOn.Duplicate(it.Name) : doc.GetElement(Material.Create(doc, it.Name)) as Material
                            ?? throw new OpFailedException("create_failed", "Revit did not create material '" + it.Name + "'");
                        if (m is null) throw new OpFailedException("create_failed", "Revit did not create material '" + it.Name + "'");
                        result.Created.Add(Kit.Id(m.Id));
                        states.Set(it.Name, "created");
                        var shNew = it.Spec.GetObject("shading");
                        if (shNew is null || !shNew.Has("useRenderAppearance")) m.UseRenderAppearanceForShading = false;
                    }
                    ApplyFields(doc, it, m, skipped);
                    ApplyAppearance(doc, u, it, m, result);
                }
                map.Set(it.Name, Kit.Id(m.Id));
                if (ElementIds.IsValid(m.AppearanceAssetId)) assets.Set(it.Name, Kit.Id(m.AppearanceAssetId));
            }
            result.Outputs.Add("materials", map);
            result.Outputs.Add("appearanceAssets", assets);
            result.Readback.Add("state", states);
            if (skipped.Count > 0) result.Readback.Add("skipped", skipped);
        }

        private static void ApplyFields(Document doc, Item it, Material m, JsonArray skipped)
        {
            foreach (var kv in it.Identity)
            {
                if (kv.Key == "class") { m.MaterialClass = ((JsonString)kv.Value).Value; continue; }
                if (kv.Key == "category") { m.MaterialCategory = ((JsonString)kv.Value).Value; continue; }
                var p = m.get_Parameter(IdentityParam(kv.Key)!.Value);
                bool ok = p != null && !p.IsReadOnly &&
                          (kv.Value is JsonNumber n ? p.StorageType == StorageType.Double && p.Set(n.Value)
                                                    : p.StorageType == StorageType.String && p.Set(((JsonString)kv.Value).Value));
                if (!ok) skipped.Add(new JsonObject().Add("material", it.Name).Add("field", kv.Key).Add("reason", p is null ? "no such parameter on this material" : p.IsReadOnly ? "read-only" : "Revit refused the value"));
            }
            if (it.Spec.GetObject("shading") is OpArgs sh)
            {
                if (Wave2Support.Rgb(sh.GetStringOrNull("color")) is Color c) m.Color = c;
                if (sh.Has("transparency")) m.Transparency = sh.GetInt("transparency");
                if (sh.Has("useRenderAppearance")) m.UseRenderAppearanceForShading = sh.GetBool("useRenderAppearance");
            }
            foreach (var s in it.Slots)
            {
                if (s.PatternPending) throw new OpFailedException("validation_failed", "a pattern of " + s.Key + " was still missing when the op ran");
                SetSlot(m, s);
            }
            if (it.Thermal != null) m.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, it.Thermal);
            if (it.Structural != null) m.SetMaterialAspectByPropertySet(MaterialAspect.Structural, it.Structural);
        }

        private static void ApplyAppearance(Document doc, RevitUnits u, Item it, Material m, OpResult result)
        {
            if (it.Appearance == Appear.None) return;
            ElementId assetId;
            switch (it.Appearance)
            {
                case Appear.Share:
                    assetId = it.AssetSource!.Id;
                    if ((it.DiffuseColor != null || it.Texture != null) && !result.Created.Contains(Kit.Id(assetId))) result.Modified.Add(Kit.Id(assetId));
                    break;
                case Appear.Library:
                    assetId = AppearanceAssetElement.Create(doc, it.Name, it.LibraryAsset!).Id;
                    result.Created.Add(Kit.Id(assetId));
                    break;
                case Appear.Duplicate:
                    assetId = it.AssetSource!.Duplicate(it.Name).Id;
                    result.Created.Add(Kit.Id(assetId));
                    break;
                default:
                    assetId = it.AssetSource!.Id;
                    if (!result.Created.Contains(Kit.Id(assetId))) result.Modified.Add(Kit.Id(assetId));
                    break;
            }
            m.AppearanceAssetId = assetId;
            if (it.DiffuseColor is null && it.Texture is null) return;
            using (var scope = new AppearanceAssetEditScope(doc))
            {
                var asset = scope.Start(assetId);
                try
                {
                    var diffuse = asset.FindByName(Generic.GenericDiffuse) as AssetPropertyDoubleArray4d
                        ?? throw new OpFailedException("appearance_unsupported", "the appearance asset of '" + it.Name + "' has no generic diffuse colour (it is not a Generic appearance); give appearance.from a Generic asset");
                    if (it.DiffuseColor != null)
                    {
                        if (!diffuse.IsEditable()) throw new OpFailedException("appearance_unsupported", "the diffuse colour of '" + it.Name + "' is not editable");
                        diffuse.SetValueAsColor(it.DiffuseColor);
                    }
                    if (it.Texture is OpArgs tx)
                    {
                        var bmp = diffuse.GetSingleConnectedAsset();
                        if (bmp is null)
                        {
                            const string schema = "UnifiedBitmapSchema";
                            if (!diffuse.IsValidSchemaIdentifier(schema))
                                throw new OpFailedException("texture_slot_missing", "the appearance of '" + it.Name + "' has no image slot and Revit does not accept '" + schema + "' to add one; give appearance.from an asset that already has an image");
                            diffuse.AddConnectedAsset(schema);
                            bmp = diffuse.GetSingleConnectedAsset() ?? throw new OpFailedException("texture_slot_missing", "adding an image slot to '" + it.Name + "' gave none");
                        }
                        SetString(bmp, UnifiedBitmap.UnifiedbitmapBitmap, tx.GetString("path"));
                        if (tx.GetLengthOrNull("width") is double w) SetDistance(bmp, UnifiedBitmap.TextureRealWorldScaleX, w, u);
                        if (tx.GetLengthOrNull("height") is double h) SetDistance(bmp, UnifiedBitmap.TextureRealWorldScaleY, h, u);
                        SetDistance(bmp, UnifiedBitmap.TextureRealWorldOffsetX, tx.GetLength("offsetX"), u);
                        SetDistance(bmp, UnifiedBitmap.TextureRealWorldOffsetY, tx.GetLength("offsetY"), u);
                        if (bmp.FindByName(UnifiedBitmap.TextureWAngle) is AssetPropertyDouble ang && ang.IsEditable()) ang.Value = tx.GetAngle("rotation");
                        foreach (var rep in new[] { UnifiedBitmap.TextureURepeat, UnifiedBitmap.TextureVRepeat })
                            if (bmp.FindByName(rep) is AssetPropertyBoolean b && b.IsEditable()) b.Value = tx.GetBool("repeat");
                    }
                    scope.Commit(true);
                }
                catch (Exception)
                {
                    if (scope.IsActive) scope.Cancel();
                    throw;
                }
            }
        }

        private static void SetString(Asset a, string name, string value)
        {
            if (!(a.FindByName(name) is AssetPropertyString p) || !p.IsEditable() || !p.IsValidValue(value))
                throw new OpFailedException("appearance_unsupported", "the image property " + name + " cannot be set to '" + value + "'");
            p.Value = value;
        }

        private static void SetDistance(Asset a, string name, double jobValue, RevitUnits u)
        {
            if (!(a.FindByName(name) is AssetPropertyDistance p) || !p.IsEditable())
                throw new OpFailedException("appearance_unsupported", "the image property " + name + " is missing or not editable");
            p.Value = UnitUtils.Convert(jobValue, RevitUnits.ForJobUnit(u.JobUnit), p.GetUnitTypeId());
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var map = result.Outputs["materials"] as JsonObject ?? new JsonObject();
            var skipped = (result.Readback["skipped"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(o => (o.GetStringOrNull("material") ?? "") + "/" + (o.GetStringOrNull("field") ?? "")).ToHashSet(StringComparer.Ordinal);
            var list = new JsonArray();
            foreach (var o in args.GetObjectList("materials"))
            {
                string name = o.GetString("name");
                string? idText = map.GetStringOrNull(name);
                if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Material m)) { check.Fail("material '" + name + "' not found after the op"); continue; }
                var rb = new JsonObject().Add("name", name).Add("id", idText);
                check.Text(rb, "readName", m.Name, name, "material name");
                if (o.GetObject("identity") is OpArgs idn)
                {
                    var io = new JsonObject();
                    foreach (var kv in idn.Raw)
                    {
                        if (kv.Value.IsNull || skipped.Contains(name + "/" + kv.Key)) continue;
                        var have = ReadIdentity(m, kv.Key);
                        io.Add(kv.Key, have.DeepClone());
                        if (!SameIdentity(have, kv.Value)) check.Fail("material '" + name + "' " + kv.Key + " reads " + have.ToJson() + ", requested " + kv.Value.ToJson());
                    }
                    rb.Add("identity", io);
                }
                if (o.GetObject("shading") is OpArgs sh)
                {
                    check.Text(rb, "color", Wave2Support.RgbText(m.Color), sh.GetStringOrNull("color") is string c ? Wave2Support.RgbText(Wave2Support.Rgb(c)) : null, "material '" + name + "' shading colour");
                    rb.Add("transparency", m.Transparency);
                    if (sh.Has("transparency") && m.Transparency != sh.GetInt("transparency")) check.Fail("material '" + name + "' transparency reads " + m.Transparency);
                    check.Flag(rb, "useRenderAppearance", m.UseRenderAppearanceForShading, sh.Has("useRenderAppearance") ? sh.GetBool("useRenderAppearance") : (bool?)null, "material '" + name + "' use render appearance");
                }
                foreach (var (group, slot) in new[] { ("surface", "foreground"), ("surface", "background"), ("cut", "foreground"), ("cut", "background") })
                {
                    if (!(o.GetObject(group) is OpArgs g) || !(g.GetObject(slot) is OpArgs ps)) continue;
                    string key = group + "." + slot;
                    var pid = GetPattern(m, key);
                    string pname = ps.GetString("pattern");
                    var target = ps.Has("target") && ps.GetString("target") == "model" ? FillPatternTarget.Model : FillPatternTarget.Drafting;
                    var want = string.Equals(pname, "none", StringComparison.OrdinalIgnoreCase) ? ElementId.InvalidElementId : Wave2Support.Pattern(doc, pname, target, out _)?.Id;
                    rb.Add(key, ElementIds.IsValid(pid) ? (JsonValue)(doc.GetElement(pid)?.Name ?? Kit.Id(pid)) : "none");
                    if (want is null || pid != want) check.Fail("material '" + name + "' " + key + " pattern reads '" + (ElementIds.IsValid(pid) ? doc.GetElement(pid)?.Name : "none") + "', requested '" + pname + "'");
                    if (ps.GetStringOrNull("color") is string pc && !SameColor(GetPatternColor(m, key), Wave2Support.Rgb(pc)))
                        check.Fail("material '" + name + "' " + key + " colour reads " + Wave2Support.RgbText(GetPatternColor(m, key)) + ", requested " + pc);
                }
                if (o.GetObject("appearance") is OpArgs ap)
                {
                    var asset = AssetOf(doc, m);
                    rb.Add("appearanceAsset", asset is null ? (JsonValue)JsonValue.Null : new JsonObject().Add("id", Kit.Id(asset.Id)).Add("name", asset.Name));
                    if (asset is null) check.Fail("material '" + name + "' has no appearance asset");
                    else
                    {
                        if (!ap.GetBool("shareAsset") && asset.Name != name && (ap.Has("from") || ap.Has("color") || ap.Has("texture")))
                            check.Fail("material '" + name + "' uses appearance asset '" + asset.Name + "', expected its own asset named like it");
                        if (ap.GetStringOrNull("color") is string ac)
                            check.Text(rb, "appearanceColor", Wave2Support.RgbText(DiffuseOf(asset)), Wave2Support.RgbText(Wave2Support.Rgb(ac)), "material '" + name + "' appearance colour");
                        if (ap.GetObject("texture") is OpArgs tx)
                        {
                            string? stored = BitmapOf(asset);
                            rb.Add("imageStored", stored is null ? (JsonValue)JsonValue.Null : stored);
                            if (!SamePath(stored, tx.GetString("path"))) check.Fail("material '" + name + "' image reads '" + (stored ?? "none") + "', requested '" + tx.GetString("path") + "'");
                        }
                    }
                }
                if (o.GetStringOrNull("thermalAsset") != null) rb.Add("thermalAsset", Kit.Id(m.ThermalAssetId));
                if (o.GetStringOrNull("structuralAsset") != null) rb.Add("structuralAsset", Kit.Id(m.StructuralAssetId));
                list.Add(rb);
            }
            result.Readback.Add("materials", list);
            check.Apply(result);
        }
    }
}
