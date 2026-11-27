using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Sheets
{
    /// <summary>
    /// xref.attach: Database.OverlayXref (default) or Database.AttachXref creates the xref block definition; a block
    /// reference in model space places it at the position, rotation and scale given (0,0,0, 0 degrees, 1 by default)
    /// on the standard's xref layer. Stored path: relative to the host drawing by default (an unsaved host, or a file
    /// on another drive, falls back to the full path with a warning); "none" stores the file name only.
    ///
    /// Units: a reference created through the API is not scaled for units the way the XATTACH command does it, so
    /// the engine applies the factor itself: (millimetres per xref unit) / (millimetres per host unit), times the job's
    /// scale. The xref's INSUNITS is read from a side database during validation. A unitless xref gets factor 1 and a
    /// warning. The factor is reported.
    /// </summary>
    internal sealed class XrefAttach : OpHandler<XrefAttach.Plan>
    {
        public override string Name => "xref.attach";

        internal sealed class Plan
        {
            public string FullPath = "";
            public string BlockName = "";
            public bool Overlay = true;
            public string StoredPath = "";
            public string PathType = "";
            public Point2 Position;
            public double Rotation;
            public double Scale = 1;
            public double UnitFactor = 1;
            public int XrefUnits;
            public string Layer = "";
            public ObjectId DefId;
            public ObjectId RefId;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            if (p != null) ctx.Plan.Add(PlannedNames.Block, p.BlockName);
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            string path = args.GetString("path");
            string? host = SheetDb.DrawingFolder(ctx);
            string? full = Path.IsPathRooted(path) ? path : host != null ? Path.Combine(host, path) : null;
            if (full is null || !SheetDb.FileExists(full))
            {
                OpContext.Error(issues, args, "path", "xref_file_missing", "'" + path + "' does not exist" + (host is null ? " (a relative path needs a saved host drawing)" : ""));
                return null;
            }
            p.FullPath = Path.GetFullPath(full);
            if (!string.Equals(Path.GetExtension(p.FullPath), ".dwg", StringComparison.OrdinalIgnoreCase))
            {
                OpContext.Error(issues, args, "path", "xref_not_dwg", "external references must be .dwg files");
                return null;
            }
            if (ctx.Document.IsNamedDrawing && SheetDb.SamePath(p.FullPath, ctx.Document.Name))
            {
                OpContext.Error(issues, args, "path", "xref_self", "a drawing cannot reference itself");
                return null;
            }
            p.BlockName = args.GetStringOrNull("name") ?? Path.GetFileNameWithoutExtension(p.FullPath);
            if (!AcadValues.IsValidSymbolName(p.BlockName, out string nameError))
            {
                OpContext.Error(issues, args, "name", IssueCodes.NameInvalid, nameError);
                return null;
            }
            if (SheetDb.BlockExists(ctx, p.BlockName))
            {
                OpContext.Error(issues, args, "name", "block_exists", "a block or reference named '" + p.BlockName + "' already exists; give another name or use xref.repath");
                return null;
            }
            p.Overlay = args.GetString("mode") == "overlay";
            if (!p.Overlay && ctx.IsValidating)
                OpContext.Warning(issues, args, "mode", "xref_attach_mode", "attach carries this reference into any drawing that references the host; overlay is the default for background references");

            p.PathType = args.GetString("pathType");
            if (p.PathType == "relative")
            {
                string? rel = host is null ? null : SheetDb.RelativePath(host, p.FullPath);
                if (rel is null)
                {
                    p.PathType = "full";
                    if (ctx.IsValidating)
                        OpContext.Warning(issues, args, "pathType", "xref_path_full", host is null ? "the host drawing is not saved, so the path is stored in full; save it and run xref.repath" : "the xref is on another drive or server; relative paths are not available, so the path is stored in full");
                }
                else p.StoredPath = rel;
            }
            if (p.PathType == "full") p.StoredPath = p.FullPath;
            if (p.PathType == "none") p.StoredPath = Path.GetFileName(p.FullPath);

            p.Position = args.GetPoint2("position");
            p.Rotation = args.GetAngle("rotation");
            p.Scale = args.GetNumber("scale");
            if (ctx.IsValidating && (p.Position.X != 0 || p.Position.Y != 0 || p.Rotation != 0 || p.Scale != 1))
                OpContext.Warning(issues, args, "position", "xref_not_at_origin", "references normally go at 0,0,0, rotation 0, scale 1 on a shared origin");

            p.XrefUnits = SideDatabase.Insunits(p.FullPath);
            double? xmm = DrawingUnits.MillimetresPer(p.XrefUnits);
            double? hmm = ctx.Units.MmPerUnit;
            if (xmm.HasValue && hmm.HasValue) p.UnitFactor = xmm.Value / hmm.Value;
            else if (ctx.IsValidating)
                OpContext.Warning(issues, args, "path", "xref_unitless", "the referenced drawing's INSUNITS is " + DrawingUnits.NameOf(p.XrefUnits) + "; it is inserted without unit scaling (factor 1). Check it overlays at the right size.");
            if (ctx.IsValidating && Math.Abs(p.UnitFactor - 1) > 1e-12 && xmm.HasValue)
                OpContext.Warning(issues, args, "scale", "xref_unit_scaled", "the reference is in " + DrawingUnits.NameOf(p.XrefUnits) + " and the host in " + ctx.Units.Name + "; it is scaled by " + p.UnitFactor);

            var layer = StandardLayers.Resolve(ctx, args, "layer", StandardLayers.Xref(ctx.Standard), "external references", issues);
            if (layer is null) return null;
            if (SheetDb.IsProtectedLayer(layer))
            {
                OpContext.Error(issues, args, "layer", "layer_not_allowed", "references are not placed on layer 0 or Defpoints; use the xref layer");
                return null;
            }
            p.Layer = layer;
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            if (p is null) throw new InvalidOperationException(Name + ": the reference could not be planned.");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            p.DefId = p.Overlay ? db.OverlayXref(p.FullPath, p.BlockName) : db.AttachXref(p.FullPath, p.BlockName);
            if (p.DefId.IsNull) throw new InvalidOperationException("AutoCAD did not create the reference '" + p.BlockName + "'.");
            var def = (BlockTableRecord)tr.GetObject(p.DefId, OpenMode.ForWrite);
            if (!string.Equals(def.PathName, p.StoredPath, StringComparison.OrdinalIgnoreCase)) def.PathName = p.StoredPath;
            result.Created.Add(OpContext.HandleOf(p.DefId));

            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var br = new BlockReference(ctx.Units.ToDrawing(p.Position), p.DefId);
            br.SetDatabaseDefaults(db);
            br.Layer = p.Layer;
            br.Rotation = p.Rotation * Math.PI / 180.0;
            br.ScaleFactors = new Scale3d(p.Scale * p.UnitFactor);
            p.RefId = Db.Append(ctx, ms, br);
            result.Created.Add(OpContext.HandleOf(p.RefId));
            result.Outputs.Add("reference", OpContext.HandleOf(p.RefId));
            result.Outputs.Add("storedPath", p.StoredPath);
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var def = (BlockTableRecord)tr.GetObject(p.DefId, OpenMode.ForRead);
            var br = (BlockReference)tr.GetObject(p.RefId, OpenMode.ForRead);
            var pos = ctx.Units.ToJob(br.Position);
            double scale = br.ScaleFactors.X;
            result.Readback
                .Add("name", def.Name)
                .Add("isXref", def.IsFromExternalReference)
                .Add("mode", def.IsFromOverlayReference ? "overlay" : "attach")
                .Add("storedPath", def.PathName ?? "")
                .Add("pathType", p.PathType)
                .Add("status", def.XrefStatus.ToString())
                .Add("layer", br.Layer)
                .Add("position", DrawingUnits.PointJson(pos))
                .Add("rotation", AcadValues.Round(br.Rotation * 180.0 / Math.PI))
                .Add("scale", AcadValues.Round(scale))
                .Add("unitFactor", AcadValues.Round(p.UnitFactor, 12))
                .Add("xrefUnits", DrawingUnits.NameOf(p.XrefUnits))
                .Add("hostUnits", ctx.Units.Name);
            bool ok = true;
            if (!def.IsFromExternalReference) ok = Mismatch(result, "'" + def.Name + "' is not an external reference");
            if (def.IsFromOverlayReference != p.Overlay) ok = Mismatch(result, "the reference mode reads back " + (def.IsFromOverlayReference ? "overlay" : "attach"));
            if (!string.Equals(def.PathName, p.StoredPath, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "the stored path reads back '" + def.PathName + "', expected '" + p.StoredPath + "'");
            if (def.XrefStatus != XrefStatus.Resolved) ok = Mismatch(result, "the reference is " + def.XrefStatus + " after attaching");
            if (!string.Equals(br.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "the reference is on layer '" + br.Layer + "'");
            if (Math.Abs(pos.X - p.Position.X) > ctx.LengthTolerance || Math.Abs(pos.Y - p.Position.Y) > ctx.LengthTolerance || Math.Abs(br.Position.Z) > 1e-9)
                ok = Mismatch(result, "the reference position reads back differently");
            if (!SA.Core.Units.Tolerance.AngleEqual(br.Rotation * 180.0 / Math.PI, p.Rotation)) ok = Mismatch(result, "the rotation reads back differently");
            double want = p.Scale * p.UnitFactor;
            if (Math.Abs(scale - want) > 1e-9 * want || Math.Abs(br.ScaleFactors.Y - want) > 1e-9 * want || Math.Abs(br.ScaleFactors.Z - want) > 1e-9 * want)
                ok = Mismatch(result, "the scale reads back " + scale + ", expected " + want);
            return ok;
        }
    }
}
