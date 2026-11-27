using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SA.Cli;
using SA.Core.Json;
using SA.Core.Protocol;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>
    /// An in-memory duplex stream standing in for the pipe. Bytes the client writes are collected; once a whole frame has
    /// arrived the handler answers it. A handler returning null never answers (a hung engine).
    /// </summary>
    internal sealed class FakeEngineStream : Stream
    {
        private readonly Func<JsonValue, JsonValue?> _handler;
        private readonly ManualResetEventSlim _release;
        private readonly MemoryStream _in = new MemoryStream();
        private MemoryStream? _out;

        public FakeEngineStream(Func<JsonValue, JsonValue?> handler, ManualResetEventSlim release)
        {
            _handler = handler;
            _release = release;
        }

        public List<JsonValue> Received { get; } = new List<JsonValue>();

        public override void Write(byte[] buffer, int offset, int count) => _in.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_out is null)
            {
                _in.Position = 0;
                var req = Framing.ReadJson(_in) ?? throw new IOException("no request");
                Received.Add(req);
                var resp = _handler(req);
                if (resp is null)
                {
                    _release.Wait();
                    return 0;
                }
                _out = new MemoryStream();
                Framing.WriteJson(_out, resp);
                _out.Position = 0;
            }
            return _out.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal sealed class FakeConnector : IEngineConnector
    {
        private readonly Func<JsonValue, JsonValue?> _handler;
        public readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
        public int Connects;
        public bool Fail;
        public List<FakeEngineStream> Streams { get; } = new List<FakeEngineStream>();

        public FakeConnector(Func<JsonValue, JsonValue?> handler) { _handler = handler; }

        public Stream Connect(SessionInfo session, int timeoutMs)
        {
            Connects++;
            if (Fail) throw new TimeoutException("pipe not found");
            var s = new FakeEngineStream(_handler, Release);
            Streams.Add(s);
            return s;
        }
    }

    internal sealed class FakeSessions : ISessionStore
    {
        public List<SessionInfo> Items = new List<SessionInfo>();
        public List<SessionInfo> List() => Items;
        public static SessionInfo Acad(int pid) => new SessionInfo("acad", pid, EngineApps.PipeName("acad", pid), 2026, "25.1", DateTimeOffset.UtcNow);
    }

    public class CliTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sa-cli-tests-" + Guid.NewGuid().ToString("N"));

        public CliTests() { Directory.CreateDirectory(_dir); }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private string File(string name, string content)
        {
            string p = Path.Combine(_dir, name);
            System.IO.File.WriteAllText(p, content);
            return p;
        }

        private static (int code, JsonObject? json, string text) Run(FakeConnector conn, FakeSessions sessions, params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = new CliApp(o, e, sessions, conn).Run(args);
            string text = o.ToString();
            JsonObject? json = null;
            try { json = JsonValue.Parse(text) as JsonObject; } catch (JsonParseException) { }
            return (code, json, text + e);
        }

        private static JsonValue Report(JsonValue req, string status) =>
            Response.Success(req.AsObject().GetStringOrNull("id")!, new JsonObject().Add("jobId", "j").Add("status", status)).ToJson();

        [Fact]
        public void LintOfflineValidAndInvalid()
        {
            var conn = new FakeConnector(_ => null);
            var good = File("good.json", JobTests.GoodAcadJob);
            var r = Run(conn, new FakeSessions(), "job", "lint", good);
            Assert.Equal(ExitCodes.Ok, r.code);
            Assert.True(r.json!["ok"]!.AsBool());
            Assert.Equal(4, r.json["ops"]!.AsInt64());

            var bad = File("bad.json", JobTests.GoodAcadJob.Replace("\"mitre\"", "\"x\"").Replace("\"kind\": \"door\"", "\"kind\": \"gate\""));
            r = Run(conn, new FakeSessions(), "job", "lint", bad);
            Assert.Equal(ExitCodes.Failed, r.code);
            Assert.Equal(1, r.json!["errors"]!.AsInt64());
            Assert.Equal("$.ops[2].args.kind", r.json["issues"]!.AsArray()[0].AsObject().GetStringOrNull("path"));
            Assert.Equal(0, conn.Connects);

            Assert.Equal(ExitCodes.Usage, Run(conn, new FakeSessions(), "job", "lint", Path.Combine(_dir, "missing.json")).code);
        }

        [Fact]
        public void LintStandardsAndChecksOffline()
        {
            var conn = new FakeConnector(_ => null);
            var r = Run(conn, new FakeSessions(), "standard", "lint", RealData.StandardPath("us-ncs-style.json"));
            Assert.Equal(ExitCodes.Ok, r.code);
            r = Run(conn, new FakeSessions(), "checks", "lint", RealData.ChecksPath("revit.json"));
            Assert.Equal(ExitCodes.Ok, r.code);
            Assert.True(r.json!["checks"]!.AsObject()["checks"]!.AsInt64() > 50);
            var bad = File("bad-standard.json", "{ \"name\": 3 }");
            Assert.Equal(ExitCodes.Failed, Run(conn, new FakeSessions(), "standard", "lint", bad).code);
            Assert.Equal(ExitCodes.Usage, Run(conn, new FakeSessions(), "checks", "lint").code);
            Assert.Equal(0, conn.Connects);
        }

        [Fact]
        public void OpsListingTextAndJson()
        {
            var conn = new FakeConnector(_ => null);
            var r = Run(conn, new FakeSessions(), "ops", "--app", "revit", "--json");
            Assert.Equal(ExitCodes.Ok, r.code);
            var ops = r.json!["ops"]!.AsArray();
            Assert.All(ops, o => Assert.Equal("revit", o.AsObject().GetStringOrNull("app")));
            Assert.Contains(ops, o => o.AsObject().GetStringOrNull("name") == "wall.create");
            var text = Run(conn, new FakeSessions(), "ops", "--app", "acad");
            Assert.Contains("wall.draw  [autocad]", text.text);
            Assert.DoesNotContain("wall.create", text.text);
            Assert.Equal(ExitCodes.Usage, Run(conn, new FakeSessions(), "ops", "--app", "max").code);
        }

        [Fact]
        public void SessionSelection()
        {
            var conn = new FakeConnector(req => Response.Success(req.AsObject().GetStringOrNull("id")!, new JsonObject().Add("busy", false)).ToJson());
            var none = new FakeSessions();
            Assert.Equal(ExitCodes.Session, Run(conn, none, "acad", "ping").code);
            var two = new FakeSessions { Items = { FakeSessions.Acad(10), FakeSessions.Acad(20) } };
            var r = Run(conn, two, "acad", "ping");
            Assert.Equal(ExitCodes.Session, r.code);
            Assert.Equal(2, r.json!["sessions"]!.AsArray().Count);
            Assert.Equal(0, conn.Connects);
            Assert.Equal(ExitCodes.Ok, Run(conn, two, "acad", "ping", "--pid", "20").code);
            Assert.Equal(ExitCodes.Session, Run(conn, two, "acad", "ping", "--pid", "30").code);
            Assert.Equal(ExitCodes.Session, Run(conn, two, "revit", "ping").code);
            var s = Run(conn, two, "sessions");
            Assert.Equal(2, s.json!["sessions"]!.AsArray().Count);
        }

        [Fact]
        public void AuditAndRunPassStandardChecksAndRevitOptions()
        {
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var ok = new FakeConnector(req => Response.Success(req.AsObject().GetStringOrNull("id")!, new JsonObject()).ToJson());
            Assert.Equal(ExitCodes.Ok, Run(ok, sessions, "acad", "audit", "--standard", "std.json", "--checks-file", "mine.json", "--checks-mode", "add").code);
            var p = ok.Streams.Single().Received.Single().AsObject()["params"]!.AsObject();
            Assert.Equal(Path.GetFullPath("std.json"), p.GetStringOrNull("standard"));
            Assert.Equal(Path.GetFullPath("mine.json"), p.GetStringOrNull("checksFile"));
            Assert.Equal("add", p.GetStringOrNull("checksMode"));

            var none = new FakeConnector(_ => null);
            Assert.Equal(ExitCodes.Usage, Run(none, sessions, "acad", "audit", "--checks-mode", "add").code);
            Assert.Equal(ExitCodes.Usage, Run(none, sessions, "acad", "audit", "--checks-file", "c.json", "--checks-mode", "merge").code);
            var job = File("job.json", JobTests.GoodAcadJob);
            Assert.Equal(ExitCodes.Usage, Run(none, sessions, "acad", "run", job, "--start-timeout", "0").code);
            Assert.Equal(0, none.Connects);

            var run = new FakeConnector(req => Report(req, "committed"));
            Assert.Equal(ExitCodes.Ok, Run(run, sessions, "acad", "run", job, "--fail-on-warnings", "--start-timeout", "120").code);
            var rp = run.Streams.Single().Received.Single().AsObject()["params"]!.AsObject();
            Assert.True(rp["failOnWarnings"]!.AsBool());
            Assert.Equal(120, rp["startTimeoutSeconds"]!.AsInt64());
        }

        [Fact]
        public void RunSendsOnceAndExitCodeFollowsStatus()
        {
            var job = File("job.json", JobTests.GoodAcadJob);
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var commit = new FakeConnector(req => Report(req, "committed"));
            Assert.Equal(ExitCodes.Ok, Run(commit, sessions, "acad", "run", job).code);
            var sent = commit.Streams.Single().Received.Single().AsObject();
            Assert.Equal("run", sent.GetStringOrNull("method"));
            Assert.Equal("commit", sent["params"]!.AsObject().GetStringOrNull("mode"));
            Assert.Equal("2026-11-27T10-15-00Z-walls-01", sent["params"]!.AsObject()["job"]!.AsObject().GetStringOrNull("jobId"));

            var rehearse = new FakeConnector(req => Report(req, "rolled_back"));
            Assert.Equal(ExitCodes.Ok, Run(rehearse, sessions, "acad", "run", job, "--rehearse").code);
            Assert.Equal("rehearse", rehearse.Streams.Single().Received.Single().AsObject()["params"]!.AsObject().GetStringOrNull("mode"));
            Assert.Equal(ExitCodes.Failed, Run(rehearse, sessions, "acad", "run", job).code);

            var failed = new FakeConnector(req => Response.Failure(req.AsObject().GetStringOrNull("id")!, ErrorCodes.WrongDocument, "other").ToJson());
            var f = Run(failed, sessions, "acad", "run", job);
            Assert.Equal(ExitCodes.Failed, f.code);
            Assert.Equal("wrong_document", f.json!["error"]!.AsObject().GetStringOrNull("code"));
        }

        [Fact]
        public void InvalidOrWrongAppJobIsNeverSent()
        {
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var conn = new FakeConnector(req => Report(req, "committed"));
            var bad = File("bad.json", JobTests.GoodAcadJob.Replace("\"wall.draw\"", "\"wall.drawx\""));
            var r = Run(conn, sessions, "acad", "run", bad);
            Assert.Equal(ExitCodes.Failed, r.code);
            Assert.Equal("schema_invalid", r.json!["error"]!.AsObject().GetStringOrNull("code"));
            Assert.Equal(ExitCodes.Failed, Run(conn, new FakeSessions { Items = { new SessionInfo("revit", 3, "sa-revit-3", 2026, "", DateTimeOffset.UtcNow) } }, "revit", "run", File("ok.json", JobTests.GoodAcadJob)).code);
            Assert.Equal(0, conn.Connects);
        }

        [Fact]
        public void TimeoutNeverResendsAndPointsToStatus()
        {
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var hung = new FakeConnector(_ => null);
            try
            {
                var r = Run(hung, sessions, "acad", "run", File("job.json", JobTests.GoodAcadJob), "--timeout", "1");
                Assert.Equal(ExitCodes.Timeout, r.code);
                Assert.Equal("timeout", r.json!["error"]!.AsObject().GetStringOrNull("code"));
                Assert.Equal("sa acad status 2026-11-27T10-15-00Z-walls-01", r.json.GetStringOrNull("next"));
                Assert.Contains("Do not send it again", r.json["error"]!.AsObject().GetStringOrNull("message"));
                Assert.Equal(1, hung.Connects);
                Assert.Single(hung.Streams.Single().Received);
            }
            finally { hung.Release.Set(); }
        }

        [Fact]
        public void TransportFailureIsReportedWithoutRetry()
        {
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var down = new FakeConnector(_ => null) { Fail = true };
            var r = Run(down, sessions, "acad", "run", File("job.json", JobTests.GoodAcadJob));
            Assert.Equal(ExitCodes.Transport, r.code);
            Assert.Equal(1, down.Connects);
            Assert.Contains("status", r.json!.GetStringOrNull("next"));
        }

        [Fact]
        public void OtherMethodsBuildTheirParams()
        {
            var sessions = new FakeSessions { Items = { FakeSessions.Acad(10) } };
            var conn = new FakeConnector(req => Response.Success(req.AsObject().GetStringOrNull("id")!, new JsonObject().Add("x", 1)).ToJson());
            Assert.Equal(ExitCodes.Ok, Run(conn, sessions, "acad", "audit", "--checks", "a.b, c.d").code);
            Assert.Equal(ExitCodes.Ok, Run(conn, sessions, "acad", "snapshot", "--sections", "layers,blocks").code);
            Assert.Equal(ExitCodes.Ok, Run(conn, sessions, "acad", "probe", "Autodesk.AutoCAD.DatabaseServices.Database.Insunits").code);
            Assert.Equal(ExitCodes.Ok, Run(conn, sessions, "acad", "status", "j1").code);
            string outFile = Path.Combine(_dir, "snap.json");
            var snap = Run(conn, sessions, "acad", "snapshot", "--out", outFile);
            Assert.Equal(outFile, snap.json!.GetStringOrNull("out"));
            Assert.Equal(1, JsonValue.Parse(System.IO.File.ReadAllText(outFile)).AsObject()["x"]!.AsInt64());
            var p = conn.Streams.Select(s => s.Received.Single().AsObject()).ToList();
            Assert.Equal(new[] { "a.b", "c.d" }, p[0]["params"]!.AsObject()["checks"]!.AsArray().Select(x => x.AsString()));
            Assert.Equal(2, p[1]["params"]!.AsObject()["sections"]!.AsArray().Count);
            Assert.Equal("j1", p[3]["params"]!.AsObject().GetStringOrNull("jobId"));

            Assert.Equal(ExitCodes.Usage, Run(conn, sessions, "acad", "audit", "--checks", "a", "--group", "g").code);
            Assert.Equal(ExitCodes.Usage, Run(conn, sessions, "acad", "capture", "--target", "x").code);
            Assert.Equal(ExitCodes.Usage, Run(conn, sessions, "acad", "explode").code);
            Assert.Equal(ExitCodes.Usage, Run(conn, sessions, "acad", "ping", "--bogus").code);
            Assert.Equal(ExitCodes.Usage, Run(conn, sessions).code);
            Assert.Equal(ExitCodes.Usage, Run(conn, sessions, "acad", "run", "a.json", "--timeout", "0").code);
        }

        [Fact]
        public void FileSessionStoreIgnoresDeadAndBrokenFiles()
        {
            File("acad-1.json", FakeSessions.Acad(1).ToJson().ToJson());
            File("acad-2.json", FakeSessions.Acad(2).ToJson().ToJson());
            File("acad-3.json", "{ half written");
            var store = new FileSessionStore(_dir, pid => pid == 2);
            Assert.Equal(new[] { 2 }, store.List().Select(s => s.ProcessId));
            Assert.Empty(new FileSessionStore(Path.Combine(_dir, "none")).List());
        }
    }
}
