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
    /// family.load: load an .rfa (all types, or only the listed ones, which may come from its type catalog).
    ///
    /// The family takes the file's name. Validate checks the path (absolute, .rfa, exists) and reads
    /// BasicFileInfo.Extract: a file saved in a later Revit release is refused. It is an ensure op:
    /// <list type="bullet">
    /// <item>family not loaded: Document.LoadFamily / LoadFamilySymbol;</item>
    /// <item>family loaded, neither reload nor overwriteParameters: nothing is reloaded and the family is reported
    /// unchanged, except that listed types the project lacks are loaded. If the file's family differs from the loaded
    /// one, Revit would reload the definition to add them, so the load is cancelled (OnFamilyFound returns false,
    /// documented as "false to cancel") and the op fails with family_differs;</item>
    /// <item>family loaded, reload true: the definition is reloaded (geometry, nested families) and parameter values
    /// are kept;</item>
    /// <item>family loaded, overwriteParameters true: the family is reloaded and its types' parameter values are
    /// reset to the file's.</item>
    /// </list>
    /// With reload or overwriteParameters the family and its types are declared as modified for the ownership check.
    /// The load options never answer "overwrite parameter values" with true unless the job asked, and nested shared
    /// families keep the project's version (FamilySource.Project) unless the job asked to overwrite. Revit calls the
    /// options only when the family "is both loaded and changed" (IFamilyLoadOptions remarks), so an identical file
    /// loads nothing and LoadFamily returns false; that is reported as unchanged.
    ///
    /// Families in Generic Models are loaded with a warning (RV-002 / RV-039).
    /// </summary>
    public sealed class FamilyLoad : OpHandler
    {
        public override string Name => "family.load";

        /// <summary>Answers Revit's conflict questions without a dialog, overwriting values only when asked.</summary>
        private sealed class LoadOptions : IFamilyLoadOptions
        {
            private readonly bool _overwrite;
            private readonly bool _replace;
            public LoadOptions(bool overwrite, bool replace) { _overwrite = overwrite; _replace = replace || overwrite; }
            public bool FamilyFound { get; private set; }
            /// <summary>The family was loaded and differs from the file, and replacing it was not allowed.</summary>
            public bool Cancelled { get; private set; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                FamilyFound = true;
                overwriteParameterValues = _overwrite;
                if (!_replace) { Cancelled = true; return false; }
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = _overwrite ? FamilySource.Family : FamilySource.Project;
                overwriteParameterValues = _overwrite;
                return true;
            }
        }

        private sealed class Plan
        {
            public string Path = "";
            public string FamilyName = "";
            public Family? Existing;
            public List<string> Types = new List<string>();
            public List<string> MissingTypes = new List<string>();
            public bool Overwrite;
            public bool Reload;
            public string? Format;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan { Path = args.GetString("path").Trim(), Overwrite = args.GetBool("overwriteParameters"), Reload = args.GetBool("reload"), Types = args.GetStringList("types") };
            string at = ctx.FieldPath(args, "path");
            if (!System.IO.Path.IsPathRooted(p.Path)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "path must be absolute"); return null; }
            if (!string.Equals(System.IO.Path.GetExtension(p.Path), ".rfa", StringComparison.OrdinalIgnoreCase)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "the file must be a family (.rfa)"); return null; }
            if (!File.Exists(p.Path)) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "file not found: " + p.Path); return null; }
            try
            {
                var info = BasicFileInfo.Extract(p.Path);
                p.Format = info.Format;
                if (info.IsSavedInLaterVersion)
                    OpContext.Error(issues, at, RevitIssueCodes.Invalid, "the family was saved in a later Revit release (" + info.Format + ") than this one (" + ReleaseInfo.Year + ")");
            }
            catch (Exception e) { OpContext.Warning(issues, at, RevitIssueCodes.Unchecked, "the file's version could not be read: " + e.Message); }
            p.FamilyName = System.IO.Path.GetFileNameWithoutExtension(p.Path);
            var dupTypes = p.Types.GroupBy(t => t, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupTypes.Count > 0) OpContext.Error(issues, ctx.FieldPath(args, "types"), RevitIssueCodes.Invalid, "types listed twice: " + string.Join(", ", dupTypes));
            if (p.Types.Any(t => t.Trim().Length == 0)) OpContext.Error(issues, ctx.FieldPath(args, "types"), RevitIssueCodes.Invalid, "a type name is empty");

            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            p.Existing = families.FirstOrDefault(f => string.Equals(f.Name, p.FamilyName, StringComparison.Ordinal));
            if (p.Existing is null)
            {
                var other = families.FirstOrDefault(f => string.Equals(f.Name, p.FamilyName, StringComparison.OrdinalIgnoreCase));
                if (other != null) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "a family named '" + other.Name + "' is loaded (names differ only by case); rename the file or use that family");
            }
            else
            {
                if (p.Existing.IsInPlace) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "'" + p.FamilyName + "' is an in-place family in this project; a loadable family cannot replace it");
                var have = p.Existing.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name ?? "").ToHashSet(StringComparer.Ordinal);
                p.MissingTypes = p.Types.Where(t => !have.Contains(t)).ToList();
                if (p.Overwrite || p.Reload || p.MissingTypes.Count > 0)
                {
                    ctx.DeclareModifies(p.Existing.Id);
                    if (p.Overwrite || p.Reload) foreach (var id in p.Existing.GetFamilySymbolIds()) ctx.DeclareModifies(id);
                }
                if (p.Overwrite)
                    OpContext.Warning(issues, ctx.FieldPath(args, "overwriteParameters"), RevitIssueCodes.Mismatch, "'" + p.FamilyName + "' is loaded; reloading it resets its types' parameter values to the file's");
                else if (p.Reload)
                    OpContext.Warning(issues, ctx.FieldPath(args, "reload"), RevitIssueCodes.Mismatch, "'" + p.FamilyName + "' is loaded; if the file differs, its definition (geometry, nested families) is replaced in every placed instance");
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
            var before = p!.Existing != null ? new HashSet<long>(p.Existing.GetFamilySymbolIds().Select(ElementIds.ToLong)) : new HashSet<long>();
            var options = new LoadOptions(p.Overwrite, p.Reload);
            Family? family = p.Existing;
            bool changed = false;
            if (p.Existing != null && !p.Overwrite && !p.Reload && p.MissingTypes.Count == 0)
            {
                result.Unchanged.Add(Kit.Id(p.Existing.Id));
                result.Outputs.Add("family", Kit.Id(p.Existing.Id));
                return;
            }
            if (p.Types.Count == 0)
            {
                bool ok = doc.LoadFamily(p.Path, options, out Family loaded);
                if (ok && loaded != null) { family = loaded; changed = true; }
                else if (p.Existing is null) throw new OpFailedException("load_failed", "Revit did not load '" + p.Path + "'");
                if (options.Cancelled) throw Differs(p);
            }
            else
            {
                var load = p.Existing != null && !p.Overwrite && !p.Reload ? p.MissingTypes : p.Types;
                foreach (var t in load)
                {
                    bool ok = doc.LoadFamilySymbol(p.Path, t, options, out FamilySymbol symbol);
                    if (options.Cancelled) throw Differs(p);
                    if (ok && symbol != null) { family = symbol.Family; changed = true; continue; }
                    var found = family?.GetFamilySymbolIds().Select(id => doc.GetElement(id)).FirstOrDefault(e => e?.Name == t);
                    if (found is null) throw new OpFailedException("type_not_found", "type '" + t + "' is not in '" + p.Path + "' or its type catalog");
                }
            }
            if (family is null) throw new OpFailedException("load_failed", "the family was not found after loading");
            string fid = Kit.Id(family.Id);
            if (p.Existing is null) result.Created.Add(fid);
            else if (changed || options.FamilyFound) result.Modified.Add(fid);
            else result.Unchanged.Add(fid);
            foreach (var sid in family.GetFamilySymbolIds())
                if (!before.Contains(ElementIds.ToLong(sid))) result.Created.Add(Kit.Id(sid));
            if (family.FamilyCategory?.BuiltInCategory == BuiltInCategory.OST_GenericModel)
                ctx.Warn("family '" + family.Name + "' is in Generic Models; it will be missing from category schedules and tags and exports as a proxy (RV-002/RV-039)");
            result.Outputs.Add("family", fid);
        }

        private static OpFailedException Differs(Plan p) =>
            new OpFailedException("family_differs", "'" + p.FamilyName + "' is loaded and the file's version differs, so adding " +
                (p.MissingTypes.Count > 0 ? "type(s) " + string.Join(", ", p.MissingTypes) : "its types") +
                " would reload the family definition in every placed instance. Nothing was loaded; set reload:true to reload it keeping parameter values, or overwriteParameters:true to also reset them.");

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            string? idText = result.Outputs.GetStringOrNull("family");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Family family))
            {
                check.Fail("the family is not in the project after the op");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            check.Text(rb, "name", family.Name, System.IO.Path.GetFileNameWithoutExtension(args.GetString("path").Trim()), "family name");
            rb.Add("category", family.FamilyCategory?.Name ?? "");
            var names = family.GetFamilySymbolIds().Select(s => doc.GetElement(s)?.Name ?? "").Where(n => n.Length > 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            foreach (var t in args.GetStringList("types"))
                if (!names.Contains(t)) check.Fail("type '" + t + "' is not in family '" + family.Name + "' after the op");
            rb.Add("types", JsonArray.FromStrings(names));
            rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
            rb.Add("overwroteParameters", result.Modified.Contains(idText) && args.GetBool("overwriteParameters"));
            rb.Add("reloaded", result.Modified.Contains(idText) && (args.GetBool("reload") || args.GetBool("overwriteParameters")));
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            var listed = args.GetStringList("types");
            result.Outputs.Set("types", JsonArray.FromStrings(listed.Count > 0 ? listed : names));
            check.Apply(result);
        }
    }
}
