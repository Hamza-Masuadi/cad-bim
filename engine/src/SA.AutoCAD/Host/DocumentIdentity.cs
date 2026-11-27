using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using SA.Core.Json;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Change tracking for one open document. Created when the engine first sees the document; never reused.
    /// </summary>
    internal sealed class DocumentTracker
    {
        private long _changes;
        private long _suspendDepth;

        internal DocumentTracker(Document document)
        {
            Document = document;
            Token = Guid.NewGuid().ToString("N").Substring(0, 12);
            Path = document.Name ?? "";
        }

        internal Document Document { get; }
        internal string Token { get; }
        /// <summary>Last known document name (full path for a saved drawing). Updated on the main thread.</summary>
        internal string Path { get; set; }
        internal long Changes => Interlocked.Read(ref _changes);

        internal void Bump()
        {
            if (Interlocked.Read(ref _suspendDepth) == 0) Interlocked.Increment(ref _changes);
        }

        /// <summary>Counts a change even while counting is suspended (a rehearsal found a change that survived its rollback).</summary>
        internal void ForceBump() => Interlocked.Increment(ref _changes);

        /// <summary>Ignore changes until <see cref="Resume"/>.</summary>
        internal void Suspend() => Interlocked.Increment(ref _suspendDepth);

        internal void Resume() => Interlocked.Decrement(ref _suspendDepth);

        private List<ObjectId>? _rehearsalAppended;

        /// <summary>
        /// Main thread. Suspends counting for a rehearsal, which is rolled back, and records every object appended meanwhile
        /// so <see cref="RehearsalAppended"/> can show which of them survived the rollback.
        /// </summary>
        internal void BeginRehearsal()
        {
            _rehearsalAppended = new List<ObjectId>();
            Suspend();
        }

        /// <summary>Main thread. Ends <see cref="BeginRehearsal"/>; does nothing when no rehearsal is open.</summary>
        internal void EndRehearsal()
        {
            if (_rehearsalAppended is null) return;
            _rehearsalAppended = null;
            Resume();
        }

        /// <summary>Objects appended since <see cref="BeginRehearsal"/> (empty outside a rehearsal).</summary>
        internal IReadOnlyList<ObjectId> RehearsalAppended => (IReadOnlyList<ObjectId>?)_rehearsalAppended ?? Array.Empty<ObjectId>();

        internal void Appended(ObjectId id)
        {
            _rehearsalAppended?.Add(id);
            Bump();
        }

        /// <summary>See <see cref="DocumentIdentity"/> for the definition.</summary>
        internal string Fingerprint => "acad1-" + Token + "-" + Changes.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Document identity = path plus fingerprint.
    ///
    /// Path is <c>Document.Name</c>: the full path of a saved drawing, or "DrawingN.dwg" for one never saved
    /// (<c>isNamed</c> false in the snapshot). Paths compare case-insensitively after normalising separators.
    ///
    /// Fingerprint is "acad1-&lt;token&gt;-&lt;changes&gt;":
    /// - token: random, created when this engine process first sees the document. Closing and reopening the drawing, or
    ///   restarting AutoCAD, gives a new token, so a snapshot taken before never matches afterwards (conservative).
    /// - changes: a counter incremented by every Database.ObjectAppended, ObjectErased and ObjectModified event in the
    ///   drawing (any object, any table, any space), and by changes to the drawing-level system variables listed in
    ///   <see cref="MaterialSysVars"/>. ObjectModified on ViewportTableRecord is ignored because zooming and panning
    ///   update the active viewport record without changing the drawing.
    ///   A rehearsal suspends counting because everything it does is rolled back; it records the objects appended
    ///   meanwhile, and the job runner bumps the counter (and fails the rehearsal) when any of them survives the rollback
    ///   or a material system variable differs afterwards.
    /// It is cheap (an integer increment per event) and readable off the main thread for ping.
    /// What it does not see: changes made while the engine was not loaded (covered by the token) and changes to
    /// non-database state (UI, views).
    /// </summary>
    internal static class DocumentIdentity
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Database, DocumentTracker> Trackers = new Dictionary<Database, DocumentTracker>();
        private static volatile DocumentTracker? _active;

        internal static readonly HashSet<string> MaterialSysVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INSUNITS", "MEASUREMENT", "LUNITS", "LUPREC", "AUNITS", "AUPREC", "ANGBASE", "ANGDIR", "CANNOSCALE",
            "LTSCALE", "PSLTSCALE", "MSLTSCALE", "CELTSCALE", "DIMSCALE", "TEXTSIZE", "EXTNAMES", "PSTYLEMODE",
        };

        /// <summary>Main thread. Hooks every open document and the document collection.</summary>
        internal static void Attach()
        {
            var dm = AcApp.DocumentManager;
            foreach (Document d in dm) Track(d);
            dm.DocumentCreated += OnDocumentCreated;
            dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            dm.DocumentActivated += OnDocumentActivated;
            AcApp.SystemVariableChanged += OnSystemVariableChanged;
            RefreshActive();
        }

        internal static void Detach()
        {
            try
            {
                var dm = AcApp.DocumentManager;
                dm.DocumentCreated -= OnDocumentCreated;
                dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
                dm.DocumentActivated -= OnDocumentActivated;
                AcApp.SystemVariableChanged -= OnSystemVariableChanged;
            }
            catch (Exception e) { Log.Warn("Detaching document events: " + e.Message); }
            lock (Gate)
            {
                foreach (var kv in Trackers) Unhook(kv.Key);
                Trackers.Clear();
            }
            _active = null;
        }

        /// <summary>The tracker for a document, created on first use. Main thread.</summary>
        internal static DocumentTracker Track(Document doc)
        {
            lock (Gate)
            {
                if (Trackers.TryGetValue(doc.Database, out var t)) return t;
                t = new DocumentTracker(doc);
                Trackers.Add(doc.Database, t);
                var db = doc.Database;
                db.ObjectAppended += OnObjectAppended;
                db.ObjectErased += OnObjectErased;
                db.ObjectModified += OnObjectModified;
                return t;
            }
        }

        private static void Unhook(Database db)
        {
            try
            {
                db.ObjectAppended -= OnObjectAppended;
                db.ObjectErased -= OnObjectErased;
                db.ObjectModified -= OnObjectModified;
            }
            catch { }
        }

        private static DocumentTracker? Find(Database? db)
        {
            if (db is null) return null;
            lock (Gate) return Trackers.TryGetValue(db, out var t) ? t : null;
        }

        private static void OnObjectAppended(object? sender, ObjectEventArgs e)
        {
            var t = Find(sender as Database);
            if (t is null) return;
            ObjectId id = ObjectId.Null;
            try { id = e.DBObject.ObjectId; }
            catch { }
            t.Appended(id);
        }

        private static void OnObjectErased(object? sender, ObjectErasedEventArgs e) => Find(sender as Database)?.Bump();

        private static void OnObjectModified(object? sender, ObjectEventArgs e)
        {
            try
            {
                if (e.DBObject is ViewportTableRecord) return;
            }
            catch { }
            Find(sender as Database)?.Bump();
        }

        private static void OnSystemVariableChanged(object? sender, Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
        {
            try
            {
                if (MaterialSysVars.Contains(e.Name)) _active?.Bump();
            }
            catch { }
        }

        private static void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
        {
            try { if (e.Document != null) Track(e.Document); }
            catch (Exception ex) { Log.Error("Tracking a new document failed.", ex); }
        }

        private static void OnDocumentToBeDestroyed(object? sender, DocumentCollectionEventArgs e)
        {
            try
            {
                if (e.Document is null) return;
                lock (Gate)
                {
                    var db = e.Document.Database;
                    if (Trackers.Remove(db)) Unhook(db);
                }
                if (_active != null && ReferenceEquals(_active.Document, e.Document)) _active = null;
            }
            catch (Exception ex) { Log.Error("Untracking a closing document failed.", ex); }
        }

        private static void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
        {
            try { RefreshActive(); }
            catch (Exception ex) { Log.Error("Refreshing the active document failed.", ex); }
        }

        /// <summary>Main thread. Updates the cached active document read by ping.</summary>
        internal static void RefreshActive()
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc is null) { _active = null; return; }
                var t = Track(doc);
                t.Path = doc.Name ?? "";
                _active = t;
            }
            catch (Exception e)
            {
                Log.Warn("Could not refresh the active document: " + e.Message);
            }
        }

        /// <summary>Cached identity of the active document; safe on any thread (never touches the AutoCAD API).</summary>
        internal static JsonObject CachedActiveJson()
        {
            var t = _active;
            if (t is null) return new JsonObject().Add("open", false);
            return new JsonObject().Add("open", true).Add("path", t.Path).Add("fingerprint", t.Fingerprint);
        }

        internal static string NormalisePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string p = path!.Trim().Replace('/', '\\');
            try
            {
                if (System.IO.Path.IsPathRooted(p)) p = System.IO.Path.GetFullPath(p);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { }
            return p.TrimEnd('\\').ToUpperInvariant();
        }

        internal static bool SamePath(string? a, string? b) => NormalisePath(a) == NormalisePath(b) && NormalisePath(a).Length > 0;

        /// <summary>Main thread. The open document whose name matches <paramref name="path"/>, or null.</summary>
        internal static Document? FindOpen(string path)
        {
            foreach (Document d in AcApp.DocumentManager)
                if (SamePath(d.Name, path)) return d;
            return null;
        }

        /// <summary>Main thread. The open document with this fingerprint, or null.</summary>
        internal static Document? FindByFingerprint(string fingerprint)
        {
            foreach (Document d in AcApp.DocumentManager)
                if (Track(d).Fingerprint == fingerprint) return d;
            return null;
        }

        internal static string FingerprintOf(Document doc) => Track(doc).Fingerprint;
    }
}
