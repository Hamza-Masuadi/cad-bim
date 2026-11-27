using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// revision.cloud: RevisionCloud.Create(doc, view, revisionId, curves).
    ///
    /// <para>The API documents that clouds can only be made for revisions not yet issued, in most graphical views
    /// except 3D views and graphical column schedules, and directly on sheets; that model-view curves are in model
    /// space and sheet curves in sheet space; that curves are projected onto the view's plane; and that the cloud's
    /// bumps face each line's normal assuming clockwise curves. So the loop is put in clockwise order in the view's
    /// right and up directions (on sheets: paper units) before it is passed.</para>
    ///
    /// <para>The revision is an id or reference, else a revision number or description that matches exactly one
    /// revision (Revision.GetAllRevisionIds). RevisionNumber is read inside a try because per-sheet numbering makes it
    /// meaningless. An issued revision is refused: unissue it with revision.ensure first. Comments and Mark go to
    /// ALL_MODEL_INSTANCE_COMMENTS and ALL_MODEL_MARK.</para>
    /// </summary>
    public sealed class RevisionCloudCreate : OpHandler
    {
        public override string Name => "revision.cloud";

        private sealed class Plan
        {
            public View View = null!;
            public Revision Revision = null!;
            public List<XYZ> Points = new List<XYZ>();
        }

        private static string? Number(Revision r)
        {
            try { return r.RevisionNumber; } catch (Exception) { return null; }
        }

        internal static Revision? FindRevision(OpContext ctx, JsonValue? value, string path, List<Issue> issues, out bool pending)
        {
            pending = OpContext.IsPending(value);
            if (pending) return null;
            var doc = ctx.Doc;
            var all = Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id) as Revision).Where(r => r != null).Cast<Revision>().ToList();
            string? id0 = Refs.ElementId(value);
            if (id0 != null && ElementIds.TryParse(id0, out var eid) && doc.GetElement(eid) is Revision byId) return byId;
            if (!(value is JsonString s))
            {
                OpContext.Error(issues, path, RevitIssueCodes.NotFound, "element " + (id0 ?? "?") + " is not a revision");
                return null;
            }
            string t = s.Value.Trim();
            foreach (var cmp in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                var hits = all.Where(r => string.Equals(Number(r), t, cmp) || string.Equals(r.Description, t, cmp)).ToList();
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1)
                {
                    OpContext.Error(issues, path, RevitIssueCodes.Ambiguous, hits.Count + " revisions match '" + t + "' (ids " + string.Join(", ", hits.Select(r => ElementIds.ToWire(r.Id))) + "); use an id");
                    return null;
                }
            }
            OpContext.Error(issues, path, RevitIssueCodes.NotFound, "no revision numbered or described '" + t + "'" +
                Lookup.Suggest(t, all.Select(r => Number(r) ?? r.Description ?? "").Where(x => x.Length > 0).ToList()));
            return null;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pv);
            var rev = FindRevision(ctx, args.Get("revision"), ctx.FieldPath(args, "revision"), issues, out bool pr);
            if (rev != null && rev.Issued)
                OpContext.Error(issues, ctx.FieldPath(args, "revision"), RevitIssueCodes.NotAllowed, "revision " + (Number(rev) ?? ElementIds.ToWire(rev.Id)) + " is issued; Revit only draws clouds for revisions not yet issued");
            if (pv || pr || v is null || rev is null) return null;
            if (W2.Refuse(v, "revision clouds", ctx.FieldPath(args, "view"), issues, allowSheet: true)) return null;
            List<Point2> pts;
            if (args.Has("rect"))
            {
                var r = args.GetObject("rect")!;
                var min = W2.FramePoint(ctx, r, v, "min");
                var max = W2.FramePoint(ctx, r, v, "max");
                if (max.X <= min.X || max.Y <= min.Y) { OpContext.Error(issues, ctx.FieldPath(args, "rect"), RevitIssueCodes.Invalid, "rect max must be above and to the right of min"); return null; }
                pts = new List<Point2> { min, new Point2(max.X, min.Y), max, new Point2(min.X, max.Y) };
            }
            else
            {
                pts = W2.FramePoints(ctx, args, v, "boundary");
                if (!W2.IsSimplePolygon(pts, W2.FrameUnits(ctx, v).LengthTolerance)) { OpContext.Error(issues, ctx.FieldPath(args, "boundary"), RevitIssueCodes.Invalid, "boundary must be a simple polygon"); return null; }
            }
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) <= W2.FrameUnits(ctx, v).LengthTolerance) pts.RemoveAt(pts.Count - 1);
            var model = W2.ToModel(ctx, v, pts);
            // Clockwise as the view shows it: the frame's axes are mirrored on screen in ceiling plans.
            var right = v.RightDirection;
            var up = v.UpDirection;
            if (new Polygon2(model.Select(q => new Point2(q.DotProduct(right), q.DotProduct(up)))).IsCounterClockwise) model.Reverse();
            var p = new Plan { View = v, Revision = rev, Points = model };
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var curves = new List<Curve>();
            for (int i = 0; i < p.Points.Count; i++) curves.Add(Line.CreateBound(p.Points[i], p.Points[(i + 1) % p.Points.Count]));
            var cloud = RevisionCloud.Create(ctx.Doc, p.View, p.Revision.Id, curves) ?? throw new OpFailedException("create_failed", "Revit did not create the revision cloud");
            if (args.GetStringOrNull("comments") is string c) cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c);
            if (args.GetStringOrNull("mark") is string m) cloud.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(m);
            result.Created.Add(ElementIds.ToWire(cloud.Id));
            result.Outputs.Set("cloud", ElementIds.ToWire(cloud.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string? id = result.Outputs.GetStringOrNull("cloud");
            var cloud = id != null && ElementIds.TryParse(id, out var eid) ? ctx.Doc.GetElement(eid) as RevisionCloud : null;
            var issues = new List<Issue>();
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out _);
            var rev = FindRevision(ctx, args.Get("revision"), ctx.FieldPath(args, "revision"), issues, out _);
            if (cloud is null || v is null || rev is null) { check.Fail(cloud is null ? "the revision cloud was not found after the op" : "could not resolve the request again"); check.Apply(result); return; }
            var rb = result.Readback;
            rb.Set("id", id!);
            check.Text(rb, "revision", ElementIds.ToWire(cloud.RevisionId), ElementIds.ToWire(rev.Id));
            check.Text(rb, "view", ElementIds.ToWire(cloud.OwnerViewId), ElementIds.ToWire(v.Id));
            rb.Set("curves", cloud.GetSketchCurves().Count);
            if (cloud.GetSketchCurves().Count == 0) check.Fail("the cloud has no sketch curves");
            if (args.GetStringOrNull("comments") is string c) check.Text(rb, "comments", cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(), c);
            if (args.GetStringOrNull("mark") is string m) check.Text(rb, "mark", cloud.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(), m);
            check.Apply(result);
        }
    }
}
