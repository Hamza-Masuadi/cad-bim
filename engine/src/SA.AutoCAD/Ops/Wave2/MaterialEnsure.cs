using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// material.ensure: render materials in the ACAD_MATERIAL dictionary, created inside the job transaction
    /// (DBDictionary.SetAt and AddNewlyCreatedDBObject) so an aborted rehearsal removes them. New materials get Mode
    /// Realistic, the description, a diffuse colour (Method.Override, factor 1) and an optional image map with a tiled
    /// mapper. An existing material is left alone unless update is true, and is still read back: requested values that
    /// differ are reported as warnings. With update only the given fields change; the rest of the diffuse component is
    /// kept.
    /// </summary>
    internal sealed class MaterialEnsure : OpHandler<MaterialEnsure.Plan>
    {
        public override string Name => "material.ensure";

        internal sealed class Item
        {
            public string Name = "";
            public string? Description;
            public AcColor? Color;
            public string? Texture;
            public double? Width, Height;
            public Projection Projection = Projection.Planar;
            public bool TwoSided = true, TwoSidedGiven;
            public bool Exists;
            public string Action = "";
            public string Handle = "";
        }

        internal sealed class Plan
        {
            public List<Item> Items = new List<Item>();
            public bool Update;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Build(ctx, args, issues);
            foreach (var it in p.Items) ctx.Plan.Add(Materials.PlannedKind, it.Name);
        }

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Update = args.GetBool("update") };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in args.GetObjectList("materials"))
            {
                var it = new Item { Name = m.GetString("name").Trim(), Description = m.GetStringOrNull("description") };
                if (Materials.Reserved.Any(r => string.Equals(r, it.Name, StringComparison.OrdinalIgnoreCase)))
                    OpContext.Error(issues, m, "name", "material_reserved", "'" + it.Name + "' is a built-in material entry and is not changed");
                else if (!AcadValues.IsValidSymbolName(it.Name, out string err))
                    OpContext.Error(issues, m, "name", "name_invalid", err);
                if (!seen.Add(it.Name)) OpContext.Error(issues, m, "name", "name_duplicate", "material '" + it.Name + "' is listed twice");
                if (m.Has("color"))
                {
                    if (AcadValues.TryParseColor(m.Get("color"), false, out var c, out string cerr)) it.Color = c;
                    else OpContext.Error(issues, m, "color", IssueCodes.ColorInvalid, cerr);
                }
                var tex = m.GetObject("texture");
                if (tex != null)
                {
                    it.Texture = Materials.CheckTexture(ctx, tex, issues);
                    it.Width = tex.GetLengthOrNull("width");
                    it.Height = tex.GetLengthOrNull("height");
                    it.Projection = Materials.ToProjection(tex.GetString("projection"));
                    if (ctx.IsValidating && it.Width is null)
                        OpContext.Warning(issues, tex, "width", "texture_size_unset", "no width given: the image repeats at AutoCAD's default size rather than a real-world size");
                }
                it.TwoSided = m.GetBool("twoSided");
                it.TwoSidedGiven = m.Has("twoSided");
                it.Exists = Db.Read(ctx, tr => !Materials.Id(tr, ctx.Database, it.Name).IsNull);
                p.Items.Add(it);
            }
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var map = new JsonObject();
            foreach (var it in p.Items)
            {
                if (it.Exists)
                {
                    var id = Materials.Id(tr, db, it.Name);
                    it.Handle = OpContext.HandleOf(id);
                    if (!p.Update)
                    {
                        it.Action = "unchanged";
                        result.Unchanged.Add(it.Handle);
                    }
                    else
                    {
                        var mat = (Material)tr.GetObject(id, OpenMode.ForWrite);
                        Set(ctx, mat, it, false);
                        it.Action = "updated";
                        result.Modified.Add(it.Handle);
                    }
                }
                else
                {
                    var dict = (DBDictionary)tr.GetObject(db.MaterialDictionaryId, OpenMode.ForWrite);
                    var mat = new Material { Name = it.Name };
                    Set(ctx, mat, it, true);
                    var id = dict.SetAt(it.Name, mat);
                    tr.AddNewlyCreatedDBObject(mat, true);
                    it.Handle = OpContext.HandleOf(id);
                    it.Action = "created";
                    result.Created.Add(it.Handle);
                }
                map.Add(it.Name, it.Handle);
            }
            result.Outputs.Add("materials", map);
            return p;
        }

        private static void Set(OpContext ctx, Material mat, Item it, bool isNew)
        {
            if (isNew) mat.Mode = Mode.Realistic;
            if (it.Description != null) mat.Description = it.Description;
            if (isNew || it.TwoSidedGiven) mat.TwoSided = it.TwoSided;
            if (it.Color is null && it.Texture is null) return;
            var old = mat.Diffuse;
            var colour = it.Color != null ? new MaterialColor(Method.Override, 1.0, it.Color.EntityColor) : (isNew ? new MaterialColor() : old.Color);
            MaterialMap diffuseMap;
            if (it.Texture != null)
            {
                var tex = new ImageFileTexture { SourceFileName = it.Texture };
                diffuseMap = new MaterialMap(Source.File, tex, 1.0, Materials.BuildMapper(ctx, it.Projection, it.Width, it.Height));
            }
            else diffuseMap = isNew ? new MaterialMap() : old.Map;
            mat.Diffuse = new MaterialDiffuseComponent(colour, diffuseMap);
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var list = new JsonArray();
            foreach (var it in p.Items)
            {
                var id = Materials.Id(tr, ctx.Database, it.Name);
                if (id.IsNull) { mismatches.Add("material '" + it.Name + "' is not in the material dictionary"); continue; }
                var mat = (Material)tr.GetObject(id, OpenMode.ForRead);
                var d = mat.Diffuse;
                var colour = AcColor.FromEntityColor(d.Color.Color);
                string? texture = (d.Map?.Texture as ImageFileTexture)?.SourceFileName;
                var rb = new JsonObject().Add("name", mat.Name).Add("handle", OpContext.HandleOf(id)).Add("action", it.Action)
                    .Add("description", mat.Description ?? "").Add("color", d.Color.Method == Method.Override ? AcadValues.ColorJson(colour) : JsonValue.Null)
                    .Add("texture", texture != null ? (JsonValue)texture : JsonValue.Null).Add("twoSided", mat.TwoSided).Add("mode", mat.Mode.ToString());
                if (texture != null && d.Map?.Mapper != null) rb.Add("mapper", Materials.MapperJson(d.Map.Mapper));
                list.Add(rb);

                var why = new List<string>();
                if (!string.Equals(mat.Name, it.Name, StringComparison.OrdinalIgnoreCase)) why.Add("name " + mat.Name);
                if (it.Description != null && (mat.Description ?? "") != it.Description) why.Add("description '" + mat.Description + "'");
                if (it.Color != null && !AcadValues.SameColor(colour, it.Color) && !SameRgb(colour, it.Color)) why.Add("colour " + AcadValues.ColorJson(colour).ToJson());
                if (it.Texture != null && !string.Equals(texture, it.Texture, StringComparison.OrdinalIgnoreCase)) why.Add("texture " + (texture ?? "(none)"));
                if ((it.TwoSidedGiven || it.Action == "created") && mat.TwoSided != it.TwoSided) why.Add("twoSided " + mat.TwoSided);
                if (why.Count == 0) continue;
                if (it.Action == "unchanged") ctx.Warn("material '" + it.Name + "' exists and differs from the request (" + string.Join(", ", why) + "); set update true to change it.");
                else mismatches.Add("material '" + it.Name + "' reads back with " + string.Join(", ", why));
            }
            result.Readback.Add("materials", list);
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }

        /// <summary>An ACI request may be stored as its RGB equivalent.</summary>
        private static bool SameRgb(AcColor a, AcColor b)
        {
            var ea = a.EntityColor; var eb = b.EntityColor;
            int ra = ea.IsByAci ? EntityColor.LookUpRgb((byte)ea.ColorIndex) : ea.TrueColor & 0xFFFFFF;
            int rb = eb.IsByAci ? EntityColor.LookUpRgb((byte)eb.ColorIndex) : eb.TrueColor & 0xFFFFFF;
            return ra == rb;
        }
    }
}
