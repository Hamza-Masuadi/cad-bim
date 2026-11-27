using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// linetype.ensure: builds LinetypeTableRecords from .lin definitions inside the job transaction (no
    /// Database.LoadLineTypeFile, whose changes are not known to roll back when a rehearsal aborts). Simple linetypes
    /// and linetypes with text elements are built; linetypes with shape elements are refused (they need the shape
    /// number inside a compiled .shx, which the managed API does not expose). The file defaults to acadiso.lin for
    /// metric drawings (MEASUREMENT 1) and acad.lin otherwise, found on the support path. An existing linetype is left
    /// alone (unchanged, with differences as a warning) unless update is true.
    /// </summary>
    internal sealed class LinetypeEnsure : OpHandler<List<LinetypeEnsure.LtPlan>>
    {
        public override string Name => "linetype.ensure";

        internal sealed class LtPlan
        {
            public string Name = "";
            public string File = "";
            public LinDefinition Def = null!;
            public string? Description;
            public bool Exists;
            public string State = "";
            public string Handle = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            foreach (var p in Plan(ctx, args, issues))
                if (!p.Exists) ctx.Plan.Add(PlannedNames.Linetype, p.Name);
        }

        private static List<LtPlan> Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var plans = new List<LtPlan>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in args.GetObjectList("linetypes"))
            {
                string name = item.GetString("name");
                if (!AcadValues.IsValidSymbolName(name, out string err)) { OpContext.Error(issues, item, "name", IssueCodes.NameInvalid, err); continue; }
                if (LinetypeSupport.IsBuiltIn(name)) { OpContext.Error(issues, item, "name", "linetype_builtin", "'" + name + "' is built in and cannot be loaded"); continue; }
                if (!seen.Add(name)) { OpContext.Error(issues, item, "name", "duplicate_linetype", "linetype '" + name + "' is listed more than once"); continue; }
                string file = item.GetStringOrNull("file") ?? Db.DefaultResourceFile(ctx.Database, ".lin");
                string? path = Path.IsPathRooted(file) ? (System.IO.File.Exists(file) ? file : null) : Db.FindSupportFile(ctx.Database, file);
                if (path is null) { OpContext.Error(issues, item, "file", "file_missing", "linetype file '" + file + "' was not found" + (Path.IsPathRooted(file) ? "" : " on the support path")); continue; }
                var def = LinFile.Find(path, name, out string lerr);
                if (def is null) { OpContext.Error(issues, item, "name", IssueCodes.LinetypeMissing, lerr); continue; }
                if (def.ShapeElement != null)
                {
                    OpContext.Error(issues, item, "name", "linetype_shapes", "linetype '" + name + "' uses shapes (" + def.ShapeElement + "); this engine builds only simple and text linetypes. Load it in AutoCAD first.");
                    continue;
                }
                foreach (var t in def.Texts.Values)
                    if (!Db.TextStyleExists(ctx, t.Style))
                        OpContext.Error(issues, item, "name", IssueCodes.TextStyleMissing, "linetype '" + name + "' writes text in style '" + t.Style + "', which is not in the drawing; add it with textstyle.ensure first");
                var p = new LtPlan { Name = def.Name, File = path, Def = def, Description = item.GetStringOrNull("description") };
                p.Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.LinetypeTableId, def.Name).IsNull);
                plans.Add(p);
            }
            return plans;
        }

        protected override List<LtPlan> Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var plans = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            bool update = args.GetBool("update");
            var tr = ctx.Transaction;
            var db = ctx.Database;
            foreach (var p in plans)
            {
                var existing = Db.SymbolId(tr, db.LinetypeTableId, p.Name);
                if (!existing.IsNull && !update)
                {
                    p.State = "unchanged";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Unchanged.Add(p.Handle);
                    var diff = Differences(ctx, (LinetypeTableRecord)tr.GetObject(existing, OpenMode.ForRead), p);
                    if (diff.Count > 0) ctx.Warn("Linetype '" + p.Name + "' exists and differs from " + p.File + " (" + string.Join(", ", diff) + "); left unchanged because update is false.");
                    continue;
                }
                LinetypeTableRecord rec;
                if (existing.IsNull)
                {
                    rec = new LinetypeTableRecord { Name = p.Name };
                    var table = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForWrite);
                    var id = table.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    p.State = "created";
                    p.Handle = OpContext.HandleOf(id);
                    result.Created.Add(p.Handle);
                }
                else
                {
                    rec = (LinetypeTableRecord)tr.GetObject(existing, OpenMode.ForWrite);
                    p.State = "updated";
                    p.Handle = OpContext.HandleOf(existing);
                    result.Modified.Add(p.Handle);
                }
                Write(ctx, rec, p);
            }
            return plans;
        }

        internal static void Write(OpContext ctx, LinetypeTableRecord rec, LtPlan p)
        {
            var def = p.Def;
            rec.AsciiDescription = p.Description ?? def.Description;
            rec.IsScaledToFit = false;
            rec.NumDashes = def.Dashes.Count;
            rec.PatternLength = def.PatternLength;
            for (int i = 0; i < def.Dashes.Count; i++)
            {
                rec.SetDashLengthAt(i, def.Dashes[i]);
                if (def.Texts.TryGetValue(i, out var t))
                {
                    var styleId = Db.SymbolId(ctx.Transaction, ctx.Database.TextStyleTableId, t.Style);
                    if (styleId.IsNull) throw new InvalidOperationException("Text style '" + t.Style + "' for linetype '" + p.Name + "' is missing.");
                    rec.SetShapeStyleAt(i, styleId);
                    rec.SetShapeNumberAt(i, 0);
                    rec.SetTextAt(i, t.Text);
                    rec.SetShapeScaleAt(i, t.Scale);
                    rec.SetShapeRotationAt(i, t.Rotation);
                    rec.SetShapeIsUcsOrientedAt(i, t.Absolute);
                    rec.SetShapeOffsetAt(i, new Vector2d(t.X, t.Y));
                }
            }
        }

        private static List<string> Differences(OpContext ctx, LinetypeTableRecord rec, LtPlan p)
        {
            var d = new List<string>();
            if (rec.NumDashes != p.Def.Dashes.Count) { d.Add("dash count " + rec.NumDashes + " vs " + p.Def.Dashes.Count); return d; }
            for (int i = 0; i < rec.NumDashes; i++)
                if (Math.Abs(rec.DashLengthAt(i) - p.Def.Dashes[i]) > 1e-9) { d.Add("dash " + (i + 1)); break; }
            foreach (var kv in p.Def.Texts)
                if (rec.TextAt(kv.Key) != kv.Value.Text) d.Add("text of dash " + (kv.Key + 1));
            return d;
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, List<LtPlan> plans)
        {
            var tr = ctx.Transaction;
            bool ok = true;
            var list = new JsonArray();
            foreach (var p in plans)
            {
                var id = Db.SymbolId(tr, ctx.Database.LinetypeTableId, p.Name);
                if (id.IsNull) { ok = Mismatch(result, "Linetype '" + p.Name + "' is not in the drawing after the op."); continue; }
                var rec = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                var dashes = new JsonArray();
                for (int i = 0; i < rec.NumDashes; i++) dashes.Add(AcadValues.Round(rec.DashLengthAt(i)));
                var texts = new JsonArray();
                for (int i = 0; i < rec.NumDashes; i++) { string t = rec.TextAt(i) ?? ""; if (t.Length > 0) texts.Add(t); }
                list.Add(new JsonObject().Add("name", rec.Name).Add("handle", OpContext.HandleOf(id)).Add("state", p.State)
                    .Add("description", rec.AsciiDescription ?? "").Add("dashes", dashes).Add("patternLength", AcadValues.Round(rec.PatternLength))
                    .Add("texts", texts).Add("file", p.File));
                if (p.State == "unchanged") continue;
                var diff = Differences(ctx, rec, p);
                if (diff.Count > 0) ok = Mismatch(result, "Linetype '" + p.Name + "' reads back differently: " + string.Join(", ", diff) + ".");
                if (Math.Abs(rec.PatternLength - p.Def.PatternLength) > 1e-9) ok = Mismatch(result, "Linetype '" + p.Name + "': pattern length reads back as " + rec.PatternLength + ".");
            }
            result.Readback.Add("linetypes", list);
            return ok;
        }
    }
}
