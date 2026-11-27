using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using SA.Revit.Host;

namespace SA.Revit.Ops
{
    /// <summary>
    /// A handler that changes the job's document through one Revit call that must run with no transaction open
    /// (Document.LoadFamily from an edited family document). The runner keeps the job's transaction group open but
    /// starts no Transaction for the op; Execute must leave the document with no open transaction, and every change it
    /// makes to the job's document must come from that call, so the group still holds it as one undo step. Deletion
    /// guard, dialog guard and readback apply as for any op. The op must be alone in its job (JobValidator.OpNotAlone).
    /// </summary>
    internal interface IOutsideTransactionOp
    {
    }

    /// <summary>
    /// Closes, without saving, the documents such ops open (EditFamily copies). Document.Close can refuse: it throws while
    /// API transactions or groups are open, and returns false when another add-in cancels the close in DocumentClosing.
    /// A copy that did not close is kept here and closed again by the runner once the job's transaction group has ended.
    /// Main thread only.
    /// </summary>
    internal static class OutsideDocuments
    {
        private static readonly List<Document> LeftOpen = new List<Document>();

        public static void Close(Document doc)
        {
            if (TryClose(doc, out string why)) return;
            Log.Warn("A document opened by the job did not close (" + why + "); it is closed again after the job");
            LeftOpen.Add(doc);
        }

        /// <summary>Closes every document an earlier close left open; returns a line for each one that is still open.</summary>
        public static List<string> CloseLeftOpen()
        {
            var still = new List<string>();
            for (int i = LeftOpen.Count - 1; i >= 0; i--)
            {
                var doc = LeftOpen[i];
                string title = "";
                try { title = doc.IsValidObject ? doc.Title : ""; } catch (Exception) { }
                if (TryClose(doc, out string why)) { LeftOpen.RemoveAt(i); continue; }
                still.Add("'" + title + "' (" + why + ")");
            }
            return still;
        }

        private static bool TryClose(Document doc, out string why)
        {
            why = "";
            try
            {
                if (!doc.IsValidObject) return true;
                if (doc.Close(false)) return true;
                why = "Revit returned false; another add-in may have cancelled the close";
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            return false;
        }
    }
}
