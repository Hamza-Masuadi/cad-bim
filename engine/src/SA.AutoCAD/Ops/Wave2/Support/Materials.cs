using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2.Support
{
    /// <summary>Render material lookups and texture mappers shared by material.ensure and material.assign.</summary>
    internal static class Materials
    {
        internal const string PlannedKind = "material";

        internal static readonly string[] Reserved = { "Global", "ByLayer", "ByBlock" };

        /// <summary>The material's id in the ACAD_MATERIAL dictionary (Global, ByLayer and ByBlock are entries too), or Null.</summary>
        internal static ObjectId Id(Transaction tr, Database db, string name) => DraftDb.DictionaryEntry(tr, db.MaterialDictionaryId, name);

        internal static bool Exists(OpContext ctx, string name) =>
            (ctx.IsValidating && ctx.Plan.Contains(PlannedKind, name)) || Db.Read(ctx, tr => !Id(tr, ctx.Database, name).IsNull);

        internal static Projection ToProjection(string p)
        {
            switch (p)
            {
                case "box": return Projection.Box;
                case "cylinder": return Projection.Cylinder;
                case "sphere": return Projection.Sphere;
                default: return Projection.Planar;
            }
        }

        internal static string ProjectionName(Projection p) =>
            p == Projection.Box ? "box" : p == Projection.Cylinder ? "cylinder" : p == Projection.Sphere ? "sphere" : p == Projection.Planar ? "planar" : p.ToString();

        /// <summary>
        /// A tiled mapper whose transform scales texture space by 1/width and 1/height (drawing units), so one image
        /// repeat spans width x height. This reading of Mapper.Transform is not documented; readback reports the
        /// stored transform without judging it.
        /// </summary>
        internal static Mapper BuildMapper(OpContext ctx, Projection projection, double? jobWidth, double? jobHeight)
        {
            var m = Matrix3d.Identity;
            if (jobWidth.HasValue)
            {
                double w = ctx.Units.ToDrawing(jobWidth.Value), h = ctx.Units.ToDrawing(jobHeight ?? jobWidth.Value);
                m = new Matrix3d(new[] { 1 / w, 0, 0, 0, 0, 1 / h, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });
            }
            return new Mapper(projection, Tiling.Tile, Tiling.Tile, AutoTransform.TransformObject, m);
        }

        internal static JsonObject MapperJson(Mapper m)
        {
            var t = m.Transform;
            var cells = new JsonArray();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) cells.Add(AcadValues.Round(t[r, c]));
            return new JsonObject().Add("projection", ProjectionName(m.Projection)).Add("autoTransform", m.AutoTransform.ToString()).Add("transform", cells);
        }

        /// <summary>
        /// D5: the texture must exist on this machine (absolute, or found on AutoCAD's search paths), and a path under a
        /// user profile (\Users\name\, Desktop, Downloads) is refused unless allowLocalPath. Returns the path to store.
        /// </summary>
        internal static string? CheckTexture(OpContext ctx, OpArgs tex, List<Issue> issues)
        {
            string path = tex.GetString("path").Trim();
            if (ctx.IsRef(tex, "path")) return path;
            string? resolved = null;
            try
            {
                if (Path.IsPathRooted(path)) resolved = File.Exists(path) ? Path.GetFullPath(path) : null;
                else resolved = Db.FindSupportFile(ctx.Database, path);
            }
            catch (Exception) { resolved = null; }
            if (resolved is null)
            {
                OpContext.Error(issues, tex, "path", "texture_missing", "texture '" + path + "' does not exist on this machine" + (Path.IsPathRooted(path) ? "" : " or on AutoCAD's support and texture search paths"));
                return null;
            }
            if (!tex.GetBool("allowLocalPath") && IsUserProfilePath(resolved))
                OpContext.Error(issues, tex, "path", "texture_local_path", "texture '" + resolved + "' is under a user profile; other machines will not find it. Put it in the project texture folder, or set allowLocalPath true");
            else if (ctx.IsValidating && resolved.Length > 1 && resolved[1] == ':')
                OpContext.Warning(issues, tex, "path", "texture_drive_path", "texture '" + resolved + "' is on a drive letter; a shared (UNC) project path travels better");
            return Path.IsPathRooted(path) ? resolved : path;
        }

        internal static bool IsUserProfilePath(string path)
        {
            string p = path.Replace('/', '\\');
            if (p.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("\\Documents and Settings\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0 && p.StartsWith(profile.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return p.IndexOf("\\Desktop\\", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("\\Downloads\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
