using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SA.Core.Json;
using SA.Core.Protocol;

namespace SA.Revit.Host
{
    /// <summary>
    /// Picks the document for read-only methods (snapshot, audit, capture): params.document.path when given
    /// (it must match exactly one open document), else the active document.
    /// </summary>
    internal static class DocResolve
    {
        public static Document? For(Engine engine, UIApplication app, Request req, out Response? failure)
        {
            failure = null;
            string? path = (req.Params["document"] as JsonObject)?.GetStringOrNull("path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var m = engine.Tracker.FindByPath(app.Application, path!);
                if (m.Count == 1) return m[0];
                failure = Response.Failure(req.Id, ErrorCodes.WrongDocument,
                    m.Count == 0 ? "The document '" + path + "' is not open." : "Several open documents match '" + path + "'.");
                return null;
            }
            var doc = app.ActiveUIDocument?.Document;
            if (doc is null) failure = Response.Failure(req.Id, ErrorCodes.WrongDocument, "No document is open.");
            return doc;
        }
    }
}
