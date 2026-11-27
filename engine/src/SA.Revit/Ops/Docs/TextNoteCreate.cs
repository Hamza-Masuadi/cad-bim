using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Docs
{
    /// <summary>
    /// text.note: one text note in a view, through TextNote.Create(doc, viewId, position, [width,] text,
    /// TextNoteOptions { TypeId, HorizontalAlignment, Rotation }).
    ///
    /// <para><b>Position convention.</b> Plans: [x, y] in project coordinates, at the view's level elevation.
    /// Drafting views and legends: [x, y] in the view's own coordinates. Sheets: sheet coordinates in paper units
    /// (OpContext.Paper). Sections, elevations and detail views:
    /// x = distance along the view's right direction from View.Origin, y = elevation above the project base point.
    /// The API documents the position as the text box's top-left corner for left alignment, top-right for right and
    /// top-middle for centre. Width is in paper units and must lie between TextElement.GetMinimumAllowedWidth and
    /// GetMaximumAllowedWidth for the type; omitted, the note does not wrap.</para>
    ///
    /// <para><b>Refused.</b> Text that repeats a door or window Mark, or a room number or name, exactly: typed text
    /// instead of tags goes stale (RV-009); use tag.all. 3D views, schedules and templates. A note on a sheet gets
    /// a warning (RV-046, annotate in views).</para>
    /// </summary>
    public sealed class TextNoteCreate : OpHandler
    {
        public override string Name => "text.note";

        private sealed class Plan
        {
            public View View = null!;
            public string Text = "";
            public TextNoteType Type = null!;
            public double? Width;
            public double Rotation;
            public HorizontalTextAlignment Align;
            public XYZ Position = XYZ.Zero;
        }

        internal static XYZ Place(OpContext ctx, View v, Point2 p) => W2.ToModel(ctx, v, p);

        private static Point2 Requested(OpContext ctx, OpArgs args, View v) => v is ViewSheet ? ctx.PaperPoint(args, "position") : args.GetPoint2("position");

        private static Point2 Back(OpContext ctx, View v, XYZ q) => W2.FromModel(ctx, v, q);

        internal static string Normalise(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan { Text = args.GetString("text"), Rotation = ctx.Units.AngleToInternal(args.GetAngle("rotation")) };
            if (p.Text.Trim().Length == 0) OpContext.Error(issues, ctx.FieldPath(args, "text"), RevitIssueCodes.Invalid, "text is empty");
            switch (args.GetString("align"))
            {
                case "center": p.Align = HorizontalTextAlignment.Center; break;
                case "right": p.Align = HorizontalTextAlignment.Right; break;
                default: p.Align = HorizontalTextAlignment.Left; break;
            }
            if (args.GetStringOrNull("type") is string tn)
            {
                var f = Lookup.Type<TextNoteType>(doc, tn, "text note type");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "type"), f.Code, f.Error!);
                else p.Type = f.Value!;
            }
            else
            {
                var id = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                var t = ElementIds.IsValid(id) ? doc.GetElement(id) as TextNoteType : null;
                t ??= new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
                if (t is null) OpContext.Error(issues, ctx.FieldPath(args, "type"), RevitIssueCodes.NotFound, "the model has no text note type");
                else p.Type = t;
            }
            if (ctx.PaperLengthOrNull(args, "width") is double w && p.Type != null)
            {
                double wi = ctx.Paper.LengthToInternal(w);
                double min = TextElement.GetMinimumAllowedWidth(doc, p.Type.Id), max = TextElement.GetMaximumAllowedWidth(doc, p.Type.Id);
                if (wi < min || wi > max)
                    OpContext.Error(issues, ctx.FieldPath(args, "width"), RevitIssueCodes.Invalid, "width must be between " +
                        RevitUnits.Num(ctx.Paper.LengthFromInternal(min)).ToJson() + " and " + RevitUnits.Num(ctx.Paper.LengthFromInternal(max)).ToJson() + " " + SA.Core.Units.Length.Symbol(ctx.Paper.JobUnit) + " (paper) for this type");
                else p.Width = wi;
            }
            // RV-009: typed text that duplicates element data that tags should show.
            string t0 = p.Text.Trim();
            if (t0.Length > 0)
            {
                foreach (var bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
                    foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
                        if (string.Equals(e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()?.Trim(), t0, StringComparison.OrdinalIgnoreCase))
                        {
                            OpContext.Error(issues, ctx.FieldPath(args, "text"), RevitIssueCodes.NotAllowed, "'" + t0 + "' is the Mark of " + (e.Category?.Name ?? "an element") + " " + ElementIds.ToWire(e.Id) + "; tag it (tag.all) instead of typing it (RV-009)");
                            goto doneMarks;
                        }
                foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
                    if (string.Equals(r.Number?.Trim(), t0, StringComparison.OrdinalIgnoreCase) || string.Equals(r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()?.Trim(), t0, StringComparison.OrdinalIgnoreCase))
                    {
                        OpContext.Error(issues, ctx.FieldPath(args, "text"), RevitIssueCodes.NotAllowed, "'" + t0 + "' is the number or name of room " + ElementIds.ToWire(r.Id) + "; tag the room instead of typing it (RV-009)");
                        break;
                    }
                doneMarks:;
            }
            var v = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out bool pending);
            if (pending || v is null) return DocsUtil.HasErrors(issues) ? null : p;
            if (v.IsTemplate || v is View3D || v is ViewSchedule)
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.NotAllowed, "text notes cannot go in " + DocsUtil.Describe(v));
                return null;
            }
            if (v is ViewSheet)
                OpContext.Warning(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "note on a sheet: annotate in the view so the note stays with what it describes (RV-046)");
            p.View = v;
            p.Position = Place(ctx, v, Requested(ctx, args, v));
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var opts = new TextNoteOptions(p.Type.Id) { HorizontalAlignment = p.Align, Rotation = p.Rotation };
            var note = p.Width.HasValue
                ? TextNote.Create(ctx.Doc, p.View.Id, p.Position, p.Width.Value, p.Text, opts)
                : TextNote.Create(ctx.Doc, p.View.Id, p.Position, p.Text, opts);
            result.Created.Add(ElementIds.ToWire(note.Id));
            result.Outputs.Set("note", ElementIds.ToWire(note.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string? id = result.Outputs.GetStringOrNull("note");
            var note = id != null && ElementIds.TryParse(id, out var eid) ? ctx.Doc.GetElement(eid) as TextNote : null;
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            if (note is null || p is null) { check.Fail(note is null ? "the text note was not found after the op" : "could not resolve the request again"); check.Apply(result); return; }
            var rb = result.Readback;
            rb.Set("id", id!);
            check.Text(rb, "text", Normalise(note.Text), Normalise(p.Text));
            check.Text(rb, "type", note.TextNoteType?.Name, p.Type.Name);
            check.Text(rb, "view", ElementIds.ToWire(note.OwnerViewId), ElementIds.ToWire(p.View.Id));
            check.Text(rb, "align", note.HorizontalAlignment.ToString(), p.Align.ToString());
            var at = Back(ctx, p.View, note.Coord);
            var pu = W2.FrameUnits(ctx, p.View);
            check.Point(rb, "position", new XYZ(pu.LengthToInternal(at.X), pu.LengthToInternal(at.Y), 0), Requested(ctx, args, p.View), null, pu);
            if (p.Width.HasValue) check.Length(rb, "width", note.Width, ctx.Paper.LengthFromInternal(p.Width.Value), "width (paper)", ctx.Paper);
            var right = p.View.RightDirection;
            var up = p.View.UpDirection;
            double angle = Math.Atan2(note.BaseDirection.DotProduct(up), note.BaseDirection.DotProduct(right));
            double deg = ctx.Units.AngleFromInternal(angle);
            rb.Set("rotation", RevitUnits.Num(deg));
            double diff = Math.Abs(SA.Core.Units.Tolerance.NormaliseDegrees(deg - args.GetAngle("rotation")));
            if (Math.Min(diff, 360 - diff) > 0.01) check.Fail("rotation: read back " + deg + ", requested " + args.GetAngle("rotation"));
            rb.Set("state", "created");
            check.Apply(result);
        }
    }
}
