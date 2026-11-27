using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    /// <summary>
    /// Find and replace on multiline text contents without touching formatting codes. The contents split into literal
    /// runs (with \\, \{ and \} unescaped) and codes kept verbatim: braces, \P paragraph and \N column breaks, \~,
    /// one-letter toggles (\L \l \O \o \K \k), \U+XXXX and \M+NXXXX characters, and the codes that run to a semicolon
    /// (\f \F \H \W \Q \T \A \C \c \p \S). A match never spans a code, so text split by formatting is not found.
    /// </summary>
    internal static class MTextRuns
    {
        private const string ToSemicolon = "fFHWQTACcpS";

        internal static string Replace(string contents, Func<string, string> replaceRun)
        {
            var sb = new StringBuilder();
            var run = new StringBuilder();
            void Flush()
            {
                if (run.Length == 0) return;
                sb.Append(Escape(replaceRun(run.ToString())));
                run.Clear();
            }
            int i = 0;
            while (i < contents.Length)
            {
                char ch = contents[i];
                if (ch == '{' || ch == '}') { Flush(); sb.Append(ch); i++; continue; }
                if (ch != '\\' || i + 1 >= contents.Length) { run.Append(ch); i++; continue; }
                char code = contents[i + 1];
                if (code == '\\' || code == '{' || code == '}') { run.Append(code); i += 2; continue; }
                int end;
                if (ToSemicolon.IndexOf(code) >= 0)
                {
                    int semi = contents.IndexOf(';', i + 2);
                    end = semi < 0 ? contents.Length : semi + 1;
                }
                else if ((code == 'U' || code == 'u') && i + 2 < contents.Length && contents[i + 2] == '+') end = Math.Min(contents.Length, i + 7);
                else if ((code == 'M' || code == 'm') && i + 2 < contents.Length && contents[i + 2] == '+') end = Math.Min(contents.Length, i + 8);
                else end = i + 2;
                Flush();
                sb.Append(contents, i, end - i);
                i = end;
            }
            Flush();
            return sb.ToString();
        }

        private static string Escape(string literal) => literal.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

        /// <summary>The regex for the request: escaped unless regex, wrapped in word boundaries for wholeWord.</summary>
        internal static Regex Pattern(string find, bool regex, bool wholeWord, bool matchCase)
        {
            string core = regex ? find : Regex.Escape(find);
            if (wholeWord) core = @"(?<!\w)(?:" + core + @")(?!\w)";
            var opts = RegexOptions.CultureInvariant | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            return new Regex(core, opts, TimeSpan.FromSeconds(2));
        }
    }
}
