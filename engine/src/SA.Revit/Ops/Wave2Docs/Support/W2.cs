using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Wave2Docs.Support
{
    /// <summary>
    /// Lookups and geometry shared by the wave-2 documentation handlers. Read-only: Validate may call all of it.
    ///
    /// The 2D frame (decision 21, as text.note): plans take project [x, y] at the view's level; sections, elevations
    /// and detail views take [distance along the view's right direction from View.Origin, elevation above the project
    /// base point]; drafting views and legends their own [x, y]; sheets sheet coordinates in paper units.
    /// </summary>
    internal static class W2
    {
        /// <summary>
        /// Sections, elevations and detail views that look horizontally. A detail callout drawn in a plan is
        /// ViewType.Detail but looks down, so the view direction decides, not the view type alone.
        /// </summary>
        public static bool IsVertical(View v) =>
            (v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Detail) && Math.Abs(v.ViewDirection.Z) < 1e-6;

        /// <summary>Plans, and detail views that look down (plan callouts with a Detail type): project XY.</summary>
        public static bool IsPlanLike(View v) =>
            v is ViewPlan || (v.ViewType == ViewType.Detail && Math.Abs(Math.Abs(v.ViewDirection.Z) - 1) < 1e-6);

        /// <summary>Elevation of a plan-like view's drawing plane: the plan's level, else its sketch plane, else its origin.</summary>
        public static double PlaneZ(View v)
        {
            if (v is ViewPlan vp && vp.GenLevel != null) return vp.GenLevel.ProjectElevation;
            try { if (v.SketchPlane?.GetPlane() is Plane pl) return pl.Origin.Z; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return v.Origin.Z;
        }

        /// <summary>Units of the view's frame: paper on sheets, job units elsewhere.</summary>
        public static RevitUnits FrameUnits(OpContext ctx, View v) => v is ViewSheet ? ctx.Paper : ctx.Units;

        /// <summary>A frame point (in <see cref="FrameUnits"/>) to model coordinates on the view's plane.</summary>
        public static XYZ ToModel(OpContext ctx, View v, Point2 p)
        {
            if (IsVertical(v))
            {
                var right = DocsUtil.Flat(v.RightDirection) ?? XYZ.BasisX;
                var q = v.Origin + right * ctx.Units.LengthToInternal(p.X);
                return new XYZ(q.X, q.Y, ctx.Units.LengthToInternal(p.Y) + DocsUtil.BaseZ(ctx.Doc));
            }
            if (IsPlanLike(v)) return ctx.Units.PointToInternal(p, PlaneZ(v));
            return FrameUnits(ctx, v).PointToInternal(p, 0);
        }

        public static XYZ ToModelInternal(OpContext ctx, View v, double xInternal, double yInternal)
        {
            var u = FrameUnits(ctx, v);
            return ToModel(ctx, v, new Point2(u.LengthFromInternal(xInternal), u.LengthFromInternal(yInternal)));
        }

        /// <summary>Model coordinates to the view's frame (in <see cref="FrameUnits"/>).</summary>
        public static Point2 FromModel(OpContext ctx, View v, XYZ q)
        {
            if (IsVertical(v))
            {
                var right = DocsUtil.Flat(v.RightDirection) ?? XYZ.BasisX;
                return new Point2(ctx.Units.LengthFromInternal((q - v.Origin).DotProduct(right)), ctx.Units.LengthFromInternal(q.Z - DocsUtil.BaseZ(ctx.Doc)));
            }
            return FrameUnits(ctx, v).PointFromInternal(q);
        }

        /// <summary>The model direction of the frame's x axis.</summary>
        public static XYZ XAxis(View v) => IsVertical(v) ? DocsUtil.Flat(v.RightDirection) ?? XYZ.BasisX : XYZ.BasisX;

        /// <summary>The model direction of the frame's y axis.</summary>
        public static XYZ YAxis(View v) => IsVertical(v) ? XYZ.BasisZ : XYZ.BasisY;

        public static List<XYZ> ToModel(OpContext ctx, View v, IEnumerable<Point2> pts) => pts.Select(p => ToModel(ctx, v, p)).ToList();

        /// <summary>Points of a frame field, paper units on sheets.</summary>
        public static Point2 FramePoint(OpContext ctx, OpArgs args, View v, string field) =>
            v is ViewSheet ? ctx.PaperPoint(args, field) : args.GetPoint2(field);

        public static List<Point2> FramePoints(OpContext ctx, OpArgs args, View v, string field)
        {
            if (!(v is ViewSheet)) return args.GetPoint2List(field).ToList();
            var arr = args.Get(field) as JsonArray ?? new JsonArray();
            var list = new List<Point2>();
            for (int i = 0; i < arr.Count; i++)
                list.Add(OpArgs.ToPoint2(arr[i], ctx.Paper.JobUnit, JsonPath.Index(ctx.FieldPath(args, field), i)));
            return list;
        }

        /// <summary>Points of a raw JSON list in the frame's units.</summary>
        public static List<Point2> FramePoints(OpContext ctx, View v, JsonValue? value, string path)
        {
            var unit = FrameUnits(ctx, v).JobUnit;
            var arr = value as JsonArray ?? new JsonArray();
            var list = new List<Point2>();
            for (int i = 0; i < arr.Count; i++) list.Add(OpArgs.ToPoint2(arr[i], unit, JsonPath.Index(path, i)));
            return list;
        }

        /// <summary>A closed loop of straight lines through the points; null when two consecutive points coincide.</summary>
        public static CurveLoop? Loop(IList<XYZ> pts, double tolerance)
        {
            var list = new List<XYZ>(pts);
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tolerance) list.RemoveAt(list.Count - 1);
            if (list.Count < 3) return null;
            var curves = new List<Curve>();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                var b = list[(i + 1) % list.Count];
                if (a.DistanceTo(b) <= tolerance) return null;
                curves.Add(Line.CreateBound(a, b));
            }
            return CurveLoop.Create(curves);
        }

        /// <summary>True when the 2D polygon is simple (no self-crossing) and has area.</summary>
        public static bool IsSimplePolygon(IList<Point2> pts, double tolerance)
        {
            var list = new List<Point2>(pts);
            if (list.Count > 1 && list[0].DistanceTo(list[list.Count - 1]) <= tolerance) list.RemoveAt(list.Count - 1);
            if (list.Count < 3) return false;
            var poly = new Polygon2(list);
            return poly.Area > tolerance * tolerance && poly.IsSimple(tolerance);
        }

        /// <summary>An element from a ref value (id or reference).</summary>
        public static Element? Element(OpContext ctx, JsonValue? value, string path, List<Issue> issues, out bool pending)
        {
            pending = OpContext.IsPending(value);
            if (pending) return null;
            string? id = Refs.ElementId(value);
            if (id is null || !ElementIds.TryParse(id, out var eid))
            {
                OpContext.Error(issues, path, RevitIssueCodes.Invalid, "expected an element id or a reference");
                return null;
            }
            var e = ctx.Doc.GetElement(eid);
            if (e is null) OpContext.Error(issues, path, RevitIssueCodes.NotFound, "element " + id + " does not exist");
            return e;
        }

        /// <summary>The graphics style of a line style (a subcategory of Lines) by name.</summary>
        public static GraphicsStyle? LineStyle(Document doc, string name, string path, List<Issue> issues)
        {
            var lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
            var subs = new List<Category>();
            if (lines != null) foreach (Category c in lines.SubCategories) subs.Add(c);
            var hit = subs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
                      ?? subs.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var gs = hit?.GetGraphicsStyle(GraphicsStyleType.Projection);
            if (gs is null)
                OpContext.Error(issues, path, RevitIssueCodes.NotFound, "no line style '" + name + "'" + Lookup.Suggest(name, subs.Select(c => c.Name).ToList()));
            return gs;
        }

        /// <summary>"r,g,b" (0-255 each) or "#RRGGBB".</summary>
        public static bool TryColor(string text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            string t = text.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal) && t.Length == 7 &&
                int.TryParse(t.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
            {
                r = (byte)((hex >> 16) & 0xFF);
                g = (byte)((hex >> 8) & 0xFF);
                b = (byte)(hex & 0xFF);
                return true;
            }
            var parts = t.Split(',');
            if (parts.Length != 3) return false;
            var v = new int[3];
            for (int i = 0; i < 3; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || v[i] > 255) return false;
            r = (byte)v[0];
            g = (byte)v[1];
            b = (byte)v[2];
            return true;
        }

        public static string ColorText(Color c) => c.IsValid ? c.Red + "," + c.Green + "," + c.Blue : "none";

        /// <summary>The element's family type, "Family : Type", or its type name.</summary>
        public static string TypeName(Document doc, Element e)
        {
            var t = doc.GetElement(e.GetTypeId()) as ElementType;
            return t is null ? "" : Lookup.FamilyTypeName(t);
        }

        private static readonly ViewType[] Annotatable =
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan, ViewType.AreaPlan, ViewType.Section,
            ViewType.Elevation, ViewType.Detail, ViewType.DraftingView, ViewType.Legend,
        };

        /// <summary>
        /// Errors for the view kinds 2D annotation cannot go in. A list of the kinds that can, so table views (panel
        /// schedules), graphical column schedules, renderings, walkthroughs and reports are refused too.
        /// </summary>
        public static bool Refuse(View v, string what, string path, List<Issue> issues, bool allowSheet)
        {
            bool ok = v is ViewSheet ? allowSheet : !(v is View3D) && !(v is TableView) && Annotatable.Contains(v.ViewType);
            if (v.IsTemplate || !ok)
            {
                OpContext.Error(issues, path, RevitIssueCodes.NotAllowed, what + " cannot go in " + DocsUtil.Describe(v));
                return true;
            }
            return false;
        }

        /// <summary>The level elevation a plan's 2D content lies at.</summary>
        public static double PlanZ(View v) => PlaneZ(v);

        public static JsonArray Ids(IEnumerable<ElementId> ids) => JsonArray.FromStrings(ids.Select(ElementIds.ToWire));

        /// <summary>The view's crop box corners mapped into <paramref name="frame"/>'s 2D frame, as their bounding rectangle.</summary>
        public static Rect2 CropExtent(OpContext ctx, View view, View frame)
        {
            var box = view.CropBox;
            var t = box.Transform;
            var pts = new[]
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Max.X, box.Max.Y, box.Min.Z), new XYZ(box.Min.X, box.Max.Y, box.Min.Z),
            }.Select(q => FromModel(ctx, frame, t.OfPoint(q))).ToList();
            return Rect2.FromCorners(new Point2(pts.Min(q => q.X), pts.Min(q => q.Y)), new Point2(pts.Max(q => q.X), pts.Max(q => q.Y)));
        }

        public static JsonObject RectJson(Rect2 r) =>
            new JsonObject().Add("min", DocsUtil.Xy(r.MinX, r.MinY)).Add("max", DocsUtil.Xy(r.MaxX, r.MaxY));

        public static void CompareRect(ReadbackChecker check, string name, Rect2 actual, Rect2 requested, RevitUnits units)
        {
            double tol = units.LengthTolerance;
            if (Math.Abs(actual.MinX - requested.MinX) > tol || Math.Abs(actual.MinY - requested.MinY) > tol ||
                Math.Abs(actual.MaxX - requested.MaxX) > tol || Math.Abs(actual.MaxY - requested.MaxY) > tol)
                check.Fail(name + ": read back [" + Fmt(actual.MinX) + ", " + Fmt(actual.MinY) + "] - [" + Fmt(actual.MaxX) + ", " + Fmt(actual.MaxY) +
                           "], requested [" + Fmt(requested.MinX) + ", " + Fmt(requested.MinY) + "] - [" + Fmt(requested.MaxX) + ", " + Fmt(requested.MaxY) + "]");
        }

        public static string Fmt(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>
        /// ViewSetup's name, scale and template rules for views created without a view family type (legends
        /// duplicated, area plans): the same checks as ViewSetup.Build, and Type stays null so readback skips it.
        /// </summary>
        public static ViewSetup Setup(OpContext ctx, OpArgs args, ViewType viewType, string? defaultName, List<Issue> issues, bool forReadback)
        {
            var s = new ViewSetup();
            string? raw = args.GetStringOrNull("name");
            if (raw != null) s.Name = DocsUtil.CheckName(raw, "view", ctx.FieldPath(args, "name"), issues);
            else if (!forReadback && defaultName != null) s.Name = defaultName;
            else if (!forReadback)
                OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid, "name is required: Revit's default names pile up as unnamed views (RV-028)");
            if (raw != null && s.Name != null && !forReadback && ctx.Standard?.ViewNaming != null)
            {
                var vn = ctx.Standard.ParseViewName(s.Name);
                if (!vn.Valid) OpContext.Warning(issues, ctx.FieldPath(args, "name"), IssueCodes.NameInvalid, "view name does not follow the standard's view naming: " + vn.Explain());
            }
            if (s.Name != null && !forReadback && DocsUtil.NameTaken(ctx.Doc, s.Name, viewType) is View taken)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Exists,
                    "a " + viewType + " view named '" + taken.Name + "' already exists (id " + ElementIds.ToWire(taken.Id) + ")");
                s.Name = null;
            }
            s.Scale = DocsUtil.Scale(ctx, args.GetStringOrNull("scale"), ctx.FieldPath(args, "scale"), issues);
            if (args.GetStringOrNull("template") is string tn)
            {
                s.Template = DocsUtil.Template(ctx.Doc, tn, new[] { viewType }, ctx.FieldPath(args, "template"), issues);
                if (s.Template != null && DocsUtil.TemplateControlsScale(s.Template))
                {
                    s.TemplateScale = s.Template.Scale;
                    if (s.Scale.HasValue && s.Scale.Value != s.Template.Scale)
                        OpContext.Error(issues, ctx.FieldPath(args, "scale"), RevitIssueCodes.Invalid,
                            "template '" + s.Template.Name + "' controls the scale (1:" + s.Template.Scale + "); omit scale or use 1:" + s.Template.Scale);
                }
            }
            return s;
        }
    }
}
