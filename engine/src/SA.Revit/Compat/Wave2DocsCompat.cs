using System;
using System.Globalization;
using System.Text;

namespace SA.Revit.Compat
{
    /// <summary>
    /// Release differences for the wave-2 documentation ops.
    ///
    /// Reading text files whose encoding Revit does not document (schedule export): 2024 runs on .NET Framework 4.8,
    /// where Encoding.GetEncoding finds the system ANSI code page directly; 2025 to 2027 run on .NET 8 and 10, where
    /// legacy code pages need CodePagesEncodingProvider registered first.
    /// </summary>
    internal static class Wave2DocsCompat
    {
        /// <summary>Text from file bytes: by byte order mark, else strict UTF-8, else the system ANSI code page.</summary>
        public static string Decode(byte[] bytes, out string encoding)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = "utf-8-bom"; return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3); }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = "utf-16le"; return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = "utf-16be"; return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
            try
            {
                encoding = "utf-8";
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException) { }
            encoding = "ansi";
            return Ansi().GetString(bytes);
        }

        private static Encoding Ansi()
        {
#if Y2025_OR_GREATER
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
            catch (Exception) { return Encoding.GetEncoding(1252); }
        }
    }
}
