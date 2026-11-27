using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.Core.Standards
{
    /// <summary>A layer colour: an AutoCAD Colour Index (1-255) or a true colour.</summary>
    public sealed class ColorSpec
    {
        private ColorSpec(int? aci, byte r, byte g, byte b)
        {
            Aci = aci; R = r; G = g; B = b;
        }

        public int? Aci { get; }
        public bool IsTrueColor => Aci is null;
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static ColorSpec FromAci(int aci)
        {
            if (aci < 1 || aci > 255) throw new ArgumentOutOfRangeException(nameof(aci), "ACI colours are 1 to 255.");
            return new ColorSpec(aci, 0, 0, 0);
        }

        public static ColorSpec FromRgb(byte r, byte g, byte b) => new ColorSpec(null, r, g, b);

        private static readonly Regex Hex = new Regex("^#([0-9A-Fa-f]{6})$", RegexOptions.CultureInvariant);
        private static readonly Regex Rgb = new Regex(@"^\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*$", RegexOptions.CultureInvariant);

        /// <summary>Accepts an integer 1-255, "#RRGGBB" or "r,g,b".</summary>
        public static bool TryParse(JsonValue v, out ColorSpec? color)
        {
            color = null;
            if (v is JsonNumber n && n.TryGetInt64(out long aci) && aci >= 1 && aci <= 255) { color = FromAci((int)aci); return true; }
            if (!(v is JsonString s)) return false;
            var m = Hex.Match(s.Value);
            if (m.Success)
            {
                int x = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                color = FromRgb((byte)(x >> 16), (byte)(x >> 8), (byte)x);
                return true;
            }
            m = Rgb.Match(s.Value);
            if (m.Success)
            {
                int r = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (r > 255 || g > 255 || b > 255) return false;
                color = FromRgb((byte)r, (byte)g, (byte)b);
                return true;
            }
            return false;
        }

        public override string ToString() => Aci.HasValue ? Aci.Value.ToString(CultureInfo.InvariantCulture) : R + "," + G + "," + B;
    }

    public sealed class LayerSpec
    {
        internal LayerSpec(string name, ColorSpec color, string linetype, double? lineweight, bool plot, string? description, int transparency)
        {
            Name = name; Color = color; Linetype = linetype; Lineweight = lineweight; Plot = plot; Description = description; Transparency = transparency;
        }

        public string Name { get; }
        public ColorSpec Color { get; }
        public string Linetype { get; }
        /// <summary>Millimetres, or null for the application default.</summary>
        public double? Lineweight { get; }
        public bool Plot { get; }
        public string? Description { get; }
        /// <summary>0 to 90 percent.</summary>
        public int Transparency { get; }
    }

    public sealed class TextStyleSpec
    {
        internal TextStyleSpec(string name, string font, double height, double widthFactor, double oblique, bool annotative, string? use)
        {
            Name = name; Font = font; Height = height; WidthFactor = widthFactor; ObliqueAngle = oblique; Annotative = annotative; Use = use;
        }

        public string Name { get; }
        public string Font { get; }
        /// <summary>
        /// Fixed height as a plain number in the standard's <c>units.length</c> (<see cref="ProjectStandard.LengthUnit"/>); when the
        /// standard gives no unit, in the job's length unit (that is how the engines read it). For an annotative style it is
        /// the paper height. 0 means the height is set per text object.
        /// </summary>
        public double Height { get; }
        public double WidthFactor { get; }
        public double ObliqueAngle { get; }
        public bool Annotative { get; }
        public string? Use { get; }
    }

    /// <summary>A dimension style as named settings (AutoCAD dimension variables or Revit type parameters by name).</summary>
    public sealed class DimStyleSpec
    {
        internal DimStyleSpec(string name, string? description, bool annotative, JsonObject settings)
        {
            Name = name; Description = description; Annotative = annotative; Settings = settings;
        }

        public string Name { get; }
        public string? Description { get; }
        public bool Annotative { get; }
        public JsonObject Settings { get; }
    }

    public sealed class RevisionFormat
    {
        internal RevisionFormat(string name, Regex pattern, string patternText, string? description)
        {
            Name = name; Pattern = pattern; PatternText = patternText; Description = description;
        }

        public string Name { get; }
        public Regex Pattern { get; }
        public string PatternText { get; }
        public string? Description { get; }
    }

    public sealed class TitleBlockField
    {
        internal TitleBlockField(string name, string? description, bool required, string? source)
        {
            Name = name; Description = description; Required = required; Source = source;
        }

        /// <summary>Attribute tag (AutoCAD) or parameter name (Revit).</summary>
        public string Name { get; }
        public string? Description { get; }
        public bool Required { get; }
        /// <summary>Where the value comes from, e.g. "sheet.number", "project.name", "revision.current".</summary>
        public string? Source { get; }
    }

    public sealed class StandardFormatException : FormatException
    {
        public StandardFormatException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>A loaded project standard. Every list and rule comes from the standards file; nothing is hard-coded.</summary>
    public sealed class ProjectStandard
    {
        internal ProjectStandard() { }

        public string Id { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string? Version { get; internal set; }
        public string? Description { get; internal set; }
        public string? Jurisdiction { get; internal set; }
        public LengthUnit? LengthUnit { get; internal set; }
        public IReadOnlyDictionary<string, List<CodeEntry>> CodeLists { get; internal set; } = new Dictionary<string, List<CodeEntry>>();
        public ILayerNameScheme? LayerScheme { get; internal set; }
        public List<LayerSpec> Layers { get; } = new List<LayerSpec>();
        public List<TextStyleSpec> TextStyles { get; } = new List<TextStyleSpec>();
        public List<DimStyleSpec> DimStyles { get; } = new List<DimStyleSpec>();
        public NamingRule? SheetNumbering { get; internal set; }
        public NamingRule? FileNaming { get; internal set; }
        public List<CodeEntry> StatusCodes { get; } = new List<CodeEntry>();
        public List<RevisionFormat> RevisionFormats { get; } = new List<RevisionFormat>();
        public List<Scale> Scales { get; } = new List<Scale>();
        /// <summary>How scales may be combined on a sheet (<c>scaleRules</c>), or null when the standard sets no rule.</summary>
        public ScaleRules? ScaleRules { get; internal set; }
        public List<TextHeight> TextHeights { get; } = new List<TextHeight>();
        public List<TitleBlockField> TitleBlockFields { get; } = new List<TitleBlockField>();
        /// <summary>Rule for view names (<c>viewNaming</c>), or null when the standard sets none.</summary>
        public NamingRule? ViewNaming { get; internal set; }
        /// <summary>Layer for viewport frames (<c>layerRoles.viewport</c>), or null.</summary>
        public string? ViewportLayer { get; internal set; }
        /// <summary>Layer for external reference inserts (<c>layerRoles.xref</c>), or null.</summary>
        public string? XrefLayer { get; internal set; }
        /// <summary>Layer for the title block insert (<c>layerRoles.titleBlock</c>), or null.</summary>
        public string? TitleBlockLayer { get; internal set; }
        /// <summary>AutoCAD block name of the title block (<c>titleBlock.blockName</c>), or null.</summary>
        public string? TitleBlockName { get; internal set; }
        /// <summary>Revit title block type written "Family : Type" (<c>titleBlock.familyType</c>), or null.</summary>
        public string? TitleBlockFamilyType { get; internal set; }
        /// <summary>Names a purge must never remove (<c>purge.keep</c>), as written. See <see cref="NamesToKeep"/>.</summary>
        public List<string> PurgeKeep { get; } = new List<string>();
        /// <summary>Named regular expressions that checks can refer to (param "standardPattern").</summary>
        public Dictionary<string, Regex> Patterns { get; } = new Dictionary<string, Regex>(StringComparer.Ordinal);
        public JsonObject Raw { get; internal set; } = new JsonObject();

        public LayerSpec? FindLayer(string name) => Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        public Scale? FindScale(string name) => Scales.FirstOrDefault(s => s.Name == name);
        public TextHeight? FindTextHeight(string name) => TextHeights.FirstOrDefault(t => t.Name == name);

        public NameResult ParseLayerName(string name) =>
            LayerScheme?.Parse(name) ?? throw new InvalidOperationException("Standard '" + Id + "' defines no layer naming scheme.");

        public NameResult ParseSheetId(string id) =>
            SheetNumbering?.Parse(id) ?? throw new InvalidOperationException("Standard '" + Id + "' defines no sheet numbering.");

        public NameResult ParseFileName(string fileName) =>
            FileNaming?.Parse(fileName) ?? throw new InvalidOperationException("Standard '" + Id + "' defines no file naming rule.");

        public NameResult ParseViewName(string viewName) =>
            ViewNaming?.Parse(viewName) ?? throw new InvalidOperationException("Standard '" + Id + "' defines no view naming rule.");

        /// <summary>
        /// Every name the standard relies on, which a purge must keep: <see cref="PurgeKeep"/>, the layers, their linetypes,
        /// the text and dimension styles, the role layers and the title block block name. Compared without case.
        /// </summary>
        public HashSet<string> NamesToKeep()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in PurgeKeep) set.Add(n);
            foreach (var l in Layers) { set.Add(l.Name); if (l.Linetype.Length > 0) set.Add(l.Linetype); }
            foreach (var t in TextStyles) set.Add(t.Name);
            foreach (var d in DimStyles) set.Add(d.Name);
            foreach (var n in new[] { ViewportLayer, XrefLayer, TitleBlockLayer, TitleBlockName }) if (n != null) set.Add(n);
            return set;
        }

        public bool IsStatusCode(string code) => StatusCodes.Any(s => s.Code == code);

        /// <summary>The first revision format the code matches, or null.</summary>
        public RevisionFormat? MatchRevision(string code) => RevisionFormats.FirstOrDefault(f => f.Pattern.IsMatch(code));

        /// <summary>Model-space height for a named plotted text height at a scale ratio.</summary>
        public double ModelTextHeight(string textHeightName, double ratio, LengthUnit modelUnit)
        {
            var t = FindTextHeight(textHeightName) ?? throw new KeyNotFoundException("Standard '" + Id + "' has no text height '" + textHeightName + "'.");
            return t.ModelHeight(ratio, modelUnit);
        }

        /// <summary>Consistency problems inside the file itself, e.g. layer list entries that break the layer naming scheme.</summary>
        public List<string> SelfCheck()
        {
            var problems = new List<string>();
            if (LayerScheme != null)
                foreach (var l in Layers)
                {
                    var r = LayerScheme.Parse(l.Name);
                    if (!r.Valid) problems.Add("layer " + r.Explain());
                }
            foreach (var g in Layers.GroupBy(l => l.Name.ToUpperInvariant()).Where(g => g.Count() > 1))
                problems.Add("layer '" + g.First().Name + "' is listed " + g.Count() + " times");
            if (SheetNumbering != null)
                foreach (var e in SheetNumbering.Examples)
                {
                    var r = SheetNumbering.Parse(e);
                    if (!r.Valid) problems.Add("sheet numbering example " + r.Explain());
                }
            if (FileNaming != null)
                foreach (var e in FileNaming.Examples)
                {
                    var r = FileNaming.Parse(e);
                    if (!r.Valid) problems.Add("file naming example " + r.Explain());
                }
            if (ViewNaming != null)
                foreach (var e in ViewNaming.Examples)
                {
                    var r = ViewNaming.Parse(e);
                    if (!r.Valid) problems.Add("view naming example " + r.Explain());
                }
            foreach (var role in new[] { Tuple.Create("viewport", ViewportLayer), Tuple.Create("xref", XrefLayer), Tuple.Create("titleBlock", TitleBlockLayer) })
            {
                if (role.Item2 is null) continue;
                var layer = FindLayer(role.Item2);
                if (layer is null) { if (Layers.Count > 0) problems.Add("layerRoles." + role.Item1 + " '" + role.Item2 + "' is not in the layer list"); continue; }
                if (role.Item1 == "viewport" && layer.Plot) problems.Add("layerRoles.viewport '" + layer.Name + "' plots, so viewport frames would print");
                if (role.Item1 != "viewport" && !layer.Plot) problems.Add("layerRoles." + role.Item1 + " '" + layer.Name + "' does not plot");
            }
            return problems;
        }
    }

    public static class StandardLoader
    {
        /// <summary>Lineweights AutoCAD accepts, in millimetres.</summary>
        public static readonly double[] StandardLineweights =
        {
            0.00, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90,
            1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11,
        };

        public static ProjectStandard LoadFile(string path) => Parse(File.ReadAllText(path));

        /// <summary>Loads a standards file without throwing. On failure <paramref name="error"/> names the file and the JSON path.</summary>
        public static bool TryLoadFile(string path, out ProjectStandard? standard, out string? error)
        {
            standard = null; error = null;
            try
            {
                standard = LoadFile(path);
                return true;
            }
            catch (StandardFormatException e) { error = path + ": " + e.Message; }
            catch (IOException e) { error = path + ": " + e.Message; }
            catch (UnauthorizedAccessException e) { error = path + ": " + e.Message; }
            catch (ArgumentException e) { error = path + ": " + e.Message; }
            return false;
        }

        public static ProjectStandard Parse(string text)
        {
            JsonValue json;
            try { json = JsonValue.Parse(text); }
            catch (JsonParseException e) { throw new StandardFormatException(e.Message, e); }
            return FromJson(json);
        }

        /// <summary>Loads a standard. Errors name the JSON path, e.g. "$.layers[4].lineweight: 0.33 is not a standard lineweight".</summary>
        public static ProjectStandard FromJson(JsonValue json)
        {
            try
            {
                return Load(new JsonCursor(json));
            }
            catch (JsonAccessException e)
            {
                throw new StandardFormatException(e.Message, e);
            }
        }

        private static ProjectStandard Load(JsonCursor c)
        {
            c.RejectUnknownKeys("$schema", "id", "name", "version", "description", "jurisdiction", "units", "codeLists", "layerNaming", "layers",
                "textStyles", "dimStyles", "sheetNumbering", "fileNaming", "statusCodes", "revisionCodes", "scales", "textHeights", "titleBlock",
                "patterns", "notes", "viewNaming", "layerRoles", "purge", "scaleRules");
            var s = new ProjectStandard
            {
                Id = c.GetNonEmptyString("id"),
                Name = c.GetNonEmptyString("name"),
                Version = c.GetStringOrNull("version"),
                Description = c.GetStringOrNull("description"),
                Jurisdiction = c.GetStringOrNull("jurisdiction"),
                Raw = c.AsObject(),
            };

            var units = c.Optional("units");
            if (units != null)
            {
                units.RejectUnknownKeys("length");
                string u = units.GetString("length");
                if (!Length.TryParseUnit(u, out var lu)) throw units.Required("length").Fail("expected mm, cm, m, in or ft");
                s.LengthUnit = lu;
            }

            var lists = new Dictionary<string, List<CodeEntry>>(StringComparer.Ordinal);
            var cl = c.Optional("codeLists");
            if (cl != null)
                foreach (var kv in cl.Members()) lists[kv.Key] = ReadCodeArray(kv.Value);
            s.CodeLists = lists;

            var ln = c.Optional("layerNaming");
            if (ln != null)
            {
                string scheme = ln.GetString("scheme");
                if (scheme == "us") s.LayerScheme = UsLayerScheme.FromJson(ln, lists);
                else if (scheme == "iso13567") s.LayerScheme = IsoLayerScheme.FromJson(ln, lists);
                else throw ln.Required("scheme").Fail("expected 'us' or 'iso13567'");
            }

            var layers = c.Optional("layers");
            if (layers != null)
                foreach (var l in layers.Items())
                {
                    l.RejectUnknownKeys("name", "color", "linetype", "lineweight", "plot", "description", "transparency");
                    var colorC = l.Required("color");
                    if (!ColorSpec.TryParse(colorC.Value, out var color)) throw colorC.Fail("expected an ACI number 1-255, \"#RRGGBB\" or \"r,g,b\"");
                    double? lw = null;
                    var lwc = l.Optional("lineweight");
                    if (lwc != null)
                    {
                        if (lwc.Value is JsonString lws && lws.Value == "default") lw = null;
                        else
                        {
                            double v = lwc.AsNumber();
                            if (!StandardLineweights.Any(x => Math.Abs(x - v) < 1e-9)) throw lwc.Fail(v.ToString(CultureInfo.InvariantCulture) + " is not a standard lineweight (mm)");
                            lw = v;
                        }
                    }
                    int tr = l.GetInt32("transparency", 0);
                    if (tr < 0 || tr > 90) throw l.Required("transparency").Fail("transparency is 0 to 90");
                    s.Layers.Add(new LayerSpec(l.GetNonEmptyString("name"), color!, l.GetString("linetype", "Continuous"), lw, l.GetBool("plot", true), l.GetStringOrNull("description"), tr));
                }

            var ts = c.Optional("textStyles");
            if (ts != null)
                foreach (var t in ts.Items())
                {
                    t.RejectUnknownKeys("name", "font", "height", "widthFactor", "obliqueAngle", "annotative", "use");
                    double h = t.GetNumber("height", 0);
                    if (h < 0) throw t.Required("height").Fail("height must not be negative");
                    double wf = t.GetNumber("widthFactor", 1);
                    if (!(wf > 0)) throw t.Required("widthFactor").Fail("widthFactor must be positive");
                    s.TextStyles.Add(new TextStyleSpec(t.GetNonEmptyString("name"), t.GetNonEmptyString("font"), h, wf, t.GetNumber("obliqueAngle", 0), t.GetBool("annotative", false), t.GetStringOrNull("use")));
                }

            var ds = c.Optional("dimStyles");
            if (ds != null)
                foreach (var d in ds.Items())
                {
                    d.RejectUnknownKeys("name", "description", "annotative", "settings");
                    s.DimStyles.Add(new DimStyleSpec(d.GetNonEmptyString("name"), d.GetStringOrNull("description"), d.GetBool("annotative", false), d.Optional("settings")?.AsObject() ?? new JsonObject()));
                }

            var sn = c.Optional("sheetNumbering");
            if (sn != null) s.SheetNumbering = NamingRule.FromJson(sn, lists);
            var fn = c.Optional("fileNaming");
            if (fn != null) s.FileNaming = NamingRule.FromJson(fn, lists);
            var vn = c.Optional("viewNaming");
            if (vn != null) s.ViewNaming = NamingRule.FromJson(vn, lists);

            var roles = c.Optional("layerRoles");
            if (roles != null)
            {
                roles.RejectUnknownKeys("viewport", "xref", "titleBlock", "description");
                s.ViewportLayer = OptionalName(roles, "viewport");
                s.XrefLayer = OptionalName(roles, "xref");
                s.TitleBlockLayer = OptionalName(roles, "titleBlock");
            }

            var purge = c.Optional("purge");
            if (purge != null)
            {
                purge.RejectUnknownKeys("keep", "description");
                foreach (var k in purge.Required("keep").Items())
                {
                    string n = k.AsString();
                    if (n.Trim().Length == 0) throw k.Fail("names to keep must not be empty");
                    s.PurgeKeep.Add(n);
                }
            }

            var sc = c.Optional("statusCodes");
            if (sc != null) s.StatusCodes.AddRange(ReadCodeArray(sc));

            var rc = c.Optional("revisionCodes");
            if (rc != null)
            {
                rc.RejectUnknownKeys("description", "formats");
                foreach (var f in rc.Required("formats").Items())
                {
                    f.RejectUnknownKeys("name", "pattern", "description", "example");
                    var pc = f.Required("pattern");
                    var rx = NamingUtil.Compile(pc);
                    var ex = f.GetStringOrNull("example");
                    if (ex != null && !rx.IsMatch(ex)) throw f.Required("example").Fail("example '" + ex + "' does not match the pattern");
                    s.RevisionFormats.Add(new RevisionFormat(f.GetNonEmptyString("name"), rx, pc.AsString(), f.GetStringOrNull("description")));
                }
            }

            var scales = c.Optional("scales");
            if (scales != null)
                foreach (var x in scales.Items())
                {
                    x.RejectUnknownKeys("name", "ratio", "use");
                    string name = x.GetNonEmptyString("name");
                    double? ratio = x.GetNumberOrNull("ratio");
                    bool parsed = Scale.TryParseRatio(name, out double fromName);
                    if (ratio is null)
                    {
                        if (!parsed) throw x.Required("name").Fail("'" + name + "' is not a scale; give 'ratio'");
                        ratio = fromName;
                    }
                    else if (!(ratio > 0)) throw x.Required("ratio").Fail("ratio must be positive");
                    else if (parsed && Math.Abs(fromName - ratio.Value) > 1e-9 * ratio.Value) throw x.Required("ratio").Fail("ratio " + ratio + " disagrees with the name '" + name + "' (" + fromName + ")");
                    s.Scales.Add(new Scale(name, ratio.Value));
                }
            var scaleRules = c.Optional("scaleRules");
            if (scaleRules != null) s.ScaleRules = ScaleRules.FromJson(scaleRules);

            var th = c.Optional("textHeights");
            if (th != null)
                foreach (var x in th.Items())
                {
                    x.RejectUnknownKeys("name", "plotted", "unit", "use");
                    if (!Length.TryParseUnit(x.GetString("unit", "mm"), out var u)) throw x.Required("unit").Fail("expected mm, cm, m, in or ft");
                    double p = x.GetNumber("plotted");
                    if (!(p > 0)) throw x.Required("plotted").Fail("plotted height must be positive");
                    s.TextHeights.Add(new TextHeight(x.GetNonEmptyString("name"), p, u, x.GetStringOrNull("use")));
                }

            var tb = c.Optional("titleBlock");
            if (tb != null)
            {
                tb.RejectUnknownKeys("description", "fields", "blockName", "familyType");
                var tbFields = tb.Optional("fields");
                if (tbFields != null)
                    foreach (var f in tbFields.Items())
                    {
                        f.RejectUnknownKeys("name", "description", "required", "source");
                        s.TitleBlockFields.Add(new TitleBlockField(f.GetNonEmptyString("name"), f.GetStringOrNull("description"), f.GetBool("required", false), f.GetStringOrNull("source")));
                    }
                s.TitleBlockName = OptionalName(tb, "blockName");
                s.TitleBlockFamilyType = OptionalName(tb, "familyType");
                if (s.TitleBlockFamilyType != null && !FamilyTypeRegex.IsMatch(s.TitleBlockFamilyType))
                    throw tb.Required("familyType").Fail("expected \"Family : Type\"");
            }

            var pats = c.Optional("patterns");
            if (pats != null)
                foreach (var kv in pats.Members()) s.Patterns[kv.Key] = NamingUtil.Compile(kv.Value);

            return s;
        }

        private static readonly Regex FamilyTypeRegex = new Regex(@"^.+\S\s:\s\S.+$", RegexOptions.CultureInvariant);

        private static string? OptionalName(JsonCursor c, string key)
        {
            var v = c.Optional(key);
            if (v is null || v.Value.IsNull) return null;
            string s = v.AsString();
            if (s.Trim().Length == 0) throw v.Fail("must not be empty");
            return s;
        }

        /// <summary>Reads "codes" (inline) or "codeList" (a name in codeLists) from a field definition. Null when neither is present.</summary>
        internal static List<CodeEntry>? ReadCodes(JsonCursor field, IReadOnlyDictionary<string, List<CodeEntry>> lists)
        {
            var inline = field.Optional("codes");
            var named = field.Optional("codeList");
            if (inline != null && named != null) throw field.Fail("give either 'codes' or 'codeList', not both");
            if (inline != null) return ReadCodeArray(inline);
            if (named != null)
            {
                string n = named.AsString();
                if (!lists.TryGetValue(n, out var list)) throw named.Fail("no code list named '" + n + "' in codeLists");
                return list;
            }
            return null;
        }

        /// <summary>A code array: strings ("A") or objects ({ "code": "A", "name": "Architectural" }). Codes must be unique.</summary>
        internal static List<CodeEntry> ReadCodeArray(JsonCursor c)
        {
            var list = new List<CodeEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in c.Items())
            {
                CodeEntry e;
                if (item.Value is JsonString s) e = new CodeEntry(s.Value);
                else
                {
                    item.RejectUnknownKeys("code", "name", "description");
                    e = new CodeEntry(item.GetNonEmptyString("code"), item.GetStringOrNull("name"), item.GetStringOrNull("description"));
                }
                if (e.Code.Length == 0) throw item.Fail("code must not be empty");
                if (!seen.Add(e.Code)) throw item.Fail("code '" + e.Code + "' appears twice");
                list.Add(e);
            }
            return list;
        }
    }
}
