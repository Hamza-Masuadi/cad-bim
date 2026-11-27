using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Families;
using SA.Core.Geometry;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.FamilyEditing;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FamilyCreation
{
    /// <summary>
    /// Builds a <see cref="FamilyRecipe"/> in an open family document, inside the caller's transaction. Order matters:
    /// type values and formulas are set before any dimension is labelled, because a label makes the parameter drive the
    /// dimension (RevitAPI.xml, Dimension.FamilyLabel), so a parameter still at 0 would collapse the geometry. Faces are
    /// locked with NewAlignment only after a regeneration, and only to reference planes they already lie on (the API does
    /// not move them). Levels cannot be aligned to faces, so "Ref. Level" is a pinned horizontal reference plane at 0.
    /// Problems become issues; nothing here throws for a bad recipe.
    /// </summary>
    internal sealed class FamilyBuilder
    {
        private sealed class PlaneHandle
        {
            public ReferencePlane Element = null!;
            public RecipePlane Recipe = null!;
            public Reference Reference => Element.GetReference();
        }

        private readonly Document _doc;
        private readonly FamilyRecipe _r;
        private readonly RevitUnits _u;
        private readonly List<Issue> _issues;
        private readonly string _path;
        private readonly double _tol;
        private ViewPlan? _plan;
        private View? _front;
        private readonly Dictionary<string, PlaneHandle> _planes = new Dictionary<string, PlaneHandle>(StringComparer.Ordinal);
        private readonly Dictionary<string, FamilyParameter> _params = new Dictionary<string, FamilyParameter>(StringComparer.Ordinal);

        public List<KeyValuePair<RecipeForm, GenericForm>> Forms { get; } = new List<KeyValuePair<RecipeForm, GenericForm>>();
        public List<string> CreatedMaterials { get; } = new List<string>();
        public List<string> CreatedSubcategories { get; } = new List<string>();
        public int Symbols { get; private set; }
        public int Dimensions { get; private set; }
        public int Locks { get; private set; }

        public FamilyBuilder(Document famDoc, FamilyRecipe recipe, RevitUnits units, List<Issue> issues, string argsPath)
        {
            _doc = famDoc;
            _r = recipe;
            _u = units;
            _issues = issues;
            _path = argsPath;
            _tol = units.LengthToleranceInternal;
        }

        private bool Failed => _issues.Any(i => i.IsError);
        private void Error(string path, string code, string message) => _issues.Add(new Issue(path, code, message));
        private void Warn(string path, string code, string message) => _issues.Add(new Issue(path, code, message, IssueSeverity.Warning));
        private double L(double jobLength) => _u.LengthToInternal(jobLength);
        private FamilyManager Fm => _doc.FamilyManager;

        /// <summary>Runs every step; stops at the first step that reports an error.</summary>
        public void Build(BuiltInCategory? setCategory, string categoryLabel)
        {
            var steps = new List<Action>
            {
                () => { if (setCategory.HasValue) SetCategory(setCategory.Value, categoryLabel); },
                FindViews, AddParameters, MakeTypes, MakeMaterials, SetFormulas, SetValues, MakePlanes, MakeForms,
                () => _doc.Regenerate(),
                LockForms, Associate, MakeSymbols, MakeDimensions,
                () => _doc.Regenerate(),
            };
            foreach (var step in steps)
            {
                if (Failed) return;
                try { step(); }
                catch (Exception e) { Error(_path, IssueCodes.InvalidValue, "building the family failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ category and views

        private void SetCategory(BuiltInCategory bic, string label)
        {
            var cat = Category.GetCategory(_doc, bic);
            if (cat is null) { Error(_path + ".category", IssueCodes.NotFound, "the family template has no category '" + label + "'"); return; }
            try { _doc.OwnerFamily.FamilyCategory = cat; }
            catch (Autodesk.Revit.Exceptions.ArgumentException e)
            {
                Error(_path + ".category", IssueCodes.NotAllowed, "Revit does not allow category '" + label + "' for a family made from this template: " + e.Message);
            }
        }

        private void FindViews()
        {
            var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            _plan = views.OfType<ViewPlan>().FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);
            _front = views.FirstOrDefault(v => v.ViewType == ViewType.Elevation && Math.Abs(v.ViewDirection.Y) > 0.99 && v.ViewDirection.Y < 0)
                     ?? views.FirstOrDefault(v => v.ViewType == ViewType.Elevation && Math.Abs(v.ViewDirection.Y) > 0.99);
            if (_plan is null) Error(_path + ".template", IssueCodes.NotFound, "the family template has no floor plan view, so planes and dimensions cannot be drawn; use a model family template");
            if (_front is null) Error(_path + ".template", IssueCodes.NotFound, "the family template has no front or back elevation, so heights cannot be dimensioned; use a model family template");
        }

        // ------------------------------------------------------------------ parameters, types, values

        private static string DefaultGroup(string spec) =>
            spec == "length" || spec == "angle" ? "dimensions" : spec == "material" ? "materials" : spec == "yesNo" ? "graphics" : "data";

        private void AddParameters()
        {
            foreach (var p in _r.Parameters)
            {
                var spec = FamilyDoc.SpecFor(p.Spec);
                var existing = FamilyDoc.Find(Fm, p.Name, out _);
                if (existing != null)
                {
                    if (!ParameterApi.SameSpec(FamilyDoc.Spec(existing), spec) || existing.IsInstance != p.Instance)
                    {
                        Error(p.Path + ".name", IssueCodes.ExistingDiffers, "the template already has '" + p.Name + "' as a " + (existing.IsInstance ? "instance " : "type ") +
                            ParameterApi.SpecLabel(FamilyDoc.Spec(existing)) + " parameter; choose another name");
                        continue;
                    }
                    Warn(p.Path + ".name", IssueCodes.AlreadyExists, "the template already has parameter '" + p.Name + "'; it is used as it is");
                    _params[p.Name] = existing;
                    continue;
                }
                string groupName = p.Group ?? DefaultGroup(p.Spec);
                var group = FamilyDoc.GroupFor(groupName);
                if (!Fm.IsUserAssignableParameterGroup(group))
                {
                    if (p.Group != null) { Error(p.Path + ".group", IssueCodes.NotAllowed, "Revit does not allow new parameters in group '" + groupName + "' in this family"); continue; }
                    group = FamilyDoc.GroupFor("data");
                }
                try
                {
                    var added = Fm.AddParameter(p.Name, group, spec, p.Instance);
                    if (added is null) Error(p.Path, IssueCodes.InvalidValue, "Revit did not add parameter '" + p.Name + "'");
                    else _params[p.Name] = added;
                }
                catch (Exception e) { Error(p.Path, IssueCodes.InvalidValue, "Revit refused parameter '" + p.Name + "': " + e.Message); }
            }
        }

        private void MakeTypes()
        {
            var existing = Fm.Types.Cast<FamilyType>().Where(t => t != null).ToList();
            var keep = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _r.Types.Count; i++)
            {
                var rt = _r.Types[i];
                try
                {
                    var same = Fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == rt.Name);
                    if (same != null) Fm.CurrentType = same;
                    else if (i == 0 && existing.Count > 0)
                    {
                        Fm.CurrentType = existing[0];
                        Fm.RenameCurrentType(rt.Name);
                    }
                    else Fm.NewType(rt.Name);
                    keep.Add(rt.Name);
                }
                catch (Exception e) { Error(rt.Path + ".name", IssueCodes.InvalidValue, "Revit did not create type '" + rt.Name + "': " + e.Message); }
            }
            foreach (var extra in Fm.Types.Cast<FamilyType>().Where(t => !keep.Contains(t.Name)).ToList())
            {
                try { Fm.CurrentType = extra; Fm.DeleteCurrentType(); }
                catch (Exception e) { Warn(_path + ".types", IssueCodes.Constraint, "the template's type '" + extra.Name + "' could not be removed: " + e.Message); }
            }
            CurrentFirst();
        }

        private void CurrentFirst()
        {
            var first = _r.Types.Count > 0 ? Fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == _r.Types[0].Name) : null;
            if (first != null) Fm.CurrentType = first;
        }

        private void MakeMaterials()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in _r.Types)
                foreach (var kv in t.Values)
                    if (_r.Parameter(kv.Key)?.Spec == "material" && kv.Value is JsonString s && s.Value.Trim().Length > 0) names.Add(s.Value);
            foreach (var name in names.Where(n => FamilyDoc.MaterialByName(_doc, n) is null))
            {
                try { Material.Create(_doc, name); CreatedMaterials.Add(name); }
                catch (Exception e) { Error(_path + ".types", IssueCodes.InvalidValue, "Revit did not create material '" + name + "': " + e.Message); }
            }
        }

        private void SetFormulas()
        {
            CurrentFirst();
            foreach (var p in _r.Parameters.Where(p => p.Formula != null))
            {
                if (!_params.TryGetValue(p.Name, out var prm)) continue;
                if (!prm.CanAssignFormula) { Error(p.Path + ".formula", IssueCodes.NotAllowed, "'" + p.Name + "' cannot take a formula"); continue; }
                try { Fm.SetFormula(prm, p.Formula); }
                catch (Exception e) { Error(p.Path + ".formula", IssueCodes.InvalidValue, "Revit refused the formula for '" + p.Name + "' (" + p.Formula + "): " + e.Message); }
            }
        }

        private void SetValues()
        {
            foreach (var rt in _r.Types)
            {
                var ft = Fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == rt.Name);
                if (ft is null) continue;
                Fm.CurrentType = ft;
                foreach (var kv in rt.Values)
                {
                    string at = rt.ValuePaths.TryGetValue(kv.Key, out var vp) ? vp : rt.Path;
                    if (!_params.TryGetValue(kv.Key, out var prm) || prm.IsDeterminedByFormula) continue;
                    if (kv.Value.IsNull) continue;
                    if (!FamilyDoc.TryPrepare(_doc, prm, kv.Value, _u, out var pv, out string err)) { Error(at, IssueCodes.InvalidValue, "'" + kv.Key + "' (type '" + rt.Name + "'): " + err); continue; }
                    try { FamilyDoc.Set(Fm, prm, pv); }
                    catch (Exception e) { Error(at, IssueCodes.InvalidValue, "Revit refused '" + kv.Key + "' = " + kv.Value.ToJson() + " in type '" + rt.Name + "': " + e.Message); }
                }
            }
            CurrentFirst();
        }

        // ------------------------------------------------------------------ planes

        private static XYZ Normal(string axis) => axis == "x" ? XYZ.BasisX : axis == "y" ? XYZ.BasisY : XYZ.BasisZ;

        private static double Coord(XYZ p, string axis) => axis == "x" ? p.X : axis == "y" ? p.Y : p.Z;

        private ReferencePlane? FindReserved(string name, string axis)
        {
            var all = new FilteredElementCollector(_doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>().ToList();
            var byName = all.FirstOrDefault(p => p.Name == name && Math.Abs(Math.Abs(p.Normal.DotProduct(Normal(axis))) - 1) < 1e-6);
            if (byName != null) return byName;
            return all.FirstOrDefault(p => Math.Abs(Math.Abs(p.Normal.DotProduct(Normal(axis))) - 1) < 1e-6 && Math.Abs(Coord(p.GetPlane().Origin, axis)) <= _tol &&
                                           (axis != "z" || p.Name.Length == 0 || p.Name == name || p.Name == RefLevelPlaneName));
        }

        private const string RefLevelPlaneName = "Ref. Level (plane)";

        private ReferencePlane NewPlane(string axis, double atInternal)
        {
            double len = L(Length(1000));
            ReferencePlane rp;
            if (axis == "z")
                rp = _doc.FamilyCreate.NewReferencePlane(new XYZ(-len, 0, atInternal), new XYZ(len, 0, atInternal), XYZ.BasisY, _front!);
            else if (axis == "x")
                rp = _doc.FamilyCreate.NewReferencePlane(new XYZ(atInternal, -len, 0), new XYZ(atInternal, len, 0), XYZ.BasisZ, _plan!);
            else
                rp = _doc.FamilyCreate.NewReferencePlane(new XYZ(-len, atInternal, 0), new XYZ(len, atInternal, 0), XYZ.BasisZ, _plan!);
            return rp;
        }

        private double Length(double mm) => SA.Core.Units.Length.Convert(mm, SA.Core.Units.LengthUnit.Millimetre, _u.JobUnit);

        private void MakePlanes()
        {
            foreach (var rp in _r.Planes)
            {
                try
                {
                    ReferencePlane? el;
                    if (rp.Reserved)
                    {
                        el = FindReserved(rp.Name, rp.Axis);
                        if (el is null)
                        {
                            el = NewPlane(rp.Axis, 0);
                            el.Name = rp.Axis == "z" ? RefLevelPlaneName : rp.Name;
                        }
                        if (!el.Pinned) el.Pinned = true;
                    }
                    else
                    {
                        el = NewPlane(rp.Axis, L(rp.At));
                        el.Name = rp.Name;
                        if (rp.Pinned) el.Pinned = true;
                    }
                    _planes[rp.Name] = new PlaneHandle { Element = el, Recipe = rp };
                }
                catch (Exception e) { Error(rp.Path, IssueCodes.InvalidValue, "Revit did not create reference plane '" + rp.Name + "': " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ forms

        /// <summary>Sketch frame: origin and the 3D directions of sketch u, v and the normal.</summary>
        private (XYZ o, XYZ u, XYZ v, XYZ n) Frame(RecipeForm f)
        {
            double off = L(f.Offset);
            switch (f.Sketch)
            {
                case "front": return (new XYZ(0, off, 0), XYZ.BasisX, XYZ.BasisZ, XYZ.BasisY);
                case "side": return (new XYZ(off, 0, 0), XYZ.BasisY, XYZ.BasisZ, XYZ.BasisX);
                default: return (new XYZ(0, 0, off), XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);
            }
        }

        private static XYZ At((XYZ o, XYZ u, XYZ v, XYZ n) fr, double u, double v, double w) => fr.o + fr.u * u + fr.v * v + fr.n * w;

        private CurveArray LoopCurves(RecipeLoop loop, Func<double, double, XYZ> map)
        {
            var arr = new CurveArray();
            if (loop.IsCircle)
            {
                var c = loop.Centre!.Value;
                double r = loop.Radius;
                XYZ p0 = map(L(c.X + r), L(c.Y)), p1 = map(L(c.X), L(c.Y + r)), p2 = map(L(c.X - r), L(c.Y)), p3 = map(L(c.X), L(c.Y - r));
                arr.Append(Arc.Create(p0, p2, p1));
                arr.Append(Arc.Create(p2, p0, p3));
                return arr;
            }
            var pts = loop.Points;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                arr.Append(Line.CreateBound(map(L(a.X), L(a.Y)), map(L(b.X), L(b.Y))));
            }
            return arr;
        }

        private CurveArray PointsCurves(List<Point2> pts, Func<double, double, XYZ> map) =>
            LoopCurves(new RecipeLoop { Points = pts }, map);

        private void MakeForms()
        {
            foreach (var f in _r.Forms)
            {
                try
                {
                    var form = MakeForm(f);
                    if (form != null) Forms.Add(new KeyValuePair<RecipeForm, GenericForm>(f, form));
                }
                catch (Exception e) { Error(f.Path, IssueCodes.InvalidValue, "Revit did not create form '" + f.Label + "': " + e.Message); }
            }
        }

        private GenericForm? MakeForm(RecipeForm f)
        {
            bool solid = !f.Void;
            var create = _doc.FamilyCreate;
            if (f.Kind == "extrusion" && f.IsBox)
            {
                var x = f.BoxX!.Select(n => _planes[n].Recipe).OrderBy(p => p.At).ToArray();
                var y = f.BoxY!.Select(n => _planes[n].Recipe).OrderBy(p => p.At).ToArray();
                var z = f.BoxZ!.Select(n => _planes[n].Recipe).OrderBy(p => p.At).ToArray();
                double zb = L(z[0].At);
                var loop = new RecipeLoop
                {
                    Points = new List<Point2> { new Point2(x[0].At, y[0].At), new Point2(x[1].At, y[0].At), new Point2(x[1].At, y[1].At), new Point2(x[0].At, y[1].At) },
                };
                var profile = new CurveArrArray();
                profile.Append(LoopCurves(loop, (a, b) => new XYZ(a, b, zb)));
                var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, zb)));
                return create.NewExtrusion(solid, profile, sp, L(z[1].At) - zb);
            }

            var fr = Frame(f);
            switch (f.Kind)
            {
                case "extrusion":
                {
                    var profile = new CurveArrArray();
                    foreach (var l in f.Profile) profile.Append(LoopCurves(l, (a, b) => At(fr, a, b, 0)));
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(fr.n, fr.o));
                    double lo = L(Math.Min(f.Start, f.End!.Value)), hi = L(Math.Max(f.Start, f.End.Value));
                    var ex = create.NewExtrusion(solid, profile, sp, Math.Abs(hi) > _tol ? hi : lo);
                    if (Math.Abs(hi) > _tol) { if (Math.Abs(lo) > _tol) ex.StartOffset = lo; }
                    else { ex.EndOffset = hi; ex.StartOffset = lo; }
                    return ex;
                }
                case "blend":
                {
                    double w0 = L(f.Start), w1 = L(f.End!.Value);
                    var bottom = LoopCurves(f.Profile[0], (a, b) => At(fr, a, b, w0));
                    var top = PointsCurves(Clean(f.TopProfile), (a, b) => At(fr, a, b, w1));
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(fr.n, fr.o + fr.n * w0));
                    return create.NewBlend(solid, bottom, top, sp);
                }
                case "revolution":
                {
                    var profile = new CurveArrArray();
                    foreach (var l in f.Profile) profile.Append(LoopCurves(l, (a, b) => At(fr, a, b, 0)));
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(fr.n, fr.o));
                    var axis = Line.CreateBound(At(fr, L(f.Axis![0].X), L(f.Axis[0].Y), 0), At(fr, L(f.Axis[1].X), L(f.Axis[1].Y), 0));
                    return create.NewRevolution(solid, profile, sp, axis, _u.AngleToInternal(f.StartAngle), _u.AngleToInternal(f.EndAngle));
                }
                case "sweep":
                {
                    var path = new CurveArray();
                    for (int i = 0; i + 1 < f.PathPoints.Count; i++)
                        path.Append(Line.CreateBound(At(fr, L(f.PathPoints[i].X), L(f.PathPoints[i].Y), 0), At(fr, L(f.PathPoints[i + 1].X), L(f.PathPoints[i + 1].Y), 0)));
                    var loops = new CurveArrArray();
                    foreach (var l in f.Profile) loops.Append(LoopCurves(l, (a, b) => new XYZ(a, b, 0)));
                    var prof = _doc.Application.Create.NewCurveLoopsProfile(loops);
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(fr.n, fr.o));
                    return create.NewSweep(solid, path, sp, prof, f.ProfileAt, ProfilePlaneLocation.Start);
                }
                case "sweptBlend":
                {
                    var path = Line.CreateBound(At(fr, L(f.PathPoints[0].X), L(f.PathPoints[0].Y), 0), At(fr, L(f.PathPoints[1].X), L(f.PathPoints[1].Y), 0));
                    var b = new CurveArrArray(); b.Append(LoopCurves(f.Profile[0], (u, v) => new XYZ(u, v, 0)));
                    var t = new CurveArrArray(); t.Append(PointsCurves(Clean(f.TopProfile), (u, v) => new XYZ(u, v, 0)));
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(fr.n, fr.o));
                    return create.NewSweptBlend(solid, path, sp, _doc.Application.Create.NewCurveLoopsProfile(b), _doc.Application.Create.NewCurveLoopsProfile(t));
                }
            }
            return null;
        }

        private List<Point2> Clean(List<Point2> pts)
        {
            double tol = SA.Core.Units.Tolerance.DefaultLength(_r.Unit);
            var l = new List<Point2>();
            foreach (var p in pts) if (l.Count == 0 || l[l.Count - 1].DistanceTo(p) > tol) l.Add(p);
            if (l.Count > 1 && l[0].DistanceTo(l[l.Count - 1]) <= tol) l.RemoveAt(l.Count - 1);
            return l;
        }

        // ------------------------------------------------------------------ locking faces

        private void LockForms()
        {
            foreach (var kv in Forms)
            {
                var f = kv.Key;
                var locks = new List<KeyValuePair<string, string>>();
                if (f.IsBox)
                {
                    var x = f.BoxX!.OrderBy(n => _planes[n].Recipe.At).ToArray();
                    var y = f.BoxY!.OrderBy(n => _planes[n].Recipe.At).ToArray();
                    var z = f.BoxZ!.OrderBy(n => _planes[n].Recipe.At).ToArray();
                    locks.Add(new KeyValuePair<string, string>("-x", x[0])); locks.Add(new KeyValuePair<string, string>("+x", x[1]));
                    locks.Add(new KeyValuePair<string, string>("-y", y[0])); locks.Add(new KeyValuePair<string, string>("+y", y[1]));
                    locks.Add(new KeyValuePair<string, string>("-z", z[0])); locks.Add(new KeyValuePair<string, string>("+z", z[1]));
                }
                else locks.AddRange(f.Lock);
                foreach (var lk in locks)
                {
                    string at = f.IsBox ? f.Path + ".box" : JsonPath.Key(f.Path + ".lock", lk.Key);
                    if (!_planes.TryGetValue(lk.Value, out var plane)) continue;
                    string axis = lk.Key.Substring(1);
                    var normal = Normal(axis) * (lk.Key[0] == '+' ? 1 : -1);
                    var face = Face(kv.Value, normal, axis, L(plane.Recipe.At));
                    if (face is null) { Error(at, IssueCodes.NotFound, "form '" + f.Label + "' has no flat " + lk.Key + " face on plane '" + lk.Value + "' to lock"); continue; }
                    var view = axis == "z" ? _front! : (View)_plan!;
                    try { _doc.FamilyCreate.NewAlignment(view, plane.Reference, face); Locks++; }
                    catch (Exception e) { Error(at, IssueCodes.InvalidValue, "Revit refused to lock the " + lk.Key + " face of form '" + f.Label + "' to plane '" + lk.Value + "': " + e.Message); }
                }
            }
        }

        private Reference? Face(GenericForm form, XYZ normal, string axis, double coordInternal)
        {
            var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true };
            var geo = form.get_Geometry(opt);
            if (geo is null) return null;
            PlanarFace? best = null;
            double bestGap = double.MaxValue;
            foreach (var g in geo)
            {
                if (!(g is Solid s)) continue;
                foreach (Face face in s.Faces)
                {
                    if (!(face is PlanarFace pf) || pf.Reference is null) continue;
                    if (pf.FaceNormal.DotProduct(normal) < 1 - 1e-6) continue;
                    double gap = Math.Abs(Coord(pf.Origin, axis) - coordInternal);
                    if (gap < bestGap) { bestGap = gap; best = pf; }
                }
            }
            return best != null && bestGap <= _tol ? best.Reference : null;
        }

        // ------------------------------------------------------------------ materials, subcategories, visibility

        private Category? Subcategory(string name, string at)
        {
            var parent = _doc.OwnerFamily.FamilyCategory;
            if (parent is null) { Error(at, IssueCodes.NotFound, "the family has no category to hold subcategory '" + name + "'"); return null; }
            foreach (Category sub in parent.SubCategories)
                if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase)) return sub;
            if (!parent.CanAddSubcategory) { Error(at, IssueCodes.NotAllowed, "category '" + parent.Name + "' takes no new subcategories"); return null; }
            try
            {
                var made = _doc.Settings.Categories.NewSubcategory(parent, name);
                CreatedSubcategories.Add(name);
                return made;
            }
            catch (Exception e) { Error(at, IssueCodes.InvalidValue, "Revit did not create subcategory '" + name + "': " + e.Message); return null; }
        }

        private void AssociateParam(Element e, BuiltInParameter bip, string paramName, string at, string what)
        {
            if (!_params.TryGetValue(paramName, out var fp)) return;
            var ep = e.get_Parameter(bip);
            if (ep is null || !Fm.CanElementParameterBeAssociated(ep)) { Error(at, IssueCodes.NotAllowed, "the form's " + what + " cannot be tied to a family parameter"); return; }
            try { Fm.AssociateElementParameterToFamilyParameter(ep, fp); }
            catch (Exception ex) { Error(at, IssueCodes.InvalidValue, "Revit refused to tie the form's " + what + " to '" + paramName + "': " + ex.Message); }
        }

        private void Associate()
        {
            foreach (var kv in Forms)
            {
                var f = kv.Key;
                var form = kv.Value;
                if (f.Material != null && !f.Void) AssociateParam(form, BuiltInParameter.MATERIAL_ID_PARAM, f.Material, f.Path + ".material", "material");
                if (f.VisibleParameter != null) AssociateParam(form, BuiltInParameter.IS_VISIBLE_PARAM, f.VisibleParameter, f.Path + ".visibleParameter", "visibility");
                if (f.Void) continue;
                if (f.Subcategory != null && Subcategory(f.Subcategory, f.Path + ".subcategory") is Category sub)
                {
                    try { form.Subcategory = sub; }
                    catch (Exception e) { Error(f.Path + ".subcategory", IssueCodes.InvalidValue, "Revit refused subcategory '" + f.Subcategory + "' for form '" + f.Label + "': " + e.Message); }
                }
                var vis = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                {
                    IsShownInCoarse = f.Coarse, IsShownInMedium = f.Medium, IsShownInFine = f.Fine,
                    IsShownInTopBottom = f.Plan, IsShownInFrontBack = f.FrontBack, IsShownInLeftRight = f.LeftRight,
                };
                try { form.SetVisibility(vis); }
                catch (Exception e) { Error(f.Path + ".visibility", IssueCodes.InvalidValue, "Revit refused the visibility of form '" + f.Label + "': " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ symbolic lines

        private void MakeSymbols()
        {
            if (_r.Symbols.Count == 0) return;
            var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
            foreach (var s in _r.Symbols)
            {
                var lines = new List<(XYZ a, XYZ b, string plane)>();
                if (s.IsRect)
                {
                    var x = s.X!.OrderBy(n => _planes[n].Recipe.At).ToArray();
                    var y = s.Y!.OrderBy(n => _planes[n].Recipe.At).ToArray();
                    double x0 = L(_planes[x[0]].Recipe.At), x1 = L(_planes[x[1]].Recipe.At), y0 = L(_planes[y[0]].Recipe.At), y1 = L(_planes[y[1]].Recipe.At);
                    lines.Add((new XYZ(x0, y0, 0), new XYZ(x1, y0, 0), y[0]));
                    lines.Add((new XYZ(x1, y0, 0), new XYZ(x1, y1, 0), x[1]));
                    lines.Add((new XYZ(x1, y1, 0), new XYZ(x0, y1, 0), y[1]));
                    lines.Add((new XYZ(x0, y1, 0), new XYZ(x0, y0, 0), x[0]));
                }
                else
                {
                    var on = _planes[s.On].Recipe;
                    double c = L(on.At), a = L(_planes[s.From].Recipe.At), b = L(_planes[s.To].Recipe.At);
                    lines.Add(on.Axis == "x" ? (new XYZ(c, a, 0), new XYZ(c, b, 0), s.On) : (new XYZ(a, c, 0), new XYZ(b, c, 0), s.On));
                }
                Category? sub = s.Subcategory != null ? Subcategory(s.Subcategory, s.Path + ".subcategory") : null;
                foreach (var (a, b, plane) in lines)
                {
                    try
                    {
                        var curve = _doc.FamilyCreate.NewSymbolicCurve(Line.CreateBound(a, b), sp);
                        curve.SetVisibility(new FamilyElementVisibility(FamilyElementVisibilityType.ViewSpecific) { IsShownInCoarse = s.Coarse, IsShownInMedium = s.Medium, IsShownInFine = s.Fine });
                        if (sub?.GetGraphicsStyle(GraphicsStyleType.Projection) is GraphicsStyle style) curve.Subcategory = style;
                        Symbols++;
                        var r = curve.GeometryCurve?.Reference;
                        if (r is null) { Warn(s.Path, IssueCodes.NotPrechecked, "a symbolic line gave no reference, so it is not locked to plane '" + plane + "'"); continue; }
                        _doc.FamilyCreate.NewAlignment(_plan!, _planes[plane].Reference, r);
                        Locks++;
                    }
                    catch (Exception e) { Error(s.Path, IssueCodes.InvalidValue, "Revit refused a symbolic line on plane '" + plane + "': " + e.Message); }
                }
            }
        }

        // ------------------------------------------------------------------ dimensions

        private void MakeDimensions()
        {
            double gap = L(Length(300)), step = L(Length(150));
            var extent = new Dictionary<string, (double lo, double hi)>();
            foreach (var g in _planes.Values.GroupBy(p => p.Recipe.Axis))
                extent[g.Key] = (g.Min(p => L(p.Recipe.At)), g.Max(p => L(p.Recipe.At)));
            (double lo, double hi) E(string axis) => extent.TryGetValue(axis, out var e) ? e : (0, 0);
            int n = 0;
            foreach (var d in _r.Dimensions)
            {
                var handles = d.Planes.Select(p => _planes.TryGetValue(p, out var h) ? h : null).ToList();
                if (handles.Any(h => h is null)) continue;
                string axis = handles[0]!.Recipe.Axis;
                var refs = new ReferenceArray();
                foreach (var h in handles) refs.Append(h!.Reference);
                double a = handles.Min(h => L(h!.Recipe.At)), b = handles.Max(h => L(h!.Recipe.At));
                double off = gap + step * n++;
                Line line;
                View view;
                if (axis == "x") { line = Line.CreateBound(new XYZ(a, E("y").lo - off, 0), new XYZ(b, E("y").lo - off, 0)); view = _plan!; }
                else if (axis == "y") { line = Line.CreateBound(new XYZ(E("x").lo - off, a, 0), new XYZ(E("x").lo - off, b, 0)); view = _plan!; }
                else { line = Line.CreateBound(new XYZ(E("x").hi + off, 0, a), new XYZ(E("x").hi + off, 0, b)); view = _front!; }
                try
                {
                    var dim = _doc.FamilyCreate.NewDimension(view, line, refs);
                    if (dim is null) { Error(d.Path, IssueCodes.InvalidValue, "Revit did not create the dimension between " + string.Join(", ", d.Planes)); continue; }
                    Dimensions++;
                    if (d.Equal) dim.AreSegmentsEqual = true;
                    if (d.Parameter != null && _params.TryGetValue(d.Parameter, out var fp)) dim.FamilyLabel = fp;
                }
                catch (Exception e)
                {
                    Error(d.Path, IssueCodes.InvalidValue, "Revit refused the dimension between " + string.Join(", ", d.Planes) + (d.Parameter != null ? " labelled '" + d.Parameter + "'" : "") + ": " + e.Message);
                }
            }
        }
    }
}
