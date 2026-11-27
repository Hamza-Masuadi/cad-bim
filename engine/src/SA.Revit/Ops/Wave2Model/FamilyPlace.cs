using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.FromCad;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Model
{
    /// <summary>
    /// family.place: instances of a loaded family type at points, by the family's placement type.
    ///
    /// <list type="bullet">
    /// <item>OneLevelBased, TwoLevelsBased: NewFamilyInstance(XYZ, symbol, Level, NonStructural) at the level's elevation;
    /// offset goes into Elevation from Level (INSTANCE_ELEVATION_PARAM) or Offset from Host; rotation is applied
    /// afterwards about the vertical through the point.</item>
    /// <item>OneLevelBasedHosted: NewFamilyInstance(XYZ, symbol, host, Level, NonStructural). On a wall the point is put on
    /// the wall's location line (no rotation: the wall orients it); on a floor, ceiling or roof the point is dropped to
    /// the host's top face (bottom face for ceilings) along the vertical.</item>
    /// <item>WorkPlaneBased with host: NewFamilyInstance(Reference, XYZ, XYZ, symbol) on the chosen host face, from
    /// HostObjectUtils (GetSideFaces, GetTopFaces, GetBottomFaces); the face that holds the point is used.</item>
    /// <item>WorkPlaneBased without host: the same call on a level (Level.GetPlaneReference) or a named reference plane.</item>
    /// </list>
    /// Line-based, view-based and adaptive families are refused. The host is declared as modified, conservatively:
    /// hosted instances can cut it.
    ///
    /// Readback: each instance's type, location point (against the location the op computed), level, host,
    /// rotation (where it was applied about the vertical), flips, mark and parameters.
    ///
    /// With fromPlacements instead of type and points, one instance per block reference of a placements.json; see
    /// <see cref="PlacementsPlacer"/>.
    /// </summary>
    public sealed class FamilyPlace : OpHandler
    {
        public override string Name => "family.place";

        private enum Mode { Level, Hosted, Face, Plane }

        private sealed class Target
        {
            public XYZ Location = XYZ.Zero;
            public Reference? Face;
            public XYZ? RefDir;
        }

        private sealed class Plan
        {
            public FamilySymbol Symbol = null!;
            public Mode Mode;
            public Level? Level;
            public HostObject? Host;
            public double Offset;
            public bool OffsetAsParam;
            public double Rotation;
            public bool RotateAfter;
            public List<Target> Targets = new List<Target>();
        }

        internal static Found<FamilySymbol> Symbol(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var col = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
            if (args.GetStringOrNull("category") is string cname)
            {
                var cat = Lookup.Category(doc, cname, out string? cerr);
                if (cat is null) return new Found<FamilySymbol>(null, cerr, RevitIssueCodes.NotFound);
                col = col.OfCategoryId(cat.Id);
            }
            string name = args.GetString("type");
            return Lookup.ByName(col.Cast<FamilySymbol>(), name, "family type", Lookup.FamilyTypeName);
        }

        private static IList<Reference> FaceRefs(HostObject h, string face)
        {
            switch (face)
            {
                case "top": return HostObjectUtils.GetTopFaces(h);
                case "bottom": return HostObjectUtils.GetBottomFaces(h);
                case "exterior": return HostObjectUtils.GetSideFaces(h, ShellLayerType.Exterior);
                default: return HostObjectUtils.GetSideFaces(h, ShellLayerType.Interior);
            }
        }

        /// <summary>Where the vertical through <paramref name="plan"/> meets one of the faces; the highest hit.</summary>
        private static XYZ? Drop(Document doc, Element host, IList<Reference> refs, XYZ plan, out Reference? hit)
        {
            hit = null;
            var bb = host.get_BoundingBox(null);
            if (bb is null) return null;
            var line = Line.CreateBound(new XYZ(plan.X, plan.Y, bb.Min.Z - 1), new XYZ(plan.X, plan.Y, bb.Max.Z + 1));
            XYZ? best = null;
            foreach (var r in refs)
            {
                if (!(host.GetGeometryObjectFromReference(r) is Face f)) continue;
                if (f.Intersect(line, out IntersectionResultArray results) != SetComparisonResult.Overlap || results is null) continue;
                foreach (IntersectionResult ir in results)
                    if (best is null || ir.XYZPoint.Z > best.Z) { best = ir.XYZPoint; hit = r; }
            }
            return best;
        }

        /// <summary>The closest point on one of the faces, with the face's normal there.</summary>
        private static XYZ? Nearest(Element host, IList<Reference> refs, XYZ probe, out Reference? hit, out XYZ normal, out double distance)
        {
            hit = null;
            normal = XYZ.BasisX;
            distance = double.MaxValue;
            XYZ? best = null;
            foreach (var r in refs)
            {
                if (!(host.GetGeometryObjectFromReference(r) is Face f)) continue;
                var ir = f.Project(probe);
                if (ir is null || ir.Distance >= distance) continue;
                distance = ir.Distance;
                best = ir.XYZPoint;
                hit = r;
                normal = f.ComputeNormal(ir.UVPoint);
            }
            return best;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            string typeAt = ctx.FieldPath(args, "type");
            if (args.GetStringOrNull("category") is string cname && Lookup.Category(doc, cname, out string? cerr) is null)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, cerr!);
                return null;
            }
            var sym = Symbol(ctx, args, issues);
            if (!sym.Ok) { OpContext.Error(issues, typeAt, sym.Code, sym.Error!); return null; }
            p.Symbol = sym.Value!;
            var placement = p.Symbol.Family.FamilyPlacementType;
            bool hasHost = args.Has("host");
            bool hostPending = ctx.IsPending(args, "host");
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                case FamilyPlacementType.TwoLevelsBased:
                    p.Mode = Mode.Level; break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    p.Mode = Mode.Hosted; break;
                case FamilyPlacementType.WorkPlaneBased:
                    p.Mode = hasHost ? Mode.Face : Mode.Plane; break;
                default:
                    OpContext.Error(issues, typeAt, RevitIssueCodes.Invalid, "'" + Lookup.FamilyTypeName(p.Symbol) + "' is a " + placement +
                        " family; family.place places level-based, hosted, face-based and work-plane-based families only");
                    return null;
            }
            string name = Lookup.FamilyTypeName(p.Symbol);

            if (hasHost && !hostPending)
            {
                var h = Kit.Element(doc, args.Get("host"), out string herr);
                if (h is null) OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.NotFound, herr);
                else if (!(h is Wall || h is Floor || h is Ceiling || h is RoofBase)) OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "element " + Kit.Id(h.Id) + " is a " + (h.Category?.Name ?? h.GetType().Name) + "; the host must be a wall, floor, ceiling or roof");
                else p.Host = (HostObject)h;
            }
            if (p.Mode == Mode.Level && (hasHost || args.Has("workPlane")))
                OpContext.Error(issues, ctx.FieldPath(args, hasHost ? "host" : "workPlane"), RevitIssueCodes.Invalid, "'" + name + "' is a level-based family; it takes a level, not a host or work plane");
            if (p.Mode == Mode.Hosted && !hasHost)
                OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "'" + name + "' is a hosted family; give the wall, floor, ceiling or roof as host");
            if (p.Mode == Mode.Hosted && args.Has("workPlane"))
                OpContext.Error(issues, ctx.FieldPath(args, "workPlane"), RevitIssueCodes.Invalid, "'" + name + "' is a hosted family; it takes a host, not a work plane");
            if (args.Has("face") && p.Mode != Mode.Face)
                OpContext.Error(issues, ctx.FieldPath(args, "face"), RevitIssueCodes.Invalid, "face applies only to face-based families placed on a host");
            if (p.Mode == Mode.Face && !args.Has("face"))
                OpContext.Error(issues, ctx.FieldPath(args, "face"), RevitIssueCodes.Invalid, "'" + name + "' is face-based; say which face of the host to place on (top, bottom, exterior or interior)");

            if (args.GetStringOrNull("level") is string ln)
            {
                var lv = Lookup.Level(doc, ln);
                if (!lv.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!);
                else p.Level = lv.Value;
            }
            else if (p.Host != null && doc.GetElement(p.Host.LevelId) is Level hl) p.Level = hl;
            if (p.Level is null && !args.Has("level") && !hostPending &&
                (p.Mode == Mode.Level || p.Mode == Mode.Hosted || (p.Mode == Mode.Plane && !args.Has("workPlane"))))
                OpContext.Error(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Invalid, "give the level to place '" + name + "' on");

            p.Offset = u.LengthToInternal(args.GetLength("offset"));
            p.Rotation = u.AngleToInternal(args.GetAngle("rotation"));
            bool rotated = Math.Abs(p.Rotation) > 1e-9;

            var pts = args.GetPoint2List("points");
            for (int i = 0; i < pts.Count; i++)
                if (Kit.CoordinateError(pts[i], u) is string ce) OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "points"), i), RevitIssueCodes.Invalid, ce);

            var bic = p.Symbol.Category?.BuiltInCategory ?? BuiltInCategory.INVALID;
            Kit.CheckParameterNames(ctx, args, "parameters", bic, null, issues, "instances of '" + name + "'");
            if (args.GetStringOrNull("mark") is string mark)
            {
                var marks = pts.Count > 1 ? Enumerable.Range(1, pts.Count).Select(i => mark + "-" + i).ToList() : new List<string> { mark };
                foreach (var m in marks)
                    if (bic != BuiltInCategory.INVALID && Kit.WithMark(doc, bic, m).Count > 0)
                        OpContext.Warning(issues, ctx.FieldPath(args, "mark"), RevitIssueCodes.Exists, "mark '" + m + "' is already used in " + p.Symbol.Category!.Name);
            }
            if (Kit.HasErrors(issues) || hostPending) return Kit.HasErrors(issues) ? null : p;

            switch (p.Mode)
            {
                case Mode.Level:
                    p.OffsetAsParam = true;
                    p.RotateAfter = rotated;
                    foreach (var pt in pts) p.Targets.Add(new Target { Location = u.PointToInternal(pt, p.Level!.ProjectElevation) });
                    break;

                case Mode.Hosted:
                    p.OffsetAsParam = true;
                    if (p.Host is Wall wall)
                    {
                        if (rotated) OpContext.Error(issues, ctx.FieldPath(args, "rotation"), RevitIssueCodes.Invalid, "a wall-hosted instance takes its orientation from the wall; rotation must be 0");
                        if (!(wall.Location is LocationCurve lc)) { OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "the wall has no location line"); break; }
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var plan = u.PointToInternal(pts[i], lc.Curve.GetEndPoint(0).Z);
                            var along = Openings.Along(lc.Curve, plan);
                            var on = along.HasValue ? Openings.At(lc.Curve, along.Value) : null;
                            double off = on is null ? double.MaxValue : new XYZ(on.X - plan.X, on.Y - plan.Y, 0).GetLength();
                            if (on is null || off > wall.Width / 2 + u.LengthToleranceInternal)
                                OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "points"), i), RevitIssueCodes.Invalid,
                                    "the point is not on wall " + Kit.Id(wall.Id) + (on is null ? "" : " (" + Kit.Num(u.LengthFromInternal(off)) + " from its location line)"));
                            else p.Targets.Add(new Target { Location = new XYZ(on.X, on.Y, p.Level!.ProjectElevation) });
                        }
                    }
                    else
                    {
                        p.RotateAfter = rotated;
                        string face = p.Host is Ceiling ? "bottom" : "top";
                        IList<Reference> refs;
                        try { refs = FaceRefs(p.Host!, face); }
                        catch (Autodesk.Revit.Exceptions.ApplicationException e) { OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "the host's " + face + " faces cannot be read: " + e.Message); break; }
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var at = Drop(doc, p.Host!, refs, u.PointToInternal(pts[i], 0), out _);
                            if (at is null) OpContext.Error(issues, JsonPath.Index(ctx.FieldPath(args, "points"), i), RevitIssueCodes.Invalid, "the point is not over the " + face + " face of host " + Kit.Id(p.Host!.Id));
                            else p.Targets.Add(new Target { Location = at });
                        }
                    }
                    break;

                case Mode.Face:
                {
                    string face = args.GetString("face");
                    bool side = face == "exterior" || face == "interior";
                    if (side && !(p.Host is Wall)) { OpContext.Error(issues, ctx.FieldPath(args, "face"), RevitIssueCodes.Invalid, "exterior and interior faces belong to walls; use top or bottom for this host"); break; }
                    if (!side && p.Host is Wall) { OpContext.Error(issues, ctx.FieldPath(args, "face"), RevitIssueCodes.Invalid, "a wall has exterior and interior faces, not top or bottom"); break; }
                    if (side && rotated) OpContext.Error(issues, ctx.FieldPath(args, "rotation"), RevitIssueCodes.Invalid, "on a wall face the instance is aligned with the wall; rotation must be 0");
                    IList<Reference> refs;
                    try { refs = FaceRefs(p.Host!, face); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException e) { OpContext.Error(issues, ctx.FieldPath(args, "face"), RevitIssueCodes.Invalid, "the host's " + face + " faces cannot be read: " + e.Message); break; }
                    p.OffsetAsParam = !side;
                    var dir = new XYZ(Math.Cos(p.Rotation), Math.Sin(p.Rotation), 0);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        string at = JsonPath.Index(ctx.FieldPath(args, "points"), i);
                        if (side)
                        {
                            var probe = u.PointToInternal(pts[i], (p.Level?.ProjectElevation ?? 0) + p.Offset);
                            var on = Nearest(p.Host!, refs, probe, out var r, out var n, out double d);
                            if (on is null || r is null || d > ((Wall)p.Host!).Width + u.LengthToleranceInternal)
                                OpContext.Error(issues, at, RevitIssueCodes.Invalid, "the point is not on the " + face + " face of wall " + Kit.Id(p.Host!.Id));
                            else p.Targets.Add(new Target { Location = on, Face = r, RefDir = XYZ.BasisZ.CrossProduct(n).Normalize() });
                        }
                        else
                        {
                            var on = Drop(doc, p.Host!, refs, u.PointToInternal(pts[i], 0), out var r);
                            if (on is null || r is null) OpContext.Error(issues, at, RevitIssueCodes.Invalid, "the point is not over the " + face + " face of host " + Kit.Id(p.Host!.Id));
                            else p.Targets.Add(new Target { Location = on, Face = r, RefDir = dir });
                        }
                    }
                    break;
                }

                case Mode.Plane:
                {
                    var dir = new XYZ(Math.Cos(p.Rotation), Math.Sin(p.Rotation), 0);
                    Reference? plane = null;
                    double? z = null;
                    Plane? vertical = null;
                    XYZ? vdir = null;
                    if (args.GetStringOrNull("workPlane") is string wp)
                    {
                        string at = ctx.FieldPath(args, "workPlane");
                        var lv = Lookup.Level(doc, wp);
                        var planes = new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
                        var rp = Lookup.ByName(planes, wp, "reference plane");
                        if (lv.Ok && rp.Ok) { OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, "'" + wp + "' names both a level and a reference plane"); break; }
                        if (lv.Ok) { plane = lv.Value!.GetPlaneReference(); z = lv.Value.ProjectElevation; }
                        else if (rp.Ok)
                        {
                            var pl = rp.Value!.GetPlane();
                            plane = rp.Value.GetReference();
                            if (Math.Abs(Math.Abs(pl.Normal.Z) - 1) < 1e-6) z = pl.Origin.Z;
                            else if (Math.Abs(pl.Normal.Z) < 1e-6) { vertical = pl; vdir = rp.Value.Direction.Normalize(); }
                            else { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "reference plane '" + wp + "' is sloped; only horizontal and vertical work planes are supported"); break; }
                        }
                        else if (lv.Code == RevitIssueCodes.Ambiguous || rp.Code == RevitIssueCodes.Ambiguous) { OpContext.Error(issues, at, RevitIssueCodes.Ambiguous, (lv.Code == RevitIssueCodes.Ambiguous ? lv.Error : rp.Error)!); break; }
                        else
                        {
                            var names = Lookup.Levels(doc).Select(l => l.Name).Concat(planes.Select(r => r.Name)).Where(n => n.Length > 0).Distinct().ToList();
                            OpContext.Error(issues, at, RevitIssueCodes.NotFound, "no level or reference plane named '" + wp + "'" + Lookup.Suggest(wp, names));
                            break;
                        }
                    }
                    else { plane = p.Level!.GetPlaneReference(); z = p.Level.ProjectElevation; }

                    if (vertical != null)
                    {
                        if (rotated) OpContext.Error(issues, ctx.FieldPath(args, "rotation"), RevitIssueCodes.Invalid, "on a vertical work plane the instance is aligned with the plane; rotation must be 0");
                        p.OffsetAsParam = false;
                        foreach (var pt in pts)
                        {
                            var probe = u.PointToInternal(pt, (p.Level?.ProjectElevation ?? vertical.Origin.Z) + p.Offset);
                            var loc = probe - vertical.Normal.Multiply(vertical.Normal.DotProduct(probe - vertical.Origin));
                            p.Targets.Add(new Target { Location = loc, Face = plane, RefDir = vdir });
                        }
                    }
                    else
                    {
                        p.OffsetAsParam = true;
                        foreach (var pt in pts) p.Targets.Add(new Target { Location = u.PointToInternal(pt, z!.Value), Face = plane, RefDir = dir });
                    }
                    break;
                }
            }
            if (p.Host != null) ctx.DeclareModifies(p.Host.Id);
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            if (args.Has("fromPlacements")) PlacementsPlacer.Validate(ctx, args, issues);
            else Build(ctx, args, issues);
        }

        /// <summary>Writes the offset into the first writable offset parameter; returns that parameter, or null when no offset was set.</summary>
        private static BuiltInParameter? SetOffset(FamilyInstance fi, Plan p)
        {
            if (!p.OffsetAsParam || Math.Abs(p.Offset) < 1e-9) return null;
            var order = p.Mode == Mode.Face || p.Mode == Mode.Plane
                ? new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM }
                : new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM };
            foreach (var bip in order)
                if (Kit.SetDouble(fi, bip, p.Offset)) return bip;
            throw new OpFailedException("parameter_value", "the instance has no writable elevation or offset parameter for offset");
        }

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            if (args.Has("fromPlacements")) { PlacementsPlacer.Apply(ctx, args, result); return; }
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            if (!p!.Symbol.IsActive) { p.Symbol.Activate(); doc.Regenerate(); }
            string? mark = args.GetStringOrNull("mark");
            var placed = new List<FamilyInstance>();
            var expected = new JsonArray();
            var offsetParams = new JsonArray();
            for (int i = 0; i < p.Targets.Count; i++)
            {
                var t = p.Targets[i];
                FamilyInstance? fi;
                switch (p.Mode)
                {
                    case Mode.Level: fi = doc.Create.NewFamilyInstance(t.Location, p.Symbol, p.Level!, StructuralType.NonStructural); break;
                    case Mode.Hosted: fi = doc.Create.NewFamilyInstance(t.Location, p.Symbol, p.Host!, p.Level!, StructuralType.NonStructural); break;
                    default: fi = doc.Create.NewFamilyInstance(t.Face!, t.Location, t.RefDir!, p.Symbol); break;
                }
                if (fi is null) throw new OpFailedException("create_failed", "Revit did not place instance " + (i + 1) + " of '" + Lookup.FamilyTypeName(p.Symbol) + "'");
                offsetParams.Add(SetOffset(fi, p) is BuiltInParameter ob ? (JsonValue)ob.ToString() : JsonValue.Null);
                if (p.RotateAfter) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(t.Location, t.Location + XYZ.BasisZ), p.Rotation);
                if (args.GetBool("flipHand")) { if (!fi.CanFlipHand || !fi.flipHand()) throw new OpFailedException("flip_failed", "instance " + (i + 1) + " cannot be flipped by hand"); }
                if (args.GetBool("flipFacing")) { if (!fi.CanFlipFacing || !fi.flipFacing()) throw new OpFailedException("flip_failed", "instance " + (i + 1) + " cannot be flipped by facing"); }
                Kit.SetMark(fi, mark is null ? null : p.Targets.Count > 1 ? mark + "-" + (i + 1) : mark, "instance " + (i + 1));
                Kit.SetParameters(doc, fi, args.GetMap("parameters"), u, "instance " + (i + 1));
                placed.Add(fi);
                expected.Add(u.PointJson(t.Location));
            }
            doc.Regenerate();
            if (p.Host != null)
                foreach (var fi in placed)
                    if (fi.Host is null || fi.Host.Id != p.Host.Id)
                        throw new OpFailedException("host_rejected", "Revit did not host instance " + Kit.Id(fi.Id) + " on " + Kit.Id(p.Host.Id) + (fi.Host != null ? " (host is " + Kit.Id(fi.Host.Id) + ")" : " (no host)"));
            var ids = placed.Select(f => Kit.Id(f.Id)).ToList();
            result.Created.AddRange(ids);
            result.Outputs.Add("elements", Ids(ids));
            result.Outputs.Add("count", ids.Count);
            result.Outputs.Add("refused", new JsonArray());
            result.Readback.Add("placement", p.Mode.ToString().ToLowerInvariant());
            result.Readback.Add("expectedPoints", expected);
            result.Readback.Add("offsetParameters", offsetParams);
            if (p.RotateAfter) result.Readback.Add("rotationApplied", true);
        }

        private static double AngleGap(double a, double b)
        {
            double d = (a - b) % (2 * Math.PI);
            if (d < 0) d += 2 * Math.PI;
            return Math.Min(d, 2 * Math.PI - d);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            if (args.Has("fromPlacements")) { PlacementsPlacer.Readback(ctx, args, result); return; }
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var ids = result.Outputs.TryGetValue("elements", out var v) && v is JsonArray a ? a : new JsonArray();
            var expected = result.Readback.TryGetValue("expectedPoints", out var ev) && ev is JsonArray ea ? ea : new JsonArray();
            bool rotationApplied = result.Readback.ContainsKey("rotationApplied");
            string mode = result.Readback.GetStringOrNull("placement") ?? "";
            var sym = Symbol(ctx, args, new List<Issue>()).Value;
            string? wantLevel = mode == "level" || mode == "hosted"
                ? (args.GetStringOrNull("level") is string ln ? Lookup.Level(doc, ln).Value?.Name
                    : Kit.Element(doc, args.Get("host"), out _) is Element h ? Lookup.LevelName(doc, h.LevelId) : null)
                : null;
            string? wantHost = args.GetRefId("host");
            double wantRotation = u.AngleToInternal(args.GetAngle("rotation"));
            string? mark = args.GetStringOrNull("mark");
            var items = new JsonArray();
            if (ids.Count != args.GetPoint2List("points").Count) check.Fail(ids.Count + " instance(s) placed, requested " + args.GetPoint2List("points").Count);
            for (int i = 0; i < ids.Count; i++)
            {
                string idText = ids[i] is JsonString s ? s.Value : "";
                if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is FamilyInstance fi)) { check.Fail("instance " + idText + " no longer exists after commit"); continue; }
                var o = new JsonObject().Add("id", idText);
                string label = "instance " + (i + 1);
                check.Text(o, "type", Lookup.FamilyTypeName(fi.Symbol), sym is null ? null : Lookup.FamilyTypeName(sym), label + " type");
                if (fi.Location is LocationPoint lp)
                {
                    Point2? want = i < expected.Count ? OpArgs.ToPoint2(expected[i], u.JobUnit) : (Point2?)null;
                    check.Point(o, "point", lp.Point, want, label + " point");
                    if (rotationApplied)
                    {
                        double r;
                        try { r = lp.Rotation; } catch (Autodesk.Revit.Exceptions.ApplicationException) { r = double.NaN; }
                        o.Add("rotation", double.IsNaN(r) ? (JsonValue)JsonValue.Null : RevitUnits.Num(u.AngleFromInternal(r)));
                        if (double.IsNaN(r) || AngleGap(r, wantRotation) > 1e-4) check.Fail(label + " rotation reads " + (double.IsNaN(r) ? "unknown" : Kit.Num(u.AngleFromInternal(r))) + ", requested " + Kit.Num(args.GetAngle("rotation")));
                    }
                }
                else check.Fail(label + " has no location point");
                if (result.Readback["offsetParameters"] is JsonArray ops && i < ops.Count && ops[i] is JsonString os &&
                    Enum.TryParse(os.Value, out BuiltInParameter obip))
                {
                    var op = fi.get_Parameter(obip);
                    if (op is null || op.StorageType != StorageType.Double) check.Fail(label + " offset parameter " + os.Value + " cannot be read");
                    else check.Length(o, "offset", op.AsDouble(), args.GetLength("offset"), label + " offset");
                }
                if (wantLevel != null) check.Text(o, "level", Lookup.LevelName(doc, fi.LevelId), wantLevel, label + " level");
                check.Text(o, "host", fi.Host is null ? null : Kit.Id(fi.Host.Id), wantHost, label + " host");
                check.Flag(o, "handFlipped", fi.HandFlipped, args.GetBool("flipHand") ? true : (bool?)null, label + " hand flipped");
                check.Flag(o, "facingFlipped", fi.FacingFlipped, args.GetBool("flipFacing") ? true : (bool?)null, label + " facing flipped");
                if (mark != null) check.Text(o, "mark", fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), ids.Count > 1 ? mark + "-" + (i + 1) : mark, label + " mark");
                Kit.CheckParameters(doc, fi, args.GetMap("parameters"), u, check, o, label);
                items.Add(o);
            }
            result.Readback.Add("instances", items);
            check.Apply(result);
        }
    }
}
