using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// link.revit (Revit): RevitLinkType.Create with RevitLinkOptions(relative, WorksetConfiguration), then
    /// RevitLinkInstance.Create. RevitLinkType.Create refuses a path that is already linked, so an existing loaded link
    /// type for the same file is reused (typeCreated false); a further instance needs allowDuplicate. An unloaded link
    /// type for the file is refused: reload it first.
    ///
    /// RevitLinkInstance.Create supports only origin and shared placement; placement point places origin to origin,
    /// then moves the instance to point/elevation and rotates it about the vertical through that point.
    ///
    /// coordinates acquire: Document.AcquireCoordinates(instance). The API documents that, unlike the UI, it always
    /// overwrites the host's geolocation, and removes it when the link has none; so acquire is refused when the host
    /// has a geographic coordinate system and the link's is empty or different, and when the link type is placed more
    /// than once (documented as refused). The active ProjectLocation, the SiteLocation and the survey point are declared.
    /// Publishing is not offered: Document.PublishCoordinates records the position in the linked model, outside this
    /// job's undo step and ownership check, and Revit then asks to save that file.
    /// Read back: the shared position of the link's origin, measured in the host's active location, must equal the
    /// linked model's own shared position of its origin.
    /// </summary>
    public sealed class LinkRevit : OpHandler
    {
        public override string Name => "link.revit";

        private static RevitLinkType? ExistingType(Document doc, string path) =>
            new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .FirstOrDefault(t => LinkCad.SamePath(LinkCad.LinkedPath(t), path));

        private static List<RevitLinkInstance> InstancesOf(Document doc, ElementId typeId) =>
            new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().Where(i => i.GetTypeId() == typeId).ToList();

        private static RevitLinkType? Build(OpContext ctx, OpArgs args, List<Issue> issues, out bool reuse)
        {
            reuse = false;
            var doc = ctx.Doc;
            if (doc.IsFamilyDocument) { OpContext.Error(issues, args.Path, RevitIssueCodes.NotAllowed, "Revit links models into project documents only"); return null; }
            string path = args.GetString("path");
            if (LinkCad.FileProblem(path, ".rvt") is string bad) { OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.NotFound, bad); return null; }
            if (!string.IsNullOrEmpty(doc.PathName) && LinkCad.SamePath(doc.PathName, path)) { OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.Invalid, "a model cannot link itself"); return null; }
            try { BasicFileInfo.Extract(path); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.Invalid, path + " cannot be read as a Revit model: " + e.Message); return null; }
            if (args.GetString("placement") == "point" && !args.Has("point")) OpContext.Error(issues, ctx.FieldPath(args, "point"), RevitIssueCodes.Invalid, "placement point needs point");
            if (args.GetString("placement") != "point" && (args.Has("elevation") && args.GetLength("elevation") != 0 || args.Has("rotation") && args.GetAngle("rotation") != 0))
                OpContext.Error(issues, ctx.FieldPath(args, "placement"), RevitIssueCodes.Invalid, "elevation and rotation are used only with placement point");
            var type = ExistingType(doc, path);
            if (type != null)
            {
                if (!RevitLinkType.IsLoaded(doc, type.Id)) { OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.NotAllowed, path + " is linked as type '" + type.Name + "' but unloaded; reload it first"); return null; }
                var inst = InstancesOf(doc, type.Id);
                if (inst.Count > 0 && !args.GetBool("allowDuplicate"))
                    OpContext.Error(issues, ctx.FieldPath(args, "path"), RevitIssueCodes.Exists, path + " is already linked (instance " + Kit.Id(inst[0].Id) + "); set allowDuplicate:true to add another");
                reuse = true;
                if (inst.Count > 0 && args.GetString("coordinates") == "acquire")
                    OpContext.Error(issues, ctx.FieldPath(args, "coordinates"), RevitIssueCodes.NotAllowed, "Revit cannot acquire coordinates from a model placed more than once; " + path + " is already placed");
            }
            if (args.GetString("coordinates") == "acquire")
            {
                ctx.DeclareModifies(doc.ActiveProjectLocation.Id);
                ctx.DeclareModifies(doc.SiteLocation.Id);
                if (BasePoint.GetSurveyPoint(doc) is BasePoint survey) ctx.DeclareModifies(survey.Id);
            }
            return Kit.HasErrors(issues) ? null : type;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues, out _);

        private static WorksetConfiguration Worksets(string mode) =>
            new WorksetConfiguration(mode == "none" ? WorksetConfigurationOption.CloseAllWorksets : mode == "last_viewed" ? WorksetConfigurationOption.OpenLastViewed : WorksetConfigurationOption.OpenAllWorksets);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var type = Build(ctx, args, issues, out bool reuse);
            Kit.Require(issues);
            var doc = ctx.Doc;
            string path = args.GetString("path");
            ElementId typeId;
            if (reuse) typeId = type!.Id;
            else
            {
                LinkLoadResult load;
                try { load = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(path), new RevitLinkOptions(args.GetBool("relative"), Worksets(args.GetString("worksets")))); }
                catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("link_failed", "Revit refused to link " + path + ": " + e.Message); }
                if (!LinkLoadResult.IsCodeSuccess(load.LoadResult) || !ElementIds.IsValid(load.ElementId))
                    throw new OpFailedException("link_failed", "Revit did not load " + path + " (" + load.LoadResult + ")");
                typeId = load.ElementId;
                result.Created.Add(Kit.Id(typeId));
            }

            string placement = args.GetString("placement");
            RevitLinkInstance inst;
            try { inst = RevitLinkInstance.Create(doc, typeId, placement == "shared" ? ImportPlacement.Shared : ImportPlacement.Origin); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("link_failed", "Revit refused to place the link" + (placement == "shared" ? " by shared coordinates" : "") + ": " + e.Message); }
            if (placement == "point")
            {
                var at = ctx.Units.PointToInternal(args.GetPoint2("point"), ctx.Units.LengthToInternal(args.GetLength("elevation")));
                ElementTransformUtils.MoveElement(doc, inst.Id, at);
                double rot = ctx.Units.AngleToInternal(args.GetAngle("rotation"));
                if (Math.Abs(rot) > 1e-12) ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(at, at + XYZ.BasisZ), rot);
                result.Readback.Add("origin", ctx.Units.PointJson(at));
                result.Readback.Add("elevation", ctx.Units.LengthJson(at.Z));
            }

            if (args.GetString("coordinates") == "acquire")
            {
                doc.Regenerate();
                var linked = inst.GetLinkDocument() ?? throw new OpFailedException("link_failed", "the linked model is not loaded; cannot acquire coordinates");
                string hostGeo = doc.SiteLocation?.GeoCoordinateSystemId ?? "";
                string linkGeo = linked.SiteLocation?.GeoCoordinateSystemId ?? "";
                if (hostGeo.Length > 0 && !string.Equals(hostGeo, linkGeo, StringComparison.OrdinalIgnoreCase))
                    throw new OpFailedException("geolocation_conflict", "acquiring coordinates would " + (linkGeo.Length == 0 ? "remove" : "replace") + " this model's geographic coordinate system '" + hostGeo + "'" +
                        (linkGeo.Length == 0 ? " (the linked model has none)" : " with '" + linkGeo + "'") + "; the API overwrites it without asking. Do this in Revit if it is intended.");
                try { doc.AcquireCoordinates(inst.Id); }
                catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("coordinates_failed", "Revit refused to acquire coordinates: " + e.Message); }
                result.Modified.Add(Kit.Id(doc.ActiveProjectLocation.Id));
                result.Modified.Add(Kit.Id(doc.SiteLocation!.Id));
                result.Readback.Add("geoCoordinateSystemBefore", hostGeo);
            }
            if (args.GetBool("pin")) inst.Pinned = true;
            result.Created.Add(Kit.Id(inst.Id));
            result.Outputs.Add("instance", Kit.Id(inst.Id));
            result.Outputs.Add("linkType", Kit.Id(typeId));
            result.Outputs.Add("typeCreated", !reuse);
        }

        private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var rb = result.Readback;
            if (!ElementIds.TryParse(result.Outputs.GetStringOrNull("instance"), out var id) || !(doc.GetElement(id) is RevitLinkInstance inst))
            {
                check.Fail("the link instance does not exist after the op");
                check.Apply(result);
                return;
            }
            string path = args.GetString("path");
            string? have = LinkCad.LinkedPath(doc.GetElement(inst.GetTypeId()));
            check.Text(rb, "path", LinkCad.SamePath(have, path) ? path : have, path);
            check.Flag(rb, "loaded", RevitLinkType.IsLoaded(doc, inst.GetTypeId()), true);
            check.Flag(rb, "pinned", inst.Pinned, args.GetBool("pin"));
            var t = inst.GetTotalTransform();
            if (args.GetString("placement") == "point")
            {
                check.Point(rb, "origin", t.Origin, args.GetPoint2("point"));
                check.Length(rb, "elevation", t.Origin.Z, args.GetLength("elevation"));
                double rot = u.AngleFromInternal(Math.Atan2(t.BasisX.Y, t.BasisX.X));
                rb.Set("rotation", RevitUnits.Num(rot));
                double d = Math.Abs(Math.IEEERemainder(rot - args.GetAngle("rotation"), 360));
                if (d > 1e-6) check.Fail("rotation: read back " + Fmt(rot) + ", requested " + Fmt(args.GetAngle("rotation")) + " degrees");
            }
            else if (args.GetString("placement") == "origin" && t.Origin.GetLength() > u.LengthToleranceInternal) check.Fail("the link's origin is not at this model's origin");

            if (rb.GetStringOrNull("geoCoordinateSystemBefore") is string geoBefore && geoBefore.Length > 0)
                check.Text(rb, "geoCoordinateSystem", doc.SiteLocation?.GeoCoordinateSystemId ?? "", geoBefore, "geographic coordinate system");
            var linked = inst.GetLinkDocument();
            if (args.GetString("placement") == "shared" || args.GetString("coordinates") != "none")
            {
                if (linked is null) check.Fail("the linked model is not loaded; shared coordinates not read back");
                else
                {
                    var host = doc.ActiveProjectLocation.GetProjectPosition(t.Origin);
                    var own = linked.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                    var o = new JsonObject()
                        .Add("hostEastWest", u.LengthJson(host.EastWest)).Add("hostNorthSouth", u.LengthJson(host.NorthSouth)).Add("hostElevation", u.LengthJson(host.Elevation))
                        .Add("linkEastWest", u.LengthJson(own.EastWest)).Add("linkNorthSouth", u.LengthJson(own.NorthSouth)).Add("linkElevation", u.LengthJson(own.Elevation));
                    rb.Set("sharedPosition", o);
                    double tol = u.LengthToleranceInternal;
                    if (Math.Abs(host.EastWest - own.EastWest) > tol || Math.Abs(host.NorthSouth - own.NorthSouth) > tol || Math.Abs(host.Elevation - own.Elevation) > tol)
                        check.Fail("shared coordinates differ: the link's origin is at E " + Fmt(u.LengthFromInternal(host.EastWest)) + " N " + Fmt(u.LengthFromInternal(host.NorthSouth)) +
                                   " in this model, but at E " + Fmt(u.LengthFromInternal(own.EastWest)) + " N " + Fmt(u.LengthFromInternal(own.NorthSouth)) + " in the linked model");
                }
            }
            check.Apply(result);
        }
    }
}
