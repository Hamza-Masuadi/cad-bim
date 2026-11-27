using System;
using System.Collections.Generic;
using System.IO;
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
    /// param.shared.ensure: shared parameters in the shared parameter file, bound to categories in the project.
    ///
    /// The file is the job's "file" or the one Revit has set (Application.SharedParametersFilename); a missing file
    /// is refused rather than created, because a second shared parameter file is how duplicate GUIDs start. The
    /// application's filename is restored after every use.
    ///
    /// Definitions: a name already in the file (in any group) is used as it is, after checking its GUID (when one is
    /// requested) and data type; a missing one is created in the group with the requested GUID. The project is
    /// checked too: a project parameter or a shared parameter with another GUID under the same name is refused
    /// ("never create a second shared parameter with the same name and a different GUID", families-parameters.md).
    ///
    /// Binding: Insert(definition, binding, groupTypeId), the ForgeTypeId overload present in 2024-2027 (the
    /// BuiltInParameterGroup overload is gone from 2025). An existing binding that lacks categories, or differs in
    /// kind or group, is refused unless update is true; update never drops categories (removing a category discards
    /// its values), it re-inserts with the union.
    ///
    /// Side effect outside the model: creating a definition writes the shared parameter file at once, and a rollback
    /// would not undo that. The handler therefore always works on a copy in the op's staging folder: a rehearsal's copy
    /// is deleted, and a commit's copy replaces the file only after the job has committed, and only if nobody changed
    /// the file meanwhile (see OpContext.StageFolder). A later op of the same job reads and extends that copy.
    /// </summary>
    public sealed class ParamSharedEnsure : OpHandler
    {
        public override string Name => "param.shared.ensure";

        private static ForgeTypeId Spec(string dataType)
        {
            switch (dataType)
            {
                case "text": return SpecTypeId.String.Text;
                case "integer": return SpecTypeId.Int.Integer;
                case "number": return SpecTypeId.Number;
                case "length": return SpecTypeId.Length;
                case "area": return SpecTypeId.Area;
                case "volume": return SpecTypeId.Volume;
                case "angle": return SpecTypeId.Angle;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "url": return SpecTypeId.String.Url;
                case "material": return SpecTypeId.Reference.Material;
                default: return SpecTypeId.String.MultilineText;
            }
        }

        private sealed class Item
        {
            public string Name = "";
            public string Path = "";
            public ForgeTypeId Spec = new ForgeTypeId();
            public Guid? Guid;
            public string Description = "";
            public string? FileGroup;            // group in the file holding it, when it exists
            public Guid? FileGuid;
            public SharedParameterElement? InProject;
            public bool Bound, BindingDiffers;
            public List<string> Differences = new List<string>();
        }

        private sealed class Plan
        {
            public string File = "";
            public string Group = "";
            public List<Item> Items = new List<Item>();
            public List<Category> Categories = new List<Category>();
            public bool Instance;
            public ForgeTypeId PropertyGroup = new ForgeTypeId();
            public string PropertyGroupLabel = "";
            public bool Update;
        }

        /// <summary>Runs <paramref name="body"/> with Revit's shared parameter file set to <paramref name="path"/>, then restores the setting.</summary>
        private static T WithFile<T>(Autodesk.Revit.ApplicationServices.Application app, string path, Func<DefinitionFile, T> body,
            OpContext? warnTo = null, string? fallback = null)
        {
            string original = app.SharedParametersFilename ?? "";
            bool swap = !string.Equals(original, path, StringComparison.OrdinalIgnoreCase);
            try
            {
                if (swap) app.SharedParametersFilename = path;
                var df = app.OpenSharedParameterFile() ?? throw new OpFailedException("shared_parameter_file", "Revit could not open the shared parameter file '" + path + "'");
                return body(df);
            }
            finally
            {
                if (swap)
                {
                    // Restoring an empty setting is not documented to work; if Revit refuses, point it at the real file
                    // rather than at a staged copy that is about to be deleted.
                    try { app.SharedParametersFilename = original; }
                    catch (Exception e)
                    {
                        string now = path;
                        if (fallback != null) { try { app.SharedParametersFilename = fallback; now = fallback; } catch (Exception) { } }
                        warnTo?.Warn("Revit's shared parameter file setting could not be restored to '" + original + "' (" + e.Message + "); it now names '" + now + "'");
                    }
                }
            }
        }

        private static ForgeTypeId? GroupFor(string label, out string shown, out string error)
        {
            error = "";
            shown = label;
            if (string.Equals(label, "Other", StringComparison.OrdinalIgnoreCase)) { shown = "Other"; return new ForgeTypeId(); }
            var all = ParameterUtils.GetAllBuiltInGroups();
            var labels = new List<string>();
            foreach (var g in all)
            {
                string l;
                try { l = LabelUtils.GetLabelForGroup(g); } catch (Exception) { continue; }
                labels.Add(l);
                if (string.Equals(l, label, StringComparison.OrdinalIgnoreCase) || string.Equals(g.TypeId, label, StringComparison.OrdinalIgnoreCase)) { shown = l; return g; }
            }
            error = "no properties group '" + label + "'" + Lookup.Suggest(label, labels.Distinct().ToList());
            return null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool afterCommit = false)
        {
            var doc = ctx.Doc;
            var app = doc.Application;
            var p = new Plan { Group = args.GetString("group").Trim(), Instance = args.GetString("binding") == "instance", Update = args.GetBool("update") };
            p.File = args.GetStringOrNull("file")?.Trim() ?? app.SharedParametersFilename ?? "";
            string fileAt = ctx.FieldPath(args, "file");
            if (p.File.Length == 0) { OpContext.Error(issues, fileAt, RevitIssueCodes.NotFound, "Revit has no shared parameter file set; give file"); return null; }
            if (!Path.IsPathRooted(p.File)) { OpContext.Error(issues, fileAt, RevitIssueCodes.Invalid, "file must be an absolute path"); return null; }
            if (!File.Exists(p.File)) { OpContext.Error(issues, fileAt, RevitIssueCodes.NotFound, "shared parameter file not found: " + p.File + " (it is not created automatically; use the office's file)"); return null; }
            if (p.Group.Length == 0) OpContext.Error(issues, ctx.FieldPath(args, "group"), RevitIssueCodes.Invalid, "group is empty");

            var pg = GroupFor(args.GetString("propertyGroup"), out p.PropertyGroupLabel, out string gerr);
            if (pg is null) OpContext.Error(issues, ctx.FieldPath(args, "propertyGroup"), RevitIssueCodes.NotFound, gerr);
            else p.PropertyGroup = pg;

            var seenCat = new HashSet<long>();
            var cats = args.GetStringList("categories");
            for (int i = 0; i < cats.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, "categories"), i);
                var c = Lookup.Category(doc, cats[i], out string? cerr);
                if (c is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, cerr!); continue; }
                if (!c.AllowsBoundParameters) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "category '" + c.Name + "' cannot have project or shared parameters bound to it"); continue; }
                if (seenCat.Add(ElementIds.ToLong(c.Id))) p.Categories.Add(c);
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in args.GetObjectList("parameters"))
            {
                var it = new Item { Name = spec.GetString("name").Trim(), Path = spec.Path, Spec = Spec(spec.GetString("dataType")), Description = spec.GetStringOrNull("description") ?? "" };
                if (it.Name.Length == 0) { OpContext.Error(issues, spec.Path + ".name", RevitIssueCodes.Invalid, "parameter name is empty"); continue; }
                if (Kit.BadTypeName(it.Name) is string bad && bad.Contains("does not allow")) OpContext.Error(issues, spec.Path + ".name", RevitIssueCodes.Invalid, bad);
                if (!names.Add(it.Name)) OpContext.Error(issues, spec.Path + ".name", RevitIssueCodes.Invalid, "parameter '" + it.Name + "' is listed twice");
                if (spec.GetStringOrNull("guid") is string gs)
                {
                    if (Guid.TryParse(gs, out var g)) it.Guid = g;
                    else OpContext.Error(issues, spec.Path + ".guid", RevitIssueCodes.Invalid, "'" + gs + "' is not a GUID");
                }
                p.Items.Add(it);
            }
            if (Kit.HasErrors(issues)) return null;

            // The file: existing definitions by name in any group; requested GUIDs must not belong to another name.
            try
            {
                WithFile(app, ctx.ActualPath(p.File), df =>
                {
                    var byName = new Dictionary<string, KeyValuePair<string, ExternalDefinition>>(StringComparer.Ordinal);
                    var byGuid = new Dictionary<Guid, string>();
                    foreach (DefinitionGroup g in df.Groups)
                        foreach (Definition d in g.Definitions)
                            if (d is ExternalDefinition ed)
                            {
                                if (!byName.ContainsKey(ed.Name)) byName[ed.Name] = new KeyValuePair<string, ExternalDefinition>(g.Name, ed);
                                byGuid[ed.GUID] = ed.Name;
                            }
                    foreach (var it in p.Items)
                    {
                        if (byName.TryGetValue(it.Name, out var hit))
                        {
                            it.FileGroup = hit.Key;
                            it.FileGuid = hit.Value.GUID;
                            if (it.Guid.HasValue && it.Guid.Value != hit.Value.GUID)
                                OpContext.Error(issues, it.Path + ".guid", RevitIssueCodes.Mismatch, "the file already has '" + it.Name + "' with GUID " + hit.Value.GUID + "; a second definition with another GUID would split schedules and tags");
                            if (!ParameterApi.SameSpec(hit.Value.GetDataType(), it.Spec))
                                OpContext.Error(issues, it.Path + ".dataType", RevitIssueCodes.Mismatch, "'" + it.Name + "' in the file is " + ParameterApi.SpecLabel(hit.Value.GetDataType()) + ", requested " + ParameterApi.SpecLabel(it.Spec));
                            if (!string.Equals(hit.Key, p.Group, StringComparison.Ordinal) && !afterCommit)
                                OpContext.Warning(issues, it.Path + ".name", RevitIssueCodes.Mismatch, "'" + it.Name + "' exists in group '" + hit.Key + "' of the file, not '" + p.Group + "'; that definition is used");
                        }
                        else if (it.Guid.HasValue && byGuid.TryGetValue(it.Guid.Value, out var owner))
                            OpContext.Error(issues, it.Path + ".guid", RevitIssueCodes.Exists, "GUID " + it.Guid.Value + " already belongs to '" + owner + "' in the file");
                    }
                    return 0;
                });
            }
            catch (OpFailedException e) { OpContext.Error(issues, fileAt, RevitIssueCodes.Invalid, e.Message); return null; }
            catch (Exception e) { OpContext.Error(issues, fileAt, RevitIssueCodes.Invalid, "the shared parameter file could not be read: " + e.Message); return null; }

            // The project: parameters with the same name.
            var it2 = doc.ParameterBindings.ForwardIterator();
            while (it2.MoveNext())
            {
                if (!(it2.Key is InternalDefinition def)) continue;
                var item = p.Items.FirstOrDefault(x => string.Equals(x.Name, def.Name, StringComparison.Ordinal));
                if (item is null) continue;
                var spe = doc.GetElement(def.Id) as SharedParameterElement;
                Guid? want = item.Guid ?? item.FileGuid;
                if (spe is null)
                {
                    OpContext.Error(issues, item.Path + ".name", RevitIssueCodes.Exists, "the project has a project (non-shared) parameter named '" + item.Name + "'; a shared one with the same name would duplicate it");
                    continue;
                }
                if (want.HasValue && spe.GuidValue != want.Value)
                {
                    OpContext.Error(issues, item.Path + ".name", RevitIssueCodes.Mismatch, "the project's shared parameter '" + item.Name + "' has GUID " + spe.GuidValue + ", not " + want.Value);
                    continue;
                }
                item.InProject = spe;
                item.Bound = true;
                if (it2.Current is ElementBinding eb)
                {
                    bool inst = eb is InstanceBinding;
                    if (inst != p.Instance) item.Differences.Add("bound as " + (inst ? "instance" : "type") + ", requested " + (p.Instance ? "instance" : "type"));
                    var have = new HashSet<long>();
                    foreach (Category c in eb.Categories) have.Add(ElementIds.ToLong(c.Id));
                    var missing = p.Categories.Where(c => !have.Contains(ElementIds.ToLong(c.Id))).Select(c => c.Name).ToList();
                    if (missing.Count > 0) item.Differences.Add("not bound to " + string.Join(", ", missing));
                    if (!ParameterApi.SameSpec(ParameterApi.Group(def), p.PropertyGroup) && !(string.IsNullOrEmpty(ParameterApi.Group(def).TypeId) && string.IsNullOrEmpty(p.PropertyGroup.TypeId)))
                        item.Differences.Add("in properties group '" + ParameterApi.GroupLabel(def) + "', requested '" + p.PropertyGroupLabel + "'");
                }
                if (!ParameterApi.SameSpec(ParameterApi.Spec(def), item.Spec))
                    OpContext.Error(issues, item.Path + ".dataType", RevitIssueCodes.Mismatch, "the project's '" + item.Name + "' is " + ParameterApi.SpecLabel(ParameterApi.Spec(def)) + ", requested " + ParameterApi.SpecLabel(item.Spec));
            }
            // Shared parameters present but not bound (loaded with a family), by GUID.
            foreach (var item in p.Items.Where(x => x.InProject is null))
            {
                Guid? want = item.Guid ?? item.FileGuid;
                if (want.HasValue) item.InProject = SharedParameterElement.Lookup(doc, want.Value);
            }
            if (!afterCommit)
                foreach (var item in p.Items.Where(x => x.Differences.Count > 0))
                {
                    item.BindingDiffers = true;
                    if (!p.Update)
                        OpContext.Error(issues, item.Path + ".name", RevitIssueCodes.Mismatch, "'" + item.Name + "' is bound differently: " + string.Join("; ", item.Differences) + "; set update:true to change the binding (categories are only added)");
                    else
                    {
                        if (item.InProject != null) ctx.DeclareModifies(item.InProject.Id);
                        if (item.Differences.Any(d => d.StartsWith("bound as", StringComparison.Ordinal)))
                            OpContext.Warning(issues, item.Path + ".name", RevitIssueCodes.Mismatch, "changing '" + item.Name + "' between instance and type binding discards its current values");
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
            var app = doc.Application;
            string source = ctx.ActualPath(p!.File);
            var real = new FileInfo(p.File);
            DateTime realWrite = real.LastWriteTimeUtc;
            long realLength = real.Length;
            string path = Path.Combine(ctx.StageFolder(Path.GetDirectoryName(p.File)!, inTemp: true), Path.GetFileName(p.File));
            File.Copy(source, path);
            if (ctx.Mode == RunMode.Rehearse)
                ctx.Warn("rehearsal: shared parameter definitions were written to a temporary copy of '" + p.File + "', not to the file itself");
            var guids = new JsonObject();
            WithFile(app, path, df =>
            {
                foreach (var it in p.Items)
                {
                    ExternalDefinition? def = null;
                    foreach (DefinitionGroup g in df.Groups)
                    {
                        def = g.Definitions.get_Item(it.Name) as ExternalDefinition;
                        if (def != null) break;
                    }
                    if (def is null)
                    {
                        var group = df.Groups.get_Item(p.Group) ?? df.Groups.Create(p.Group);
                        var opts = new ExternalDefinitionCreationOptions(it.Name, it.Spec) { Description = it.Description };
                        if (it.Guid.HasValue) opts.GUID = it.Guid.Value;
                        def = group.Definitions.Create(opts) as ExternalDefinition ?? throw new OpFailedException("create_failed", "Revit did not create the definition '" + it.Name + "'");
                    }
                    guids.Add(it.Name, def.GUID.ToString("D"));
                    bool existedInProject = SharedParameterElement.Lookup(doc, def.GUID) != null;
                    if (it.Bound && !it.BindingDiffers)
                    {
                        result.Unchanged.Add(Kit.Id(it.InProject!.Id));
                        continue;
                    }
                    var set = app.Create.NewCategorySet();
                    foreach (var c in p.Categories) set.Insert(c);
                    if (it.Bound)
                    {
                        // Keep every category already bound; only add.
                        var existing = doc.ParameterBindings.get_Item(it.InProject!.GetDefinition()) as ElementBinding;
                        if (existing != null) foreach (Category c in existing.Categories) set.Insert(c);
                    }
                    Binding binding = p.Instance ? (Binding)app.Create.NewInstanceBinding(set) : app.Create.NewTypeBinding(set);
                    bool ok = it.Bound ? doc.ParameterBindings.ReInsert(def, binding, p.PropertyGroup) : doc.ParameterBindings.Insert(def, binding, p.PropertyGroup);
                    if (!ok) throw new OpFailedException("binding_failed", "Revit refused to bind '" + it.Name + "' to " + string.Join(", ", p.Categories.Select(c => c.Name)));
                    var spe = SharedParameterElement.Lookup(doc, def.GUID) ?? throw new OpFailedException("binding_failed", "'" + it.Name + "' is not in the project after binding");
                    if (existedInProject) result.Modified.Add(Kit.Id(spe.Id));
                    else result.Created.Add(Kit.Id(spe.Id));
                }
                return 0;
            }, ctx, p.File);
            if (ctx.Mode == RunMode.Commit) ctx.StageFile(path, p.File, true, realWrite, realLength, copyOver: true);
            result.Outputs.Add("guids", guids);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            var guids = result.Outputs["guids"] as JsonObject ?? new JsonObject();
            bool instance = args.GetString("binding") == "instance";
            var cats = args.GetStringList("categories").Select(c => Lookup.Category(doc, c, out _)).Where(c => c != null).Select(c => c!).ToList();
            var group = GroupFor(args.GetString("propertyGroup"), out string groupLabel, out _);
            var arr = new JsonArray();
            foreach (var spec in args.GetObjectList("parameters"))
            {
                string name = spec.GetString("name").Trim();
                var rb = new JsonObject().Add("name", name);
                if (!(guids.GetStringOrNull(name) is string gs) || !Guid.TryParse(gs, out var guid)) { check.Fail("no GUID recorded for '" + name + "'"); continue; }
                rb.Add("guid", gs);
                var spe = SharedParameterElement.Lookup(doc, guid);
                if (spe is null) { check.Fail("shared parameter '" + name + "' (" + gs + ") is not in the project"); arr.Add(rb); continue; }
                rb.Add("id", Kit.Id(spe.Id));
                var def = spe.GetDefinition();
                check.Text(rb, "projectName", def.Name, name, "'" + name + "' name in the project");
                check.Text(rb, "dataType", ParameterApi.SpecLabel(ParameterApi.Spec(def)), ParameterApi.SpecLabel(Spec(spec.GetString("dataType"))), "'" + name + "' data type");
                if (spec.GetStringOrNull("guid") is string wantGuid && Guid.TryParse(wantGuid, out var wg) && wg != guid) check.Fail("'" + name + "' has GUID " + guid + ", requested " + wg);
                var b = doc.ParameterBindings.get_Item(def) as ElementBinding;
                if (b is null) { check.Fail("'" + name + "' is not bound"); arr.Add(rb); continue; }
                check.Text(rb, "binding", b is InstanceBinding ? "instance" : "type", instance ? "instance" : "type", "'" + name + "' binding");
                var bound = new List<string>();
                var have = new HashSet<long>();
                foreach (Category c in b.Categories) { bound.Add(c.Name); have.Add(ElementIds.ToLong(c.Id)); }
                rb.Add("categories", JsonArray.FromStrings(bound.OrderBy(x => x, StringComparer.Ordinal)));
                foreach (var c in cats) if (!have.Contains(ElementIds.ToLong(c.Id))) check.Fail("'" + name + "' is not bound to " + c.Name);
                if (group != null)
                {
                    bool same = ParameterApi.SameSpec(ParameterApi.Group(def), group) || (string.IsNullOrEmpty(ParameterApi.Group(def).TypeId) && string.IsNullOrEmpty(group.TypeId));
                    rb.Add("propertyGroup", ParameterApi.GroupLabel(def));
                    if (!same) check.Fail("'" + name + "' is in group '" + ParameterApi.GroupLabel(def) + "', requested '" + groupLabel + "'");
                }
                string sid = Kit.Id(spe.Id);
                rb.Add("state", result.Created.Contains(sid) ? "created" : result.Modified.Contains(sid) ? "updated" : "unchanged");
                arr.Add(rb);
            }
            result.Readback.Add("file", ctx.Mode == RunMode.Rehearse ? "(temporary copy)" : (args.GetStringOrNull("file") ?? doc.Application.SharedParametersFilename ?? ""));
            result.Readback.Add("parameters", arr);
            check.Apply(result);
        }
    }
}
