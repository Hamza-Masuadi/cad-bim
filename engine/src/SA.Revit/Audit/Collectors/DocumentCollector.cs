using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Checks;
using SA.Revit.Compat;

namespace SA.Revit.Audit.Collectors
{
    /// <summary>
    /// Table "document" (FACTS.md 2.1): exactly one record, no id.
    /// Fields: title (string), isWorkshared (bool), extentsMaxDistanceMm (number), designOptionSetCount,
    /// designOptionCount, groupInstanceCount, unusedGroupTypeCount, unusedFamilyCount, importedImageCount,
    /// planRegionCount, lineStyleCount, fillPatternCount, textTypeCount (int).
    ///
    /// extentsMaxDistanceMm: over every element that is not an element type, not view-specific, whose category has
    /// CategoryType.Model, leaving out levels, grids, reference planes, scope boxes, the project base point, the survey
    /// point, the internal origin, cameras and link instances: the farthest corner of get_BoundingBox(null) from (0,0,0).
    ///
    /// unusedFamilyCount: loadable families (not in-place, IsEditable) none of whose symbols is "used". A symbol is used
    /// when an element has it as its type (GetTypeId), when any element type refers to it (or to its family) through
    /// an element-id parameter (tags of level and grid heads, profiles of sweeps and mullions, curtain panels, spot
    /// symbols, nested family-type parameters...), or when a railing type names it as a baluster or post
    /// (BalusterPlacement). Default family types of categories (Document.GetDefaultFamilyTypeId) are not treated as uses.
    /// </summary>
    internal sealed class DocumentCollector : IFactCollector
    {
        public string Table => "document";

        private static readonly HashSet<BuiltInCategory> ExtentsExcluded = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_CLines, BuiltInCategory.OST_VolumeOfInterest,
            BuiltInCategory.OST_ProjectBasePoint, BuiltInCategory.OST_SharedBasePoint, BuiltInCategory.OST_IOS_GeoSite,
            BuiltInCategory.OST_Cameras, BuiltInCategory.OST_RvtLinks,
        };

        public void Collect(AuditContext ctx, FactTable table)
        {
            var doc = ctx.Doc;
            var r = table.Add();
            r.Set("title", doc.Title ?? "");
            r.Set("isWorkshared", doc.IsWorkshared);
            r.Set("extentsMaxDistanceMm", ExtentsMaxDistanceMm(doc));
            r.Set("designOptionSetCount", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DesignOptionSets).GetElementCount());
            r.Set("designOptionCount", new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).GetElementCount());
            r.Set("groupInstanceCount", new FilteredElementCollector(doc).OfClass(typeof(Group)).GetElementCount());
            var use = AuditCache.TypeUse(ctx);
            int unusedGroupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).ToElementIds()
                .Count(id => !use.ContainsKey(ElementIds.ToLong(id)));
            r.Set("unusedGroupTypeCount", unusedGroupTypes);
            r.Set("unusedFamilyCount", UnusedFamilies(ctx));
            r.Set("importedImageCount", new FilteredElementCollector(doc).OfClass(typeof(ImageType)).GetElementCount());
            r.Set("planRegionCount", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType().GetElementCount());
            int lineStyles = 0;
            try
            {
                var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                lineStyles = lines?.SubCategories.Size ?? 0;
            }
            catch (Exception) { ctx.Omitted(Table, "lineStyleCount"); }
            r.Set("lineStyleCount", lineStyles);
            r.Set("fillPatternCount", new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).GetElementCount());
            r.Set("textTypeCount", new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).GetElementCount());
        }

        private static double ExtentsMaxDistanceMm(Document doc)
        {
            double ax = 0, ay = 0, az = 0;
            bool any = false;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
            {
                var c = e.Category;
                if (c is null || e is RevitLinkInstance) continue;
                CategoryType ct;
                try { ct = c.CategoryType; } catch (Exception) { continue; }
                if (ct != CategoryType.Model) continue;
                var bic = ElementIds.ToBuiltInCategory(c.Id);
                if (bic.HasValue && ExtentsExcluded.Contains(bic.Value)) continue;
                BoundingBoxXYZ? bb;
                try { bb = e.get_BoundingBox(null); } catch (Exception) { continue; }
                if (bb is null) continue;
                // The farthest corner of an axis-aligned box from the origin takes the larger |coordinate| on each axis.
                ax = Math.Max(ax, Math.Max(Math.Abs(bb.Min.X), Math.Abs(bb.Max.X)));
                ay = Math.Max(ay, Math.Max(Math.Abs(bb.Min.Y), Math.Abs(bb.Max.Y)));
                az = Math.Max(az, Math.Max(Math.Abs(bb.Min.Z), Math.Abs(bb.Max.Z)));
                any = true;
            }
            if (!any) return 0;
            double d = Math.Sqrt(ax * ax + ay * ay + az * az);
            return Math.Round(AuditCache.MmValue(d), 3);
        }

        private static int UnusedFamilies(AuditContext ctx)
        {
            var doc = ctx.Doc;
            var use = AuditCache.TypeUse(ctx);
            var referenced = new HashSet<long>(use.Keys);
            // Element-id parameters of every element type (heads, profiles, panels, symbols, nested family types).
            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                ParameterSet ps;
                try { ps = t.Parameters; } catch (Exception) { continue; }
                foreach (Parameter p in ps)
                {
                    try
                    {
                        if (p.StorageType != StorageType.ElementId) continue;
                        var id = p.AsElementId();
                        if (ElementIds.IsValid(id) && ElementIds.ToLong(id) > 0) referenced.Add(ElementIds.ToLong(id));
                    }
                    catch (Exception) { }
                }
                if (t is RailingType rt) AddBalusters(rt, referenced);
            }
            int unused = 0;
            foreach (var f in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                bool loadable;
                try { loadable = !f.IsInPlace && f.IsEditable; } catch (Exception) { continue; }
                if (!loadable) continue;
                if (referenced.Contains(ElementIds.ToLong(f.Id))) continue;
                ICollection<ElementId> symbols;
                try { symbols = f.GetFamilySymbolIds(); } catch (Exception) { continue; }
                if (symbols.Any(s => referenced.Contains(ElementIds.ToLong(s)))) continue;
                unused++;
            }
            return unused;
        }

        private static void AddBalusters(RailingType rt, HashSet<long> referenced)
        {
            try
            {
                var bp = rt.BalusterPlacement;
                if (bp is null) return;
                var pattern = bp.BalusterPattern;
                if (pattern != null)
                {
                    int n = pattern.GetBalusterCount();
                    for (int i = 0; i < n; i++) Add(pattern.GetBaluster(i));
                }
                var posts = bp.PostPattern;
                if (posts != null)
                {
                    Add(posts.CornerPost);
                    Add(posts.StartPost);
                    Add(posts.EndPost);
                }
            }
            catch (Exception) { }

            void Add(BalusterInfo? info)
            {
                if (info is null) return;
                var id = info.BalusterFamilyId;
                if (ElementIds.IsValid(id)) referenced.Add(ElementIds.ToLong(id));
            }
        }
    }
}
