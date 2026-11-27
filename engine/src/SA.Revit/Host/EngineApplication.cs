using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;

namespace SA.Revit.Host
{
    /// <summary>
    /// The add-in entry point named in CadBim.addin. Nothing here may throw into Revit: every failure is
    /// caught and logged, and Revit is always told Succeeded so it shows no load-failure dialog (an engine that failed
    /// to start writes no session file, so clients simply see no engine; the log says why).
    ///
    /// This class touches no SA.Core type directly: the assembly resolver for SA.Core.dll is installed first, and
    /// only then is <see cref="EngineHost"/> (which uses Core) called.
    /// </summary>
    public sealed class EngineApplication : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromOwnFolder;
            }
            catch (Exception) { }
            try
            {
                EngineHost.Start(application);
            }
            catch (Exception e)
            {
                SafeLog("Engine start-up failed", e);
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { EngineHost.Stop(application); }
            catch (Exception e) { SafeLog("Engine shutdown failed", e); }
            return Result.Succeeded;
        }

        private static void SafeLog(string message, Exception e)
        {
            try { Log.Error(message, e); } catch (Exception) { }
        }

        /// <summary>Resolves SA.Core from the folder holding this assembly when the host's loader does not find it.</summary>
        private static Assembly? ResolveFromOwnFolder(object? sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name);
                if (name.Name != "SA.Core") return null;
                string? dir = Path.GetDirectoryName(typeof(EngineApplication).Assembly.Location);
                if (string.IsNullOrEmpty(dir)) return null;
                string path = Path.Combine(dir!, "SA.Core.dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Start-up and shutdown, separated from the entry point so Core is loaded only after the resolver is in place.</summary>
    internal static class EngineHost
    {
        private static Engine? _engine;
        private static PipeServer? _server;

        public static Engine? Current => _engine;

        public static void Start(UIControlledApplication app)
        {
            Log.Init();
            var ctrl = app.ControlledApplication;
            Log.Info("Starting CadBim Revit engine " + Engine.EngineVersion + " for Revit " + ctrl.VersionNumber + " (" + ctrl.VersionBuild + "), built for " +
                     Compat.ReleaseInfo.Year + " / " + Compat.PipeFactory.TargetRuntime + ", CLR " + Environment.Version);
            if (ctrl.VersionNumber != Compat.ReleaseInfo.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))
                Log.Warn("This build targets Revit " + Compat.ReleaseInfo.Year + " but is loaded by Revit " + ctrl.VersionNumber + ".");

            var engine = new Engine(ctrl.VersionNumber, ctrl.VersionBuild, ctrl.Language.ToString());
            foreach (var p in engine.Handlers.Problems) Log.Warn("Handler: " + p);
            foreach (var p in engine.Audit.Collectors.Problems) Log.Warn("Fact collector: " + p);
            var missing = engine.Handlers.Missing();
            if (missing.Count > 0) Log.Info(missing.Count + " Revit op schema(s) have no handler yet: " + string.Join(", ", missing));

            engine.Dispatcher.Create();
            engine.Tracker.Attach(ctrl);
            app.ViewActivated += (s, e) => engine.Tracker.OnViewActivated(e.Document);
            app.Idling += (s, e) =>
            {
                try { engine.Dispatcher.OnIdling(); } catch (Exception ex) { Log.Warn("Idling: " + ex.Message); }
            };
            _engine = engine;

            var server = new PipeServer(engine.PipeName, engine.Handle);
            server.Start();
            _server = server;
            engine.WriteSessionFile();
            Log.Info("Listening on \\\\.\\pipe\\" + engine.PipeName + "; session file " + engine.SessionFile);
        }

        public static void Stop(UIControlledApplication app)
        {
            var engine = _engine;
            _engine = null;
            try { engine?.DeleteSessionFile(); } catch (Exception) { }
            try { _server?.Dispose(); } catch (Exception) { }
            _server = null;
            if (engine != null)
            {
                try { engine.Tracker.Detach(app.ControlledApplication); } catch (Exception) { }
                try { engine.Dispose(); } catch (Exception) { }
            }
            Log.Info("Engine stopped.");
        }
    }
}
