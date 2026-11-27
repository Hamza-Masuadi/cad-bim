using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.Cli
{
    public static class ExitCodes
    {
        public const int Ok = 0;
        /// <summary>The engine answered ok=false, a run did not commit, or lint found errors.</summary>
        public const int Failed = 1;
        public const int Usage = 2;
        /// <summary>No running engine, or several and no --pid.</summary>
        public const int Session = 3;
        /// <summary>Could not connect, or the connection broke.</summary>
        public const int Transport = 4;
        /// <summary>No answer in time. For run, the job may still finish: ask with status.</summary>
        public const int Timeout = 5;
    }

    /// <summary>Finds running engines from their session files.</summary>
    public interface ISessionStore
    {
        /// <summary>Sessions whose process is still alive, ordered by app then process id.</summary>
        List<SessionInfo> List();
    }

    public sealed class FileSessionStore : ISessionStore
    {
        private readonly string _folder;
        private readonly Func<int, bool> _isAlive;

        public FileSessionStore(string? folder = null, Func<int, bool>? isAlive = null)
        {
            _folder = folder ?? SaPaths.Sessions();
            _isAlive = isAlive ?? IsProcessAlive;
        }

        public List<SessionInfo> List()
        {
            var list = new List<SessionInfo>();
            if (!Directory.Exists(_folder)) return list;
            foreach (var file in Directory.GetFiles(_folder, "*.json"))
            {
                try
                {
                    var s = SessionInfo.Parse(JsonValue.Parse(File.ReadAllBytes(file)));
                    if (_isAlive(s.ProcessId)) list.Add(s);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
                {
                    // A half-written or stale file is not a session.
                }
            }
            return list.OrderBy(s => s.App, StringComparer.Ordinal).ThenBy(s => s.ProcessId).ToList();
        }

        public static bool IsProcessAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; } // exists but not inspectable
        }
    }

    /// <summary>Opens a connection to an engine. One connection carries one request and one response.</summary>
    public interface IEngineConnector
    {
        Stream Connect(SessionInfo session, int timeoutMs);
    }

    /// <summary>
    /// Connects with impersonation limited to identification (the server can learn who the client is, not act as it),
    /// then checks that the process serving the pipe is the session's process: a stale session file whose pipe name was
    /// taken by another process is refused before anything is sent.
    /// </summary>
    public sealed class PipeConnector : IEngineConnector
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        public Stream Connect(SessionInfo session, int timeoutMs)
        {
            var pipe = new NamedPipeClientStream(".", session.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            try
            {
                pipe.Connect(timeoutMs);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server))
                    throw new IOException("Could not read the process serving pipe '" + session.PipeName + "' (Windows error " + Marshal.GetLastWin32Error() + "); nothing was sent.");
                if (server != (uint)session.ProcessId)
                    throw new IOException("Pipe '" + session.PipeName + "' is served by process " + server + ", not the session's process " + session.ProcessId +
                        "; nothing was sent. The session file may be stale.");
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }

    public sealed class ClientResult
    {
        public Response? Response { get; set; }
        public bool TimedOut { get; set; }
        public string? TransportError { get; set; }
    }

    /// <summary>
    /// Sends one request and waits for one response. It never sends a request twice: on timeout or a broken
    /// connection the caller is told, and for run must ask with status instead of retrying.
    /// </summary>
    public sealed class EngineClient
    {
        private readonly IEngineConnector _connector;

        public EngineClient(IEngineConnector connector)
        {
            _connector = connector;
        }

        public int ConnectTimeoutMs { get; set; } = 5000;

        public ClientResult Send(SessionInfo session, Request request, TimeSpan timeout)
        {
            var task = Task.Run(() =>
            {
                using (var stream = _connector.Connect(session, ConnectTimeoutMs))
                {
                    Framing.WriteJson(stream, request.ToJson());
                    var bytes = Framing.ReadMessage(stream);
                    if (bytes is null) throw new IOException("The engine closed the connection without answering.");
                    return Response.Parse(bytes);
                }
            });

            bool finished;
            try
            {
                finished = task.Wait(timeout);
            }
            catch (AggregateException ae)
            {
                var inner = ae.InnerException ?? ae;
                return new ClientResult { TransportError = inner.GetType().Name + ": " + inner.Message };
            }
            if (!finished) return new ClientResult { TimedOut = true };
            return new ClientResult { Response = task.Result };
        }
    }
}
