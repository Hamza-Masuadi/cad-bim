using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// group.place: a new model group from elements (Creation.ItemFactoryBase.NewGroup, then the group type renamed),
    /// or instances of an existing model group type at points (PlaceGroup; the group origin goes at the point).
    ///
    /// Elements already in a group, element types, and view-specific elements (which would make a detail group) are
    /// refused. The grouped elements are declared as modified. The API documents that NewGroup can add closely
    /// connected elements (sketches, sketch planes) to the group, so readback checks that every requested element is a
    /// member, not that the member count is equal. A new type name that an existing model group type already has is
    /// refused. Rotation of placed instances is applied with ElementTransformUtils.RotateElement about the vertical
    /// through the point; LocationPoint.Rotation is documented as unsupported for groups, so it is not read back.
    /// </summary>
    public sealed class GroupPlace : OpHandler
    {
        public override string Name => "group.place";

        private sealed class Plan
        {
            public List<ElementId> Members = new List<ElementId>();
            public string? NewName;
            public GroupType? Type;
            public Level? Level;
            public List<XYZ> Points = new List<XYZ>();
            public double Rotation;
        }

        private static List<GroupType> ModelGroupTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(GroupType)).OfCategory(BuiltInCategory.OST_IOSModelGroups).Cast<GroupType>().ToList();

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan { Rotation = u.AngleToInternal(args.GetAngle("rotation")) };
            if (args.Has("elements"))
            {
                var seen = new HashSet<long>();
                foreach (var kv in Kit.IdList(ctx, args, "elements", out _))
                {
                    if (!ElementIds.TryParse(kv.Value, out var id) || !(doc.GetElement(id) is Element e)) { OpContext.Error(issues, kv.Key, RevitIssueCodes.NotFound, "element " + kv.Value + " does not exist"); continue; }
                    if (!seen.Add(ElementIds.ToLong(id))) continue;
                    if (e is ElementType) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is a type; only placed elements can be grouped");
                    else if (ElementIds.IsValid(e.GroupId)) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is already in group " + Kit.Id(e.GroupId));
                    else if (ElementIds.IsValid(e.OwnerViewId)) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is view-specific; only model elements make a model group");
                    else if (e is Group) OpContext.Error(issues, kv.Key, RevitIssueCodes.Invalid, "element " + kv.Value + " is a group; nested groups are not made by this op");
                    else { p.Members.Add(id); ctx.DeclareModifies(id); }
                }
                p.NewName = args.GetString("name");
                string at = ctx.FieldPath(args, "name");
                if (Kit.BadTypeName(p.NewName) is string bad) OpContext.Error(issues, at, RevitIssueCodes.Invalid, bad);
                else if (ModelGroupTypes(doc).FirstOrDefault(t => string.Equals(t.Name, p.NewName, StringComparison.OrdinalIgnoreCase)) is GroupType clash)
                    OpContext.Error(issues, at, RevitIssueCodes.Exists, "a model group type named '" + clash.Name + "' exists (id " + Kit.Id(clash.Id) + "); place it with groupType or choose another name");
            }
            else
            {
                var t = Lookup.ByName(ModelGroupTypes(doc), args.GetString("groupType"), "model group type");
                if (!t.Ok) OpContext.Error(issues, ctx.FieldPath(args, "groupType"), t.Code, t.Error!);
                else p.Type = t.Value;
            }
            if (args.Has("points"))
            {
                var lv = Lookup.Level(doc, args.GetString("level"));
                if (!lv.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!);
                else
                {
                    p.Level = lv.Value;
                    var pts = args.GetPoint2List("points");
                    for (int i = 0; i < pts.Count; i++)
                    {
                        if (Kit.CoordinateError(pts[i], u) is string ce) OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "points"), i), RevitIssueCodes.Invalid, ce);
                        p.Points.Add(u.PointToInternal(pts[i], p.Level!.ProjectElevation));
                    }
                }
            }
            else if (Math.Abs(p.Rotation) > 1e-9)
                OpContext.Warning(issues, ctx.FieldPath(args, "rotation"), RevitIssueCodes.Invalid, "rotation applies to instances placed at points; there are none, so it is ignored");
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            GroupType type;
            if (p!.Members.Count > 0)
            {
                var g = doc.Create.NewGroup(p.Members) ?? throw new OpFailedException("create_failed", "Revit did not make a group from the " + p.Members.Count + " element(s)");
                type = g.GroupType;
                type.Name = p.NewName!;
                doc.Regenerate();
                result.Created.Add(Kit.Id(type.Id));
                result.Created.Add(Kit.Id(g.Id));
                result.Modified.AddRange(p.Members.Select(Kit.Id));
                result.Outputs.Add("group", Kit.Id(g.Id));
            }
            else
            {
                type = p.Type!;
                result.Outputs.Add("group", JsonValue.Null);
            }
            var placed = new List<string>();
            foreach (var pt in p.Points)
            {
                var gi = doc.Create.PlaceGroup(pt, type) ?? throw new OpFailedException("create_failed", "Revit did not place group '" + type.Name + "' at a point");
                if (Math.Abs(p.Rotation) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, gi.Id, Line.CreateBound(pt, pt + XYZ.BasisZ), p.Rotation);
                placed.Add(Kit.Id(gi.Id));
            }
            result.Created.AddRange(placed);
            result.Outputs.Add("groupType", Kit.Id(type.Id));
            result.Outputs.Add("placed", Ids(placed));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var rb = new JsonObject();
            string? typeId = result.Outputs.GetStringOrNull("groupType");
            if (typeId is null || !ElementIds.TryParse(typeId, out var tid) || !(doc.GetElement(tid) is GroupType type))
            {
                check.Fail("the group type no longer exists after commit");
                check.Apply(result);
                return;
            }
            string? wantType = args.GetStringOrNull("name");
            if (wantType is null && args.GetStringOrNull("groupType") is string gt)
                wantType = string.Equals(type.Name, gt, StringComparison.OrdinalIgnoreCase) ? type.Name : gt;
            check.Text(rb, "groupType", type.Name, wantType);
            if (result.Outputs.GetStringOrNull("group") is string gid)
            {
                if (!ElementIds.TryParse(gid, out var id) || !(doc.GetElement(id) is Group g)) check.Fail("group " + gid + " no longer exists");
                else
                {
                    var members = new HashSet<long>(g.GetMemberIds().Select(ElementIds.ToLong));
                    var missing = Kit.IdList(ctx, args, "elements", out _).Select(kv => kv.Value).Distinct()
                        .Where(s => !ElementIds.TryParse(s, out var eid) || !members.Contains(ElementIds.ToLong(eid))).ToList();
                    rb.Add("members", members.Count);
                    if (missing.Count > 0) check.Fail("element(s) " + string.Join(", ", missing.Take(10)) + " are not members of the new group");
                    check.Text(rb, "groupTypeOfGroup", Kit.Id(g.GroupType.Id), typeId, "group type of the new group");
                }
            }
            var placed = result.Outputs.TryGetValue("placed", out var pv) && pv is JsonArray pa ? pa : new JsonArray();
            var pts = args.Has("points") ? args.GetPoint2List("points") : new List<Point2>();
            var items = new JsonArray();
            if (placed.Count != pts.Count) check.Fail(placed.Count + " instance(s) placed, requested " + pts.Count);
            for (int i = 0; i < placed.Count; i++)
            {
                string s = placed[i] is JsonString js ? js.Value : "";
                if (!ElementIds.TryParse(s, out var id) || !(doc.GetElement(id) is Group gi)) { check.Fail("group instance " + s + " no longer exists"); continue; }
                var o = new JsonObject().Add("id", s);
                check.Text(o, "groupType", Kit.Id(gi.GroupType.Id), typeId, "instance " + (i + 1) + " group type");
                if (gi.Location is LocationPoint lp) check.Point(o, "point", lp.Point, i < pts.Count ? pts[i] : (Point2?)null, "instance " + (i + 1) + " origin");
                else check.Fail("instance " + (i + 1) + " has no location point");
                items.Add(o);
            }
            rb.Add("placed", items);
            if (Math.Abs(args.GetAngle("rotation")) > 1e-9 && placed.Count > 0) rb.Add("rotationChecked", false);
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            check.Apply(result);
        }
    }
}
