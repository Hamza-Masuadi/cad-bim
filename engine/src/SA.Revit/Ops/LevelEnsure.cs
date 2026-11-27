using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    /// <summary>
    /// level.ensure: create missing levels (with plan / ceiling views), leave matching ones alone, and refuse a level
    /// that exists at a different elevation unless update is true. For an existing level, createPlanView or
    /// createCeilingView given as true creates that view when the level has none (adding a view does not change the
    /// level); left at their defaults they do nothing for an existing level.
    ///
    /// Elevations in the job are above the project base point. Internally a level's height is taken as
    /// Level.ProjectElevation (documented as "relative to project origin", read here as the internal origin) minus
    /// the project base point's Z (BasePoint.Position, internal coordinates). When the level's Elevation Base is the
    /// project base point, readback also compares Level.Elevation directly, so a wrong reading of either property
    /// fails the op instead of passing silently.
    /// </summary>
    public sealed class LevelEnsure : OpHandler
    {
        public override string Name => "level.ensure";

        private sealed class Plan
        {
            public OpArgs Spec = null!;
            public string Name = "";
            public double Elevation;
            public Level? Existing;
            public bool Update;
            public LevelType? Type;
            public ViewFamilyType? PlanType;
            public ViewFamilyType? CeilingType;
        }

        internal static double PbpZ(Document doc)
        {
            try { return BasePoint.GetProjectBasePoint(doc).Position.Z; }
            catch (Exception) { return 0; }
        }

        /// <summary>Elevation above the project base point, internal units.</summary>
        internal static double AboveBase(Level l, double pbpZ) => l.ProjectElevation - pbpZ;

        private List<Plan> Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = new List<Plan>();
            var doc = ctx.Doc;
            var u = ctx.Units;
            double pbpZ = PbpZ(doc);
            bool update = args.GetBool("update");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var levels = args.GetObjectList("levels");
            foreach (var spec in levels)
            {
                if (OpContext.IsPending(spec.Raw)) continue;
                var p = new Plan { Spec = spec, Name = spec.GetString("name").Trim(), Elevation = spec.GetLength("elevation") };
                string at = spec.Path;
                if (p.Name.Length == 0) { OpContext.Error(issues, at + ".name", RevitIssueCodes.Invalid, "level name is empty"); continue; }
                if (!seen.Add(p.Name)) { OpContext.Error(issues, at + ".name", RevitIssueCodes.Invalid, "level '" + p.Name + "' is listed twice"); continue; }
                var found = Lookup.Level(doc, p.Name);
                if (found.Ok && string.Equals(found.Value!.Name, p.Name, StringComparison.Ordinal))
                {
                    p.Existing = found.Value;
                    double actual = u.LengthFromInternal(AboveBase(p.Existing, pbpZ));
                    if (!Tolerance.Equal(actual, p.Elevation, u.LengthTolerance))
                    {
                        if (update) { p.Update = true; ctx.DeclareModifies(p.Existing.Id); }
                        else OpContext.Error(issues, at + ".elevation", RevitIssueCodes.Mismatch,
                            "level '" + p.Name + "' exists at " + Math.Round(actual, 3) + " " + Length.Symbol(u.JobUnit) + ", not " + p.Elevation + "; set update:true to move it");
                    }
                }
                else if (found.Ok)
                {
                    OpContext.Error(issues, at + ".name", RevitIssueCodes.Mismatch,
                        "a level named '" + found.Value!.Name + "' exists (names differ only by case); use that exact name");
                    continue;
                }
                string? typeName = spec.GetStringOrNull("levelType");
                if (typeName != null)
                {
                    var t = Lookup.Type<LevelType>(doc, typeName, "level type");
                    if (!t.Ok) OpContext.Error(issues, at + ".levelType", t.Code, t.Error!);
                    else
                    {
                        p.Type = t.Value;
                        if (p.Existing != null && p.Existing.GetTypeId() != t.Value!.Id)
                        {
                            if (update) { p.Update = true; ctx.DeclareModifies(p.Existing.Id); }
                            else OpContext.Error(issues, at + ".levelType", RevitIssueCodes.Mismatch,
                                "level '" + p.Name + "' exists with another type; set update:true to change it");
                        }
                    }
                }
                if (p.Existing != null)
                {
                    if (spec.Has("createPlanView") && spec.GetBool("createPlanView") && !HasPlan(doc, p.Existing, ViewType.FloorPlan))
                    {
                        p.PlanType = Lookup.FirstViewFamilyType(doc, ViewFamily.FloorPlan);
                        if (p.PlanType is null) OpContext.Error(issues, at + ".createPlanView", RevitIssueCodes.NotFound, "the model has no floor plan view type");
                    }
                    if (spec.Has("createCeilingView") && spec.GetBool("createCeilingView") && !HasPlan(doc, p.Existing, ViewType.CeilingPlan))
                    {
                        p.CeilingType = Lookup.FirstViewFamilyType(doc, ViewFamily.CeilingPlan);
                        if (p.CeilingType is null) OpContext.Error(issues, at + ".createCeilingView", RevitIssueCodes.NotFound, "the model has no ceiling plan view type");
                    }
                }
                else
                {
                    if (spec.GetBool("createPlanView"))
                    {
                        p.PlanType = Lookup.FirstViewFamilyType(doc, ViewFamily.FloorPlan);
                        if (p.PlanType is null) OpContext.Error(issues, at + ".createPlanView", RevitIssueCodes.NotFound, "the model has no floor plan view type");
                    }
                    if (spec.GetBool("createCeilingView"))
                    {
                        p.CeilingType = Lookup.FirstViewFamilyType(doc, ViewFamily.CeilingPlan);
                        if (p.CeilingType is null) OpContext.Error(issues, at + ".createCeilingView", RevitIssueCodes.NotFound, "the model has no ceiling plan view type");
                    }
                }
                if (Math.Abs(u.LengthToInternal(p.Elevation)) > 10000)
                    OpContext.Error(issues, at + ".elevation", RevitIssueCodes.Invalid, "elevation is implausibly far from the base point (over 3 km)");
                plans.Add(p);
            }
            return plans;
        }

        private static bool HasPlan(Document doc, Level level, ViewType type) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Any(v => !v.IsTemplate && v.ViewType == type && v.GenLevel != null && v.GenLevel.Id == level.Id);

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plans = Build(ctx, args, issues);
            if (issues.Any(i => i.IsError)) throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            var doc = ctx.Doc;
            var u = ctx.Units;
            double pbpZ = PbpZ(doc);
            var map = new JsonObject();
            foreach (var p in plans)
            {
                Level level;
                if (p.Existing != null)
                {
                    level = p.Existing;
                    if (p.Update)
                    {
                        // Move by the difference, so the result is independent of the level's Elevation Base setting.
                        double delta = u.LengthToInternal(p.Elevation) - AboveBase(level, pbpZ);
                        if (Math.Abs(delta) > 1e-9) level.Elevation = level.Elevation + delta;
                        if (p.Type != null && level.GetTypeId() != p.Type.Id) level.ChangeTypeId(p.Type.Id);
                        result.Modified.Add(ElementIds.ToWire(level.Id));
                    }
                    else result.Unchanged.Add(ElementIds.ToWire(level.Id));
                    if (p.PlanType != null) result.Created.Add(ElementIds.ToWire(ViewPlan.Create(doc, p.PlanType.Id, level.Id).Id));
                    if (p.CeilingType != null) result.Created.Add(ElementIds.ToWire(ViewPlan.Create(doc, p.CeilingType.Id, level.Id).Id));
                }
                else
                {
                    level = Level.Create(doc, u.LengthToInternal(p.Elevation) + pbpZ);
                    level.Name = p.Name;
                    if (p.Type != null) level.ChangeTypeId(p.Type.Id);
                    level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY)?.Set(p.Spec.GetBool("buildingStory") ? 1 : 0);
                    result.Created.Add(ElementIds.ToWire(level.Id));
                    if (p.PlanType != null) result.Created.Add(ElementIds.ToWire(ViewPlan.Create(doc, p.PlanType.Id, level.Id).Id));
                    if (p.CeilingType != null) result.Created.Add(ElementIds.ToWire(ViewPlan.Create(doc, p.CeilingType.Id, level.Id).Id));
                }
                map.Add(p.Name, ElementIds.ToWire(level.Id));
            }
            result.Outputs.Add("levels", map);
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            double pbpZ = PbpZ(doc);
            var check = new ReadbackChecker(u);
            var arr = new JsonArray();
            var map = result.Outputs["levels"] as JsonObject ?? new JsonObject();
            foreach (var spec in args.GetObjectList("levels"))
            {
                string name = spec.GetString("name").Trim();
                double want = spec.GetLength("elevation");
                var rb = new JsonObject();
                string? idText = map.GetStringOrNull(name);
                if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is Level level))
                {
                    check.Fail("level '" + name + "' not found after the op");
                    continue;
                }
                rb.Add("id", idText);
                check.Text(rb, "name", level.Name, name, "level '" + name + "' name");
                check.Length(rb, "elevation", AboveBase(level, pbpZ), want, "level '" + name + "' elevation");
                rb.Add("elevationM", RevitUnits.Num(RevitUnits.ToMetres(AboveBase(level, pbpZ))));
                var baseParam = level.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE) ?? doc.GetElement(level.GetTypeId())?.get_Parameter(BuiltInParameter.LEVEL_RELATIVE_BASE_TYPE);
                if (baseParam != null && baseParam.StorageType == StorageType.Integer && baseParam.AsInteger() == 0)
                    check.Length(rb, "elevationProperty", level.Elevation, want, "level '" + name + "' Elevation property (base = project base point)");
                rb.Add("display", u.Display(SpecTypeId.Length, level.Elevation));
                var story = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                if (story != null) check.Flag(rb, "buildingStory", story.AsInteger() == 1, result.Created.Contains(idText) ? spec.GetBool("buildingStory") : (bool?)null);
                if (spec.GetStringOrNull("levelType") is string lt && doc.GetElement(level.GetTypeId()) is ElementType et)
                    check.Text(rb, "levelType", et.Name, lt.Contains(" : ") ? null : lt, "level '" + name + "' type");
                var views = new JsonArray();
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                    if (!v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id && result.Created.Contains(ElementIds.ToWire(v.Id)))
                        views.Add(new JsonObject().Add("id", ElementIds.ToWire(v.Id)).Add("name", v.Name).Add("type", v.ViewType.ToString()));
                rb.Add("viewsCreated", views);
                if (result.Created.Contains(idText))
                {
                    int wantViews = (spec.GetBool("createPlanView") ? 1 : 0) + (spec.GetBool("createCeilingView") ? 1 : 0);
                    if (views.Count != wantViews) check.Fail("level '" + name + "': " + views.Count + " view(s) created, expected " + wantViews);
                }
                else
                {
                    if (spec.Has("createPlanView") && spec.GetBool("createPlanView") && !HasPlan(doc, level, ViewType.FloorPlan))
                        check.Fail("level '" + name + "' has no floor plan after the op");
                    if (spec.Has("createCeilingView") && spec.GetBool("createCeilingView") && !HasPlan(doc, level, ViewType.CeilingPlan))
                        check.Fail("level '" + name + "' has no ceiling plan after the op");
                }
                rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
                arr.Add(rb);
            }
            result.Readback.Add("levels", arr);
            check.Apply(result);
        }
    }
}
