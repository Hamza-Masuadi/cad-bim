using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// material.assign: sets LayerTableRecord.MaterialId on layers, or Entity.MaterialId (Entity.Material for ByLayer and
    /// ByBlock) on entities, with an optional tiled mapper on entities. Xref-dependent layers are refused, since the
    /// change would not be saved back to the xref. Filtered entities on locked layers are skipped and counted; listed
    /// ones are an error.
    /// </summary>
    internal sealed class MaterialAssign : OpHandler<MaterialAssign.Plan>
    {
        public override string Name => "material.assign";

        internal sealed class Plan
        {
            public string Material = "";
            /// <summary>"layers" or "entities".</summary>
            public string Target = "";
            public List<ObjectId> Ids = new List<ObjectId>();
            public int SkippedLocked;
            public bool Deferred;
            public Projection? Projection;
            public double? Width, Height;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static bool IsByName(string m) =>
            string.Equals(m, "ByLayer", StringComparison.OrdinalIgnoreCase) || string.Equals(m, "ByBlock", StringComparison.OrdinalIgnoreCase);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Material = args.GetString("material").Trim() };
            p.Target = args.Has("layers") ? "layers" : "entities";
            bool byName = IsByName(p.Material);
            if (byName) p.Material = string.Equals(p.Material, "ByLayer", StringComparison.OrdinalIgnoreCase) ? "ByLayer" : "ByBlock";
            if (p.Target == "layers" && byName)
                OpContext.Error(issues, args, "material", "material_invalid", "a layer cannot take " + p.Material + "; give a material name");
            else if (!byName && !ctx.IsRef(args, "material") && !Materials.Exists(ctx, p.Material))
                OpContext.Error(issues, args, "material", "material_missing", "material '" + p.Material + "' is not in the drawing; create it with material.ensure first");

            var mapping = args.GetObject("mapping");
            if (mapping != null)
            {
                if (p.Target == "layers") OpContext.Error(issues, args, "mapping", "field_not_used", "mapping applies to entities; layers have no mapper");
                p.Projection = Materials.ToProjection(mapping.GetString("projection"));
                p.Width = mapping.GetLength("width");
                p.Height = mapping.GetLengthOrNull("height");
            }

            if (p.Target == "layers")
            {
                var names = args.GetStringList("layers");
                Db.Read(ctx, tr =>
                {
                    var lt = (LayerTable)tr.GetObject(ctx.Database.LayerTableId, OpenMode.ForRead);
                    var all = new List<LayerTableRecord>();
                    foreach (ObjectId id in lt) if (!id.IsErased) all.Add((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead));
                    foreach (var n in names)
                    {
                        var rx = SheetDb.Wildcard(n);
                        var hits = all.Where(l => rx.IsMatch(l.Name)).ToList();
                        bool planned = !SheetDb.HasWildcard(n) && ctx.IsValidating && ctx.Plan.Contains(PlannedNames.Layer, n);
                        if (hits.Count == 0 && !planned)
                            OpContext.Error(issues, args, "layers", IssueCodes.LayerMissing, "no layer matches '" + n + "'");
                        foreach (var l in hits)
                        {
                            if (l.IsDependent)
                            {
                                if (!SheetDb.HasWildcard(n)) OpContext.Error(issues, args, "layers", "layer_xref", "layer '" + l.Name + "' belongs to an xref; change the material in the xref drawing");
                                continue;
                            }
                            if (!p.Ids.Contains(l.ObjectId)) p.Ids.Add(l.ObjectId);
                        }
                    }
                    return 0;
                });
                return p;
            }
            var s = Targets.Select(ctx, args, issues, lockedIsError: false);
            p.Ids = s.Ids;
            p.SkippedLocked = s.SkippedLocked;
            p.Deferred = s.Deferred;
            return p;
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var matId = IsByName(p.Material) ? ObjectId.Null : Materials.Id(tr, ctx.Database, p.Material);
            if (!IsByName(p.Material) && matId.IsNull) throw new InvalidOperationException("Material '" + p.Material + "' does not exist.");
            var targets = new JsonArray();
            foreach (var id in p.Ids)
            {
                var o = tr.GetObject(id, OpenMode.ForWrite);
                if (o is LayerTableRecord l) l.MaterialId = matId;
                else if (o is Entity e)
                {
                    if (matId.IsNull) e.Material = p.Material;
                    else e.MaterialId = matId;
                    if (p.Projection.HasValue) e.MaterialMapper = Materials.BuildMapper(ctx, p.Projection.Value, p.Width, p.Height);
                }
                result.Modified.Add(OpContext.HandleOf(id));
                targets.Add(OpContext.HandleOf(id));
            }
            result.Outputs.Add("count", p.Ids.Count).Add("targets", targets);
            if (p.SkippedLocked > 0) ctx.Warn(p.SkippedLocked + " entities on locked layers were skipped.");
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            int bad = 0;
            string first = "";
            JsonObject? mapper = null;
            foreach (var id in p.Ids)
            {
                var o = tr.GetObject(id, OpenMode.ForRead);
                string got;
                if (o is LayerTableRecord l) got = l.MaterialId.IsNull ? "" : ((Material)tr.GetObject(l.MaterialId, OpenMode.ForRead)).Name;
                else
                {
                    var e = (Entity)o;
                    got = e.Material ?? "";
                    if (p.Projection.HasValue && mapper is null && e.MaterialMapper != null) mapper = Materials.MapperJson(e.MaterialMapper);
                }
                if (!string.Equals(got, p.Material, StringComparison.OrdinalIgnoreCase))
                {
                    bad++;
                    if (first.Length == 0) first = OpContext.HandleOf(id) + " has '" + got + "'";
                }
            }
            result.Readback.Add("material", p.Material).Add("count", p.Ids.Count).Add("matching", p.Ids.Count - bad).Add("skippedLocked", p.SkippedLocked);
            if (mapper != null) result.Readback.Add("mapper", mapper);
            return bad == 0 || Mismatch(result, bad + " " + p.Target + " read back with another material (first " + first + ")");
        }
    }
}
