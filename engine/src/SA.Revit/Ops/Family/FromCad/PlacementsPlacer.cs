using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SA.Core.Families;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FromCad
{
    /// <summary>
    /// family.place with fromPlacements: one level-based instance per block reference of a placements.json.
    /// <list type="bullet">
    /// <item>Family type per block: familyMap, else the one type whose CAD Block parameter is the block name (what
    /// family.fromcad writes), else the family named like the block when it has one type. Otherwise every reference
    /// of the block is refused.</item>
    /// <item>Frame: with cadLink, the CAD instance's Instance.GetTotalTransform() (which Revit documents as including
    /// the true north transform of imports); with translate, rotate by rotationDeg about the origin, then translate,
    /// in job units. Drawing units are converted with UnitUtils from placements.json's unit; a link transform whose
    /// scale is already that unit factor is used as is (see <see cref="CadPlacement.InputScale"/>).</item>
    /// <item>Per reference (<see cref="CadPlacement.Plan"/>): position, rotation φ about the vertical, and mirroring.
    /// The instance is made with NewFamilyInstance(XYZ, symbol, Level, NonStructural) at the level's elevation,
    /// rotated with ElementTransformUtils.RotateElement, and when mirrored, mirrored in place with
    /// ElementTransformUtils.MirrorElements(mirrorCopies false) about the vertical plane through its point whose
    /// normal is its rotated X axis (cos φ, sin φ, 0): that reflection times R(φ) is R(φ) diag(-1, 1), the
    /// reference's orientation. Each instance is made in a sub-transaction so a reference whose instance Revit
    /// cannot mirror (CanMirrorElement) is rolled back and refused on its own.</item>
    /// </list>
    /// Readback per instance: type, point, level, offset, mirrored flag, and the instance transform's X and Y axes
    /// against the expected ones.
    /// </summary>
    internal static class PlacementsPlacer
    {
        private sealed class Item
        {
            public PlacementBlock Block = null!;
            public PlacementReference Ref = null!;
            public FamilySymbol Symbol = null!;
            public PlannedInstance Planned = null!;
            public XYZ Location = XYZ.Zero;
        }

        private sealed class Plan
        {
            public Level Level = null!;
            public double Offset;
            public List<Item> Items = new List<Item>();
            public JsonArray Refused = new JsonArray();
            public JsonObject Align = new JsonObject();
            public ImportInstance? Link;
        }

        private static JsonObject Refusal(PlacementBlock b, PlacementReference? r, string reason) =>
            new JsonObject().Add("block", b.Name).Add("handle", r is null ? (JsonValue)JsonValue.Null : r.Handle).Add("reason", reason);

        // ------------------------------------------------------------------ plan

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            if (ctx.IsPending(args, "fromPlacements")) return null;
            var fp = args.GetObject("fromPlacements")!;
            string fpAt = ctx.FieldPath(args, "fromPlacements");
            var f = CadFamilies.Load(fp.GetString("file"), JsonPath.Key(fpAt, "file"), issues);
            if (f is null) return null;
            var p = new Plan();

            var lv = Lookup.Level(doc, args.GetString("level"));
            if (!lv.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!); return null; }
            p.Level = lv.Value!;
            p.Offset = u.LengthToInternal(args.GetLength("offset"));

            Category? cat = null;
            if (args.GetStringOrNull("category") is string cname)
            {
                cat = Lookup.Category(doc, cname, out string? cerr);
                if (cat is null) { OpContext.Error(issues, ctx.FieldPath(args, "category"), RevitIssueCodes.NotFound, cerr!); return null; }
            }

            string blocksAt = JsonPath.Key(fpAt, "blocks");
            var selected = new List<PlacementBlock>();
            if (fp.Has("blocks"))
            {
                var names = fp.GetStringList("blocks");
                for (int i = 0; i < names.Count; i++)
                {
                    var b = f.Find(names[i]);
                    if (b is null) OpContext.Error(issues, JsonPath.Index(blocksAt, i), RevitIssueCodes.NotFound, "no block '" + names[i] + "' in the placements file" + Lookup.Suggest(names[i], f.Blocks.Select(x => x.Name).ToList()));
                    else if (!selected.Contains(b)) selected.Add(b);
                }
            }
            else selected.AddRange(f.Blocks.Where(b => b.References.Count > 0));

            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => cat is null || (s.Category != null && s.Category.Id == cat.Id)).ToList();
            var mapAt = JsonPath.Key(fpAt, "familyMap");
            var mapped = new Dictionary<PlacementBlock, FamilySymbol>();
            foreach (var kv in fp.GetMap("familyMap"))
            {
                string at = JsonPath.Key(mapAt, kv.Key);
                var b = selected.FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (b is null) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "block '" + kv.Key + "' is not among the blocks placed"); continue; }
                if (!(kv.Value is JsonString js)) { OpContext.Error(issues, at, RevitIssueCodes.Invalid, "give \"Family : Type\""); continue; }
                var found = Lookup.ByName(symbols, js.Value, "family type", Lookup.FamilyTypeName);
                if (!found.Ok) { OpContext.Error(issues, at, found.Code, found.Error!); continue; }
                mapped[b] = found.Value!;
            }
            if (Kit.HasErrors(issues)) return null;

            var byBlockParam = new Dictionary<string, List<FamilySymbol>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in symbols)
                if (s.LookupParameter(CadFamilies.BlockParameter)?.AsString() is string bn && bn.Length > 0)
                {
                    if (!byBlockParam.TryGetValue(bn, out var l)) byBlockParam[bn] = l = new List<FamilySymbol>();
                    l.Add(s);
                }

            if (!Frame(ctx, fp, JsonPath.Key(fpAt, "align"), f, p, issues, out var frame, out double factor)) return null;

            var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Location is LocationPoint).ToList();
            var seenPoints = new Dictionary<string, string>(StringComparer.Ordinal);
            double tol = u.LengthToleranceInternal;
            var categories = new HashSet<BuiltInCategory>();
            foreach (var b in selected)
            {
                FamilySymbol? sym = mapped.TryGetValue(b, out var m) ? m : null;
                string? why = null;
                if (sym is null && byBlockParam.TryGetValue(b.Name, out var tagged))
                {
                    if (tagged.Count == 1) sym = tagged[0];
                    else why = tagged.Count + " family types carry " + CadFamilies.BlockParameter + " '" + b.Name + "' (" + string.Join(", ", tagged.Take(4).Select(Lookup.FamilyTypeName)) + "); give familyMap for it";
                }
                if (sym is null && why is null)
                {
                    var named = symbols.Where(s => string.Equals(s.Family.Name, b.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (named.Count == 1) sym = named[0];
                    else why = named.Count > 1
                        ? "family '" + b.Name + "' has " + named.Count + " types; give familyMap for it"
                        : "no loaded family was made from block '" + b.Name + "' (run family.fromcad, or give familyMap)";
                }
                if (sym != null && sym.Family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                    why = "'" + Lookup.FamilyTypeName(sym) + "' is a " + sym.Family.FamilyPlacementType + " family; fromPlacements places level-based families only";
                if (why != null)
                {
                    p.Refused.Add(Refusal(b, null, why + " (all " + b.References.Count + " reference(s) refused)"));
                    continue;
                }
                if (sym!.Category?.BuiltInCategory is BuiltInCategory bc) categories.Add(bc);
                foreach (var r in b.References)
                {
                    var planned = CadPlacement.Plan(r, frame, factor, out string? refusal);
                    if (planned is null) { p.Refused.Add(Refusal(b, r, refusal!)); continue; }
                    var loc = new XYZ(planned.X, planned.Y, p.Level.ProjectElevation);
                    if (Kit.CoordinateError(u.PointFromInternal(loc), u) is string ce) { p.Refused.Add(Refusal(b, r, ce)); continue; }
                    string key = b.Name + "|" + Math.Round(planned.X / tol).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Math.Round(planned.Y / tol).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (seenPoints.TryGetValue(key, out var firstHandle)) { p.Refused.Add(Refusal(b, r, "reference " + firstHandle + " of the same block is at the same point")); continue; }
                    seenPoints[key] = r.Handle;
                    var twin = existing.FirstOrDefault(fi => fi.Symbol.Id == sym.Id && Same(fi, loc, planned, tol));
                    if (twin != null) { p.Refused.Add(Refusal(b, r, "instance " + Kit.Id(twin.Id) + " of '" + Lookup.FamilyTypeName(sym) + "' is already there with the same orientation")); continue; }
                    p.Items.Add(new Item { Block = b, Ref = r, Symbol = sym, Planned = planned, Location = loc });
                }
            }

            foreach (var bc in categories)
                Kit.CheckParameterNames(ctx, args, "parameters", bc, null, issues, "the placed instances");
            if (p.Items.Count == 0)
                OpContext.Error(issues, fpAt, RevitIssueCodes.Invalid, "no reference can be placed: " +
                    string.Join("; ", p.Refused.OfType<JsonObject>().Take(5).Select(o => o.GetStringOrNull("block") + " " + (o.GetStringOrNull("handle") ?? "") + ": " + o.GetStringOrNull("reason"))));
            else if (p.Refused.Count > 0 && fp.GetBool("failOnRefused"))
                OpContext.Error(issues, JsonPath.Key(fpAt, "failOnRefused"), RevitIssueCodes.Invalid, p.Refused.Count + " reference(s) refused: " +
                    string.Join("; ", p.Refused.OfType<JsonObject>().Take(5).Select(o => o.GetStringOrNull("block") + " " + (o.GetStringOrNull("handle") ?? "") + ": " + o.GetStringOrNull("reason"))));
            else if (p.Refused.Count > 0)
                OpContext.Warning(issues, fpAt, RevitIssueCodes.Unchecked, p.Refused.Count + " reference(s) will not be placed; see the op's refused output");
            return Kit.HasErrors(issues) ? null : p;
        }

        private static bool Same(FamilyInstance fi, XYZ loc, PlannedInstance planned, double tol)
        {
            var lp = (LocationPoint)fi.Location;
            if (new XYZ(lp.Point.X - loc.X, lp.Point.Y - loc.Y, 0).GetLength() > tol || Math.Abs(lp.Point.Z - loc.Z) > tol) return false;
            var t = fi.GetTransform();
            return fi.Mirrored == planned.Mirrored &&
                   Math.Abs(t.BasisX.X - planned.BasisXx) < 1e-4 && Math.Abs(t.BasisX.Y - planned.BasisXy) < 1e-4;
        }

        private static bool Frame(OpContext ctx, OpArgs fp, string alignAt, PlacementsFile f, Plan p, List<Issue> issues, out PlanFrame frame, out double factor)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            frame = default;
            factor = 0;
            var a = fp.GetObject("align")!;
            double k = UnitUtils.ConvertToInternalUnits(1, RevitUnits.ForJobUnit(f.Source.LengthUnit!.Value));
            if (!a.Has("cadLink"))
            {
                var t = a.GetPoint2("translate");
                double deg = a.GetAngle("rotationDeg");
                frame = PlanFrame.RotateThenTranslate(u.LengthToInternal(t.X), u.LengthToInternal(t.Y), deg);
                factor = k;
                p.Align.Add("mode", "translate").Add("translate", new JsonArray(RevitUnits.Num(t.X), RevitUnits.Num(t.Y))).Add("rotationDeg", RevitUnits.Num(deg));
                return true;
            }
            string linkAt = JsonPath.Key(alignAt, "cadLink");
            var imp = FindLink(doc, a.GetRefId("cadLink") ?? "", out string? lerr);
            if (imp is null) { OpContext.Error(issues, linkAt, RevitIssueCodes.NotFound, lerr!); return false; }
            p.Link = imp;
            var T = imp.GetTotalTransform();
            if (Math.Abs(Math.Abs(T.BasisZ.Normalize().Z) - 1) > 1e-6 || Math.Abs(T.BasisX.Z) > 1e-9 || Math.Abs(T.BasisY.Z) > 1e-9)
            { OpContext.Error(issues, linkAt, RevitIssueCodes.Invalid, "CAD instance " + Kit.Id(imp.Id) + " is tilted out of plan; fromPlacements needs a CAD link placed flat"); return false; }
            frame = new PlanFrame(T.Origin.X, T.Origin.Y, T.BasisX.X, T.BasisX.Y, T.BasisY.X, T.BasisY.Y);
            if (!CadPlacement.InputScale(frame, k, out factor, out string? serr))
            { OpContext.Error(issues, linkAt, RevitIssueCodes.Invalid, "CAD instance " + Kit.Id(imp.Id) + ": " + serr); return false; }
            p.Align.Add("mode", "cadLink").Add("link", Kit.Id(imp.Id)).Add("name", LinkName(doc, imp))
                .Add("origin", u.PointJson(T.Origin)).Add("originZ", u.LengthJson(T.Origin.Z))
                .Add("rotationDeg", RevitUnits.Num(Angle.ToDegrees(Math.Atan2(T.BasisX.Y, T.BasisX.X))))
                .Add("reflected", frame.Determinant < 0).Add("scale", RevitUnits.Num(frame.Scale)).Add("unitInTransform", Math.Abs(factor - 1) < 1e-12);
            var inherent = imp.GetTransform();
            if (!inherent.Origin.IsAlmostEqualTo(T.Origin) || !inherent.BasisX.IsAlmostEqualTo(T.BasisX) || !inherent.BasisY.IsAlmostEqualTo(T.BasisY))
            {
                p.Align.Add("inherentOrigin", u.PointJson(inherent.Origin)).Add("inherentRotationDeg", RevitUnits.Num(Angle.ToDegrees(Math.Atan2(inherent.BasisX.Y, inherent.BasisX.X))));
                OpContext.Warning(issues, linkAt, RevitIssueCodes.Unchecked, "the CAD instance's total transform differs from its own (Revit adds true north for imports); the total one is used, see readback align");
            }
            return true;
        }

        private static string LinkName(Document doc, ImportInstance imp) =>
            doc.GetElement(imp.GetTypeId()) is ElementType t ? t.Name : imp.Name;

        private static ImportInstance? FindLink(Document doc, string text, out string? error)
        {
            error = null;
            if (text.Length > 0 && text.All(char.IsDigit) && ElementIds.TryParse(text, out var id))
            {
                if (doc.GetElement(id) is ImportInstance byId) return byId;
                error = "element " + text + " is not a linked or imported CAD instance";
                return null;
            }
            var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            bool Match(string? n) => n != null && (string.Equals(n, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(n), text, StringComparison.OrdinalIgnoreCase));
            var hits = all.Where(i => Match(LinkName(doc, i)) || Match(i.Name)).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) { error = hits.Count + " CAD instances are named '" + text + "' (ids " + string.Join(", ", hits.Take(6).Select(h => Kit.Id(h.Id))) + "); give the id"; return null; }
            error = "no linked or imported CAD instance named '" + text + "'" + Lookup.Suggest(text, all.Select(i => LinkName(doc, i)).Distinct().ToList());
            return null;
        }

        // ------------------------------------------------------------------ handler parts

        public static void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        public static void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            foreach (var s in p!.Items.Select(i => i.Symbol).Distinct())
                if (!s.IsActive) s.Activate();
            doc.Regenerate();
            string? mark = args.GetStringOrNull("mark");
            bool failOnRefused = args.GetObject("fromPlacements")!.GetBool("failOnRefused");
            var placed = new List<KeyValuePair<FamilyInstance, Item>>();
            foreach (var it in p.Items)
            {
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    var fi = doc.Create.NewFamilyInstance(it.Location, it.Symbol, p.Level, StructuralType.NonStructural)
                        ?? throw new OpFailedException("create_failed", "Revit did not place block '" + it.Block.Name + "' reference " + it.Ref.Handle);
                    if (Math.Abs(p.Offset) > 1e-9 && !Kit.SetDouble(fi, BuiltInParameter.INSTANCE_ELEVATION_PARAM, p.Offset))
                        throw new OpFailedException("parameter_value", "the instance of '" + Lookup.FamilyTypeName(it.Symbol) + "' has no writable elevation parameter for offset");
                    double phi = it.Planned.Rotation;
                    if (phi > 1e-9) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(it.Location, it.Location + XYZ.BasisZ), phi);
                    if (it.Planned.Mirrored)
                    {
                        if (!ElementTransformUtils.CanMirrorElement(doc, fi.Id))
                        {
                            sub.RollBack();
                            p.Refused.Add(Refusal(it.Block, it.Ref, "the reference is mirrored and Revit cannot mirror instances of '" + Lookup.FamilyTypeName(it.Symbol) + "'"));
                            continue;
                        }
                        var plane = Plane.CreateByNormalAndOrigin(new XYZ(Math.Cos(phi), Math.Sin(phi), 0), it.Location);
                        ElementTransformUtils.MirrorElements(doc, new List<ElementId> { fi.Id }, plane, false);
                    }
                    sub.Commit();
                    placed.Add(new KeyValuePair<FamilyInstance, Item>(fi, it));
                }
            }
            if (failOnRefused && p.Refused.Count > 0)
                throw new OpFailedException("references_refused", p.Refused.Count + " reference(s) refused while placing", new JsonObject().Add("refused", p.Refused));
            if (placed.Count == 0) throw new OpFailedException("references_refused", "no reference could be placed", new JsonObject().Add("refused", p.Refused));
            for (int i = 0; i < placed.Count; i++)
            {
                var fi = placed[i].Key;
                string label = "block '" + placed[i].Value.Block.Name + "' reference " + placed[i].Value.Ref.Handle;
                Kit.SetMark(fi, mark is null ? null : placed.Count > 1 ? mark + "-" + (i + 1) : mark, label);
                Kit.SetParameters(doc, fi, args.GetMap("parameters"), u, label);
            }
            doc.Regenerate();

            var expected = new JsonArray();
            foreach (var kv in placed)
            {
                var pl = kv.Value.Planned;
                expected.Add(new JsonObject().Add("id", Kit.Id(kv.Key.Id)).Add("block", kv.Value.Block.Name).Add("handle", kv.Value.Ref.Handle)
                    .Add("type", Lookup.FamilyTypeName(kv.Value.Symbol)).Add("point", u.PointJson(kv.Value.Location))
                    .Add("basis", new JsonArray(pl.BasisXx, pl.BasisXy, pl.BasisYx, pl.BasisYy))
                    .Add("mirrored", pl.Mirrored).Add("rotationDeg", RevitUnits.Num(pl.RotationDegrees)));
            }
            if (p.Link != null) p.Align.Add("linkEvidence", LinkEvidence(p.Link, placed.Select(x => x.Value.Location).ToList(), u.LengthToleranceInternal, ctx));
            var ids = placed.Select(x => Kit.Id(x.Key.Id)).ToList();
            result.Created.AddRange(ids);
            result.Outputs.Add("elements", JsonArray.FromStrings(ids));
            result.Outputs.Add("count", ids.Count);
            result.Outputs.Add("refused", p.Refused);
            result.Readback.Add("placement", "fromPlacements");
            result.Readback.Add("align", p.Align);
            result.Readback.Add("expected", expected);
        }

        /// <summary>
        /// Independent check of the frame: the origins of the block instances inside the CAD link's geometry (each a
        /// GeometryInstance, transforms accumulated) that coincide with a placed point.
        /// </summary>
        private static JsonObject LinkEvidence(ImportInstance link, List<XYZ> points, double tol, OpContext ctx)
        {
            var origins = new List<XYZ>();
            try { Origins(link.get_Geometry(new Options()), Transform.Identity, origins, 0); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            int matched = 0;
            if ((long)origins.Count * points.Count <= 50_000_000)
                foreach (var pt in points)
                    if (origins.Any(o => Math.Abs(o.X - pt.X) <= tol && Math.Abs(o.Y - pt.Y) <= tol)) matched++;
            var o2 = new JsonObject().Add("blockInstancesInLink", origins.Count).Add("placedPoints", points.Count).Add("matched", matched);
            if (origins.Count > 0 && points.Count > 0 && matched == 0)
                ctx.Warn("none of the placed points coincides with a block instance in the CAD link's geometry; check that the link shows the same drawing the placements file came from and that it is not moved");
            return o2;
        }

        private static void Origins(GeometryElement? ge, Transform acc, List<XYZ> into, int depth)
        {
            if (ge is null || depth > 4) return;
            foreach (GeometryObject g in ge)
                if (g is GeometryInstance gi)
                {
                    var t = acc.Multiply(gi.Transform);
                    if (depth > 0) into.Add(t.Origin);
                    Origins(gi.GetSymbolGeometry(), t, into, depth + 1);
                }
        }

        public static void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            var expected = result.Readback["expected"] as JsonArray ?? new JsonArray();
            result.Readback.Remove("expected");
            string wantLevel = Lookup.Level(doc, args.GetString("level")).Value?.Name ?? args.GetString("level");
            var items = new JsonArray();
            foreach (var e in expected.OfType<JsonObject>())
            {
                string idText = e.GetStringOrNull("id") ?? "";
                string label = "block '" + e.GetStringOrNull("block") + "' reference " + e.GetStringOrNull("handle");
                if (!ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is FamilyInstance fi)) { check.Fail(label + ": instance " + idText + " does not exist"); continue; }
                var o = new JsonObject().Add("id", idText).Add("block", e.GetStringOrNull("block")).Add("handle", e.GetStringOrNull("handle"));
                check.Text(o, "type", Lookup.FamilyTypeName(fi.Symbol), e.GetStringOrNull("type"), label + " type");
                if (fi.Location is LocationPoint lp)
                    check.Point(o, "point", lp.Point, e["point"] is JsonValue pv ? OpArgs.ToPoint2(pv, u.JobUnit) : (Point2?)null, label + " point");
                else check.Fail(label + " has no location point");
                check.Text(o, "level", Lookup.LevelName(doc, fi.LevelId), wantLevel, label + " level");
                if (Math.Abs(args.GetLength("offset")) > 1e-9)
                {
                    var op = fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                    if (op is null) check.Fail(label + " elevation parameter cannot be read");
                    else check.Length(o, "offset", op.AsDouble(), args.GetLength("offset"), label + " offset");
                }
                bool wantMirrored = e["mirrored"] is JsonBool mb && mb.Value;
                check.Flag(o, "mirrored", fi.Mirrored, wantMirrored, label + " mirrored");
                var t = fi.GetTransform();
                o.Add("basisX", new JsonArray(RevitUnits.Num(t.BasisX.X), RevitUnits.Num(t.BasisX.Y))).Add("basisY", new JsonArray(RevitUnits.Num(t.BasisY.X), RevitUnits.Num(t.BasisY.Y)));
                if (e["basis"] is JsonArray ba && ba.Count == 4 && ba.All(x => x is JsonNumber))
                {
                    double bx = ((JsonNumber)ba[0]).Value, by = ((JsonNumber)ba[1]).Value, cx = ((JsonNumber)ba[2]).Value, cy = ((JsonNumber)ba[3]).Value;
                    if (Math.Abs(t.BasisX.X - bx) > 1e-4 || Math.Abs(t.BasisX.Y - by) > 1e-4 || Math.Abs(t.BasisY.X - cx) > 1e-4 || Math.Abs(t.BasisY.Y - cy) > 1e-4)
                        check.Fail(label + " orientation: axes read back X [" + Kit.Num(t.BasisX.X) + ", " + Kit.Num(t.BasisX.Y) + "] Y [" + Kit.Num(t.BasisY.X) + ", " + Kit.Num(t.BasisY.Y) +
                            "], expected X [" + Kit.Num(bx) + ", " + Kit.Num(by) + "] Y [" + Kit.Num(cx) + ", " + Kit.Num(cy) + "]");
                }
                o.Add("rotationDeg", e["rotationDeg"] ?? JsonValue.Null);
                items.Add(o);
            }
            if (result.Outputs["elements"] is JsonArray els && els.Count != expected.Count) check.Fail(els.Count + " instances reported, " + expected.Count + " expected");
            result.Readback.Add("instances", items);
            check.Apply(result);
        }
    }
}
