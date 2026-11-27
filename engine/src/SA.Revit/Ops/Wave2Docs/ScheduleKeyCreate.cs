using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// schedule.key.create (ensure): a key schedule (ViewSchedule.CreateKeySchedule) whose keys set several
    /// parameters of the category's elements at once.
    ///
    /// <para><b>Fields</b> are matched by name against ScheduleDefinition.GetSchedulableFields (exact, then ignoring
    /// case). The Key Name column Revit adds first is left in place. keyParameter sets
    /// ViewSchedule.KeyScheduleParameterName, the parameter the category's elements get.</para>
    ///
    /// <para><b>Keys</b> are rows: TableSectionData.InsertRow on the body section (documented as allowed in key
    /// schedules) at the first index CanInsertRow accepts from the end. The new key element is the one that appears
    /// in FilteredElementCollector(doc, schedule.Id) after the insert; its Key Name (REF_TABLE_ELEM_NAME) and values
    /// (ParamAccess, numbers in job units) are then set.</para>
    ///
    /// <para><b>Ensure.</b> A schedule of the name must be a key schedule of the same category. Missing fields and
    /// keys, a different key parameter name and different values are differences: refused unless update, in which
    /// case they are added or set. Nothing is ever removed, so extra fields and keys stay. The existing schedule and
    /// the keys whose values change are declared.</para>
    /// </summary>
    public sealed class ScheduleKeyCreate : OpHandler
    {
        public override string Name => "schedule.key.create";

        private sealed class KeyReq
        {
            public string Name = "";
            public string Path = "";
            public List<KeyValuePair<string, JsonValue>> Values = new List<KeyValuePair<string, JsonValue>>();
            public Element? Existing;
        }

        private sealed class Plan
        {
            public string ScheduleName = "";
            public Category Category = null!;
            public string? KeyParameter;
            public List<string> Fields = new List<string>();
            public List<KeyReq> Keys = new List<KeyReq>();
            public ViewSchedule? Existing;
            public List<string> Differences = new List<string>();
        }

        internal static string KeyName(Element e) => e.get_Parameter(BuiltInParameter.REF_TABLE_ELEM_NAME)?.AsString() ?? e.Name;

        internal static List<Element> KeyElements(Document doc, ViewSchedule s) =>
            new FilteredElementCollector(doc, s.Id).WhereElementIsNotElementType().ToElements().ToList();

        private static string FieldName(Document doc, SchedulableField f)
        {
            try { return f.GetName(doc); } catch (Autodesk.Revit.Exceptions.ApplicationException) { return f.ParameterId != null ? FilterValues.Label(doc, f.ParameterId) : f.FieldType.ToString(); }
        }

        internal static SchedulableField? MatchField(Document doc, ScheduleDefinition def, string name, out string? error)
        {
            error = null;
            var named = def.GetSchedulableFields().Select(f => new { f, n = FieldName(doc, f) }).ToList();
            var hit = named.Where(x => string.Equals(x.n, name, StringComparison.Ordinal)).ToList();
            if (hit.Count == 0) hit = named.Where(x => string.Equals(x.n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count >= 1) return hit[0].f;
            error = "'" + name + "' is not a schedulable field of a key schedule for this category" + Lookup.Suggest(name, named.Select(x => x.n).Distinct().ToList());
            return null;
        }

        private static List<string> FieldNames(ScheduleDefinition def) =>
            Enumerable.Range(0, def.GetFieldCount()).Select(i => def.GetField(i).GetName()).ToList();

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan { KeyParameter = args.GetStringOrNull("keyParameter")?.Trim() };
            string? name = DocsUtil.CheckName(args.GetString("name"), "schedule", ctx.FieldPath(args, "name"), issues);
            var cat = DocsUtil.Category(doc, args.GetString("category"), ctx.FieldPath(args, "category"), issues);
            if (name is null || cat is null) return null;
            p.ScheduleName = name;
            p.Category = cat;
            if (!ViewSchedule.IsValidCategoryForKeySchedule(cat.Id))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.Invalid, "category '" + cat.Name + "' cannot have a key schedule");
                return null;
            }
            p.Existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .FirstOrDefault(s => !s.IsTemplate && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            var other = DocsUtil.NameTaken(doc, name, ViewType.Schedule, p.Existing?.Id);
            if (other != null) { OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists, "a view named '" + other.Name + "' already exists (id " + ElementIds.ToWire(other.Id) + ")"); return null; }
            if (p.Existing != null && (!p.Existing.Definition.IsKeySchedule || p.Existing.Definition.CategoryId != cat.Id))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Mismatch, "schedule '" + p.Existing.Name + "' exists but is not a key schedule of " + cat.Name + "; choose another name");
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fl = args.GetStringList("fields");
            for (int i = 0; i < fl.Count; i++)
            {
                string f = fl[i].Trim();
                if (!seen.Add(f)) OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "fields"), i), RevitIssueCodes.Invalid, "field '" + f + "' is listed twice");
                else if (p.Existing != null && MatchField(doc, p.Existing.Definition, f, out string? err) is null)
                    OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "fields"), i), RevitIssueCodes.NotFound, err!);
                p.Fields.Add(f);
            }
            var keyNames = new HashSet<string>(StringComparer.Ordinal);
            var existingKeys = p.Existing != null ? KeyElements(doc, p.Existing) : new List<Element>();
            foreach (var k in args.GetObjectList("keys"))
            {
                var kr = new KeyReq { Name = k.GetString("name").Trim(), Path = k.Path, Values = k.GetMap("values") };
                if (!keyNames.Add(kr.Name)) OpContext.Error(issues, k.Path + ".name", RevitIssueCodes.Invalid, "key '" + kr.Name + "' is listed twice");
                foreach (var kv in kr.Values)
                    if (!seen.Contains(kv.Key)) OpContext.Error(issues, k.Path + ".values." + kv.Key, RevitIssueCodes.Invalid, "'" + kv.Key + "' is not one of fields");
                kr.Existing = existingKeys.FirstOrDefault(e => string.Equals(KeyName(e), kr.Name, StringComparison.Ordinal));
                p.Keys.Add(kr);
            }
            if (DocsUtil.HasErrors(issues)) return null;
            if (p.Existing is null || forReadback) return p;

            var def = p.Existing.Definition;
            var have = FieldNames(def);
            var changedKeys = new List<Element>();
            if (p.KeyParameter != null && !string.Equals(p.Existing.KeyScheduleParameterName, p.KeyParameter, StringComparison.Ordinal))
            {
                p.Differences.Add("key parameter is '" + p.Existing.KeyScheduleParameterName + "'");
                OpContext.Warning(issues, ctx.FieldPath(args, "keyParameter"), RevitIssueCodes.Mismatch,
                    "renaming the key parameter renames it on every " + cat.Name + " element; those elements are not declared, so in a workshared model you may need to own them");
            }
            foreach (var f in p.Fields)
            {
                var sf = MatchField(doc, def, f, out _);
                string actual = sf != null ? FieldName(doc, sf) : f;
                if (!have.Contains(actual, StringComparer.OrdinalIgnoreCase)) p.Differences.Add("field '" + f + "' is missing");
            }
            foreach (var k in p.Keys)
            {
                if (k.Existing is null) { p.Differences.Add("key '" + k.Name + "' is missing"); continue; }
                bool differs = false;
                foreach (var kv in k.Values)
                {
                    var prm = ParamAccess.Find(k.Existing, kv.Key, out _);
                    if (prm is null) continue;
                    if (!ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out string perr))
                    {
                        OpContext.Error(issues, k.Path + ".values." + kv.Key, RevitIssueCodes.Invalid, perr);
                        continue;
                    }
                    if (!ParamAccess.Matches(prm, pv, ctx.Units)) { p.Differences.Add("key '" + k.Name + "' " + kv.Key + " is " + ParamAccess.Read(prm, ctx.Units).ToJson()); differs = true; }
                }
                if (differs) changedKeys.Add(k.Existing);
            }
            if (p.Differences.Count > 0)
            {
                if (!args.GetBool("update"))
                    OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Mismatch,
                        "key schedule '" + p.Existing.Name + "' exists and differs (" + string.Join("; ", p.Differences.Take(8)) + "); set update:true to add and set what is missing (nothing is removed)");
                else if (declare)
                {
                    ctx.DeclareModifies(p.Existing.Id);
                    foreach (var e in changedKeys) ctx.DeclareModifies(e.Id);
                }
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        private static Element InsertKey(Document doc, ViewSchedule s)
        {
            var before = new HashSet<long>(KeyElements(doc, s).Select(e => ElementIds.ToLong(e.Id)));
            var body = s.GetTableData().GetSectionData(SectionType.Body);
            int at = new[] { body.LastRowNumber + 1, body.LastRowNumber, body.FirstRowNumber + 1, body.FirstRowNumber }.Where(i => i >= 0 && body.CanInsertRow(i)).DefaultIfEmpty(-1).First();
            if (at < 0) throw new OpFailedException("not_allowed", "Revit does not allow a new row in key schedule '" + s.Name + "'");
            body.InsertRow(at);
            doc.Regenerate();
            var added = KeyElements(doc, s).Where(e => !before.Contains(ElementIds.ToLong(e.Id))).ToList();
            if (added.Count != 1) throw new OpFailedException("key_failed", "inserting a row in '" + s.Name + "' gave " + added.Count + " new key elements, expected 1");
            return added[0];
        }

        private static void SetValues(OpContext ctx, Element key, KeyReq k)
        {
            foreach (var kv in k.Values)
            {
                var prm = ParamAccess.Find(key, kv.Key, out string? err) ?? throw new OpFailedException("parameter_missing", "key '" + k.Name + "' has no parameter '" + kv.Key + "': " + err);
                if (!ParamAccess.TryPrepare(ctx.Doc, prm, kv.Value, ctx.Units, out var pv, out string perr)) throw new OpFailedException("parameter_value", "key '" + k.Name + "' " + kv.Key + ": " + perr);
                if (!ParamAccess.Matches(prm, pv, ctx.Units) && !ParamAccess.Apply(prm, pv))
                    throw new OpFailedException("parameter_value", "Revit refused the value of " + kv.Key + " on key '" + k.Name + "'");
            }
        }

        /// <summary>True when SetValues would write something on an existing key (the only keys declared in Validate).</summary>
        private static bool Differs(OpContext ctx, Element key, KeyReq k) =>
            k.Values.Any(kv => ParamAccess.Find(key, kv.Key, out _) is Parameter prm
                && (!ParamAccess.TryPrepare(ctx.Doc, prm, kv.Value, ctx.Units, out var pv, out _) || !ParamAccess.Matches(prm, pv, ctx.Units)));

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            ViewSchedule s;
            var keys = new JsonObject();
            if (p.Existing != null && p.Differences.Count == 0)
            {
                result.Unchanged.Add(ElementIds.ToWire(p.Existing.Id));
                foreach (var k in p.Keys) keys.Set(k.Name, ElementIds.ToWire(k.Existing!.Id));
                result.Outputs.Set("schedule", ElementIds.ToWire(p.Existing.Id));
                result.Outputs.Set("keys", keys);
                return;
            }
            if (p.Existing is null)
            {
                s = ViewSchedule.CreateKeySchedule(doc, p.Category.Id);
                s.Name = p.ScheduleName;
                result.Created.Add(ElementIds.ToWire(s.Id));
            }
            else
            {
                s = p.Existing;
                result.Modified.Add(ElementIds.ToWire(s.Id));
            }
            if (p.KeyParameter != null && !string.Equals(s.KeyScheduleParameterName, p.KeyParameter, StringComparison.Ordinal))
                s.KeyScheduleParameterName = p.KeyParameter;
            var def = s.Definition;
            var have = FieldNames(def);
            var bad = new List<string>();
            foreach (var f in p.Fields)
            {
                var sf = MatchField(doc, def, f, out string? err);
                if (sf is null) { bad.Add(err!); continue; }
                if (!have.Contains(FieldName(doc, sf), StringComparer.OrdinalIgnoreCase)) def.AddField(sf);
            }
            if (bad.Count > 0) throw new OpFailedException("field_missing", string.Join("; ", bad));
            doc.Regenerate();
            foreach (var k in p.Keys)
            {
                Element key;
                if (k.Existing != null)
                {
                    key = k.Existing;
                    if (Differs(ctx, key, k)) result.Modified.Add(ElementIds.ToWire(key.Id));
                }
                else
                {
                    key = InsertKey(doc, s);
                    var np = key.get_Parameter(BuiltInParameter.REF_TABLE_ELEM_NAME);
                    if (np is null || np.IsReadOnly || !np.Set(k.Name)) throw new OpFailedException("parameter_value", "Revit refused key name '" + k.Name + "'");
                    result.Created.Add(ElementIds.ToWire(key.Id));
                }
                SetValues(ctx, key, k);
                keys.Set(k.Name, ElementIds.ToWire(key.Id));
            }
            result.Outputs.Set("schedule", ElementIds.ToWire(s.Id));
            result.Outputs.Set("keys", keys);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            string? id = result.Outputs.GetStringOrNull("schedule");
            var s = id != null && ElementIds.TryParse(id, out var eid) ? doc.GetElement(eid) as ViewSchedule : null;
            if (s is null) { check.Fail("the key schedule was not found after the op"); check.Apply(result); return; }
            var rb = result.Readback;
            rb.Set("name", s.Name);
            if (!string.Equals(s.Name, args.GetString("name").Trim(), StringComparison.OrdinalIgnoreCase))
                check.Fail("schedule name is '" + s.Name + "', requested '" + args.GetString("name").Trim() + "'");
            check.Flag(rb, "keySchedule", s.Definition.IsKeySchedule, true);
            rb.Set("keyParameter", s.KeyScheduleParameterName);
            if (args.GetStringOrNull("keyParameter") is string kp && !string.Equals(s.KeyScheduleParameterName, kp.Trim(), StringComparison.Ordinal))
                check.Fail("key parameter is '" + s.KeyScheduleParameterName + "', requested '" + kp + "'");
            var have = FieldNames(s.Definition);
            rb.Set("fields", JsonArray.FromStrings(have));
            foreach (var f in args.GetStringList("fields"))
            {
                var sf = MatchField(doc, s.Definition, f.Trim(), out _);
                if (sf is null || !have.Contains(FieldName(doc, sf), StringComparer.OrdinalIgnoreCase)) check.Fail("field '" + f + "' is not in the schedule");
            }
            var elements = KeyElements(doc, s);
            rb.Set("keyCount", elements.Count);
            foreach (var k in args.GetObjectList("keys"))
            {
                string kn = k.GetString("name").Trim();
                var e = elements.FirstOrDefault(x => string.Equals(KeyName(x), kn, StringComparison.Ordinal));
                if (e is null) { check.Fail("key '" + kn + "' is not in the schedule"); continue; }
                foreach (var kv in k.GetMap("values"))
                {
                    var prm = ParamAccess.Find(e, kv.Key, out _);
                    if (prm is null) { check.Fail("key '" + kn + "' has no parameter '" + kv.Key + "'"); continue; }
                    if (ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out _) && !ParamAccess.Matches(prm, pv, ctx.Units))
                        check.Fail("key '" + kn + "' " + kv.Key + " reads " + ParamAccess.Read(prm, ctx.Units).ToJson() + ", requested " + kv.Value.ToJson());
                }
            }
            rb.Set("state", result.Created.Contains(id!) ? "created" : result.Modified.Contains(id!) ? "updated" : "unchanged");
            check.Apply(result);
        }
    }
}
