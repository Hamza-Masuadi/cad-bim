using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Json;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Snapshot
{
    /// <summary>
    /// Snapshot section builders. Conventions: ids are strings; lengths are in job units (ctx.Units) unless the key
    /// says otherwise ("elevationM" is metres, "display" is the project's own formatting); lists use ctx.Capped.
    /// </summary>
    internal static class Sections
    {
        private static string Id(ElementId id) => ElementIds.ToWire(id);

        private static FilteredElementCollector C(Document d) => new FilteredElementCollector(d);

        // ------------------------------------------------------------------ document, units

        public static JsonValue Document(SnapshotContext c)
        {
            var d = c.Doc;
            var id = c.Engine.Tracker.Compute(d);
            var o = id.ToJson();
            o.Add("isModified", d.IsModified);
            o.Add("revit", new JsonObject().Add("version", d.Application.VersionNumber).Add("build", d.Application.VersionBuild)
                .Add("subVersion", d.Application.SubVersionNumber).Add("engineRelease", ReleaseInfo.Year));
            var av = d.ActiveView;
            o.Add("activeView", av is null ? (JsonValue)JsonValue.Null :
                new JsonObject().Add("id", Id(av.Id)).Add("name", av.Name).Add("type", av.ViewType.ToString()));
            try
            {
                var pbp = BasePoint.GetProjectBasePoint(d);
                var sp = BasePoint.GetSurveyPoint(d);
                o.Add("basePoints", new JsonObject()
                    .Add("projectBasePoint", c.Units.PointJson(pbp.Position)).Add("projectBasePointZ", c.Units.LengthJson(pbp.Position.Z))
                    .Add("surveyPoint", c.Units.PointJson(sp.Position)).Add("surveyPointZ", c.Units.LengthJson(sp.Position.Z)));
            }
            catch (Exception) { }
            return o;
        }

        public static JsonValue Units(SnapshotContext c)
        {
            var d = c.Doc;
            return new JsonObject()
                .Add("project", new JsonObject()
                    .Add("length", RevitUnits.ProjectUnit(d, SpecTypeId.Length))
                    .Add("area", RevitUnits.ProjectUnit(d, SpecTypeId.Area))
                    .Add("volume", RevitUnits.ProjectUnit(d, SpecTypeId.Volume))
                    .Add("angle", RevitUnits.ProjectUnit(d, SpecTypeId.Angle)))
                .Add("job", c.Units.DescribeJobUnits())
                .Add("note", "Values in this snapshot are in job units unless the key ends in M (metres) or is 'display' (project formatting). Examples format 1 m, 1 m2, 1 m3 and 1 degree.");
        }

        // ------------------------------------------------------------------ datum

        public static JsonValue Levels(SnapshotContext c)
        {
            double pbpZ = 0;
            try { pbpZ = BasePoint.GetProjectBasePoint(c.Doc).Position.Z; } catch (Exception) { }
            return c.Capped(Lookup.Levels(c.Doc).OrderBy(l => l.ProjectElevation), l =>
            {
                double above = l.ProjectElevation - pbpZ;
                return (JsonValue)new JsonObject().Add("id", Id(l.Id)).Add("name", l.Name)
                    .Add("elevation", c.Units.LengthJson(above))
                    .Add("elevationM", RevitUnits.Num(RevitUnits.ToMetres(above)))
                    .Add("display", c.Units.Display(SpecTypeId.Length, l.Elevation))
                    .Add("buildingStory", l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.AsInteger() == 1);
            });
        }

        public static JsonValue Grids(SnapshotContext c)
        {
            return c.Capped(C(c.Doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => g.Name, StringComparer.Ordinal), g =>
            {
                var o = new JsonObject().Add("id", Id(g.Id)).Add("name", g.Name);
                try
                {
                    var cv = g.Curve;
                    o.Add("start", c.Units.PointJson(cv.GetEndPoint(0))).Add("end", c.Units.PointJson(cv.GetEndPoint(1))).Add("curved", g.IsCurved);
                }
                catch (Exception) { }
                return (JsonValue)o;
            });
        }

        public static JsonValue Phases(SnapshotContext c)
        {
            var arr = new JsonArray();
            foreach (Phase ph in c.Doc.Phases) arr.Add(new JsonObject().Add("id", Id(ph.Id)).Add("name", ph.Name));
            return arr;
        }

        public static JsonValue Worksets(SnapshotContext c)
        {
            var d = c.Doc;
            var o = new JsonObject().Add("isWorkshared", d.IsWorkshared);
            if (!d.IsWorkshared) return o.Add("items", new JsonArray());
            WorksetId active = WorksetId.InvalidWorksetId;
            try { active = d.GetWorksetTable().GetActiveWorksetId(); } catch (Exception) { }
            var items = new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name, StringComparer.Ordinal)
                .Select(w => (JsonValue)new JsonObject().Add("id", w.Id.IntegerValue).Add("name", w.Name).Add("editable", w.IsEditable)
                    .Add("owner", w.Owner ?? "").Add("open", w.IsOpen).Add("active", w.Id == active).Add("default", w.IsDefaultWorkset));
            return o.Add("currentUser", d.Application.Username).Add("items", new JsonArray(items));
        }

        public static JsonValue DesignOptions(SnapshotContext c)
        {
            return c.Capped(C(c.Doc).OfClass(typeof(DesignOption)).Cast<DesignOption>(), o =>
                (JsonValue)new JsonObject().Add("id", Id(o.Id)).Add("name", o.Name).Add("primary", o.IsPrimary));
        }

        public static JsonValue Links(SnapshotContext c)
        {
            var d = c.Doc;
            return c.Capped(C(d).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>(), li =>
            {
                var o = new JsonObject().Add("id", Id(li.Id)).Add("name", li.Name).Add("pinned", li.Pinned);
                var tid = li.GetTypeId();
                bool loaded = false;
                try { loaded = RevitLinkType.IsLoaded(d, tid); } catch (Exception) { }
                o.Add("loaded", loaded).Add("typeId", Id(tid));
                try
                {
                    var ld = li.GetLinkDocument();
                    if (ld != null) o.Add("path", ld.PathName ?? "");
                }
                catch (Exception) { }
                return (JsonValue)o;
            });
        }

        // ------------------------------------------------------------------ types

        public static JsonValue HostTypes<T>(SnapshotContext c, bool layers) where T : HostObjAttributes
        {
            return c.Capped(C(c.Doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name, StringComparer.Ordinal), t =>
            {
                var o = new JsonObject().Add("id", Id(t.Id)).Add("name", t.Name);
                if (t is WallType wt)
                {
                    o.Add("kind", wt.Kind.ToString());
                    try { o.Add("function", wt.Function.ToString()); } catch (Exception) { }
                }
                CompoundStructure? cs = null;
                try { cs = t.GetCompoundStructure(); } catch (Exception) { }
                if (cs != null)
                {
                    o.Add("thickness", c.Units.LengthJson(cs.GetWidth()));
                    if (layers)
                    {
                        int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                        var arr = new JsonArray();
                        var ls = cs.GetLayers();
                        for (int i = 0; i < ls.Count; i++)
                        {
                            var layer = ls[i];
                            string mat = ElementIds.IsValid(layer.MaterialId) && c.Doc.GetElement(layer.MaterialId) is Material m ? m.Name : "";
                            arr.Add(new JsonObject().Add("function", layer.Function.ToString()).Add("thickness", c.Units.LengthJson(layer.Width))
                                .Add("material", mat).Add("core", i >= first && i <= last && first >= 0));
                        }
                        o.Add("layers", arr);
                    }
                }
                else if (t is WallType w2)
                {
                    try { o.Add("thickness", c.Units.LengthJson(w2.Width)); } catch (Exception) { }
                }
                return (JsonValue)o;
            });
        }

        public static JsonValue Families(SnapshotContext c)
        {
            var d = c.Doc;
            var o = new JsonObject();
            o.Add("doors", Symbols(c, BuiltInCategory.OST_Doors));
            o.Add("windows", Symbols(c, BuiltInCategory.OST_Windows));
            o.Add("furniture", Symbols(c, BuiltInCategory.OST_Furniture));
            o.Add("titleBlocks", Symbols(c, BuiltInCategory.OST_TitleBlocks));
            var tags = C(d).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && s.Category.IsTagCategory)
                .OrderBy(s => s.Category!.Name, StringComparer.Ordinal).ThenBy(Lookup.FamilyTypeName, StringComparer.Ordinal)
                .Select(s => (JsonValue)new JsonObject().Add("id", Id(s.Id)).Add("name", Lookup.FamilyTypeName(s)).Add("category", s.Category!.Name));
            o.Add("tags", c.Capped(tags));
            return o;
        }

        private static JsonObject Symbols(SnapshotContext c, BuiltInCategory cat)
        {
            return c.Capped(C(c.Doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>()
                .OrderBy(Lookup.FamilyTypeName, StringComparer.Ordinal)
                , s => (JsonValue)new JsonObject().Add("id", Id(s.Id)).Add("name", Lookup.FamilyTypeName(s)).Add("active", s.IsActive));
        }

        public static JsonValue ViewFamilyTypes(SnapshotContext c)
        {
            return c.Capped(C(c.Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .OrderBy(v => v.ViewFamily.ToString(), StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal)
                , v => (JsonValue)new JsonObject().Add("id", Id(v.Id)).Add("name", v.Name).Add("viewFamily", v.ViewFamily.ToString()));
        }

        // ------------------------------------------------------------------ views and sheets

        private static bool Skip(View v) =>
            v.ViewType == ViewType.Internal || v.ViewType == ViewType.ProjectBrowser || v.ViewType == ViewType.SystemBrowser ||
            v.ViewType == ViewType.Undefined || v is ViewSheet || v is ViewSchedule;

        public static JsonValue Views(SnapshotContext c, bool templates)
        {
            var d = c.Doc;
            return c.Capped(C(d).OfClass(typeof(View)).Cast<View>().Where(v => !Skip(v) && v.IsTemplate == templates)
                .OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal)
                , v =>
                {
                    var o = new JsonObject().Add("id", Id(v.Id)).Add("name", v.Name).Add("type", v.ViewType.ToString()).Add("scale", SheetIndex.Scale(v));
                    if (!templates)
                    {
                        var tid = v.ViewTemplateId;
                        o.Add("template", ElementIds.IsValid(tid) && d.GetElement(tid) is View t ? (JsonValue)t.Name : JsonValue.Null);
                        o.Add("onSheet", c.ViewToSheet.TryGetValue(ElementIds.ToLong(v.Id), out var sh) ? (JsonValue)sh : JsonValue.Null);
                        if (v is ViewPlan vp && vp.GenLevel != null) o.Add("level", vp.GenLevel.Name);
                    }
                    return (JsonValue)o;
                });
        }

        public static JsonValue Sheets(SnapshotContext c)
        {
            var d = c.Doc;
            return c.Capped(C(d).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber, StringComparer.Ordinal), s =>
            {
                var o = new JsonObject().Add("id", Id(s.Id)).Add("number", s.SheetNumber).Add("name", s.Name).Add("placeholder", s.IsPlaceholder);
                var tb = C(d, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement() as FamilyInstance;
                o.Add("titleBlock", tb is null ? (JsonValue)JsonValue.Null : Lookup.FamilyTypeName(tb.Symbol));
                var vps = new JsonArray();
                foreach (var vpId in s.GetAllViewports())
                {
                    if (!(d.GetElement(vpId) is Viewport vp)) continue;
                    var view = d.GetElement(vp.ViewId) as View;
                    var e = new JsonObject().Add("id", Id(vp.Id)).Add("viewId", Id(vp.ViewId)).Add("view", view?.Name ?? "");
                    try { e.Add("centre", c.Units.PointJson(vp.GetBoxCenter())); } catch (Exception) { }
                    vps.Add(e);
                }
                o.Add("viewports", vps);
                return (JsonValue)o;
            });
        }

        private static FilteredElementCollector C(Document d, ElementId viewId) => new FilteredElementCollector(d, viewId);

        public static JsonValue Schedules(SnapshotContext c)
        {
            return c.Capped(C(c.Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .OrderBy(s => s.Name, StringComparer.Ordinal), s =>
                {
                    string cat = "";
                    try { cat = Category.GetCategory(c.Doc, s.Definition.CategoryId)?.Name ?? ""; } catch (Exception) { }
                    return (JsonValue)new JsonObject().Add("id", Id(s.Id)).Add("name", s.Name).Add("category", cat)
                        .Add("onSheet", c.ViewToSheet.TryGetValue(ElementIds.ToLong(s.Id), out var sh) ? (JsonValue)sh : JsonValue.Null);
                });
        }

        // ------------------------------------------------------------------ model state

        public static JsonValue Rooms(SnapshotContext c)
        {
            int placed = 0, unplaced = 0, notEnclosed = 0;
            var list = new List<JsonValue>();
            foreach (var r in C(c.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                string state;
                if (r.Location is null) { unplaced++; state = "unplaced"; }
                else if (r.Area <= 0) { notEnclosed++; state = "not_enclosed"; }
                else { placed++; state = "placed"; }
                list.Add(new JsonObject().Add("id", Id(r.Id)).Add("number", r.Number ?? "").Add("name", r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "")
                    .Add("level", r.Level?.Name ?? "").Add("state", state).Add("area", RevitUnits.Num(c.Units.AreaFromInternal(r.Area))));
            }
            var o = c.Capped(list);
            o.Add("placed", placed).Add("unplaced", unplaced).Add("notEnclosed", notEnclosed).Add("areaUnit", c.Units.AreaSymbol);
            return o;
        }

        public static JsonValue Warnings(SnapshotContext c)
        {
            var all = c.Doc.GetWarnings();
            var top = all.GroupBy(w => w.GetFailureDefinitionId().Guid)
                .Select(g => new { g.Key, Text = g.First().GetDescriptionText(), Count = g.Count() })
                .OrderByDescending(x => x.Count).Take(Math.Min(25, c.Limit))
                .Select(x => (JsonValue)new JsonObject().Add("failureDefinitionId", x.Key.ToString("D")).Add("description", x.Text ?? "").Add("count", x.Count));
            return new JsonObject().Add("count", all.Count).Add("topTypes", new JsonArray(top));
        }

        public static JsonValue Selection(SnapshotContext c)
        {
            var uidoc = c.App.ActiveUIDocument;
            if (uidoc is null || !uidoc.Document.Equals(c.Doc))
                return new JsonObject().Add("available", false).Add("reason", "the snapshot document is not the active document");
            var ids = uidoc.Selection.GetElementIds().Select(Id);
            var o = c.Capped(ids.Select(s => (JsonValue)s));
            o.Add("available", true);
            return o;
        }

        public static JsonValue Categories(SnapshotContext c)
        {
            var d = c.Doc;
            var rows = new List<KeyValuePair<Category, int>>();
            foreach (Category cat in d.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model && cat.CategoryType != CategoryType.Annotation) continue;
                int n;
                try { n = C(d).OfCategoryId(cat.Id).WhereElementIsNotElementType().GetElementCount(); }
                catch (Exception) { continue; }
                if (n > 0) rows.Add(new KeyValuePair<Category, int>(cat, n));
            }
            return c.Capped(rows.OrderByDescending(r => r.Value).ThenBy(r => r.Key.Name, StringComparer.Ordinal), r =>
            {
                var o = new JsonObject().Add("name", r.Key.Name).Add("count", r.Value).Add("type", r.Key.CategoryType.ToString());
                var bic = r.Key.BuiltInCategory;
                if (bic != BuiltInCategory.INVALID) o.Add("builtIn", bic.ToString());
                return (JsonValue)o;
            });
        }
    }
}
