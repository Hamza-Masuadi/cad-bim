using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// entity.transform: one move, rotate, scale or mirror (a Matrix3d in drawing units) applied with Entity.TransformBy.
    /// Copies are made with Database.DeepCloneObjects into each entity's own space, so attributes, dynamic block data,
    /// extension dictionaries (annotation scales) and xdata come along; copy n gets the transform applied n times.
    /// Mirror with text "readable" re-orients single-line text and attributes the way MIRRTEXT 0 does: the text axis
    /// closer to perpendicular to the mirror line is flipped back, the mirrored flags are cleared, and the mirrored
    /// insertion (or alignment) point is kept. Multiline text is never mirrored by AutoCAD.
    /// </summary>
    internal sealed class EntityTransform : OpHandler<EntityTransform.Plan>
    {
        public override string Name => "entity.transform";

        internal sealed class Target
        {
            public ObjectId Source;
            public ObjectId Result;
            public int Copy;
            public Point3d[]? Expected;
        }

        internal sealed class Plan
        {
            public List<ObjectId> Ids = new List<ObjectId>();
            public int SkippedLocked;
            public string Kind = "";
            public Matrix3d M = Matrix3d.Identity;
            public Vector3d MirrorAxis;
            public bool Readable;
            public bool Copy;
            public int Copies = 1;
            public List<Target> Targets = new List<Target>();
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan { Copy = args.GetBool("copy"), Copies = args.GetInt("copies") };
            var u = ctx.Units;
            if (args.GetObject("move") is OpArgs mv)
            {
                p.Kind = "move";
                var from = mv.GetPoint2("from"); var to = mv.GetPoint2("to");
                p.M = Matrix3d.Displacement(u.ToDrawing(from).GetVectorTo(u.ToDrawing(to)));
                if (from.DistanceTo(to) <= ctx.LengthTolerance && !p.Copy) OpContext.Warning(issues, mv, "to", "no_change", "from and to are the same point; nothing moves");
            }
            else if (args.GetObject("rotate") is OpArgs ro)
            {
                p.Kind = "rotate";
                p.M = Matrix3d.Rotation(Num.Rad(ro.GetAngle("angle")), Vector3d.ZAxis, u.ToDrawing(ro.GetPoint2("base")));
            }
            else if (args.GetObject("scale") is OpArgs sc)
            {
                p.Kind = "scale";
                p.M = Matrix3d.Scaling(sc.GetNumber("factor"), u.ToDrawing(sc.GetPoint2("base")));
            }
            else if (args.GetObject("mirror") is OpArgs mi)
            {
                p.Kind = "mirror";
                var a = mi.GetPoint2("axisStart"); var b = mi.GetPoint2("axisEnd");
                if (a.DistanceTo(b) <= ctx.LengthTolerance) OpContext.Error(issues, mi, "axisEnd", "axis_invalid", "the mirror line's two points coincide");
                else
                {
                    p.M = Matrix3d.Mirroring(new Line3d(u.ToDrawing(a), u.ToDrawing(b)));
                    p.MirrorAxis = u.ToDrawing(a).GetVectorTo(u.ToDrawing(b)).GetNormal();
                }
                p.Readable = mi.GetString("text") == "readable";
            }
            if (args.Has("copies") && !p.Copy) OpContext.Error(issues, args, "copies", "field_not_used", "copies needs copy true");
            if (p.Copy && p.Copies > 1 && p.Kind == "mirror") OpContext.Error(issues, args, "copies", "copies_mirror", "a second mirror copy would land on the original; mirror makes one copy");

            var s = Support.Targets.Select(ctx, args, issues, lockedIsError: false);
            Support.Targets.CheckCount(ctx, args, issues, s, "entities");
            p.Ids = s.Ids;
            p.SkippedLocked = s.SkippedLocked;
            return p;
        }

        /// <summary>Points that identify where an entity is (centre, insertion, ends), in drawing units.</summary>
        private static Point3d[]? KeyPoints(Entity e)
        {
            switch (e)
            {
                case Circle c: return new[] { c.Center };
                case Arc a: return new[] { a.Center };
                case Ellipse el: return new[] { el.Center };
                case DBText t when IsTwoPoint(t): return new[] { t.Position, t.AlignmentPoint };
                case DBText t: return new[] { t.Justify == AttachmentPoint.BaseLeft ? t.Position : t.AlignmentPoint };
                case MText mt: return new[] { mt.Location };
                case BlockReference br: return new[] { br.Position };
                case DBPoint pt: return new[] { pt.Position };
                case Curve cv:
                    try { return new[] { cv.StartPoint, cv.EndPoint }; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
                default: return null;
            }
        }

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var keys = new Dictionary<ObjectId, Point3d[]?>();
            foreach (var id in p.Ids) keys[id] = KeyPoints((Entity)tr.GetObject(id, OpenMode.ForRead));
            var handles = new JsonArray();

            if (!p.Copy)
            {
                foreach (var id in p.Ids)
                {
                    var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    Transform(tr, e, p);
                    p.Targets.Add(new Target { Source = id, Result = id, Copy = 0, Expected = keys[id]?.Select(k => k.TransformBy(p.M)).ToArray() });
                    result.Modified.Add(OpContext.HandleOf(id));
                    handles.Add(OpContext.HandleOf(id));
                }
            }
            else
            {
                var byOwner = p.Ids.GroupBy(id => ((Entity)tr.GetObject(id, OpenMode.ForRead)).OwnerId).ToList();
                var mk = Matrix3d.Identity;
                for (int k = 1; k <= p.Copies; k++)
                {
                    mk = p.M * mk;
                    var clones = new Dictionary<ObjectId, ObjectId>();
                    foreach (var g in byOwner)
                    {
                        var ids = new ObjectIdCollection(g.ToArray());
                        using (var map = new IdMapping())
                        {
                            db.DeepCloneObjects(ids, g.Key, map, false);
                            foreach (IdPair pair in map)
                                if (pair.IsPrimary && pair.IsCloned && ids.Contains(pair.Key)) clones[pair.Key] = pair.Value;
                        }
                    }
                    var step = new Plan { M = mk, Kind = p.Kind, MirrorAxis = p.MirrorAxis, Readable = p.Readable };
                    foreach (var id in p.Ids)
                    {
                        if (!clones.TryGetValue(id, out var cid)) throw new InvalidOperationException("entity " + OpContext.HandleOf(id) + " was not copied");
                        var e = (Entity)tr.GetObject(cid, OpenMode.ForWrite);
                        Transform(tr, e, step);
                        p.Targets.Add(new Target { Source = id, Result = cid, Copy = k, Expected = keys[id]?.Select(x => x.TransformBy(mk)).ToArray() });
                        result.Created.Add(OpContext.HandleOf(cid));
                        handles.Add(OpContext.HandleOf(cid));
                    }
                }
            }
            result.Outputs.Add("handles", handles).Add("count", p.Ids.Count);
            if (p.SkippedLocked > 0) ctx.Warn(p.SkippedLocked + " entities on locked layers were skipped.");
            return p;
        }

        private static void Transform(Transaction tr, Entity e, Plan p)
        {
            var before = new Dictionary<ObjectId, double>();
            if (p.Kind == "mirror" && p.Readable)
            {
                if (e is DBText t0 && !t0.IsMirroredInX && !t0.IsMirroredInY) before[e.ObjectId] = t0.Rotation;
                if (e is BlockReference br0)
                    foreach (ObjectId aid in br0.AttributeCollection)
                        if (!aid.IsErased && tr.GetObject(aid, OpenMode.ForRead) is AttributeReference a0 && !a0.IsMTextAttribute && !a0.IsMirroredInX && !a0.IsMirroredInY) before[aid] = a0.Rotation;
            }
            e.TransformBy(p.M);
            if (before.Count == 0) return;
            if (e is DBText t && before.TryGetValue(e.ObjectId, out double r)) Readable(t, r, p.MirrorAxis);
            if (e is BlockReference br)
                foreach (ObjectId aid in br.AttributeCollection)
                    if (before.TryGetValue(aid, out double ra)) Readable((DBText)tr.GetObject(aid, OpenMode.ForWrite), ra, p.MirrorAxis);
        }

        /// <summary>Text mirrored by TransformBy, made readable again (plan text: normal along Z).</summary>
        private static void Readable(DBText t, double rotationBefore, Vector3d axis)
        {
            var d = new Vector3d(Math.Cos(rotationBefore), Math.Sin(rotationBefore), 0);
            var up = new Vector3d(-d.Y, d.X, 0);
            var m = Matrix3d.Mirroring(new Line3d(Point3d.Origin, axis));
            var dm = d.TransformBy(m);
            var um = up.TransformBy(m);
            var dir = Math.Abs(d.DotProduct(axis)) < Math.Abs(up.DotProduct(axis)) ? -dm : dm;
            if (!t.IsWriteEnabled) t.UpgradeOpen();
            t.IsMirroredInX = false;
            t.IsMirroredInY = false;
            if (IsTwoPoint(t))
            {
                // Aligned and fit text take their rotation from the baseline start to end, so the ends swap instead.
                Point3d start = t.Position, end = t.AlignmentPoint;
                if ((end - start).DotProduct(dir) < 0)
                {
                    t.Position = end;
                    t.AlignmentPoint = start;
                }
            }
            else
            {
                t.Rotation = Math.Atan2(dir.Y, dir.X);
            }
            if (t.Justify != AttachmentPoint.BaseLeft) t.AdjustAlignment(t.Database);
        }

        private static bool IsTwoPoint(DBText t) => t.Justify == AttachmentPoint.BaseAlign || t.Justify == AttachmentPoint.BaseFit;

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            var tr = ctx.Transaction;
            double tol = ctx.Units.ToDrawing(ctx.LengthTolerance);
            int bad = 0, checkedCount = 0;
            string first = "";
            var list = new JsonArray();
            foreach (var t in p.Targets)
            {
                if (t.Result.IsErased) { bad++; if (first.Length == 0) first = OpContext.HandleOf(t.Result) + " is erased"; continue; }
                var e = (Entity)tr.GetObject(t.Result, OpenMode.ForRead);
                var got = KeyPoints(e);
                if (list.Count < 200)
                    list.Add(new JsonObject().Add("handle", OpContext.HandleOf(t.Result)).Add("source", OpContext.HandleOf(t.Source)).Add("copy", t.Copy)
                        .Add("at", got != null && got.Length > 0 ? (JsonValue)DrawingUnits.PointJson(ctx.Units.ToJob(got[0])) : JsonValue.Null));
                if (t.Expected is null || got is null) continue;
                checkedCount++;
                bool ok = t.Expected.Any(x => got.Any(g => g.DistanceTo(x) <= tol));
                if (!ok) { bad++; if (first.Length == 0) first = OpContext.HandleOf(t.Result) + " is at " + ctx.Units.ToJob(got[0]) + ", expected " + ctx.Units.ToJob(t.Expected[0]); }
            }
            result.Readback.Add("targets", list).Add("pointsChecked", checkedCount).Add("skippedLocked", p.SkippedLocked);
            return bad == 0 || Mismatch(result, bad + " entities are not where the transform puts them (first " + first + ")");
        }
    }
}
