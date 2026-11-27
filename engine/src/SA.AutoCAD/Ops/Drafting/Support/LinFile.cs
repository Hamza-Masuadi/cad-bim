using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>A text element of a complex linetype, attached to the dash before it.</summary>
    internal sealed class LinText
    {
        public string Text = "";
        public string Style = "";
        public double Scale = 1;
        /// <summary>Radians.</summary>
        public double Rotation;
        /// <summary>True for A= (absolute, upright to the UCS), false for R= (relative to the line).</summary>
        public bool Absolute;
        public double X, Y;
    }

    internal sealed class LinDefinition
    {
        public string Name = "";
        public string Description = "";
        public List<double> Dashes = new List<double>();
        /// <summary>Dash index to its text element.</summary>
        public Dictionary<int, LinText> Texts = new Dictionary<int, LinText>();
        /// <summary>Set when the definition uses shapes ([NAME,file.shx,...]), which this engine does not build.</summary>
        public string? ShapeElement;
        public double PatternLength => Dashes.Sum(d => Math.Abs(d));
    }

    /// <summary>
    /// Reads linetype definitions from a .lin file: "*NAME,description" then "A,dash,dash,...". Dashes: positive = dash,
    /// negative = space, 0 = dot. A bracketed element ["TEXT",STYLE,S=..,R=..|A=..,X=..,Y=..] belongs to the dash written
    /// just before it. Shape elements ([SHAPE,file.shx,...]) are recognised and reported, not built.
    /// </summary>
    internal static class LinFile
    {
        internal static LinDefinition? Find(string path, string name, out string error)
        {
            error = "";
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { error = "cannot read " + path + ": " + e.Message; return null; }
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (!l.StartsWith("*", StringComparison.Ordinal)) continue;
                int comma = l.IndexOf(',');
                string n = (comma < 0 ? l.Substring(1) : l.Substring(1, comma - 1)).Trim();
                if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
                var def = new LinDefinition { Name = n, Description = comma < 0 ? "" : l.Substring(comma + 1).Trim() };
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string d = lines[j].Trim();
                    if (d.Length == 0 || d.StartsWith(";", StringComparison.Ordinal)) continue;
                    if (!d.StartsWith("A,", StringComparison.OrdinalIgnoreCase)) { error = "definition of " + n + " does not start with an A, alignment line"; return null; }
                    if (!ParsePattern(d.Substring(2), def, out error)) return null;
                    return def;
                }
                error = "definition of " + n + " has no pattern line";
                return null;
            }
            error = "no linetype '" + name + "' in " + path;
            return null;
        }

        private static bool ParsePattern(string body, LinDefinition def, out string error)
        {
            error = "";
            foreach (var item in SplitTop(body))
            {
                string t = item.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!t.EndsWith("]", StringComparison.Ordinal)) { error = "unterminated element " + t; return false; }
                    var parts = SplitTop(t.Substring(1, t.Length - 2)).Select(x => x.Trim()).ToList();
                    if (parts.Count < 2) { error = "element " + t + " needs a text or shape and a style or file"; return false; }
                    if (!parts[0].StartsWith("\"", StringComparison.Ordinal)) { def.ShapeElement = t; continue; }
                    if (def.Dashes.Count == 0) { error = "text element " + t + " comes before any dash"; return false; }
                    var lt = new LinText { Text = parts[0].Trim('"'), Style = parts[1] };
                    for (int k = 2; k < parts.Count; k++)
                    {
                        string p = parts[k];
                        int eq = p.IndexOf('=');
                        if (eq < 1) { error = "bad element field '" + p + "'"; return false; }
                        string key = p.Substring(0, eq).Trim().ToUpperInvariant();
                        string val = p.Substring(eq + 1).Trim();
                        bool deg = true;
                        if (val.EndsWith("r", StringComparison.OrdinalIgnoreCase)) { deg = false; val = val.Substring(0, val.Length - 1); }
                        else if (val.EndsWith("d", StringComparison.OrdinalIgnoreCase)) val = val.Substring(0, val.Length - 1);
                        if (!double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) { error = "bad number in '" + p + "'"; return false; }
                        switch (key)
                        {
                            case "S": lt.Scale = v; break;
                            case "R": lt.Rotation = deg ? v * Math.PI / 180 : v; lt.Absolute = false; break;
                            case "A": lt.Rotation = deg ? v * Math.PI / 180 : v; lt.Absolute = true; break;
                            case "U": lt.Rotation = deg ? v * Math.PI / 180 : v; lt.Absolute = false; break;
                            case "X": lt.X = v; break;
                            case "Y": lt.Y = v; break;
                            default: error = "unknown element field '" + key + "'"; return false;
                        }
                    }
                    def.Texts[def.Dashes.Count - 1] = lt;
                    continue;
                }
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double dash)) { error = "bad dash length '" + t + "'"; return false; }
                def.Dashes.Add(dash);
            }
            if (def.Dashes.Count == 0) { error = "the pattern has no dashes"; return false; }
            if (def.Dashes.Count > 12) { error = "the pattern has " + def.Dashes.Count + " dashes; AutoCAD allows 12"; return false; }
            return true;
        }

        /// <summary>Splits on commas that are outside brackets and quotes.</summary>
        private static List<string> SplitTop(string s)
        {
            var list = new List<string>();
            int depth = 0;
            bool quote = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') quote = !quote;
                else if (!quote && c == '[') depth++;
                else if (!quote && c == ']') depth--;
                else if (!quote && depth == 0 && c == ',') { list.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            list.Add(s.Substring(start));
            return list;
        }
    }
}
