using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Units;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops.Modelling.Support
{
    /// <summary>
    /// Shared implementation of door.place and window.place: one wall-hosted family instance.
    ///
    /// Validate: the host is a basic or stacked wall with a location curve (curtain walls are refused: ordinary
    /// inserts cannot be hosted there; use the curtain door panel family); the type is a wall-hosted family of the
    /// right category; the level is the host's base level or lies within the wall's height; the insert, at its type
    /// width, lies wholly within the wall's length; its head is below the top of the wall; it does not overlap
    /// another door or window on the same wall along the wall and in height; the mark is not already used in the
    /// category (RV-005).
    ///
    /// Apply: activates the type (and regenerates), places the instance at the point on the location curve with
    /// NewFamilyInstance(point, symbol, host, level, NonStructural), sets the sill, flips, mark and parameters,
    /// regenerates, then refuses (rolls the op back) when Revit did not host it in the wall, moved it along the wall,
    /// or it now overlaps another opening.
    ///
    /// Readback: host id, level, offset along the wall from its start to the centre, the plan point, sill height,
    /// hand and facing flips, type, mark, parameters, and the within-wall and overlap checks again on the committed
    /// model.
    ///
    /// The host wall is declared as modified: inserting cuts it, and in a workshared model the insert needs the host.
    /// </summary>
    public abstract class HostedInsertPlace : OpHandler
    {
        protected abstract BuiltInCategory Category { get; }
        protected abstract string Kind { get; }

        private sealed class Plan
        {
            public Wall? Host;
            public Curve? Curve;
            public FamilySymbol Symbol = null!;
            public Level? Level;
            public double? Along;        // internal, centre of the insert from the curve start
            public double? Width;        // internal
            public double? Height;       // internal
            public double? Sill;         // internal, requested
        }

        private Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var p = new Plan();
            string kind = Kind;

            var sym = Lookup.Symbol(doc, Category, args.GetString("type"), kind + " type");
            if (!sym.Ok) OpContext.Error(issues, ctx.FieldPath(args, "type"), sym.Code, sym.Error!);
            else
            {
                p.Symbol = sym.Value!;
                var placement = p.Symbol.Family.FamilyPlacementType;
                if (placement != FamilyPlacementType.OneLevelBasedHosted)
                    OpContext.Error(issues, ctx.FieldPath(args, "type"), RevitIssueCodes.Invalid,
                        "'" + Lookup.FamilyTypeName(p.Symbol) + "' is not a wall-hosted family (placement " + placement + "); this op places inserts in walls");
                p.Width = Openings.Width(null, p.Symbol);
                p.Height = Openings.Height(null, p.Symbol);
            }

            if (args.Has("sillHeight")) p.Sill = u.LengthToInternal(args.GetLength("sillHeight"));
            if (Category == BuiltInCategory.OST_Doors && p.Sill.HasValue && p.Sill.Value > u.LengthToleranceInternal)
                OpContext.Warning(issues, ctx.FieldPath(args, "sillHeight"), RevitIssueCodes.Unchecked, "a door with a sill above 0 is unusual; check it is intended");

            if (args.GetStringOrNull("mark") is string mark)
            {
                var dup = Kit.WithMark(doc, Category, mark);
                if (dup.Count > 0) OpContext.Error(issues, ctx.FieldPath(args, "mark"), RevitIssueCodes.Exists,
                    "mark '" + mark + "' is already used by " + kind + " " + Kit.Id(dup[0].Id) + "; marks must be unique");
            }
            Kit.CheckParameterNames(ctx, args, "parameters", Category, new FilteredElementCollector(doc).OfCategory(Category).WhereElementIsNotElementType().FirstElement(), issues, kind + "s");

            if (ctx.IsPending(args, "host")) return Kit.HasErrors(issues) ? null : p;
            var hostEl = Kit.Element(doc, args.Get("host"), out string herr);
            if (hostEl is null) { OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.NotFound, herr); return null; }
            if (!(hostEl is Wall wall))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "host " + Kit.Id(hostEl.Id) + " is a " + (hostEl.Category?.Name ?? hostEl.GetType().Name) + ", not a wall");
                return null;
            }
            p.Host = wall;
            if (wall.WallType.Kind == WallKind.Curtain)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "wall " + Kit.Id(wall.Id) + " is a curtain wall; ordinary " + kind + "s cannot be hosted in it (use the project's curtain-wall door or window panel)");
                return null;
            }
            if (!(wall.Location is LocationCurve lc)) { OpContext.Error(issues, ctx.FieldPath(args, "host"), RevitIssueCodes.Invalid, "wall " + Kit.Id(wall.Id) + " has no location curve"); return null; }
            p.Curve = lc.Curve;
            ctx.DeclareModifies(wall.Id);

            var baseLevel = doc.GetElement(wall.LevelId) as Level;
            if (args.GetStringOrNull("level") is string lname)
            {
                var lv = Lookup.Level(doc, lname);
                if (!lv.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), lv.Code, lv.Error!);
                else
                {
                    p.Level = lv.Value;
                    if (baseLevel != null && p.Level!.Id != baseLevel.Id)
                    {
                        var bb = wall.get_BoundingBox(null);
                        double z = p.Level.ProjectElevation;
                        if (bb is null || z < bb.Min.Z - u.LengthToleranceInternal || z > bb.Max.Z - u.LengthToleranceInternal)
                            OpContext.Error(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Invalid, "level '" + p.Level.Name + "' is outside the height of wall " + Kit.Id(wall.Id) + " (base level '" + baseLevel.Name + "')");
                        else OpContext.Warning(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.Unchecked, "the " + kind + " is placed on '" + p.Level.Name + "', not on the wall's base level '" + baseLevel.Name + "'");
                    }
                }
            }
            else p.Level = baseLevel;
            if (p.Level is null) OpContext.Error(issues, ctx.FieldPath(args, "level"), RevitIssueCodes.NotFound, "wall " + Kit.Id(wall.Id) + " has no base level; give level");

            double length = p.Curve.Length;
            if (args.Has("offset"))
            {
                p.Along = u.LengthToInternal(args.GetLength("offset"));
                if (p.Along < -u.LengthToleranceInternal || p.Along > length + u.LengthToleranceInternal)
                    OpContext.Error(issues, ctx.FieldPath(args, "offset"), RevitIssueCodes.Invalid, "offset " + Kit.Num(args.GetLength("offset")) + " is outside wall " + Kit.Id(wall.Id) +
                        " (length " + Kit.Num(u.LengthFromInternal(length)) + " " + Length.Symbol(u.JobUnit) + ")");
            }
            else
            {
                var pt = args.GetPoint2("point");
                var xyz = u.PointToInternal(pt, p.Curve.GetEndPoint(0).Z);
                p.Along = Openings.Along(p.Curve, xyz);
                if (p.Along is null) OpContext.Error(issues, ctx.FieldPath(args, "point"), RevitIssueCodes.Invalid, "the point does not project onto wall " + Kit.Id(wall.Id));
                else
                {
                    double dist = Openings.At(p.Curve, p.Along.Value).DistanceTo(new XYZ(xyz.X, xyz.Y, Openings.At(p.Curve, p.Along.Value).Z));
                    if (dist > wall.Width * 2 + u.LengthToInternal(SA.Core.Units.Length.Convert(500, LengthUnit.Millimetre, u.JobUnit)))
                        OpContext.Error(issues, ctx.FieldPath(args, "point"), RevitIssueCodes.Invalid, "the point is " + Kit.Num(u.LengthFromInternal(dist)) + " " + Length.Symbol(u.JobUnit) + " from wall " + Kit.Id(wall.Id) + "; give a point on or near the wall");
                }
            }
            if (p.Along.HasValue && p.Width.HasValue)
            {
                double half = p.Width.Value / 2;
                if (p.Along.Value - half < -u.LengthToleranceInternal || p.Along.Value + half > length + u.LengthToleranceInternal)
                    OpContext.Error(issues, ctx.FieldPath(args, args.Has("offset") ? "offset" : "point"), RevitIssueCodes.Invalid,
                        "a " + Kit.Num(u.LengthFromInternal(p.Width.Value)) + " " + Length.Symbol(u.JobUnit) + " wide " + kind + " centred at " + Kit.Num(u.LengthFromInternal(p.Along.Value)) +
                        " runs past the end of wall " + Kit.Id(wall.Id) + " (length " + Kit.Num(u.LengthFromInternal(length)) + ")");
                double? sill = p.Sill ?? (Category == BuiltInCategory.OST_Doors ? 0 : (double?)null);
                var me = new OpeningSpan { From = p.Along.Value - half, To = p.Along.Value + half, Bottom = sill, Top = sill.HasValue && p.Height.HasValue ? sill + p.Height : null };
                var clash = Openings.Clash(me, Openings.OnWall(doc, wall, null, out _), u.LengthToleranceInternal);
                if (clash != null && me.Bottom.HasValue)
                    OpContext.Error(issues, ctx.FieldPath(args, args.Has("offset") ? "offset" : "point"), RevitIssueCodes.Exists,
                        "the " + kind + " would overlap " + clash.Category + " " + Kit.Id(clash.Id) + " in wall " + Kit.Id(wall.Id));
                else if (clash != null)
                    OpContext.Warning(issues, ctx.FieldPath(args, args.Has("offset") ? "offset" : "point"), RevitIssueCodes.Unchecked,
                        "the " + kind + " overlaps " + clash.Category + " " + Kit.Id(clash.Id) + " along the wall; heights are checked after placement");
                double? wallHeight = Kit.ParamDouble(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (wallHeight.HasValue && me.Top.HasValue && p.Level != null && baseLevel != null)
                {
                    double top = me.Top.Value + p.Level.ProjectElevation - baseLevel.ProjectElevation - (Kit.ParamDouble(wall, BuiltInParameter.WALL_BASE_OFFSET) ?? 0);
                    if (top > wallHeight.Value + u.LengthToleranceInternal)
                        OpContext.Error(issues, ctx.FieldPath(args, "sillHeight"), RevitIssueCodes.Invalid, "the " + kind + " head would be above the top of wall " + Kit.Id(wall.Id));
                }
            }
            else if (p.Along.HasValue)
                OpContext.Warning(issues, ctx.FieldPath(args, "type"), RevitIssueCodes.Unchecked, "the type's width is unknown; the fit within the wall is checked after placement");
            return Kit.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            Kit.Require(issues);
            var doc = ctx.Doc;
            var u = ctx.Units;
            if (p!.Host is null || p.Curve is null || p.Level is null || !p.Along.HasValue) throw new OpFailedException("validation_failed", "the host wall could not be resolved");
            if (!p.Symbol.IsActive) { p.Symbol.Activate(); doc.Regenerate(); }
            var at = Openings.At(p.Curve, p.Along.Value);
            var location = new XYZ(at.X, at.Y, p.Level.ProjectElevation);
            var fi = doc.Create.NewFamilyInstance(location, p.Symbol, p.Host, p.Level, StructuralType.NonStructural)
                     ?? throw new OpFailedException("create_failed", "Revit did not create the " + Kind);
            if (p.Sill.HasValue && !Kit.SetDouble(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, p.Sill.Value))
                throw new OpFailedException("parameter_value", "Revit refused the sill height for the " + Kind);
            if (args.GetBool("flipHand")) { if (!fi.CanFlipHand || !fi.flipHand()) throw new OpFailedException("flip_failed", "the " + Kind + " cannot be flipped by hand"); }
            if (args.GetBool("flipFacing")) { if (!fi.CanFlipFacing || !fi.flipFacing()) throw new OpFailedException("flip_failed", "the " + Kind + " cannot be flipped by facing"); }
            Kit.SetMark(fi, args.GetStringOrNull("mark"), "the " + Kind);
            Kit.SetParameters(doc, fi, args.GetMap("parameters"), u, "the " + Kind);
            doc.Regenerate();

            // The host must have accepted the instance where it was asked for.
            if (fi.Host is null || fi.Host.Id != p.Host.Id)
                throw new OpFailedException("host_rejected", "Revit did not host the " + Kind + " in wall " + Kit.Id(p.Host.Id) + (fi.Host != null ? " (host is " + Kit.Id(fi.Host.Id) + ")" : " (no host)"));
            var problem = Fit(doc, fi, p.Host, p.Along.Value, u);
            if (problem != null) throw new OpFailedException("placement_rejected", problem);

            string id = Kit.Id(fi.Id);
            result.Created.Add(id);
            result.Outputs.Add("element", id);
        }

        /// <summary>Null when the instance sits at the requested position, inside the wall's length, without overlapping another opening.</summary>
        private string? Fit(Document doc, FamilyInstance fi, Wall host, double? wantAlong, RevitUnits u)
        {
            if (!(host.Location is LocationCurve lc)) return "the host wall has no location curve";
            var span = Openings.Span(doc, fi, lc.Curve);
            if (!(fi.Location is LocationPoint lp)) return "the " + Kind + " has no location point";
            var along = Openings.Along(lc.Curve, lp.Point);
            if (along is null) return "the " + Kind + " does not project onto its host";
            if (wantAlong.HasValue && Math.Abs(along.Value - wantAlong.Value) > u.LengthToleranceInternal)
                return "Revit placed the " + Kind + " at " + Kit.Num(u.LengthFromInternal(along.Value)) + " along the wall, not at " + Kit.Num(u.LengthFromInternal(wantAlong.Value));
            if (span is null) return null;
            double len = lc.Curve.Length;
            if (span.From < -u.LengthToleranceInternal || span.To > len + u.LengthToleranceInternal)
                return "the " + Kind + " (" + Kit.Num(u.LengthFromInternal(span.From)) + " to " + Kit.Num(u.LengthFromInternal(span.To)) + ") runs past the end of the wall (length " + Kit.Num(u.LengthFromInternal(len)) + ")";
            var clash = Openings.Clash(span, Openings.OnWall(doc, host, fi.Id, out _), u.LengthToleranceInternal);
            if (clash != null) return "the " + Kind + " overlaps " + clash.Category + " " + Kit.Id(clash.Id) + " in wall " + Kit.Id(host.Id);
            return null;
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var u = ctx.Units;
            var check = new ReadbackChecker(u);
            string? idText = result.Outputs.GetStringOrNull("element");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is FamilyInstance fi))
            {
                check.Fail("the " + Kind + " no longer exists after commit");
                check.Apply(result);
                return;
            }
            var rb = new JsonObject().Add("id", idText);
            string? wantHost = Refs.ElementId(args.Get("host"));
            var host = fi.Host;
            check.Text(rb, "host", host is null ? null : Kit.Id(host.Id), wantHost, Kind + " host");
            result.Outputs.Set("host", host is null ? (JsonValue)JsonValue.Null : Kit.Id(host.Id));
            check.Text(rb, "category", fi.Category?.Name, Autodesk.Revit.DB.Category.GetCategory(doc, Category)?.Name, Kind + " category");
            check.Text(rb, "type", Lookup.FamilyTypeName(fi.Symbol), Lookup.Symbol(doc, Category, args.GetString("type"), Kind + " type").Value is FamilySymbol ws ? Lookup.FamilyTypeName(ws) : null, Kind + " type");
            string? wantLevel = args.GetStringOrNull("level") is string ln ? Lookup.Level(doc, ln).Value?.Name : host != null ? Lookup.LevelName(doc, host.LevelId) : null;
            check.Text(rb, "level", Lookup.LevelName(doc, fi.LevelId), wantLevel, Kind + " level");
            if (host is Wall wall && wall.Location is LocationCurve lc && fi.Location is LocationPoint lp)
            {
                var along = Openings.Along(lc.Curve, lp.Point);
                // The requested position, worked out again against the committed wall (Validate would now see the new insert itself).
                double? wantAlong = args.Has("offset") ? args.GetLength("offset") : (double?)null;
                if (!wantAlong.HasValue)
                {
                    var req = Openings.Along(lc.Curve, u.PointToInternal(args.GetPoint2("point"), lc.Curve.GetEndPoint(0).Z));
                    if (req.HasValue) wantAlong = u.LengthFromInternal(req.Value);
                }
                if (along.HasValue) check.Length(rb, "offset", along.Value, wantAlong, Kind + " offset along the wall");
                else check.Fail("the " + Kind + " does not project onto its host");
                rb.Add("point", u.PointJson(lp.Point));
                check.Length(rb, "wallLength", lc.Curve.Length, null);
                var problem = Fit(doc, fi, wall, null, u);
                if (problem != null) check.Fail(problem);
                var span = Openings.Span(doc, fi, lc.Curve);
                if (span != null) { rb.Add("from", u.LengthJson(span.From)); rb.Add("to", u.LengthJson(span.To)); }
                else rb.Add("extentChecked", false);
            }
            else check.Fail("the " + Kind + " has no wall host or location point");
            var sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill != null && sill.StorageType == StorageType.Double) check.Length(rb, "sillHeight", sill.AsDouble(), args.GetLengthOrNull("sillHeight"), Kind + " sill height");
            var head = fi.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM);
            if (head != null && head.StorageType == StorageType.Double) check.Length(rb, "headHeight", head.AsDouble(), null);
            check.Flag(rb, "handFlipped", fi.HandFlipped, args.GetBool("flipHand"), Kind + " hand flipped");
            check.Flag(rb, "facingFlipped", fi.FacingFlipped, args.GetBool("flipFacing"), Kind + " facing flipped");
            if (args.GetStringOrNull("mark") is string mark) check.Text(rb, "mark", fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), mark, Kind + " mark");
            Kit.CheckParameters(doc, fi, args.GetMap("parameters"), u, check, rb, "the " + Kind);
            foreach (var kv in rb) result.Readback.Add(kv.Key, kv.Value.DeepClone());
            result.Outputs.Set("offset", rb.TryGetValue("offset", out var offset) ? offset.DeepClone() : JsonValue.Null);
            check.Apply(result);
        }
    }
}
