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
using SA.Revit.Ops.Docs;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// tag.place: chosen elements tagged in one view, each tag where the job says.
    ///
    /// <para><b>API.</b> Element tags: IndependentTag.Create(doc, tagTypeId, viewId, new Reference(element), leader,
    /// orientation, point) when a type is given, else IndependentTag.Create(doc, viewId, reference, leader,
    /// TagMode.TM_ADDBY_CATEGORY, orientation, point), which uses the category's loaded tag. Material tags:
    /// TM_ADDBY_MATERIAL on the reference of the element's first face that has a material (Options with
    /// ComputeReferences in the view). Room tags: Document.Create.NewRoomTag(new LinkElementId(roomId), uv, viewId), plans
    /// only. Area tags: Document.Create.NewAreaTag(areaPlan, area, uv), area plans only.</para>
    ///
    /// <para><b>Position.</b> <c>point</c> is the head in the view's frame; <c>offset</c> is a head offset from the
    /// element's anchor (location point, curve middle, else bounding box centre, as tag.all) along the view's right and
    /// up directions, in paper units times the view scale. The API documents the create point as the head without a
    /// leader and as the leader end with one, so a leader tag is created at the anchor and its TagHeadPosition then
    /// set to the head.</para>
    ///
    /// <para><b>Refused.</b> Elements already tagged in the view by a tag of the same category (or a multi-category
    /// tag), unless allowSecondTag (tag once, dimensions-annotation.md); categories with no loaded tag; drafting
    /// views, legends, sheets, schedules, 3D views and templates. Tagging changes no tagged element, so nothing is
    /// declared.</para>
    /// </summary>
    public sealed class TagPlace : OpHandler
    {
        public override string Name => "tag.place";

        private enum Kind { Element, Material, Room, Area }

        private sealed class Item
        {
            public Element Element = null!;
            public Kind Kind;
            public FamilySymbol? Type;
            public bool Leader;
            public TagOrientation Orientation;
            public XYZ Anchor = XYZ.Zero;
            public XYZ Head = XYZ.Zero;
            public Reference? Reference;
            public string Path = "";
        }

        private sealed class Plan
        {
            public View View = null!;
            public List<Item> Items = new List<Item>();
        }

        private static FamilySymbol? TagType(Document doc, string name, BuiltInCategory tagCat, string path, List<Issue> issues)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && (ElementIds.ToBuiltInCategory(s.Category.Id) == tagCat || ElementIds.ToBuiltInCategory(s.Category.Id) == BuiltInCategory.OST_MultiCategoryTags)).ToList();
            var f = name.Contains(" : ") ? Lookup.ByName(all, name, "tag type", Lookup.FamilyTypeName) : Lookup.ByName(all, name, "tag type");
            if (!f.Ok) OpContext.Error(issues, path, f.Code, f.Error! + " (tag types of " + tagCat + " or multi-category)");
            return f.Value;
        }

        private static bool HasTagLoaded(Document doc, BuiltInCategory tagCat) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tagCat).GetElementCount() > 0;

        private static Reference? MaterialFace(Element e, View v)
        {
            var opts = new Options { ComputeReferences = true, View = v };
            var geo = e.get_Geometry(opts);
            if (geo is null) return null;
            foreach (var obj in geo)
            {
                // Only symbol geometry without a transform carries references Revit accepts for new annotation;
                // GetInstanceGeometry returns a copy whose references are documented as unusable for that.
                IEnumerable<GeometryObject> items = obj is GeometryInstance gi ? gi.GetSymbolGeometry().Cast<GeometryObject>() : new[] { obj };
                foreach (var g in items)
                    if (g is Solid s)
                        foreach (Face f in s.Faces)
                            if (f.Reference != null && ElementIds.IsValid(f.MaterialElementId)) return f.Reference;
            }
            return null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (pending || v is null) return null;
            if (!DocsUtil.IsAnnotatable2D(v) || v.IsTemplate)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "tags go in plans, sections, elevations and detail views; not in " + DocsUtil.Describe(v));
                return null;
            }
            var p = new Plan { View = v };
            double scale = Math.Max(1, v.Scale);
            var right = v.RightDirection;
            var up = v.UpDirection;
            var opOffset = ctx.PaperPoint(args, "offset");
            var tagged = new Dictionary<string, Dictionary<long, int>>(StringComparer.Ordinal);
            var areaTagged = new HashSet<long>(new FilteredElementCollector(doc, v.Id).OfClass(typeof(SpatialElementTag)).OfType<AreaTag>()
                .Where(t => t.Area != null).Select(t => ElementIds.ToLong(t.Area.Id)));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool allowSecond = args.GetBool("allowSecondTag");
            foreach (var a in args.GetObjectList("tags"))
            {
                var e = W2.Element(ctx, a.Get("element"), ctx.FieldPath(a, "element"), issues, out bool ep);
                if (ep || e is null) continue;
                var item = new Item
                {
                    Element = e,
                    Path = a.Path,
                    Leader = a.Has("leader") ? a.GetBool("leader") : args.GetBool("leader"),
                    Orientation = (a.GetStringOrNull("orientation") ?? args.GetString("orientation")) == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal,
                };
                bool material = a.GetBool("material");
                item.Kind = material ? Kind.Material : e is Room ? Kind.Room : e is Area ? Kind.Area : Kind.Element;
                var anchor = TagAll.Anchor(e, v);
                if (anchor is null) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Invalid, "element " + ElementIds.ToWire(e.Id) + " has no location to tag"); continue; }
                item.Anchor = anchor;
                if (a.Has("point")) item.Head = W2.ToModel(ctx, v, a.GetPoint2("point"));
                else
                {
                    var off = a.Has("offset") ? ctx.PaperPoint(a, "offset") : opOffset;
                    item.Head = anchor + right * (ctx.Paper.LengthToInternal(off.X) * scale) + up * (ctx.Paper.LengthToInternal(off.Y) * scale);
                }
                string? typeName = a.GetStringOrNull("tagType") ?? args.GetStringOrNull("tagType");
                string typePath = a.Has("tagType") ? ctx.FieldPath(a, "tagType") : ctx.FieldPath(args, "tagType");
                string key = ElementIds.ToWire(e.Id) + "/" + item.Kind;
                if (!seen.Add(key) && !allowSecond) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Invalid, "element " + ElementIds.ToWire(e.Id) + " is listed twice"); continue; }
                switch (item.Kind)
                {
                    case Kind.Room:
                        if (!(v is ViewPlan)) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.NotAllowed, "room tags are placed in plans by this op"); continue; }
                        if (((Room)e).Area <= 0) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Invalid, "room " + ElementIds.ToWire(e.Id) + " is not placed or not enclosed"); continue; }
                        if (typeName != null) item.Type = TagType(doc, typeName, BuiltInCategory.OST_RoomTags, typePath, issues);
                        else if (!HasTagLoaded(doc, BuiltInCategory.OST_RoomTags)) { OpContext.Error(issues, typePath, RevitIssueCodes.NotFound, "no room tag family is loaded"); continue; }
                        if (!allowSecond && Tagged(doc, v, tagged, BuiltInCategory.OST_RoomTags, true).ContainsKey(ElementIds.ToLong(e.Id)))
                            OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Exists, "room " + ElementIds.ToWire(e.Id) + " is already tagged in this view");
                        break;
                    case Kind.Area:
                        if (v.ViewType != ViewType.AreaPlan) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.NotAllowed, "area tags go in area plans"); continue; }
                        if (typeName != null) item.Type = TagType(doc, typeName, BuiltInCategory.OST_AreaTags, typePath, issues);
                        if (!allowSecond && areaTagged.Contains(ElementIds.ToLong(e.Id)))
                            OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Exists, "area " + ElementIds.ToWire(e.Id) + " is already tagged in this view");
                        break;
                    case Kind.Material:
                        item.Reference = MaterialFace(e, v);
                        if (item.Reference is null) { OpContext.Error(issues, ctx.FieldPath(a, "material"), RevitIssueCodes.Invalid, "element " + ElementIds.ToWire(e.Id) + " shows no face with a material in this view"); continue; }
                        if (typeName != null) item.Type = TagType(doc, typeName, BuiltInCategory.OST_MaterialTags, typePath, issues);
                        else if (!HasTagLoaded(doc, BuiltInCategory.OST_MaterialTags)) { OpContext.Error(issues, typePath, RevitIssueCodes.NotFound, "no material tag family is loaded"); continue; }
                        break;
                    default:
                        var cat = e.Category;
                        var tagCat = cat is null ? null : TagAll.TagCategoryFor(doc, cat);
                        if (tagCat is null) { OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.NotAllowed, "elements of " + (cat?.Name ?? "this kind") + " have no tag category"); continue; }
                        if (typeName != null) item.Type = TagType(doc, typeName, tagCat.Value, typePath, issues);
                        else if (!HasTagLoaded(doc, tagCat.Value)) { OpContext.Error(issues, typePath, RevitIssueCodes.NotFound, "no " + tagCat.Value + " family is loaded; load one or give tagType"); continue; }
                        if (!allowSecond && Tagged(doc, v, tagged, tagCat.Value, false).ContainsKey(ElementIds.ToLong(e.Id)))
                            OpContext.Error(issues, ctx.FieldPath(a, "element"), RevitIssueCodes.Exists, "element " + ElementIds.ToWire(e.Id) + " is already tagged in this view (tag once)");
                        item.Reference = new Reference(e);
                        break;
                }
                p.Items.Add(item);
            }
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static Dictionary<long, int> Tagged(Document doc, View v, Dictionary<string, Dictionary<long, int>> cache, BuiltInCategory tagCat, bool rooms)
        {
            string k = tagCat.ToString();
            if (!cache.TryGetValue(k, out var d))
            {
                d = TagAll.TaggedInView(doc, v, ElementIds.FromBuiltInCategory(tagCat), rooms);
                cache[k] = d;
            }
            return d;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var doc = ctx.Doc;
            var ids = new List<ElementId>();
            foreach (var it in p.Items)
            {
                if (it.Type != null && !it.Type.IsActive) it.Type.Activate();
                Element tag;
                switch (it.Kind)
                {
                    case Kind.Room:
                    {
                        var rt = doc.Create.NewRoomTag(new LinkElementId(it.Element.Id), new UV(it.Head.X, it.Head.Y), p.View.Id)
                                 ?? throw new OpFailedException("create_failed", it.Path + ": Revit did not tag the room");
                        if (it.Type != null) rt.ChangeTypeId(it.Type.Id);
                        if (it.Leader) rt.HasLeader = true;
                        tag = rt;
                        break;
                    }
                    case Kind.Area:
                    {
                        var at = doc.Create.NewAreaTag((ViewPlan)p.View, (Area)it.Element, new UV(it.Head.X, it.Head.Y))
                                 ?? throw new OpFailedException("create_failed", it.Path + ": Revit did not tag the area");
                        if (it.Type != null) at.ChangeTypeId(it.Type.Id);
                        if (it.Leader) at.HasLeader = true;
                        tag = at;
                        break;
                    }
                    default:
                    {
                        var at = it.Leader ? it.Anchor : it.Head;
                        IndependentTag t;
                        if (it.Type != null && it.Kind == Kind.Element)
                            t = IndependentTag.Create(doc, it.Type.Id, p.View.Id, it.Reference!, it.Leader, it.Orientation, at);
                        else
                        {
                            t = IndependentTag.Create(doc, p.View.Id, it.Reference!, it.Leader, it.Kind == Kind.Material ? TagMode.TM_ADDBY_MATERIAL : TagMode.TM_ADDBY_CATEGORY, it.Orientation, at);
                            if (t != null && it.Type != null) t.ChangeTypeId(it.Type.Id);
                        }
                        if (t is null) throw new OpFailedException("create_failed", it.Path + ": Revit did not create the tag");
                        if (it.Leader) t.TagHeadPosition = it.Head;
                        tag = t;
                        break;
                    }
                }
                ids.Add(tag.Id);
            }
            result.Created.AddRange(ids.Select(ElementIds.ToWire));
            result.Outputs.Set("tags", W2.Ids(ids));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            var doc = ctx.Doc;
            var issues = new List<Issue>();
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out _);
            var requests = args.GetObjectList("tags");
            if (v is null) { check.Fail("could not resolve the view again"); check.Apply(result); return; }
            var recs = new JsonArray();
            for (int i = 0; i < result.Created.Count; i++)
            {
                string tid = result.Created[i];
                var tag = ElementIds.TryParse(tid, out var id) ? doc.GetElement(id) : null;
                var rec = new JsonObject().Add("id", tid);
                recs.Add(rec);
                if (tag is null) { check.Fail("tag " + tid + " is missing after the op"); continue; }
                var a = i < requests.Count ? requests[i] : null;
                string? want = a?.GetRefId("element");
                if (tag.OwnerViewId != v.Id) check.Fail("tag " + tid + " is not in " + DocsUtil.Describe(v));
                XYZ? head = null;
                string? taggedId = null;
                switch (tag)
                {
                    case IndependentTag it:
                        head = it.TagHeadPosition;
                        var local = it.GetTaggedLocalElementIds();
                        taggedId = local.Count > 0 ? ElementIds.ToWire(local.First()) : null;
                        rec.Add("leader", it.HasLeader);
                        break;
                    case RoomTag rt:
                        head = rt.TagHeadPosition;
                        taggedId = ElementIds.IsValid(rt.TaggedLocalRoomId) ? ElementIds.ToWire(rt.TaggedLocalRoomId) : null;
                        rec.Add("leader", rt.HasLeader);
                        break;
                    case AreaTag at:
                        head = at.TagHeadPosition;
                        taggedId = at.Area != null ? ElementIds.ToWire(at.Area.Id) : null;
                        rec.Add("leader", at.HasLeader);
                        break;
                }
                check.Text(rec, "element", taggedId, want);
                rec.Add("type", W2.TypeName(doc, tag));
                string? typeName = a?.GetStringOrNull("tagType") ?? args.GetStringOrNull("tagType");
                if (typeName != null)
                {
                    string got = W2.TypeName(doc, tag);
                    string bare = doc.GetElement(tag.GetTypeId())?.Name ?? "";
                    if (!string.Equals(got, typeName, StringComparison.OrdinalIgnoreCase) && !string.Equals(bare, typeName, StringComparison.OrdinalIgnoreCase))
                        check.Fail("tag " + tid + ": type reads '" + got + "', requested '" + typeName + "'");
                }
                Point2? wantHead = null;
                if (a != null && a.Has("point")) wantHead = a.GetPoint2("point");
                else if (a != null && want != null && ElementIds.TryParse(want, out var weid) && doc.GetElement(weid) is Element te && TagAll.Anchor(te, v) is XYZ anchor)
                {
                    var off = a.Has("offset") ? ctx.PaperPoint(a, "offset") : ctx.PaperPoint(args, "offset");
                    double scale = Math.Max(1, v.Scale);
                    wantHead = W2.FromModel(ctx, v, anchor + v.RightDirection * (ctx.Paper.LengthToInternal(off.X) * scale) + v.UpDirection * (ctx.Paper.LengthToInternal(off.Y) * scale));
                }
                if (head != null && wantHead.HasValue)
                {
                    var u = W2.FrameUnits(ctx, v);
                    check.Point(rec, "head", u.PointToInternal(W2.FromModel(ctx, v, head), 0), wantHead.Value, "tags[" + i + "].head", u);
                }
                else if (head != null) rec.Add("head", DocsUtil.Xy(W2.FromModel(ctx, v, head).X, W2.FromModel(ctx, v, head).Y));
                else check.Fail("tag " + tid + " has no head position to read back");
            }
            if (result.Created.Count != requests.Count) check.Fail(result.Created.Count + " tag(s) created, " + requests.Count + " requested");
            result.Readback.Set("tags", recs);
            check.Apply(result);
        }
    }
}
