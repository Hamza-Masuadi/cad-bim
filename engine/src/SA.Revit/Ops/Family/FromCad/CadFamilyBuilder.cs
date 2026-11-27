using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Revit.Compat;
using SA.Revit.Host;
using SA.Revit.Ops.FamilyCreation.Support;
using SA.Revit.Ops.FamilyEditing;
using SA.Revit.Ops.Modelling.Support;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FromCad
{
    /// <summary>What to build for one block. Lengths in Revit internal units, in the family's coordinates (origin = block base point).</summary>
    internal sealed class FamilySpec
    {
        public string Block = "";
        public string Source = "";
        public string Dwg = "";
        public ImportUnit Unit;
        public string Body = "box";
        public string Symbol = "import";
        public string TypeName = "";
        public string Subcategory = "";
        public string MaterialParameter = "";
        public bool BodyInPlan;
        /// <summary>The family's category; set on the family when the template has another (a Generic Model template standing in).</summary>
        public BuiltInCategory? SetCategory;
        public double MinX, MinY, MaxX, MaxY, Height;
    }

    internal sealed class FamilyBuilt
    {
        public JsonObject Symbol = new JsonObject();
        public JsonObject Flex = new JsonObject();
        public bool FlexOk;
    }

    /// <summary>
    /// Builds one family in a new family document (Application.NewFamilyDocument), in one family transaction:
    /// <list type="number">
    /// <item>category: with a Generic Model template, OwnerFamily.FamilyCategory is set (Revit throws ArgumentException
    /// for categories a family cannot take);</item>
    /// <item>plan symbol: Document.Import of the block's DWG into the template's floor plan view with ThisViewOnly,
    /// ImportPlacement.Origin (the DWG origin, which block.export puts at the block's base point, lands on the family
    /// origin) and the drawing's unit. With symbol "lines" the import's curves become symbolic lines on the reference
    /// level in the symbol subcategory, and the import is deleted. Revit's API gives an import instance no subcategory,
    /// so in "import" mode the subcategory is applied only if the instance exposes FAMILY_ELEM_SUBCATEGORY;</item>
    /// <item>box body: an extrusion of the extents rectangle on the reference level, faces locked to four reference
    /// planes (the template's centre plane is reused where an edge lies on it), Width and Depth as labelled dimensions
    /// between them, the low-side plane locked to the centre plane so the box grows away from the base point, Height
    /// associated with the extrusion end, and a type material parameter associated with the extrusion's material;</item>
    /// <item>type parameters CAD Block and CAD Source.</item>
    /// </list>
    /// The flex test then sets each size case in a family transaction inside a transaction group that is rolled back,
    /// and compares the body's bounding box with the case; any failure or warning Revit raises fails the case.
    /// </summary>
    internal static class CadFamilyBuilder
    {
        public const string Width = "Width";
        public const string Depth = "Depth";
        public const string Height = "Height";
        private const int MaxLines = 20000;

        private static double Tol => UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);

        public static FamilyBuilt Build(OpContext ctx, Document famDoc, FamilySpec s)
        {
            var built = new FamilyBuilt();
            var collector = new FailureCollector();
            using (var t = new Transaction(famDoc, "SA family.fromcad"))
            {
                if (t.Start() != TransactionStatus.Started) throw new OpFailedException("transaction_failed", "Revit refused to start a transaction in the family for block '" + s.Block + "'");
                collector.Install(t);
                try { Make(ctx, famDoc, s, built); }
                catch (Exception e)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    if (e is OpFailedException) throw;
                    throw new OpFailedException("family_build_failed", "building the family for block '" + s.Block + "' failed: " + e.GetType().Name + ": " + e.Message);
                }
                var status = t.Commit();
                foreach (var w in collector.Warnings) ctx.Job.Report.Warnings.Add(new ReportWarning("app", "in the family for block '" + s.Block + "': " + w.Text(), ctx.Op.Id));
                if (status != TransactionStatus.Committed || collector.Errors.Count > 0)
                    throw new OpFailedException("family_build_failed", "Revit rolled back the family for block '" + s.Block + "' (" + status + "): " +
                        (collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Take(3).Select(x => x.Text())) : "no failure message"));
            }
            Flex(ctx, famDoc, s, built);
            return built;
        }

        private static void Make(OpContext ctx, Document famDoc, FamilySpec s, FamilyBuilt built)
        {
            var fm = famDoc.FamilyManager;
            var owner = famDoc.OwnerFamily ?? throw new OpFailedException("template_unusable", "the new family document has no owner family");
            if (s.SetCategory is BuiltInCategory bic && (owner.FamilyCategory is null || owner.FamilyCategory.BuiltInCategory != bic))
            {
                Category? cat = null;
                try { cat = Category.GetCategory(famDoc, bic); } catch (Exception) { }
                if (cat is null) throw new OpFailedException("category_refused", "the family document has no category " + bic);
                try { owner.FamilyCategory = cat; }
                catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("category_refused", "Revit does not let a Generic Model family become '" + cat.Name + "': " + e.Message); }
            }
            var famCat = owner.FamilyCategory ?? throw new OpFailedException("template_unusable", "the family has no category");
            var level = new FilteredElementCollector(famDoc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault()
                ?? throw new OpFailedException("template_unusable", "the family template has no reference level");
            var plan = new FilteredElementCollector(famDoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .OrderBy(v => v.GenLevel != null && v.GenLevel.Id == level.Id ? 0 : 1).FirstOrDefault()
                ?? throw new OpFailedException("template_unusable", "the family template has no floor plan view for the plan symbol");
            var sketch = SketchPlane.Create(famDoc, level.Id);

            var importId = Import(famDoc, s, plan, true);
            if (s.Symbol == "lines")
            {
                built.Symbol = ToLines(famDoc, (ImportInstance)famDoc.GetElement(importId), plan, sketch, Subcategory(famDoc, famCat, s.Subcategory), s.Block);
                famDoc.Delete(importId);
            }
            else built.Symbol = ImportSubcategory(ctx, famDoc, (ImportInstance)famDoc.GetElement(importId), famCat, s);

            var pBlock = Param(fm, CadFamilies.BlockParameter, GroupTypeId.IdentityData, SpecTypeId.String.Text);
            var pSource = Param(fm, CadFamilies.SourceParameter, GroupTypeId.IdentityData, SpecTypeId.String.Text);
            EnsureType(fm, s.TypeName);
            fm.Set(pBlock, s.Block);
            fm.Set(pSource, s.Source);

            if (s.Body == "box") Box(famDoc, s, plan, sketch);
            else if (s.Body == "import3d") Import(famDoc, s, plan, false);
        }

        // ------------------------------------------------------------------ symbol

        private static ElementId Import(Document famDoc, FamilySpec s, ViewPlan plan, bool thisViewOnly)
        {
            var opts = new DWGImportOptions { ThisViewOnly = thisViewOnly, Placement = ImportPlacement.Origin, Unit = s.Unit, ColorMode = ImportColorMode.Preserved };
            bool ok;
            ElementId id;
            try { ok = famDoc.Import(s.Dwg, opts, plan, out id); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("import_failed", "Revit refused to import " + s.Dwg + (thisViewOnly ? " into the family's plan view" : " as 3D") + ": " + e.Message); }
            if (!ok || !(famDoc.GetElement(id) is ImportInstance)) throw new OpFailedException("import_failed", "Revit did not import " + s.Dwg + " into the family for block '" + s.Block + "'");
            return id;
        }

        private static Category Subcategory(Document famDoc, Category parent, string name)
        {
            foreach (Category c in parent.SubCategories)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            try { return famDoc.Settings.Categories.NewSubcategory(parent, name); }
            catch (Autodesk.Revit.Exceptions.ApplicationException e) { throw new OpFailedException("subcategory_refused", "Revit did not create subcategory '" + name + "' of " + parent.Name + ": " + e.Message); }
        }

        private static JsonObject ImportSubcategory(OpContext ctx, Document famDoc, ImportInstance imp, Category famCat, FamilySpec s)
        {
            var o = new JsonObject().Add("mode", "import").Add("element", ElementIds.ToWire(imp.Id)).Add("viewSpecific", true);
            var p = imp.get_Parameter(BuiltInParameter.FAMILY_ELEM_SUBCATEGORY);
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
            {
                var sub = Subcategory(famDoc, famCat, s.Subcategory);
                if (p.Set(sub.Id)) return o.Add("subcategory", sub.Name).Add("subcategoryApplied", true);
            }
            ctx.Warn("block '" + s.Block + "': Revit gives the imported DWG no subcategory, so its layers show under 'Imports in Families' in Object Styles; use symbol \"lines\" for a symbol in subcategory '" + s.Subcategory + "'");
            return o.Add("subcategory", JsonValue.Null).Add("subcategoryApplied", false);
        }

        /// <summary>The import's horizontal curves as symbolic lines on the reference level; anything else (text, fills, sloped curves) is counted as skipped.</summary>
        private static JsonObject ToLines(Document famDoc, ImportInstance imp, ViewPlan plan, SketchPlane sketch, Category sub, string block)
        {
            var found = new List<Curve>();
            int skipped = 0;
            Walk(imp.get_Geometry(new Options { View = plan }), Transform.Identity, found, ref skipped, 0, famDoc.Application.ShortCurveTolerance);
            var style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            int made = 0;
            foreach (var c in found.SelectMany(Bound))
            {
                var flat = Flat(c);
                if (flat is null || (flat.IsBound && flat.Length <= famDoc.Application.ShortCurveTolerance)) { skipped++; continue; }
                if (made >= MaxLines) throw new OpFailedException("symbol_too_large", "block '" + block + "' has more than " + MaxLines + " curves; use symbol \"import\"");
                var sc = famDoc.FamilyCreate.NewSymbolicCurve(flat, sketch);
                if (style != null) sc.Subcategory = style;
                made++;
            }
            if (made == 0) throw new OpFailedException("symbol_empty", "block '" + block + "' has no horizontal lines or arcs to draw (" + skipped + " other objects); use symbol \"import\"");
            return new JsonObject().Add("mode", "lines").Add("subcategory", sub.Name).Add("subcategoryApplied", true).Add("lines", made).Add("skipped", skipped);
        }

        private static void Walk(GeometryElement? ge, Transform acc, List<Curve> found, ref int skipped, int depth, double shortCurve)
        {
            if (ge is null) return;
            foreach (GeometryObject o in ge)
            {
                switch (o)
                {
                    case GeometryInstance gi:
                        if (depth < 16) Walk(gi.GetSymbolGeometry(), acc.Multiply(gi.Transform), found, ref skipped, depth + 1, shortCurve);
                        break;
                    case Curve c:
                        found.Add(c.CreateTransformed(acc));
                        break;
                    case PolyLine pl:
                        var pts = pl.GetCoordinates();
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            var a = acc.OfPoint(pts[i]);
                            var b = acc.OfPoint(pts[i + 1]);
                            if (a.DistanceTo(b) > shortCurve) found.Add(Line.CreateBound(a, b));
                        }
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
        }

        /// <summary>Closed curves (circles, full ellipses) split in two halves; symbolic lines need bound curves.</summary>
        private static IEnumerable<Curve> Bound(Curve c)
        {
            if (c.IsBound) { yield return c; yield break; }
            if (!c.IsCyclic) yield break;
            double half = c.Period / 2;
            var a = c.Clone();
            a.MakeBound(0, half);
            var b = c.Clone();
            b.MakeBound(half, c.Period);
            yield return a;
            yield return b;
        }

        /// <summary>The curve moved down to z = 0 when it lies in a horizontal plane; null otherwise.</summary>
        private static Curve? Flat(Curve c)
        {
            IList<XYZ> pts;
            try { pts = c.Tessellate(); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
            if (pts.Count == 0) return null;
            double z = pts[0].Z;
            if (pts.Any(p => Math.Abs(p.Z - z) > Tol)) return null;
            return Math.Abs(z) <= 1e-9 ? c : c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, -z)));
        }

        // ------------------------------------------------------------------ parameters and type

        private static FamilyParameter Param(FamilyManager fm, string name, ForgeTypeId group, ForgeTypeId spec)
        {
            var existing = FamilyDoc.Find(fm, name, out _);
            if (existing != null)
            {
                if (!ParameterApi.SameSpec(FamilyDoc.Spec(existing), spec) || existing.IsInstance)
                    throw new OpFailedException("parameter_conflict", "the template already has a parameter '" + name + "' (" + (existing.IsInstance ? "instance " : "type ") +
                        ParameterApi.SpecLabel(FamilyDoc.Spec(existing)) + "); family.fromcad needs it as a type " + ParameterApi.SpecLabel(spec) + " parameter");
                return existing;
            }
            return fm.AddParameter(name, group, spec, false) ?? throw new OpFailedException("parameter_refused", "Revit did not add parameter '" + name + "'");
        }

        private static void EnsureType(FamilyManager fm, string name)
        {
            if (FamilyDoc.Type(fm, name) is FamilyType same) { fm.CurrentType = same; return; }
            if (fm.CurrentType is null && fm.Types.Cast<FamilyType>().FirstOrDefault() is FamilyType first) fm.CurrentType = first;
            if (fm.CurrentType is null) fm.NewType(name);
            else fm.RenameCurrentType(name);
        }

        // ------------------------------------------------------------------ box body

        private static Extrusion Box(Document famDoc, FamilySpec s, ViewPlan plan, SketchPlane sketch)
        {
            var fm = famDoc.FamilyManager;
            var pW = Param(fm, Width, GroupTypeId.Geometry, SpecTypeId.Length);
            var pD = Param(fm, Depth, GroupTypeId.Geometry, SpecTypeId.Length);
            var pH = Param(fm, Height, GroupTypeId.Geometry, SpecTypeId.Length);
            var pM = Param(fm, s.MaterialParameter, GroupTypeId.Materials, SpecTypeId.Reference.Material);
            double w = s.MaxX - s.MinX, d = s.MaxY - s.MinY;
            fm.Set(pW, w);
            fm.Set(pD, d);
            fm.Set(pH, s.Height);

            var c00 = new XYZ(s.MinX, s.MinY, 0);
            var c10 = new XYZ(s.MaxX, s.MinY, 0);
            var c11 = new XYZ(s.MaxX, s.MaxY, 0);
            var c01 = new XYZ(s.MinX, s.MaxY, 0);
            var loop = new CurveArray();
            loop.Append(Line.CreateBound(c00, c10));
            loop.Append(Line.CreateBound(c10, c11));
            loop.Append(Line.CreateBound(c11, c01));
            loop.Append(Line.CreateBound(c01, c00));
            var profile = new CurveArrArray();
            profile.Append(loop);
            var ext = famDoc.FamilyCreate.NewExtrusion(true, profile, sketch, s.Height)
                ?? throw new OpFailedException("body_failed", "Revit did not create the box for block '" + s.Block + "'");
            famDoc.Regenerate();

            double m = Math.Max(w, d) * 0.25;
            var planes = new FilteredElementCollector(famDoc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
            var centreX = planes.FirstOrDefault(p => IsCentre(p, XYZ.BasisX));
            var centreY = planes.FirstOrDefault(p => IsCentre(p, XYZ.BasisY));
            var left = Side(famDoc, plan, centreX, true, s.MinX, s.MinY - m, s.MaxY + m, "CAD Left");
            var right = Side(famDoc, plan, centreX, true, s.MaxX, s.MinY - m, s.MaxY + m, "CAD Right");
            var front = Side(famDoc, plan, centreY, false, s.MinY, s.MinX - m, s.MaxX + m, "CAD Front");
            var back = Side(famDoc, plan, centreY, false, s.MaxY, s.MinX - m, s.MaxX + m, "CAD Back");

            Align(famDoc, plan, ext, left, -XYZ.BasisX, s.MinX, true);
            Align(famDoc, plan, ext, right, XYZ.BasisX, s.MaxX, true);
            Align(famDoc, plan, ext, front, -XYZ.BasisY, s.MinY, false);
            Align(famDoc, plan, ext, back, XYZ.BasisY, s.MaxY, false);

            Dim(famDoc, plan, left, right, Line.CreateBound(new XYZ(s.MinX, s.MaxY + m, 0), new XYZ(s.MaxX, s.MaxY + m, 0))).FamilyLabel = pW;
            Dim(famDoc, plan, front, back, Line.CreateBound(new XYZ(s.MaxX + m, s.MinY, 0), new XYZ(s.MaxX + m, s.MaxY, 0))).FamilyLabel = pD;
            if (centreX != null && left.Id != centreX.Id && right.Id != centreX.Id)
                Dim(famDoc, plan, centreX, left, Line.CreateBound(new XYZ(0, s.MaxY + 2 * m, 0), new XYZ(s.MinX, s.MaxY + 2 * m, 0))).IsLocked = true;
            if (centreY != null && front.Id != centreY.Id && back.Id != centreY.Id)
                Dim(famDoc, plan, centreY, front, Line.CreateBound(new XYZ(s.MaxX + 2 * m, 0, 0), new XYZ(s.MaxX + 2 * m, s.MinY, 0))).IsLocked = true;

            fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM)
                ?? throw new OpFailedException("body_failed", "the extrusion has no end parameter"), pH);
            fm.AssociateElementParameterToFamilyParameter(ext.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)
                ?? throw new OpFailedException("body_failed", "the extrusion has no material parameter"), pM);

            if (!s.BodyInPlan)
            {
                var vis = ext.GetVisibility();
                vis.IsShownInTopBottom = false;
                try { vis.IsShownInPlanRCPCut = false; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { }
                ext.SetVisibility(vis);
            }
            return ext;
        }

        /// <summary>A vertical reference plane through the family origin with the given normal (the template's centre planes).</summary>
        private static bool IsCentre(ReferencePlane p, XYZ normal)
        {
            var pl = p.GetPlane();
            return Math.Abs(Math.Abs(pl.Normal.DotProduct(normal)) - 1) < 1e-9 && Math.Abs(pl.Normal.DotProduct(pl.Origin)) <= Tol;
        }

        private static ReferencePlane Side(Document famDoc, View plan, ReferencePlane? centre, bool normalX, double at, double from, double to, string name)
        {
            if (centre != null && Math.Abs(at) <= Tol) return centre;
            var bubble = normalX ? new XYZ(at, to, 0) : new XYZ(from, at, 0);
            var free = normalX ? new XYZ(at, from, 0) : new XYZ(to, at, 0);
            var rp = famDoc.FamilyCreate.NewReferencePlane(bubble, free, XYZ.BasisZ, plan)
                ?? throw new OpFailedException("body_failed", "Revit did not create reference plane " + name);
            try { rp.Name = name; }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { }
            return rp;
        }

        private static void Align(Document famDoc, View plan, Extrusion ext, ReferencePlane rp, XYZ normal, double at, bool alongX)
        {
            Reference? face = null;
            foreach (var obj in ext.get_Geometry(new Options { ComputeReferences = true }))
            {
                if (!(obj is Solid so) || so.Faces.Size == 0) continue;
                foreach (Face f in so.Faces)
                    if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(normal) && Math.Abs((alongX ? pf.Origin.X : pf.Origin.Y) - at) <= Tol && pf.Reference != null)
                        face = pf.Reference;
            }
            if (face is null) throw new OpFailedException("body_failed", "the box face at " + (alongX ? "x" : "y") + " = " + at.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " ft was not found to lock to its reference plane");
            famDoc.FamilyCreate.NewAlignment(plan, rp.GetReference(), face);
        }

        private static Dimension Dim(Document famDoc, View plan, ReferencePlane a, ReferencePlane b, Line line)
        {
            var refs = new ReferenceArray();
            refs.Append(a.GetReference());
            refs.Append(b.GetReference());
            return famDoc.FamilyCreate.NewLinearDimension(plan, line, refs)
                ?? throw new OpFailedException("body_failed", "Revit did not create the dimension between '" + a.Name + "' and '" + b.Name + "'");
        }

        // ------------------------------------------------------------------ flex test

        private static void Flex(OpContext ctx, Document famDoc, FamilySpec s, FamilyBuilt built)
        {
            var u = ctx.Units;
            if (s.Body != "box")
            {
                built.FlexOk = true;
                built.Flex = new JsonObject().Add("ok", true).Add("cases", new JsonArray()).Add("note", "body " + s.Body + " has no size parameters to flex");
                return;
            }
            var fm = famDoc.FamilyManager;
            double w = s.MaxX - s.MinX, d = s.MaxY - s.MinY;
            var cases = new[] { ("nominal", 1.0), ("larger", 1.25), ("smaller", 0.8) };
            var list = new JsonArray();
            bool allOk = true;
            foreach (var (name, k) in cases)
            {
                double ew = w * k, ed = d * k, eh = s.Height * k;
                var problems = new List<string>();
                var c = new JsonObject().Add("name", name)
                    .Add("expected", new JsonObject().Add(Width, u.LengthJson(ew)).Add(Depth, u.LengthJson(ed)).Add(Height, u.LengthJson(eh)));
                using (var g = new TransactionGroup(famDoc, "SA flex " + name))
                {
                    g.Start();
                    var collector = new FailureCollector();
                    using (var t = new Transaction(famDoc, "SA flex " + name))
                    {
                        t.Start();
                        collector.Install(t);
                        try
                        {
                            fm.Set(FamilyDoc.Find(fm, Width, out _)!, ew);
                            fm.Set(FamilyDoc.Find(fm, Depth, out _)!, ed);
                            fm.Set(FamilyDoc.Find(fm, Height, out _)!, eh);
                        }
                        catch (Exception e) { problems.Add("Revit refused the values: " + e.Message); }
                        if (problems.Count > 0) t.RollBack();
                        else if (t.Commit() != TransactionStatus.Committed) problems.Add("Revit did not commit the values");
                    }
                    problems.AddRange(collector.Errors.Select(x => "error: " + x.Text()));
                    problems.AddRange(collector.Warnings.Select(x => "warning: " + x.Text()));
                    var bb = FlexTest.SolidBounds(famDoc);
                    if (bb is null) problems.Add("the body has no solid geometry");
                    else
                    {
                        double mw = bb.Max.X - bb.Min.X, md = bb.Max.Y - bb.Min.Y, mh = bb.Max.Z - bb.Min.Z;
                        c.Add("measured", new JsonObject().Add(Width, u.LengthJson(mw)).Add(Depth, u.LengthJson(md)).Add(Height, u.LengthJson(mh))
                            .Add("min", new JsonArray(u.LengthJson(bb.Min.X), u.LengthJson(bb.Min.Y), u.LengthJson(bb.Min.Z))));
                        if (Math.Abs(mw - ew) > Tol) problems.Add(Width + " measures " + Show(u, mw) + ", expected " + Show(u, ew));
                        if (Math.Abs(md - ed) > Tol) problems.Add(Depth + " measures " + Show(u, md) + ", expected " + Show(u, ed));
                        if (Math.Abs(mh - eh) > Tol) problems.Add(Height + " measures " + Show(u, mh) + ", expected " + Show(u, eh));
                        if (name == "nominal" && (Math.Abs(bb.Min.X - s.MinX) > Tol || Math.Abs(bb.Min.Y - s.MinY) > Tol || Math.Abs(bb.Min.Z) > Tol))
                            problems.Add("the box is not at the block's extents: its lower corner is at [" + Show(u, bb.Min.X) + ", " + Show(u, bb.Min.Y) + ", " + Show(u, bb.Min.Z) +
                                "], expected [" + Show(u, s.MinX) + ", " + Show(u, s.MinY) + ", 0]");
                    }
                    g.RollBack();
                }
                c.Add("ok", problems.Count == 0);
                if (problems.Count > 0) { c.Add("problems", JsonArray.FromStrings(problems)); allOk = false; }
                list.Add(c);
            }
            built.FlexOk = allOk;
            built.Flex = new JsonObject().Add("ok", allOk).Add("cases", list);
        }

        private static string Show(RevitUnits u, double internalLength) => Kit.Num(u.LengthFromInternal(internalLength));
    }
}
