using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Validation;

namespace SA.Cli
{
    /// <summary>The sa command line. All output is JSON on stdout except "sa ops" without --json and usage text.</summary>
    public sealed class CliApp
    {
        private readonly TextWriter _out;
        private readonly TextWriter _err;
        private readonly ISessionStore _sessions;
        private readonly EngineClient _client;
        private readonly OpRegistry _registry;

        public CliApp(TextWriter stdout, TextWriter stderr, ISessionStore sessions, IEngineConnector connector, OpRegistry? registry = null)
        {
            _out = stdout;
            _err = stderr;
            _sessions = sessions;
            _client = new EngineClient(connector);
            _registry = registry ?? OpRegistry.Default;
        }

        public const string Usage =
@"sa sessions                              List running engines
sa <acad|revit> ping
sa <acad|revit> snapshot [--sections a,b] [--out file.json]
sa <acad|revit> validate job.json [--allow-script]
sa <acad|revit> run job.json [--rehearse] [--allow-script] [--timeout 600]
                 Revit only: [--fail-on-warnings] [--start-timeout <seconds>]
sa <acad|revit> status <jobId>
sa <acad|revit> audit [--checks a,b | --group g] [--standard file.json]
                 [--checks-file path [--checks-mode replace|add]] [--out file.json]
sa <acad|revit> capture --target ... --out file.png [--format png|pdf]
sa <acad|revit> probe Type.Member ...
sa job lint job.json                     Offline schema validation, no app needed
sa standard lint file.json               Load a project standard as the engines do, no app needed
sa checks lint file.json                 Load a checks file as the engines do, no app needed
sa ops [--app autocad|revit] [--json]    List op schemas
Common options: --pid <id> picks a session when several run; --timeout <seconds>.
Exit codes: 0 ok, 1 failed, 2 usage, 3 no or ambiguous session, 4 connection, 5 timeout.";

        private sealed class Args
        {
            public readonly List<string> Positional = new List<string>();
            public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> Flags = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly HashSet<string> FlagNames = new HashSet<string> { "--rehearse", "--allow-script", "--json", "--help", "--fail-on-warnings" };
        private static readonly HashSet<string> OptionNames = new HashSet<string> { "--pid", "--timeout", "--out", "--sections", "--checks", "--group", "--target", "--format", "--app",
            "--standard", "--checks-file", "--checks-mode", "--start-timeout" };

        private static Args? ParseArgs(string[] argv, int start, out string error)
        {
            error = "";
            var a = new Args();
            for (int i = start; i < argv.Length; i++)
            {
                string s = argv[i];
                if (FlagNames.Contains(s)) { a.Flags.Add(s); continue; }
                if (OptionNames.Contains(s))
                {
                    if (i + 1 >= argv.Length) { error = s + " needs a value"; return null; }
                    a.Options[s] = argv[++i];
                    continue;
                }
                if (s.StartsWith("--", StringComparison.Ordinal)) { error = "unknown option " + s; return null; }
                a.Positional.Add(s);
            }
            return a;
        }

        public int Run(string[] argv)
        {
            try
            {
                return RunInner(argv);
            }
            catch (Exception e)
            {
                return Fail(ExitCodes.Failed, ErrorCodes.Internal, e.GetType().Name + ": " + e.Message);
            }
        }

        private int RunInner(string[] argv)
        {
            if (argv.Length == 0 || argv[0] == "--help" || argv[0] == "help" || argv[0] == "-h")
            {
                _err.WriteLine(Usage);
                return argv.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
            }
            var args = ParseArgs(argv, 1, out string perr);
            if (args is null) return UsageError(perr);

            switch (argv[0])
            {
                case "sessions":
                    return Sessions();
                case "job":
                    if (args.Positional.Count == 2 && args.Positional[0] == "lint") return Lint(args.Positional[1]);
                    return UsageError("expected: sa job lint job.json");
                case "standard":
                case "checks":
                    if (args.Positional.Count == 2 && args.Positional[0] == "lint") return LintData(argv[0], args.Positional[1]);
                    return UsageError("expected: sa " + argv[0] + " lint file.json");
                case "ops":
                    return Ops(args);
                case EngineApps.AutoCad:
                case EngineApps.Revit:
                    return Engine(argv[0], args);
                default:
                    return UsageError("unknown command '" + argv[0] + "'");
            }
        }

        private int UsageError(string message)
        {
            _err.WriteLine("sa: " + message);
            _err.WriteLine(Usage);
            return ExitCodes.Usage;
        }

        private void Print(JsonValue v) => _out.WriteLine(v.ToJson(true));

        private int Fail(int exit, string code, string message, JsonObject? extra = null)
        {
            var e = new JsonObject().Add("code", code).Add("message", message);
            var o = new JsonObject().Add("ok", false).Add("error", e);
            if (extra != null) foreach (var kv in extra) o.Set(kv.Key, kv.Value);
            Print(o);
            return exit;
        }

        private int Sessions()
        {
            var arr = new JsonArray(_sessions.List().Select(s => (JsonValue)s.ToJson()));
            Print(new JsonObject().Add("ok", true).Add("sessions", arr));
            return ExitCodes.Ok;
        }

        private int LintData(string kind, string file)
        {
            if (!File.Exists(file)) return Fail(ExitCodes.Usage, ErrorCodes.BadRequest, "file not found: " + file);
            var o = new JsonObject();
            try
            {
                if (kind == "standard") o.Add("name", SA.Core.Standards.StandardLoader.LoadFile(file).Name);
                else o.Add("checks", SA.Core.Checks.CheckCatalog.LoadFile(file).Checks.Count);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return Fail(ExitCodes.Failed, ErrorCodes.BadRequest, file + ": " + e.Message);
            }
            Print(new JsonObject().Add("ok", true).Add("file", Path.GetFullPath(file)).Add(kind, o));
            return ExitCodes.Ok;
        }

        private int Lint(string file)
        {
            if (!File.Exists(file)) return Fail(ExitCodes.Usage, ErrorCodes.BadRequest, "file not found: " + file);
            var v = JobValidator.Validate(File.ReadAllText(file, Encoding.UTF8), _registry);
            var o = new JsonObject().Add("ok", v.Ok);
            if (v.Job != null)
            {
                o.Add("jobId", v.Job.JobId).Add("app", AppKinds.ToWire(v.Job.App)).Add("ops", v.Job.Ops.Count)
                 .Add("lengthUnit", SA.Core.Units.Length.Symbol(v.Job.LengthUnit));
            }
            o.Add("errors", v.Issues.Count(i => i.IsError)).Add("warnings", v.Issues.Count(i => !i.IsError));
            o.Add("issues", Issue.ToJson(v.Issues));
            Print(o);
            return v.Ok ? ExitCodes.Ok : ExitCodes.Failed;
        }

        private int Ops(Args args)
        {
            AppKind? app = null;
            if (args.Options.TryGetValue("--app", out var appText))
            {
                string wire = EngineApps.IsKnown(appText) ? EngineApps.ToJobApp(appText) : appText;
                if (!AppKinds.TryParse(wire, out var a)) return UsageError("--app must be autocad or revit");
                app = a;
            }
            if (args.Flags.Contains("--json"))
            {
                Print(new JsonObject().Add("ok", true).Add("ops", _registry.ToJson(app)));
                return ExitCodes.Ok;
            }
            foreach (var s in _registry.All.Where(x => app is null || x.App == app))
            {
                _out.WriteLine(s.Name + "  [" + AppKinds.ToWire(s.App) + "]" + (s.Destructive ? " destructive" : "") + (s.Ensure ? " ensure" : ""));
                _out.WriteLine("  " + s.Summary);
                WriteFields(s.Fields, s.Constraints, "    ");
            }
            return ExitCodes.Ok;
        }

        private void WriteFields(IReadOnlyList<FieldSpec> fields, IReadOnlyList<FieldConstraint> constraints, string indent)
        {
            foreach (var f in fields)
            {
                _out.WriteLine(indent + f.Name + (f.Required ? "*" : "") + " : " + f.TypeLabel() + (f.Default != null ? " = " + f.Default.ToJson() : ""));
                var nested = f.Type == FieldType.Object ? f : f.Item != null && f.Item.Type == FieldType.Object ? f.Item : null;
                if (nested != null) WriteFields(nested.Fields, nested.Constraints, indent + "  ");
            }
            foreach (var c in constraints) _out.WriteLine(indent + "(" + c.Describe() + ")");
        }

        private int Engine(string app, Args args)
        {
            if (args.Positional.Count == 0) return UsageError("missing method after '" + app + "'");
            string method = args.Positional[0];
            if (!Methods.IsKnown(method)) return UsageError("unknown method '" + method + "'");

            int timeoutSeconds = method == Methods.Run ? 600 : method == Methods.Capture || method == Methods.Audit || method == Methods.Snapshot ? 300 : 60;
            if (args.Options.TryGetValue("--timeout", out var ts))
            {
                if (!int.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out timeoutSeconds) || timeoutSeconds < 1)
                    return UsageError("--timeout must be a positive number of seconds");
            }

            var p = new JsonObject();
            string? jobId = null;
            bool rehearse = args.Flags.Contains("--rehearse");
            switch (method)
            {
                case Methods.Ping:
                    break;
                case Methods.Snapshot:
                    if (args.Options.TryGetValue("--sections", out var sec)) p.Add("sections", JsonArray.FromStrings(SplitList(sec)));
                    break;
                case Methods.Validate:
                case Methods.Run:
                {
                    if (args.Positional.Count != 2) return UsageError("expected: sa " + app + " " + method + " job.json");
                    string file = args.Positional[1];
                    if (!File.Exists(file)) return Fail(ExitCodes.Usage, ErrorCodes.BadRequest, "file not found: " + file);
                    var lint = JobValidator.Validate(File.ReadAllText(file, Encoding.UTF8), _registry);
                    if (!lint.Ok || lint.Job is null)
                        return Fail(ExitCodes.Failed, ErrorCodes.SchemaInvalid, "the job failed offline validation; nothing was sent",
                            new JsonObject().Add("issues", Issue.ToJson(lint.Issues)));
                    string jobApp = EngineApps.ToJobApp(app);
                    if (AppKinds.ToWire(lint.Job.App) != jobApp)
                        return Fail(ExitCodes.Failed, ErrorCodes.SchemaInvalid, "the job is for " + AppKinds.ToWire(lint.Job.App) + " but was sent to " + app);
                    jobId = lint.Job.JobId;
                    p.Add("job", lint.Job.ToJson());
                    if (method == Methods.Run) p.Add("mode", rehearse ? "rehearse" : "commit");
                    if (args.Flags.Contains("--allow-script")) p.Add("allowScript", true);
                    if (args.Flags.Contains("--fail-on-warnings")) p.Add("failOnWarnings", true);
                    if (args.Options.TryGetValue("--start-timeout", out var st))
                    {
                        if (!int.TryParse(st, NumberStyles.None, CultureInfo.InvariantCulture, out int startSeconds) || startSeconds < 1)
                            return UsageError("--start-timeout must be a positive number of seconds");
                        p.Add("startTimeoutSeconds", startSeconds);
                    }
                    break;
                }
                case Methods.Status:
                    if (args.Positional.Count != 2) return UsageError("expected: sa " + app + " status <jobId>");
                    jobId = args.Positional[1];
                    p.Add("jobId", jobId);
                    break;
                case Methods.Audit:
                    if (args.Options.TryGetValue("--checks", out var ch)) p.Add("checks", JsonArray.FromStrings(SplitList(ch)));
                    if (args.Options.TryGetValue("--group", out var g)) p.Add("group", g);
                    if (p.ContainsKey("checks") && p.ContainsKey("group")) return UsageError("give --checks or --group, not both");
                    if (args.Options.TryGetValue("--standard", out var std)) p.Add("standard", Path.GetFullPath(std));
                    if (args.Options.TryGetValue("--checks-file", out var cf)) p.Add("checksFile", Path.GetFullPath(cf));
                    if (args.Options.TryGetValue("--checks-mode", out var cm))
                    {
                        if (cm != "replace" && cm != "add") return UsageError("--checks-mode must be replace or add");
                        if (!p.ContainsKey("checksFile")) return UsageError("--checks-mode needs --checks-file");
                        p.Add("checksMode", cm);
                    }
                    break;
                case Methods.Capture:
                    if (!args.Options.TryGetValue("--target", out var target)) return UsageError("capture needs --target");
                    if (!args.Options.TryGetValue("--out", out var capOut)) return UsageError("capture needs --out");
                    p.Add("target", target).Add("out", Path.GetFullPath(capOut));
                    if (args.Options.TryGetValue("--format", out var fmt)) p.Add("format", fmt);
                    break;
                case Methods.Probe:
                    if (args.Positional.Count < 2) return UsageError("probe needs at least one Type or Type.Member");
                    p.Add("names", JsonArray.FromStrings(args.Positional.Skip(1)));
                    break;
            }

            var session = PickSession(app, args, out int exit);
            if (session is null) return exit;

            var request = Request.Create(method, p);
            var result = _client.Send(session, request, TimeSpan.FromSeconds(timeoutSeconds));

            if (result.TimedOut)
            {
                var extra = new JsonObject().Add("requestId", request.Id);
                string msg = "no answer within " + timeoutSeconds + " s.";
                if (method == Methods.Run && jobId != null)
                {
                    extra.Add("jobId", jobId).Add("next", "sa " + app + " status " + jobId);
                    msg += " The job may still be running or may have finished. Do not send it again; ask with: sa " + app + " status " + jobId;
                }
                return Fail(ExitCodes.Timeout, ErrorCodes.Timeout, msg, extra);
            }
            if (result.TransportError != null || result.Response is null)
            {
                var extra = new JsonObject().Add("pipe", session.PipeName);
                string msg = "connection to the engine failed: " + (result.TransportError ?? "no response");
                if (method == Methods.Run && jobId != null)
                {
                    extra.Add("jobId", jobId).Add("next", "sa " + app + " status " + jobId);
                    msg += ". If the request was delivered the job may have run; check with status before sending it again.";
                }
                return Fail(ExitCodes.Transport, ErrorCodes.Internal, msg, extra);
            }

            var resp = result.Response;
            string? outFile = method == Methods.Snapshot || method == Methods.Audit ? Opt(args, "--out") : null;
            if (outFile != null && resp.Ok)
            {
                File.WriteAllText(outFile, (resp.Result ?? new JsonObject()).ToJson(true), new UTF8Encoding(false));
                Print(new JsonObject().Add("ok", true).Add("out", Path.GetFullPath(outFile)));
            }
            else Print(resp.ToJson());

            if (!resp.Ok) return ExitCodes.Failed;
            if (method == Methods.Run)
            {
                var report = resp.Result as JsonObject;
                string? status = report?.GetStringOrNull("status");
                // A rehearsal succeeds only as a clean rollback: rolled_back with no error.
                bool good = rehearse ? status == "rolled_back" && report?["error"] is null : status == "committed";
                return good ? ExitCodes.Ok : ExitCodes.Failed;
            }
            return ExitCodes.Ok;
        }

        private static string? Opt(Args a, string name) => a.Options.TryGetValue(name, out var v) ? v : null;

        private static IEnumerable<string> SplitList(string s) =>
            s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);

        private SessionInfo? PickSession(string app, Args args, out int exit)
        {
            exit = ExitCodes.Ok;
            var all = _sessions.List().Where(s => s.App == app).ToList();
            if (args.Options.TryGetValue("--pid", out var pidText))
            {
                if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    exit = UsageError("--pid must be a process id");
                    return null;
                }
                var s = all.FirstOrDefault(x => x.ProcessId == pid);
                if (s is null) exit = Fail(ExitCodes.Session, ErrorCodes.BadRequest, "no running " + app + " engine with process id " + pid,
                    new JsonObject().Add("sessions", new JsonArray(all.Select(x => (JsonValue)x.ToJson()))));
                return s;
            }
            if (all.Count == 1) return all[0];
            if (all.Count == 0)
            {
                exit = Fail(ExitCodes.Session, ErrorCodes.BadRequest, "no running " + app + " engine found (is the app open with the engine loaded?)");
                return null;
            }
            exit = Fail(ExitCodes.Session, ErrorCodes.BadRequest, all.Count + " " + app + " engines are running; choose one with --pid",
                new JsonObject().Add("sessions", new JsonArray(all.Select(x => (JsonValue)x.ToJson()))));
            return null;
        }
    }
}
