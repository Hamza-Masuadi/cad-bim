using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.Core.Families
{
    /// <summary>
    /// The Revit side of placements.json: loading it for family.fromcad and family.place (fromPlacements), the extra
    /// rules those ops need on top of <see cref="Placements.Validate"/>, family and template names, and the maths that
    /// turns a block reference into a family instance's position, rotation and mirroring.
    /// </summary>
    public static class CadFamilies
    {
        public static readonly string[] Bodies = { "box", "none", "import3d" };
        public static readonly string[] Symbols = { "import", "lines" };

        /// <summary>Type parameter family.fromcad writes into each family: the placements.json block name it was made from.</summary>
        public const string BlockParameter = "CAD Block";
        /// <summary>Type parameter with the source drawing's file name.</summary>
        public const string SourceParameter = "CAD Source";

        /// <summary>
        /// Reads and checks placements.json. Every problem becomes an issue at <paramref name="fieldPath"/> (the job field
        /// that names the file) whose message starts with the path inside the file. Returns null when the file cannot be
        /// used; warnings (references outside model space) do not stop it.
        /// </summary>
        public static PlacementsFile? Load(string path, string fieldPath, List<Issue> issues)
        {
            if (!Path.IsPathRooted(path)) { issues.Add(new Issue(fieldPath, IssueCodes.InvalidValue, "give the full path of placements.json, not '" + path + "'")); return null; }
            if (!File.Exists(path)) { issues.Add(new Issue(fieldPath, IssueCodes.NotFound, path + " does not exist on this machine")); return null; }
            PlacementsFile f;
            try { f = Placements.ReadFile(path); }
            catch (Exception e) when (e is FormatException || e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                issues.Add(new Issue(fieldPath, Placements.InvalidCode, path + " cannot be read as " + PlacementsFile.FormatId + ": " + e.Message));
                return null;
            }
            int before = issues.Count;
            foreach (var i in Placements.Validate(f, Path.GetDirectoryName(path)))
                issues.Add(new Issue(fieldPath, i.Code, Path.GetFileName(path) + " " + i.Path + ": " + i.Message));
            foreach (var i in RevitRules(f))
                issues.Add(new Issue(fieldPath, i.Code, Path.GetFileName(path) + " " + i.Path + ": " + i.Message, i.Severity));
            return issues.Skip(before).Any(i => i.IsError) ? null : f;
        }

        /// <summary>Rules only the Revit side needs: a drawing unit Revit can import at a known scale.</summary>
        public static List<Issue> RevitRules(PlacementsFile f)
        {
            var issues = new List<Issue>();
            if (f.Source.LengthUnit is null)
                issues.Add(new Issue("$.source.units", IssueCodes.UnitsUnsupported, "drawing unit '" + f.Source.Units + "' (INSUNITS " +
                    f.Source.Insunits.ToString(CultureInfo.InvariantCulture) + ") has no exact Revit import unit; set the drawing to mm, cm, m, in or ft in AutoCAD and export again"));
            for (int i = 0; i < f.Blocks.Count; i++)
                for (int j = 0; j < f.Blocks[i].References.Count; j++)
                    if (f.Blocks[i].References[j].Space != "model")
                        issues.Add(new Issue("$.blocks[" + i.ToString(CultureInfo.InvariantCulture) + "].references[" + j.ToString(CultureInfo.InvariantCulture) + "].space",
                            IssueCodes.NotAllowed, "is '" + f.Blocks[i].References[j].Space + "'; only model space references are placed", IssueSeverity.Warning));
            return issues;
        }

        // ------------------------------------------------------------------ names

        private static readonly char[] RevitNameInvalid = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>The family name for a block: the override, else prefix + block name.</summary>
        public static string FamilyName(string blockName, string? prefix, string? overrideName) =>
            overrideName != null && overrideName.Trim().Length > 0 ? overrideName.Trim() : (prefix ?? "") + blockName;

        /// <summary>Why a family name cannot be used as a Revit family name and as an .rfa file name; null when it can.</summary>
        public static string? FamilyNameProblem(string name)
        {
            if (name.Trim().Length == 0) return "the family name is empty";
            if (name != name.Trim()) return "family name '" + name + "' starts or ends with a space";
            var bad = name.Where(c => Array.IndexOf(RevitNameInvalid, c) >= 0).Distinct().ToList();
            if (bad.Count > 0) return "family name '" + name + "' contains " + string.Join(" ", bad.Select(c => "'" + c + "'")) + ", which Revit does not allow in names";
            if (!Placements.IsSafeFileName(name + ".rfa")) return "family name '" + name + "' cannot be a Windows file name";
            return null;
        }

        /// <summary>
        /// Template file names to look for, best first, for a category whose names are given (display name first, then
        /// the English name): "Metric &lt;name&gt;.rft" before "&lt;name&gt;.rft" for a metric job, the other way round
        /// for an imperial one.
        /// </summary>
        public static List<string> TemplateNames(IEnumerable<string> categoryNames, bool metric)
        {
            var list = new List<string>();
            foreach (var n in categoryNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
            {
                var pair = metric ? new[] { "Metric " + n + ".rft", n + ".rft" } : new[] { n + ".rft", "Metric " + n + ".rft" };
                foreach (var p in pair) if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            }
            return list;
        }

        /// <summary>English template stems for the categories furniture blocks usually go to (display names are localised).</summary>
        public static string? EnglishTemplateStem(string builtInCategory)
        {
            switch (builtInCategory)
            {
                case "OST_GenericModel": return "Generic Model";
                case "OST_Furniture": return "Furniture";
                case "OST_FurnitureSystems": return "Furniture System";
                case "OST_Casework": return "Casework";
                case "OST_PlumbingFixtures": return "Plumbing Fixture";
                case "OST_SpecialityEquipment": return "Specialty Equipment";
                case "OST_LightingFixtures": return "Lighting Fixture";
                case "OST_ElectricalEquipment": return "Electrical Equipment";
                case "OST_ElectricalFixtures": return "Electrical Fixture";
                case "OST_MechanicalEquipment": return "Mechanical Equipment";
                case "OST_Planting": return "Planting";
                case "OST_Entourage": return "Entourage";
                case "OST_FoodServiceEquipment": return "Food Service Equipment";
                case "OST_MedicalEquipment": return "Medical Equipment";
                default: return null;
            }
        }
    }

    /// <summary>
    /// A plan transform from one 2D space to another: p' = Origin + L p, with L = [Xx Yx; Xy Yy] (columns are the images
    /// of the X and Y axes). Lengths in whatever unit the caller works in.
    /// </summary>
    public readonly struct PlanFrame
    {
        public PlanFrame(double ox, double oy, double xx, double xy, double yx, double yy)
        {
            OX = ox; OY = oy; XX = xx; XY = xy; YX = yx; YY = yy;
        }

        public double OX { get; }
        public double OY { get; }
        public double XX { get; }
        public double XY { get; }
        public double YX { get; }
        public double YY { get; }

        public double Determinant => XX * YY - YX * XY;

        /// <summary>Uniform scale of L (square root of |det|).</summary>
        public double Scale => Math.Sqrt(Math.Abs(Determinant));

        /// <summary>Rotate by <paramref name="degrees"/> about the origin, then translate.</summary>
        public static PlanFrame RotateThenTranslate(double tx, double ty, double degrees)
        {
            double a = Angle.ToRadians(degrees);
            return new PlanFrame(tx, ty, Math.Cos(a), Math.Sin(a), -Math.Sin(a), Math.Cos(a));
        }

        /// <summary>True when L is a rotation or reflection times a uniform scale (columns equal length and at right angles).</summary>
        public bool IsConformal(double tolerance = 1e-6)
        {
            double lx = Math.Sqrt(XX * XX + XY * XY), ly = Math.Sqrt(YX * YX + YY * YY);
            if (lx < 1e-12 || ly < 1e-12) return false;
            return Math.Abs(lx - ly) <= tolerance * Math.Max(lx, ly) && Math.Abs(XX * YX + XY * YY) <= tolerance * lx * ly;
        }
    }

    /// <summary>Where and how one block reference becomes a family instance, in the frame's output units.</summary>
    public sealed class PlannedInstance
    {
        public double X;
        public double Y;
        /// <summary>Rotation of the instance about the vertical, radians, [0, 2π), applied after the mirror.</summary>
        public double Rotation;
        /// <summary>The instance is mirrored: its local X axis is flipped before the rotation.</summary>
        public bool Mirrored;
        /// <summary>Expected images of the family's X and Y axes in the output space (unit vectors).</summary>
        public double BasisXx, BasisXy, BasisYx, BasisYy;

        public double RotationDegrees => Angle.ToDegrees(Rotation);
    }

    /// <summary>
    /// Block reference to family instance:
    /// <list type="number">
    /// <item>position: P' = O + L (a P), with P the reference's insertion point in drawing units (z ignored) and a the
    /// factor from drawing units to the frame's input units (<see cref="InputScale"/>);</item>
    /// <item>orientation: A = (L / s) R(θ) diag(sign sx, sign sy), with s the frame's scale, θ the reference rotation and
    /// sx, sy its scale factors (each must be ±1);</item>
    /// <item>A is a rotation when det A &gt; 0: the instance is rotated by φ = atan2(A21, A11); otherwise A = R(φ) F with
    /// F = diag(-1, 1), the instance is mirrored across its own Y axis and rotated by φ = atan2(-A21, -A11).</item>
    /// </list>
    /// </summary>
    public static class CadPlacement
    {
        /// <summary>
        /// The factor that takes drawing units into the frame's input space. A frame of scale 1 takes output units (the
        /// caller converts drawing units first: a = <paramref name="outputPerDrawingUnit"/>); a frame whose scale equals
        /// the unit factor already includes the conversion (a = 1). Any other scale is refused.
        /// </summary>
        public static bool InputScale(PlanFrame frame, double outputPerDrawingUnit, out double factor, out string? error)
        {
            factor = 0;
            error = null;
            if (!frame.IsConformal()) { error = "the transform is not a rotation or mirror with uniform scale (it shears or stretches)"; return false; }
            double s = frame.Scale;
            if (Math.Abs(s - 1) <= 1e-6) { factor = outputPerDrawingUnit; return true; }
            if (outputPerDrawingUnit > 0 && Math.Abs(s / outputPerDrawingUnit - 1) <= 1e-6) { factor = 1; return true; }
            error = "the transform scales by " + s.ToString("R", CultureInfo.InvariantCulture) + ", which is neither 1 nor the drawing's unit factor " +
                    outputPerDrawingUnit.ToString("R", CultureInfo.InvariantCulture) + "; the link was probably placed with a different unit or a custom scale";
            return false;
        }

        /// <summary>Plans one reference; null with a reason when it cannot be placed as an unscaled family instance.</summary>
        public static PlannedInstance? Plan(PlacementReference r, PlanFrame frame, double inputFactor, out string? refusal)
        {
            refusal = null;
            if (r.Space != "model") { refusal = "the reference is in layout '" + r.Space + "'; only model space references are placed"; return null; }
            if (r.Scale.Length != 3 || r.Scale.Take(2).Any(s => Math.Abs(Math.Abs(s) - 1) > 1e-6))
            {
                refusal = "the reference is scaled (" + string.Join(", ", r.Scale.Select(s => s.ToString("0.####", CultureInfo.InvariantCulture))) + "); families are placed at their own size";
                return null;
            }
            if (Math.Abs(r.Scale[2] - 1) > 1e-6)
            {
                refusal = "the reference's Z scale is " + r.Scale[2].ToString("0.####", CultureInfo.InvariantCulture) + "; only 1 is placed (a negative Z scale turns the block over, which a plan placement cannot show)";
                return null;
            }
            double s = frame.Scale;
            if (s < 1e-12) { refusal = "the transform collapses the plan"; return null; }
            double px = r.Position[0] * inputFactor, py = r.Position[1] * inputFactor;
            var pi = new PlannedInstance
            {
                X = frame.OX + frame.XX * px + frame.YX * py,
                Y = frame.OY + frame.XY * px + frame.YY * py,
            };
            double t = Angle.ToRadians(r.RotationDeg);
            double c = Math.Cos(t), si = Math.Sin(t);
            double gx = Math.Sign(r.Scale[0]), gy = Math.Sign(r.Scale[1]);
            double b11 = c * gx, b21 = si * gx, b12 = -si * gy, b22 = c * gy;
            double l11 = frame.XX / s, l21 = frame.XY / s, l12 = frame.YX / s, l22 = frame.YY / s;
            double a11 = l11 * b11 + l12 * b21, a21 = l21 * b11 + l22 * b21;
            double a12 = l11 * b12 + l12 * b22, a22 = l21 * b12 + l22 * b22;
            pi.Mirrored = a11 * a22 - a12 * a21 < 0;
            pi.Rotation = Normalise(pi.Mirrored ? Math.Atan2(-a21, -a11) : Math.Atan2(a21, a11));
            pi.BasisXx = a11; pi.BasisXy = a21; pi.BasisYx = a12; pi.BasisYy = a22;
            return pi;
        }

        private static double Normalise(double radians)
        {
            double r = radians % (2 * Math.PI);
            if (r < 0) r += 2 * Math.PI;
            if (r >= 2 * Math.PI - 1e-12) r = 0;
            return r;
        }
    }
}
