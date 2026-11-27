using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Host
{
    internal sealed class FailureRecord
    {
        public string Severity = "";
        public string Description = "";
        public string DefinitionId = "";
        public List<string> Elements = new List<string>();

        public JsonObject ToJson() => new JsonObject().Add("severity", Severity).Add("description", Description)
            .Add("failureDefinitionId", DefinitionId).Add("elements", JsonArray.FromStrings(Elements));

        public string Text() => Description + (Elements.Count > 0 ? " [elements " + string.Join(", ", Elements) + "]" : "") +
                                " (" + DefinitionId + ")";
    }

    /// <summary>
    /// Installed on every transaction the engine commits. Every warning is recorded and then deleted (so Revit has
    /// nothing to show); any error is recorded and the transaction is rolled back with failures cleared, so no
    /// error dialog appears. Nothing is resolved on the user's behalf and nothing is cleared unrecorded.
    /// Documented in the API reference for FailureProcessingResult.ProceedWithRollBack: to roll back silently,
    /// set ClearAfterRollback in the accessor's options before returning it.
    /// </summary>
    internal sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<FailureRecord> Warnings { get; } = new List<FailureRecord>();
        public List<FailureRecord> Errors { get; } = new List<FailureRecord>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool errors = false;
            try
            {
                foreach (var f in accessor.GetFailureMessages())
                {
                    var rec = Describe(f);
                    var sev = f.GetSeverity();
                    if (sev == FailureSeverity.Warning)
                    {
                        Warnings.Add(rec);
                        accessor.DeleteWarning(f);
                    }
                    else
                    {
                        Errors.Add(rec);
                        errors = true;
                    }
                }
            }
            catch (Exception e)
            {
                Errors.Add(new FailureRecord { Severity = "engine", Description = "failure preprocessing threw: " + e.Message });
                errors = true;
            }
            if (!errors) return FailureProcessingResult.Continue;
            try
            {
                var opts = accessor.GetFailureHandlingOptions();
                opts.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(opts);
            }
            catch (Exception) { }
            return FailureProcessingResult.ProceedWithRollBack;
        }

        private static FailureRecord Describe(FailureMessageAccessor f)
        {
            var r = new FailureRecord();
            try { r.Severity = f.GetSeverity().ToString().ToLowerInvariant(); } catch (Exception) { }
            try { r.Description = f.GetDescriptionText() ?? ""; } catch (Exception) { }
            try { r.DefinitionId = f.GetFailureDefinitionId().Guid.ToString("D"); } catch (Exception) { }
            try { foreach (var id in f.GetFailingElementIds()) r.Elements.Add(ElementIds.ToWire(id)); } catch (Exception) { }
            try { foreach (var id in f.GetAdditionalElementIds()) r.Elements.Add(ElementIds.ToWire(id)); } catch (Exception) { }
            return r;
        }

        /// <summary>Options for a transaction: this preprocessor, silent rollback, and modal handling so Commit returns a final status, never Pending.</summary>
        public FailureHandlingOptions Install(Transaction t)
        {
            var opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(this);
            opts.SetClearAfterRollback(true);
            opts.SetForcedModalHandling(true);
            t.SetFailureHandlingOptions(opts);
            return opts;
        }
    }

    internal sealed class DialogRecord
    {
        public string Kind = "";
        public string DialogId = "";
        public string Message = "";
        public int Result;
        public bool Accepted;

        /// <summary>Whether the answer that Revit took was an affirmative one (OK, Yes) rather than cancel, close or no.</summary>
        public bool Affirmative;

        /// <summary>
        /// The report form. Accepted there means the dialog was answered OK or Yes; a dialog whose override Revit refused
        /// is marked in the message, since it may still be open.
        /// </summary>
        public ReportDialog ToReport() =>
            new ReportDialog(Kind, DialogId, Accepted ? Message : Message + " (Revit refused every answer the engine gave)")
            { DismissedWith = Result, Accepted = Accepted && Affirmative };
    }

    /// <summary>
    /// Subscribed to UIApplication.DialogBoxShowing only while the engine holds the API context. Each dialog is
    /// dismissed with the safest available answer (cancel, else close, else no, else OK) and recorded; the job runner
    /// treats any dialog during the op phase as a failure, because Revit asked a question no one answered.
    /// </summary>
    internal sealed class DialogGuard : IDisposable
    {
        private readonly UIApplication _app;
        public List<DialogRecord> Dialogs { get; } = new List<DialogRecord>();

        public DialogGuard(UIApplication app)
        {
            _app = app;
            _app.DialogBoxShowing += OnShowing;
        }

        private void OnShowing(object? sender, DialogBoxShowingEventArgs e)
        {
            var rec = new DialogRecord();
            try
            {
                rec.DialogId = e.DialogId ?? "";
                int[] answers;
                if (e is TaskDialogShowingEventArgs td)
                {
                    rec.Kind = "taskDialog";
                    rec.Message = td.Message ?? "";
                    answers = new[] { (int)TaskDialogResult.Cancel, (int)TaskDialogResult.Close, (int)TaskDialogResult.No, (int)TaskDialogResult.Ok };
                }
                else if (e is MessageBoxShowingEventArgs mb)
                {
                    rec.Kind = "messageBox";
                    rec.Message = mb.Message ?? "";
                    // Low four bits are the Win32 MB_* button set; answer with that set's cancel-like button.
                    switch (mb.DialogType & 0xF)
                    {
                        case 0: answers = new[] { 1 }; break;              // OK
                        case 2: answers = new[] { 3, 2 }; break;           // Abort/Retry/Ignore: Abort
                        case 4: answers = new[] { 7, 2 }; break;           // Yes/No: No
                        default: answers = new[] { 2, 7, 1 }; break;       // anything with Cancel
                    }
                }
                else
                {
                    rec.Kind = "dialogBox";
                    answers = new[] { 2 };                                 // any non-zero value dismisses a dialog box
                }
                foreach (int a in answers)
                {
                    bool ok;
                    try { ok = e.OverrideResult(a); } catch (Exception) { ok = false; }
                    rec.Result = a;
                    rec.Accepted = ok;
                    rec.Affirmative = rec.Kind == "taskDialog" ? a == (int)TaskDialogResult.Ok || a == (int)TaskDialogResult.Yes
                        : rec.Kind == "messageBox" && (a == 1 || a == 6);   // Win32 IDOK, IDYES
                    if (ok) break;
                }
            }
            catch (Exception ex) { rec.Message += " (guard error: " + ex.Message + ")"; }
            Dialogs.Add(rec);
            Log.Warn("Dismissed " + rec.Kind + " '" + rec.DialogId + "' with " + rec.Result + (rec.Accepted ? "" : " (not accepted)") + ": " + rec.Message);
        }

        public void Dispose()
        {
            try { _app.DialogBoxShowing -= OnShowing; } catch (Exception) { }
        }
    }
}
