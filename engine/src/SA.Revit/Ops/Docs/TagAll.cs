using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// tag.all: tags every element of a category that is visible in the view and not yet tagged there.
    ///
    /// <para><b>What counts.</b> Elements: FilteredElementCollector(doc, viewId).OfCategoryId(category) (elements
    /// visible in the view), local model only; rooms must be placed (Location not null, Area &gt; 0). "Already tagged"
    /// = referenced by an IndependentTag in the view (GetTaggedLocalElementIds) whose category is the category's tag
    /// category or Multi-Category Tags, or for rooms by a RoomTag in the view (TaggedLocalRoomId). Keynote and
    /// material tags do not count. Elements already tagged are always left alone: tagging them again would double-tag
    /// (schedules-tags.md section 4).</para>
    ///
    /// <para><b>Tag type.</b> <c>tagType</c> ("Family : Type") in the tag category or Multi-Category Tags; otherwise
    /// the document's default type for the tag category (Document.GetDefaultFamilyTypeId), else the first loaded one.
    /// The tag category is the category's BuiltInCategory with "Tags" (OST_Doors to OST_DoorTags). A category with
    /// no tag family loaded is refused with that message.</para>
    ///
    /// <para><b>Placement.</b> IndependentTag.Create(doc, tagTypeId, viewId, new Reference(element), leader,
    /// orientation, point); rooms with Document.Create.NewRoomTag(LinkElementId, UV, viewId) in plan views. The point
    /// is the element's location point, the midpoint of its location curve, or the centre of its bounding box in the
    /// view, moved by <c>offset</c> (paper units, multiplied by the view scale) along the view's right and up
    /// directions. An element whose tag Revit refuses is listed in <c>untaggable</c> with the reason in a warning.</para>
    ///
    /// <para><b>View.</b> Plans, ceiling plans, sections, elevations, details and locked 3D views (the API refuses
    /// unlocked 3D, perspective and template views). Areas and MEP spaces are refused: they need area-plan and space
    /// tag APIs not covered here.</para>
    /// </summary>
    public sealed class TagAll : OpHandler
    {
        public override string Name => "tag.all";

        private sealed class Plan
        {
            public View View = null!;
            public Category Category = null!;
            public bool Rooms;
            public FamilySymbol TagType = null!;
            public ElementId TagCategory = ElementId.InvalidElementId;
            public List<Element> ToTag = new List<Element>();
            public List<string> AlreadyTagged = new List<string>();
            public List<KeyValuePair<string, string>> Untaggable = new List<KeyValuePair<string, string>>();
        }

        internal static BuiltInCategory? TagCategoryFor(Document doc, Category cat)
        {
            var bic = ElementIds.ToBuiltInCategory(cat.Id);
            if (!bic.HasValue) return null;
            string n = bic.Value.ToString();
            var candidates = new List<string>();
            if (n.EndsWith("s", StringComparison.Ordinal)) candidates.Add(n.Substring(0, n.Length - 1) + "Tags");
            candidates.Add(n + "Tags");
            foreach (var c in candidates)
                if (Enum.TryParse(c, false, out BuiltInCategory t))
                {
                    try { if (Category.GetCategory(doc, t) != null) return t; } catch (Exception) { }
                }
            return null;
        }

        /// <summary>Ids of local elements tagged in the view by tags of the given tag category (or multi-category tags).</summary>
        internal static Dictionary<long, int> TaggedInView(Document doc, View view, ElementId tagCategory, bool rooms)
        {
            var counts = new Dictionary<long, int>();
            void Add(ElementId id) { long k = ElementIds.ToLong(id); counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1; }
            if (rooms)
            {
                foreach (var t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(SpatialElementTag)).OfType<RoomTag>())
                    if (!t.IsTaggingLink && ElementIds.IsValid(t.TaggedLocalRoomId)) Add(t.TaggedLocalRoomId);
                return counts;
            }
            var multi = ElementIds.FromBuiltInCategory(BuiltInCategory.OST_MultiCategoryTags);
            foreach (var t in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                var cid = t.Category?.Id;
                if (cid is null || (cid != tagCategory && cid != multi)) continue;
                foreach (var id in t.GetTaggedLocalElementIds()) Add(id);
            }
            return counts;
        }

        internal static XYZ? Anchor(Element e, View view)
        {
            switch (e.Location)
            {
                case LocationPoint lp: return lp.Point;
                case LocationCurve lc when lc.Curve != null: return lc.Curve.Evaluate(0.5, true);
            }
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            return bb is null ? null : (bb.Min + bb.Max) * 0.5;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues, bool forReadback = false)
        {
            var doc = ctx.Doc;
            var p = new Plan();
            var view = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            var cat = DocsUtil.Category(doc, args.GetString("category"), ctx.FieldPath(args, "category"), issues);
            if (cat is null) return null;
            p.Category = cat;
            var bic = ElementIds.ToBuiltInCategory(cat.Id);
            if (bic == BuiltInCategory.OST_Areas || bic == BuiltInCategory.OST_MEPSpaces)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotAllowed, "tag.all does not tag " + cat.Name + " (area and space tags need their own APIs)");
                return null;
            }
            p.Rooms = bic == BuiltInCategory.OST_Rooms;
            var tagBic = TagCategoryFor(doc, cat);
            if (tagBic is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotAllowed, "category '" + cat.Name + "' has no tag category"); return null; }
            p.TagCategory = ElementIds.FromBuiltInCategory(tagBic.Value);

            if (args.GetStringOrNull("tagType") is string tt)
            {
                var f = Lookup.Symbol(doc, tagBic.Value, tt, cat.Name + " tag type");
                if (!f.Ok && !p.Rooms)
                {
                    var m = Lookup.Symbol(doc, BuiltInCategory.OST_MultiCategoryTags, tt, "multi-category tag type");
                    if (m.Ok) f = m;
                }
                if (!f.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "tagType"), f.Code, f.Error!); return null; }
                p.TagType = f.Value!;
            }
            else
            {
                var def = doc.GetDefaultFamilyTypeId(p.TagCategory);
                var sym = ElementIds.IsValid(def) ? doc.GetElement(def) as FamilySymbol : null;
                sym ??= new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tagBic.Value).Cast<FamilySymbol>()
                    .OrderBy(s => Lookup.FamilyTypeName(s), StringComparer.Ordinal).FirstOrDefault();
                if (sym is null) { OpContext.Error(issues, ctx.FieldPath(args, "tagType"), RevitIssueCodes.NotFound, "no " + cat.Name + " tag family is loaded; load one first"); return null; }
                p.TagType = sym;
            }
            if (pending || view is null) return DocsUtil.HasErrors(issues) ? null : p;
            p.View = view;
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule)
            { OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(view) + " cannot hold tags"); return null; }
            if (view is View3D v3)
            {
                if (v3.IsPerspective || !v3.IsLocked) { OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "3D view " + DocsUtil.Describe(view) + " must be locked and not perspective to hold tags"); return null; }
            }
            else if (!DocsUtil.IsAnnotatable2D(view)) { OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view " + DocsUtil.Describe(view) + " does not show model elements to tag"); return null; }
            if (p.Rooms && !(view is ViewPlan)) { OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "rooms are tagged in plan views"); return null; }

            var tagged = TaggedInView(doc, view, p.TagCategory, p.Rooms);
            var elements = new FilteredElementCollector(doc, view.Id).OfCategoryId(cat.Id).WhereElementIsNotElementType().ToElements();
            foreach (var e in elements)
            {
                string id = ElementIds.ToWire(e.Id);
                if (p.Rooms && (!(e is Room r) || r.Location is null || !(r.Area > 0))) { p.Untaggable.Add(new KeyValuePair<string, string>(id, "room is not placed or not enclosed")); continue; }
                if (tagged.ContainsKey(ElementIds.ToLong(e.Id))) { p.AlreadyTagged.Add(id); continue; }
                if (Anchor(e, view) is null) { p.Untaggable.Add(new KeyValuePair<string, string>(id, "no location or extent in the view")); continue; }
                p.ToTag.Add(e);
            }
            if (!forReadback)
            {
                int doubledBefore = tagged.Count(kv => kv.Value > 1);
                if (doubledBefore > 0)
                    OpContext.Warning(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Exists,
                        doubledBefore + " " + cat.Name + " element(s) already have more than one tag in view " + DocsUtil.Describe(view) + "; the op leaves them as they are");
            }
            if (!forReadback && elements.Count == 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, "no " + cat.Name + " are visible in view " + DocsUtil.Describe(view) + "; nothing will be tagged");
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var view = p.View;
            if (!p.TagType.IsActive) { p.TagType.Activate(); doc.Regenerate(); }
            var off = ctx.PaperPoint(args, "offset");
            double scale = Math.Max(1, view.Scale);
            var shift = view.RightDirection * (ctx.Paper.LengthToInternal(off.X) * scale) + view.UpDirection * (ctx.Paper.LengthToInternal(off.Y) * scale);
            bool leader = args.GetBool("leader");
            bool vertical = args.GetString("orientation") == "vertical";
            var untaggable = p.Untaggable.Select(x => x.Key).ToList();
            int count = 0;
            foreach (var e in p.ToTag)
            {
                var at = Anchor(e, view)! + shift;
                try
                {
                    Element? tag;
                    if (p.Rooms)
                    {
                        var rt = doc.Create.NewRoomTag(new LinkElementId(e.Id), new UV(at.X, at.Y), view.Id);
                        if (rt != null)
                        {
                            if (rt.GetTypeId() != p.TagType.Id) rt.ChangeTypeId(p.TagType.Id);
                            rt.HasLeader = leader;
                            rt.TagOrientation = vertical ? SpatialElementTagOrientation.Vertical : SpatialElementTagOrientation.Horizontal;
                        }
                        tag = rt;
                    }
                    else
                    {
                        tag = IndependentTag.Create(doc, p.TagType.Id, view.Id, new Reference(e), leader, vertical ? TagOrientation.Vertical : TagOrientation.Horizontal, at);
                    }
                    if (tag is null) { untaggable.Add(ElementIds.ToWire(e.Id)); ctx.Warn("element " + ElementIds.ToWire(e.Id) + " could not be tagged: Revit returned no tag"); continue; }
                    result.Created.Add(ElementIds.ToWire(tag.Id));
                    count++;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex) { untaggable.Add(ElementIds.ToWire(e.Id)); ctx.Warn("element " + ElementIds.ToWire(e.Id) + " could not be tagged: " + ex.Message); }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex) { untaggable.Add(ElementIds.ToWire(e.Id)); ctx.Warn("element " + ElementIds.ToWire(e.Id) + " could not be tagged: " + ex.Message); }
            }
            foreach (var u in p.Untaggable) ctx.Warn("element " + u.Key + " not tagged: " + u.Value);
            result.Outputs.Set("tagged", count);
            result.Outputs.Set("untaggable", JsonArray.FromStrings(untaggable));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues, forReadback: true);
            if (p is null || p.View is null) { check.Fail("could not resolve the request again: " + string.Join("; ", issues.Select(i => i.Message))); check.Apply(result); return; }
            var rb = result.Readback;
            int ok = 0;
            var ours = new HashSet<long>();
            foreach (var idText in result.Created)
            {
                var tag = ElementIds.TryParse(idText, out var id) ? doc.GetElement(id) : null;
                if (tag is null) { check.Fail("tag " + idText + " is missing after the op"); continue; }
                if (tag.OwnerViewId != p.View.Id) check.Fail("tag " + idText + " is not in view " + p.View.Name);
                if (tag.GetTypeId() != p.TagType.Id) check.Fail("tag " + idText + " has type " + (doc.GetElement(tag.GetTypeId()) as ElementType)?.Name + ", requested " + p.TagType.Name);
                var targets = tag is IndependentTag it ? it.GetTaggedLocalElementIds().ToList() : tag is RoomTag rt ? new List<ElementId> { rt.TaggedLocalRoomId } : new List<ElementId>();
                if (targets.Count == 0 || targets.Any(t => !(doc.GetElement(t) is Element te) || te.Category?.Id != p.Category.Id))
                    check.Fail("tag " + idText + " does not reference a " + p.Category.Name + " element");
                else ok++;
                foreach (var t in targets) ours.Add(ElementIds.ToLong(t));
            }
            // Never double-tagged by this op: no element it tagged has more than one tag in the view. Elements that
            // were double-tagged before the op are reported, not failed (Build warned about them).
            var counts = TaggedInView(doc, p.View, p.TagCategory, p.Rooms);
            var doubled = counts.Where(kv => kv.Value > 1 && ours.Contains(kv.Key)).Select(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (doubled.Count > 0) check.Fail(doubled.Count + " element(s) tagged by this op have more than one tag in the view: " + string.Join(", ", doubled.Take(10)));
            rb.Set("doubleTaggedBefore", counts.Count(kv => kv.Value > 1 && !ours.Contains(kv.Key)));
            int wanted = result.Outputs["tagged"] is JsonNumber n && n.TryGetInt64(out long l) ? (int)l : -1;
            rb.Set("tagged", ok);
            if (wanted != ok) check.Fail("tags read back: " + ok + ", reported created: " + wanted);
            rb.Set("alreadyTaggedBefore", Math.Max(0, p.AlreadyTagged.Count - ok));
            rb.Set("stillUntagged", p.ToTag.Count);
            rb.Set("untaggable", (result.Outputs["untaggable"] as JsonArray)?.Count ?? 0);
            rb.Set("tagType", Lookup.FamilyTypeName(p.TagType));
            check.Apply(result);
        }
    }
}
