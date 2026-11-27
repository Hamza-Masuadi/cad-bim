using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Geometry;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.Core.Families
{
    public sealed class RecipeParameter
    {
        public string Name = "";
        public string Spec = "";
        public bool Instance;
        public string? Group;
        public string? Formula;
        public JsonValue? Value;
        public string Path = "";
    }

    public sealed class RecipeType
    {
        public string Name = "";
        public string Path = "";
        /// <summary>Every value this type gets: parameters[].value first, overridden by the type's own values.</summary>
        public List<KeyValuePair<string, JsonValue>> Values = new List<KeyValuePair<string, JsonValue>>();
        public Dictionary<string, string> ValuePaths = new Dictionary<string, string>(StringComparer.Ordinal);

        public JsonValue? ValueOf(string parameter)
        {
            foreach (var kv in Values) if (kv.Key == parameter) return kv.Value;
            return null;
        }
    }

    public sealed class RecipePlane
    {
        public string Name = "";
        /// <summary>"x", "y" or "z": the axis the plane is perpendicular to.</summary>
        public string Axis = "";
        /// <summary>Starting position along the axis, job units.</summary>
        public double At;
        public bool Pinned;
        /// <summary>One of the template's origin planes (never moved).</summary>
        public bool Reserved;
        public string Path = "";
        public bool Fixed => Pinned || Reserved;
    }

    public sealed class RecipeDimension
    {
        public List<string> Planes = new List<string>();
        public string? Parameter;
        public bool Equal;
        public string Path = "";
    }

    public sealed class RecipeLoop
    {
        public List<Point2> Points = new List<Point2>();
        public Point2? Centre;
        public double Radius;
        public string Path = "";
        public bool IsCircle => Centre.HasValue;

        /// <summary>The loop as a polygon; a circle as a 48-sided one (for bounds and crossing tests only).</summary>
        public List<Point2> Outline()
        {
            if (!IsCircle) return Points;
            var c = Centre!.Value;
            var list = new List<Point2>();
            for (int i = 0; i < 48; i++)
            {
                double a = 2 * Math.PI * i / 48;
                list.Add(new Point2(c.X + Radius * Math.Cos(a), c.Y + Radius * Math.Sin(a)));
            }
            return list;
        }
    }

    public sealed class RecipeForm
    {
        public string? Id;
        public int Index;
        public string Kind = "";
        public bool Void;
        public string[]? BoxX, BoxY, BoxZ;
        public string Sketch = "plan";
        public double Offset;
        public List<RecipeLoop> Profile = new List<RecipeLoop>();
        public List<Point2> TopProfile = new List<Point2>();
        public double Start;
        public double? End;
        public Point2[]? Axis;
        public double StartAngle;
        public double EndAngle = 360;
        public List<Point2> PathPoints = new List<Point2>();
        public int ProfileAt;
        public List<KeyValuePair<string, string>> Lock = new List<KeyValuePair<string, string>>();
        public string? Material;
        public string? Subcategory;
        public string? VisibleParameter;
        public bool Plan = true, FrontBack = true, LeftRight = true, Coarse = true, Medium = true, Fine = true;
        public string Path = "";
        public bool IsBox => BoxX != null;
        public string Label => Id ?? (Kind + " " + Index.ToString(CultureInfo.InvariantCulture));
    }

    public sealed class RecipeSymbol
    {
        public bool IsRect;
        public string[]? X, Y;
        public string On = "", From = "", To = "";
        public string? Subcategory;
        public bool Coarse = true, Medium = true, Fine = true;
        public string Path = "";
    }

    public sealed class RecipeFlex
    {
        public string? X, Y, Z;
        public double? Tolerance;
        public string Path = "";

        public IEnumerable<KeyValuePair<string, string>> Axes()
        {
            if (X != null) yield return new KeyValuePair<string, string>("x", X);
            if (Y != null) yield return new KeyValuePair<string, string>("y", Y);
            if (Z != null) yield return new KeyValuePair<string, string>("z", Z);
        }
    }

    /// <summary>
    /// The args of family.create read into a model (lengths in job units), with every rule the schema cannot express:
    /// names, references between parameters, formulas, planes, dimensions, forms and types. Lint runs it through
    /// <see cref="Check"/>; the Revit handler builds from the same model, so what lint accepted is what is built.
    /// </summary>
    public sealed class FamilyRecipe
    {
        public const string CenterLeftRight = "Center (Left/Right)";
        public const string CenterFrontBack = "Center (Front/Back)";
        public const string RefLevel = "Ref. Level";

        public string Name = "";
        public string? Category;
        public string? Template;
        public string? OutputDir;
        public bool Overwrite;
        public string ParameterValues = "family";
        public bool Load = true;
        public List<RecipeParameter> Parameters = new List<RecipeParameter>();
        public List<RecipeType> Types = new List<RecipeType>();
        /// <summary>The three reserved planes first, then the job's planes.</summary>
        public List<RecipePlane> Planes = new List<RecipePlane>();
        public List<RecipeDimension> Dimensions = new List<RecipeDimension>();
        public List<RecipeForm> Forms = new List<RecipeForm>();
        public List<RecipeSymbol> Symbols = new List<RecipeSymbol>();
        public RecipeFlex? Flex;
        public LengthUnit Unit;

        public RecipePlane? Plane(string name) => Planes.FirstOrDefault(p => p.Name == name);
        public RecipeParameter? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);

        private static readonly char[] RevitProhibited = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' };
        private static readonly char[] FileProhibited = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        private static readonly Regex DeviceName = new Regex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AbsoluteWindowsPath = new Regex(@"^([A-Za-z]:[\\/]|\\\\[^\\]+\\)", RegexOptions.CultureInvariant);

        /// <summary>Lint entry point: every cross-field problem of family.create args.</summary>
        public static List<Issue> Check(JsonObject args, LengthUnit unit, string path)
        {
            var issues = new List<Issue>();
            Parse(args, unit, path, issues);
            return issues;
        }

        /// <summary>Reads and checks the args. Schema errors are assumed reported already; this adds the cross-field ones.</summary>
        public static FamilyRecipe Parse(JsonObject args, LengthUnit unit, string path, List<Issue> issues)
        {
            var r = new FamilyRecipe { Unit = unit };
            var c = new Checker(r, unit, path, issues);
            c.Run(args);
            return r;
        }

        // ------------------------------------------------------------------ checking

        private sealed class Checker
        {
            private readonly FamilyRecipe _r;
            private readonly LengthUnit _unit;
            private readonly string _path;
            private readonly List<Issue> _issues;
            private readonly double _tol;

            public Checker(FamilyRecipe r, LengthUnit unit, string path, List<Issue> issues)
            {
                _r = r; _unit = unit; _path = path; _issues = issues;
                _tol = Tolerance.DefaultLength(unit);
            }

            private void Error(string path, string code, string message) => _issues.Add(new Issue(path, code, message));
            private void Warn(string path, string code, string message) => _issues.Add(new Issue(path, code, message, IssueSeverity.Warning));
            private string P(string field) => JsonPath.Key(_path, field);

            public void Run(JsonObject a)
            {
                _r.Name = Str(a, "name") ?? "";
                _r.Category = Str(a, "category");
                _r.Template = Str(a, "template");
                _r.OutputDir = Str(a, "outputDir");
                _r.Overwrite = Bool(a, "overwrite", false);
                _r.ParameterValues = Str(a, "parameterValues") ?? "family";
                _r.Load = Bool(a, "load", true);
                CheckFamilyName();
                CheckPaths();
                ReadParameters(a);
                ReadPlanes(a);
                ReadTypes(a);
                CheckFormulas();
                ReadDimensions(a);
                ReadForms(a);
                ReadSymbols(a);
                ReadFlex(a);
                CheckAnchoring();
                CheckDrivingValues();
            }

            private void CheckFamilyName()
            {
                string at = P("name");
                string n = _r.Name;
                if (n.Trim().Length == 0) return;
                if (n != n.Trim()) Error(at, IssueCodes.InvalidValue, "the family name has leading or trailing spaces");
                int bad = n.IndexOfAny(FileProhibited.Concat(RevitProhibited).ToArray());
                if (bad >= 0) Error(at, IssueCodes.InvalidValue, "the family name contains '" + n[bad] + "', which a file name or Revit does not allow");
                if (DeviceName.IsMatch(n)) Error(at, IssueCodes.InvalidValue, "'" + n + "' is a Windows device name and cannot be a file name");
                if (n.EndsWith(".", StringComparison.Ordinal)) Error(at, IssueCodes.InvalidValue, "a file name cannot end with '.'");
                if (n.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) Error(at, IssueCodes.InvalidValue, "give the name without .rfa");
                if (n.Length > 120) Error(at, IssueCodes.InvalidValue, "the family name is longer than 120 characters");
            }

            private void CheckPaths()
            {
                if (_r.Template != null)
                {
                    if (!AbsoluteWindowsPath.IsMatch(_r.Template)) Error(P("template"), IssueCodes.InvalidValue, "template must be an absolute Windows path (C:\\... or \\\\server\\...)");
                    if (!_r.Template.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)) Error(P("template"), IssueCodes.InvalidValue, "a family template file ends in .rft");
                }
                if (_r.OutputDir != null && !AbsoluteWindowsPath.IsMatch(_r.OutputDir))
                    Error(P("outputDir"), IssueCodes.InvalidValue, "outputDir must be an absolute Windows path");
                if (!_r.Overwrite && _r.ParameterValues == "project")
                    Warn(P("parameterValues"), IssueCodes.Constraint, "parameterValues matters only when overwrite reloads a family the project already has");
            }

            // ---------------------------------------------------------- parameters

            private void ReadParameters(JsonObject a)
            {
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (o, at) in Items(a, "parameters", P("parameters")))
                {
                    var p = new RecipeParameter
                    {
                        Name = Str(o, "name") ?? "", Spec = Str(o, "spec") ?? "text", Instance = Bool(o, "instance", false),
                        Group = Str(o, "group"), Formula = Str(o, "formula"), Value = Present(o, "value"), Path = at,
                    };
                    if (p.Formula != null && p.Formula.Trim().Length == 0) p.Formula = null;
                    _r.Parameters.Add(p);
                    if (p.Name.Trim().Length == 0) continue;
                    string np = at + ".name";
                    if (p.Name != p.Name.Trim()) Error(np, IssueCodes.InvalidValue, "parameter '" + p.Name + "' has leading or trailing spaces");
                    int bad = p.Name.IndexOfAny(RevitProhibited);
                    if (bad >= 0) Error(np, IssueCodes.InvalidValue, "parameter '" + p.Name + "' contains '" + p.Name[bad] + "', which Revit does not allow in names");
                    else if (p.Name.IndexOfAny(new[] { '+', '-', '*', '/', '^', '=', '(', ')', '"', '\'' }) >= 0)
                        Warn(np, IssueCodes.InvalidValue, "parameter '" + p.Name + "' contains a formula operator; Revit formulas cannot name it reliably");
                    if (seen.TryGetValue(p.Name, out var first))
                        Error(np, IssueCodes.DuplicateId, first == p.Name ? "parameter '" + p.Name + "' is declared twice" : "parameters '" + first + "' and '" + p.Name + "' differ only by case");
                    else seen[p.Name] = p.Name;
                    if (p.Formula != null && p.Value != null)
                        Error(at + ".value", IssueCodes.Constraint, "'" + p.Name + "' has a formula, so it takes no value");
                    if (p.Value != null && CheckValue(p, p.Value) is string err) Error(at + ".value", IssueCodes.Type, "'" + p.Name + "': " + err);
                    if (p.Instance && p.Spec == "material" && p.Value != null)
                        Warn(at + ".instance", IssueCodes.Constraint, "an instance material parameter's value is only the default each new instance starts with");
                }
            }

            /// <summary>Null when the value suits the parameter's kind, else the problem.</summary>
            private string? CheckValue(RecipeParameter p, JsonValue v)
            {
                if (v is JsonString rs && Jobs.Refs.IsRefText(rs.Value)) return "a reference to another op cannot be a family parameter value";
                switch (p.Spec)
                {
                    case "length":
                        if (v is JsonNumber) return null;
                        if (v is JsonString ls && Length.TryParse(ls.Value, _unit, out _, out string le)) return null;
                        return "expected a length (a number in job units, or a string such as \"0.9m\")";
                    case "angle":
                        if (v is JsonNumber || (v is JsonString s && Angle.TryParseDegrees(s.Value, out _))) return null;
                        return "expected an angle in degrees";
                    case "area": case "volume": case "slope": case "number": case "currency":
                        return v is JsonNumber ? null : "expected a number";
                    case "integer":
                        return v is JsonNumber n && n.TryGetInt64(out _) ? null : "expected an integer";
                    case "yesNo":
                        if (v is JsonBool) return null;
                        return v is JsonNumber b && b.TryGetInt64(out long bl) && (bl == 0 || bl == 1) ? null : "expected true or false";
                    case "material":
                        if (v.IsNull) return null;
                        return v is JsonString m && m.Value.Trim().Length > 0 ? null : "expected the name of a material";
                    default:
                        return v is JsonString || (v is JsonNumber tn && tn.IsIntegerLiteral) || v.IsNull ? null : "expected text";
                }
            }

            private static bool IsReservedPlaneName(string name) =>
                string.Equals(name, CenterLeftRight, StringComparison.OrdinalIgnoreCase) || string.Equals(name, CenterFrontBack, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, RefLevel, StringComparison.OrdinalIgnoreCase);

            private RecipeParameter? Param(string? name, string at, string what, params string[] specs)
            {
                if (name is null) return null;
                var p = _r.Parameter(name);
                if (p is null)
                {
                    Error(at, IssueCodes.NotFound, what + " '" + name + "' is not declared in parameters" + JobValidator.Suggest(name, _r.Parameters.Select(x => x.Name)));
                    return null;
                }
                if (specs.Length > 0 && Array.IndexOf(specs, p.Spec) < 0)
                {
                    Error(at, IssueCodes.Type, what + " '" + name + "' must be a " + string.Join(" or ", specs) + " parameter; it is " + p.Spec);
                    return null;
                }
                return p;
            }

            // ---------------------------------------------------------- types

            private void ReadTypes(JsonObject a)
            {
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (o, at) in Items(a, "types", P("types")))
                {
                    var t = new RecipeType { Name = Str(o, "name") ?? "", Path = at };
                    foreach (var p in _r.Parameters.Where(p => p.Value != null))
                    {
                        t.Values.Add(new KeyValuePair<string, JsonValue>(p.Name, p.Value!));
                        t.ValuePaths[p.Name] = p.Path + ".value";
                    }
                    _r.Types.Add(t);
                    string nat = at + ".name";
                    if (t.Name.Trim().Length > 0)
                    {
                        if (t.Name != t.Name.Trim()) Error(nat, IssueCodes.InvalidValue, "type '" + t.Name + "' has leading or trailing spaces");
                        int bad = t.Name.IndexOfAny(RevitProhibited);
                        if (bad >= 0) Error(nat, IssueCodes.InvalidValue, "type '" + t.Name + "' contains '" + t.Name[bad] + "', which Revit does not allow in names");
                        if (seen.TryGetValue(t.Name, out var first))
                            Error(nat, IssueCodes.DuplicateId, first == t.Name ? "type '" + t.Name + "' is listed twice" : "types '" + first + "' and '" + t.Name + "' differ only by case");
                        else seen[t.Name] = t.Name;
                    }
                    if (!(Present(o, "values") is JsonObject values)) continue;
                    foreach (var kv in values)
                    {
                        string vat = JsonPath.Key(at + ".values", kv.Key);
                        var p = Param(kv.Key, vat, "parameter");
                        if (p is null) continue;
                        if (p.Formula != null) { Error(vat, IssueCodes.Constraint, "'" + p.Name + "' is set by its formula (" + p.Formula + "), so a type cannot give it a value"); continue; }
                        if (kv.Value.IsNull && p.Spec != "material" && p.Spec != "text") { Error(vat, IssueCodes.Type, "'" + p.Name + "' needs a value, not null"); continue; }
                        if (CheckValue(p, kv.Value) is string err) { Error(vat, IssueCodes.Type, "'" + p.Name + "': " + err); continue; }
                        t.Values.RemoveAll(x => x.Key == kv.Key);
                        t.Values.Add(new KeyValuePair<string, JsonValue>(kv.Key, kv.Value));
                        t.ValuePaths[kv.Key] = vat;
                    }
                }
            }

            // ---------------------------------------------------------- formulas

            private void CheckFormulas()
            {
                var deps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var names = _r.Parameters.Where(p => p.Name.Length > 0).Select(p => p.Name).Distinct().ToList();
                foreach (var p in _r.Parameters.Where(p => p.Formula != null && p.Name.Length > 0))
                {
                    string at = p.Path + ".formula";
                    var used = FamilyFormulas.Analyse(p.Formula!, names, out var unknown, out string? syntax);
                    if (syntax != null) Error(at, IssueCodes.InvalidValue, "formula of '" + p.Name + "': " + syntax);
                    foreach (var u in unknown)
                        Error(at, IssueCodes.NotFound, "formula of '" + p.Name + "' names '" + u + "', which is not a declared parameter or a formula function or unit" +
                            JobValidator.Suggest(u, names));
                    deps[p.Name] = used;
                    if (!p.Instance)
                        foreach (var d in used)
                            if (_r.Parameter(d) is RecipeParameter dp && dp.Instance)
                                Error(at, IssueCodes.Constraint, "type parameter '" + p.Name + "' has a formula using instance parameter '" + d + "'; Revit refuses that (make '" + p.Name + "' an instance parameter)");
                }
                var state = new Dictionary<string, int>(StringComparer.Ordinal);
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var start in deps.Keys)
                {
                    var stack = new List<string>();
                    if (Cycle(start, deps, state, stack) && stack.Count > 0 && reported.Add(string.Join(">", stack.OrderBy(x => x, StringComparer.Ordinal))))
                        Error(_r.Parameter(start)!.Path + ".formula", IssueCodes.Constraint, "formulas refer to each other in a circle: " + string.Join(" -> ", stack));
                }
            }

            private static bool Cycle(string n, Dictionary<string, List<string>> deps, Dictionary<string, int> state, List<string> stack)
            {
                if (state.TryGetValue(n, out int s))
                {
                    if (s == 1) { int i = stack.IndexOf(n); var loop = stack.Skip(i).ToList(); loop.Add(n); stack.Clear(); stack.AddRange(loop); return true; }
                    return false;
                }
                state[n] = 1;
                stack.Add(n);
                if (deps.TryGetValue(n, out var next))
                    foreach (var d in next)
                        if (Cycle(d, deps, state, stack)) return true;
                stack.RemoveAt(stack.Count - 1);
                state[n] = 2;
                return false;
            }

            // ---------------------------------------------------------- planes and dimensions

            private void ReadPlanes(JsonObject a)
            {
                _r.Planes.Add(new RecipePlane { Name = CenterLeftRight, Axis = "x", Reserved = true, Path = P("planes") });
                _r.Planes.Add(new RecipePlane { Name = CenterFrontBack, Axis = "y", Reserved = true, Path = P("planes") });
                _r.Planes.Add(new RecipePlane { Name = RefLevel, Axis = "z", Reserved = true, Path = P("planes") });
                var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (o, at) in Items(a, "planes", P("planes")))
                {
                    var pl = new RecipePlane { Name = Str(o, "name") ?? "", Axis = Str(o, "axis") ?? "x", At = Len(o, "at") ?? 0, Pinned = Bool(o, "pinned", false), Path = at };
                    string nat = at + ".name";
                    if (pl.Name.Trim().Length == 0) continue;
                    if (IsReservedPlaneName(pl.Name)) { Error(nat, IssueCodes.AlreadyExists, "'" + pl.Name + "' is a reserved plane (the template's own); refer to it without declaring it"); continue; }
                    if (pl.Name != pl.Name.Trim()) Error(nat, IssueCodes.InvalidValue, "plane '" + pl.Name + "' has leading or trailing spaces");
                    int bad = pl.Name.IndexOfAny(RevitProhibited);
                    if (bad >= 0) Error(nat, IssueCodes.InvalidValue, "plane '" + pl.Name + "' contains '" + pl.Name[bad] + "', which Revit does not allow in names");
                    if (seen.TryGetValue(pl.Name, out var first)) { Error(nat, IssueCodes.DuplicateId, "plane '" + pl.Name + "' is declared twice" + (first != pl.Name ? " (names differ only by case)" : "")); continue; }
                    seen[pl.Name] = pl.Name;
                    _r.Planes.Add(pl);
                }
                foreach (var g in _r.Planes.Where(p => !p.Reserved).GroupBy(p => p.Axis))
                {
                    var list = g.ToList();
                    var all = _r.Planes.Where(p => p.Axis == g.Key).ToList();
                    foreach (var pl in list)
                        foreach (var other in all)
                            if (!ReferenceEquals(pl, other) && Math.Abs(pl.At - other.At) <= _tol && (other.Reserved || all.IndexOf(other) < all.IndexOf(pl)))
                                Warn(pl.Path + ".at", IssueCodes.Constraint, "plane '" + pl.Name + "' starts at the same position as '" + other.Name + "'; a dimension between them would have zero length");
                }
            }

            private RecipePlane? PlaneRef(string? name, string at, string? axis, string what)
            {
                if (name is null) return null;
                var pl = _r.Plane(name);
                if (pl is null)
                {
                    Error(at, IssueCodes.NotFound, what + " '" + name + "' is not a declared or reserved plane" + JobValidator.Suggest(name, _r.Planes.Select(p => p.Name)));
                    return null;
                }
                if (axis != null && pl.Axis != axis)
                {
                    Error(at, IssueCodes.Constraint, what + " '" + name + "' is perpendicular to " + pl.Axis + "; this needs a plane perpendicular to " + axis);
                    return null;
                }
                return pl;
            }

            private void ReadDimensions(JsonObject a)
            {
                var labelled = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (o, at) in Items(a, "dimensions", P("dimensions")))
                {
                    var d = new RecipeDimension { Planes = Strings(o, "planes"), Parameter = Str(o, "parameter"), Equal = Bool(o, "equal", false), Path = at };
                    _r.Dimensions.Add(d);
                    var planes = new List<RecipePlane>();
                    for (int i = 0; i < d.Planes.Count; i++)
                    {
                        var pl = PlaneRef(d.Planes[i], JsonPath.Index(at + ".planes", i), null, "plane");
                        if (pl != null) planes.Add(pl);
                    }
                    if (planes.Count != d.Planes.Count) continue;
                    if (planes.Select(p => p.Axis).Distinct().Count() > 1) { Error(at + ".planes", IssueCodes.Constraint, "a dimension's planes must all be perpendicular to one axis (found " + string.Join(", ", planes.Select(p => p.Name + ": " + p.Axis)) + ")"); continue; }
                    if (d.Planes.Distinct(StringComparer.Ordinal).Count() != d.Planes.Count) { Error(at + ".planes", IssueCodes.Constraint, "a plane is listed twice in one dimension"); continue; }
                    if (d.Planes.Count == 2)
                    {
                        if (d.Equal) Error(at + ".equal", IssueCodes.Constraint, "equal needs three or more planes");
                        if (d.Parameter is null) { Error(at, IssueCodes.Required, "a two-plane dimension needs the length parameter that drives it"); continue; }
                        Param(d.Parameter, at + ".parameter", "dimension label", "length");
                        if (planes.All(p => p.Fixed))
                            Error(at, IssueCodes.Constraint, "both planes are fixed (reserved or pinned), so the parameter cannot move either; unpin one");
                        string key = string.Join("|", d.Planes.OrderBy(x => x, StringComparer.Ordinal));
                        if (!labelled.Add(key)) Error(at, IssueCodes.Constraint, "planes " + string.Join(" and ", d.Planes) + " already have a labelled dimension; a second one over-constrains them");
                    }
                    else
                    {
                        if (!d.Equal) Error(at + ".equal", IssueCodes.Constraint, "a dimension over three or more planes is an equal-spacing constraint; set equal true (label two planes instead to drive a distance)");
                        if (d.Parameter != null) Error(at + ".parameter", IssueCodes.Constraint, "only a two-plane dimension takes a parameter");
                        if (planes.Count(p => p.Fixed) > 1) Error(at, IssueCodes.Constraint, "more than one of these planes is fixed (reserved or pinned), so equal spacing cannot move them");
                    }
                }
            }

            /// <summary>Planes not tied by dimensions to a reserved or pinned plane can drift when Revit regenerates.</summary>
            private void CheckAnchoring()
            {
                var anchored = new HashSet<string>(_r.Planes.Where(p => p.Fixed).Select(p => p.Name), StringComparer.Ordinal);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var d in _r.Dimensions)
                        if (d.Planes.Any(anchored.Contains))
                            foreach (var n in d.Planes) grew |= anchored.Add(n);
                }
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in _r.Forms)
                {
                    foreach (var n in (f.BoxX ?? new string[0]).Concat(f.BoxY ?? new string[0]).Concat(f.BoxZ ?? new string[0])) used.Add(n);
                    foreach (var kv in f.Lock) used.Add(kv.Value);
                }
                foreach (var s in _r.Symbols)
                {
                    foreach (var n in (s.X ?? new string[0]).Concat(s.Y ?? new string[0])) used.Add(n);
                    if (!s.IsRect) { used.Add(s.On); used.Add(s.From); used.Add(s.To); }
                }
                foreach (var pl in _r.Planes.Where(p => !p.Reserved && !anchored.Contains(p.Name)))
                    Warn(pl.Path, IssueCodes.Constraint, "plane '" + pl.Name + "' is not tied by dimensions to a reserved or pinned plane; it" +
                        (used.Contains(pl.Name) ? " and the geometry locked to it" : "") + " can drift when Revit regenerates (add a labelled dimension from a fixed plane, or pin it)");
            }

            /// <summary>Every parameter that drives a dimension or is a flex size needs a positive value in every type.</summary>
            private void CheckDrivingValues()
            {
                var driving = _r.Dimensions.Where(d => d.Parameter != null).Select(d => d.Parameter!)
                    .Concat(_r.Flex?.Axes().Select(kv => kv.Value) ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
                foreach (var name in driving)
                {
                    var p = _r.Parameter(name);
                    if (p is null || p.Spec != "length" || p.Formula != null) continue;
                    foreach (var t in _r.Types.Where(t => t.Name.Length > 0))
                    {
                        var v = t.ValueOf(name);
                        if (v is null) { Error(t.Path, IssueCodes.Required, "type '" + t.Name + "' gives no value for '" + name + "', which sizes the geometry (give it in the type or in parameters[].value)"); continue; }
                        double? len = v is JsonNumber n ? n.Value : v is JsonString s && Length.TryParse(s.Value, _unit, out double d, out _) ? d : (double?)null;
                        if (len.HasValue && len.Value <= 0)
                            Error(t.ValuePaths.TryGetValue(name, out var vp) ? vp : t.Path, IssueCodes.Range, "type '" + t.Name + "': '" + name + "' must be greater than 0 (it sizes the geometry)");
                    }
                }
            }

            // ---------------------------------------------------------- forms

            private static readonly string[] Common = { "id", "kind", "void", "material", "subcategory", "visibleParameter", "visibility", "lock" };
            private static readonly Dictionary<string, string[]> KindFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["extrusion"] = new[] { "box", "sketch", "offset", "profile", "start", "end" },
                ["blend"] = new[] { "sketch", "offset", "profile", "topProfile", "start", "end" },
                ["revolution"] = new[] { "sketch", "offset", "profile", "axis", "startAngle", "endAngle" },
                ["sweep"] = new[] { "sketch", "offset", "profile", "path", "profileAt" },
                ["sweptBlend"] = new[] { "sketch", "offset", "profile", "topProfile", "path" },
            };

            private void ReadForms(JsonObject a)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                int index = 0;
                foreach (var (o, at) in Items(a, "forms", P("forms")))
                {
                    var f = new RecipeForm
                    {
                        Index = index++, Id = Str(o, "id"), Kind = Str(o, "kind") ?? "extrusion", Void = Bool(o, "void", false), Sketch = Str(o, "sketch") ?? "plan",
                        Offset = Len(o, "offset") ?? 0, Start = Len(o, "start") ?? 0, End = Len(o, "end"), StartAngle = Ang(o, "startAngle") ?? 0, EndAngle = Ang(o, "endAngle") ?? 360,
                        ProfileAt = Present(o, "profileAt") is JsonNumber pa && pa.TryGetInt64(out long pal) ? (int)pal : 0,
                        Material = Str(o, "material"), Subcategory = Str(o, "subcategory"), VisibleParameter = Str(o, "visibleParameter"), Path = at,
                    };
                    _r.Forms.Add(f);
                    if (f.Id != null && !ids.Add(f.Id)) Error(at + ".id", IssueCodes.DuplicateId, "form id '" + f.Id + "' is used twice");
                    if (!KindFields.TryGetValue(f.Kind, out var allowed)) continue;
                    foreach (var k in o.Keys)
                        if (Present(o, k) != null && Array.IndexOf(Common, k) < 0 && Array.IndexOf(allowed, k) < 0)
                            Error(JsonPath.Key(at, k), IssueCodes.Constraint, "'" + k + "' is not used by a " + f.Kind);

                    foreach (var (lo, lat) in Items(o, "profile", at + ".profile"))
                        f.Profile.Add(ReadLoop(lo, lat));
                    f.TopProfile = Points(o, "topProfile");
                    f.PathPoints = Points(o, "path");
                    var axis = Points(o, "axis");
                    if (axis.Count == 2) f.Axis = axis.ToArray();
                    if (Present(o, "lock") is JsonObject lk)
                        foreach (var kv in lk)
                            if (kv.Value is JsonString ls) f.Lock.Add(new KeyValuePair<string, string>(kv.Key, ls.Value));
                    if (Present(o, "visibility") is JsonObject vis)
                    {
                        f.Plan = Bool(vis, "plan", true); f.FrontBack = Bool(vis, "frontBack", true); f.LeftRight = Bool(vis, "leftRight", true);
                        f.Coarse = Bool(vis, "coarse", true); f.Medium = Bool(vis, "medium", true); f.Fine = Bool(vis, "fine", true);
                        if (!f.Coarse && !f.Medium && !f.Fine) Error(at + ".visibility", IssueCodes.Constraint, "the form must show at one detail level at least (coarse, medium or fine)");
                        if (!f.Plan && !f.FrontBack && !f.LeftRight) Warn(at + ".visibility", IssueCodes.Constraint, "the form shows in no view direction (only 3D views)");
                    }
                    if (Present(o, "box") is JsonObject box)
                    {
                        f.BoxX = Strings(box, "x").ToArray(); f.BoxY = Strings(box, "y").ToArray(); f.BoxZ = Strings(box, "z").ToArray();
                    }
                    CheckForm(f, o, at);
                }
            }

            private RecipeLoop ReadLoop(JsonObject o, string at)
            {
                var loop = new RecipeLoop { Path = at };
                if (Present(o, "centre") is JsonValue cv && Pt(cv) is Point2 c)
                {
                    loop.Centre = c;
                    loop.Radius = Len(o, "radius") ?? 0;
                    return loop;
                }
                var pts = Points(o, "points");
                foreach (var p in pts)
                    if (loop.Points.Count == 0 || loop.Points[loop.Points.Count - 1].DistanceTo(p) > _tol) loop.Points.Add(p);
                if (loop.Points.Count > 1 && loop.Points[0].DistanceTo(loop.Points[loop.Points.Count - 1]) <= _tol) loop.Points.RemoveAt(loop.Points.Count - 1);
                return loop;
            }

            private void CheckLoop(RecipeLoop l, string what)
            {
                if (l.IsCircle) return;
                if (l.Points.Count < 3) { Error(l.Path, IssueCodes.TooFew, what + " needs at least 3 distinct points"); return; }
                var poly = new Polygon2(l.Points);
                if (poly.Area <= _tol * _tol) { Error(l.Path, IssueCodes.InvalidValue, what + " encloses no area (its points are on one line)"); return; }
                if (!poly.IsSimple(_tol * 0.01)) Error(l.Path, IssueCodes.InvalidValue, what + " crosses or touches itself");
            }

            private void CheckLoopsApart(List<RecipeLoop> loops, string at)
            {
                for (int i = 0; i < loops.Count; i++)
                    for (int j = i + 1; j < loops.Count; j++)
                    {
                        var a = loops[i].Outline();
                        var b = loops[j].Outline();
                        if (a.Count < 3 || b.Count < 3) continue;
                        bool cross = new Polygon2(a).Edges().Any(e => new Polygon2(b).Edges().Any(g => e.Intersect(g, _tol * 0.01).Kind != IntersectionKind.None));
                        if (cross) Error(at, IssueCodes.InvalidValue, "profile loops " + i + " and " + j + " cross or touch; loops of one profile must lie apart (one inside another makes a hole)");
                    }
            }

            private void CheckForm(RecipeForm f, JsonObject o, string at)
            {
                foreach (var l in f.Profile) CheckLoop(l, "profile loop");
                if (f.TopProfile.Count > 0) CheckLoop(new RecipeLoop { Points = Clean(f.TopProfile), Path = at + ".topProfile" }, "topProfile");
                CheckLoopsApart(f.Profile, at + ".profile");
                if (f.Material != null)
                {
                    if (f.Void) Error(at + ".material", IssueCodes.Constraint, "a void has no material");
                    else Param(f.Material, at + ".material", "material parameter", "material");
                }
                if (f.VisibleParameter != null) Param(f.VisibleParameter, at + ".visibleParameter", "visibility parameter", "yesNo");
                if (f.Void && (f.Subcategory != null || Present(o, "visibility") != null))
                    Warn(at, IssueCodes.Constraint, "a void cuts solids and is never drawn, so subcategory and visibility do nothing");
                if (f.Subcategory != null && (f.Subcategory.Trim().Length == 0 || f.Subcategory.IndexOfAny(RevitProhibited) >= 0))
                    Error(at + ".subcategory", IssueCodes.InvalidValue, "subcategory name '" + f.Subcategory + "' is not usable");

                var locked = new Dictionary<string, RecipePlane>(StringComparer.Ordinal);
                foreach (var kv in f.Lock)
                {
                    string lat = JsonPath.Key(at + ".lock", kv.Key);
                    if (Array.IndexOf(Jobs.Ops.RevitOpsFamilyCreate.LockFaces, kv.Key) < 0) { Error(lat, IssueCodes.Enum, "'" + kv.Key + "' is not a face direction (+x, -x, +y, -y, +z, -z)"); continue; }
                    var pl = PlaneRef(kv.Value, lat, kv.Key.Substring(1), "plane");
                    if (pl != null) locked[kv.Key] = pl;
                }

                switch (f.Kind)
                {
                    case "extrusion":
                        if (f.IsBox)
                        {
                            if (f.Profile.Count > 0 || f.End.HasValue) Error(at, IssueCodes.Constraint, "give box, or profile with end, not both");
                            if (f.Lock.Count > 0) Error(at + ".lock", IssueCodes.Constraint, "a box locks all six faces itself; lock is for profile extrusions");
                            var x = BoxPair(f.BoxX!, at + ".box.x", "x"); var y = BoxPair(f.BoxY!, at + ".box.y", "y"); var z = BoxPair(f.BoxZ!, at + ".box.z", "z");
                            if (o["sketch"] != null || o["offset"] != null || o["start"] != null) Warn(at, IssueCodes.Constraint, "a box takes its position from its planes; sketch, offset and start are ignored");
                            return;
                        }
                        if (f.Profile.Count == 0 || !f.End.HasValue) { Error(at, IssueCodes.Required, "an extrusion needs box, or profile and end"); return; }
                        if (Math.Abs(f.End.Value - f.Start) <= _tol) Error(at + ".end", IssueCodes.Range, "end must differ from start (the extrusion has no depth)");
                        CheckLocks(f, locked, f.Profile.SelectMany(l => l.Outline()).ToList(), at);
                        break;
                    case "blend":
                        if (f.Profile.Count != 1) Error(at + ".profile", IssueCodes.Constraint, "a blend's profile is exactly one loop");
                        if (f.TopProfile.Count == 0) Error(at + ".topProfile", IssueCodes.Required, "a blend needs topProfile");
                        if (!f.End.HasValue) { Error(at + ".end", IssueCodes.Required, "a blend needs end (the top loop's distance from the sketch plane)"); return; }
                        if (Math.Abs(f.End.Value - f.Start) <= _tol) Error(at + ".end", IssueCodes.Range, "end must differ from start");
                        CheckLocks(f, locked, f.Profile.SelectMany(l => l.Outline()).Concat(f.TopProfile).ToList(), at);
                        break;
                    case "revolution":
                        if (f.Profile.Count == 0) Error(at + ".profile", IssueCodes.Required, "a revolution needs a profile");
                        if (f.Axis is null) { Error(at + ".axis", IssueCodes.Required, "a revolution needs axis"); return; }
                        var seg = new Segment2(f.Axis[0], f.Axis[1]);
                        if (seg.Length <= _tol) { Error(at + ".axis", IssueCodes.InvalidValue, "the axis points coincide"); return; }
                        if (f.EndAngle <= f.StartAngle) Error(at + ".endAngle", IssueCodes.Range, "endAngle must be greater than startAngle");
                        else if (f.EndAngle - f.StartAngle > 360 + 1e-9) Error(at + ".endAngle", IssueCodes.Range, "a revolution turns at most 360 degrees");
                        foreach (var l in f.Profile)
                        {
                            var pts = l.Outline();
                            bool left = pts.Any(p => seg.Vector.Cross(p - seg.A) > _tol), right = pts.Any(p => seg.Vector.Cross(p - seg.A) < -_tol);
                            if (left && right) Error(l.Path, IssueCodes.InvalidValue, "the profile crosses the axis of revolution; keep it on one side");
                        }
                        if (f.Lock.Count > 0) Warn(at + ".lock", IssueCodes.NotPrechecked, "faces of a revolution are checked against their planes only by Revit");
                        break;
                    case "sweep":
                        if (f.Profile.Count == 0) Error(at + ".profile", IssueCodes.Required, "a sweep needs a profile");
                        if (f.PathPoints.Count < 2) { Error(at + ".path", IssueCodes.Required, "a sweep needs a path of two points or more"); return; }
                        CheckPath(f.PathPoints, at + ".path");
                        if (f.ProfileAt > f.PathPoints.Count - 2) Error(at + ".profileAt", IssueCodes.Range, "profileAt must name a path segment (0 to " + (f.PathPoints.Count - 2) + ")");
                        if (f.Lock.Count > 0) Warn(at + ".lock", IssueCodes.NotPrechecked, "faces of a sweep are checked against their planes only by Revit");
                        break;
                    case "sweptBlend":
                        if (f.Profile.Count != 1) Error(at + ".profile", IssueCodes.Constraint, "a swept blend's profile is exactly one loop");
                        if (f.TopProfile.Count == 0) Error(at + ".topProfile", IssueCodes.Required, "a swept blend needs topProfile");
                        if (f.PathPoints.Count != 2) { Error(at + ".path", IssueCodes.Constraint, "a swept blend's path is one straight segment: exactly two points"); return; }
                        CheckPath(f.PathPoints, at + ".path");
                        if (f.Lock.Count > 0) Warn(at + ".lock", IssueCodes.NotPrechecked, "faces of a swept blend are checked against their planes only by Revit");
                        break;
                }
            }

            private string[]? BoxPair(string[] names, string at, string axis)
            {
                if (names.Length != 2) return null;
                var a = PlaneRef(names[0], JsonPath.Index(at, 0), axis, "plane");
                var b = PlaneRef(names[1], JsonPath.Index(at, 1), axis, "plane");
                if (a is null || b is null) return null;
                if (a.Name == b.Name) { Error(at, IssueCodes.Constraint, "the two planes must differ"); return null; }
                if (Math.Abs(a.At - b.At) <= _tol) { Error(at, IssueCodes.Constraint, "planes '" + a.Name + "' and '" + b.Name + "' start at the same position; the box would have no size"); return null; }
                return names;
            }

            private void CheckPath(List<Point2> pts, string at)
            {
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (pts[i].DistanceTo(pts[i + 1]) <= _tol) Error(JsonPath.Index(at, i + 1), IssueCodes.InvalidValue, "path segment " + i + " has no length");
                for (int i = 0; i + 1 < pts.Count; i++)
                    for (int j = i + 2; j + 1 < pts.Count; j++)
                        if (new Segment2(pts[i], pts[i + 1]).Intersect(new Segment2(pts[j], pts[j + 1]), _tol * 0.01).Kind != IntersectionKind.None)
                            Error(at, IssueCodes.InvalidValue, "the path crosses itself (segments " + i + " and " + j + ")");
            }

            /// <summary>Each locked face must start on its plane: the profile's extent on that side equals the plane's position.</summary>
            private void CheckLocks(RecipeForm f, Dictionary<string, RecipePlane> locked, List<Point2> uv, string at)
            {
                if (locked.Count == 0 || uv.Count == 0) return;
                string u = f.Sketch == "side" ? "y" : "x", v = f.Sketch == "plan" ? "y" : "z", w = f.Sketch == "plan" ? "z" : f.Sketch == "front" ? "y" : "x";
                double end = f.End ?? f.Start;
                var lo = new Dictionary<string, double> { [u] = uv.Min(p => p.X), [v] = uv.Min(p => p.Y), [w] = f.Offset + Math.Min(f.Start, end) };
                var hi = new Dictionary<string, double> { [u] = uv.Max(p => p.X), [v] = uv.Max(p => p.Y), [w] = f.Offset + Math.Max(f.Start, end) };
                foreach (var kv in locked)
                {
                    string axis = kv.Key.Substring(1);
                    double face = kv.Key[0] == '+' ? hi[axis] : lo[axis];
                    if (Math.Abs(face - kv.Value.At) > _tol)
                        Error(JsonPath.Key(at + ".lock", kv.Key), IssueCodes.Constraint, "the form's " + kv.Key + " face starts at " + Fmt(face) + " but plane '" + kv.Value.Name +
                            "' starts at " + Fmt(kv.Value.At) + "; draw the profile to the plane so the face lies on it");
                }
            }

            // ---------------------------------------------------------- symbols and flex

            private void ReadSymbols(JsonObject a)
            {
                foreach (var (o, at) in Items(a, "symbols", P("symbols")))
                {
                    var s = new RecipeSymbol { Subcategory = Str(o, "subcategory"), Coarse = Bool(o, "coarse", true), Medium = Bool(o, "medium", true), Fine = Bool(o, "fine", true), Path = at };
                    _r.Symbols.Add(s);
                    if (!s.Coarse && !s.Medium && !s.Fine) Error(at, IssueCodes.Constraint, "the lines must show at one detail level at least");
                    if (s.Subcategory != null && (s.Subcategory.Trim().Length == 0 || s.Subcategory.IndexOfAny(RevitProhibited) >= 0))
                        Error(at + ".subcategory", IssueCodes.InvalidValue, "subcategory name '" + s.Subcategory + "' is not usable");
                    if (Present(o, "rect") is JsonObject rect)
                    {
                        s.IsRect = true;
                        s.X = Strings(rect, "x").ToArray(); s.Y = Strings(rect, "y").ToArray();
                        if (s.X.Length == 2) BoxPair(s.X, at + ".rect.x", "x");
                        if (s.Y.Length == 2) BoxPair(s.Y, at + ".rect.y", "y");
                    }
                    else if (Present(o, "line") is JsonObject line)
                    {
                        s.On = Str(line, "on") ?? ""; s.From = Str(line, "from") ?? ""; s.To = Str(line, "to") ?? "";
                        var on = PlaneRef(s.On, at + ".line.on", null, "plane");
                        if (on is null) continue;
                        if (on.Axis == "z") { Error(at + ".line.on", IssueCodes.Constraint, "a plan line lies on an x or y plane, not on '" + on.Name + "' (a z plane)"); continue; }
                        string other = on.Axis == "x" ? "y" : "x";
                        var from = PlaneRef(s.From, at + ".line.from", other, "plane");
                        var to = PlaneRef(s.To, at + ".line.to", other, "plane");
                        if (from != null && to != null && Math.Abs(from.At - to.At) <= _tol) Error(at + ".line", IssueCodes.Constraint, "planes '" + from.Name + "' and '" + to.Name + "' start at the same position; the line would have no length");
                    }
                }
            }

            private void ReadFlex(JsonObject a)
            {
                if (!(Present(a, "flex") is JsonObject o))
                {
                    if (a["forms"] != null) Warn(P("flex"), IssueCodes.Constraint, "no flex sizes: the flex test will only check that every type regenerates without warnings");
                    return;
                }
                _r.Flex = new RecipeFlex { X = Str(o, "x"), Y = Str(o, "y"), Z = Str(o, "z"), Tolerance = Len(o, "tolerance"), Path = P("flex") };
                foreach (var kv in _r.Flex.Axes()) Param(kv.Value, JsonPath.Key(P("flex"), kv.Key), "flex size", "length");
                if (!_r.Flex.Axes().Any()) Warn(P("flex"), IssueCodes.Constraint, "flex names no size parameter (x, y or z)");
                if (_r.Forms.All(f => f.Void)) Error(P("forms"), IssueCodes.Constraint, "every form is a void; the family needs a solid");
            }

            // ---------------------------------------------------------- reading helpers

            private static JsonValue? Present(JsonObject o, string key) { var v = o[key]; return v is null || v.IsNull ? null : v; }
            private static string? Str(JsonObject o, string key) => Present(o, key) is JsonString s ? s.Value : null;
            private static bool Bool(JsonObject o, string key, bool def) => Present(o, key) is JsonBool b ? b.Value : def;

            private double? Len(JsonObject o, string key)
            {
                var v = Present(o, key);
                if (v is JsonNumber n) return n.Value;
                if (v is JsonString s && Length.TryParse(s.Value, _unit, out double d, out _)) return d;
                return null;
            }

            private static double? Ang(JsonObject o, string key)
            {
                var v = Present(o, key);
                if (v is JsonNumber n) return n.Value;
                if (v is JsonString s && Angle.TryParseDegrees(s.Value, out double d)) return d;
                return null;
            }

            private Point2? Pt(JsonValue v)
            {
                if (!(v is JsonArray a) || a.Count != 2) return null;
                double? x = a[0] is JsonNumber nx ? nx.Value : a[0] is JsonString sx && Length.TryParse(sx.Value, _unit, out double dx, out _) ? dx : (double?)null;
                double? y = a[1] is JsonNumber ny ? ny.Value : a[1] is JsonString sy && Length.TryParse(sy.Value, _unit, out double dy, out _) ? dy : (double?)null;
                return x.HasValue && y.HasValue ? new Point2(x.Value, y.Value) : (Point2?)null;
            }

            private List<Point2> Points(JsonObject o, string key)
            {
                var list = new List<Point2>();
                if (Present(o, key) is JsonArray a) foreach (var i in a) if (Pt(i) is Point2 p) list.Add(p);
                return list;
            }

            private List<Point2> Clean(List<Point2> pts)
            {
                var l = new List<Point2>();
                foreach (var p in pts) if (l.Count == 0 || l[l.Count - 1].DistanceTo(p) > _tol) l.Add(p);
                if (l.Count > 1 && l[0].DistanceTo(l[l.Count - 1]) <= _tol) l.RemoveAt(l.Count - 1);
                return l;
            }

            private static List<string> Strings(JsonObject o, string key)
            {
                var list = new List<string>();
                if (Present(o, key) is JsonArray a) foreach (var i in a) if (i is JsonString s) list.Add(s.Value);
                return list;
            }

            private static IEnumerable<(JsonObject, string)> Items(JsonObject o, string key, string at)
            {
                if (!(Present(o, key) is JsonArray a)) yield break;
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is JsonObject item) yield return (item, JsonPath.Index(at, i));
            }

            private static string Fmt(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// What a Revit formula names. Parameter names may hold spaces, so declared names are matched first (longest
    /// first, whole words); what is left must be numbers, operators, functions or units.
    /// </summary>
    public static class FamilyFormulas
    {
        public static readonly string[] Functions =
            { "if", "and", "or", "not", "sin", "cos", "tan", "asin", "acos", "atan", "sqrt", "exp", "ln", "log", "abs", "round", "roundup", "rounddown", "pi", "size_lookup" };
        public static readonly string[] Units = { "mm", "cm", "dm", "m", "ft", "in" };

        private static readonly Regex Word = new Regex(@"(?<![\w.])[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

        /// <summary>The declared parameters the formula uses; <paramref name="unknown"/> gets every other name in it.</summary>
        public static List<string> Analyse(string formula, IList<string> declared, out List<string> unknown, out string? syntax)
        {
            unknown = new List<string>();
            syntax = null;
            string text = Regex.Replace(formula, "\"[^\"]*\"", " 0 ");
            int depth = 0;
            foreach (char ch in text)
            {
                if (ch == '(') depth++;
                else if (ch == ')' && --depth < 0) break;
            }
            if (depth != 0) syntax = "the brackets do not balance";
            var used = new List<string>();
            foreach (var name in declared.OrderByDescending(n => n.Length))
            {
                int i = 0;
                bool hit = false;
                while ((i = text.IndexOf(name, i, StringComparison.Ordinal)) >= 0)
                {
                    bool before = i == 0 || !IsWordChar(text[i - 1]);
                    bool after = i + name.Length >= text.Length || !IsWordChar(text[i + name.Length]);
                    if (before && after)
                    {
                        text = text.Substring(0, i) + " 0 " + text.Substring(i + name.Length);
                        hit = true;
                        i += 3;
                    }
                    else i += name.Length;
                }
                if (hit) used.Add(name);
            }
            foreach (Match m in Word.Matches(text))
            {
                string w = m.Value;
                if (Functions.Contains(w, StringComparer.OrdinalIgnoreCase) || Units.Contains(w, StringComparer.Ordinal)) continue;
                if (!unknown.Contains(w)) unknown.Add(w);
            }
            return used;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
