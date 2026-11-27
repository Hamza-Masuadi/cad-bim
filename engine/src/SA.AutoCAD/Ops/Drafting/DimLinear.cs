using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// dim.linear: one AlignedDimension (orientation aligned) or RotatedDimension (horizontal: rotation 0, vertical:
    /// rotation 90 degrees) in model space. The dimension line lies at offset from the measured points, positive to the
    /// left looking from start to end (for horizontal and vertical, along the measured axis in the direction of the
    /// points). Style and layer as validated; no dimension variable overrides; textOverride is refused unless it keeps
    /// the measured value (see <see cref="DimCommon.CheckOverride"/>). Not associative (see <see cref="DimCommon"/>).
    /// </summary>
    internal sealed class DimLinear : OpHandler<DimLinear.LinearPlan>
    {
        public override string Name => "dim.linear";

        internal sealed class LinearPlan
        {
            public DimStyleChoice Style = new DimStyleChoice();
            public string Layer = "";
            public DimPlanItem Item = new DimPlanItem();
            public string? Override;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static LinearPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new LinearPlan();
            var a = args.GetPoint2("start");
            var b = args.GetPoint2("end");
            double offset = args.GetLength("offset");
            string orientation = args.GetString("orientation");
            if (!DimCommon.Direction(a, b, orientation, ctx.LengthTolerance, out var dir, out string err))
                OpContext.Error(issues, args, "end", "dimension_zero", err);
            if (Math.Abs(offset) <= ctx.LengthTolerance)
                OpContext.Warning(issues, args, "offset", "offset_zero", "an offset of 0 puts the dimension line on the measured points");
            var n = dir.PerpLeft;
            p.Item = new DimPlanItem
            {
                P1 = a, P2 = b,
                LinePoint = a + n * offset + dir * ((b - a).Dot(dir) / 2),
                Rotation = orientation == "aligned" ? (double?)null : orientation == "horizontal" ? 0.0 : Math.PI / 2,
                Expected = Math.Abs((b - a).Dot(dir)),
            };
            p.Style = DimCommon.Style(ctx, args, issues);
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "dimensions");
            p.Override = DimCommon.CheckOverride(ctx, args, issues);
            return p;
        }

        protected override LinearPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var styleId = Db.SymbolId(tr, ctx.Database.DimStyleTableId, p.Style.Name);
            if (styleId.IsNull) throw new InvalidOperationException("Dimension style '" + p.Style.Name + "' is missing.");
            if (p.Style.Annotative) Anno.UseScale(ctx, p.Style.Scale);
            var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForWrite);
            result.Created.Add(DimCommon.Create(ctx, ms, p.Item, styleId, p.Layer, p.Style.Annotative, p.Override));
            return p;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, LinearPlan p)
        {
            var mismatches = new List<string>();
            var list = new JsonArray();
            double? m = DimCommon.Verify(ctx, p.Item, p.Style.Name, p.Layer, p.Style.Annotative, p.Override, mismatches, list);
            if (list.Count > 0) result.Readback = (JsonObject)list[0];
            if (m.HasValue) result.Outputs.Add("measurement", AcadValues.Round(m.Value));
            foreach (var s in mismatches) Mismatch(result, s);
            return mismatches.Count == 0;
        }
    }
}
