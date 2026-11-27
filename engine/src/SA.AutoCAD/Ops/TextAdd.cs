using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Units;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops
{
    /// <summary>
    /// text.add: one DBText (single line) or MText in model space.
    ///
    /// Height: "height" is the model height; with annotative it is the paper height and the model height is
    /// paper x ratio. Paper heights are in <see cref="OpContext.PaperUnit"/> (mm, or inches for an imperial job). "plottedHeight" + "scale" gives model = plotted x ratio. "heightName" + "scale" takes the plotted
    /// height from the standard. Annotative text needs the scale in the drawing's annotation scale list (from "scale", or
    /// CANNOSCALE when no scale is given); CANNOSCALE is switched to that scale for the op and restored after the job, so
    /// the object's only scale context is the requested one.
    ///
    /// Justification: t/m/b + l/c/r. For single-line text "b" is the baseline (AutoCAD's Left, Center and Right
    /// justifications); "bl" is plain Left and uses Position, every other value uses AlignmentPoint.
    /// </summary>
    internal sealed class TextAdd : OpHandler<TextAdd.TextPlan>
    {
        public override string Name => "text.add";

        internal sealed class TextPlan
        {
            public string Text = "";
            public Point2 Position;
            /// <summary>Model height in job units.</summary>
            public double ModelHeight;
            public double RotationDegrees;
            public string Justify = "bl";
            public bool Multiline;
            public double? Width;
            public bool Annotative;
            public string? AnnotationScaleName;
            public string Style = "";
            public string Layer = "";
            public string Handle = "";
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        private static TextPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new TextPlan
            {
                Text = args.GetString("text"),
                Position = args.GetPoint2("position"),
                RotationDegrees = args.GetAngle("rotation"),
                Justify = args.GetString("justify"),
                Multiline = args.GetBool("multiline"),
                Width = args.GetLengthOrNull("width"),
                Annotative = args.GetBool("annotative"),
            };
            if (p.Text.Length == 0) OpContext.Error(issues, args, "text", IssueCodes.TextEmpty, "text is empty");
            if (!p.Multiline && (p.Text.Contains("\n") || p.Text.Contains("\\P")))
                OpContext.Error(issues, args, "text", IssueCodes.TextMultiline, "single-line text cannot contain line breaks; set multiline: true");
            if (p.Width.HasValue && !p.Multiline)
                OpContext.Warning(issues, args, "width", IssueCodes.WidthIgnored, "width applies to multiline text only");

            string? scaleText = args.GetStringOrNull("scale");
            double ratio = 1;
            if (scaleText != null && !Scale.TryParseRatio(scaleText, out ratio))
                OpContext.Error(issues, args, "scale", IssueCodes.ScaleInvalid, "'" + scaleText + "' is not a scale such as 1:100");

            if (p.Annotative)
            {
                AnnotationScale? anno = null;
                if (scaleText != null) anno = AnnotationScales.Find(ctx.Database, scaleText);
                else
                {
                    try { anno = ctx.Database.Cannoscale; } catch { anno = null; }
                    if (anno != null) ratio = AnnotationScales.Ratio(ctx.Database, anno);
                }
                if (anno is null && !(ctx.IsValidating && ctx.Plan.Contains(PlannedNames.AnnotationScale, scaleText ?? "")))
                    OpContext.Error(issues, args, scaleText != null ? "scale" : "annotative", "annotation_scale_missing",
                        "annotation scale " + (scaleText ?? "(current)") + " is not in the drawing's scale list; add it with doc.setup first");
                p.AnnotationScaleName = anno?.Name ?? scaleText;
            }

            if (args.Has("height"))
            {
                p.ModelHeight = p.Annotative ? ctx.PaperLength(args, "height") * ratio : args.GetLength("height");
            }
            else if (args.Has("plottedHeight"))
            {
                p.ModelHeight = ctx.PaperLength(args, "plottedHeight") * ratio;
            }
            else if (args.Has("heightName"))
            {
                string name = args.GetString("heightName");
                if (ctx.Standard is null)
                    OpContext.Error(issues, args, "heightName", IssueCodes.StandardMissing, "heightName needs the job to name a standard");
                else if (ctx.Standard.FindTextHeight(name) is null)
                    OpContext.Error(issues, args, "heightName", IssueCodes.TextHeightUnknown, "the standard has no text height named '" + name + "'");
                else p.ModelHeight = ctx.Standard.ModelTextHeight(name, ratio, ctx.JobUnit);
            }
            if (!(p.ModelHeight > 0) && issues.TrueForAll(i => !i.IsError))
                OpContext.Error(issues, args, "height", IssueCodes.HeightInvalid, "the text height works out as " + p.ModelHeight + "; it must be positive");

            p.Style = args.GetStringOrNull("style") ?? Db.Read(ctx, tr => ((TextStyleTableRecord)tr.GetObject(ctx.Database.Textstyle, OpenMode.ForRead)).Name);
            if (args.Has("style") && !ctx.IsRef(args, "style") && !Db.TextStyleExists(ctx, p.Style))
                OpContext.Error(issues, args, "style", IssueCodes.TextStyleMissing, "text style '" + p.Style + "' is not in the drawing; add it with textstyle.ensure first");
            p.Layer = args.GetStringOrNull("layer") ?? Db.CurrentLayerName(ctx);
            if (args.Has("layer") && !ctx.IsRef(args, "layer") && !Db.LayerExists(ctx, p.Layer))
                OpContext.Error(issues, args, "layer", IssueCodes.LayerMissing, "layer '" + p.Layer + "' is not in the drawing; add it with layer.ensure first");
            return p;
        }

        protected override TextPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var styleId = Db.SymbolId(tr, db.TextStyleTableId, p.Style);
            if (styleId.IsNull) throw new InvalidOperationException("Text style '" + p.Style + "' is missing.");
            if (Db.SymbolId(tr, db.LayerTableId, p.Layer).IsNull) throw new InvalidOperationException("Layer '" + p.Layer + "' is missing.");

            if (p.Annotative && p.AnnotationScaleName != null)
            {
                var current = db.Cannoscale;
                if (current is null || !string.Equals(current.Name, p.AnnotationScaleName, StringComparison.OrdinalIgnoreCase))
                    ctx.SetSysVar("CANNOSCALE", p.AnnotationScaleName);
            }

            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);
            var pos = ctx.Units.ToDrawing(p.Position);
            double rotation = p.RotationDegrees * Math.PI / 180.0;
            Entity entity;
            if (p.Multiline)
            {
                var m = new MText();
                m.SetDatabaseDefaults(db);
                m.Location = pos;
                m.Contents = p.Text;
                m.TextStyleId = styleId;
                m.TextHeight = ctx.Units.ToDrawing(p.ModelHeight);
                m.Width = p.Width.HasValue ? ctx.Units.ToDrawing(p.Width.Value) : 0;
                m.Rotation = rotation;
                m.Attachment = Attachment(p.Justify);
                m.Layer = p.Layer;
                entity = m;
                Db.Append(ctx, ms, m);
            }
            else
            {
                var t = new DBText();
                t.SetDatabaseDefaults(db);
                t.TextString = p.Text;
                t.TextStyleId = styleId;
                t.Height = ctx.Units.ToDrawing(p.ModelHeight);
                t.Rotation = rotation;
                t.Layer = p.Layer;
                t.Position = pos;
                if (p.Justify != "bl")
                {
                    t.HorizontalMode = Horizontal(p.Justify);
                    t.VerticalMode = Vertical(p.Justify);
                    t.AlignmentPoint = pos;
                }
                entity = t;
                Db.Append(ctx, ms, t);
                // Recomputes Position from AlignmentPoint; needs the text to be database-resident.
                if (p.Justify != "bl") t.AdjustAlignment(db);
            }
            if (p.Annotative) entity.Annotative = AnnotativeStates.True;
            p.Handle = OpContext.HandleOf(entity.ObjectId);
            result.Created.Add(p.Handle);
            result.Outputs.Add("height", AcadValues.Round(p.ModelHeight));
            return p;
        }

        private static AttachmentPoint Attachment(string j)
        {
            switch (j)
            {
                case "tl": return AttachmentPoint.TopLeft;
                case "tc": return AttachmentPoint.TopCenter;
                case "tr": return AttachmentPoint.TopRight;
                case "ml": return AttachmentPoint.MiddleLeft;
                case "mc": return AttachmentPoint.MiddleCenter;
                case "mr": return AttachmentPoint.MiddleRight;
                case "bc": return AttachmentPoint.BottomCenter;
                case "br": return AttachmentPoint.BottomRight;
                default: return AttachmentPoint.BottomLeft;
            }
        }

        private static TextHorizontalMode Horizontal(string j) =>
            j[1] == 'c' ? TextHorizontalMode.TextCenter : j[1] == 'r' ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft;

        private static TextVerticalMode Vertical(string j) =>
            j[0] == 't' ? TextVerticalMode.TextTop : j[0] == 'm' ? TextVerticalMode.TextVerticalMid : TextVerticalMode.TextBase;

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, TextPlan p)
        {
            var id = ctx.IdOf(p.Handle);
            if (id.IsNull) return Mismatch(result, "The text " + p.Handle + " is not in the drawing after the op.");
            var tr = ctx.Transaction;
            var e = (Entity)tr.GetObject(id, OpenMode.ForRead);
            Point2 at;
            double height, rotation;
            string text, style;
            if (e is MText m)
            {
                at = ctx.Units.ToJob(m.Location);
                height = ctx.Units.ToJob(m.TextHeight);
                rotation = m.Rotation;
                text = m.Contents;
                style = ((TextStyleTableRecord)tr.GetObject(m.TextStyleId, OpenMode.ForRead)).Name;
                result.Readback.Add("kind", "mtext").Add("attachment", m.Attachment.ToString());
            }
            else if (e is DBText t)
            {
                at = ctx.Units.ToJob(p.Justify == "bl" ? t.Position : t.AlignmentPoint);
                height = ctx.Units.ToJob(t.Height);
                rotation = t.Rotation;
                text = t.TextString;
                style = ((TextStyleTableRecord)tr.GetObject(t.TextStyleId, OpenMode.ForRead)).Name;
                result.Readback.Add("kind", "text").Add("horizontalMode", t.HorizontalMode.ToString()).Add("verticalMode", t.VerticalMode.ToString());
            }
            else return Mismatch(result, "Object " + p.Handle + " is not text.");

            double rotDeg = rotation * 180.0 / Math.PI;
            bool annotative = e.Annotative == AnnotativeStates.True;
            result.Readback
                .Add("handle", p.Handle)
                .Add("text", text)
                .Add("position", DrawingUnits.PointJson(at))
                .Add("height", AcadValues.Round(height))
                .Add("rotation", AcadValues.Round(rotDeg))
                .Add("style", style)
                .Add("layer", e.Layer)
                .Add("annotative", annotative);

            bool ok = true;
            double tol = ctx.LengthTolerance;
            if (at.DistanceTo(p.Position) > tol) ok = Mismatch(result, "Text position read back is " + at + ", requested " + p.Position + ".");
            if (Math.Abs(height - p.ModelHeight) > tol) ok = Mismatch(result, "Text height read back is " + height + ", requested " + p.ModelHeight + ".");
            if (!Tolerance.AngleEqual(rotDeg, p.RotationDegrees, 1e-6)) ok = Mismatch(result, "Text rotation read back differs.");
            if (text != p.Text) ok = Mismatch(result, "Text contents read back differ.");
            if (!string.Equals(style, p.Style, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "Text style read back is '" + style + "'.");
            if (!string.Equals(e.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) ok = Mismatch(result, "Text layer read back is '" + e.Layer + "'.");
            if (annotative != p.Annotative) ok = Mismatch(result, "Annotative read back is " + annotative + ".");
            return ok;
        }
    }
}
