using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SA.Core.Json;
using SA.Core.Protocol;
using Xunit;

namespace SA.Core.Tests
{
    /// <summary>A stream that returns at most one byte per Read, to exercise partial reads.</summary>
    internal sealed class TrickleStream : MemoryStream
    {
        public TrickleStream(byte[] data) : base(data) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
    }

    public class ProtocolTests
    {
        [Fact]
        public void FrameRoundTripWithLittleEndianHeader()
        {
            var ms = new MemoryStream();
            Framing.WriteMessage(ms, Encoding.UTF8.GetBytes("{\"a\":1}"));
            var bytes = ms.ToArray();
            Assert.Equal(new byte[] { 7, 0, 0, 0 }, bytes.Take(4).ToArray());
            ms.Position = 0;
            Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(Framing.ReadMessage(ms)!));
            Assert.Null(Framing.ReadMessage(ms));
        }

        [Fact]
        public void SeveralMessagesAndPartialReads()
        {
            var ms = new MemoryStream();
            Framing.WriteJson(ms, JsonValue.Parse("[1]"));
            Framing.WriteJson(ms, JsonValue.Parse("{\"b\":\"" + new string('x', 300) + "\"}"));
            var t = new TrickleStream(ms.ToArray());
            Assert.Equal("[1]", Framing.ReadJson(t)!.ToJson());
            Assert.Equal(300, Framing.ReadJson(t)!.AsObject()["b"]!.AsString().Length);
            Assert.Null(Framing.ReadJson(t));
        }

        [Fact]
        public void HeaderOf300BytesIsDecoded()
        {
            Assert.Equal(300, Framing.DecodeHeader(Framing.EncodeHeader(300)));
            Assert.Equal(new byte[] { 0x2C, 0x01, 0, 0 }, Framing.EncodeHeader(300));
        }

        [Fact]
        public void TruncatedHeaderAndPayloadAreErrors()
        {
            Assert.Throws<FramingException>(() => Framing.ReadMessage(new MemoryStream(new byte[] { 5, 0 })));
            Assert.Throws<FramingException>(() => Framing.ReadMessage(new MemoryStream(new byte[] { 5, 0, 0, 0, 1, 2 })));
        }

        [Fact]
        public void OversizeAndZeroLengthAreRejectedBeforeAllocation()
        {
            uint over = ProtocolInfo.MaxMessageBytes + 1u;
            var header = new[] { (byte)over, (byte)(over >> 8), (byte)(over >> 16), (byte)(over >> 24) };
            var e = Assert.Throws<FramingException>(() => Framing.ReadMessage(new MemoryStream(header)));
            Assert.Contains("exceeds", e.Message);
            Assert.Throws<FramingException>(() => Framing.ReadMessage(new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
            Assert.Throws<FramingException>(() => Framing.ReadMessage(new MemoryStream(new byte[] { 0, 0, 0, 0 })));
            Assert.Equal(ProtocolInfo.MaxMessageBytes, Framing.DecodeHeader(Framing.EncodeHeader(ProtocolInfo.MaxMessageBytes)));
        }

        [Fact]
        public void WriterRefusesEmptyAndOversizePayloads()
        {
            Assert.Throws<FramingException>(() => Framing.WriteMessage(new MemoryStream(), new byte[0]));
            Assert.Throws<FramingException>(() => Framing.WriteMessage(new MemoryStream(), new byte[ProtocolInfo.MaxMessageBytes + 1]));
        }

        [Fact]
        public async Task AsyncRoundTrip()
        {
            var ms = new MemoryStream();
            await Framing.WriteMessageAsync(ms, Encoding.UTF8.GetBytes("\"hi\""));
            ms.Position = 0;
            var got = await Framing.ReadMessageAsync(new TrickleStream(ms.ToArray()));
            Assert.Equal("\"hi\"", Encoding.UTF8.GetString(got!));
        }

        [Fact]
        public void RequestRoundTripAndChecks()
        {
            var r = Request.Create(Methods.Run, new JsonObject().Add("mode", "rehearse"));
            var back = Request.Parse(r.ToJson().ToUtf8Bytes());
            Assert.Equal(r.Id, back.Id);
            Assert.Equal("run", back.Method);
            Assert.Equal("rehearse", back.Params.GetStringOrNull("mode"));
            Assert.Throws<ProtocolException>(() => Request.Parse(JsonValue.Parse("{\"v\":2,\"id\":\"x\",\"method\":\"ping\"}")));
            Assert.Throws<ProtocolException>(() => Request.Parse(JsonValue.Parse("{\"v\":1,\"method\":\"ping\"}")));
            Assert.Throws<ProtocolException>(() => Request.Parse(JsonValue.Parse("{\"v\":1,\"id\":\"x\",\"method\":\"ping\",\"extra\":1}")));
            Assert.Throws<ProtocolException>(() => Request.Parse(Encoding.UTF8.GetBytes("{bad")));
        }

        [Fact]
        public void ResponsesUseOnlyTheClosedErrorList()
        {
            var ok = Response.Parse(Response.Success("1", new JsonObject().Add("x", 1)).ToJson());
            Assert.True(ok.Ok);
            var fail = Response.Parse(Response.Failure("1", ErrorCodes.WrongDocument, "other file").ToJson());
            Assert.False(fail.Ok);
            Assert.Equal("wrong_document", fail.Error!.Code);
            Assert.Throws<ArgumentException>(() => Response.Failure("1", "made_up", "x"));
            Assert.Throws<ProtocolException>(() => Response.Parse(JsonValue.Parse("{\"v\":1,\"id\":\"1\",\"ok\":false,\"error\":{\"code\":\"nope\",\"message\":\"\"}}")));
            Assert.Throws<ProtocolException>(() => Response.Parse(JsonValue.Parse("{\"v\":1,\"id\":\"1\",\"ok\":true,\"error\":{\"code\":\"busy\"}}")));
            Assert.Equal(14, ErrorCodes.All.Count);
        }

        [Fact]
        public void SessionFileRoundTripAndNames()
        {
            var s = new SessionInfo("revit", 4242, EngineApps.PipeName("revit", 4242), 2026, "26.1.0", DateTimeOffset.Parse("2026-11-27T10:00:00Z"));
            var back = SessionInfo.Parse(s.ToJson());
            Assert.Equal("sa-revit-4242", back.PipeName);
            Assert.Equal(2026, back.ReleaseYear);
            Assert.Equal(s.StartTime, back.StartTime);
            Assert.Throws<ArgumentException>(() => EngineApps.PipeName("autocad", 1));
            Assert.EndsWith(Path.Combine("sessions", "acad-7.json"), SaPaths.SessionFile("acad", 7, "/tmp/x"));
            Assert.EndsWith(Path.Combine("ledger", "revit", "j1.json"), SaPaths.LedgerFile("revit", "j1", "/tmp/x"));
        }
    }
}
