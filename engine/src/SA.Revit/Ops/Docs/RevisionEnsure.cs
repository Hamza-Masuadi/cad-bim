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
    /// revision.ensure (ensure): revisions in the model's revision table (Revision.Create), added to sheets through
    /// ViewSheet.SetAdditionalRevisionIds. This uses Revit's revision tool, never typed revision text
    /// (sheets-sets.md section 7).
    ///
    /// <para><b>Identity.</b> A revision is identified by its description (exact). Two existing revisions with the same
    /// description are refused as ambiguous. Date, issued to and issued by are compared when given; issued is
    /// compared only when the job states it. Differences are refused unless update is true. An issued revision is
    /// locked in Revit, so changing one is refused even with update: unissue it in Revit first.</para>
    ///
    /// <para><b>Code.</b> Revit assigns the revision number from the revision's numbering sequence; the API has no
    /// setter for it (Revision.RevisionNumber is read-only). <c>code</c> is therefore checked: against the job
    /// standard's revision formats when it lists any, and in readback against the number Revit gave the revision; a
    /// different number fails the op. Numbering "per project" or "per sheet" is a project setting this op does not
    /// change (changing it later renumbers issued revisions).</para>
    ///
    /// <para><b>Sheets.</b> Each sheet number in addToSheets must exist; revisions it does not show yet are added to its
    /// additional revisions (its existing ones are kept). That is the op's purpose and needs no update flag; the
    /// sheets are declared as modified.</para>
    /// </summary>
    public sealed class RevisionEnsure : OpHandler
    {
        public override string Name => "revision.ensure";

        private sealed class Item
        {
            public OpArgs Args = null!;
            public string Description = "";
            public Revision? Existing;
            public bool Change;
        }

        private sealed class Plan
        {
            public List<Item> Items = new List<Item>();
            public List<ViewSheet> Sheets = new List<ViewSheet>();
        }

        private static List<string> Differences(Revision r, OpArgs a)
        {
            var d = new List<string>();
            if (a.GetStringOrNull("date") is string date && !string.Equals(r.RevisionDate, date, StringComparison.Ordinal)) d.Add("date is '" + r.RevisionDate + "'");
            if (a.GetStringOrNull("issuedTo") is string to && !string.Equals(r.IssuedTo, to, StringComparison.Ordinal)) d.Add("issued to is '" + r.IssuedTo + "'");
            if (a.GetStringOrNull("issuedBy") is string by && !string.Equals(r.IssuedBy, by, StringComparison.Ordinal)) d.Add("issued by is '" + r.IssuedBy + "'");
            if (a.Has("issued") && r.Issued != a.GetBool("issued")) d.Add("issued is " + (r.Issued ? "true" : "false"));
            return d;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var all = Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id)).OfType<Revision>().ToList();
            bool update = args.GetBool("update");
            var std = DocsUtil.Standard(ctx);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in args.GetObjectList("revisions"))
            {
                var it = new Item { Args = a, Description = a.GetString("description").Trim() };
                string at = JsonPath.Key(a.Path, "description");
                if (it.Description.Length == 0) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "description is empty"); continue; }
                if (!seen.Add(it.Description)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "revision '" + it.Description + "' is listed twice"); continue; }
                if (a.GetStringOrNull("code") is string code && std != null && std.RevisionFormats.Count > 0 && std.MatchRevision(code) is null)
                    OpContext.Error(issues, JsonPath.Key(a.Path, "code"), RevitIssueCodes.Invalid, "revision code '" + code + "' matches none of the standard's revision formats (" + string.Join(", ", std.RevisionFormats.Select(f => f.Name)) + ")");
                var same = all.Where(r => string.Equals(r.Description, it.Description, StringComparison.Ordinal)).ToList();
                if (same.Count > 1) { OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, same.Count + " revisions are described '" + it.Description + "' (ids " + string.Join(", ", same.Select(r => ElementIds.ToWire(r.Id))) + ")"); continue; }
                it.Existing = same.FirstOrDefault();
                if (it.Existing != null)
                {
                    if (a.GetStringOrNull("code") is string c2 && !string.Equals(it.Existing.RevisionNumber, c2, StringComparison.Ordinal))
                        OpContext.Error(issues, JsonPath.Key(a.Path, "code"), RevitIssueCodes.Mismatch, "revision '" + it.Description + "' has number '" + it.Existing.RevisionNumber + "' from its numbering sequence; Revit assigns numbers, they cannot be set");
                    var diffs = Differences(it.Existing, a);
                    if (diffs.Count > 0)
                    {
                        if (!update) OpContext.Error(issues, at, RevitIssueCodes.Mismatch, "revision '" + it.Description + "' exists and differs (" + string.Join("; ", diffs) + "); set update:true to change it");
                        else if (it.Existing.Issued && !(a.Has("issued") && !a.GetBool("issued") && diffs.Count == 1))
                            OpContext.Error(issues, at, RevitIssueCodes.ReadOnly, "revision '" + it.Description + "' is issued (locked); unissue it in Revit before changing it");
                        else { it.Change = true; if (declare) ctx.DeclareModifies(it.Existing.Id); }
                    }
                }
                else if (a.GetStringOrNull("code") != null)
                    OpContext.Warning(issues, JsonPath.Key(a.Path, "code"), RevitIssueCodes.Unchecked, "Revit numbers new revisions from the numbering sequence; readback fails the op if the number is not '" + a.GetString("code") + "'");
                p.Items.Add(it);
            }
            var nums = args.GetStringList("addToSheets");
            for (int i = 0; i < nums.Count; i++)
            {
                var s = DocsUtil.Sheet(ctx, new JsonString(nums[i]), JsonPath.Index(ctx.FieldPath(args, "addToSheets"), i), issues, out _);
                if (s is null) continue;
                if (p.Sheets.Any(x => x.Id == s.Id)) continue;
                p.Sheets.Add(s);
                if (declare)
                {
                    var shown = new HashSet<long>(s.GetAllRevisionIds().Select(ElementIds.ToLong));
                    if (p.Items.Any(x => x.Existing is null || !shown.Contains(ElementIds.ToLong(x.Existing.Id)))) ctx.DeclareModifies(s.Id);
                }
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        /// <summary>Writes the fields; returns whether the revision is to be issued, which the caller does last.</summary>
        private static bool Set(Revision r, OpArgs a)
        {
            if (r.Issued) r.Issued = false; // only reached when the request itself unissues it
            r.Description = a.GetString("description").Trim();
            if (a.GetStringOrNull("date") is string d) r.RevisionDate = d;
            if (a.GetStringOrNull("issuedTo") is string t) r.IssuedTo = t;
            if (a.GetStringOrNull("issuedBy") is string b) r.IssuedBy = b;
            return a.GetBool("issued");
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var ids = new List<ElementId>();
            var issue = new List<Revision>();
            foreach (var it in p.Items)
            {
                Revision r;
                if (it.Existing is null)
                {
                    r = Revision.Create(doc);
                    if (Set(r, it.Args)) issue.Add(r);
                    result.Created.Add(ElementIds.ToWire(r.Id));
                }
                else
                {
                    r = it.Existing;
                    if (it.Change) { if (Set(r, it.Args)) issue.Add(r); result.Modified.Add(ElementIds.ToWire(r.Id)); }
                    else result.Unchanged.Add(ElementIds.ToWire(r.Id));
                }
                ids.Add(r.Id);
            }
            foreach (var s in p.Sheets)
            {
                var shown = new HashSet<long>(s.GetAllRevisionIds().Select(ElementIds.ToLong));
                var add = ids.Where(i => !shown.Contains(ElementIds.ToLong(i))).ToList();
                if (add.Count == 0) { result.Unchanged.Add(ElementIds.ToWire(s.Id)); continue; }
                var extra = s.GetAdditionalRevisionIds().ToList();
                extra.AddRange(add);
                s.SetAdditionalRevisionIds(extra);
                result.Modified.Add(ElementIds.ToWire(s.Id));
            }
            // Issued last: the UI locks an issued revision's "Revisions on Sheet" box; the API documents no such lock,
            // but this order needs none.
            foreach (var r in issue) r.Issued = true;
            result.Outputs.Set("revisions", JsonArray.FromStrings(ids.Select(ElementIds.ToWire)));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var rb = result.Readback;
            var ids = (result.Outputs["revisions"] as JsonArray ?? new JsonArray()).Select(v => v.AsString()).ToList();
            var list = args.GetObjectList("revisions");
            if (ids.Count != list.Count) { check.Fail("expected " + list.Count + " revision ids, have " + ids.Count); check.Apply(result); return; }
            var arr = new JsonArray();
            var revs = new List<Revision>();
            for (int i = 0; i < ids.Count; i++)
            {
                var r = ElementIds.TryParse(ids[i], out var eid) ? doc.GetElement(eid) as Revision : null;
                if (r is null) { check.Fail("revision " + ids[i] + " is missing after the op"); continue; }
                revs.Add(r);
                var a = list[i];
                var rec = new JsonObject().Add("id", ids[i]);
                check.Text(rec, "description", r.Description, a.GetString("description").Trim());
                check.Text(rec, "date", r.RevisionDate, a.GetStringOrNull("date"));
                check.Text(rec, "issuedTo", r.IssuedTo, a.GetStringOrNull("issuedTo"));
                check.Text(rec, "issuedBy", r.IssuedBy, a.GetStringOrNull("issuedBy"));
                check.Text(rec, "number", r.RevisionNumber, a.GetStringOrNull("code"), "revision number");
                check.Flag(rec, "issued", r.Issued, a.Has("issued") || result.Created.Contains(ids[i]) ? a.GetBool("issued") : (bool?)null);
                rec.Add("state", result.Created.Contains(ids[i]) ? "created" : result.Modified.Contains(ids[i]) ? "updated" : "unchanged");
                arr.Add(rec);
            }
            rb.Set("revisions", arr);
            var sheets = new JsonArray();
            foreach (var num in args.GetStringList("addToSheets"))
            {
                var s = Lookup.SheetByNumber(doc, num.StartsWith("sheet:", StringComparison.OrdinalIgnoreCase) ? num.Substring(6).Trim() : num.Trim());
                if (s is null) { check.Fail("sheet " + num + " is missing after the op"); continue; }
                var shown = new HashSet<long>(s.GetAllRevisionIds().Select(ElementIds.ToLong));
                foreach (var r in revs) if (!shown.Contains(ElementIds.ToLong(r.Id))) check.Fail("sheet " + s.SheetNumber + " does not show revision '" + r.Description + "'");
                sheets.Add(s.SheetNumber);
            }
            rb.Set("sheets", sheets);
            check.Apply(result);
        }
    }
}
