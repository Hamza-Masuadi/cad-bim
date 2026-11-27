using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.Core.Families
{
    /// <summary>The drawing the blocks came from.</summary>
    public sealed class PlacementsSource
    {
        /// <summary>File name of the drawing, e.g. "GF-R10.dwg".</summary>
        public string Drawing = "";
        public string Fingerprint = "";
        /// <summary>Unit of every length in the file: mm, cm, m, in, ft, or the AutoCAD unit name for other INSUNITS values.</summary>
        public string Units = "";
        /// <summary>The drawing's INSUNITS code (1 to 24).</summary>
        public int Insunits;

        /// <summary>The length unit of <see cref="Units"/> when it is one the jobs use; null for other drawing units.</summary>
        public LengthUnit? LengthUnit => Length.TryParseUnit(Units, out var u) ? u : (LengthUnit?)null;
    }

    /// <summary>One block reference: model space WCS, drawing units, degrees counter-clockwise from WCS X.</summary>
    public sealed class PlacementReference
    {
        public string Handle = "";
        /// <summary>"model", or the layout name for a reference in paper space.</summary>
        public string Space = "model";
        public string Layer = "";
        public double[] Position = new double[3];
        public double RotationDeg;
        /// <summary>Signed X, Y, Z scale factors.</summary>
        public double[] Scale = { 1, 1, 1 };
        public bool Mirrored;
    }

    /// <summary>One exported block table record and the references that use it.</summary>
    public sealed class PlacementBlock
    {
        /// <summary>The block name, or "&lt;effectiveName&gt;__&lt;n&gt;" for an anonymous representation of a dynamic block.</summary>
        public string Name = "";
        public string EffectiveName = "";
        /// <summary>The DWG's file name, in the same folder as placements.json.</summary>
        public string File = "";
        public bool IsDynamic;
        /// <summary>Geometric extents of the definition relative to its base point, in block units, before reference scale.</summary>
        public double[] Min = new double[3];
        public double[] Max = new double[3];
        public bool Has3d;
        public List<PlacementReference> References = new List<PlacementReference>();
    }

    /// <summary>
    /// placements.json (format cadbim.placements/1): what block.export writes next to the block DWGs, and what the
    /// Revit side reads to build families and place them. Shape and rules: reports/FAMILY-CREATE-CONTRACT.md section 1.
    /// </summary>
    public sealed class PlacementsFile
    {
        public const string FormatId = "cadbim.placements/1";
        public const string DefaultFileName = "placements.json";

        public PlacementsSource Source = new PlacementsSource();
        public List<PlacementBlock> Blocks = new List<PlacementBlock>();

        public PlacementBlock? Find(string name) =>
            Blocks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writer, reader, validator and file-name rules for <see cref="PlacementsFile"/>.</summary>
    public static class Placements
    {
        // ------------------------------------------------------------------ writing

        public static JsonObject ToJson(PlacementsFile f) =>
            new JsonObject()
                .Add("format", PlacementsFile.FormatId)
                .Add("source", new JsonObject()
                    .Add("drawing", f.Source.Drawing).Add("fingerprint", f.Source.Fingerprint)
                    .Add("units", f.Source.Units).Add("insunits", f.Source.Insunits))
                .Add("blocks", new JsonArray(f.Blocks.Select(b => (JsonValue)new JsonObject()
                    .Add("name", b.Name).Add("effectiveName", b.EffectiveName).Add("file", b.File).Add("isDynamic", b.IsDynamic)
                    .Add("extents", new JsonObject().Add("min", Vec(b.Min)).Add("max", Vec(b.Max)))
                    .Add("has3d", b.Has3d)
                    .Add("references", new JsonArray(b.References.Select(r => (JsonValue)new JsonObject()
                        .Add("handle", r.Handle).Add("space", r.Space).Add("layer", r.Layer)
                        .Add("position", Vec(r.Position)).Add("rotationDeg", r.RotationDeg)
                        .Add("scale", Vec(r.Scale)).Add("mirrored", r.Mirrored)))))));

        /// <summary>The file text: indented JSON.</summary>
        public static string Write(PlacementsFile f) => ToJson(f).ToJson(true);

        /// <summary>Writes UTF-8 without a byte order mark.</summary>
        public static void WriteFile(PlacementsFile f, string path) => System.IO.File.WriteAllText(path, Write(f), new UTF8Encoding(false));

        private static JsonArray Vec(double[] v) => new JsonArray(v.Select(x => (JsonValue)x));

        // ------------------------------------------------------------------ reading

        /// <summary>Parses placements.json. Throws a <see cref="FormatException"/> naming the JSON path on a shape error; run <see cref="Validate"/> on the result for the rules.</summary>
        public static PlacementsFile Parse(string text)
        {
            var root = new JsonCursor(JsonValue.Parse(text));
            root.AsObject();
            string format = root.GetString("format");
            if (format != PlacementsFile.FormatId)
                throw root.Required("format").Fail("is '" + format + "'; this reader reads " + PlacementsFile.FormatId);
            var s = root.Required("source");
            var f = new PlacementsFile
            {
                Source = new PlacementsSource
                {
                    Drawing = s.GetString("drawing"),
                    Fingerprint = s.GetString("fingerprint", ""),
                    Units = s.GetString("units"),
                    Insunits = s.GetInt32("insunits"),
                },
            };
            foreach (var b in root.Required("blocks").Items())
            {
                var ext = b.Required("extents");
                var block = new PlacementBlock
                {
                    Name = b.GetString("name"),
                    EffectiveName = b.GetString("effectiveName"),
                    File = b.GetString("file"),
                    IsDynamic = b.GetBool("isDynamic", false),
                    Min = Vec(ext.Required("min")),
                    Max = Vec(ext.Required("max")),
                    Has3d = b.GetBool("has3d", false),
                };
                foreach (var r in b.Required("references").Items())
                    block.References.Add(new PlacementReference
                    {
                        Handle = r.GetString("handle"),
                        Space = r.GetString("space", "model"),
                        Layer = r.GetString("layer"),
                        Position = Vec(r.Required("position")),
                        RotationDeg = r.GetNumber("rotationDeg"),
                        Scale = Vec(r.Required("scale")),
                        Mirrored = r.GetBool("mirrored", false),
                    });
                f.Blocks.Add(block);
            }
            return f;
        }

        public static PlacementsFile ReadFile(string path) => Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));

        private static double[] Vec(JsonCursor c)
        {
            var items = c.Items().ToList();
            if (items.Count != 3) throw c.Fail("must be [x, y, z]");
            return items.Select(i => i.AsNumber()).ToArray();
        }

        // ------------------------------------------------------------------ rules

        public const string InvalidCode = "placements_invalid";

        private static readonly Regex HandleRx = new Regex("^[0-9A-Fa-f]{1,16}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The contract's rules. Errors only; an empty list means the file can be used. <paramref name="folder"/>, when
        /// given, is where placements.json lies: every block's DWG must exist there.
        /// </summary>
        public static List<Issue> Validate(PlacementsFile f, string? folder = null)
        {
            var issues = new List<Issue>();
            void Err(string path, string msg) => issues.Add(new Issue(path, InvalidCode, msg));

            if (f.Source.Drawing.Trim().Length == 0) Err("$.source.drawing", "is empty");
            if (f.Source.Insunits < 1 || f.Source.Insunits > 24) Err("$.source.insunits", "must be an INSUNITS code from 1 to 24 (unitless drawings are not exported)");
            else if (SymbolFor(f.Source.Insunits) is string sym && f.Source.Units != sym)
                Err("$.source.units", "is '" + f.Source.Units + "' but INSUNITS " + f.Source.Insunits + " is '" + sym + "'");
            if (f.Source.Units.Trim().Length == 0) Err("$.source.units", "is empty");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < f.Blocks.Count; i++)
            {
                var b = f.Blocks[i];
                string p = JsonPath.Index("$.blocks", i);
                if (b.Name.Length == 0) Err(p + ".name", "is empty");
                else if (!names.Add(b.Name)) Err(p + ".name", "'" + b.Name + "' is listed twice");
                if (b.EffectiveName.Length == 0) Err(p + ".effectiveName", "is empty");
                else if (b.Name != b.EffectiveName)
                {
                    if (!b.IsDynamic) Err(p + ".name", "differs from effectiveName '" + b.EffectiveName + "', which only an anonymous representation of a dynamic block may do");
                    else if (!IsAnonymousName(b.Name, b.EffectiveName)) Err(p + ".name", "must be '" + b.EffectiveName + "__<n>' for an anonymous representation");
                }
                if (!b.File.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) Err(p + ".file", "'" + b.File + "' must end in .dwg");
                if (!IsSafeFileName(b.File)) Err(p + ".file", "'" + b.File + "' is not a plain, safe Windows file name");
                else if (!files.Add(b.File)) Err(p + ".file", "'" + b.File + "' is used by two blocks");
                else if (folder != null && !System.IO.File.Exists(Path.Combine(folder, b.File))) Err(p + ".file", "'" + b.File + "' is not in " + folder);
                if (!Finite(b.Min) || !Finite(b.Max)) Err(p + ".extents", "must be finite [x, y, z] points");
                else if (b.Min.Zip(b.Max, (lo, hi) => lo > hi).Any(x => x)) Err(p + ".extents", "min is above max on some axis");

                for (int j = 0; j < b.References.Count; j++)
                {
                    var r = b.References[j];
                    string rp = JsonPath.Index(p + ".references", j);
                    if (!HandleRx.IsMatch(r.Handle)) Err(rp + ".handle", "'" + r.Handle + "' is not an object handle");
                    else if (!handles.Add(r.Handle)) Err(rp + ".handle", "'" + r.Handle + "' is listed twice");
                    if (r.Space.Length == 0) Err(rp + ".space", "is empty");
                    if (r.Layer.Length == 0) Err(rp + ".layer", "is empty");
                    if (!Finite(r.Position)) Err(rp + ".position", "must be a finite [x, y, z] point");
                    if (double.IsNaN(r.RotationDeg) || r.RotationDeg < 0 || r.RotationDeg >= 360) Err(rp + ".rotationDeg", "must be in [0, 360)");
                    if (!Finite(r.Scale) || r.Scale.Any(s => s == 0)) Err(rp + ".scale", "must be three finite, non-zero factors");
                    else if (r.Mirrored != IsMirrored(r.Scale)) Err(rp + ".mirrored", "must be " + (!r.Mirrored).ToString().ToLowerInvariant() + " for scale " + Vec(r.Scale).ToJson());
                }
            }
            return issues;
        }

        private static bool Finite(double[] v) => v.Length == 3 && v.All(x => !double.IsNaN(x) && !double.IsInfinity(x));

        /// <summary>True when the scale has an odd number of negative factors.</summary>
        public static bool IsMirrored(double[] scale) => scale.Count(s => s < 0) % 2 == 1;

        /// <summary>The entry name of the n-th (from 1, first seen) anonymous representation of a dynamic block.</summary>
        public static string AnonymousName(string effectiveName, int n) => effectiveName + "__" + n.ToString(CultureInfo.InvariantCulture);

        public static bool IsAnonymousName(string name, string effectiveName) =>
            name.StartsWith(effectiveName + "__", StringComparison.Ordinal) &&
            int.TryParse(name.Substring(effectiveName.Length + 2), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 &&
            name.Substring(effectiveName.Length + 2) == n.ToString(CultureInfo.InvariantCulture);

        /// <summary>The unit symbol written for an INSUNITS code the jobs know (in, ft, mm, cm, m); null for others.</summary>
        public static string? SymbolFor(int insunits)
        {
            switch (insunits)
            {
                case 1: return "in";
                case 2: return "ft";
                case 4: return "mm";
                case 5: return "cm";
                case 6: return "m";
                default: return null;
            }
        }

        // ------------------------------------------------------------------ file names

        private static readonly char[] WindowsInvalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        /// <summary>
        /// The block name with characters Windows refuses in file names (and control characters) turned into "_". Trailing
        /// dots and spaces, which Windows drops silently, also become "_".
        /// </summary>
        public static string SafeStem(string blockName)
        {
            var c = blockName.Select(ch => ch < 32 || Array.IndexOf(WindowsInvalid, ch) >= 0 ? '_' : ch).ToArray();
            for (int i = c.Length - 1; i >= 0 && (c[i] == '.' || c[i] == ' '); i--) c[i] = '_';
            return c.Length == 0 ? "_" : new string(c);
        }

        /// <summary>True when the part before the first dot is a DOS device name, which Windows opens as the device in any folder.</summary>
        public static bool IsReservedDeviceName(string fileName)
        {
            string stem = fileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9';
        }

        /// <summary>A plain file name Windows accepts as a file in a folder.</summary>
        public static bool IsSafeFileName(string fileName) =>
            fileName.Length > 0 && fileName == SafeStem(fileName) && fileName != "." && fileName != ".." && !IsReservedDeviceName(fileName);

        /// <summary>
        /// DWG file names for block names, in order: the safe stem plus ".dwg", "-2", "-3"... on a case-insensitive
        /// collision. A name whose stem is a device name gets null (refused); <paramref name="reserved"/> lists names
        /// already taken in the folder's output (placements.json).
        /// </summary>
        public static List<string?> AssignFileNames(IEnumerable<string> blockNames, IEnumerable<string>? reserved = null)
        {
            var used = new HashSet<string>(reserved ?? new[] { PlacementsFile.DefaultFileName }, StringComparer.OrdinalIgnoreCase);
            var result = new List<string?>();
            foreach (var name in blockNames)
            {
                string stem = SafeStem(name);
                if (IsReservedDeviceName(stem)) { result.Add(null); continue; }
                string file = stem + ".dwg";
                for (int n = 2; !used.Add(file); n++) file = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".dwg";
                result.Add(file);
            }
            return result;
        }
    }
}
