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

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// view.rename: renames views (View.Name). Every new name is checked in Validate: not empty, no character Revit
    /// refuses, not used by another view of the same view type (unless that view is itself renamed away in the same
    /// op), and not given twice. Swaps (A to B and B to A) are done in two passes through temporary unique names, so
    /// Revit never sees a clash. Sheets are refused: sheet names belong to sheet.create. A view that already has the
    /// name is reported unchanged. Every renamed view is declared as modified for the ownership check.
    /// </summary>
    public sealed class ViewRename : OpHandler
    {
        public override string Name => "view.rename";

        private sealed class Item
        {
            public View View = null!;
            public string NewName = "";
        }

        private static List<Item>? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var items = new List<Item>();
            var list = args.GetObjectList("views");
            var seenIds = new HashSet<long>();
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                var v = DocsUtil.View(ctx, o.Get("view"), JsonPath.Key(o.Path, "view"), issues, out bool pending);
                string? name = DocsUtil.CheckName(o.GetString("name"), "view", JsonPath.Key(o.Path, "name"), issues);
                if (pending || v is null || name is null) continue;
                if (v is ViewSheet) { OpContext.Error(issues, JsonPath.Key(o.Path, "view"), RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " is a sheet; rename sheets with sheet.create (update:true)"); continue; }
                if (!seenIds.Add(ElementIds.ToLong(v.Id))) { OpContext.Error(issues, JsonPath.Key(o.Path, "view"), RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(v) + " is listed twice"); continue; }
                var prm = v.get_Parameter(BuiltInParameter.VIEW_NAME);
                if (prm != null && prm.IsReadOnly) { OpContext.Error(issues, JsonPath.Key(o.Path, "name"), RevitIssueCodes.ReadOnly, "the name of view " + DocsUtil.Describe(v) + " is read-only"); continue; }
                items.Add(new Item { View = v, NewName = name });
            }
            // Uniqueness per view type, allowing names freed by other renames in this op.
            var renamedAway = new HashSet<long>(items.Where(x => !string.Equals(x.View.Name, x.NewName, StringComparison.Ordinal)).Select(x => ElementIds.ToLong(x.View.Id)));
            foreach (var g in items.GroupBy(x => x.View.ViewType))
            {
                foreach (var dup in g.GroupBy(x => x.NewName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
                    OpContext.Error(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Invalid, "name '" + dup.Key + "' is given to " + dup.Count() + " " + g.Key + " views");
                foreach (var it in g)
                {
                    var taken = DocsUtil.NameTaken(doc, it.NewName, it.View.ViewType, it.View.Id);
                    if (taken != null && !renamedAway.Contains(ElementIds.ToLong(taken.Id)))
                        OpContext.Error(issues, ctx.FieldPath(args, "views"), RevitIssueCodes.Exists,
                            "a " + it.View.ViewType + " view named '" + taken.Name + "' already exists (id " + ElementIds.ToWire(taken.Id) + ")");
                }
            }
            if (declare) foreach (var it in items) if (!string.Equals(it.View.Name, it.NewName, StringComparison.Ordinal)) ctx.DeclareModifies(it.View.Id);
            return DocsUtil.HasErrors(issues) ? null : items;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var items = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var change = items.Where(x => !string.Equals(x.View.Name, x.NewName, StringComparison.Ordinal)).ToList();
            foreach (var it in items.Except(change)) result.Unchanged.Add(ElementIds.ToWire(it.View.Id));
            // Pass 1: temporary unique names for views whose new name is currently held by another view in the list.
            var current = new HashSet<string>(change.Select(x => x.View.Name), StringComparer.OrdinalIgnoreCase);
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            int n = 0;
            foreach (var it in change)
                if (change.Any(o => o != it && string.Equals(o.NewName, it.View.Name, StringComparison.OrdinalIgnoreCase)) || current.Contains(it.NewName))
                    it.View.Name = "SA-tmp-" + token + "-" + (n++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Pass 2: final names.
            foreach (var it in change)
            {
                it.View.Name = it.NewName;
                result.Modified.Add(ElementIds.ToWire(it.View.Id));
            }
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var arr = new JsonArray();
            foreach (var o in args.GetObjectList("views"))
            {
                var f = Lookup.View(ctx.Doc, o.Get("view"));
                string want = o.GetString("name").Trim();
                // A view given by its old name cannot be found by that name any more: look it up among the changed ids.
                View? v = f.Ok ? f.Value : null;
                // In a swap, the old name now belongs to the other view, so a name match is only trusted when it already reads the new name.
                if (v is null || !string.Equals(v.Name, want, StringComparison.Ordinal))
                {
                    v = result.Modified.Concat(result.Unchanged)
                        .Select(id => ElementIds.TryParse(id, out var e) ? ctx.Doc.GetElement(e) as View : null)
                        .FirstOrDefault(x => x != null && string.Equals(x.Name, want, StringComparison.Ordinal));
                }
                if (v is null) { check.Fail("no view named '" + want + "' after the op"); continue; }
                var rec = new JsonObject().Add("id", ElementIds.ToWire(v.Id));
                check.Text(rec, "name", v.Name, want, "view " + ElementIds.ToWire(v.Id) + " name");
                rec.Add("state", result.Modified.Contains(ElementIds.ToWire(v.Id)) ? "updated" : "unchanged");
                arr.Add(rec);
            }
            result.Readback.Set("views", arr);
            check.Apply(result);
        }
    }
}
