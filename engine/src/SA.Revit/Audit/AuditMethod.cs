using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Core.Protocol;
using SA.Core.Units;
using SA.Revit.Host;
using SA.Revit.Ops.Support;

namespace SA.Revit.Audit
{
    /// <summary>
    /// The audit method. Read only; no transaction is opened.
    ///
    /// Params (Core's AuditOptions.FromParams, plus engine ones):
    /// - checks (ids), group or groups: which checks; none means every Revit check loaded.
    /// - checksFile (alias catalog): a project's checks file or folder, or a list; replaces the bundled revit.json
    ///   unless checksMode is "add". Relative paths resolve against the model's folder.
    /// - standard: a project standard file; else the bundled us-ncs-style (project length in ft or in) or
    ///   iso19650-uk-style (otherwise).
    /// - includeFacts (bool), tables (limit which facts tables are collected), units (lengths in facts without a unit
    ///   in their name; default mm), reviewStatus (default true: review-by-eye checks get status "review"; false keeps
    ///   Core's older "fail" with review: true), document.path (else the active document).
    ///
    /// Response: checks (every result: pass, fail, review, not_implemented or error; findings carry recordId = element
    /// id, or data.elementIds for warnings and groups), reviewByEye (the review items with their instructions),
    /// summary (CheckSummary: total, pass, fail, review, notImplemented, error), catalogs, standard {file, source},
    /// factCounts, factsMeta (units, truncation, omitted fields, collector errors), problems, and facts on request.
    /// </summary>
    internal static class AuditMethod
    {
        public static Response Run(Engine engine, UIApplication app, Request req)
        {
            var doc = DocResolve.For(engine, app, req, out var failure);
            if (doc is null) return failure!;
            var p = req.Params;
            var unit = LengthUnit.Millimetre;
            string? u = p.GetStringOrNull("units");
            if (u != null && !Length.TryParseUnit(u, out unit))
                return Response.Failure(req.Id, ErrorCodes.BadRequest, "params.units must be mm, cm, m, in or ft.");
            AuditOptions options;
            try { options = AuditOptions.FromParams(p); }
            catch (FormatException e) { return Response.Failure(req.Id, ErrorCodes.BadRequest, e.Message); }
            bool reviewStatus = !(p["reviewStatus"] is JsonBool rs) || rs.Value;
            var units = new RevitUnits(doc, unit);
            var onlyTables = p["tables"] is JsonArray ta ? ta.OfType<JsonString>().Select(s => s.Value).ToList() : null;

            var setup = engine.Audit.Load(options, doc, null, out string standardSource);
            var problems = new List<string>(setup.Problems);
            var facts = engine.Audit.Collect(doc, units, onlyTables);
            // A standard that failed to load is a problem (from Core's loader), never silently replaced by the default.
            if (setup.Standard is null && standardSource != "none") standardSource += ":not_loaded";
            var results = engine.Audit.Run(facts, setup.Catalog, options.CheckIds.Count > 0 ? options.CheckIds : null,
                options.Groups.Count > 0 ? options.Groups : null, setup.Standard, problems, reviewStatus, defaultStandard: false);

            CheckSummary.Split(results, out _, out var review);
            var counts = new JsonObject();
            foreach (var t in facts.Tables) counts.Add(t.Name, t.Records.Count);
            var o = new JsonObject()
                .Add("document", engine.Tracker.Compute(doc).ToJson())
                .Add("catalogs", JsonArray.FromStrings(setup.CatalogFiles))
                .Add("standard", new JsonObject().Add("file", setup.StandardFile is null ? JsonValue.Null : (JsonValue)setup.StandardFile).Add("source", standardSource))
                .Add("checks", new JsonArray(results.Select(r => (JsonValue)r.ToJson())))
                .Add("reviewByEye", new JsonArray(review.Select(r => (JsonValue)new JsonObject()
                    .Add("id", r.Id)
                    .Add("title", r.Title ?? "")
                    .Add("instructions", r.ReviewInstructions ?? ""))))
                .Add("summary", CheckSummary.Of(results).ToJson())
                .Add("factCounts", counts)
                .Add("factsMeta", facts.Meta)
                .Add("problems", JsonArray.FromStrings(problems))
                .Add("errors", JsonArray.FromStrings(problems));
            if (options.IncludeFacts) o.Add("facts", facts.ToJson());
            return Response.Success(req.Id, o);
        }
    }
}
