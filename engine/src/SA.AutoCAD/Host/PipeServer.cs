using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using SA.AutoCAD.Compat;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// The engine's pipe server (ARCHITECTURE section 4). One background accept thread; each connection is served on its
    /// own background thread and carries exactly one request and one response. Several connections may be open at once so
    /// that ping and status can be answered while a long run is in progress; the request router decides what is busy.
    ///
    /// Never touches the AutoCAD API: requests that need the drawing are handed to <see cref="MainThread"/> by the router.
    /// </summary>
    internal sealed class PipeServer : IDisposable
    {
        /// <summary>Pipe instances at once (connected plus the one waiting). A ninth client waits in its own Connect timeout.</summary>
        private const int MaxInstances = 8;

        /// <summary>A client must send its whole request within this time after connecting.</summary>
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

        private readonly string _pipeName;
        private readonly Func<Request, Response> _handler;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Thread? _acceptThread;
        private NamedPipeServerStream? _waiting;
        private int _open;

        internal PipeServer(string pipeName, Func<Request, Response> handler)
        {
            _pipeName = pipeName;
            _handler = handler;
        }

        internal string PipeName => _pipeName;

        /// <summary>Set when the first instance could not be created (for example, the name is taken).</summary>
        internal string? StartError { get; private set; }

        /// <summary>Creates the first instance synchronously so a failure is known before the session file is written.</summary>
        internal bool Start()
        {
            NamedPipeServerStream first;
            try
            {
                first = PipeFactory.Create(_pipeName, MaxInstances, first: true);
            }
            catch (Exception e)
            {
                StartError = e.GetType().Name + ": " + e.Message;
                Log.Error("Could not create pipe '" + _pipeName + "'. The engine will not accept requests.", e);
                return false;
            }
            _acceptThread = new Thread(() => AcceptLoop(first)) { IsBackground = true, Name = "SA pipe accept" };
            _acceptThread.Start();
            Log.Info("Pipe server listening on \\\\.\\pipe\\" + _pipeName);
            return true;
        }

        private void AcceptLoop(NamedPipeServerStream first)
        {
            NamedPipeServerStream? next = first;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (next is null)
                    {
                        try
                        {
                            next = PipeFactory.Create(_pipeName, MaxInstances, first: false);
                        }
                        catch (IOException)
                        {
                            // All instances are in use; wait for one to close.
                            if (_stop.Token.WaitHandle.WaitOne(200)) break;
                            continue;
                        }
                    }
                    _waiting = next;
                    next.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                    _waiting = null;
                    var conn = next;
                    next = null;
                    Interlocked.Increment(ref _open);
                    var t = new Thread(() => Serve(conn)) { IsBackground = true, Name = "SA pipe connection" };
                    t.Start();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Log.Error("Pipe accept loop error; continuing.", e);
                    try { next?.Dispose(); } catch { }
                    next = null;
                    if (_stop.Token.WaitHandle.WaitOne(500)) break;
                }
            }
            try { next?.Dispose(); } catch { }
            _waiting = null;
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                byte[]? payload;
                using (var timeout = new CancellationTokenSource(ReadTimeout))
                using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                {
                    try
                    {
                        payload = Framing.ReadMessageAsync(pipe, timeout.Token).GetAwaiter().GetResult();
                    }
                    catch (FramingException e)
                    {
                        // Oversized or truncated frame: answer if the pipe is still open, then close.
                        Log.Warn("Rejected frame: " + e.Message);
                        TryWrite(pipe, Response.Failure("", ErrorCodes.BadRequest, e.Message));
                        return;
                    }
                    catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is OperationCanceledException)
                    {
                        Log.Warn("Client disconnected or timed out before sending a whole request: " + e.Message);
                        return;
                    }
                }
                if (payload is null) return;

                Response response;
                Request request;
                try
                {
                    request = Request.Parse(payload);
                }
                catch (ProtocolException e)
                {
                    TryWrite(pipe, Response.Failure(TryReadId(payload), ErrorCodes.BadRequest, e.Message));
                    return;
                }

                try
                {
                    response = _handler(request);
                }
                catch (Exception e)
                {
                    Log.Error("Unhandled error answering '" + request.Method + "'.", e);
                    response = Response.Failure(request.Id, ErrorCodes.Internal, e.GetType().Name + ": " + e.Message);
                }
                TryWrite(pipe, response);
            }
            catch (Exception e)
            {
                Log.Error("Pipe connection error.", e);
            }
            finally
            {
                try { pipe.Dispose(); } catch { }
                Interlocked.Decrement(ref _open);
            }
        }

        private static void TryWrite(NamedPipeServerStream pipe, Response response)
        {
            byte[] bytes;
            try
            {
                bytes = response.ToJson().ToUtf8Bytes();
                if (bytes.Length > ProtocolInfo.MaxMessageBytes)
                {
                    Log.Warn("Response of " + bytes.Length + " bytes exceeds the frame cap; sending an error instead.");
                    bytes = Response.Failure(response.Id, ErrorCodes.Internal,
                        "The response is larger than " + ProtocolInfo.MaxMessageBytes + " bytes; narrow the request (for example snapshot sections).").ToJson().ToUtf8Bytes();
                }
            }
            catch (Exception e)
            {
                Log.Error("Could not serialise a response.", e);
                return;
            }
            // A client that stops reading must not hold this pipe instance forever: the write and drain share a timeout
            // that disposes the pipe, which ends both blocking calls.
            using (var timeout = new CancellationTokenSource(WriteTimeout))
            using (timeout.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
            try
            {
                if (!pipe.IsConnected) { Log.Warn("Client left before the response for request '" + response.Id + "' was written."); return; }
                Framing.WriteMessage(pipe, bytes);
                // Flush (inside WriteMessage) waits until the client has read the data, so closing cannot discard it.
                try { pipe.WaitForPipeDrain(); } catch { }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
            {
                Log.Warn("Client disconnected before reading the response for request '" + response.Id + "': " + e.Message);
            }
        }

        private static string TryReadId(byte[] payload)
        {
            try
            {
                if (JsonValue.Parse(payload) is JsonObject o && o["id"] is JsonString s) return s.Value;
            }
            catch { }
            return "";
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _waiting?.Dispose(); } catch { }
            try { _acceptThread?.Join(2000); } catch { }
            Log.Info("Pipe server stopped (" + Volatile.Read(ref _open) + " connection(s) still open).");
        }
    }
}
