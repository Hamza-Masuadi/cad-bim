using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// text.replace: literal find and replace in text notes and in chosen text parameters.
    ///
    /// <para><b>Text notes</b> are edited through their FormattedText: each match is replaced with
    /// FormattedText.SetPlainText(TextRange, string), last match first so earlier ranges stay valid, then
    /// TextNote.SetFormattedText. Bold, italic, lists and the other character formats outside the replaced range are
    /// kept; the replacement takes the format of the text it replaces. FormattedText separates paragraphs with "\r"
    /// and refuses "\n", so line breaks in find and replace are written as "\r" for text notes (parameters keep them as
    /// given). The trailing "\r" Revit keeps at the end of a note is never matched.</para>
    ///
    /// <para><b>Parameters</b> are String-storage parameters (ParamAccess.Find: display name, built-in name or GUID)
    /// on non-type elements of the categories and, with includeTypes, on their types. A read-only parameter with a
    /// match is an error (Validate), so the op never changes part of what was asked.</para>
    ///
    /// <para>Elements in groups are skipped with a warning (editing a group member outside group edit mode is
    /// refused or breaks the group). expectCount compares with the number of elements that would change, before
    /// anything is changed. dryRun opens the transaction but changes nothing; its readback confirms the text is still
    /// the "before" text. Every changed element is declared in Validate; with ownership "skip", elements the runner
    /// left out are not changed, not counted and named in a warning. Readback checks every planned change, not only
    /// the first 500 listed in changes.</para>
    /// </summary>
    public sealed class TextReplace : OpHandler
    {
        public override string Name => "text.replace";

        private const int MaxListed = 500;

        private sealed class Change
        {
            public Element Element = null!;
            public string Kind = "";
            public string Field = "";
            /// <summary>The parameter name as the job gave it, used again in readback (display names can be ambiguous).</summary>
            public string Lookup = "";
            public Parameter? Param;
            public string Before = "";
            public List<int> Hits = new List<int>();
            public string After = "";
        }

        private sealed class Plan
        {
            public List<Change> Changes = new List<Change>();
            public int Skipped;
        }

        internal static string Plain(TextNote tn)
        {
            string s = tn.GetFormattedText().GetPlainText() ?? "";
            return s.EndsWith("\r", StringComparison.Ordinal) ? s.Substring(0, s.Length - 1) : s;
        }

        private static string Describe(Element e) => (e is ElementType ? "type" : e.Category?.Name ?? "element") + " " + ElementIds.ToWire(e.Id) + (string.IsNullOrEmpty(e.Name) ? "" : " '" + e.Name + "'");

        internal static string Breaks(string s) => s.Replace("\r\n", "\r").Replace("\n", "\r");

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool declare)
        {
            var doc = ctx.Doc;
            string rawFind = args.GetString("find");
            string? rawReplace = args.GetStringOrNull("replace");
            bool dry = args.GetBool("dryRun");
            if (rawFind.Length == 0)
                OpContext.Error(issues, ctx.FieldPath(args, "find"), RevitIssueCodes.Invalid, "find is empty");
            if (rawReplace is null && !dry)
                OpContext.Error(issues, ctx.FieldPath(args, "replace"), RevitIssueCodes.Invalid, "replace is required unless dryRun is true (\"\" deletes the match)");
            rawReplace ??= "";
            // Text notes separate paragraphs with "\r" only; parameters keep the line breaks as given.
            string noteFind = Breaks(rawFind), noteReplace = Breaks(rawReplace);
            bool mc = args.GetBool("matchCase"), ww = args.GetBool("wholeWord");
            if (!args.GetBool("textNotes") && args.GetStringList("parameters").Count == 0)
                OpContext.Error(issues, ctx.FieldPath(args, "textNotes"), RevitIssueCodes.Invalid, "nothing to search: textNotes is false and no parameters are given");

            HashSet<long>? views = null;
            bool pending = false;
            if (args.Get("views") is JsonArray va)
            {
                views = new HashSet<long>();
                for (int i = 0; i < va.Count; i++)
                {
                    var v = DocsUtil.View(ctx, va[i], JsonPath.Index(ctx.FieldPath(args, "views"), i), issues, out bool pd);
                    pending |= pd;
                    if (v != null) views.Add(ElementIds.ToLong(v.Id));
                }
            }
            var cats = new List<Category>();
            var catNames = args.GetStringList("categories");
            for (int i = 0; i < catNames.Count; i++)
            {
                var c = DocsUtil.Category(doc, catNames[i], JsonPath.Index(ctx.FieldPath(args, "categories"), i), issues);
                if (c != null) cats.Add(c);
            }
            if (pending || DocsUtil.HasErrors(issues)) return null;

            var p = new Plan();
            var leftOut = new List<string>();
            var seenParams = new HashSet<string>(StringComparer.Ordinal);
            void Consider(Element e, string kind, string field, string lookup, Parameter? prm, string text, string at)
            {
                string find = prm is null ? noteFind : rawFind;
                string replace = prm is null ? noteReplace : rawReplace;
                var hits = TextMatch.FindAll(text, find, mc, ww);
                if (hits.Count == 0) return;
                if (prm != null && !seenParams.Add(ElementIds.ToWire(e.Id) + "/" + ElementIds.ToWire(prm.Id))) return;
                if (ElementIds.IsValid(e.GroupId))
                {
                    p.Skipped++;
                    OpContext.Warning(issues, at, RevitIssueCodes.Invalid, Describe(e) + " is in a group; its " + field + " is left unchanged");
                    return;
                }
                if (!dry && ctx.IsLeftOut(e.Id))
                {
                    leftOut.Add(ElementIds.ToWire(e.Id));
                    return;
                }
                if (prm != null && prm.IsReadOnly && !dry)
                {
                    OpContext.Error(issues, at, RevitIssueCodes.Invalid, "parameter '" + field + "' of " + Describe(e) + " matches but is read-only");
                    return;
                }
                p.Changes.Add(new Change { Element = e, Kind = kind, Field = field, Lookup = lookup, Param = prm, Before = text, Hits = hits, After = TextMatch.Apply(text, hits, find.Length, replace) });
            }

            if (args.GetBool("textNotes"))
                foreach (TextNote tn in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>().OrderBy(t => ElementIds.ToLong(t.Id)))
                {
                    if (views != null && !views.Contains(ElementIds.ToLong(tn.OwnerViewId))) continue;
                    Consider(tn, "textNote", "text", "text", null, Plain(tn), ctx.FieldPath(args, "find"));
                }

            var pnames = args.GetStringList("parameters");
            if (pnames.Count > 0)
            {
                var targets = new List<Element>();
                var seen = new HashSet<long>();
                foreach (var c in cats)
                    foreach (var e in new FilteredElementCollector(doc).OfCategoryId(c.Id).WhereElementIsNotElementType().OrderBy(x => ElementIds.ToLong(x.Id)))
                    {
                        if (seen.Add(ElementIds.ToLong(e.Id))) targets.Add(e);
                        if (args.GetBool("includeTypes") && doc.GetElement(e.GetTypeId()) is ElementType t && seen.Add(ElementIds.ToLong(t.Id))) targets.Add(t);
                    }
                var found = new bool[pnames.Count];
                foreach (var e in targets)
                    for (int i = 0; i < pnames.Count; i++)
                    {
                        var prm = ParamAccess.Find(e, pnames[i], out _);
                        if (prm is null || prm.StorageType != StorageType.String) continue;
                        found[i] = true;
                        Consider(e, "parameter", prm.Definition?.Name ?? pnames[i], pnames[i], prm, prm.AsString() ?? "", JsonPath.Index(ctx.FieldPath(args, "parameters"), i));
                    }
                for (int i = 0; i < pnames.Count; i++)
                    if (!found[i])
                        OpContext.Warning(issues, JsonPath.Index(ctx.FieldPath(args, "parameters"), i), RevitIssueCodes.NotFound, "no element of the categories has a text parameter '" + pnames[i] + "'");
            }

            if (leftOut.Count > 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "find"), RevitIssueCodes.Invalid,
                    leftOut.Distinct().Count() + " element(s) with matches are not editable by you and are left unchanged (ownership skip): " + string.Join(", ", leftOut.Distinct().Take(20)));
            int count = p.Changes.Select(c => ElementIds.ToLong(c.Element.Id)).Distinct().Count();
            if (args.Has("expectCount") && args.GetInt("expectCount") != count)
                OpContext.Error(issues, ctx.FieldPath(args, "expectCount"), RevitIssueCodes.Invalid,
                    count + " element(s) would change (" + p.Changes.Sum(c => c.Hits.Count) + " match(es)), expectCount is " + args.GetInt("expectCount") + "; nothing was changed");
            if (DocsUtil.HasErrors(issues)) return null;
            if (declare && !dry)
                foreach (var id in p.Changes.Select(c => c.Element.Id).Distinct()) ctx.DeclareModifies(id);
            return p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, true);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, false) ?? throw DocsUtil.Invalid(issues);
            bool dry = args.GetBool("dryRun");
            string noteReplace = Breaks(args.GetStringOrNull("replace") ?? "");
            int noteFindLength = Breaks(args.GetString("find")).Length;
            if (!dry)
                foreach (var c in p.Changes)
                {
                    if (c.Element is TextNote tn)
                    {
                        var ft = tn.GetFormattedText();
                        for (int i = c.Hits.Count - 1; i >= 0; i--) ft.SetPlainText(new TextRange(c.Hits[i], noteFindLength), noteReplace);
                        tn.SetFormattedText(ft);
                    }
                    else if (c.Param != null && !c.Param.Set(c.After))
                        throw new OpFailedException(RevitIssueCodes.Invalid, "Revit refused the new value of '" + c.Field + "' on " + Describe(c.Element));
                }
            var ids = p.Changes.Select(c => c.Element.Id).GroupBy(ElementIds.ToLong).Select(g => g.First()).ToList();
            if (!dry) foreach (var id in ids) result.Modified.Add(ElementIds.ToWire(id));
            var list = new JsonArray();
            foreach (var c in p.Changes.Take(MaxListed))
                list.Add(new JsonObject().Add("id", ElementIds.ToWire(c.Element.Id)).Add("kind", c.Kind).Add("field", c.Field).Add("before", c.Before).Add("after", c.After));
            result.Outputs.Set("count", ids.Count);
            result.Outputs.Set("matches", p.Changes.Sum(c => c.Hits.Count));
            result.Outputs.Set("changes", list);
            result.Outputs.Set("skippedGroups", p.Skipped);
            Planned.Remove(result);
            Planned.Add(result, p.Changes.Select(c => new Expected { Id = c.Element.Id, Field = c.Field, Lookup = c.Lookup, IsNote = c.Param is null, Before = c.Before, After = c.After }).ToList());
        }

        private sealed class Expected
        {
            public ElementId Id = ElementId.InvalidElementId;
            public string Field = "", Lookup = "", Before = "", After = "";
            public bool IsNote;
        }

        /// <summary>Every planned change of a run, so readback checks all of them and not only the listed ones.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<OpResult, List<Expected>> Planned =
            new System.Runtime.CompilerServices.ConditionalWeakTable<OpResult, List<Expected>>();

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            bool dry = args.GetBool("dryRun");
            int ok = 0;
            if (!Planned.TryGetValue(result, out var planned))
            {
                check.Fail("the planned changes of this run are not available to read back");
                planned = new List<Expected>();
            }
            foreach (var x in planned)
            {
                string id = ElementIds.ToWire(x.Id);
                string want = dry ? x.Before : x.After;
                if (!(ctx.Doc.GetElement(x.Id) is Element e)) { check.Fail("element " + id + " is gone"); continue; }
                string? now = x.IsNote ? (e is TextNote tn ? Plain(tn) : null) : ParamAccess.Find(e, x.Lookup, out _)?.AsString();
                if (!string.Equals(now ?? "", want, StringComparison.Ordinal))
                    check.Fail(Describe(e) + " " + x.Field + " reads '" + now + "', expected '" + want + "'");
                else ok++;
            }
            Planned.Remove(result);
            result.Readback.Set("checked", ok);
            result.Readback.Set("dryRun", dry);
            check.Apply(result);
        }
    }
}
