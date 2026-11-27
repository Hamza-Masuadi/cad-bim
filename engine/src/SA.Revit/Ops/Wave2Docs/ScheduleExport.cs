using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Output;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// schedule.export: ViewSchedule.Export(folder, name, ViewScheduleExportOptions) into the op's staging folder,
    /// published after the job like every export (OutputSupport). Changes nothing, so no transaction is opened.
    ///
    /// <para>Options map one to one: FieldDelimiter (",", ";" or a tab), TextQualifier (ExportTextQualifier
    /// DoubleQuote, Quote or None), ColumnHeaders (ExportColumnHeaders None, OneRow, MultipleRows),
    /// HeadersFootersBlanks and Title. The defaults differ from Revit's (tab, multiple rows, headers and title on):
    /// a comma CSV with one header row and no title reads straight into a spreadsheet or a script. The API documents
    /// only argument and folder errors for Export; any Revit refusal becomes export_refused or export_failed through
    /// OutputSupport.Guard.</para>
    ///
    /// <para>Readback: the file exists and is not empty; its encoding is read from the byte order mark (else UTF-8,
    /// else the ANSI code page, which Revit does not document); with one_row headers and no title the first record
    /// must equal the headings of the schedule's visible fields in order, so a file that does not match the schedule is
    /// caught. Line count is reported.</para>
    /// </summary>
    public sealed class ScheduleExport : OpHandler
    {
        public override string Name => "schedule.export";
        public override bool ModifiesModel => false;

        private static readonly string[] Allowed = { "view", "drawing", "date", "jobId" };

        private sealed class Plan
        {
            public ViewSchedule Schedule = null!;
            public string Path = "";
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var v = DocsUtil.View(ctx, args.Get("schedule"), ctx.FieldPath(args, "schedule"), issues, out bool pending);
            if (pending || v is null) return null;
            if (!(v is ViewSchedule s) || v.IsTemplate)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "schedule"), RevitIssueCodes.Invalid, DocsUtil.Describe(v) + " is not a schedule");
                return null;
            }
            string ext = args.GetString("delimiter") == "tab" ? ".txt" : ".csv";
            string? path = FileTarget.Plan(ctx, args, issues, args.GetStringOrNull("fileName") ?? "{view}", Allowed, s, ext, ".csv", ".txt", ".tsv");
            if (path is null) return null;
            return new Plan { Schedule = s, Path = path };
        }

        internal static List<string> Headings(ViewSchedule s)
        {
            var def = s.Definition;
            var list = new List<string>();
            foreach (var id in def.GetFieldOrder())
            {
                var f = def.GetField(id);
                if (f is null || f.IsHidden) continue;
                list.Add(f.ColumnHeading ?? f.GetName());
            }
            return list;
        }

        private static List<List<string>> Rows(OpArgs args, byte[] bytes, out string encoding)
        {
            string text = Wave2DocsCompat.Decode(bytes, out encoding);
            string q = args.GetString("textQualifier");
            char? quote = q == "none" ? (char?)null : q == "single_quote" ? '\'' : '"';
            return Delimited.Parse(text, Delimited.Separator(args.GetString("delimiter")), quote);
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var opts = new ViewScheduleExportOptions
            {
                FieldDelimiter = Delimited.Separator(args.GetString("delimiter")).ToString(),
                TextQualifier = args.GetString("textQualifier") == "none" ? ExportTextQualifier.None : args.GetString("textQualifier") == "single_quote" ? ExportTextQualifier.Quote : ExportTextQualifier.DoubleQuote,
                ColumnHeaders = args.GetString("columnHeaders") == "none" ? ExportColumnHeaders.None : args.GetString("columnHeaders") == "multiple_rows" ? ExportColumnHeaders.MultipleRows : ExportColumnHeaders.OneRow,
                HeadersFootersBlanks = args.GetBool("groupHeadersFootersBlanks"),
                Title = args.GetBool("title"),
            };
            string stage = ctx.StageFolder(Path.GetDirectoryName(p.Path)!);
            string name = Path.GetFileName(p.Path);
            OutputSupport.Guard(ctx, "schedule export of '" + p.Schedule.Name + "'", () => { p.Schedule.Export(stage, name, opts); return true; });
            string staged = Path.Combine(stage, name);
            if (!File.Exists(staged))
                throw new OpFailedException(OutputSupport.Failed, "Revit reported no error but wrote no file " + name);
            ctx.StageFile(staged, p.Path, args.GetBool("overwrite"));
            result.Outputs.Set("file", p.Path);
            result.Outputs.Set("lines", Rows(args, File.ReadAllBytes(staged), out _).Count);
            FileTarget.Artifact(ctx, Path.GetExtension(p.Path).TrimStart('.'), p.Path, p.Schedule);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string path = result.Outputs.GetStringOrNull("file") ?? "";
            var bytes = FileTarget.ReadBack(ctx, result, path, check);
            if (bytes != null)
            {
                var rows = Rows(args, bytes, out string enc);
                result.Readback.Set("encoding", enc);
                result.Readback.Set("lines", rows.Count);
                result.Outputs.Set("lines", rows.Count);
                var issues = new List<Issue>();
                var v = DocsUtil.View(ctx, args.Get("schedule"), ctx.FieldPath(args, "schedule"), issues, out _) as ViewSchedule;
                if (v != null && v.Definition.ShowHeaders && args.GetString("columnHeaders") == "one_row" && !args.GetBool("title") && rows.Count > 0)
                {
                    var want = Headings(v);
                    var got = rows[0].Select(x => x.Trim()).ToList();
                    result.Readback.Set("headings", SA.Core.Json.JsonArray.FromStrings(got));
                    if (!got.SequenceEqual(want.Select(x => x.Trim()), StringComparer.Ordinal))
                        check.Fail("the first line of " + path + " is [" + string.Join(" | ", got) + "]; the schedule's visible headings are [" + string.Join(" | ", want) + "]");
                }
            }
            check.Apply(result);
        }
    }
}
