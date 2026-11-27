using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Host
{
    /// <summary>
    /// Who a document is, and a cheap fingerprint of its state.
    ///
    /// <b>Path.</b> A local or workshared-local file: <c>Document.PathName</c> (for a workshared model this is the
    /// local copy; the central path is reported as <c>centralPath</c>). A cloud model: the user-visible cloud path
    /// (<c>ModelPathUtils.ConvertModelPathToUserVisiblePath(GetCloudModelPath())</c>); a job may also name it as
    /// <c>cloud:&lt;projectGuid&gt;/&lt;modelGuid&gt;</c>. An unsaved document: <c>unsaved:&lt;title&gt;</c>.
    /// Paths compare case-insensitively with / and \ treated alike.
    ///
    /// <b>Fingerprint</b> <c>r1:&lt;session&gt;:&lt;versionGuid&gt;:&lt;saves&gt;:&lt;changes&gt;</c>:
    /// <list type="bullet">
    /// <item>session: random per Revit session, so a snapshot taken before Revit restarted never matches (unsaved
    /// edits from that session are gone, so its state cannot be trusted);</item>
    /// <item>versionGuid and saves: <c>Document.GetDocumentVersion</c>. The GUID changes as the model changes
    /// (the API says "not necessarily" on every change) and the save count on every save;</item>
    /// <item>changes: a session-wide clock read when this document last changed (every committed, undone or redone
    /// transaction), so even a change the GUID misses changes the fingerprint. The clock only moves forward and is
    /// shared by all documents, so a document whose entry is dropped (a cancelled close) and created again never
    /// returns to a value an earlier state had.</item>
    /// </list>
    /// The engine's own rehearsals are excluded from the counter, and after a rehearsal the pre-rehearsal
    /// fingerprint stays accepted as long as nothing else changes (the version GUID may not roll back with the
    /// transaction group). Selection and view changes do not change the fingerprint.
    /// </summary>
    internal sealed class DocumentIdentity
    {
        public string Path = "";
        public string Title = "";
        public string Fingerprint = "";
        public bool IsCloud;
        public bool IsWorkshared;
        public bool IsFamily;
        public bool IsReadOnly;
        public bool IsLinked;
        public string? CentralPath;
        public string? CloudProjectGuid;
        public string? CloudModelGuid;
        public string? CloudUrn;
        public string VersionGuid = "";
        public int Saves;
        public long Changes;

        public JsonObject ToJson()
        {
            var o = new JsonObject().Add("path", Path).Add("fingerprint", Fingerprint).Add("title", Title)
                .Add("isWorkshared", IsWorkshared).Add("isCloud", IsCloud).Add("isFamilyDocument", IsFamily).Add("readOnly", IsReadOnly);
            if (CentralPath != null) o.Add("centralPath", CentralPath);
            if (IsCloud)
            {
                var c = new JsonObject();
                if (CloudProjectGuid != null) c.Add("projectGuid", CloudProjectGuid);
                if (CloudModelGuid != null) c.Add("modelGuid", CloudModelGuid);
                if (CloudUrn != null) c.Add("urn", CloudUrn);
                o.Add("cloud", c);
            }
            o.Add("fingerprintParts", new JsonObject().Add("versionGuid", VersionGuid).Add("saves", Saves).Add("changes", Changes));
            return o;
        }

        public static string NormalisePath(string p) => p.Trim().Replace('\\', '/');

        public bool Matches(string jobPath)
        {
            string a = NormalisePath(jobPath);
            if (string.Equals(a, NormalisePath(Path), StringComparison.OrdinalIgnoreCase)) return true;
            if (IsCloud && CloudProjectGuid != null && CloudModelGuid != null &&
                string.Equals(a, "cloud:" + CloudProjectGuid + "/" + CloudModelGuid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// Tracks open documents, their change counters and the cached identity of the active document (for ping,
    /// which must not touch the API). All event handlers run on Revit's main thread.
    /// </summary>
    internal sealed class DocumentTracker
    {
        private sealed class Entry
        {
            public Entry(Document doc) { Doc = doc; Changes = Interlocked.Increment(ref _clock); }
            public Document Doc;
            public long Changes;
            /// <summary>Fingerprints still accepted for the current state (set after a rehearsal).</summary>
            public HashSet<string> Equivalent = new HashSet<string>(StringComparer.Ordinal);
            public string? EquivalentFor;
        }

        private static long _clock;
        private readonly object _gate = new object();
        private readonly List<Entry> _entries = new List<Entry>();
        private Document? _activeDoc;
        private readonly string _session = Guid.NewGuid().ToString("N").Substring(0, 8);
        private volatile DocumentIdentity? _active;
        private Document? _suspended;
        private Action<DocumentChangedEventArgs>? _jobSink;

        public string Session => _session;

        /// <summary>The active document's identity as of the last event; may be a moment stale.</summary>
        public DocumentIdentity? ActiveIdentity => _active;

        public void Attach(ControlledApplication app)
        {
            app.DocumentChanged += OnChanged;
            app.DocumentClosing += OnClosing;
            app.DocumentOpened += (s, e) => Safe(() => Touch(e.Document));
            app.DocumentCreated += (s, e) => Safe(() => Touch(e.Document));
            app.DocumentSaved += (s, e) => Safe(() => Touch(e.Document));
            app.DocumentSavedAs += (s, e) => Safe(() => Touch(e.Document));
            app.DocumentSynchronizedWithCentral += (s, e) => Safe(() => Touch(e.Document));
        }

        public void Detach(ControlledApplication app)
        {
            try { app.DocumentChanged -= OnChanged; app.DocumentClosing -= OnClosing; } catch (Exception) { }
        }

        public void OnViewActivated(Document? doc)
        {
            Safe(() =>
            {
                if (doc is null || doc.IsLinked) return;
                _activeDoc = doc;
                _active = Compute(doc);
            });
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception e) { Log.Warn("Document event handler: " + e.Message); }
        }

        private Entry Get(Document doc)
        {
            lock (_gate)
            {
                foreach (var e in _entries)
                    if (e.Doc.Equals(doc)) return e;
                var n = new Entry(doc);
                _entries.Add(n);
                return n;
            }
        }

        private void Touch(Document doc)
        {
            if (doc.IsLinked) return;
            Get(doc);
            RefreshActiveIf(doc);
        }

        private void RefreshActiveIf(Document doc)
        {
            var active = _activeDoc;
            if (active != null && !active.Equals(doc)) return;
            _activeDoc = doc;
            _active = Compute(doc);
        }

        private void OnClosing(object? sender, DocumentClosingEventArgs e)
        {
            Safe(() =>
            {
                lock (_gate) _entries.RemoveAll(x => x.Doc.Equals(e.Document));
                if (_activeDoc != null && _activeDoc.Equals(e.Document)) { _activeDoc = null; _active = null; }
            });
        }

        private void OnChanged(object? sender, DocumentChangedEventArgs e)
        {
            Safe(() =>
            {
                var doc = e.GetDocument();
                if (doc is null || doc.IsLinked) return;
                var sus = _suspended;
                if (sus != null && sus.Equals(doc))
                {
                    _jobSink?.Invoke(e);
                    return;
                }
                var entry = Get(doc);
                lock (_gate) entry.Changes = Interlocked.Increment(ref _clock);
                RefreshActiveIf(doc);
            });
        }

        /// <summary>While a job runs, its own transactions do not count as outside changes; they go to the sink.</summary>
        public void Suspend(Document doc, Action<DocumentChangedEventArgs> sink)
        {
            _suspended = doc;
            _jobSink = sink;
        }

        /// <summary>
        /// Ends a job. A committed job counts as one change. A rolled-back job leaves the counter alone, and the
        /// fingerprint from before it stays accepted for the resulting state.
        /// </summary>
        public void Resume(Document doc, bool committed, string fingerprintBefore)
        {
            _suspended = null;
            _jobSink = null;
            var entry = Get(doc);
            if (committed)
            {
                lock (_gate) { entry.Changes = Interlocked.Increment(ref _clock); entry.Equivalent.Clear(); entry.EquivalentFor = null; }
            }
            else
            {
                string now = Compute(doc).Fingerprint;
                lock (_gate)
                {
                    if (entry.EquivalentFor != now) { entry.Equivalent.Clear(); entry.EquivalentFor = now; }
                    if (now != fingerprintBefore) entry.Equivalent.Add(fingerprintBefore);
                }
            }
            RefreshActiveIf(doc);
        }

        /// <summary>True when the job's fingerprint names the document's current state.</summary>
        public bool FingerprintMatches(Document doc, string current, string jobFingerprint)
        {
            if (string.Equals(current, jobFingerprint, StringComparison.Ordinal)) return true;
            var entry = Get(doc);
            lock (_gate) return entry.EquivalentFor == current && entry.Equivalent.Contains(jobFingerprint);
        }

        public static string IdentityPath(Document doc)
        {
            try
            {
                if (doc.IsModelInCloud)
                {
                    var mp = doc.GetCloudModelPath();
                    if (mp != null) return ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }
            }
            catch (Exception) { }
            string p = doc.PathName ?? "";
            return p.Length > 0 ? p : "unsaved:" + doc.Title;
        }

        public DocumentIdentity Compute(Document doc)
        {
            var id = new DocumentIdentity
            {
                Title = doc.Title ?? "",
                Path = IdentityPath(doc),
                IsWorkshared = doc.IsWorkshared,
                IsFamily = doc.IsFamilyDocument,
                IsReadOnly = doc.IsReadOnly,
                IsLinked = doc.IsLinked,
            };
            try { id.IsCloud = doc.IsModelInCloud; } catch (Exception) { }
            if (id.IsCloud)
            {
                try
                {
                    var mp = doc.GetCloudModelPath();
                    id.CloudProjectGuid = mp.GetProjectGUID().ToString("D");
                    id.CloudModelGuid = mp.GetModelGUID().ToString("D");
                }
                catch (Exception) { }
                try { id.CloudUrn = doc.GetCloudModelUrn(); } catch (Exception) { }
            }
            if (id.IsWorkshared)
            {
                try
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null) id.CentralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
                catch (Exception) { }
            }
            try
            {
                var v = Document.GetDocumentVersion(doc);
                if (v != null) { id.VersionGuid = v.VersionGUID.ToString("N"); id.Saves = v.NumberOfSaves; }
            }
            catch (Exception) { }
            var entry = Get(doc);
            lock (_gate) id.Changes = entry.Changes;
            id.Fingerprint = "r1:" + _session + ":" + id.VersionGuid + ":" + id.Saves.ToString(CultureInfo.InvariantCulture) + ":" +
                             id.Changes.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        /// <summary>Open, non-linked documents whose identity matches the job's path.</summary>
        public List<Document> FindByPath(Application app, string jobPath)
        {
            var list = new List<Document>();
            foreach (Document d in app.Documents)
            {
                if (d.IsLinked) continue;
                if (Compute(d).Matches(jobPath)) list.Add(d);
            }
            return list;
        }
    }
}
