using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Json;
using SA.Revit.Host;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.FamilyCreation.Support
{
    /// <summary>One expected overall size: the solid geometry along an axis must equal a length parameter of the current type.</summary>
    internal sealed class FlexAxis
    {
        public string Axis = "x";
        public FamilyParameter Parameter = null!;
    }

    internal sealed class FlexOutcome
    {
        public string Type = "";
        public bool Ok = true;
        public List<string> Problems = new List<string>();
        public JsonObject Json = new JsonObject();
    }

    /// <summary>
    /// The flex test: every type is made current and the family regenerated in a transaction of its own, with every
    /// Revit failure collected. The type passes when Revit reports nothing and the bounding box of the solid (non-void)
    /// geometry matches each expected size. Sizes come from the edges of the regenerated solids, so voids that cut a
    /// solid are already applied.
    /// </summary>
    internal static class FlexTest
    {
        public static List<FlexOutcome> Run(Document famDoc, IReadOnlyList<string> typeNames, IReadOnlyList<FlexAxis> axes, double toleranceInternal, RevitUnits u)
        {
            var fm = famDoc.FamilyManager;
            var outcomes = new List<FlexOutcome>();
            foreach (var name in typeNames)
            {
                var o = new FlexOutcome { Type = name };
                outcomes.Add(o);
                var ft = fm.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == name);
                if (ft is null) { o.Ok = false; o.Problems.Add("type '" + name + "' is not in the family"); Finish(o); continue; }
                var collector = new FailureCollector();
                using (var t = new Transaction(famDoc, "SA flex " + name))
                {
                    if (t.Start() != TransactionStatus.Started) { o.Ok = false; o.Problems.Add("Revit refused to start a transaction"); Finish(o); continue; }
                    collector.Install(t);
                    try
                    {
                        fm.CurrentType = ft;
                        famDoc.Regenerate();
                        Measure(famDoc, ft, axes, toleranceInternal, u, o);
                    }
                    catch (Exception e)
                    {
                        o.Ok = false;
                        o.Problems.Add("regenerating type '" + name + "' failed: " + e.Message);
                    }
                    var status = o.Ok ? t.Commit() : RollBack(t);
                    foreach (var w in collector.Warnings) { o.Ok = false; o.Problems.Add("Revit warned: " + w.Text()); }
                    foreach (var er in collector.Errors) { o.Ok = false; o.Problems.Add("Revit error: " + er.Text()); }
                    if (o.Ok && status != TransactionStatus.Committed) { o.Ok = false; o.Problems.Add("Revit did not keep the regenerated type (" + status + ")"); }
                }
                Finish(o);
            }
            return outcomes;
        }

        private static TransactionStatus RollBack(Transaction t)
        {
            try { return t.RollBack(); }
            catch (Exception) { return TransactionStatus.Error; }
        }

        private static void Finish(FlexOutcome o)
        {
            o.Json.Set("type", o.Type).Set("ok", o.Ok);
            if (o.Problems.Count > 0) o.Json.Set("problems", JsonArray.FromStrings(o.Problems));
        }

        private static void Measure(Document famDoc, FamilyType ft, IReadOnlyList<FlexAxis> axes, double tol, RevitUnits u, FlexOutcome o)
        {
            var box = SolidBounds(famDoc);
            if (box is null) { o.Ok = false; o.Problems.Add("the type has no solid geometry"); return; }
            var size = box.Max - box.Min;
            o.Json.Set("size", new JsonObject().Add("x", u.LengthJson(size.X)).Add("y", u.LengthJson(size.Y)).Add("z", u.LengthJson(size.Z)));
            var checks = new JsonObject();
            foreach (var a in axes)
            {
                double? expected = ft.AsDouble(a.Parameter);
                double actual = a.Axis == "x" ? size.X : a.Axis == "y" ? size.Y : size.Z;
                var c = new JsonObject().Add("parameter", a.Parameter.Definition?.Name ?? "").Add("measured", u.LengthJson(actual));
                if (expected is null) { o.Ok = false; o.Problems.Add("'" + (a.Parameter.Definition?.Name ?? "") + "' has no value in this type"); c.Add("expected", JsonValue.Null); }
                else
                {
                    c.Add("expected", u.LengthJson(expected.Value));
                    bool ok = Math.Abs(actual - expected.Value) <= tol;
                    c.Add("ok", ok);
                    if (!ok)
                    {
                        o.Ok = false;
                        o.Problems.Add("size along " + a.Axis + " is " + RevitUnits.Num(u.LengthFromInternal(actual)) + " but '" + (a.Parameter.Definition?.Name ?? "") + "' is " +
                            RevitUnits.Num(u.LengthFromInternal(expected.Value)) + ": the geometry does not follow the parameter");
                    }
                }
                checks.Add(a.Axis, c);
            }
            o.Json.Set("checks", checks);
        }

        /// <summary>The bounding box of every solid form's regenerated solids, from their edges. Null when there is none.</summary>
        public static BoundingBoxXYZ? SolidBounds(Document famDoc)
        {
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            foreach (var form in new FilteredElementCollector(famDoc).OfClass(typeof(GenericForm)).Cast<GenericForm>().Where(f => f.IsSolid))
            {
                GeometryElement? geo;
                try { geo = form.get_Geometry(opt); }
                catch (Exception) { continue; }
                if (geo is null) continue;
                foreach (var solid in Solids(geo))
                    foreach (Edge e in solid.Edges)
                        foreach (var p in e.Tessellate())
                        {
                            any = true;
                            lo[0] = Math.Min(lo[0], p.X); lo[1] = Math.Min(lo[1], p.Y); lo[2] = Math.Min(lo[2], p.Z);
                            hi[0] = Math.Max(hi[0], p.X); hi[1] = Math.Max(hi[1], p.Y); hi[2] = Math.Max(hi[2], p.Z);
                        }
            }
            if (!any) return null;
            return new BoundingBoxXYZ { Min = new XYZ(lo[0], lo[1], lo[2]), Max = new XYZ(hi[0], hi[1], hi[2]) };
        }

        private static IEnumerable<Solid> Solids(GeometryElement geo)
        {
            foreach (var g in geo)
            {
                if (g is Solid s && s.Volume > 0) yield return s;
                else if (g is GeometryInstance gi)
                {
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) foreach (var x in Solids(inner)) yield return x;
                }
            }
        }

        /// <summary>The plan extent of the symbolic lines, for a warning when they do not follow the forms.</summary>
        public static BoundingBoxXYZ? SymbolBounds(Document famDoc)
        {
            var curves = new FilteredElementCollector(famDoc).OfClass(typeof(CurveElement)).OfType<SymbolicCurve>().Select(c => c.GeometryCurve).Where(c => c != null).ToList();
            if (curves.Count == 0) return null;
            var pts = curves.SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }).ToList();
            return new BoundingBoxXYZ { Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), 0), Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), 0) };
        }
    }
}
