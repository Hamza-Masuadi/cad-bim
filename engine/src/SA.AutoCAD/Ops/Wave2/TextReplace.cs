using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// text.replace: find and replace in single-line text (TextString), multiline text, attribute references (single or
    /// multiline), table cells and multileader text, optionally in block definitions too (text, multiline text and
    /// attribute definition defaults). Multiline contents go through <see cref="MTextRuns"/> so formatting codes are
    /// kept. Objects holding fields, and table cells that are linked, not content-editable, hold a field or block, or
    /// are not the top-left of a merge, are left alone (a field would be turned into plain text). Objects with a match
    /// on a locked layer are skipped and counted. Xref and anonymous blocks are never searched.
    /// </summary>
    internal sealed class TextReplace : OpHandler<TextReplace.Plan>
    {
        public override string Name => "text.replace";

        private const int MaxListed = 500;

        internal sealed class Change
        {
            public ObjectId Id;
            public string Kind = "";
            public int Row = -1, Col = -1;
            public string Before = "", After = "";
            public int Matches;
        }

        internal sealed class Plan
        {
            public List<Change> Changes = new List<Change>();
            public int SkippedLocked, SkippedFields;
            public bool DryRun;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { DryRun = args.GetBool("dryRun") };
            string find = args.GetString("find");
            string? replace = args.GetStringOrNull("replace");
            bool isRegex = args.GetBool("regex");
            if (find.Length == 0) { OpContext.Error(issues, args, "find", "find_empty", "find must not be empty"); return p; }
            if (replace is null && !p.DryRun) OpContext.Error(issues, args, "replace", "replace_missing", "give replace (\"\" deletes the matches), or set dryRun true to only count");
            Regex rx;
            try { rx = MTextRuns.Pattern(find, isRegex, args.GetBool("wholeWord"), args.GetBool("matchCase")); }
            catch (ArgumentException e) { OpContext.Error(issues, args, "find", "regex_invalid", "find is not a valid regular expression: " + e.Message); return p; }
            string repl = replace ?? "";
            if (isRegex)
            {
                try { new Regex("").Match("").Result(repl); }
                catch (ArgumentException e) { OpContext.Error(issues, args, "replace", "replace_invalid", "replace is not a valid substitution: " + e.Message); return p; }
            }
            var kinds = new HashSet<string>(args.GetStringList("kinds"));
            var layers = Targets.Patterns(args, "layers");
            bool defs = args.GetBool("includeBlockDefinitions");
            string space = args.GetString("space");

            int n = 0;
            string Plain(string s)
            {
                n = 0;
                return rx.Replace(s, m => { n++; return isRegex ? m.Result(repl) : repl; });
            }
            string Rich(string s)
            {
                int total = 0;
                string r = MTextRuns.Replace(s, run => { string o = Plain(run); total += n; return o; });
                n = total;
                return r;
            }

            try
            {
                Db.Read(ctx, tr =>
                {
                    foreach (var sp in SheetDb.Spaces(tr, ctx.Database, space, defs, false))
                    {
                        var btr = (BlockTableRecord)tr.GetObject(sp.Id, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                        {
                            if (id.IsErased) continue;
                            if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                            if (layers.Count > 0 && !Targets.AnyMatch(layers, e.Layer)) continue;
                            switch (e)
                            {
                                case AttributeDefinition ad when sp.IsBlock && kinds.Contains("attributes"):
                                    if (ad.IsMTextAttributeDefinition) Consider(tr, p, ad, "attributeDefinition", MTextOf(() => ad.MTextAttributeDefinition), Rich, () => n);
                                    else Consider(tr, p, ad, "attributeDefinition", ad.TextString, Plain, () => n);
                                    break;
                                case AttributeDefinition _:
                                    break;
                                case DBText t when kinds.Contains("text"):
                                    Consider(tr, p, t, "text", t.TextString, Plain, () => n);
                                    break;
                                case MText mt when kinds.Contains("mtext"):
                                    Consider(tr, p, mt, "mtext", mt.Contents, Rich, () => n);
                                    break;
                                case MLeader ml when kinds.Contains("mleaders") && ml.ContentType == ContentType.MTextContent:
                                    Consider(tr, p, ml, "mleader", MTextOf(() => ml.MText), Rich, () => n);
                                    break;
                                case Table table when kinds.Contains("tables"):
                                    Cells(tr, p, table, Rich, () => n);
                                    break;
                                case BlockReference br when kinds.Contains("attributes"):
                                    foreach (ObjectId aid in br.AttributeCollection)
                                    {
                                        if (aid.IsErased || !(tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)) continue;
                                        if (ar.IsMTextAttribute) Consider(tr, p, ar, "attribute", MTextOf(() => ar.MTextAttribute), Rich, () => n);
                                        else Consider(tr, p, ar, "attribute", ar.TextString, Plain, () => n);
                                    }
                                    break;
                            }
                        }
                    }
                    return 0;
                });
            }
            catch (RegexMatchTimeoutException)
            {
                OpContext.Error(issues, args, "find", "regex_timeout", "the regular expression took too long on the drawing's text; simplify it");
                return p;
            }

            int objects = p.Changes.Select(c => c.Id).Distinct().Count();
            if (args.Has("expectCount") && objects != args.GetInt("expectCount"))
                OpContext.Error(issues, args, "expectCount", "count_mismatch", "expected " + args.GetInt("expectCount") + " objects to change but " + objects + " would" +
                    (p.SkippedLocked > 0 ? " (plus " + p.SkippedLocked + " on locked layers)" : ""));
            if (ctx.IsValidating)
            {
                if (p.SkippedLocked > 0) OpContext.Warning(issues, args, "layers", "skipped_locked", p.SkippedLocked + " objects with matches are on locked layers and are left unchanged");
                if (p.SkippedFields > 0) OpContext.Warning(issues, args, "kinds", "skipped_fields", p.SkippedFields + " objects or cells with matches hold fields, links or blocks and are left unchanged");
                if (p.Changes.Count == 0) OpContext.Warning(issues, args, "find", "no_matches", "nothing matches '" + find + "'");
            }
            return p;
        }

        /// <summary>
        /// Contents of the MText copy that MLeader.MText, AttributeReference.MTextAttribute and
        /// AttributeDefinition.MTextAttributeDefinition hand out; the copy is not in the database and is disposed here,
        /// not left to the finalizer thread.
        /// </summary>
        private static string MTextOf(Func<MText?> get)
        {
            using (var mt = get()) return mt?.Contents ?? "";
        }

        private static void Consider(Transaction tr, Plan p, Entity e, string kind, string before, Func<string, string> replace, Func<int> count)
        {
            string after = replace(before);
            int n = count();
            if (n == 0 || after == before) return;
            if (e.HasFields) { p.SkippedFields++; return; }
            if (SheetDb.IsOnLockedLayer(tr, e) || (e is AttributeReference ar && tr.GetObject(ar.OwnerId, OpenMode.ForRead) is Entity owner && SheetDb.IsOnLockedLayer(tr, owner)))
            { p.SkippedLocked++; return; }
            p.Changes.Add(new Change { Id = e.ObjectId, Kind = kind, Before = before, After = after, Matches = n });
        }

        private static void Cells(Transaction tr, Plan p, Table t, Func<string, string> replace, Func<int> count)
        {
            var found = new List<Change>();
            int skipped = 0;
            for (int r = 0; r < t.Rows.Count; r++)
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    var cell = t.Cells[r, c];
                    string before = cell.TextString ?? "";
                    if (before.Length == 0) continue;
                    string after = replace(before);
                    int n = count();
                    if (n == 0 || after == before) continue;
                    if (cell.IsMerged == true)
                    {
                        var range = cell.GetMergeRange();
                        if (range.TopRow != r || range.LeftColumn != c) continue;
                    }
                    if (cell.IsLinked == true || cell.IsContentEditable != true ||
                        (cell.ContentTypes & (CellContentTypes.Field | CellContentTypes.Block)) != 0)
                    { skipped++; continue; }
                    found.Add(new Change { Id = t.ObjectId, Kind = "table", Row = r, Col = c, Before = before, After = after, Matches = n });
                }
            p.SkippedFields += skipped;
            if (found.Count == 0) return;
            if (SheetDb.IsOnLockedLayer(tr, t)) { p.SkippedLocked++; return; }
            p.Changes.AddRange(found);
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            if (!p.DryRun)
            {
                foreach (var g in p.Changes.GroupBy(c => c.Id))
                {
                    var o = tr.GetObject(g.Key, OpenMode.ForWrite);
                    foreach (var c in g) Set(ctx, o, c);
                    if (o is Table t) t.GenerateLayout();
                    result.Modified.Add(OpContext.HandleOf(g.Key));
                }
            }
            var list = new JsonArray(p.Changes.Take(MaxListed).Select(c =>
            {
                var j = new JsonObject().Add("handle", OpContext.HandleOf(c.Id)).Add("kind", c.Kind);
                if (c.Row >= 0) j.Add("cell", new JsonArray(c.Row, c.Col));
                return (JsonValue)j.Add("before", c.Before).Add("after", c.After);
            }));
            result.Outputs.Add("count", p.Changes.Select(c => c.Id).Distinct().Count()).Add("matches", p.Changes.Sum(c => c.Matches))
                .Add("changes", list).Add("skippedLocked", p.SkippedLocked);
            if (p.SkippedLocked > 0) ctx.Warn(p.SkippedLocked + " objects with matches on locked layers were left unchanged.");
            return p;
        }

        private static void Set(OpContext ctx, DBObject o, Change c)
        {
            switch (o)
            {
                case Table t: t.Cells[c.Row, c.Col].TextString = c.After; break;
                case AttributeReference ar: BlockSupport.SetValue(ctx, ar, c.After); break;
                case AttributeDefinition ad when ad.IsMTextAttributeDefinition:
                {
                    using (var mt = ad.MTextAttributeDefinition)
                    {
                        mt.Contents = c.After;
                        ad.MTextAttributeDefinition = mt;
                    }
                    ad.UpdateMTextAttributeDefinition();
                    break;
                }
                case DBText t:
                    t.TextString = c.After;
                    if (t.Justify != AttachmentPoint.BaseLeft) t.AdjustAlignment(ctx.Database);
                    break;
                case MText mt: mt.Contents = c.After; break;
                case MLeader ml:
                {
                    using (var mt = ml.MText)
                    {
                        mt.Contents = c.After;
                        ml.MText = mt;
                    }
                    break;
                }
            }
        }

        private static string Read(DBObject o, Change c)
        {
            switch (o)
            {
                case Table t: return t.Cells[c.Row, c.Col].TextString ?? "";
                case AttributeReference ar: return ar.IsMTextAttribute ? MTextOf(() => ar.MTextAttribute) : ar.TextString;
                case AttributeDefinition ad when ad.IsMTextAttributeDefinition: return MTextOf(() => ad.MTextAttributeDefinition);
                case DBText t: return t.TextString;
                case MText mt: return mt.Contents;
                case MLeader ml: return MTextOf(() => ml.MText);
                default: return "";
            }
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            int bad = 0;
            string first = "";
            if (!p.DryRun)
                foreach (var c in p.Changes)
                {
                    string got = Read(tr.GetObject(c.Id, OpenMode.ForRead), c);
                    if (got == c.After) continue;
                    bad++;
                    if (first.Length == 0) first = OpContext.HandleOf(c.Id) + (c.Row >= 0 ? " cell " + c.Row + "," + c.Col : "") + " reads '" + got + "'";
                }
            result.Readback.Add("dryRun", p.DryRun).Add("changes", p.Changes.Count).Add("matching", p.DryRun ? 0 : p.Changes.Count - bad)
                .Add("skippedLocked", p.SkippedLocked).Add("skippedFields", p.SkippedFields);
            return bad == 0 || Mismatch(result, bad + " texts read back differently (first " + first + ")");
        }
    }
}
