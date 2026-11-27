using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Revit.Host;
using SA.Revit.Ops.Support;

namespace SA.Revit.Capture
{
    /// <summary>
    /// The capture method. Params: target ("activeView", a view id, a view name, "sheet:&lt;number&gt;"), out (absolute
    /// file path; default the jobs folder), format (png|jpg|pdf, default from the file extension), pixelWidth
    /// (default 2000), overwrite (default true: the client chose the file), document.path.
    /// Never opens a transaction. The active view id is compared before and after; if Revit changed it, it is set
    /// back and the response says so.
    /// </summary>
    internal sealed class CaptureMethod
    {
        private readonly Engine _engine;

        public CaptureMethod(Engine engine)
        {
            _engine = engine;
        }

        public Response Run(UIApplication app, Request req)
        {
            var doc = DocResolve.For(_engine, app, req, out var failure);
            if (doc is null) return failure!;
            var p = req.Params;
            string target = p.GetStringOrNull("target") ?? "activeView";
            View? view;
            if (target == "activeView")
            {
                view = doc.ActiveView;
                if (view is null) return Response.Failure(req.Id, ErrorCodes.BadRequest, "The document has no active view.");
            }
            else
            {
                var f = Lookup.View(doc, p["target"]);
                if (!f.Ok) return Response.Failure(req.Id, ErrorCodes.BadRequest, f.Error ?? "target not found");
                view = f.Value!;
            }
            string? why = CaptureService.CannotExport(view);
            if (why != null) return Response.Failure(req.Id, ErrorCodes.BadRequest, why);

            string? outPath = p.GetStringOrNull("out");
            bool defaultPath = string.IsNullOrWhiteSpace(outPath);
            if (!defaultPath && !Path.IsPathRooted(outPath)) return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.out must be an absolute path.");
            string format = (p.GetStringOrNull("format") ?? (defaultPath ? "png" : Path.GetExtension(outPath!).TrimStart('.'))).ToLowerInvariant();
            if (format == "jpeg") format = "jpg";
            if (format != "png" && format != "jpg" && format != "pdf")
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "format must be png, jpg or pdf.");
            if (defaultPath)
                outPath = Path.Combine(SaPaths.Root(), "captures", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + CaptureService.SafeFileName(view.Name) + "." + format);
            int width = p["pixelWidth"] is JsonNumber n && n.TryGetInt64(out long w) ? (int)w : 2000;
            if (width < CaptureService.MinPixels || width > CaptureService.MaxPixels)
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "pixelWidth must be " + CaptureService.MinPixels + " to " + CaptureService.MaxPixels + ".");
            bool overwrite = !(p["overwrite"] is JsonBool ob) || ob.Value;

            var uidoc = app.ActiveUIDocument;
            bool isActiveDoc = uidoc != null && uidoc.Document.Equals(doc);
            ElementId? activeBefore = isActiveDoc ? uidoc!.ActiveView?.Id : null;
            CaptureResult res;
            try
            {
                res = format == "pdf"
                    ? CaptureService.ExportPdf(doc, new List<ElementId> { view.Id }, outPath!, overwrite)
                    : CaptureService.ExportImage(doc, view, outPath!, format == "jpg", width, overwrite);
            }
            catch (Exception e)
            {
                return Response.Failure(req.Id, ErrorCodes.OpFailed, "Export failed: " + e.Message);
            }
            bool restored = false;
            if (isActiveDoc && activeBefore != null)
            {
                var now = uidoc!.ActiveView?.Id;
                if (now != null && now != activeBefore && doc.GetElement(activeBefore) is View back)
                {
                    try { uidoc.ActiveView = back; restored = true; }
                    catch (Exception e) { Log.Warn("Could not restore the active view: " + e.Message); }
                }
            }
            if (res.Bytes <= 0) return Response.Failure(req.Id, ErrorCodes.OpFailed, "The exported file is missing, empty or not a " + format + " file: " + res.Path);
            var o = res.ToJson().Add("target", target).Add("view", new JsonObject().Add("id", Compat.ElementIds.ToWire(view.Id)).Add("name", view.Name).Add("type", view.ViewType.ToString()))
                .Add("activeViewRestored", restored);
            if (format != "pdf" && res.Width.HasValue && Math.Abs(res.Width.Value - width) > 2)
                o.Add("warning", "image is " + res.Width + " px wide, requested " + width);
            return Response.Success(req.Id, new JsonObject().Add("artifacts", new JsonArray(o)));
        }
    }
}
