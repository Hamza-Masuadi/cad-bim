using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// doc.setup: drawing-level settings (INSUNITS, LUNITS, LUPREC, AUNITS, AUPREC, ANGBASE, ANGDIR, MEASUREMENT,
    /// LTSCALE, PSLTSCALE, MSLTSCALE), annotation scales and CANNOSCALE, and optionally everything the job's standard
    /// defines (see <see cref="StandardApply"/>). Only the settings the job gives are changed; omitted ones are left alone.
    /// In commit mode a value written here is dropped from the job's system variable scope, so the end-of-job restore
    /// cannot put back a value an earlier op of the same job switched temporarily (CANNOSCALE for annotative objects).
    ///
    /// How header values are written: in commit mode through the Database properties (they persist and are in the
    /// job's undo step); in rehearse mode through the job's system variable scope, which the runner restores in a
    /// finally block, because a transaction abort is not documented to restore header variables.
    ///
    /// Annotation scales are added to the drawing's scale list (ObjectContextCollection.AddContext), with
    /// PaperUnits 1 and DrawingUnits = ratio x paper unit (mm or in by MEASUREMENT) / drawing unit, so "1:100" is 100
    /// in a millimetre drawing and 0.1 in a metre drawing. A scale that is already listed by name, or with the same
    /// ratio, is reported as existing.
    ///
    /// Changing INSUNITS must be the last length-bearing change of a job: later ops convert lengths with the units read
    /// when the job started, so any op after a units change (other than layer.ensure and linetype.ensure) is refused.
    /// </summary>
    internal sealed class DocSetup : OpHandler<DocSetup.SetupPlan>
    {
        public override string Name => "doc.setup";

        public override bool AllowsUnitlessDrawing => true;

        internal sealed class VarPlan
        {
            public string Name = "";
            public object Before = "";
            public object After = "";
            public bool Changes;
            public Action<Database> Commit = _ => { };
            public object SysVarValue = 0;
            public Func<Database, object> Read = _ => 0;
        }

        internal sealed class ScalePlan
        {
            public string Name = "";
            public double DrawingPerPaper;
            public bool Exists;
            public string? ExistingName;
        }

        internal sealed class SetupPlan
        {
            public List<VarPlan> Vars = new List<VarPlan>();
            public List<ScalePlan> Scales = new List<ScalePlan>();
            public string? CurrentScale;
            public string? CannoBefore;
            public double MmPerDrawingUnit = 1;
            public List<StdItem>? Standard;
            public LengthUnit StdUnit;
        }

        private static readonly Dictionary<string, int> InsUnitCodes = new Dictionary<string, int> { { "in", 1 }, { "ft", 2 }, { "mm", 4 }, { "cm", 5 }, { "m", 6 } };
        private static readonly Dictionary<string, int> LengthFormats = new Dictionary<string, int> { { "scientific", 1 }, { "decimal", 2 }, { "engineering", 3 }, { "architectural", 4 }, { "fractional", 5 } };
        private static readonly Dictionary<string, int> AngleFormats = new Dictionary<string, int> { { "decimal_degrees", 0 }, { "dms", 1 }, { "grads", 2 }, { "radians", 3 }, { "surveyor", 4 } };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plan = Plan(ctx, args, issues);
            var state = DraftState.For(ctx);
            foreach (var s in plan.Scales)
                if (!s.Exists) { ctx.Plan.Add(PlannedNames.AnnotationScale, s.Name); state.AnnotationScales[s.Name] = s.DrawingPerPaper; }
            if (plan.CurrentScale != null) state.CurrentAnnotationScale = plan.CurrentScale;
            if (plan.Standard != null)
                foreach (var it in plan.Standard)
                {
                    if (it.Exists) continue;
                    switch (it.Kind)
                    {
                        case "layer": ctx.Plan.Add(PlannedNames.Layer, it.Name); break;
                        case "linetype": ctx.Plan.Add(PlannedNames.Linetype, it.Name); break;
                        case "textstyle": ctx.Plan.Add(PlannedNames.TextStyle, it.Name); break;
                        case "dimstyle": ctx.Plan.Add(PlannedNames.DimStyle, it.Name); state.DimStyleAnnotative[it.Name] = it.DimStyle?.Annotative ?? false; break;
                    }
                }

            var units = plan.Vars.FirstOrDefault(v => v.Name == "INSUNITS");
            if (units != null && units.Changes)
            {
                var later = ctx.Job.Ops.Where(o => o.Index > ctx.CurrentOp.Index && o.Op != "layer.ensure" && o.Op != "linetype.ensure" && o.Op != "doc.setup").ToList();
                if (later.Count > 0)
                    OpContext.Error(issues, args, "insUnits", "units_change_not_last", "changing INSUNITS from " + units.Before + " to " + units.After +
                        " must be done in its own job: ops after it (" + string.Join(", ", later.Select(o => o.Id).Take(5)) + ") would convert lengths with the old units");
            }
        }

        private static SetupPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var db = ctx.Database;
            var p = new SetupPlan();
            int currentUnits = (int)db.Insunits;
            int targetUnits = currentUnits;
            if (args.Has("insUnits"))
            {
                string u = args.GetString("insUnits");
                targetUnits = InsUnitCodes[u];
                if (Length.Symbol(ctx.JobUnit) != u)
                    OpContext.Warning(issues, args, "insUnits", "units_differ_from_job", "INSUNITS " + u + " differs from the job's length unit " + Length.Symbol(ctx.JobUnit) + "; lengths in later jobs will be converted");
                if (currentUnits != 0 && currentUnits != targetUnits)
                    OpContext.Warning(issues, args, "insUnits", "units_relabel", "INSUNITS changes from " + DrawingUnits.NameOf(currentUnits) + " to " + DrawingUnits.NameOf(targetUnits) +
                        " without scaling the contents; use fix.units with scaleContents if the geometry was drawn in the wrong unit");
                int code = targetUnits;
                AddVar(p, "INSUNITS", currentUnits, code, d => d.Insunits = (UnitsValue)code, (short)code, d => (int)d.Insunits);
            }
            var mm = DrawingUnits.MillimetresPer(targetUnits);
            if (!mm.HasValue)
            {
                if (targetUnits == 0 && (args.GetBool("applyStandard") || args.Has("annotationScales")))
                    OpContext.Error(issues, args, "insUnits", IssueCodes.UnitsUnitless, "the drawing is unitless; give insUnits so text heights and scales can be converted");
                mm = 1;
            }
            p.MmPerDrawingUnit = mm.Value;

            if (args.Has("lengthFormat")) { int v = LengthFormats[args.GetString("lengthFormat")]; AddVar(p, "LUNITS", db.Lunits, v, d => d.Lunits = v, (short)v, d => d.Lunits); }
            if (args.Has("lengthPrecision"))
            {
                int v = args.GetInt("lengthPrecision");
                AddVar(p, "LUPREC", db.Luprec, v, d => d.Luprec = v, (short)v, d => d.Luprec);
                if (v > 0 && targetUnits == 4 && (!args.Has("lengthFormat") || args.GetString("lengthFormat") == "decimal"))
                    OpContext.Warning(issues, args, "lengthPrecision", "precision_too_fine", "decimals of a millimetre are finer than anything built (AC-011)");
            }
            if (args.Has("angleFormat")) { int v = AngleFormats[args.GetString("angleFormat")]; AddVar(p, "AUNITS", db.Aunits, v, d => d.Aunits = v, (short)v, d => d.Aunits); }
            if (args.Has("anglePrecision")) { int v = args.GetInt("anglePrecision"); AddVar(p, "AUPREC", db.Auprec, v, d => d.Auprec = v, (short)v, d => d.Auprec); }
            if (args.Has("angleBase"))
            {
                double angBase = args.GetAngle("angleBase") * Math.PI / 180.0;
                AddVar(p, "ANGBASE", db.Angbase, angBase, d => d.Angbase = angBase, angBase, d => d.Angbase);
            }
            if (args.Has("angleDirection"))
            {
                bool cw = args.GetString("angleDirection") == "cw";
                AddVar(p, "ANGDIR", db.Angdir ? 1 : 0, cw ? 1 : 0, d => d.Angdir = cw, (short)(cw ? 1 : 0), d => d.Angdir ? 1 : 0);
            }
            if (args.Has("measurement"))
            {
                bool metric = args.GetString("measurement") == "metric";
                int v = metric ? 1 : 0;
                AddVar(p, "MEASUREMENT", db.Measurement == MeasurementValue.Metric ? 1 : 0, v, d => d.Measurement = metric ? MeasurementValue.Metric : MeasurementValue.English, (short)v,
                    d => d.Measurement == MeasurementValue.Metric ? 1 : 0);
                bool imperialUnits = targetUnits == 1 || targetUnits == 2;
                if (metric == imperialUnits && targetUnits != 0)
                    OpContext.Warning(issues, args, "measurement", "measurement_mismatch", "MEASUREMENT " + args.GetString("measurement") + " does not match INSUNITS " + DrawingUnits.NameOf(targetUnits) + "; hatch and linetype files will not match the units");
            }
            if (args.Has("ltScale")) { double v = args.GetNumber("ltScale"); AddVar(p, "LTSCALE", db.Ltscale, v, d => d.Ltscale = v, v, d => d.Ltscale); }
            if (args.Has("psLtScale"))
            {
                bool ps = args.GetBool("psLtScale");
                AddVar(p, "PSLTSCALE", db.Psltscale ? 1 : 0, ps ? 1 : 0, d => d.Psltscale = ps, (short)(ps ? 1 : 0), d => d.Psltscale ? 1 : 0);
            }
            if (args.Has("msLtScale"))
            {
                bool msl = args.GetBool("msLtScale");
                AddVar(p, "MSLTSCALE", db.MsLtScale ? 1 : 0, msl ? 1 : 0, d => d.MsLtScale = msl, (short)(msl ? 1 : 0), d => d.MsLtScale ? 1 : 0);
            }

            // Annotation scales.
            var coll = AnnotationScales.Collection(db);
            var existing = new List<AnnotationScale>();
            if (coll != null) foreach (ObjectContext c in coll) if (c is AnnotationScale s) existing.Add(s);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = args.GetStringList("annotationScales");
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].Trim();
                string path = JsonPath.Index(JsonPath.Key(args.Path, "annotationScales"), i);
                if (!seen.Add(name)) continue;
                if (!Scale.TryParseRatio(name, out double ratio)) { issues.Add(new Issue(path, IssueCodes.ScaleInvalid, "'" + name + "' is not a scale such as 1:100 or 1/4\" = 1'-0\"")); continue; }
                double dpp = ratio * Anno.PaperUnitMm(db) / p.MmPerDrawingUnit;
                if (args.Has("measurement")) dpp = ratio * (args.GetString("measurement") == "metric" ? 1.0 : 25.4) / p.MmPerDrawingUnit;
                var sp = new ScalePlan { Name = name, DrawingPerPaper = dpp };
                var byName = existing.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                var byRatio = existing.FirstOrDefault(s => Math.Abs(Anno.DrawingPerPaper(s) - dpp) <= 1e-9 * Math.Max(1, dpp));
                if (byName != null)
                {
                    sp.Exists = true;
                    sp.ExistingName = byName.Name;
                    if (Math.Abs(Anno.DrawingPerPaper(byName) - dpp) > 1e-9 * Math.Max(1, dpp))
                        issues.Add(new Issue(path, "scale_conflict", "the drawing's scale '" + byName.Name + "' is " + byName.PaperUnits + ":" + byName.DrawingUnits + ", not " + name + " for these units; fix it in the scale list", IssueSeverity.Warning));
                }
                else if (byRatio != null) { sp.Exists = true; sp.ExistingName = byRatio.Name; }
                p.Scales.Add(sp);
            }
            if (ctx.Standard != null && list.Count > 0)
                foreach (var s in p.Scales)
                    if (ctx.Standard.Scales.Count > 0 && ctx.Standard.FindScale(s.Name) is null && !ctx.Standard.Scales.Any(x => Scale.TryParseRatio(s.Name, out double r) && Math.Abs(x.Ratio - r) < 1e-9))
                        OpContext.Warning(issues, args, "annotationScales", "scale_not_in_standard", "scale " + s.Name + " is not in the standard's scale list (AC-036)");

            string? current = args.GetStringOrNull("currentAnnotationScale");
            p.CannoBefore = Anno.CurrentName(db);
            if (current != null)
            {
                var inList = p.Scales.FirstOrDefault(s => string.Equals(s.Name, current, StringComparison.OrdinalIgnoreCase));
                if (inList != null) p.CurrentScale = inList.ExistingName ?? inList.Name;
                else
                {
                    var e = AnnotationScales.Find(db, current);
                    if (e is null && !(ctx.IsValidating && ctx.Plan.Contains(PlannedNames.AnnotationScale, current)))
                        OpContext.Error(issues, args, "currentAnnotationScale", IssueCodes.AnnotationScaleMissing, "scale " + current + " is not in the drawing's scale list; add it in annotationScales." +
                            DraftDb.Suggest(existing.Select(s => s.Name), current));
                    p.CurrentScale = e?.Name ?? current;
                }
            }

            if (args.GetBool("applyStandard"))
            {
                if (ctx.Standard is null) OpContext.Error(issues, args, "applyStandard", IssueCodes.StandardMissing, "applyStandard needs the job to name a standard");
                else
                {
                    var errors = new List<string>();
                    var warnings = new List<string>();
                    p.StdUnit = ctx.Standard.LengthUnit ?? ctx.JobUnit;
                    p.Standard = StandardApply.Plan(ctx, ctx.Standard, p.MmPerDrawingUnit, errors, warnings);
                    foreach (var e in errors) OpContext.Error(issues, args, "applyStandard", IssueCodes.StandardInvalid, e);
                    foreach (var w in warnings) OpContext.Warning(issues, args, "applyStandard", "standard_warning", w);
                }
            }
            return p;
        }

        private static void AddVar(SetupPlan p, string name, object before, object after, Action<Database> commit, object sysvarValue, Func<Database, object> read)
        {
            p.Vars.Add(new VarPlan
            {
                Name = name, Before = before, After = after, Commit = commit, SysVarValue = sysvarValue, Read = read,
                Changes = !Same(before, after),
            });
        }

        private static bool Same(object a, object b)
        {
            if (a is double da && b is double db) return Math.Abs(da - db) <= 1e-12;
            return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture);
        }

        protected override SetupPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var db = ctx.Database;
            bool rehearse = ctx.Mode == RunMode.Rehearse;
            var changed = new JsonObject();
            foreach (var v in p.Vars)
            {
                if (!v.Changes) continue;
                if (rehearse) ctx.SetSysVar(v.Name, v.SysVarValue);
                else { v.Commit(db); ctx.SysVarScope.Keep(v.Name); }
                changed.Set(v.Name, new JsonObject().Add("before", ToJson(v.Before)).Add("after", ToJson(v.After)));
            }

            var coll = AnnotationScales.Collection(db);
            foreach (var s in p.Scales)
            {
                if (s.Exists) continue;
                if (coll is null) throw new InvalidOperationException("The drawing has no annotation scale list.");
                var scale = new AnnotationScale { Name = s.Name, PaperUnits = 1, DrawingUnits = s.DrawingPerPaper };
                coll.AddContext(scale);
                ctx.Warn("Annotation scale " + s.Name + " was added to the scale list" + (rehearse ? "; a rehearsal may leave it there (scale list entries are not known to roll back)." : "."));
            }

            if (p.CurrentScale != null && !string.Equals(p.CannoBefore, p.CurrentScale, StringComparison.OrdinalIgnoreCase))
            {
                var target = AnnotationScales.Find(db, p.CurrentScale) ?? throw new InvalidOperationException("Annotation scale " + p.CurrentScale + " is not in the scale list.");
                if (rehearse) ctx.SetSysVar("CANNOSCALE", target.Name);
                else { db.Cannoscale = target; ctx.SysVarScope.Keep("CANNOSCALE"); }
                changed.Set("CANNOSCALE", new JsonObject().Add("before", p.CannoBefore ?? "").Add("after", target.Name));
            }

            if (p.Standard != null)
            {
                var created = new List<string>();
                var modified = new List<string>();
                var unchanged = new List<string>();
                StandardApply.Execute(ctx, p.Standard, args.GetBool("update"), p.MmPerDrawingUnit, p.StdUnit, created, modified, unchanged);
                result.Created.AddRange(created);
                result.Modified.AddRange(modified);
                result.Unchanged.AddRange(unchanged);
                foreach (var it in p.Standard.Where(i => i.State == "different"))
                    ctx.Warn(it.Kind + " '" + it.Name + "' exists and differs from the standard (" + string.Join("; ", it.Differences) + "); left unchanged because update is false.");
            }
            result.Outputs.Add("changed", changed);
            return p;
        }

        private static JsonValue ToJson(object v)
        {
            switch (v)
            {
                case double d: return AcadValues.Round(d);
                case int i: return i;
                case short s: return (int)s;
                case bool b: return b;
                default: return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
            }
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, SetupPlan p)
        {
            var db = ctx.Database;
            var mismatches = new List<string>();
            var vars = new JsonObject();
            foreach (var v in p.Vars)
            {
                object got = v.Read(db);
                vars.Set(v.Name, ToJson(got));
                if (!Same(got, v.After)) mismatches.Add(v.Name + " reads back as " + got + ", expected " + v.After);
            }
            string canno = Anno.CurrentName(db);
            vars.Set("CANNOSCALE", canno);
            if (p.CurrentScale != null && !string.Equals(canno, p.CurrentScale, StringComparison.OrdinalIgnoreCase)) mismatches.Add("CANNOSCALE reads back as " + canno + ", expected " + p.CurrentScale);
            result.Readback.Add("variables", vars);

            var scales = new JsonArray();
            foreach (var s in p.Scales)
            {
                var found = AnnotationScales.Find(db, s.ExistingName ?? s.Name);
                if (found is null) { mismatches.Add("annotation scale " + s.Name + " is not in the scale list after the op"); continue; }
                double dpp = Anno.DrawingPerPaper(found);
                scales.Add(new JsonObject().Add("name", found.Name).Add("state", s.Exists ? "existing" : "created")
                    .Add("paperUnits", AcadValues.Round(found.PaperUnits)).Add("drawingUnits", AcadValues.Round(found.DrawingUnits)));
                if (!s.Exists && Math.Abs(dpp - s.DrawingPerPaper) > 1e-9 * Math.Max(1, s.DrawingPerPaper)) mismatches.Add("annotation scale " + s.Name + " reads back as " + found.PaperUnits + ":" + found.DrawingUnits);
            }
            result.Readback.Add("annotationScales", scales);

            if (p.Standard != null) result.Readback.Add("standard", StandardApply.Verify(ctx, p.Standard, p.MmPerDrawingUnit, p.StdUnit, mismatches));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
