using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Revit.Compat;

namespace SA.Revit.Host
{
    /// <summary>
    /// The named pipe server (ARCHITECTURE section 4): one request and one response per connection, 4-byte
    /// little-endian length framing from Core, 64 MB cap, current-user ACL.
    ///
    /// Threading: one background accept thread. When a client connects, the next instance is created at once
    /// (before the connected one is handed to a worker), so the pipe name is never free for another process to
    /// take over between connections. Each connection is served on its own background thread so ping and status
    /// are answered while a job holds the API slot. The handler never touches the Revit API; it only enqueues.
    ///
    /// A client that stalls or disconnects mid-message: the read has a deadline (the stream is disposed when it
    /// passes), a truncated frame raises a FramingException, and in both cases nothing is executed because the
    /// request is dispatched only after the whole frame has been read and parsed. The write has a deadline too, so a
    /// client that never reads its response cannot hold an instance for good.
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        private const int MaxInstances = 8;
        private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(30);
        /// <summary>A client that does not read its response within this time loses it (the instance is freed).</summary>
        private static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(60);

        private readonly string _pipeName;
        private readonly Func<byte[], byte[]> _handler;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Thread? _thread;
        private int _active;

        public PipeServer(string pipeName, Func<byte[], byte[]> handler)
        {
            _pipeName = pipeName;
            _handler = handler;
        }

        public string PipeName => _pipeName;

        /// <summary>Creates the first instance synchronously (so a name clash fails start-up), then starts listening.</summary>
        public void Start()
        {
            var first = PipeFactory.Create(_pipeName, MaxInstances, firstInstance: true);
            _thread = new Thread(() => AcceptLoop(first)) { IsBackground = true, Name = "SA pipe accept" };
            _thread.Start();
        }

        private void AcceptLoop(NamedPipeServerStream? next)
        {
            while (!_stop.IsCancellationRequested)
            {
                var server = next;
                next = null;
                try
                {
                    if (server is null) server = PipeFactory.Create(_pipeName, MaxInstances, firstInstance: false);
                }
                catch (Exception e)
                {
                    // All instances busy, or a transient OS error: back off and try again.
                    Log.Warn("Could not create a pipe instance: " + e.Message);
                    if (_stop.Token.WaitHandle.WaitOne(500)) break;
                    continue;
                }

                try
                {
                    server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { SafeDispose(server); break; }
                catch (Exception e)
                {
                    Log.Warn("Pipe wait failed: " + e.Message);
                    SafeDispose(server);
                    if (_stop.Token.WaitHandle.WaitOne(200)) break;
                    continue;
                }

                if (_stop.IsCancellationRequested) { SafeDispose(server); break; }

                // Keep the name occupied before handing this instance over.
                try { next = PipeFactory.Create(_pipeName, MaxInstances, firstInstance: false); }
                catch (Exception) { next = null; }

                var connected = server;
                Interlocked.Increment(ref _active);
                var worker = new Thread(() => Serve(connected)) { IsBackground = true, Name = "SA pipe connection" };
                worker.Start();
            }
            if (next != null) SafeDispose(next);
        }

        private void Serve(NamedPipeServerStream stream)
        {
            try
            {
                byte[]? request;
                using (var deadline = new Timer(_ => SafeDispose(stream), null, ReadDeadline, Timeout.InfiniteTimeSpan))
                {
                    try { request = Framing.ReadMessage(stream); }
                    catch (FramingException e)
                    {
                        Log.Warn("Rejected frame: " + e.Message);
                        TryWrite(stream, Response.Failure("", ErrorCodes.BadRequest, e.Message).ToJson().ToUtf8Bytes());
                        return;
                    }
                    catch (Exception e)
                    {
                        Log.Info("Client went away before a full request arrived: " + e.Message);
                        return;
                    }
                }
                if (request is null) return;

                byte[] response;
                try { response = _handler(request); }
                catch (Exception e)
                {
                    Log.Error("Request handler threw", e);
                    response = Response.Failure("", ErrorCodes.Internal, e.GetType().Name + ": " + e.Message).ToJson().ToUtf8Bytes();
                }

                if (!TryWrite(stream, response))
                    Log.Warn("The client disconnected before the response was written. A job's outcome stays available through status.");
            }
            finally
            {
                SafeDispose(stream);
                Interlocked.Decrement(ref _active);
            }
        }

        private static bool TryWrite(NamedPipeServerStream stream, byte[] payload)
        {
            using (new Timer(_ => SafeDispose(stream), null, WriteDeadline, Timeout.InfiniteTimeSpan))
            try
            {
                if (payload.Length > ProtocolInfo.MaxMessageBytes)
                    payload = Response.Failure("", ErrorCodes.Internal,
                        "The response is " + payload.Length + " bytes, over the 64 MB limit. Narrow the request (for example snapshot params.sections or params.limit).")
                        .ToJson().ToUtf8Bytes();
                Framing.WriteMessage(stream, payload);
                try { stream.WaitForPipeDrain(); } catch (Exception) { }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void SafeDispose(Stream s)
        {
            try { s.Dispose(); } catch (Exception) { }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch (Exception) { }
            // On .NET Framework a pending wait may ignore cancellation; a throwaway connection unblocks it.
            try
            {
                using (var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                    c.Connect(200);
            }
            catch (Exception) { }
            try { _thread?.Join(1000); } catch (Exception) { }
        }
    }

    internal static class Responses
    {
        public static byte[] Bytes(Response r) => r.ToJson().ToUtf8Bytes();

        public static Response Fail(string id, string code, string message, JsonObject? details = null) =>
            Response.Failure(id, code, message, details);
    }
}
