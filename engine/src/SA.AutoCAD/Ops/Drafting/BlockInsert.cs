using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// block.insert: one BlockReference per position in model space, with attribute references for every non-constant
    /// attribute definition (given values by tag, else the default) and optional dynamic properties.
    /// Scale: the uniform scale times the block's unit factor (block INSUNITS to drawing INSUNITS, as INSERT applies;
    /// 1 when either is unitless). Annotative blocks are inserted at the current annotation scale and made annotative.
    /// Dynamic properties are checked against an existing reference of the same block while validating when one
    /// exists, and always against the new reference before they are set (see <see cref="DynProps"/>).
    /// The standard names no block library, so the block must be defined (or defined earlier in the job).
    /// </summary>
    internal sealed class BlockInsert : OpHandler<BlockInsert.InsertPlan>
    {
        public override string Name => "block.insert";

        internal sealed class InsertPlan
        {
            public string Block = "";
            public List<Point2> Positions = new List<Point2>();
            public double RotationDeg;
            public double Scale = 1;
            public double UnitFactor = 1;
            public string Layer = "";
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string, JsonValue>> Dyn = new List<KeyValuePair<string, JsonValue>>();
            public List<string> Handles = new List<string>();
            public List<List<DynValue>> DynValues = new List<List<DynValue>>();
            public bool Annotative;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static InsertPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new InsertPlan
            {
                Block = args.GetString("name"),
                RotationDeg = args.GetAngle("rotation"),
                Scale = args.GetNumber("scale"),
                Attributes = BlockSupport.StringMap(args, "attributes"),
                Dyn = args.GetMap("dynamicProperties"),
            };
            if (args.Has("position")) p.Positions.Add(args.GetPoint2("position"));
            else p.Positions = args.GetPoint2List("positions");
            if (p.Positions.Count == 0) OpContext.Error(issues, args, "positions", "positions_empty", "no insertion points");
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "block references");
            if (!DraftDb.BlockExists(ctx, p.Block)) { OpContext.Error(issues, args, "name", "block_missing", DraftDb.MissingBlock(ctx, p.Block)); return p; }
            BlockSupport.CheckTags(ctx, args, "attributes", p.Block, p.Attributes.Keys, issues);
            if (ctx.IsValidating && DraftState.For(ctx).Blocks.TryGetValue(p.Block, out var planned)) p.Annotative = planned.Annotative;
            Db.Read(ctx, tr =>
            {
                var id = DraftDb.BlockId(tr, ctx.Database, p.Block);
                if (id.IsNull) return 0;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout || btr.IsFromExternalReference)
                    OpContext.Error(issues, args, "name", "block_not_insertable", "'" + p.Block + "' is " + (btr.IsLayout ? "a layout" : "an external reference; use xref.attach"));
                p.Annotative = Anno.IsAnnotative(btr);
                p.UnitFactor = BlockSupport.UnitFactor(ctx.Database, btr);
                if (p.Dyn.Count > 0)
                {
                    if (!btr.IsDynamicBlock) { OpContext.Error(issues, args, "dynamicProperties", "block_not_dynamic", "block '" + p.Block + "' is not a dynamic block"); return 0; }
                    if (ctx.IsValidating)
                    {
                        var sample = BlockSupport.References(tr, btr, out _).FirstOrDefault();
                        if (sample.IsNull)
                            OpContext.Warning(issues, args, "dynamicProperties", "dynprops_deferred", "no reference of '" + p.Block + "' exists yet, so its dynamic properties are checked on the new reference at execution");
                        else
                        {
                            var br = (BlockReference)tr.GetObject(sample, OpenMode.ForRead);
                            foreach (var kv in p.Dyn)
                                if (DynProps.Convert(ctx, br, kv.Key, kv.Value, out string err) is null)
                                    OpContext.Error(issues, args, "dynamicProperties", "dynprop_invalid", "'" + kv.Key + "': " + err);
                        }
                    }
                }
                return 0;
            });
            if (p.Annotative && Anno.Current(ctx.Database) is null) OpContext.Error(issues, args, "name", IssueCodes.AnnotationScaleMissing, "annotative block '" + p.Block + "' needs a current annotation scale");
            if (p.Annotative && Math.Abs(p.Scale - 1) > 1e-12) OpContext.Warning(issues, args, "scale", "annotative_scaled", "annotative block '" + p.Block + "' is sized by the annotation scale; scale " + p.Scale + " multiplies that");
            return p;
        }

        protected override InsertPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var id = DraftDb.BlockId(tr, ctx.Database, p.Block);
            if (id.IsNull) throw new InvalidOperationException("Block '" + p.Block + "' is missing.");
            if (p.Annotative) Anno.UseScale(ctx, Anno.CurrentName(ctx.Database));
            double s = p.Scale * p.UnitFactor;
            foreach (var pos in p.Positions)
            {
                var br = BlockSupport.Insert(ctx, id, ctx.Units.ToDrawing(pos), p.RotationDeg * Math.PI / 180, new Scale3d(s, s, s), p.Layer, p.Attributes);
                var values = new List<DynValue>();
                if (p.Dyn.Count > 0)
                {
                    var errors = new List<string>();
                    foreach (var kv in p.Dyn)
                    {
                        var v = DynProps.Convert(ctx, br, kv.Key, kv.Value, out string err);
                        if (v is null) errors.Add("'" + kv.Key + "': " + err);
                        else values.Add(v);
                    }
                    if (errors.Count > 0) throw new InvalidOperationException("Dynamic properties of '" + p.Block + "': " + string.Join("; ", errors));
                    DynProps.Apply(br, values);
                }
                p.DynValues.Add(values);
                string h = OpContext.HandleOf(br.ObjectId);
                p.Handles.Add(h);
                result.Created.Add(h);
            }
            result.Outputs.Add("handles", JsonArray.FromStrings(p.Handles));
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, InsertPlan p)
        {
            var tr = ctx.Transaction;
            var mismatches = new List<string>();
            var list = new JsonArray();
            double s = p.Scale * p.UnitFactor;
            for (int i = 0; i < p.Handles.Count; i++)
            {
                var id = ctx.IdOf(p.Handles[i]);
                if (id.IsNull) { mismatches.Add("reference " + p.Handles[i] + " is not in the drawing after the op"); continue; }
                var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                var pos = ctx.Units.ToJob(br.Position);
                var attrs = BlockSupport.Read(tr, br);
                var dyn = new JsonObject();
                foreach (var prop in DynProps.Properties(br)) if (prop.PropertyName != "Origin") dyn.Set(prop.PropertyName, DynProps.ToJson(ctx, prop, br));
                var attrJson = new JsonObject();
                foreach (var kv in attrs) attrJson.Set(kv.Key, kv.Value);
                string name = BlockSupport.EffectiveName(tr, br);
                list.Add(new JsonObject().Add("handle", p.Handles[i]).Add("block", name).Add("position", DrawingUnits.PointJson(pos))
                    .Add("rotation", AcadValues.Round(br.Rotation * 180 / Math.PI)).Add("scale", AcadValues.Round(br.ScaleFactors.X)).Add("layer", br.Layer)
                    .Add("annotative", Anno.IsAnnotative(br)).Add("attributes", attrJson).Add("dynamicProperties", dyn));
                string label = "reference " + p.Handles[i];
                if (!string.Equals(name, p.Block, StringComparison.OrdinalIgnoreCase)) mismatches.Add(label + " is of block '" + name + "'");
                if (pos.DistanceTo(p.Positions[i]) > ctx.LengthTolerance) mismatches.Add(label + " is at " + pos + ", expected " + p.Positions[i]);
                if (!SA.Core.Units.Tolerance.AngleEqual(br.Rotation * 180 / Math.PI, p.RotationDeg, 1e-6)) mismatches.Add(label + ": rotation reads back as " + br.Rotation * 180 / Math.PI);
                if (Math.Abs(br.ScaleFactors.X - s) > 1e-9 * Math.Max(1, s)) mismatches.Add(label + ": scale reads back as " + br.ScaleFactors.X + ", expected " + s);
                if (!string.Equals(br.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add(label + " is on layer '" + br.Layer + "'");
                foreach (var kv in p.Attributes)
                    if (!attrs.TryGetValue(kv.Key.ToUpperInvariant(), out var got) || got != kv.Value) mismatches.Add(label + ": attribute " + kv.Key + " reads back as '" + (got ?? "(missing)") + "'");
                foreach (var v in p.DynValues[i])
                {
                    var prop = DynProps.Find(br, v.Name);
                    if (prop is null) mismatches.Add(label + ": dynamic property " + v.Name + " is gone");
                    else if (!DynProps.Same(prop, v)) mismatches.Add(label + ": dynamic property " + v.Name + " reads back as " + DynProps.ToJson(ctx, prop, br) + ", requested " + v.Requested + " (AutoCAD may have clamped it to the parameter's range or increments)");
                }
            }
            result.Readback.Add("references", list).Add("unitFactor", AcadValues.Round(p.UnitFactor));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
