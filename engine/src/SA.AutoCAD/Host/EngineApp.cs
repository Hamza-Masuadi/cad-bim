using System;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using SA.AutoCAD.Ops;
using SA.Core.Jobs;
using SA.Core.Protocol;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(SA.AutoCAD.Host.EngineApp))]
[assembly: CommandClass(typeof(SA.AutoCAD.Host.EngineCommands))]

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Entry point. Initialize starts the engine; Terminate stops it. Neither ever throws into AutoCAD: every failure is
    /// logged to %LOCALAPPDATA%\CadBim\logs\ and the engine stays off rather than half on.
    /// </summary>
    public sealed class EngineApp : IExtensionApplication
    {
        private static PipeServer? _pipe;
        private static string? _sessionFile;
        private static HandlerRegistry? _handlers;
        private static string? _startError;

        internal static HandlerRegistry? Handlers => _handlers;
        internal static string? PipeName => _pipe?.PipeName;
        internal static string? StartError => _startError;

        public void Initialize()
        {
            try
            {
                Start();
            }
            catch (System.Exception e)
            {
                _startError = e.GetType().Name + ": " + e.Message;
                Log.Error("Engine start failed; the engine is off.", e);
                SafeStop();
            }
        }

        public void Terminate()
        {
            SafeStop();
        }

        private static void Start()
        {
            Log.Info("Starting: build for " + EngineInfo.ReleaseYear + ", runtime " + EngineInfo.RuntimeDescription + ", engine " + EngineInfo.EngineVersion);
            try
            {
                var v = AcApp.Version;
                EngineInfo.ProductVersion = v.ToString();
                EngineInfo.ProductRelease = v;
            }
            catch (System.Exception e) { Log.Warn("Could not read AutoCAD's version: " + e.Message); }
            int running = EngineInfo.RunningReleaseYear;
            if (running != 0 && running != EngineInfo.ReleaseYear)
                Log.Warn("This DLL was built for " + EngineInfo.ReleaseYear + " but AutoCAD reports " + running + " (" + EngineInfo.ProductVersion + "). Check PackageContents.xml SeriesMin/SeriesMax.");

            var schemas = OpRegistry.Default;
            _handlers = HandlerRegistry.Discover(schemas);
            foreach (var p in _handlers.Problems) Log.Error("Handler registration: " + p);
            Log.Info(_handlers.Names.Count + " op handler(s): " + string.Join(", ", _handlers.Names) + ". Without handler (jobs naming them fail validation): " + _handlers.Missing.Count + ".");

            DocumentIdentity.Attach();
            MainThread.Attach();

            var ledger = new JobLedger();
            var runner = new JobRunner(_handlers, ledger, schemas);
            string pipeName = EngineApps.PipeName(EngineApps.AutoCad, EngineInfo.ProcessId);
            var router = new RequestRouter(runner, _handlers, pipeName);
            var pipe = new PipeServer(pipeName, router.Handle);
            if (!pipe.Start())
            {
                _startError = pipe.StartError;
                pipe.Dispose();
                MainThread.Detach();
                DocumentIdentity.Detach();
                return;
            }
            _pipe = pipe;
            WriteSessionFile(pipeName);
            Message("CadBim engine " + EngineInfo.EngineVersion + " listening on pipe " + pipeName + ".");
        }

        private static void WriteSessionFile(string pipeName)
        {
            var info = new SessionInfo(EngineApps.AutoCad, EngineInfo.ProcessId, pipeName, EngineInfo.ReleaseYear,
                EngineInfo.ProductVersion, EngineInfo.StartTime, EngineInfo.EngineVersion);
            string path = SaPaths.SessionFile(EngineApps.AutoCad, EngineInfo.ProcessId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, info.ToJson().ToUtf8Bytes(true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            _sessionFile = path;
            Log.Info("Session file written: " + path);
        }

        private static void SafeStop()
        {
            try { if (_sessionFile != null && File.Exists(_sessionFile)) File.Delete(_sessionFile); }
            catch (System.Exception e) { Log.Warn("Could not delete the session file: " + e.Message); }
            _sessionFile = null;
            try { _pipe?.Dispose(); } catch (System.Exception e) { Log.Warn("Stopping the pipe server: " + e.Message); }
            _pipe = null;
            try { MainThread.Detach(); } catch (System.Exception e) { Log.Warn("Detaching from the main thread: " + e.Message); }
            try { DocumentIdentity.Detach(); } catch (System.Exception e) { Log.Warn("Detaching document tracking: " + e.Message); }
            Log.Info("Stopped.");
        }

        internal static void Message(string text)
        {
            try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + text + "\n"); }
            catch { }
        }
    }

    /// <summary>One read-only command so a user can see the engine's state from the command line.</summary>
    public sealed class EngineCommands
    {
        [CommandMethod("SAENGINE", CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.NoUndoMarker)]
        public void Status()
        {
            try
            {
                var h = EngineApp.Handlers;
                EngineApp.Message(EngineApp.PipeName != null
                    ? "CadBim engine " + EngineInfo.EngineVersion + " (built for " + EngineInfo.ReleaseYear + ") on pipe " + EngineApp.PipeName +
                      "; " + (h?.Names.Count ?? 0) + " op handlers. Log: " + Log.Folder
                    : "CadBim engine is not running: " + (EngineApp.StartError ?? "unknown reason") + ". Log: " + Log.Folder);
            }
            catch (System.Exception e) { Log.Error("SAENGINE failed.", e); }
        }
    }
}
