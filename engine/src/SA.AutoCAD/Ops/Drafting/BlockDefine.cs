using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// block.define: a BlockTableRecord built inside the job transaction from simple entities (line, polyline, circle,
    /// arc, text, attdef), or from the model space of a .dwg read into a side database. The professional default is
    /// applied: content on layer 0 gets colour, linetype and lineweight ByBlock (the reference takes its insertion
    /// layer's properties); content on a named layer gets ByLayer. Units default to the drawing's INSUNITS so the
    /// block scales correctly when inserted elsewhere. Text and attribute definitions use the current text style.
    ///
    /// fromFile does not use Database.Insert or WblockCloneObjects (not known to roll back on a rehearsal abort): the
    /// file's model-space lines, arcs, circles, lightweight polylines, single-line texts and attribute definitions are
    /// re-created by geometry; any other entity kind refuses the op. Source layers that exist here are kept, others
    /// become layer 0; source text styles that exist here are kept, others become the current style. Texts and attribute
    /// definitions keep their justification, alignment point, width factor and oblique angle. Without basePoint the
    /// block's base point is the source drawing's INSBASE.
    ///
    /// Existing block: left unchanged (ensure semantics; differences are a warning) unless redefine is true. Redefining
    /// erases the definition's entities and builds the new ones; every reference shows the new geometry, but existing
    /// references keep their attribute references (there is no ATTSYNC in the API): the number of references affected
    /// is reported. Xref, layout, anonymous and dynamic blocks are never redefined.
    /// </summary>
    internal sealed class BlockDefine : OpHandler<BlockDefine.DefinePlan>
    {
        public override string Name => "block.define";

        internal sealed class EntitySpec
        {
            public string Kind = "";
            public string Path = "";
            public List<Point2> Points = new List<Point2>();
            /// <summary>Lightweight polyline bulges (fromFile only).</summary>
            public List<double>? Bulges;
            public bool Closed;
            public Point2 Center;
            public double Radius;
            /// <summary>Degrees.</summary>
            public double StartAngle, EndAngle, Rotation;
            public string Text = "", Tag = "", Prompt = "", Default = "";
            public double Height;
            public string Layer = "0";
            public string? TextStyle;
            public bool Invisible, Constant;
            /// <summary>Text formatting copied from a source drawing (fromFile only); null keeps the defaults.</summary>
            public TextFormat? Format;
        }

        internal sealed class TextFormat
        {
            public TextHorizontalMode Horizontal;
            public TextVerticalMode Vertical;
            public Point2 Alignment;
            public double WidthFactor = 1, Oblique;
        }

        internal sealed class DefinePlan
        {
            public string Name = "";
            public Point2 BasePoint;
            public List<EntitySpec> Entities = new List<EntitySpec>();
            public bool Annotative;
            public UnitsValue Units;
            public bool Redefine;
            public bool Exists;
            public string State = "";
            public string Handle = "";
            public int ReferencesAffected;
            public string? FromFile;
        }

        private static readonly Dictionary<string, UnitsValue> UnitNames = new Dictionary<string, UnitsValue>
        {
            { "unitless", UnitsValue.Undefined }, { "mm", UnitsValue.Millimeters }, { "cm", UnitsValue.Centimeters }, { "m", UnitsValue.Meters },
            { "in", UnitsValue.Inches }, { "ft", UnitsValue.Feet },
        };

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = Plan(ctx, args, issues);
            if (!p.Exists || p.Redefine)
            {
                ctx.Plan.Add(PlannedNames.Block, p.Name);
                DraftState.For(ctx).Blocks[p.Name] = new PlannedBlock
                {
                    Name = p.Name, Annotative = p.Annotative,
                    Tags = p.Entities.Where(e => e.Kind == "attdef").Select(e => e.Tag).ToList(),
                };
            }
        }

        private static DefinePlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var db = ctx.Database;
            var p = new DefinePlan
            {
                Name = args.GetString("name"),
                BasePoint = args.GetPoint2("basePoint"),
                Annotative = args.GetBool("annotative"),
                Redefine = args.GetBool("redefine"),
                Units = args.Has("units") ? UnitNames[args.GetString("units")] : db.Insunits,
                FromFile = args.GetStringOrNull("fromFile"),
            };
            if (!AcadValues.IsValidSymbolName(p.Name, out string err)) OpContext.Error(issues, args, "name", IssueCodes.NameInvalid, err);
            if (p.Name.StartsWith("*", StringComparison.Ordinal)) OpContext.Error(issues, args, "name", IssueCodes.NameInvalid, "names starting with * are reserved for layouts and anonymous blocks");
            Db.Read(ctx, tr =>
            {
                var id = DraftDb.BlockId(tr, db, p.Name);
                if (id.IsNull) return 0;
                p.Exists = true;
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (p.Redefine)
                {
                    if (btr.IsLayout || btr.IsFromExternalReference || btr.IsAnonymous || btr.IsDynamicBlock)
                        OpContext.Error(issues, args, "redefine", "block_not_redefinable", "block '" + p.Name + "' is " +
                            (btr.IsLayout ? "a layout" : btr.IsFromExternalReference ? "an external reference" : btr.IsAnonymous ? "anonymous" : "a dynamic block (redefining it from plain entities would destroy its parameters)") + " and cannot be redefined");
                    p.ReferencesAffected = BlockSupport.References(tr, btr, out int nested).Count + nested;
                }
                return 0;
            });
            if (p.Exists && !p.Redefine)
                OpContext.Warning(issues, args, "name", "block_exists", "block '" + p.Name + "' is already defined; it is left unchanged. Set redefine: true to replace it.");

            if (p.FromFile != null) ReadFile(ctx, args, p, issues);
            else ReadEntities(ctx, args, p, issues);
            if (p.Entities.Count == 0) OpContext.Error(issues, args, p.FromFile != null ? "fromFile" : "entities", "block_empty", "the block has no content");
            var tags = p.Entities.Where(e => e.Kind == "attdef").Select(e => e.Tag.ToUpperInvariant()).ToList();
            foreach (var dup in tags.GroupBy(t => t).Where(g => g.Count() > 1))
                OpContext.Error(issues, args, "entities", "attdef_duplicate", "attribute tag '" + dup.Key + "' is defined " + dup.Count() + " times");
            return p;
        }

        private static void ReadEntities(OpContext ctx, OpArgs args, DefinePlan p, List<Issue> issues)
        {
            foreach (var e in args.GetObjectList("entities"))
            {
                var s = new EntitySpec
                {
                    Kind = e.GetString("kind"),
                    Path = e.Path,
                    Points = e.GetPoint2List("points"),
                    Closed = e.GetBool("closed"),
                    Layer = e.GetStringOrNull("layer") ?? "0",
                };
                if (e.Has("layer") && s.Layer != "0" && !Db.LayerExists(ctx, s.Layer))
                    OpContext.Error(issues, e, "layer", IssueCodes.LayerMissing, "layer '" + s.Layer + "' is not in the drawing; add it with layer.ensure first");
                if (e.Has("layer") && s.Layer != "0")
                    OpContext.Warning(issues, e, "layer", "block_content_layer", "content on layer '" + s.Layer + "' keeps that layer in every reference; use layer 0 unless the block is deliberately multi-layer (AC-006)");
                switch (s.Kind)
                {
                    case "line":
                        if (s.Points.Count != 2) OpContext.Error(issues, e, "points", "points_count", "a line needs exactly 2 points");
                        else if (s.Points[0].DistanceTo(s.Points[1]) <= ctx.LengthTolerance) OpContext.Error(issues, e, "points", "line_zero", "the line has no length");
                        break;
                    case "polyline":
                        if (s.Points.Count < 2) OpContext.Error(issues, e, "points", "points_count", "a polyline needs at least 2 points");
                        if (s.Closed && s.Points.Count < 3) OpContext.Error(issues, e, "closed", "points_count", "a closed polyline needs at least 3 points");
                        break;
                    case "circle":
                    case "arc":
                        if (!e.Has("center")) OpContext.Error(issues, e, "center", "center_missing", "a " + s.Kind + " needs center");
                        else s.Center = e.GetPoint2("center");
                        if (!e.Has("radius")) OpContext.Error(issues, e, "radius", "radius_missing", "a " + s.Kind + " needs radius");
                        else s.Radius = e.GetLength("radius");
                        if (s.Kind == "arc")
                        {
                            if (!e.Has("startAngle") || !e.Has("endAngle")) OpContext.Error(issues, e, "startAngle", "angles_missing", "an arc needs startAngle and endAngle");
                            else { s.StartAngle = e.GetAngle("startAngle"); s.EndAngle = e.GetAngle("endAngle"); }
                        }
                        break;
                    case "text":
                    case "attdef":
                        if (!e.Has("center")) OpContext.Error(issues, e, "center", "position_missing", "a " + s.Kind + " needs center (its insertion point)");
                        else s.Center = e.GetPoint2("center");
                        if (!e.Has("height")) OpContext.Error(issues, e, "height", "height_missing", "a " + s.Kind + " needs height (model height; for an annotative block, the paper height)");
                        else s.Height = e.GetLength("height");
                        if (s.Kind == "text")
                        {
                            s.Text = e.GetStringOrNull("text") ?? "";
                            if (s.Text.Length == 0) OpContext.Error(issues, e, "text", IssueCodes.TextEmpty, "the text is empty");
                        }
                        else
                        {
                            s.Tag = (e.GetStringOrNull("tag") ?? "").Trim();
                            if (s.Tag.Length == 0 || s.Tag.IndexOf(' ') >= 0) OpContext.Error(issues, e, "tag", "tag_invalid", "an attdef needs a tag without spaces");
                            s.Tag = s.Tag.ToUpperInvariant();
                            s.Prompt = e.GetStringOrNull("prompt") ?? s.Tag;
                            s.Default = e.GetStringOrNull("defaultValue") ?? "";
                        }
                        break;
                }
                p.Entities.Add(s);
            }
        }

        private static string? ResolveFile(OpContext ctx, string file)
        {
            if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
            try
            {
                string? dir = ctx.Document.IsNamedDrawing ? Path.GetDirectoryName(ctx.Document.Name) : null;
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir!, file))) return Path.Combine(dir!, file);
            }
            catch (ArgumentException) { }
            return Db.FindSupportFile(ctx.Database, file);
        }

        private static void ReadFile(OpContext ctx, OpArgs args, DefinePlan p, List<Issue> issues)
        {
            string? path = ResolveFile(ctx, p.FromFile!);
            if (path is null) { OpContext.Error(issues, args, "fromFile", "file_missing", "drawing '" + p.FromFile + "' was not found (absolute path, the drawing's folder, or the support path)"); return; }
            var unsupported = new Dictionary<string, int>();
            var layersMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var src = new Database(false, true))
            {
                src.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, "");
                if (!args.Has("units")) p.Units = src.Insunits;
                if (!args.Has("basePoint")) p.BasePoint = P(src.Insbase, 1 / ctx.Units.Factor);
                // Source coordinates are kept as block-definition coordinates (k turns them into the job units Make expects);
                // the block's units, taken from the source's INSUNITS, make INSERT scale them.
                double f = 1 / ctx.Units.Factor;
                using (var tr = src.TransactionManager.StartOpenCloseTransaction())
                {
                    var ms = Db.ModelSpace(tr, src, OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased) continue;
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        var s = new EntitySpec { Layer = ent.Layer == "0" || Db.LayerExists(ctx, ent.Layer) ? ent.Layer : "0" };
                        if (s.Layer == "0" && ent.Layer != "0") layersMissing.Add(ent.Layer);
                        switch (ent)
                        {
                            case Line l:
                                s.Kind = "line";
                                s.Points.Add(P(l.StartPoint, f)); s.Points.Add(P(l.EndPoint, f));
                                break;
                            case Arc a:
                                s.Kind = "arc"; s.Center = P(a.Center, f); s.Radius = a.Radius * f;
                                s.StartAngle = a.StartAngle * 180 / Math.PI; s.EndAngle = a.EndAngle * 180 / Math.PI;
                                break;
                            case Circle c:
                                s.Kind = "circle"; s.Center = P(c.Center, f); s.Radius = c.Radius * f;
                                break;
                            case Polyline pl:
                                s.Kind = "polyline"; s.Closed = pl.Closed; s.Bulges = new List<double>();
                                for (int i = 0; i < pl.NumberOfVertices; i++) { var v = pl.GetPoint2dAt(i); s.Points.Add(P(new Point3d(v.X, v.Y, 0), f)); s.Bulges.Add(pl.GetBulgeAt(i)); }
                                break;
                            case AttributeDefinition ad:
                                s.Kind = "attdef"; s.Center = P(ad.Position, f); s.Height = ad.Height * f; s.Tag = ad.Tag.ToUpperInvariant();
                                s.Prompt = ad.Prompt; s.Default = ad.TextString; s.Rotation = ad.Rotation * 180 / Math.PI; s.Invisible = ad.Invisible; s.Constant = ad.Constant;
                                s.TextStyle = ((TextStyleTableRecord)tr.GetObject(ad.TextStyleId, OpenMode.ForRead)).Name;
                                s.Format = FormatOf(ad, f);
                                break;
                            case DBText t:
                                s.Kind = "text"; s.Center = P(t.Position, f); s.Height = t.Height * f; s.Text = t.TextString; s.Rotation = t.Rotation * 180 / Math.PI;
                                s.TextStyle = ((TextStyleTableRecord)tr.GetObject(t.TextStyleId, OpenMode.ForRead)).Name;
                                s.Format = FormatOf(t, f);
                                break;
                            default:
                                string kind = ent.GetType().Name;
                                unsupported[kind] = unsupported.TryGetValue(kind, out int n) ? n + 1 : 1;
                                continue;
                        }
                        if (s.TextStyle != null && !Db.TextStyleExists(ctx, s.TextStyle)) s.TextStyle = null;
                        p.Entities.Add(s);
                    }
                    tr.Commit();
                }
            }
            if (unsupported.Count > 0)
                OpContext.Error(issues, args, "fromFile", "file_content_unsupported", "'" + path + "' holds entities this op cannot rebuild without Database.Insert: " +
                    string.Join(", ", unsupported.Select(kv => kv.Value + " " + kv.Key)) + ". Supported: lines, arcs, circles, lightweight polylines, single-line text, attribute definitions.");
            if (layersMissing.Count > 0)
                OpContext.Warning(issues, args, "fromFile", "file_layers_mapped", "content on layers not in this drawing goes on layer 0: " + string.Join(", ", layersMissing.Take(10)));
            p.FromFile = path;
        }

        private static TextFormat FormatOf(DBText t, double f) => new TextFormat
        {
            Horizontal = t.HorizontalMode, Vertical = t.VerticalMode, Alignment = P(t.AlignmentPoint, f), WidthFactor = t.WidthFactor, Oblique = t.Oblique,
        };

        private static Entity Formatted(OpContext ctx, DBText t, TextFormat? format)
        {
            if (format is null) return t;
            t.WidthFactor = format.WidthFactor;
            t.Oblique = format.Oblique;
            t.HorizontalMode = format.Horizontal;
            t.VerticalMode = format.Vertical;
            if (!t.IsDefaultAlignment) t.AlignmentPoint = ctx.Units.ToDrawing(format.Alignment);
            return t;
        }

        /// <summary>A source drawing point as the job-unit value that converts back to the same coordinates.</summary>
        private static Point2 P(Point3d p, double f) => new Point2(p.X * f, p.Y * f);

        protected override DefinePlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var existing = DraftDb.BlockId(tr, db, p.Name);
            if (!existing.IsNull && !p.Redefine)
            {
                p.State = "unchanged";
                p.Handle = OpContext.HandleOf(existing);
                result.Unchanged.Add(p.Handle);
                return p;
            }
            BlockTableRecord btr;
            if (existing.IsNull)
            {
                btr = new BlockTableRecord { Name = p.Name };
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                var id = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
                p.State = "created";
                p.Handle = OpContext.HandleOf(id);
                result.Created.Add(p.Handle);
            }
            else
            {
                btr = (BlockTableRecord)tr.GetObject(existing, OpenMode.ForWrite);
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased) continue;
                    var old = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                    old.Erase();
                    result.Deleted.Add(OpContext.HandleOf(id));
                }
                p.State = "redefined";
                p.Handle = OpContext.HandleOf(existing);
                result.Modified.Add(p.Handle);
                if (p.ReferencesAffected > 0 && p.Entities.Any(e => e.Kind == "attdef"))
                    ctx.Warn("Block '" + p.Name + "' was redefined; its " + p.ReferencesAffected + " existing references show the new geometry but keep their old attribute references (run ATTSYNC in AutoCAD to update them).");
            }
            btr.Origin = ctx.Units.ToDrawing(p.BasePoint);
            btr.Units = p.Units;
            btr.Annotative = p.Annotative ? AnnotativeStates.True : AnnotativeStates.False;
            if (p.Annotative) btr.BlockScaling = BlockScaling.Uniform;
            var styleId = db.Textstyle;
            foreach (var s in p.Entities)
            {
                var ent = Make(ctx, s, styleId);
                ent.SetDatabaseDefaults(db);
                ent.Layer = s.Layer;
                if (s.Layer == "0")
                {
                    ent.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
                    ent.Linetype = "ByBlock";
                    ent.LineWeight = LineWeight.ByBlock;
                }
                else
                {
                    ent.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    ent.Linetype = "ByLayer";
                    ent.LineWeight = LineWeight.ByLayer;
                }
                btr.AppendEntity(ent);
                tr.AddNewlyCreatedDBObject(ent, true);
                if (ent is DBText t && (t.HorizontalMode != TextHorizontalMode.TextLeft || t.VerticalMode != TextVerticalMode.TextBase)) t.AdjustAlignment(db);
            }
            return p;
        }

        private static Entity Make(OpContext ctx, EntitySpec s, ObjectId defaultStyle)
        {
            var u = ctx.Units;
            ObjectId style = s.TextStyle != null ? Db.SymbolId(ctx.Transaction, ctx.Database.TextStyleTableId, s.TextStyle) : defaultStyle;
            if (style.IsNull) style = defaultStyle;
            switch (s.Kind)
            {
                case "line": return new Line(u.ToDrawing(s.Points[0]), u.ToDrawing(s.Points[1]));
                case "polyline":
                {
                    var pl = new Polyline();
                    for (int i = 0; i < s.Points.Count; i++) pl.AddVertexAt(i, u.ToDrawing2d(s.Points[i]), s.Bulges != null ? s.Bulges[i] : 0, 0, 0);
                    pl.Closed = s.Closed;
                    return pl;
                }
                case "circle": return new Circle(u.ToDrawing(s.Center), Vector3d.ZAxis, u.ToDrawing(s.Radius));
                case "arc": return new Arc(u.ToDrawing(s.Center), u.ToDrawing(s.Radius), s.StartAngle * Math.PI / 180, s.EndAngle * Math.PI / 180);
                case "text":
                    return Formatted(ctx, new DBText { Position = u.ToDrawing(s.Center), TextString = s.Text, Height = u.ToDrawing(s.Height), TextStyleId = style, Rotation = s.Rotation * Math.PI / 180 }, s.Format);
                case "attdef":
                {
                    var ad = new AttributeDefinition(u.ToDrawing(s.Center), s.Default, s.Tag, s.Prompt, style)
                    {
                        Height = u.ToDrawing(s.Height), Rotation = s.Rotation * Math.PI / 180, Invisible = s.Invisible, Constant = s.Constant, LockPositionInBlock = true,
                    };
                    return Formatted(ctx, ad, s.Format);
                }
                default: throw new InvalidOperationException("Unknown entity kind '" + s.Kind + "'.");
            }
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, DefinePlan p)
        {
            var tr = ctx.Transaction;
            var id = DraftDb.BlockId(tr, ctx.Database, p.Name);
            if (id.IsNull) return Mismatch(result, "Block '" + p.Name + "' is not defined after the op.");
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var counts = new Dictionary<string, int>();
            int notByBlock = 0;
            foreach (ObjectId eid in btr)
            {
                if (eid.IsErased) continue;
                var e = (Entity)tr.GetObject(eid, OpenMode.ForRead);
                string k = e is AttributeDefinition ? "attdef" : e is DBText ? "text" : e is Line ? "line" : e is Polyline ? "polyline" : e is Arc ? "arc" : e is Circle ? "circle" : e.GetType().Name;
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                if (e.Layer == "0" && !(e.Color.IsByBlock && e.LineWeight == LineWeight.ByBlock && string.Equals(e.Linetype, "ByBlock", StringComparison.OrdinalIgnoreCase))) notByBlock++;
            }
            var countsJson = new JsonObject();
            foreach (var kv in counts) countsJson.Add(kv.Key, kv.Value);
            var tags = BlockSupport.Tags(tr, btr);
            var basePt = ctx.Units.ToJob(btr.Origin);
            result.Readback.Add("name", btr.Name).Add("handle", OpOrHandle(id)).Add("state", p.State).Add("entities", countsJson)
                .Add("attributeTags", JsonArray.FromStrings(tags)).Add("basePoint", SA.AutoCAD.Host.DrawingUnits.PointJson(basePt))
                .Add("units", btr.Units.ToString()).Add("annotative", Anno.IsAnnotative(btr)).Add("referencesAffected", p.ReferencesAffected);
            if (p.FromFile != null) result.Readback.Add("fromFile", p.FromFile);
            if (p.State == "unchanged") return true;
            var mismatches = new List<string>();
            var want = p.Entities.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
            foreach (var kv in want)
                if (!counts.TryGetValue(kv.Key, out int got) || got != kv.Value) mismatches.Add(kv.Key + ": " + (counts.TryGetValue(kv.Key, out int g2) ? g2 : 0) + " read back, " + kv.Value + " intended");
            if (counts.Keys.Any(k => !want.ContainsKey(k))) mismatches.Add("the block holds unexpected entities: " + string.Join(", ", counts.Keys.Where(k => !want.ContainsKey(k))));
            if (basePt.DistanceTo(p.BasePoint) > ctx.LengthTolerance) mismatches.Add("the base point reads back as " + basePt);
            if (btr.Units != p.Units) mismatches.Add("the block units read back as " + btr.Units);
            if (Anno.IsAnnotative(btr) != p.Annotative) mismatches.Add("annotative reads back as " + Anno.IsAnnotative(btr));
            if (notByBlock > 0) mismatches.Add(notByBlock + " entities on layer 0 are not ByBlock");
            var wantTags = p.Entities.Where(e => e.Kind == "attdef").Select(e => e.Tag.ToUpperInvariant()).ToList();
            if (!wantTags.SequenceEqual(tags)) mismatches.Add("attribute tags read back as " + string.Join(", ", tags));
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }

        private static string OpOrHandle(ObjectId id) => OpContext.HandleOf(id);
    }
}
