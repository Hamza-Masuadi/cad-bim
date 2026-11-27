using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    /// <summary>
    /// Grid label sequences: "1" counts 1, 2, 3...; "A" counts A, B ... H, J ... N, P ... Z, AA, AB... skipping I and O
    /// (they read as 1 and 0); a label with a trailing number ("A1", "G-01") counts that number on, keeping its width.
    /// </summary>
    internal static class Labels
    {
        internal static bool TrySequence(string start, int count, out List<string> labels, out string error)
        {
            labels = new List<string>();
            error = "";
            string s = start.Trim();
            if (s.Length == 0) { error = "the first label is empty"; return false; }
            if (IsLetters(s))
            {
                string u = s.ToUpperInvariant();
                if (u.IndexOf('I') >= 0 || u.IndexOf('O') >= 0) { error = "grid letters skip I and O; start from another letter"; return false; }
                long n = LettersToIndex(u);
                for (int i = 0; i < count; i++) labels.Add(IndexToLetters(n + i));
                return true;
            }
            int end = s.Length;
            while (end > 0 && char.IsDigit(s[end - 1])) end--;
            if (end == s.Length) { error = "'" + start + "' does not end in a number or consist of letters; give the lines explicitly"; return false; }
            string prefix = s.Substring(0, end);
            string digits = s.Substring(end);
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long first)) { error = "'" + start + "' has a number that is too long"; return false; }
            for (int i = 0; i < count; i++)
                labels.Add(prefix + (first + i).ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0'));
            return true;
        }

        private static bool IsLetters(string s)
        {
            foreach (char c in s) if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')) return false;
            return true;
        }

        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // 24 letters, no I or O

        private static long LettersToIndex(string s)
        {
            long n = 0;
            foreach (char c in s) n = n * Alphabet.Length + Alphabet.IndexOf(c) + 1;
            return n - 1;
        }

        private static string IndexToLetters(long index)
        {
            var sb = new StringBuilder();
            long n = index + 1;
            while (n > 0)
            {
                long r = (n - 1) % Alphabet.Length;
                sb.Insert(0, Alphabet[(int)r]);
                n = (n - 1) / Alphabet.Length;
            }
            return sb.ToString();
        }

        /// <summary>True when an explicit label uses I or O as a letter label.</summary>
        internal static bool UsesIorO(string label)
        {
            string u = label.Trim().ToUpperInvariant();
            return IsLetters(u) && (u.IndexOf('I') >= 0 || u.IndexOf('O') >= 0);
        }

        internal static bool SameLabel(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
