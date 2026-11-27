using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    /// <summary>
    /// param.set: set instance or type parameters on listed elements or on a filtered set.
    /// Every element and every value is resolved and converted in Validate, so a bad name, a read-only parameter
    /// or a value of the wrong kind fails the job before anything changes. With target "type" each distinct type is
    /// set once. skipReadOnly turns read-only parameters into a warning and leaves them alone.
    /// </summary>
    public sealed class ParamSet : OpHandler
    {
        public override string Name => "param.set";

        private const int MaxIssues = 50;

        private sealed class Item
        {
            public Element Target = null!;
            public string Name = "";
            public Parameter Param = null!;
            public PreparedValue Value = null!;
        }

        private sealed class Plan
        {
            public List<Item> Items = new List<Item>();
            public List<string> Skipped = new List<string>();
            public int Targets;
        }

        private static List<Element> Elements(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var list = new List<Element>();
            if (args.Has("elements"))
            {
                var value = args.Raw["elements"];
                if (OpContext.IsPending(value) && !(value is JsonArray)) return list;
                // A whole-list reference ("$w1.walls" or "$w1") resolves to an array or a result object.
                var raw = value as JsonArray ?? new JsonArray(value ?? JsonValue.Null);
                for (int i = 0; i < raw.Count; i++)
                {
                    if (OpContext.IsPending(raw[i])) continue;
                    string at = JsonPath.Index(ctx.FieldPath(args, "elements"), i);
                    foreach (var idText in Refs.ElementIds(raw[i]))
                    {
                        if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Element e))
                            OpContext.Error(issues, at, RevitIssueCodes.NotFound, "element " + idText + " does not exist");
                        else list.Add(e);
                    }
                }
                return list;
            }
            var f = args.GetObject("filter");
            if (f is null) return list;
            var cat = Lookup.Category(doc, f.GetString("category"), out string? cerr);
            if (cat is null) { OpContext.Error(issues, f.Path + ".category", RevitIssueCodes.NotFound, cerr!); return list; }
            FilteredElementCollector col;
            if (f.Has("view"))
            {
                if (OpContext.IsPending(f.Raw["view"])) return list;
                var v = Lookup.View(doc, f.Get("view"));
                if (!v.Ok) { OpContext.Error(issues, f.Path + ".view", v.Code, v.Error!); return list; }
                col = new FilteredElementCollector(doc, v.Value!.Id);
            }
            else col = new FilteredElementCollector(doc);
            IEnumerable<Element> q = col.OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();
            if (f.GetStringOrNull("level") is string lname)
            {
                var lv = Lookup.Level(doc, lname);
                if (!lv.Ok) { OpContext.Error(issues, f.Path + ".level", lv.Code, lv.Error!); return list; }
                var lid = lv.Value!.Id;
                q = q.Where(e => e.LevelId == lid);
            }
            if (f.GetStringOrNull("type") is string tname)
                q = q.Where(e => doc.GetElement(e.GetTypeId()) is ElementType t &&
                                 (string.Equals(t.Name, tname, StringComparison.Ordinal) || string.Equals(Lookup.FamilyTypeName(t), tname, StringComparison.Ordinal)));
            var where = f.GetMap("where");
            if (where.Count > 0)
            {
                q = q.Where(e => where.All(kv =>
                {
                    var p = ParamAccess.Find(e, kv.Key, out _);
                    if (p is null) return false;
                    if (kv.Value is JsonString s && p.StorageType != StorageType.String)
                        return string.Equals(p.AsValueString(), s.Value, StringComparison.Ordinal);
                    if (!ParamAccess.TryPrepare(doc, p, kv.Value, ctx.Units, out var pv, out _)) return false;
                    return ParamAccess.Matches(p, pv, ctx.Units);
                }));
            }
            list.AddRange(q);
            return list;
        }

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var plan = new Plan();
            bool typeTarget = args.GetString("target") == "type";
            bool skipReadOnly = args.GetBool("skipReadOnly");
            var elements = Elements(ctx, args, issues);
            var targets = new List<Element>();
            var seen = new HashSet<long>();
            foreach (var e in elements)
            {
                Element? t = e;
                if (typeTarget)
                {
                    t = doc.GetElement(e.GetTypeId());
                    if (t is null) { Add(issues, ctx.FieldPath(args, "target"), RevitIssueCodes.NotFound, "element " + ElementIds.ToWire(e.Id) + " has no type"); continue; }
                }
                if (seen.Add(ElementIds.ToLong(t.Id))) targets.Add(t);
            }
            plan.Targets = targets.Count;
            if (targets.Count == 0 && !OpContext.IsPending(args.Raw["elements"]) && !issues.Any(i => i.IsError))
                OpContext.Warning(issues, args.Path, RevitIssueCodes.NotFound, "no elements matched; nothing will be set");

            var values = args.GetMap("values");
            if (values.Count == 0) OpContext.Error(issues, ctx.FieldPath(args, "values"), RevitIssueCodes.Invalid, "values is empty");
            foreach (var t in targets)
            {
                bool touched = false;
                foreach (var kv in values)
                {
                    string at = JsonPath.Key(ctx.FieldPath(args, "values"), kv.Key);
                    if (OpContext.IsPending(kv.Value)) continue;
                    var p = ParamAccess.Find(t, kv.Key, out string? err);
                    if (p is null) { Add(issues, at, RevitIssueCodes.NotFound, err!); continue; }
                    if (p.IsReadOnly)
                    {
                        if (skipReadOnly) { plan.Skipped.Add(ElementIds.ToWire(t.Id) + ":" + kv.Key); continue; }
                        Add(issues, at, RevitIssueCodes.ReadOnly, "parameter '" + kv.Key + "' is read-only on element " + ElementIds.ToWire(t.Id) + " (set skipReadOnly to skip it)");
                        continue;
                    }
                    if (!ParamAccess.TryPrepare(doc, p, kv.Value, ctx.Units, out var pv, out string perr))
                    {
                        Add(issues, at, RevitIssueCodes.Invalid, "parameter '" + kv.Key + "' (" + p.StorageType + ") on element " + ElementIds.ToWire(t.Id) + ": " + perr);
                        continue;
                    }
                    plan.Items.Add(new Item { Target = t, Name = kv.Key, Param = p, Value = pv });
                    touched = true;
                }
                if (touched) ctx.DeclareModifies(t.Id);
            }
            return plan;
        }

        private static void Add(List<Issue> issues, string path, string code, string message)
        {
            int n = issues.Count(i => i.IsError);
            if (n < MaxIssues) OpContext.Error(issues, path, code, message);
            else if (n == MaxIssues) OpContext.Error(issues, path, code, "more issues of this op are not listed");
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plan = Build(ctx, args, issues);
            if (plan.Skipped.Count > 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "values"), RevitIssueCodes.ReadOnly, plan.Skipped.Count + " read-only parameter value(s) will be skipped");
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plan = Build(ctx, args, issues);
            if (issues.Any(i => i.IsError)) throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Take(5).Select(i => i.Message)));
            var modified = new HashSet<string>();
            foreach (var it in plan.Items)
            {
                if (!ParamAccess.Apply(it.Param, it.Value))
                    throw new OpFailedException("parameter_value", "Revit refused '" + it.Name + "' = " + ParamAccess.Describe(it.Value) + " on element " + ElementIds.ToWire(it.Target.Id));
                modified.Add(ElementIds.ToWire(it.Target.Id));
            }
            result.Modified.AddRange(modified);
            result.Outputs.Add("count", modified.Count);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var values = args.GetMap("values");
            var perName = new JsonObject();
            bool skipReadOnly = args.GetBool("skipReadOnly");
            var skipped = new JsonArray();
            foreach (var kv in values)
            {
                int set = 0;
                JsonValue? first = null;
                bool uniform = true;
                foreach (var idText in result.Modified)
                {
                    if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Element e)) { check.Fail("element " + idText + " no longer exists"); continue; }
                    var p = ParamAccess.Find(e, kv.Key, out string? err);
                    if (p is null) { check.Fail(err ?? kv.Key); continue; }
                    if (p.IsReadOnly && skipReadOnly) { skipped.Add(idText + ":" + kv.Key); continue; }
                    var read = ParamAccess.Read(p, u);
                    if (first is null) first = read; else if (!JsonValue.DeepEquals(first, read)) uniform = false;
                    if (!ParamAccess.TryPrepare(doc, p, kv.Value, u, out var pv, out string perr)) { check.Fail(kv.Key + ": " + perr); continue; }
                    if (!ParamAccess.Matches(p, pv, u))
                        check.Fail("'" + kv.Key + "' on element " + idText + " reads " + read.ToJson() + ", requested " + kv.Value.ToJson());
                    set++;
                }
                perName.Add(kv.Key, new JsonObject().Add("set", set).Add("value", first ?? JsonValue.Null).Add("uniform", uniform));
            }
            result.Readback.Add("count", result.Modified.Count);
            result.Readback.Add("target", args.GetString("target"));
            result.Readback.Add("parameters", perName);
            if (skipped.Count > 0) result.Readback.Add("skippedReadOnly", skipped);
            check.Apply(result);
        }
    }
}
