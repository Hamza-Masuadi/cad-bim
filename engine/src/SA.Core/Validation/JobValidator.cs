using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.Core.Validation
{
    public sealed class JobValidationOptions
    {
        /// <summary>When set, verify.checks ids not in this set are errors.</summary>
        public ISet<string>? KnownCheckIds { get; set; }

        /// <summary>When true a job without document identity is an error instead of a warning.</summary>
        public bool RequireDocument { get; set; }
    }

    public sealed class JobValidation
    {
        internal JobValidation(Job? job, List<Issue> issues)
        {
            Job = job;
            Issues = issues;
        }

        /// <summary>The parsed job, or null when there are errors.</summary>
        public Job? Job { get; }
        public IReadOnlyList<Issue> Issues { get; }
        public bool Ok => Issues.All(i => !i.IsError);
        public IEnumerable<Issue> Errors => Issues.Where(i => i.IsError);

        public JsonObject ToJson() => new JsonObject().Add("ok", Ok).Add("issues", Issue.ToJson(Issues));
    }

    /// <summary>
    /// Static validation of a job against op schemas: structure, types, required fields, enums, ranges, units, references.
    /// Runs without the app. Document-level validation (does the layer exist?) is the engines' job.
    /// </summary>
    public static class JobValidator
    {
        public const string JobIdPattern = "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$";
        private static readonly Regex JobIdRegex = new Regex(JobIdPattern, RegexOptions.CultureInvariant);

        private static readonly string[] VerifyMembers = { "checks", "capture", "checksFile", "checksMode", "visual" };
        private static readonly string[] TopLevel = { "$schema", "jobId", "title", "description", "app", "document", "units", "standard", "outputDir", "ops", "verify", "ownership" };

        /// <summary>An op that must be the only op of its job (verify checks and captures are still allowed).</summary>
        public const string OpNotAlone = "op_not_alone";

        /// <summary>Ops that must be alone in their job, by app and name, with the reason given to the user.</summary>
        private static readonly Dictionary<string, string> AloneOps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["revit:family.edit"] = "it opens the family outside the job's transactions and loads it back with one Revit call, which cannot share the job with other changes",
            ["autocad:block.export"] = "it writes files that are put in place only after the job commits, from a drawing the job must not change",
            ["revit:family.fromcad"] = "it builds each family in a document of its own outside the job's transactions and loads it with one Revit call, which cannot share the job with other changes",
            ["revit:family.create"] = "it builds the family in a document of its own outside the job's transactions and loads it with one Revit call, which cannot share the job with other changes",
        };

        /// <summary>Cross-field rules an op's schema cannot express, by app and name; run after its args pass the schema.</summary>
        private static readonly Dictionary<string, Func<JsonObject, LengthUnit, string, List<Issue>>> ArgRules =
            new Dictionary<string, Func<JsonObject, LengthUnit, string, List<Issue>>>(StringComparer.Ordinal)
            {
                ["revit:family.create"] = SA.Core.Families.FamilyRecipe.Check,
            };

        private static void RunArgRules(OpSchema schema, JsonObject args, string path, Ctx ctx)
        {
            if (!ArgRules.TryGetValue(AppKinds.ToWire(schema.App) + ":" + schema.Name, out var rules)) return;
            if (ctx.Issues.Any(i => i.IsError && i.Path.StartsWith(path, StringComparison.Ordinal))) return;
            ctx.Issues.AddRange(rules(args, ctx.Unit, path));
        }

        private sealed class Ctx
        {
            public LengthUnit Unit = LengthUnit.Millimetre;
            public HashSet<string> Earlier = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> AllIds = new HashSet<string>(StringComparer.Ordinal);
            public List<Issue> Issues = new List<Issue>();
            public void Error(string path, string code, string msg) => Issues.Add(new Issue(path, code, msg));
            public void Warn(string path, string code, string msg) => Issues.Add(new Issue(path, code, msg, IssueSeverity.Warning));
        }

        public static JobValidation Validate(string text, OpRegistry registry, JobValidationOptions? options = null)
        {
            JsonValue json;
            try { json = JsonValue.Parse(text); }
            catch (JsonParseException e)
            {
                return new JobValidation(null, new List<Issue> { new Issue(JsonPath.Root, IssueCodes.Json, e.Message) });
            }
            return Validate(json, registry, options);
        }

        public static JobValidation Validate(JsonValue json, OpRegistry registry, JobValidationOptions? options = null)
        {
            var opt = options ?? new JobValidationOptions();
            var ctx = new Ctx();
            if (!(json is JsonObject root))
            {
                ctx.Error(JsonPath.Root, IssueCodes.Type, "a job must be a JSON object, found " + JsonValue.KindName(json.Kind));
                return new JobValidation(null, ctx.Issues);
            }

            foreach (var k in root.Keys)
                if (Array.IndexOf(TopLevel, k) < 0)
                    ctx.Error(JsonPath.Key("$", k), IssueCodes.UnknownField, "unknown job member '" + k + "'" + Suggest(k, TopLevel));

            string? jobId = RequiredString(root, "jobId", "$", ctx);
            if (jobId != null && !JobIdRegex.IsMatch(jobId))
                ctx.Error("$.jobId", IssueCodes.BadId, "jobId '" + jobId + "' must be 1-128 characters of letters, digits, '.', '_' or '-', starting with a letter or digit");
            string? title = OptionalString(root, "title", "$", ctx);
            string? description = OptionalString(root, "description", "$", ctx);

            AppKind app = AppKind.AutoCad;
            bool appOk = false;
            string? appText = RequiredString(root, "app", "$", ctx);
            if (appText != null)
            {
                appOk = AppKinds.TryParse(appText, out app);
                if (!appOk) ctx.Error("$.app", IssueCodes.Enum, "app must be 'autocad' or 'revit', found '" + appText + "'");
            }

            JobDocument? document = null;
            var doc = Present(root, "document");
            if (doc is null)
            {
                string msg = "no document identity; the engine cannot confirm it is changing the right file (copy document.path and document.fingerprint from the snapshot)";
                if (opt.RequireDocument) ctx.Error("$.document", IssueCodes.DocumentMissing, msg);
                else ctx.Warn("$.document", IssueCodes.DocumentMissing, msg);
            }
            else if (doc is JsonObject d)
            {
                foreach (var k in d.Keys)
                    if (k != "path" && k != "fingerprint") ctx.Error(JsonPath.Key("$.document", k), IssueCodes.UnknownField, "unknown document member '" + k + "'");
                document = new JobDocument(OptionalString(d, "path", "$.document", ctx), OptionalString(d, "fingerprint", "$.document", ctx));
                if (document.Path is null && document.Fingerprint is null)
                    ctx.Warn("$.document", IssueCodes.DocumentMissing, "document has neither path nor fingerprint");
            }
            else ctx.Error("$.document", IssueCodes.Type, "document must be an object");

            var units = Present(root, "units");
            if (units is null) ctx.Error("$.units", IssueCodes.Required, "units is required, e.g. { \"length\": \"mm\" }");
            else if (units is JsonObject u)
            {
                foreach (var k in u.Keys)
                    if (k != "length") ctx.Error(JsonPath.Key("$.units", k), IssueCodes.UnknownField, "unknown units member '" + k + "' (angles are always degrees)");
                string? lu = RequiredString(u, "length", "$.units", ctx);
                if (lu != null && !Length.TryParseUnit(lu, out ctx.Unit))
                    ctx.Error("$.units.length", IssueCodes.Enum, "units.length must be one of mm, cm, m, in, ft; found '" + lu + "'");
            }
            else ctx.Error("$.units", IssueCodes.Type, "units must be an object");

            string? standard = OptionalString(root, "standard", "$", ctx);
            string? outputDir = OptionalString(root, "outputDir", "$", ctx);
            var ownership = OwnershipMode.Refuse;
            string? ownershipText = OptionalString(root, "ownership", "$", ctx);
            if (ownershipText != null)
            {
                if (!OwnershipModes.TryParse(ownershipText, out ownership))
                    ctx.Error("$.ownership", IssueCodes.Enum, "ownership must be 'refuse' (default: refuse the job if any element it changes is not editable) or 'skip' (leave those elements out and report them), found '" + ownershipText + "'");
                else if (appOk && app != AppKind.Revit && ownership == OwnershipMode.Skip)
                    ctx.Warn("$.ownership", IssueCodes.Constraint, "ownership applies to Revit worksharing only; it is ignored for " + AppKinds.ToWire(app));
            }

            // Ops: collect ids first so a reference to a later op is reported as a forward reference.
            var opsValue = Present(root, "ops");
            var opsArray = opsValue as JsonArray;
            if (opsValue is null) ctx.Error("$.ops", IssueCodes.Required, "ops is required");
            else if (opsArray is null) ctx.Error("$.ops", IssueCodes.Type, "ops must be an array");
            else if (opsArray.Count == 0) ctx.Error("$.ops", IssueCodes.TooFew, "ops must contain at least one op");

            var ops = new List<JobOp>();
            if (opsArray != null)
            {
                foreach (var item in opsArray)
                    if (item is JsonObject o && o["id"] is JsonString ids) ctx.AllIds.Add(ids.Value);

                for (int i = 0; i < opsArray.Count; i++)
                {
                    string path = JsonPath.Index("$.ops", i);
                    if (!(opsArray[i] is JsonObject op))
                    {
                        ctx.Error(path, IssueCodes.Type, "each op must be an object");
                        continue;
                    }
                    foreach (var k in op.Keys)
                        if (k != "id" && k != "op" && k != "args" && k != "note")
                            ctx.Error(JsonPath.Key(path, k), IssueCodes.UnknownField, "unknown op member '" + k + "' (allowed: id, op, args, note)" +
                                (k != "id" && k != "op" ? "; op fields go inside 'args'" : ""));

                    string? id = RequiredString(op, "id", path, ctx);
                    if (id != null)
                    {
                        if (!Refs.IsValidOpId(id))
                            ctx.Error(path + ".id", IssueCodes.BadId, "op id '" + id + "' must start with a letter or '_' and contain only letters, digits, '_' or '-' (max 64)");
                        else if (ctx.Earlier.Contains(id))
                            ctx.Error(path + ".id", IssueCodes.DuplicateId, "op id '" + id + "' is used more than once");
                    }
                    string? note = OptionalString(op, "note", path, ctx);
                    string? name = RequiredString(op, "op", path, ctx);
                    OpSchema? schema = null;
                    if (name != null)
                    {
                        schema = appOk ? registry.Find(name, app) : registry.Find(name);
                        var anyApp = registry.Find(name);
                        if (anyApp is null)
                            ctx.Error(path + ".op", IssueCodes.UnknownOp, "unknown op '" + name + "'" +
                                Suggest(name, registry.All.Where(s => !appOk || s.App == app).Select(s => s.Name).Distinct()));
                        else if (schema is null)
                            ctx.Error(path + ".op", IssueCodes.WrongApp, "op '" + name + "' is a " + AppKinds.ToWire(anyApp.App) + " op but the job is for " + AppKinds.ToWire(app));
                    }

                    var argsValue = Present(op, "args");
                    JsonObject args;
                    if (argsValue is null) args = new JsonObject();
                    else if (argsValue is JsonObject ao) args = ao;
                    else
                    {
                        ctx.Error(path + ".args", IssueCodes.Type, "args must be an object");
                        args = new JsonObject();
                    }

                    if (schema != null)
                    {
                        ValidateObject(schema.Args, args, path + ".args", ctx);
                        RunArgRules(schema, args, path + ".args", ctx);
                        if (schema.RequiresAllowScript)
                            ctx.Warn(path + ".op", IssueCodes.Script, "'" + schema.Name + "' runs only when the run request sets allowScript, and is always rehearsed first");
                    }

                    if (id != null && Refs.IsValidOpId(id))
                    {
                        if (schema != null && !ctx.Earlier.Contains(id)) ops.Add(new JobOp(i, id, schema.Name, args, schema, note));
                        ctx.Earlier.Add(id);
                    }
                    if (schema != null && opsArray.Count > 1 && AloneOps.TryGetValue(AppKinds.ToWire(schema.App) + ":" + schema.Name, out string? why))
                        ctx.Error(path + ".op", OpNotAlone, "'" + schema.Name + "' must be the only op in its job (" + opsArray.Count + " ops found): " + why + ". Put the other ops in a job of their own");
                }
            }

            var checks = new List<string>();
            var capture = new List<JsonObject>();
            var checksFiles = new List<string>();
            bool addToBundled = false;
            SA.Core.Imaging.VisualSpec? visual = null;
            var verify = Present(root, "verify");
            if (verify != null)
            {
                if (verify is JsonObject v)
                {
                    foreach (var k in v.Keys)
                        if (Array.IndexOf(VerifyMembers, k) < 0) ctx.Error(JsonPath.Key("$.verify", k), IssueCodes.UnknownField, "unknown verify member '" + k + "' (allowed: " + string.Join(", ", VerifyMembers) + ")");
                    var cf = Present(v, "checksFile");
                    if (cf is JsonString cfs && cfs.Value.Trim().Length > 0) checksFiles.Add(cfs.Value);
                    else if (cf is JsonArray cfa && cfa.Count > 0)
                    {
                        for (int i = 0; i < cfa.Count; i++)
                        {
                            if (cfa[i] is JsonString fs && fs.Value.Trim().Length > 0) checksFiles.Add(fs.Value);
                            else ctx.Error(JsonPath.Index("$.verify.checksFile", i), IssueCodes.Type, "checks file paths are non-empty strings");
                        }
                    }
                    else if (cf != null) ctx.Error("$.verify.checksFile", IssueCodes.Type, "verify.checksFile must be a path or a non-empty list of paths (checks files or folders)");
                    var cm = Present(v, "checksMode");
                    if (cm is JsonString cms && (cms.Value == "add" || cms.Value == "replace"))
                    {
                        addToBundled = cms.Value == "add";
                        if (checksFiles.Count == 0 && cf is null) ctx.Warn("$.verify.checksMode", IssueCodes.Constraint, "verify.checksMode has no effect without verify.checksFile");
                    }
                    else if (cm != null) ctx.Error("$.verify.checksMode", IssueCodes.Type, "verify.checksMode must be 'replace' or 'add'");
                    var ch = Present(v, "checks");
                    if (ch is JsonArray ca)
                    {
                        for (int i = 0; i < ca.Count; i++)
                        {
                            string p = JsonPath.Index("$.verify.checks", i);
                            if (!(ca[i] is JsonString cs) || cs.Value.Length == 0) { ctx.Error(p, IssueCodes.Type, "check ids are non-empty strings"); continue; }
                            if (opt.KnownCheckIds != null && !opt.KnownCheckIds.Contains(cs.Value))
                            {
                                // The job's own checks files are not loaded here, so an id they define cannot be confirmed.
                                if (cf != null) ctx.Warn(p, IssueCodes.UnknownCheck, "check '" + cs.Value + "' is not a known check; it must be defined in verify.checksFile" + Suggest(cs.Value, opt.KnownCheckIds));
                                else ctx.Error(p, IssueCodes.UnknownCheck, "unknown check '" + cs.Value + "'" + Suggest(cs.Value, opt.KnownCheckIds));
                            }
                            checks.Add(cs.Value);
                        }
                    }
                    else if (ch != null) ctx.Error("$.verify.checks", IssueCodes.Type, "verify.checks must be an array of check ids");
                    if (cf != null && checks.Count == 0) ctx.Warn("$.verify.checksFile", IssueCodes.Constraint, "verify.checksFile has no effect without verify.checks");
                    var cp = Present(v, "capture");
                    if (cp is JsonArray cpa)
                    {
                        for (int i = 0; i < cpa.Count; i++)
                        {
                            string p = JsonPath.Index("$.verify.capture", i);
                            if (!(cpa[i] is JsonObject co)) { ctx.Error(p, IssueCodes.Type, "each capture is an object such as { \"target\": \"activeView\" }"); continue; }
                            if (!(co["target"] is JsonString t) || t.Value.Length == 0) ctx.Error(p + ".target", IssueCodes.Required, "capture target is required");
                            capture.Add(co);
                        }
                    }
                    else if (cp != null) ctx.Error("$.verify.capture", IssueCodes.Type, "verify.capture must be an array");
                    visual = SA.Core.Imaging.VisualSpec.Validate(Present(v, "visual"), "$.verify.visual", ctx.Issues);
                }
                else ctx.Error("$.verify", IssueCodes.Type, "verify must be an object");
            }

            bool ok = ctx.Issues.All(x => !x.IsError);
            Job? job = ok
                ? new Job(root, jobId!, title, app, document, ctx.Unit, standard, outputDir, description, ops, new JobVerify(checks, capture, checksFiles, addToBundled))
                : null;
            if (job != null) job.Ownership = ownership;
            if (job != null && visual != null) job.Verify.Visual = visual;
            return new JobValidation(job, ctx.Issues);
        }

        /// <summary>
        /// Validates one op's args against its schema, as <see cref="Validate(JsonValue, OpRegistry, JobValidationOptions)"/> does,
        /// but with references resolved already (engines call this after <see cref="Refs.Resolve"/>, where no references remain).
        /// </summary>
        public static List<Issue> ValidateArgs(OpSchema schema, JsonObject args, LengthUnit unit, string path = "$.args")
        {
            var ctx = new Ctx { Unit = unit };
            ValidateObject(schema.Args, args, path, ctx);
            RunArgRules(schema, args, path, ctx);
            return ctx.Issues;
        }

        private static JsonValue? Present(JsonObject o, string key)
        {
            var v = o[key];
            return v is null || v.IsNull ? null : v;
        }

        private static string? RequiredString(JsonObject o, string key, string parent, Ctx ctx)
        {
            var v = Present(o, key);
            string path = JsonPath.Key(parent, key);
            if (v is null) { ctx.Error(path, IssueCodes.Required, key + " is required"); return null; }
            if (!(v is JsonString s)) { ctx.Error(path, IssueCodes.Type, key + " must be a string, found " + JsonValue.KindName(v.Kind)); return null; }
            if (s.Value.Trim().Length == 0) { ctx.Error(path, IssueCodes.Required, key + " must not be empty"); return null; }
            return s.Value;
        }

        private static string? OptionalString(JsonObject o, string key, string parent, Ctx ctx)
        {
            var v = Present(o, key);
            if (v is null) return null;
            if (v is JsonString s) return s.Value;
            ctx.Error(JsonPath.Key(parent, key), IssueCodes.Type, key + " must be a string, found " + JsonValue.KindName(v.Kind));
            return null;
        }

        private static void ValidateObject(FieldSpec spec, JsonObject obj, string path, Ctx ctx)
        {
            foreach (var k in obj.Keys)
                if (spec.FindField(k) is null)
                    ctx.Error(JsonPath.Key(path, k), IssueCodes.UnknownField, "unknown field '" + k + "'" + Suggest(k, spec.Fields.Select(f => f.Name)));

            foreach (var f in spec.Fields)
            {
                var v = Present(obj, f.Name);
                string fp = JsonPath.Key(path, f.Name);
                if (v is null)
                {
                    if (f.Required) ctx.Error(fp, IssueCodes.Required, "'" + f.Name + "' is required (" + f.TypeLabel() + (f.Description.Length > 0 ? ": " + f.Description : "") + ")");
                    continue;
                }
                ValidateValue(f, v, fp, ctx);
            }

            foreach (var c in spec.Constraints)
            {
                var present = c.Fields.Where(n => Present(obj, n) != null).ToList();
                bool bad;
                switch (c.Kind)
                {
                    case ConstraintKind.ExactlyOne: bad = present.Count != 1; break;
                    case ConstraintKind.AtLeastOne: bad = present.Count == 0; break;
                    case ConstraintKind.AtMostOne: bad = present.Count > 1; break;
                    default: bad = present.Contains(c.Fields[0]) && present.Count != c.Fields.Count; break;
                }
                if (bad)
                    ctx.Error(path, IssueCodes.Constraint, "give " + c.Describe() + (present.Count > 0 ? " (found: " + string.Join(", ", present) + ")" : ""));
            }
        }

        private static void ValidateValue(FieldSpec f, JsonValue v, string path, Ctx ctx)
        {
            if (v is JsonString rs && Refs.IsRefText(rs.Value))
            {
                CheckRef(rs.Value, path, ctx);
                return;
            }

            switch (f.Type)
            {
                case FieldType.Any:
                    return;
                case FieldType.String:
                {
                    if (!(v is JsonString s)) { TypeError(f, v, path, ctx); return; }
                    string text = s.Value.StartsWith("$$", StringComparison.Ordinal) ? s.Value.Substring(1) : s.Value;
                    if (f.Required && text.Trim().Length == 0) ctx.Error(path, IssueCodes.Required, "'" + f.Name + "' must not be empty");
                    if (f.Pattern != null && !Regex.IsMatch(text, "^(?:" + f.Pattern + ")$", RegexOptions.CultureInvariant))
                        ctx.Error(path, IssueCodes.Pattern, "'" + text + "' does not match the expected form " + f.Pattern + (f.Description.Length > 0 ? " (" + f.Description + ")" : ""));
                    return;
                }
                case FieldType.Enum:
                {
                    if (!(v is JsonString s)) { TypeError(f, v, path, ctx); return; }
                    if (!f.EnumValues.Contains(s.Value))
                        ctx.Error(path, IssueCodes.Enum, "'" + s.Value + "' is not one of " + string.Join(", ", f.EnumValues) + Suggest(s.Value, f.EnumValues));
                    return;
                }
                case FieldType.Bool:
                    if (!(v is JsonBool)) TypeError(f, v, path, ctx);
                    return;
                case FieldType.Number:
                case FieldType.Integer:
                {
                    if (!(v is JsonNumber n)) { TypeError(f, v, path, ctx); return; }
                    if (f.Type == FieldType.Integer && !n.TryGetInt64(out _)) { ctx.Error(path, IssueCodes.Type, "expected an integer, found " + n.Raw); return; }
                    CheckRange(f, n.Value, n.Raw, path, ctx, 1.0);
                    return;
                }
                case FieldType.Length:
                {
                    if (!TryLength(v, ctx.Unit, out double len, out string err)) { ctx.Error(path, v is JsonString ? IssueCodes.Length : IssueCodes.Type, err); return; }
                    CheckRange(f, len, FormatNumber(len) + " " + Length.Symbol(ctx.Unit), path, ctx, Length.Convert(1, LengthUnit.Millimetre, ctx.Unit));
                    return;
                }
                case FieldType.Angle:
                {
                    double deg;
                    if (v is JsonNumber an) deg = an.Value;
                    else if (!(v is JsonString astr) || !Angle.TryParseDegrees(astr.Value, out deg))
                    {
                        ctx.Error(path, IssueCodes.Type, "expected an angle in degrees (number), found " + Describe(v));
                        return;
                    }
                    CheckRange(f, deg, FormatNumber(deg) + " degrees", path, ctx, 1.0);
                    return;
                }
                case FieldType.Point2:
                case FieldType.Point3:
                {
                    int dims = f.Type == FieldType.Point2 ? 2 : 3;
                    if (!(v is JsonArray a) || a.Count != dims)
                    {
                        ctx.Error(path, IssueCodes.Type, "expected a point [" + (dims == 2 ? "x, y" : "x, y, z") + "], found " + Describe(v));
                        return;
                    }
                    for (int i = 0; i < dims; i++)
                    {
                        string ip = JsonPath.Index(path, i);
                        if (a[i] is JsonString ps && Refs.IsRefText(ps.Value)) { CheckRef(ps.Value, ip, ctx); continue; }
                        if (!TryLength(a[i], ctx.Unit, out _, out string err)) ctx.Error(ip, IssueCodes.Type, err);
                    }
                    return;
                }
                case FieldType.List:
                {
                    if (!(v is JsonArray a)) { TypeError(f, v, path, ctx); return; }
                    if (f.MinItems.HasValue && a.Count < f.MinItems.Value) ctx.Error(path, IssueCodes.TooFew, "needs at least " + f.MinItems.Value + " item(s), found " + a.Count);
                    if (f.MaxItems.HasValue && a.Count > f.MaxItems.Value) ctx.Error(path, IssueCodes.TooMany, "allows at most " + f.MaxItems.Value + " item(s), found " + a.Count);
                    if (f.Item != null)
                        for (int i = 0; i < a.Count; i++)
                        {
                            if (a[i].IsNull) { ctx.Error(JsonPath.Index(path, i), IssueCodes.Type, "list items must not be null"); continue; }
                            ValidateValue(f.Item, a[i], JsonPath.Index(path, i), ctx);
                        }
                    return;
                }
                case FieldType.Ref:
                {
                    if (v is JsonString s && s.Value.Length > 0) return;
                    if (v is JsonNumber n && n.TryGetInt64(out _)) return;
                    if (v is JsonObject o && o["id"] != null) return; // a resolved reference
                    ctx.Error(path, IssueCodes.Type, "expected a reference \"$opId\" or an element id, found " + Describe(v));
                    return;
                }
                case FieldType.Object:
                {
                    if (!(v is JsonObject o)) { TypeError(f, v, path, ctx); return; }
                    ValidateObject(f, o, path, ctx);
                    return;
                }
                case FieldType.Map:
                {
                    if (!(v is JsonObject o)) { TypeError(f, v, path, ctx); return; }
                    if (f.Item != null)
                        foreach (var kv in o)
                            if (!kv.Value.IsNull) ValidateValue(f.Item, kv.Value, JsonPath.Key(path, kv.Key), ctx);
                    return;
                }
            }
        }

        private static void CheckRef(string text, string path, Ctx ctx)
        {
            if (!Refs.TryParse(text, out var expr, out string err)) { ctx.Error(path, IssueCodes.RefInvalid, err); return; }
            if (ctx.Earlier.Contains(expr!.OpId)) return;
            if (ctx.AllIds.Contains(expr.OpId))
                ctx.Error(path, IssueCodes.RefForward, "'" + text + "' refers to op '" + expr.OpId + "', which runs later or is this op; ops can only use results of earlier ops");
            else
                ctx.Error(path, IssueCodes.RefUnknown, "'" + text + "' refers to op '" + expr.OpId + "', which is not in this job" + Suggest(expr.OpId, ctx.Earlier));
        }

        private static bool TryLength(JsonValue v, LengthUnit unit, out double value, out string error)
        {
            value = 0;
            error = "";
            if (v is JsonNumber n) { value = n.Value; return true; }
            if (v is JsonString s)
            {
                if (Length.TryParse(s.Value, unit, out value, out string e)) return true;
                error = e;
                return false;
            }
            error = "expected a length (a number in job units, or a string such as \"3.2m\" or \"10'-6\\\"\"), found " + Describe(v);
            return false;
        }

        private static void CheckRange(FieldSpec f, double value, string shown, string path, Ctx ctx, double factor)
        {
            double? min = f.Min * factor, max = f.Max * factor;
            if (min.HasValue && (f.ExclusiveMin ? value <= min.Value : value < min.Value))
                ctx.Error(path, IssueCodes.Range, shown + " must be " + (f.ExclusiveMin ? "greater than " : "at least ") + FormatNumber(min.Value));
            if (max.HasValue && value > max.Value)
                ctx.Error(path, IssueCodes.Range, shown + " must be at most " + FormatNumber(max.Value));
        }

        private static void TypeError(FieldSpec f, JsonValue v, string path, Ctx ctx) =>
            ctx.Error(path, IssueCodes.Type, "expected " + f.TypeLabel() + ", found " + Describe(v));

        private static string Describe(JsonValue v)
        {
            string s = v.ToJson();
            if (s.Length > 40) s = s.Substring(0, 37) + "...";
            return JsonValue.KindName(v.Kind) + " " + s;
        }

        private static string FormatNumber(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>" (did you mean 'x'?)" for the closest candidate within a small edit distance, else "".</summary>
        internal static string Suggest(string word, IEnumerable<string> candidates)
        {
            string? best = null;
            int bestD = int.MaxValue;
            foreach (var c in candidates)
            {
                int d = Distance(word.ToLowerInvariant(), c.ToLowerInvariant());
                if (d < bestD) { bestD = d; best = c; }
            }
            return best != null && bestD <= Math.Max(2, word.Length / 3) ? " (did you mean '" + best + "'?)" : "";
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var t = prev; prev = cur; cur = t;
            }
            return prev[b.Length];
        }
    }
}
