using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Autodesk.Revit.UI;
using SA.Core.Protocol;

namespace SA.Revit.Host
{
    /// <summary>One unit of work that needs a valid Revit API context.</summary>
    internal sealed class WorkItem
    {
        public const int Queued = 0, Running = 1, Done = 2, Cancelled = 3;

        public WorkItem(string requestId, string method, string? jobId, Func<UIApplication, Response> work)
        {
            RequestId = requestId;
            Method = method;
            JobId = jobId;
            Work = work;
        }

        public string RequestId { get; }
        public string Method { get; }
        public string? JobId { get; }
        public Func<UIApplication, Response> Work { get; }

        /// <summary>
        /// Created with the item, before it is queued or the event is raised, and never reset. Whatever order the
        /// handler and the waiting pipe thread run in, a Set() can never be missed (the lost-wakeup race of
        /// shared, reset-after-use events cannot happen).
        /// </summary>
        public ManualResetEventSlim Completed { get; } = new ManualResetEventSlim(false);

        public int State;
        public Response? Result;
        public DateTime EnqueuedUtc { get; } = DateTime.UtcNow;
        public DateTime? StartedUtc;

        /// <summary>Queued to Running; false if the waiter cancelled it first.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref State, Running, Queued) == Queued;

        /// <summary>Queued to Cancelled; false if the handler already started it.</summary>
        public bool TryCancel() => Interlocked.CompareExchange(ref State, Cancelled, Queued) == Queued;
    }

    /// <summary>
    /// Main-thread dispatch: a single ExternalEvent whose handler drains a thread-safe queue.
    ///
    /// Raise() may answer Accepted (queued), Pending (an earlier raise has not run yet; the pending run will drain
    /// this item too), Denied or TimedOut (not queued). For the last two the waiter re-raises every second, and the
    /// Idling handler re-raises while work is queued, so a refused raise cannot strand an item. An item that has
    /// not started within its start deadline is cancelled atomically and answered with timeout: the caller then
    /// knows that nothing ran. Once started, an item always runs to the end.
    /// </summary>
    internal sealed class ApiDispatcher : IExternalEventHandler
    {
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private ExternalEvent? _event;
        private WorkItem? _current;

        /// <summary>Must be called from a valid API context (OnStartup).</summary>
        public void Create() => _event = ExternalEvent.Create(this);

        public WorkItem? Current => _current;

        public bool HasQueuedWork => !_queue.IsEmpty;

        public string GetName() => "CadBim engine";

        public void Execute(UIApplication app)
        {
            // Called by Revit on the main thread. Must never throw.
            while (_queue.TryDequeue(out var item))
            {
                if (!item.TryStart()) { item.Completed.Set(); continue; }
                item.StartedUtc = DateTime.UtcNow;
                _current = item;
                try
                {
                    item.Result = item.Work(app);
                }
                catch (Exception e)
                {
                    Log.Error("Work item '" + item.Method + "' threw", e);
                    item.Result = Response.Failure(item.RequestId, ErrorCodes.Internal, e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    _current = null;
                    Interlocked.Exchange(ref item.State, WorkItem.Done);
                    item.Completed.Set();
                }
            }
        }

        public ExternalEventRequest Raise()
        {
            var ev = _event;
            if (ev is null) return ExternalEventRequest.Denied;
            try { return ev.Raise(); }
            catch (Exception e)
            {
                Log.Warn("ExternalEvent.Raise threw: " + e.Message);
                return ExternalEventRequest.Denied;
            }
        }

        /// <summary>Called from the Idling handler (main thread): re-raise if work is waiting.</summary>
        public void OnIdling()
        {
            if (!_queue.IsEmpty) Raise();
        }

        /// <summary>Queues the item, raises the event and blocks the calling (pipe) thread until it completes.</summary>
        public Response Run(WorkItem item, TimeSpan startDeadline)
        {
            _queue.Enqueue(item);
            var r = Raise();
            bool raised = r == ExternalEventRequest.Accepted || r == ExternalEventRequest.Pending;
            if (!raised) Log.Warn("ExternalEvent.Raise returned " + r + " for " + item.Method + "; retrying.");
            var clock = Stopwatch.StartNew();
            long lastRaise = 0;
            while (true)
            {
                if (item.Completed.Wait(250)) break;
                if (Volatile.Read(ref item.State) != WorkItem.Queued) continue;
                if (clock.Elapsed > startDeadline)
                {
                    if (item.TryCancel())
                    {
                        return Response.Failure(item.RequestId, ErrorCodes.Timeout,
                            "Revit did not start the request within " + (int)startDeadline.TotalSeconds +
                            " s (a dialog, an edit mode or a running command blocks external requests). Nothing ran.",
                            new SA.Core.Json.JsonObject().Add("started", false).Add("method", item.Method));
                    }
                    continue;
                }
                if (clock.ElapsedMilliseconds - lastRaise >= 1000)
                {
                    lastRaise = clock.ElapsedMilliseconds;
                    var rr = Raise();
                    if (rr != ExternalEventRequest.Accepted && rr != ExternalEventRequest.Pending)
                        Log.Warn("ExternalEvent.Raise returned " + rr + " while waiting for " + item.Method + ".");
                }
            }
            return item.Result ?? Response.Failure(item.RequestId, ErrorCodes.Internal, "The request finished without a result.");
        }

        public void Dispose()
        {
            try { _event?.Dispose(); } catch (Exception) { }
            _event = null;
            while (_queue.TryDequeue(out var item))
            {
                if (item.TryCancel()) item.Result = Response.Failure(item.RequestId, ErrorCodes.Internal, "Revit is shutting down; nothing ran.");
                item.Completed.Set();
            }
        }
    }
}
