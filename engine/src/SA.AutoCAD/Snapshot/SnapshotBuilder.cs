using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Json;
using SA.Core.Protocol;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SA.AutoCAD.Snapshot
{
    /// <summary>
    /// The snapshot method: one compact JSON document describing the active drawing for a planner.
    /// params.sections narrows it (unknown names are rejected); params.maxItems caps every list (default 1000, at most
    /// 100000); a capped list is reported as { "count": n, "truncated": true, "items": [...] }, an uncapped one the same
    /// with truncated false. A section that fails is { "error": "..." } and does not fail the snapshot.
    /// Read-only: the document is locked for read and an open-close transaction is used, so nothing enters the undo list.
    /// Lengths are drawing units (document.units says which).
    /// </summary>
    internal static class SnapshotBuilder
    {
        internal static readonly string[] Sections =
        {
            "document", "sysvars", "layers", "linetypes", "textStyles", "dimStyles", "blocks", "xrefs", "layouts",
            "annotationScales", "extents", "selection",
        };

        /// <summary>System variables that matter for drafting decisions.</summary>
        internal static readonly string[] SysVars =
        {
            "INSUNITS", "MEASUREMENT", "LUNITS", "LUPREC", "AUNITS", "AUPREC", "ANGBASE", "ANGDIR",
            "CANNOSCALE", "ANNOAUTOSCALE", "ANNOALLVISIBLE", "MSLTSCALE", "LTSCALE", "PSLTSCALE", "CELTSCALE",
            "CLAYER", "CECOLOR", "CELTYPE", "CELWEIGHT", "CETRANSPARENCY", "DIMSTYLE", "DIMSCALE", "TEXTSTYLE", "TEXTSIZE",
            "CMLEADERSTYLE", "CTABLESTYLE", "CTAB", "TILEMODE", "UCSNAME", "ELEVATION", "THICKNESS",
            "HPNAME", "HPSCALE", "HPANG", "HPLAYER", "LWDISPLAY", "PSTYLEMODE", "XREFTYPE", "VISRETAIN",
            "EXTMIN", "EXTMAX", "LIMMIN", "LIMMAX", "INSBASE", "DBMOD", "DWGNAME", "DWGPREFIX", "BACKGROUNDPLOT",
        };

        private sealed class Ctx
        {
            public Document Doc = null!;
            public Database Db = null!;
            public Transaction Tr = null!;
            public int Max;
            public Dictionary<string, int>? LayerCounts;
            public Dictionary<string, int>? TypeCounts;
            public Dictionary<string, int>? ModelLayerCounts;
            public Dictionary<string, int>? PaperByLayout;
            public int ModelTotal;
            public bool CountsTruncated;
        }

        internal static JsonValue Build(JsonObject p)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument
                ?? throw new EngineException(ErrorCodes.WrongDocument, "No drawing is open.");
            var wanted = new List<string>();
            if (p["sections"] is JsonArray arr)
            {
                foreach (var v in arr)
                {
                    string s = (v as JsonString)?.Value ?? "";
                    if (Array.IndexOf(Sections, s) < 0)
                        throw new EngineException(ErrorCodes.BadRequest, "Unknown snapshot section '" + s + "'. Sections: " + string.Join(", ", Sections));
                    if (!wanted.Contains(s)) wanted.Add(s);
                }
            }
            if (wanted.Count == 0) wanted.AddRange(Sections);
            int max = 1000;
            if (p["maxItems"] is JsonNumber n && n.TryGetInt64(out long m)) max = (int)Math.Max(1, Math.Min(100000, m));

            var result = new JsonObject()
                .Add("engine", EngineInfo.Describe())
                .Add("takenAt", DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            using (doc.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var c = new Ctx { Doc = doc, Db = doc.Database, Tr = tr, Max = max };
                foreach (var s in wanted)
                {
                    try
                    {
                        result.Add(s, Section(c, s));
                    }
                    catch (Exception e)
                    {
                        Log.Error("Snapshot section '" + s + "' failed.", e);
                        result.Add(s, new JsonObject().Add("error", e.GetType().Name + ": " + e.Message));
                    }
                }
                tr.Commit();
            }
            return result;
        }

        private static JsonValue Section(Ctx c, string s)
        {
            switch (s)
            {
                case "document": return Document(c);
                case "sysvars": return SysVarsSection();
                case "layers": return Layers(c);
                case "linetypes": return Linetypes(c);
                case "textStyles": return TextStyles(c);
                case "dimStyles": return DimStyles(c);
                case "blocks": return Blocks(c);
                case "xrefs": return Xrefs(c);
                case "layouts": return Layouts(c);
                case "annotationScales": return AnnoScales(c);
                case "extents": return Extents(c);
                case "selection": return Selection(c);
                default: throw new ArgumentException(s);
            }
        }

        private static JsonObject Capped(IEnumerable<JsonValue> items, int max)
        {
            var list = new JsonArray();
            int count = 0;
            foreach (var i in items)
            {
                count++;
                if (list.Count < max) list.Add(i);
            }
            return new JsonObject().Add("count", count).Add("truncated", count > list.Count).Add("items", list);
        }

        internal static JsonValue ToJson(object? v)
        {
            switch (v)
            {
                case null: return JsonValue.Null;
                case string s: return s;
                case bool b: return b;
                case short sh: return (int)sh;
                case int i: return i;
                case long l: return l;
                case double d: return AcadValues.Round(d);
                case Point2d p2: return new JsonArray(AcadValues.Round(p2.X), AcadValues.Round(p2.Y));
                case Point3d p3: return new JsonArray(AcadValues.Round(p3.X), AcadValues.Round(p3.Y), AcadValues.Round(p3.Z));
                default: return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
        }

        private static JsonObject Document(Ctx c)
        {
            var doc = c.Doc;
            var db = c.Db;
            object? dbmod = SysVarScope.Get("DBMOD");
            var o = new JsonObject()
                .Add("path", doc.Name ?? "")
                .Add("isNamed", doc.IsNamedDrawing)
                .Add("fingerprint", DocumentIdentity.FingerprintOf(doc))
                .Add("units", DrawingUnits.Describe(db))
                .Add("measurement", db.Measurement == MeasurementValue.Metric ? "metric" : "imperial")
                .Add("readOnly", doc.IsReadOnly)
                .Add("modified", dbmod is short sm ? sm != 0 : dbmod is int im ? im != 0 : (JsonValue)JsonValue.Null)
                .Add("originalFileVersion", db.OriginalFileVersion.ToString())
                .Add("savedByVersion", db.OriginalFileSavedByVersion.ToString())
                .Add("lastSavedAsVersion", db.LastSavedAsVersion.ToString())
                .Add("currentLayout", LayoutManager.Current.CurrentLayout)
                .Add("tileMode", db.TileMode)
                .Add("openDocuments", new JsonArray(AcApp.DocumentManager.Cast<Document>().Select(d => (JsonValue)(d.Name ?? ""))));
            return o;
        }

        private static JsonObject SysVarsSection()
        {
            var o = new JsonObject();
            foreach (var name in SysVars)
            {
                object? v = SysVarScope.Get(name);
                o.Add(name, ToJson(v));
            }
            return o;
        }

        private static void CountEntities(Ctx c)
        {
            if (c.LayerCounts != null) return;
            c.LayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            c.ModelLayerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            c.TypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            c.PaperByLayout = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            const int OpenCap = 200000;
            int opened = 0;
            var bt = (BlockTable)c.Tr.GetObject(c.Db.BlockTableId, OpenMode.ForRead);
            var msId = SymbolUtilityServices.GetBlockModelSpaceId(c.Db);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)c.Tr.GetObject(btrId, OpenMode.ForRead);
                if (!btr.IsLayout) continue;
                bool model = btrId == msId;
                string layout = "";
                if (!model) { try { layout = ((Layout)c.Tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName; } catch { } }
                int inThis = 0;
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    inThis++;
                    if (model)
                    {
                        c.ModelTotal++;
                        string type = id.ObjectClass.DxfName;
                        c.TypeCounts.TryGetValue(type, out int t);
                        c.TypeCounts[type] = t + 1;
                    }
                    if (opened >= OpenCap) { c.CountsTruncated = true; continue; }
                    opened++;
                    if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    c.LayerCounts.TryGetValue(e.Layer, out int n);
                    c.LayerCounts[e.Layer] = n + 1;
                    if (model)
                    {
                        c.ModelLayerCounts.TryGetValue(e.Layer, out int mn);
                        c.ModelLayerCounts[e.Layer] = mn + 1;
                    }
                }
                if (!model) c.PaperByLayout[layout] = inThis;
            }
        }

        private static JsonObject Layers(Ctx c)
        {
            CountEntities(c);
            var lt = (LayerTable)c.Tr.GetObject(c.Db.LayerTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            foreach (ObjectId id in lt)
            {
                if (id.IsErased) continue;
                var l = (LayerTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                string ltName = "";
                try { ltName = ((LinetypeTableRecord)c.Tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name; } catch { }
                c.LayerCounts!.TryGetValue(l.Name, out int count);
                var tp = AcadValues.TransparencyPercent(l.Transparency);
                items.Add(new JsonObject()
                    .Add("name", l.Name).Add("handle", id.Handle.ToString())
                    .Add("color", AcadValues.ColorJson(l.Color)).Add("linetype", ltName)
                    .Add("lineweight", AcadValues.LineWeightJson(l.LineWeight)).Add("plot", l.IsPlottable)
                    .Add("frozen", l.IsFrozen).Add("off", l.IsOff).Add("locked", l.IsLocked)
                    .Add("transparency", tp.HasValue ? (JsonValue)tp.Value : JsonValue.Null)
                    .Add("description", l.Description ?? "").Add("isDependent", l.IsDependent)
                    .Add("objects", count));
            }
            var o = Capped(items, c.Max);
            var cur = (LayerTableRecord)c.Tr.GetObject(c.Db.Clayer, OpenMode.ForRead);
            o.Add("current", cur.Name).Add("objectCountsComplete", !c.CountsTruncated);
            return o;
        }

        private static JsonObject Linetypes(Ctx c)
        {
            var t = (LinetypeTable)c.Tr.GetObject(c.Db.LinetypeTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            foreach (ObjectId id in t)
            {
                if (id.IsErased) continue;
                var r = (LinetypeTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                items.Add(new JsonObject().Add("name", r.Name).Add("description", r.AsciiDescription ?? "")
                    .Add("patternLength", AcadValues.Round(r.PatternLength)).Add("isDependent", r.IsDependent));
            }
            return Capped(items, c.Max);
        }

        private static JsonObject TextStyles(Ctx c)
        {
            var t = (TextStyleTable)c.Tr.GetObject(c.Db.TextStyleTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            foreach (ObjectId id in t)
            {
                if (id.IsErased) continue;
                var r = (TextStyleTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                if (r.IsShapeFile) continue;
                items.Add(new JsonObject().Add("name", r.Name).Add("fileName", r.FileName ?? "").Add("bigFontFileName", r.BigFontFileName ?? "")
                    .Add("typeface", r.Font.TypeFace ?? "").Add("height", AcadValues.Round(r.TextSize)).Add("widthFactor", AcadValues.Round(r.XScale))
                    .Add("obliqueAngle", AcadValues.Round(r.ObliquingAngle * 180 / Math.PI)).Add("annotative", r.Annotative == AnnotativeStates.True)
                    .Add("isDependent", r.IsDependent));
            }
            var o = Capped(items, c.Max);
            o.Add("current", ((TextStyleTableRecord)c.Tr.GetObject(c.Db.Textstyle, OpenMode.ForRead)).Name);
            return o;
        }

        private static string BlockName(Ctx c, ObjectId id)
        {
            if (id.IsNull) return "";
            try { return ((BlockTableRecord)c.Tr.GetObject(id, OpenMode.ForRead)).Name; } catch { return ""; }
        }

        private static JsonObject DimStyles(Ctx c)
        {
            var t = (DimStyleTable)c.Tr.GetObject(c.Db.DimStyleTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            foreach (ObjectId id in t)
            {
                if (id.IsErased) continue;
                var d = (DimStyleTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                string txsty = "";
                try { txsty = ((TextStyleTableRecord)c.Tr.GetObject(d.Dimtxsty, OpenMode.ForRead)).Name; } catch { }
                items.Add(new JsonObject()
                    .Add("name", d.Name).Add("annotative", d.Annotative == AnnotativeStates.True)
                    .Add("DIMSCALE", AcadValues.Round(d.Dimscale)).Add("DIMTXT", AcadValues.Round(d.Dimtxt)).Add("DIMASZ", AcadValues.Round(d.Dimasz))
                    .Add("DIMEXO", AcadValues.Round(d.Dimexo)).Add("DIMEXE", AcadValues.Round(d.Dimexe)).Add("DIMGAP", AcadValues.Round(d.Dimgap))
                    .Add("DIMDLI", AcadValues.Round(d.Dimdli)).Add("DIMLFAC", AcadValues.Round(d.Dimlfac))
                    .Add("DIMDEC", d.Dimdec).Add("DIMLUNIT", d.Dimlunit).Add("DIMTAD", d.Dimtad)
                    .Add("DIMTXSTY", txsty).Add("DIMBLK", BlockName(c, d.Dimblk)).Add("DIMTSZ", AcadValues.Round(d.Dimtsz))
                    .Add("isDependent", d.IsDependent));
            }
            var o = Capped(items, c.Max);
            try { o.Add("current", ((DimStyleTableRecord)c.Tr.GetObject(c.Db.Dimstyle, OpenMode.ForRead)).Name); } catch { }
            return o;
        }

        private static JsonObject Blocks(Ctx c)
        {
            var bt = (BlockTable)c.Tr.GetObject(c.Db.BlockTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            int anonymous = 0;
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var b = (BlockTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                if (b.IsLayout) continue;
                if (b.IsAnonymous) { anonymous++; continue; }
                int instances = b.GetBlockReferenceIds(true, false).Count;
                if (b.IsDynamicBlock)
                {
                    foreach (ObjectId anon in b.GetAnonymousBlockIds())
                    {
                        try { instances += ((BlockTableRecord)c.Tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count; } catch { }
                    }
                }
                var attdefs = new JsonArray();
                if (b.HasAttributeDefinitions)
                {
                    foreach (ObjectId eid in b)
                    {
                        if (eid.ObjectClass.DxfName != "ATTDEF") continue;
                        if (!(c.Tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad)) continue;
                        attdefs.Add(new JsonObject().Add("tag", ad.Tag).Add("prompt", ad.Prompt ?? "").Add("default", ad.TextString ?? "")
                            .Add("constant", ad.Constant).Add("invisible", ad.Invisible));
                    }
                }
                items.Add(new JsonObject()
                    .Add("name", b.Name).Add("handle", id.Handle.ToString())
                    .Add("isDynamic", b.IsDynamicBlock).Add("isXref", b.IsFromExternalReference).Add("isOverlay", b.IsFromOverlayReference)
                    .Add("isDependent", b.IsDependent).Add("annotative", b.Annotative == AnnotativeStates.True)
                    .Add("units", DrawingUnits.NameOf((int)b.Units)).Add("origin", ToJson(b.Origin))
                    .Add("attributes", attdefs).Add("instances", instances));
            }
            var o = Capped(items, c.Max);
            o.Add("anonymous", anonymous);
            return o;
        }

        internal static string PathType(string path)
        {
            if (string.IsNullOrEmpty(path)) return "none";
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.Length > 1 && path[1] == ':') return "full";
            if (path.IndexOf('\\') < 0 && path.IndexOf('/') < 0) return "none";
            return "relative";
        }

        private static JsonObject Xrefs(Ctx c)
        {
            var bt = (BlockTable)c.Tr.GetObject(c.Db.BlockTableId, OpenMode.ForRead);
            var items = new List<JsonValue>();
            foreach (ObjectId id in bt)
            {
                if (id.IsErased) continue;
                var b = (BlockTableRecord)c.Tr.GetObject(id, OpenMode.ForRead);
                if (!b.IsFromExternalReference) continue;
                string stored = b.PathName ?? "";
                string? resolved = null;
                try
                {
                    string? f = Db.FindSupportFile(c.Db, stored);
                    if (f != null) resolved = f;
                    else if (Path.IsPathRooted(stored) && File.Exists(stored)) resolved = stored;
                }
                catch { }
                items.Add(new JsonObject()
                    .Add("name", b.Name).Add("handle", id.Handle.ToString())
                    .Add("storedPath", stored).Add("pathType", PathType(stored))
                    .Add("mode", b.IsFromOverlayReference ? "overlay" : "attach")
                    .Add("status", b.XrefStatus.ToString()).Add("found", b.XrefStatus == XrefStatus.Resolved || resolved != null)
                    .Add("resolvedPath", resolved ?? (JsonValue)JsonValue.Null)
                    .Add("unloaded", b.IsUnloaded)
                    .Add("instances", b.GetBlockReferenceIds(true, false).Count));
            }
            return Capped(items, c.Max);
        }

        private static JsonObject Layouts(Ctx c)
        {
            var dict = (DBDictionary)c.Tr.GetObject(c.Db.LayoutDictionaryId, OpenMode.ForRead);
            var layouts = new List<Layout>();
            foreach (DBDictionaryEntry e in dict)
                if (c.Tr.GetObject(e.Value, OpenMode.ForRead) is Layout l) layouts.Add(l);
            var items = new List<JsonValue>();
            foreach (var l in layouts.OrderBy(x => x.TabOrder))
            {
                var vps = new JsonArray();
                if (!l.ModelType)
                {
                    var btr = (BlockTableRecord)c.Tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead);
                    // The layout's own paper-space viewport is not a model viewport; found as the audit finds it.
                    var overall = SA.AutoCAD.Audit.Collectors.DrawingScan.FindOverallViewport(c.Tr, l);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || id.ObjectClass.DxfName != "VIEWPORT" || id == overall) continue;
                        if (!(c.Tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                        double cs = vp.CustomScale;
                        string anno = "";
                        try { anno = vp.AnnotationScale?.Name ?? ""; } catch { }
                        vps.Add(new JsonObject()
                            .Add("handle", id.Handle.ToString()).Add("layer", vp.Layer)
                            .Add("centre", ToJson(vp.CenterPoint)).Add("width", AcadValues.Round(vp.Width)).Add("height", AcadValues.Round(vp.Height))
                            .Add("customScale", AcadValues.Round(cs, 12))
                            .Add("scale", cs > 0 ? "1:" + AcadValues.Round(1.0 / cs, 6).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
                            .Add("standardScale", vp.StandardScale.ToString())
                            .Add("locked", vp.Locked).Add("on", vp.On)
                            .Add("viewCentre", ToJson(vp.ViewCenter)).Add("twist", AcadValues.Round(vp.TwistAngle * 180 / Math.PI))
                            .Add("annotationScale", anno));
                    }
                }
                var paper = l.PlotPaperSize;
                items.Add(new JsonObject()
                    .Add("name", l.LayoutName).Add("tabOrder", l.TabOrder).Add("modelType", l.ModelType)
                    .Add("paperSize", new JsonArray(AcadValues.Round(paper.X), AcadValues.Round(paper.Y)))
                    .Add("paperUnits", l.PlotPaperUnits.ToString())
                    .Add("canonicalMediaName", l.CanonicalMediaName ?? "")
                    .Add("plotDevice", l.PlotConfigurationName ?? "")
                    .Add("plotSettingsName", l.PlotSettingsName ?? "")
                    .Add("plotStyleTable", l.CurrentStyleSheet ?? "")
                    .Add("plotRotation", l.PlotRotation.ToString())
                    .Add("viewports", vps));
            }
            var o = Capped(items, c.Max);
            o.Add("current", LayoutManager.Current.CurrentLayout);
            return o;
        }

        private static JsonObject AnnoScales(Ctx c)
        {
            var items = new List<JsonValue>();
            var coll = AnnotationScales.Collection(c.Db);
            if (coll != null)
            {
                foreach (ObjectContext oc in coll)
                {
                    if (!(oc is AnnotationScale s)) continue;
                    items.Add(new JsonObject().Add("name", s.Name).Add("paperUnits", AcadValues.Round(s.PaperUnits))
                        .Add("drawingUnits", AcadValues.Round(s.DrawingUnits)).Add("ratio", AcadValues.Round(AnnotationScales.Ratio(c.Db, s), 9)));
                }
            }
            var o = Capped(items, c.Max);
            try { o.Add("current", c.Db.Cannoscale?.Name ?? ""); } catch { }
            return o;
        }

        private static JsonObject Extents(Ctx c)
        {
            CountEntities(c);
            var byType = new JsonObject();
            foreach (var kv in c.TypeCounts!.OrderByDescending(k => k.Value)) byType.Add(kv.Key, kv.Value);
            var byLayer = new List<JsonValue>();
            foreach (var kv in c.ModelLayerCounts!.OrderByDescending(k => k.Value))
                byLayer.Add(new JsonObject().Add("layer", kv.Key).Add("count", kv.Value));
            var paper = new JsonObject();
            foreach (var kv in c.PaperByLayout!) paper.Add(kv.Key, kv.Value);
            return new JsonObject()
                .Add("note", "extmin/extmax are the drawing header values, updated by AutoCAD on regeneration; they can be stale")
                .Add("extmin", ToJson(c.Db.Extmin)).Add("extmax", ToJson(c.Db.Extmax))
                .Add("modelSpace", new JsonObject()
                    .Add("total", c.ModelTotal)
                    .Add("byType", byType)
                    .Add("byLayer", Capped(byLayer, c.Max))
                    .Add("byLayerComplete", !c.CountsTruncated))
                .Add("paperSpace", paper);
        }

        private static JsonObject Selection(Ctx c)
        {
            // The pickfirst set of the active document; reading it does not change it.
            var res = c.Doc.Editor.SelectImplied();
            if (res.Status != PromptStatus.OK || res.Value is null)
                return new JsonObject().Add("count", 0).Add("truncated", false).Add("items", new JsonArray());
            var ids = res.Value.GetObjectIds();
            return Capped(ids.Select(i => (JsonValue)i.Handle.ToString()), c.Max);
        }
    }
}
