using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling.Support
{
    /// <summary>Small helpers shared by the modelling handlers: issue handling, ref resolution, names, outlines.</summary>
    internal static class Kit
    {
        // Revit's limit is about 20 miles from the internal origin; stay well inside it (same bound as wall.create).
        private const double MaxCoordinateFeet = 52800;

        public static bool HasErrors(List<Issue> issues) => issues.Any(i => i.IsError);

        /// <summary>Throws the op failure that Apply raises when its own re-validation finds errors.</summary>
        public static void Require(List<Issue> issues)
        {
            if (HasErrors(issues))
                throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Take(5).Select(i => i.Path + ": " + i.Message)));
        }

        public static string Num(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        public static string Id(ElementId id) => ElementIds.ToWire(id);

        /// <summary>The element a resolved ref value names (id string, number or result object), or null with an error.</summary>
        public static Element? Element(Document doc, JsonValue? value, out string error)
        {
            error = "";
            string? idText = Refs.ElementId(value);
            if (idText is null) { error = "expected an element id or a reference to an earlier op"; return null; }
            if (!ElementIds.TryParse(idText, out var id)) { error = "'" + idText + "' is not an element id"; return null; }
            var e = doc.GetElement(id);
            if (e is null) error = "element " + idText + " does not exist";
            return e;
        }

        /// <summary>The element ids of a resolved list-of-refs field; a whole-list ref resolves to an array or a result object.</summary>
        public static List<KeyValuePair<string, string>> IdList(OpContext ctx, OpArgs args, string field, out bool pending)
        {
            pending = false;
            var list = new List<KeyValuePair<string, string>>();
            var value = args.Raw[field];
            if (value is null || value.IsNull) return list;
            if (OpContext.IsPending(value) && !(value is JsonArray)) { pending = true; return list; }
            var raw = value as JsonArray ?? new JsonArray(value);
            for (int i = 0; i < raw.Count; i++)
            {
                string at = JsonPath.Index(ctx.FieldPath(args, field), i);
                if (OpContext.IsPending(raw[i])) { pending = true; continue; }
                foreach (var idText in Refs.ElementIds(raw[i])) list.Add(new KeyValuePair<string, string>(at, idText));
            }
            return list;
        }

        // ------------------------------------------------------------------ names

        private static readonly char[] ProhibitedNameChars = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' };

        /// <summary>
        /// Refuses names Revit rejects and the default or numbered-copy names the practice notes list as a mistake
        /// (RV-027: "Copy", "Type 1", "Wall 2 2 2", trailing spaces). Returns the problem, or null when the name is fine.
        /// </summary>
        public static string? BadTypeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "the name is empty";
            if (name != name.Trim()) return "the name has leading or trailing spaces";
            int bad = name.IndexOfAny(ProhibitedNameChars);
            if (bad >= 0) return "the name contains '" + name[bad] + "', which Revit does not allow in names";
            if (Regex.IsMatch(name, @"(^|\W)copy(\W|$)", RegexOptions.IgnoreCase)) return "'" + name + "' looks like a default copy name; give the type a descriptive name";
            if (Regex.IsMatch(name, @"^(type|family|wall|floor|roof|ceiling|new|default)\s*\d*$", RegexOptions.IgnoreCase)) return "'" + name + "' is a placeholder name; describe the variant (e.g. size or construction)";
            if (Regex.IsMatch(name, @"(\s\d+){2,}$")) return "'" + name + "' ends in repeated numbers, the sign of a duplicated type that was never renamed";
            return null;
        }

        // ------------------------------------------------------------------ outlines

        /// <summary>Drops a repeated closing point and consecutive duplicates.</summary>
        public static List<Point2> CleanLoop(IList<Point2> pts, double tolerance)
        {
            var list = new List<Point2>();
            foreach (var p in pts)
                if (list.Count == 0 || list[list.Count - 1].DistanceTo(p) > tolerance) list.Add(p);
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tolerance) list.RemoveAt(list.Count - 1);
            return list;
        }

        /// <summary>Checks a closed outline in job units: point count, edge length, self-intersection, coordinates.</summary>
        public static string? CheckLoop(IList<Point2> pts, RevitUnits u, double shortCurveInternal, string what)
        {
            if (pts.Count < 3) return what + " needs at least 3 distinct points";
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (u.LengthToInternal(a.DistanceTo(b)) <= shortCurveInternal)
                    return what + ": edge " + i + " is too short for Revit (" + Num(a.DistanceTo(b)) + " " + SA.Core.Units.Length.Symbol(u.JobUnit) + ")";
                if (CoordinateError(a, u) is string far) return what + ": " + far;
            }
            if (new Polygon2(pts).Area <= u.LengthTolerance * u.LengthTolerance) return what + " encloses no area (all points on a line)";
            int e1, e2;
            if (SelfIntersects(pts, out e1, out e2)) return what + " crosses itself (edges " + e1 + " and " + e2 + ")";
            return null;
        }

        public static string? CoordinateError(Point2 a, RevitUnits u) =>
            Math.Abs(u.LengthToInternal(a.X)) > MaxCoordinateFeet || Math.Abs(u.LengthToInternal(a.Y)) > MaxCoordinateFeet
                ? "point [" + Num(a.X) + ", " + Num(a.Y) + "] is more than 10 miles from the internal origin; check the units" : null;

        public static bool SelfIntersects(IList<Point2> pts, out int edgeA, out int edgeB)
        {
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var s1 = new Segment2(pts[i], pts[(i + 1) % n]);
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue; // neighbours share a vertex
                    var s2 = new Segment2(pts[j], pts[(j + 1) % n]);
                    if (SegmentsTouch(s1, s2)) { edgeA = i; edgeB = j; return true; }
                }
            }
            edgeA = edgeB = -1;
            return false;
        }

        private static bool SegmentsTouch(Segment2 a, Segment2 b)
        {
            double d1 = (b.A - a.A).Cross(a.Vector), d2 = (b.B - a.A).Cross(a.Vector);
            double d3 = (a.A - b.A).Cross(b.Vector), d4 = (a.B - b.A).Cross(b.Vector);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            double tol = 1e-9 * Math.Max(1, Math.Max(a.Length, b.Length));
            return a.DistanceTo(b.A) <= tol || a.DistanceTo(b.B) <= tol || b.DistanceTo(a.A) <= tol || b.DistanceTo(a.B) <= tol;
        }

        /// <summary>True when every point of the inner outline lies strictly inside the outer one.</summary>
        public static bool Inside(IList<Point2> inner, IList<Point2> outer, double tolerance)
        {
            var poly = new Polygon2(outer);
            return inner.All(p => poly.Locate(p, tolerance) == PointLocation.Inside);
        }

        public static CurveLoop ToCurveLoop(IList<Point2> pts, RevitUnits u, double z)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
                loop.Append(Line.CreateBound(u.PointToInternal(pts[i], z), u.PointToInternal(pts[(i + 1) % pts.Count], z)));
            return loop;
        }

        /// <summary>The outline moved outward by <paramref name="distance"/> on every edge (mitred corners).</summary>
        public static List<Point2> OffsetOutward(IList<Point2> pts, double distance)
        {
            if (distance == 0) return new List<Point2>(pts);
            bool ccw = new Polygon2(pts).IsCounterClockwise;
            int n = pts.Count;
            var lines = new List<KeyValuePair<Point2, Point2>>();
            for (int i = 0; i < n; i++)
            {
                var a = pts[i];
                var dir = (pts[(i + 1) % n] - a).Normalised();
                // Outward is to the right of the direction of travel for a counter-clockwise outline.
                var outward = ccw ? new Point2(dir.Y, -dir.X) : new Point2(-dir.Y, dir.X);
                lines.Add(new KeyValuePair<Point2, Point2>(a + outward * distance, dir));
            }
            var result = new List<Point2>();
            for (int i = 0; i < n; i++)
            {
                var prev = lines[(i - 1 + n) % n];
                var cur = lines[i];
                if (Lines.Intersect(prev.Key, prev.Value, cur.Key, cur.Value, out var p)) result.Add(p);
                else result.Add(cur.Key); // collinear edges: the offset point itself
            }
            return result;
        }

        /// <summary>Area enclosed by curve loops (first outer, others holes), from tessellated curves, internal units.</summary>
        public static double LoopsArea(IEnumerable<CurveLoop> loops)
        {
            double total = 0;
            bool first = true;
            foreach (var loop in loops)
            {
                var pts = new List<Point2>();
                foreach (Curve c in loop)
                {
                    var t = c.Tessellate();
                    for (int k = 0; k < t.Count - 1; k++) pts.Add(new Point2(t[k].X, t[k].Y));
                }
                double a = pts.Count >= 3 ? new Polygon2(pts).Area : 0;
                total += first ? a : -a;
                first = false;
            }
            return total;
        }

        /// <summary>A room's boundary as closed loops at height z (internal), outer loop first.</summary>
        public static IList<CurveLoop>? RoomLoops(Autodesk.Revit.DB.Architecture.Room room, SpatialElementBoundaryLocation location, double z, out string error)
        {
            error = "";
            var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = location };
            var segs = room.GetBoundarySegments(opts);
            if (segs is null || segs.Count == 0) { error = "room " + Id(room.Id) + " has no boundary (unplaced, not enclosed or redundant)"; return null; }
            var loops = new List<CurveLoop>();
            foreach (var list in segs)
            {
                var loop = new CurveLoop();
                foreach (var s in list)
                {
                    var c = s.GetCurve();
                    double dz = z - c.GetEndPoint(0).Z;
                    loop.Append(Math.Abs(dz) > 1e-9 ? c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, dz))) : c);
                }
                if (loop.IsOpen()) { error = "room " + Id(room.Id) + " has an open boundary loop"; return null; }
                loops.Add(loop);
            }
            // Outer loop first: the one with the largest area.
            loops.Sort((a, b) => LoopsArea(new[] { b }).CompareTo(LoopsArea(new[] { a })));
            return loops;
        }

        /// <summary>Compares two areas (internal) within 0.5 % or a small absolute amount.</summary>
        public static bool AreaClose(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-4, 0.005 * Math.Max(Math.Abs(a), Math.Abs(b)));

        /// <summary>The level's elevation as a Z in internal coordinates (Level.ProjectElevation, as level.ensure reads it).</summary>
        public static double LevelZ(Level level) => level.ProjectElevation;

        /// <summary>A small height above a level, used to probe room volumes (300 mm, converted through UnitUtils).</summary>
        public static double ProbeHeight => UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);

        public static Element? FirstOf<T>(Document doc) where T : Element => new FilteredElementCollector(doc).OfClass(typeof(T)).FirstElement();

        /// <summary>Plan views (not templates) generated from a level, the one named like the level first.</summary>
        public static ViewPlan? PlanFor(Document doc, Level level, ViewType kind = ViewType.FloorPlan, ElementId? phase = null)
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == kind && v.GenLevel != null && v.GenLevel.Id == level.Id &&
                            (phase is null || ViewPhase(v) == phase)).ToList();
            return plans.OrderBy(v => string.Equals(v.Name, level.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => ElementIds.IsValid(v.GetPrimaryViewId()) ? 1 : 0)
                .ThenBy(v => v.Name, StringComparer.Ordinal).FirstOrDefault();
        }

        /// <summary>The view's phase (VIEW_PHASE), or InvalidElementId when it has none.</summary>
        public static ElementId ViewPhase(View v) => v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId;

        /// <summary>The project's last phase, the one PlanTopology(level) is documented to use; InvalidElementId when there is none.</summary>
        public static ElementId LastPhase(Document doc)
        {
            var phases = doc.Phases;
            return phases.Size > 0 ? phases.get_Item(phases.Size - 1).Id : ElementId.InvalidElementId;
        }

        /// <summary>Checks the request's parameter names against a category before the element exists.</summary>
        public static bool CheckParameterNames(OpContext ctx, OpArgs args, string field, BuiltInCategory category, Element? sample, List<Issue> issues, string what)
        {
            bool ok = true;
            foreach (var kv in args.GetMap(field))
            {
                var known = ParamAccess.KnownFor(ctx.Doc, category, kv.Key, sample);
                string at = JsonPath.Key(ctx.FieldPath(args, field), kv.Key);
                if (known == false) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, what + " have no parameter '" + kv.Key + "'"); ok = false; }
                else if (known is null) OpContext.Warning(issues, at, RevitIssueCodes.Unchecked, "parameter '" + kv.Key + "' could not be checked before the element exists");
            }
            return ok;
        }

        /// <summary>Sets instance parameters on a new element; throws on any refusal.</summary>
        public static void SetParameters(Document doc, Element e, IEnumerable<KeyValuePair<string, JsonValue>> values, RevitUnits u, string what)
        {
            foreach (var kv in values)
            {
                var prm = ParamAccess.Find(e, kv.Key, out string? err) ?? throw new OpFailedException("parameter_not_found", err ?? kv.Key);
                if (prm.IsReadOnly) throw new OpFailedException("parameter_read_only", "parameter '" + kv.Key + "' is read-only on " + what);
                if (!ParamAccess.TryPrepare(doc, prm, kv.Value, u, out var pv, out string perr))
                    throw new OpFailedException("parameter_value", "parameter '" + kv.Key + "': " + perr);
                if (!ParamAccess.Apply(prm, pv)) throw new OpFailedException("parameter_value", "Revit refused the value for '" + kv.Key + "' on " + what);
            }
        }

        /// <summary>Reads requested parameters back into rb.parameters and records mismatches.</summary>
        public static void CheckParameters(Document doc, Element e, IEnumerable<KeyValuePair<string, JsonValue>> values, RevitUnits u, ReadbackChecker check, JsonObject rb, string label)
        {
            var po = new JsonObject();
            foreach (var kv in values)
            {
                var prm = ParamAccess.Find(e, kv.Key, out string? err);
                if (prm is null) { check.Fail(label + ": " + err); continue; }
                po.Add(kv.Key, ParamAccess.Read(prm, u));
                if (ParamAccess.TryPrepare(doc, prm, kv.Value, u, out var pv, out _) && !ParamAccess.Matches(prm, pv, u))
                    check.Fail(label + " parameter '" + kv.Key + "' reads " + ParamAccess.Read(prm, u).ToJson() + ", requested " + kv.Value.ToJson());
            }
            if (po.Count > 0) rb.Add("parameters", po);
        }

        public static void SetMark(Element e, string? mark, string what)
        {
            if (mark is null) return;
            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) ?? throw new OpFailedException("parameter_not_found", what + " has no Mark parameter");
            if (!p.Set(mark)) throw new OpFailedException("parameter_value", "Revit refused Mark '" + mark + "' on " + what);
        }

        /// <summary>Elements of a category whose Mark equals the value (duplicate-mark guard, RV-005).</summary>
        public static List<Element> WithMark(Document doc, BuiltInCategory category, string mark) =>
            new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType()
                .Where(e => string.Equals(e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), mark, StringComparison.Ordinal)).ToList();

        public static double? ParamDouble(Element? e, params BuiltInParameter[] bips)
        {
            if (e is null) return null;
            foreach (var b in bips)
            {
                var p = e.get_Parameter(b);
                if (p != null && p.StorageType == StorageType.Double && p.HasValue) return p.AsDouble();
            }
            return null;
        }

        public static bool SetDouble(Element e, BuiltInParameter bip, double v)
        {
            var p = e.get_Parameter(bip);
            return p != null && !p.IsReadOnly && p.Set(v);
        }
    }
}
