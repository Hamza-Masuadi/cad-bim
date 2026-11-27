using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using SA.Core.Checks;
using SA.Core.Json;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "elements" (FACTS.md 2.4): one record per element of the listed categories, id = element id. Element types
    /// and elements in links are left out; elements inside groups are included.
    ///
    /// Categories: OST_Walls, OST_Doors, OST_Windows, OST_Floors, OST_Ceilings, OST_Roofs, OST_Rooms, OST_MEPSpaces,
    /// OST_Furniture, OST_FurnitureSystems, OST_Casework, OST_GenericModel, OST_SpecialityEquipment,
    /// OST_PlumbingFixtures, OST_Columns, OST_StructuralColumns, OST_Mass, OST_Stairs, OST_Railings and OST_StairsRailing
    /// (Revit's Railing elements carry OST_StairsRailing; the contract names OST_Railings, so both are collected and
    /// each record says which it has), OST_Levels, OST_Grids, OST_TextNotes, and FamilyInstances of OST_DetailComponents.
    ///
    /// Fields: category, familyName, typeName (absent for rooms, spaces, levels, grids), levelName, mark, number,
    /// isPinned, isDatum, isWorkshared, worksetName, isViewSpecific, ownerViewKind, text, designOption,
    /// levelOffsetAbsMm, levelSpan, isAttached, roomState, roomId. Absent where FACTS.md says so. A field that
    /// Revit refuses to give for a record is left out of that record and counted in facts meta "omittedFields".
    ///
    /// levelOffsetAbsMm for OST_Columns: INSTANCE_ELEVATION_PARAM as the contract says, else FAMILY_BASE_LEVEL_OFFSET_PARAM
    /// (architectural columns carry "Base Offset"). Hosted means FamilyInstance.Host is set and is not a Level.
    /// </summary>
    internal sealed class ElementsCollector : IFactCollector
    {
        public string Table => "elements";

        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces,
            BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Mass, BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_Railings, BuiltInCategory.OST_StairsRailing, BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids,
            BuiltInCategory.OST_TextNotes,
        };

        /// <summary>Level-based family instance categories whose offset is INSTANCE_ELEVATION_PARAM.</summary>
        private static readonly HashSet<BuiltInCategory> ElevationCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Columns,
        };

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            var list = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(Categories)).WhereElementIsNotElementType().ToList();
            list.AddRange(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DetailComponents).OfClass(typeof(FamilyInstance)));
            if (list.Count > AuditCache.MaxRecordsPerTable)
            {
                ctx.Truncated(Table, "records", AuditCache.MaxRecordsPerTable, list.Count, "record limit");
                list = list.Take(AuditCache.MaxRecordsPerTable).ToList();
            }

            bool workshared = doc.IsWorkshared;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ThenBy(l => ElementIds.ToLong(l.Id)).ToList();
            var levelIndex = new Dictionary<long, int>();
            for (int i = 0; i < levels.Count; i++) levelIndex[ElementIds.ToLong(levels[i].Id)] = i;
            double oneMm = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters);
            var redundant = RedundantRooms(ctx);

            foreach (var e in list)
            {
                var r = table.Add(ElementIds.ToWire(e.Id));
                var bic = ElementIds.ToBuiltInCategory(e.Category?.Id);
                string? catName = AuditCache.CategoryName(e.Category);
                if (catName != null) r.Set("category", catName);
                bool isRoom = bic == BuiltInCategory.OST_Rooms, isSpace = bic == BuiltInCategory.OST_MEPSpaces;
                bool isDatum = bic == BuiltInCategory.OST_Levels || bic == BuiltInCategory.OST_Grids;

                Field(ctx, "familyName/typeName", () =>
                {
                    if (isRoom || isSpace || isDatum) return;
                    if (doc.GetElement(e.GetTypeId()) is ElementType t)
                    {
                        r.Set("familyName", t.FamilyName ?? "");
                        r.Set("typeName", t.Name ?? "");
                    }
                });
                Field(ctx, "levelName", () =>
                {
                    Level? lv = e is SpatialElement se ? se.Level : (ElementIds.IsValid(e.LevelId) ? doc.GetElement(e.LevelId) as Level : null);
                    if (lv != null) r.Set("levelName", lv.Name ?? "");
                });
                var mark = AuditCache.Text(e, BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null) r.Set("mark", mark);
                if (e is SpatialElement sp)
                    Field(ctx, "number", () => { var n = sp.Number?.Trim(); if (!string.IsNullOrEmpty(n)) r.Set("number", n!); });
                Field(ctx, "isPinned", () => r.Set("isPinned", e.Pinned));
                r.Set("isDatum", isDatum);
                r.Set("isWorkshared", workshared);
                var ws = AuditCache.WorksetName(ctx, e);
                if (ws != null) r.Set("worksetName", ws);
                bool viewSpecific = false;
                Field(ctx, "isViewSpecific", () => { viewSpecific = e.ViewSpecific; r.Set("isViewSpecific", viewSpecific); });
                if (viewSpecific)
                    Field(ctx, "ownerViewKind", () => r.Set("ownerViewKind", doc.GetElement(e.OwnerViewId) is ViewSheet ? "sheet" : "view"));
                if (e is TextNote tn)
                    Field(ctx, "text", () => r.Set("text", (tn.Text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim()));
                Field(ctx, "designOption", () => { var o = e.DesignOption; if (o != null) r.Set("designOption", o.Name ?? ""); });

                var offset = LevelOffset(e, bic);
                if (offset.HasValue) r.Set("levelOffsetAbsMm", AuditCache.Mm(Math.Abs(offset.Value)));

                if (bic == BuiltInCategory.OST_Walls && e is Wall)
                {
                    var span = LevelSpan(e, doc, levels, levelIndex, oneMm);
                    if (span.HasValue) r.Set("levelSpan", span.Value); else ctx.Omitted(Table, "levelSpan");
                    int? top = AuditCache.Int(e, BuiltInParameter.WALL_TOP_IS_ATTACHED), bottom = AuditCache.Int(e, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED);
                    if (top.HasValue || bottom.HasValue) r.Set("isAttached", top == 1 || bottom == 1);
                    else ctx.Omitted(Table, "isAttached");
                }
                if (isRoom && e is Room room)
                    Field(ctx, "roomState", () =>
                    {
                        string state = room.Location is null ? "unplaced"
                            : redundant.Contains(ElementIds.ToLong(room.Id)) ? "redundant"
                            : room.Area == 0 ? "not_enclosed" : "ok";
                        r.Set("roomState", state);
                    });
                if (isSpace && e is Space space)
                    Field(ctx, "roomId", () => { var rm = space.Room; if (rm != null) r.Set("roomId", ElementIds.ToWire(rm.Id)); });
            }

            void Field(AuditContext c, string name, Action a)
            {
                try { a(); }
                catch (Exception) { c.Omitted(Table, name); }
            }
        }

        /// <summary>The signed offset from the host level in internal units, or null where FACTS.md says absent.</summary>
        private static double? LevelOffset(Element e, BuiltInCategory? bic)
        {
            if (!bic.HasValue) return null;
            switch (bic.Value)
            {
                case BuiltInCategory.OST_Walls: return AuditCache.Double(e, BuiltInParameter.WALL_BASE_OFFSET);
                case BuiltInCategory.OST_Floors: return AuditCache.Double(e, BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                case BuiltInCategory.OST_Roofs: return AuditCache.Double(e, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                case BuiltInCategory.OST_StructuralColumns:
                    return e is FamilyInstance sc && !Hosted(sc) ? AuditCache.Double(e, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM) : null;
            }
            if (!ElevationCategories.Contains(bic.Value) || !(e is FamilyInstance fi) || Hosted(fi)) return null;
            var v = AuditCache.Double(e, BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (v is null && bic.Value == BuiltInCategory.OST_Columns) v = AuditCache.Double(e, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
            return v;
        }

        private static bool Hosted(FamilyInstance fi)
        {
            try { return fi.Host != null && !(fi.Host is Level); }
            catch (Exception) { return true; }
        }

        /// <summary>FACTS.md 2.4 levelSpan for a wall, or null when its base level cannot be read.</summary>
        private static int? LevelSpan(Element wall, Document doc, List<Level> levels, Dictionary<long, int> index, double oneMm)
        {
            var baseId = AuditCache.Id(wall, BuiltInParameter.WALL_BASE_CONSTRAINT) ?? (ElementIds.IsValid(wall.LevelId) ? wall.LevelId : null);
            if (baseId is null || !(doc.GetElement(baseId) is Level baseLevel) || !index.TryGetValue(ElementIds.ToLong(baseId), out int baseIndex)) return null;
            double top;
            var topId = AuditCache.Id(wall, BuiltInParameter.WALL_HEIGHT_TYPE);
            if (topId != null && doc.GetElement(topId) is Level topLevel)
                top = topLevel.Elevation + (AuditCache.Double(wall, BuiltInParameter.WALL_TOP_OFFSET) ?? 0);
            else
            {
                var h = AuditCache.Double(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (h is null) return null;
                top = baseLevel.Elevation + (AuditCache.Double(wall, BuiltInParameter.WALL_BASE_OFFSET) ?? 0) + h.Value;
            }
            int highest = -1;
            for (int i = 0; i < levels.Count; i++) if (levels[i].Elevation <= top + oneMm) highest = i;
            return highest - baseIndex;
        }

        /// <summary>
        /// Rooms named in a "Multiple Rooms are in the same enclosed region" warning that are not the lowest id named
        /// in it (FACTS.md 2.4 roomState).
        /// </summary>
        private static HashSet<long> RedundantRooms(AuditContext ctx)
        {
            var set = new HashSet<long>();
            foreach (var w in AuditCache.Warnings(ctx))
            {
                string d = (w.GetDescriptionText() ?? "").ToLowerInvariant();
                if (!d.Contains("multiple rooms") || !d.Contains("same enclosed region")) continue;
                var rooms = w.GetFailingElements().Concat(w.GetAdditionalElements())
                    .Where(id => ctx.Doc.GetElement(id) is Room).Select(ElementIds.ToLong).Distinct().OrderBy(x => x).ToList();
                foreach (var id in rooms.Skip(1)) set.Add(id);
            }
            return set;
        }
    }
}
