using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Families;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Host;
using SA.Revit.Ops.FamilyCreation;
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
        /// <summary>The box body as a family.create recipe (<see cref="CadBoxRecipe"/>), checked when the op is planned; null for other bodies.</summary>
        public FamilyRecipe? Recipe;
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
    /// <item>box body: the block's <see cref="FamilySpec.Recipe"/> built by family.create's <see cref="FamilyBuilder"/>
    /// first (category, Width/Depth/Height, material and CAD Block/CAD Source parameters, the type, planes, the box
    /// locked to them and the labelled dimensions), then hidden when cut in plan unless bodyInPlan;</item>
    /// <item>other bodies: type parameters CAD Block and CAD Source on the template's type, and for import3d a 3D import.</item>
    /// </list>
    /// A box family then gets family.create's flex test (<see cref="FlexTest"/>) for its type, again for two temporary
    /// types at 1.25 and 0.8 times its size (made and tested inside a transaction group that is rolled back), and a check
    /// that the nominal box sits on the block's extents.
    /// </summary>
    internal static class CadFamilyBuilder
    {
        public const string Width = CadBoxRecipe.Width;
        public const string Depth = CadBoxRecipe.Depth;
        public const string Height = CadBoxRecipe.Height;
        private const int MaxLines = 20000;

        private static double Tol => UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);

        public static FamilyBuilt Build(OpContext ctx, Document famDoc, FamilySpec s)
        {
            var built = new FamilyBuilt();
            var collector = new FailureCollector();
            FamilyBuilder? box = null;
            using (var t = new Transaction(famDoc, "SA family.fromcad"))
            {
                if (t.Start() != TransactionStatus.Started) throw new OpFailedException("transaction_failed", "Revit refused to start a transaction in the family for block '" + s.Block + "'");
                collector.Install(t);
                try
                {
                    if (s.Recipe != null) box = BuildBox(ctx, famDoc, s);
                    Make(ctx, famDoc, s, built);
                    if (box != null && !s.BodyInPlan) HideWhenCut(box);
                }
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
            if (box is null)
            {
                built.FlexOk = true;
                built.Flex = new JsonObject().Add("ok", true).Add("cases", new JsonArray()).Add("note", "body " + s.Body + " has no size parameters to flex");
                return built;
            }
            Flex(ctx, famDoc, s, built);
            return built;
        }

        /// <summary>Runs family.create's builder on the block's recipe inside the caller's transaction; its errors fail the block.</summary>
        private static FamilyBuilder BuildBox(OpContext ctx, Document famDoc, FamilySpec s)
        {
            var owner = famDoc.OwnerFamily ?? throw new OpFailedException("template_unusable", "the new family document has no owner family");
            BuiltInCategory? setCategory = s.SetCategory is BuiltInCategory bic && (owner.FamilyCategory is null || owner.FamilyCategory.BuiltInCategory != bic) ? bic : (BuiltInCategory?)null;
            var issues = new List<Issue>();
            var builder = new FamilyBuilder(famDoc, s.Recipe!, ctx.Units, issues, ctx.Op.ArgsPath);
            builder.Build(setCategory, setCategory?.ToString() ?? "");
            foreach (var w in issues.Where(i => !i.IsError)) ctx.Warn("block '" + s.Block + "': " + w.Message);
            var errors = issues.Where(i => i.IsError).ToList();
            if (errors.Count > 0)
                throw new OpFailedException("family_build_failed", "building the box family for block '" + s.Block + "' failed: " + string.Join("; ", errors.Take(5).Select(i => i.Message)),
                    new JsonObject().Add("issues", Issue.ToJson(errors)));
            return builder;
        }

        /// <summary>The recipe can hide a form in plan views but not where a plan cuts it; a tall body cut by the plan would cover the symbol.</summary>
        private static void HideWhenCut(FamilyBuilder box)
        {
            foreach (var kv in box.Forms)
            {
                var vis = kv.Value.GetVisibility();
                try { vis.IsShownInPlanRCPCut = false; }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { continue; }
                kv.Value.SetVisibility(vis);
            }
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
                var imp = (ImportInstance)famDoc.GetElement(importId);
                built.Symbol = ToLines(famDoc, imp, plan, sketch, Subcategory(famDoc, famCat, s.Subcategory), s.Block);
                // Revit pins an origin-to-origin import, and a pinned element cannot be deleted.
                if (imp.Pinned) imp.Pinned = false;
                try { famDoc.Delete(importId); }
                catch (Autodesk.Revit.Exceptions.ArgumentException e) { throw new OpFailedException("import_not_removed", "the imported DWG for block '" + s.Block + "' could not be removed after its lines were copied: " + e.Message); }
            }
            else built.Symbol = ImportSubcategory(ctx, famDoc, (ImportInstance)famDoc.GetElement(importId), famCat, s);

            if (s.Recipe != null) return;
            var pBlock = Param(fm, CadFamilies.BlockParameter, GroupTypeId.IdentityData, SpecTypeId.String.Text);
            var pSource = Param(fm, CadFamilies.SourceParameter, GroupTypeId.IdentityData, SpecTypeId.String.Text);
            EnsureType(fm, s.TypeName);
            fm.Set(pBlock, s.Block);
            fm.Set(pSource, s.Source);
            if (s.Body == "import3d") Import(famDoc, s, plan, false);
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

        // ------------------------------------------------------------------ flex test

        private static readonly (string name, double k)[] Scaled = { ("larger", 1.25), ("smaller", 0.8) };

        private static void Flex(OpContext ctx, Document famDoc, FamilySpec s, FamilyBuilt built)
        {
            var u = ctx.Units;
            var r = s.Recipe!;
            var fm = famDoc.FamilyManager;
            var axes = new List<FlexAxis>();
            if (r.Flex != null)
                foreach (var kv in r.Flex.Axes())
                    if (FamilyDoc.Find(fm, kv.Value, out _) is FamilyParameter fp) axes.Add(new FlexAxis { Axis = kv.Key, Parameter = fp });
            double tol = u.LengthToInternal(r.Flex?.Tolerance ?? u.LengthTolerance);
            double w = s.MaxX - s.MinX, d = s.MaxY - s.MinY;
            var cases = new JsonArray();
            bool allOk = true;

            foreach (var o in FlexTest.Run(famDoc, r.Types.Select(x => x.Name).ToList(), axes, tol, u))
            {
                var problems = new List<string>(o.Problems);
                if (o.Ok && FlexTest.SolidBounds(famDoc) is BoundingBoxXYZ bb && (Math.Abs(bb.Min.X - s.MinX) > tol || Math.Abs(bb.Min.Y - s.MinY) > tol || Math.Abs(bb.Min.Z) > tol))
                    problems.Add("the box is not at the block's extents: its lower corner is at [" + Show(u, bb.Min.X) + ", " + Show(u, bb.Min.Y) + ", " + Show(u, bb.Min.Z) +
                        "], expected [" + Show(u, s.MinX) + ", " + Show(u, s.MinY) + ", 0]");
                allOk &= Case(cases, r.Types.Count == 1 ? "nominal" : o.Type, o, problems, w, d, s.Height, u);
            }

            using (var g = new TransactionGroup(famDoc, "SA flex scaled sizes"))
            {
                g.Start();
                var names = new List<string>();
                var setup = new List<string>();
                var collector = new FailureCollector();
                using (var t = new Transaction(famDoc, "SA flex scaled types"))
                {
                    t.Start();
                    collector.Install(t);
                    try
                    {
                        var first = FamilyDoc.Type(fm, r.Types[0].Name) ?? throw new InvalidOperationException("type '" + r.Types[0].Name + "' is not in the family");
                        foreach (var (name, k) in Scaled)
                        {
                            fm.CurrentType = first;
                            string tn = "SA flex " + name;
                            fm.NewType(tn);
                            fm.Set(FamilyDoc.Find(fm, Width, out _)!, w * k);
                            fm.Set(FamilyDoc.Find(fm, Depth, out _)!, d * k);
                            fm.Set(FamilyDoc.Find(fm, Height, out _)!, s.Height * k);
                            names.Add(tn);
                        }
                    }
                    catch (Exception e) { setup.Add("Revit refused the scaled sizes: " + e.Message); }
                    if (setup.Count > 0) t.RollBack();
                    else if (t.Commit() != TransactionStatus.Committed) setup.Add("Revit did not commit the scaled sizes");
                }
                setup.AddRange(collector.Errors.Select(x => "error: " + x.Text()));
                setup.AddRange(collector.Warnings.Select(x => "warning: " + x.Text()));
                if (setup.Count > 0)
                {
                    foreach (var (name, k) in Scaled)
                        allOk &= Case(cases, name, null, setup, w * k, d * k, s.Height * k, u);
                }
                else
                {
                    var outcomes = FlexTest.Run(famDoc, names, axes, tol, u);
                    for (int i = 0; i < Scaled.Length; i++)
                        allOk &= Case(cases, Scaled[i].name, outcomes[i], outcomes[i].Problems, w * Scaled[i].k, d * Scaled[i].k, s.Height * Scaled[i].k, u);
                }
                g.RollBack();
            }

            using (var t = new Transaction(famDoc, "SA family.fromcad first type"))
            {
                if (t.Start() == TransactionStatus.Started)
                {
                    if (FamilyDoc.Type(fm, r.Types[0].Name) is FamilyType first) fm.CurrentType = first;
                    t.Commit();
                }
            }
            built.FlexOk = allOk;
            built.Flex = new JsonObject().Add("ok", allOk).Add("cases", cases);
        }

        /// <summary>Adds one flex case in the op's output shape { name, type, expected, measured, ok, problems }; returns whether it passed.</summary>
        private static bool Case(JsonArray cases, string name, FlexOutcome? o, List<string> problems, double w, double d, double h, RevitUnits u)
        {
            bool ok = problems.Count == 0 && (o?.Ok ?? false);
            var c = new JsonObject().Add("name", name);
            if (o != null) c.Add("type", o.Type);
            c.Add("expected", new JsonObject().Add(Width, u.LengthJson(w)).Add(Depth, u.LengthJson(d)).Add(Height, u.LengthJson(h)));
            if (o?.Json["size"] is JsonObject size)
                c.Add("measured", new JsonObject().Add(Width, size["x"] ?? JsonValue.Null).Add(Depth, size["y"] ?? JsonValue.Null).Add(Height, size["z"] ?? JsonValue.Null));
            c.Add("ok", ok);
            if (problems.Count > 0) c.Add("problems", JsonArray.FromStrings(problems));
            else if (!ok) c.Add("problems", JsonArray.FromStrings(new[] { "the flex test did not pass" }));
            cases.Add(c);
            return ok;
        }
        private static string Show(RevitUnits u, double internalLength) => Kit.Num(u.LengthFromInternal(internalLength));
    }
}
