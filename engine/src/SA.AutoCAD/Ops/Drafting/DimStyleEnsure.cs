using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// dimstyle.ensure: a new style starts as a copy of every dimension variable of basedOn (default ISO-25 in metric
    /// drawings, else Standard), then the settings are applied by system variable name (see <see cref="DimVars"/> for
    /// names, units and refusals). fromStandard fills settings the job omits from the standard's style of the same name
    /// (job settings win). Annotative defaults to true, the professional choice when the standard does not say. An
    /// existing style is left alone (unchanged, differences as a warning) unless update is true. setCurrent sets
    /// DIMSTYLE and copies that style's variables into the drawing (Database.SetDimstyleData) so new dimensions use it
    /// without overrides. A rehearsal does not set the current style (a header value; see doc.setup) and warns instead.
    /// </summary>
    internal sealed class DimStyleEnsure : OpHandler<List<DimStylePlan>>
    {
        public override string Name => "dimstyle.ensure";

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = Plan(ctx, args, issues);
            foreach (var p in plans)
            {
                if (!p.Exists || args.GetBool("update")) DraftState.For(ctx).DimStyleAnnotative[p.Name] = p.Annotative;
                if (!p.Exists) ctx.Plan.Add(PlannedNames.DimStyle, p.Name);
            }
            string? current = args.GetStringOrNull("setCurrent");
            if (current != null && !plans.Any(p => string.Equals(p.Name, current, StringComparison.OrdinalIgnoreCase)) && !DraftDb.DimStyleExists(ctx, current))
                OpContext.Error(issues, args, "setCurrent", "dimstyle_missing", "setCurrent names dimension style '" + current + "', which is not in the drawing or in this op." +
                    Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.SymbolNames(tr, ctx.Database.DimStyleTableId), current)));
        }

        private static List<DimStylePlan> Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = new List<DimStylePlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool fromStandard = args.GetBool("fromStandard");
            double mmPerUnit = ctx.Units.MmPerUnit ?? 1;
            foreach (var item in args.GetObjectList("styles"))
            {
                string name = item.GetString("name");
                if (!seen.Add(name)) { OpContext.Error(issues, item, "name", IssueCodes.DuplicateStyle, "style '" + name + "' is listed more than once"); continue; }
                DimStyleSpec? std = fromStandard ? ctx.Standard?.DimStyles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
                bool annotative = item.Has("annotative") ? item.GetBool("annotative") : std?.Annotative ?? item.GetBool("annotative");
                var sources = new List<DimSettingsSource>();
                if (std != null) sources.Add(new DimSettingsSource(std.Settings, ctx.Standard!.LengthUnit ?? ctx.JobUnit, "the standard's settings for '" + name + "'"));
                var job = item.Get("settings") as JsonObject;
                if (job != null) sources.Add(new DimSettingsSource(job, ctx.JobUnit, "settings"));
                var errors = new List<string>();
                var warnings = new List<string>();
                var p = DimStyles.Plan(ctx, name, item.GetStringOrNull("basedOn"), annotative, sources, mmPerUnit, errors, warnings);
                p.Path = item.Path;
                foreach (var e in errors) OpContext.Error(issues, item, e.StartsWith("base style", StringComparison.Ordinal) ? "basedOn" : "settings", "dimstyle_invalid", e);
                foreach (var w in warnings) OpContext.Warning(issues, item, "settings", "dimstyle_warning", w);
                if (!annotative && !p.Settings.Any(s => s.Name == "DIMSCALE"))
                    OpContext.Warning(issues, item, "annotative", "dimscale_unset", "non-annotative style '" + name + "' sets no DIMSCALE; it keeps the base style's value. Set DIMSCALE to the drawing scale, or make the style annotative.");
                plans.Add(p);
            }
            return plans;
        }

        protected override List<DimStylePlan> Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plans = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            bool update = args.GetBool("update");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var p in plans)
            {
                var existing = Db.SymbolId(tr, db.DimStyleTableId, p.Name);
                if (!existing.IsNull && !update)
                {
                    p.State = "unchanged";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Unchanged.Add(p.Handle);
                    var d = DimStyles.Differences(ctx, tr, existing, p);
                    if (d.Count > 0) ctx.Warn("Dimension style '" + p.Name + "' exists and differs from the request (" + string.Join("; ", d) + "); left unchanged because update is false.");
                    continue;
                }
                if (existing.IsNull)
                {
                    var id = DimStyles.Create(ctx, p);
                    p.State = "created";
                    p.Handle = OpContext.HandleOf(id);
                    result.Created.Add(p.Handle);
                }
                else
                {
                    DimStyles.Update(ctx, p, existing);
                    p.State = "updated";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Modified.Add(p.Handle);
                }
            }
            string? current = args.GetStringOrNull("setCurrent");
            if (current != null)
            {
                var id = Db.SymbolId(tr, db.DimStyleTableId, current);
                if (id.IsNull) throw new InvalidOperationException("Dimension style '" + current + "' for setCurrent does not exist.");
                if (ctx.Mode == RunMode.Rehearse)
                    ctx.Warn("setCurrent was not applied in the rehearsal: DIMSTYLE is a header value, and a transaction abort is not documented to restore it. The commit sets it.");
                else
                {
                    if (db.Dimstyle != id) db.Dimstyle = id;
                    db.SetDimstyleData((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead));
                }
            }
            return plans;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, List<DimStylePlan> plans)
        {
            var tr = ctx.Transaction;
            var list = new JsonArray();
            var mismatches = new List<string>();
            foreach (var p in plans)
            {
                var id = Db.SymbolId(tr, ctx.Database.DimStyleTableId, p.Name);
                if (id.IsNull) { mismatches.Add("dimension style '" + p.Name + "' is not in the drawing after the op"); continue; }
                list.Add(DimStyles.Read(ctx, tr, id, p, mismatches));
            }
            result.Readback.Add("styles", list);
            var cur = (DimStyleTableRecord)tr.GetObject(ctx.Database.Dimstyle, OpenMode.ForRead);
            result.Readback.Add("currentStyle", cur.Name);
            string? current = args.GetStringOrNull("setCurrent");
            if (current != null && ctx.Mode == RunMode.Commit && !string.Equals(cur.Name, current, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the current dimension style is '" + cur.Name + "', not '" + current + "'");
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
