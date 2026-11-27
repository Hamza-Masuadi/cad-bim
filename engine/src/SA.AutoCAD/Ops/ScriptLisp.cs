using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using LispDataType = Autodesk.AutoCAD.Runtime.LispDataType;
using SA.AutoCAD.Compat;
using SA.AutoCAD.Host;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// script.lisp, the escape hatch. Honest semantics:
    /// - Refused unless the run request sets allowScript, unless it is the only op in its job, and when the job has
    ///   verify.checks (a failing check could not roll the script's changes back). verify.capture runs after the commit
    ///   and is allowed.
    /// - Rehearse does NOT evaluate the code. AutoLISP changes the drawing through its own object opens, outside the job
    ///   transaction, so aborting the transaction would not undo them; a rehearsal that ran the code would change the
    ///   drawing. Rehearsal records the job hash and document fingerprint instead.
    /// - Commit is refused unless this exact job (same hash) was rehearsed with allowScript against the current
    ///   fingerprint (checked by the job runner).
    /// - On commit the code runs inside the job's document lock, so its changes belong to the job's single undo step:
    ///   one U removes them. They are NOT rolled back if something fails after the code has run; the job then reports
    ///   failed with error.details.scriptChangesRemain true (never rolled_back); use U to remove them.
    /// - Context: the engine runs from Application.Idle, in the application context, holding the document's write lock.
    ///   The code is evaluated with no transaction open (the runner starts the job transaction afterwards), and only when
    ///   the editor is quiescent and no command is in progress, checked again immediately before the call. Whether
    ///   acedEvaluateLisp is safe in the application context is not documented: it is the first thing to test in the app.
    ///   A native access violation inside the evaluation cannot be caught and would end AutoCAD.
    /// - timeoutSeconds cannot be enforced: evaluation runs on AutoCAD's main thread and cannot be interrupted.
    /// - Best-effort lint, not a guarantee: code that visibly prompts (get*, entsel, grread), runs commands (command,
    ///   vl-cmdf, a quoted 'command passed to apply or mapcar) or queues them (SendCommand) is refused at validation;
    ///   eval and read are warned about because they can build such calls at run time.
    /// </summary>
    internal sealed class ScriptLisp : OpHandler<ScriptLisp.ScriptPlan>
    {
        public override string Name => "script.lisp";

        internal sealed class ScriptPlan
        {
            public bool Evaluated;
            public JsonValue Value = JsonValue.Null;
        }

        private static readonly Regex Prompting = new Regex(
            @"\(\s*(getpoint|getcorner|getdist|getangle|getorient|getint|getreal|getstring|getkword|getfiled|entsel|nentsel|nentselp|grread|command|command-s|vl-cmdf|initdia|vla-sendcommand)\b" +
            @"|'\s*(command|command-s|vl-cmdf|vla-sendcommand)\b|\(\s*function\s+(command|command-s|vl-cmdf)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>SendCommand by name (vlax-invoke obj "SendCommand" ...), also inside strings.</summary>
        private static readonly Regex SendCommand = new Regex(@"sendcommand|postcommand", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Dynamic = new Regex(@"\(\s*(eval|read)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            if (!ctx.AllowScript)
                OpContext.Error(issues, args, null, IssueCodes.ScriptNotAllowed, "script.lisp runs only when the run request sets allowScript (sa ... run --allow-script)");
            if (ctx.Job.Ops.Count != 1)
                OpContext.Error(issues, args, null, IssueCodes.ScriptNotAlone, "script.lisp must be the only op in its job: its changes cannot be rolled back if another op fails");
            string code = args.GetString("code");
            string? balance = CheckBalance(code);
            if (balance != null) OpContext.Error(issues, args, "code", IssueCodes.LispSyntax, balance);
            if (ctx.Job.Verify.Checks.Count > 0)
                OpContext.Error(issues, args, null, "script_with_checks", "a job with script.lisp cannot carry verify.checks: a failing check could not roll the script's changes back. " +
                    "Run the checks in a later job (or with audit)");
            string bare = StripStringsAndComments(code);
            var m = Prompting.Match(bare);
            if (m.Success)
                OpContext.Error(issues, args, "code", IssueCodes.LispInteractive, "'" + m.Value.Trim() + "' prompts for input or runs a command; neither can be answered from a job");
            else if (SendCommand.IsMatch(code))
                OpContext.Error(issues, args, "code", IssueCodes.LispInteractive, "the code names SendCommand or PostCommand: a queued command runs after the job, outside its lock and undo step");
            if (Dynamic.IsMatch(bare))
                OpContext.Warning(issues, args, "code", "lisp_dynamic", "eval and read can build command or prompt calls at run time, which the validation cannot see; the code must not do that");
            if (Regex.IsMatch(StripStringsAndComments(code), @"\(\s*ssget\b", RegexOptions.IgnoreCase))
                OpContext.Warning(issues, args, "code", IssueCodes.LispSsget, "ssget must use a non-interactive mode such as \"_X\"; an interactive selection would wait forever");
        }

        protected override ScriptPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var plan = new ScriptPlan();
            string purpose = args.GetString("purpose");
            if (ctx.Mode == RunMode.Rehearse)
            {
                ctx.Warn("script.lisp was not evaluated: a rehearsal cannot roll back AutoLISP changes. Purpose recorded: " + purpose);
                return plan;
            }
            var doc = ctx.Document;
            if (!doc.Editor.IsQuiescent || !string.IsNullOrEmpty(doc.CommandInProgress))
                throw new EngineException(ErrorCodes.Busy, "AutoCAD is not idle (" + (string.IsNullOrEmpty(doc.CommandInProgress) ? "the editor is busy" : "command " + doc.CommandInProgress) +
                    "); script.lisp was not evaluated and nothing changed.");
            if (ctx.Database.TransactionManager.NumberOfActiveTransactions != 0)
                throw new InvalidOperationException("A transaction is open; script.lisp is evaluated only with none open. Nothing was evaluated.");
            ResultBuffer? rb;
            ctx.ScriptEvaluated = true;
            try
            {
                rb = LispBridge.Evaluate(args.GetString("code"));
            }
            catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
            {
                ctx.ScriptEvaluated = false; // the export could not be bound, so no code ran
                throw new EngineException(ErrorCodes.UnsupportedInRelease, "AutoLISP evaluation is not available in this AutoCAD release: " + e.Message);
            }
            using (rb)
            {
                plan.Value = rb is null ? JsonValue.Null : ToJson(rb);
            }
            plan.Evaluated = true;
            ctx.Warn("script.lisp ran (" + purpose + "). Its changes are part of this job's undo step and were made outside the job transaction.");
            result.Outputs.Add("value", plan.Value);
            return plan;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, ScriptPlan plan)
        {
            result.Readback.Add("evaluated", plan.Evaluated).Add("purpose", args.GetString("purpose")).Add("value", plan.Value);
            // Nothing else can be read back: the engine does not know what the code changed.
            return true;
        }

        private static JsonValue ToJson(ResultBuffer rb)
        {
            var stack = new Stack<JsonArray>();
            var root = new JsonArray();
            stack.Push(root);
            foreach (TypedValue tv in rb)
            {
                var top = stack.Peek();
                switch ((LispDataType)tv.TypeCode)
                {
                    case LispDataType.ListBegin:
                        var list = new JsonArray();
                        top.Add(list);
                        stack.Push(list);
                        break;
                    case LispDataType.ListEnd:
                    case LispDataType.DottedPair:
                        if (stack.Count > 1) stack.Pop();
                        break;
                    case LispDataType.Nil:
                    case LispDataType.None:
                    case LispDataType.Void:
                        top.Add(JsonValue.Null);
                        break;
                    case LispDataType.T_atom:
                        top.Add(true);
                        break;
                    case LispDataType.Int16:
                    case LispDataType.Int32:
                        top.Add(Convert.ToInt64(tv.Value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case LispDataType.Double:
                    case LispDataType.Angle:
                    case LispDataType.Orientation:
                        top.Add(Convert.ToDouble(tv.Value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case LispDataType.Text:
                        top.Add(tv.Value as string ?? "");
                        break;
                    case LispDataType.Point2d:
                        var p2 = (Autodesk.AutoCAD.Geometry.Point2d)tv.Value;
                        top.Add(new JsonArray(p2.X, p2.Y));
                        break;
                    case LispDataType.Point3d:
                        var p3 = (Autodesk.AutoCAD.Geometry.Point3d)tv.Value;
                        top.Add(new JsonArray(p3.X, p3.Y, p3.Z));
                        break;
                    case LispDataType.ObjectId:
                        top.Add(new JsonObject().Add("handle", ((ObjectId)tv.Value).Handle.ToString()));
                        break;
                    default:
                        top.Add(Convert.ToString(tv.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                        break;
                }
            }
            return root.Count == 1 ? root[0] : root;
        }

        private static string StripStringsAndComments(string code)
        {
            var sb = new System.Text.StringBuilder(code.Length);
            bool inString = false;
            for (int i = 0; i < code.Length; i++)
            {
                char ch = code[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; sb.Append(' '); continue; }
                if (ch == ';')
                {
                    while (i < code.Length && code[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string? CheckBalance(string code)
        {
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < code.Length; i++)
            {
                char ch = code[i];
                if (inString)
                {
                    if (ch == '\\') { i++; continue; }
                    if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') inString = true;
                else if (ch == ';') { while (i < code.Length && code[i] != '\n') i++; }
                else if (ch == '(') depth++;
                else if (ch == ')' && --depth < 0) return "unbalanced ')' at character " + (i + 1);
            }
            if (inString) return "unterminated string";
            if (depth > 0) return depth + " unclosed '('";
            return null;
        }
    }
}
