using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using SA.Core.Json;
using SA.Core.Protocol;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Host
{
    /// <summary>One unit of work for AutoCAD's main thread. It starts at most once, or is cancelled before it starts.</summary>
    internal sealed class WorkItem
    {
        private const int Queued = 0, Started = 1, Cancelled = 2;
        private readonly Func<JsonValue> _work;
        private int _state;

        internal WorkItem(string name, Func<JsonValue> work)
        {
            Name = name;
            _work = work;
        }

        internal string Name { get; }
        internal ManualResetEventSlim StartedEvent { get; } = new ManualResetEventSlim(false);
        internal ManualResetEventSlim DoneEvent { get; } = new ManualResetEventSlim(false);
        internal JsonValue? Result { get; private set; }
        internal Exception? Error { get; private set; }

        internal bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Queued) == Queued;

        internal bool TryCancel() => Interlocked.CompareExchange(ref _state, Cancelled, Queued) == Queued;

        internal bool IsCancelled => Volatile.Read(ref _state) == Cancelled;

        internal void Run()
        {
            StartedEvent.Set();
            try { Result = _work(); }
            catch (Exception e) { Error = e; }
            finally { DoneEvent.Set(); }
        }
    }

    /// <summary>
    /// Runs work on AutoCAD's main thread.
    ///
    /// Mechanism (chosen after reading the AutoCAD .NET reference shipped with the API packages):
    /// - Work is drained from <c>Application.Idle</c> ("fired when the application goes idle"), one item per event, and
    ///   only when <c>Application.IsQuiescent</c> is true ("whether there is a command, LISP script, or ARX command
    ///   active"), the editor of the active document is quiescent and <c>DocumentManager.IsApplicationContext</c> is true.
    ///   A user command in progress therefore always wins: the item waits.
    /// - The item runs in the application context. Code that writes locks the target document with
    ///   <c>Document.LockDocument(DocumentLockMode.Write, globalName, localName, false)</c>. The reference documents that
    ///   "each time a document is locked, it is considered the beginning of a command" and that the local name "is
    ///   displayed ... when this command is undone": one lock around the whole job is one undo step carrying our name.
    ///   This is also the context the developer's guide prescribes for code outside a command (modeless UI), with explicit
    ///   locking.
    /// - <c>ExecuteInCommandContextAsync</c> was rejected: it is not described in the shipped reference, it only targets
    ///   the active document, an exception thrown inside it is reported to take AutoCAD down, and it gives no control over
    ///   the undo label.
    ///
    /// Idle only fires when the message queue empties, so after queueing, the pipe thread posts WM_NULL to the main window
    /// to make sure the queue is processed even when AutoCAD sits untouched.
    /// </summary>
    internal static class MainThread
    {
        private static readonly object Gate = new object();
        private static readonly Queue<WorkItem> Pending = new Queue<WorkItem>();
        private static IntPtr _mainWindow;
        private static bool _attached;
        private static bool _draining;
        private static Timer? _nudge;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WmNull = 0x0000;

        /// <summary>Call on the main thread (from Initialize).</summary>
        internal static void Attach()
        {
            if (_attached) return;
            try { _mainWindow = AcApp.MainWindow.Handle; }
            catch (Exception e) { Log.Warn("Could not read the main window handle; relying on Idle alone. " + e.Message); }
            AcApp.Idle += OnIdle;
            _attached = true;
            // A slow safety net: while work is pending, nudge the message loop once a second.
            _nudge = new Timer(_ => { try { if (HasPending) Wake(); } catch { } }, null, 1000, 1000);
        }

        internal static void Detach()
        {
            if (!_attached) return;
            try { AcApp.Idle -= OnIdle; } catch { }
            try { _nudge?.Dispose(); } catch { }
            _attached = false;
            lock (Gate)
            {
                while (Pending.Count > 0)
                {
                    var item = Pending.Dequeue();
                    item.TryCancel();
                }
            }
        }

        private static bool HasPending { get { lock (Gate) return Pending.Count > 0; } }

        private static void Wake()
        {
            if (_mainWindow != IntPtr.Zero)
            {
                try { PostMessage(_mainWindow, WmNull, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }

        /// <summary>
        /// Queues work and waits. If it has not started within <paramref name="startTimeout"/>, it is cancelled (it will
        /// never run) and a timeout is raised. Once started it is waited for without limit: a started job always finishes
        /// and records itself in the ledger.
        /// </summary>
        internal static JsonValue Invoke(string name, Func<JsonValue> work, TimeSpan startTimeout)
        {
            if (!_attached) throw new EngineException(ErrorCodes.Internal, "The engine is not attached to AutoCAD's main thread.");
            var item = new WorkItem(name, work);
            lock (Gate) Pending.Enqueue(item);
            Wake();
            // Cancelled here, or already cancelled by Detach at shutdown: either way it will never run.
            if (!item.StartedEvent.Wait(startTimeout) && (item.TryCancel() || item.IsCancelled))
            {
                throw new EngineException(ErrorCodes.Timeout,
                    "AutoCAD did not become idle within " + (int)startTimeout.TotalSeconds + " s (a command, dialog or script is active). Nothing was run.",
                    new JsonObject().Add(ErrorDetails.Started, false).Add(ErrorDetails.Method, name.Split(' ')[0]));
            }
            item.DoneEvent.Wait();
            if (item.Error != null)
            {
                if (item.Error is EngineException) throw item.Error;
                throw new EngineException(ErrorCodes.Internal, item.Error.GetType().Name + ": " + item.Error.Message, null, item.Error);
            }
            return item.Result ?? new JsonObject();
        }

        private static void OnIdle(object? sender, EventArgs e)
        {
            // Never let anything escape into AutoCAD's event dispatch.
            try
            {
                if (_draining) return;
                WorkItem? item = null;
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                }
                if (!IsSafeToRun()) return;
                lock (Gate)
                {
                    while (Pending.Count > 0)
                    {
                        var candidate = Pending.Dequeue();
                        if (candidate.TryStart()) { item = candidate; break; }
                    }
                }
                if (item is null) return;
                _draining = true;
                try
                {
                    item.Run();
                }
                finally
                {
                    _draining = false;
                    DocumentIdentity.RefreshActive();
                }
                if (HasPending) Wake();
            }
            catch (Exception ex)
            {
                Log.Error("Error in the Idle handler.", ex);
            }
        }

        private static bool IsSafeToRun()
        {
            if (!AcApp.IsQuiescent) return false;
            var dm = AcApp.DocumentManager;
            if (!dm.IsApplicationContext) return false;
            var doc = dm.MdiActiveDocument;
            if (doc != null)
            {
                if (!doc.Editor.IsQuiescent) return false;
                if (!string.IsNullOrEmpty(doc.CommandInProgress)) return false;
            }
            return true;
        }
    }
}
