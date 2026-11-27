using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using SA.Core.Protocol;

namespace SA.AutoCAD.Host
{
    /// <summary>
    /// Finds the file a job's "standard" names. An absolute path is used as is. A relative path is tried, in order,
    /// against: the drawing's folder (saved drawings only), %LOCALAPPDATA%\CadBim\, and the bundle's Contents
    /// folder (so "data/standards/x.json" finds Contents\data\standards\x.json). The first existing file wins.
    /// </summary>
    internal static class StandardResolver
    {
        internal static IEnumerable<string> Candidates(string path, Document? doc)
        {
            if (Path.IsPathRooted(path)) { yield return path; yield break; }
            string rel = path.Replace('/', Path.DirectorySeparatorChar);
            if (doc != null && doc.IsNamedDrawing)
            {
                string? dir = null;
                try { dir = Path.GetDirectoryName(doc.Name); } catch { }
                if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir!, rel);
            }
            yield return Path.Combine(SaPaths.Root(), rel);
            string? asmDir = null;
            try { asmDir = Path.GetDirectoryName(typeof(StandardResolver).Assembly.Location); } catch { }
            if (!string.IsNullOrEmpty(asmDir)) yield return Path.GetFullPath(Path.Combine(asmDir!, "..", rel));
        }

        internal static string? Resolve(string path, Document? doc)
        {
            foreach (var c in Candidates(path, doc))
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); }
                catch (Exception) { }
            }
            return null;
        }
    }
}
