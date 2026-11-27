using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.Core.Geometry;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Standards;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Drafting
{
    /// <summary>
    /// mleader.add: one MLeader with MText content in the named multileader style (default: the current one).
    /// The leader line has its head at arrowPoint (AddLeaderLine) and its tail at landingPoint (AddLastVertex); the
    /// dogleg points away from the arrow (+X when the landing is right of the arrow, -X otherwise) with the style's
    /// dogleg length, and the text is right-aligned when it sits left of the arrow.
    /// Text height: from the standard ("notes", else "body") when it has text heights: the plotted height at the current
    /// annotation scale (model height for a non-annotative style, the same model height at the current scale for an
    /// annotative one); otherwise the style's text height times its scale (non-annotative) or times the annotation
    /// scale (annotative). The MText gets the style's text style and that height, and the leader's own TextHeight is set
    /// to it divided by the style's scale (non-annotative) or by the annotation scale (annotative, paper units). The
    /// readback fails the op when the leader's text height differs from the intended one. Annotative styles: CANNOSCALE
    /// is used and the leader is made annotative after append.
    /// </summary>
    internal sealed class MLeaderAdd : OpHandler<MLeaderAdd.LeaderPlan>
    {
        public override string Name => "mleader.add";

        internal sealed class LeaderPlan
        {
            public string Text = "";
            public Point2 Arrow, Landing;
            public string Style = "";
            public string Layer = "";
            public bool Annotative;
            public string? Scale;
            /// <summary>Model height in job units; 0 until known (execution).</summary>
            public double Height;
            public string HeightSource = "";
            public string Handle = "";
            public int LineIndex;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Plan(ctx, args, issues);

        internal static ObjectId StyleId(Transaction tr, Database db, string name) => DraftDb.DictionaryEntry(tr, db.MLeaderStyleDictionaryId, name);

        private static LeaderPlan Plan(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var db = ctx.Database;
            var p = new LeaderPlan
            {
                Text = args.GetString("text"),
                Arrow = args.GetPoint2("arrowPoint"),
                Landing = args.GetPoint2("landingPoint"),
            };
            if (p.Text.Trim().Length == 0) OpContext.Error(issues, args, "text", IssueCodes.TextEmpty, "the note is empty");
            if (p.Arrow.DistanceTo(p.Landing) <= ctx.LengthTolerance) OpContext.Error(issues, args, "landingPoint", "leader_zero", "the arrow and landing points coincide");
            string? given = args.GetStringOrNull("style");
            bool found = Db.Read(ctx, tr =>
            {
                var id = given != null ? StyleId(tr, db, given) : db.MLeaderstyle;
                if (id.IsNull) return false;
                var st = (MLeaderStyle)tr.GetObject(id, OpenMode.ForRead);
                p.Style = st.Name;
                p.Annotative = Anno.IsAnnotative(st);
                if (st.ContentType != ContentType.MTextContent)
                    OpContext.Warning(issues, args, "style", "mleader_content", "style '" + st.Name + "' is not set up for text content; the leader gets text content anyway");
                var std = ctx.Standard;
                TextHeight? th = std?.FindTextHeight("notes") ?? std?.FindTextHeight("body");
                var cur = Anno.Current(db);
                double dpp = cur is null ? 1 : Anno.DrawingPerPaper(cur);
                if (th != null)
                {
                    p.Height = Anno.ModelFromPlotted(ctx, th.Plotted, th.Unit);
                    p.HeightSource = "standard '" + th.Name + "' at " + Anno.CurrentName(db);
                    double styleModel = p.Annotative ? st.TextHeight * dpp : st.TextHeight * (st.Scale > 0 ? st.Scale : 1);
                    if (Math.Abs(ctx.Units.ToJob(styleModel) - p.Height) > ctx.LengthTolerance)
                        OpContext.Warning(issues, args, "style", "mleader_height", "style '" + st.Name + "' gives text " + AcadValues.Round(ctx.Units.ToJob(styleModel), 3) +
                            " high at this scale; the standard's '" + th.Name + "' height " + AcadValues.Round(p.Height, 3) + " is used for this note");
                }
                else
                {
                    double model = p.Annotative ? st.TextHeight * dpp : st.TextHeight * (st.Scale > 0 ? st.Scale : 1);
                    p.Height = ctx.Units.ToJob(model);
                    p.HeightSource = "style '" + st.Name + "'";
                }
                return true;
            });
            if (!found)
            {
                p.Style = given ?? "";
                OpContext.Error(issues, args, "style", "mleaderstyle_missing", "multileader style '" + given + "' is not in the drawing." +
                    Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.DictionaryNames(tr, db.MLeaderStyleDictionaryId), given ?? "")));
            }
            if (p.Annotative) { p.Scale = Anno.CurrentName(db); if (Anno.Current(db) is null) OpContext.Error(issues, args, "style", IssueCodes.AnnotationScaleMissing, "annotative leaders need a current annotation scale"); }
            p.Layer = DraftDb.Layer(ctx, args, issues, "layer", "leaders");
            return p;
        }

        protected override LeaderPlan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Plan(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var styleId = StyleId(tr, db, p.Style);
            if (styleId.IsNull) throw new InvalidOperationException("Multileader style '" + p.Style + "' is missing.");
            var style = (MLeaderStyle)tr.GetObject(styleId, OpenMode.ForRead);
            if (p.Annotative) Anno.UseScale(ctx, p.Scale);
            var ms = Db.ModelSpace(tr, db, OpenMode.ForWrite);

            bool right = p.Landing.X >= p.Arrow.X;
            var ml = new MLeader();
            ml.SetDatabaseDefaults(db);
            ml.MLeaderStyle = styleId;
            ml.Layer = p.Layer;
            ml.ContentType = ContentType.MTextContent;
            var mt = new MText();
            mt.SetDatabaseDefaults(db);
            mt.Contents = p.Text;
            mt.TextStyleId = style.TextStyleId;
            mt.TextHeight = ctx.Units.ToDrawing(p.Height);
            mt.Location = ctx.Units.ToDrawing(p.Landing);
            ml.MText = mt;
            // The leader keeps its own text height, which the style's height (times the leader scale) would otherwise
            // supply when the leader recomputes; the MText height alone is not enough. Annotative: paper units.
            double leaderScale = p.Annotative ? DrawingPerPaper(db) : (style.Scale > 0 ? style.Scale : 1);
            ml.TextHeight = ctx.Units.ToDrawing(p.Height) / leaderScale;
            ml.TextAlignmentType = right ? TextAlignmentType.LeftAlignment : TextAlignmentType.RightAlignment;
            int line = ml.AddLeaderLine(ctx.Units.ToDrawing(p.Arrow));
            ml.AddLastVertex(line, ctx.Units.ToDrawing(p.Landing));
            p.LineIndex = line;
            int leader = ml.GetLeaderIndex(line);
            double dog = style.DoglegLength * (p.Annotative ? DrawingPerPaper(db) : (style.Scale > 0 ? style.Scale : 1));
            if (style.EnableDogleg && dog > 0) ml.SetDogleg(leader, new Vector3d(right ? dog : -dog, 0, 0));
            var id = Db.Append(ctx, ms, ml);
            if (p.Annotative) ml.Annotative = AnnotativeStates.True;
            p.Handle = OpContext.HandleOf(id);
            result.Created.Add(p.Handle);
            return p;
        }

        private static double DrawingPerPaper(Database db)
        {
            var s = Anno.Current(db);
            return s is null ? 1 : Anno.DrawingPerPaper(s);
        }

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, LeaderPlan p)
        {
            var id = ctx.IdOf(p.Handle);
            if (id.IsNull) return Mismatch(result, "The leader " + p.Handle + " is not in the drawing after the op.");
            var tr = ctx.Transaction;
            if (!(tr.GetObject(id, OpenMode.ForRead) is MLeader ml)) return Mismatch(result, "Object " + p.Handle + " is not a multileader.");
            var mismatches = new List<string>();
            int lines = ml.LeaderLineCount;
            Point2 head = default, tail = default;
            if (lines < 1) mismatches.Add("the leader has no leader line");
            else
            {
                int li = p.LineIndex;
                head = ctx.Units.ToJob(ml.GetFirstVertex(li));
                tail = ctx.Units.ToJob(ml.GetLastVertex(li));
                if (head.DistanceTo(p.Arrow) > ctx.LengthTolerance) mismatches.Add("the arrow point reads back as " + head);
                if (tail.DistanceTo(p.Landing) > ctx.LengthTolerance) mismatches.Add("the landing point reads back as " + tail);
            }
            string text = ml.ContentType == ContentType.MTextContent && ml.MText != null ? ml.MText.Contents : "";
            double height = ml.MText != null ? ctx.Units.ToJob(ml.MText.TextHeight) : 0;
            string style = ((MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead)).Name;
            bool anno = Anno.IsAnnotative(ml);
            result.Readback.Add("handle", p.Handle).Add("text", text).Add("arrowPoint", DrawingUnits.PointJson(head)).Add("landingPoint", DrawingUnits.PointJson(tail))
                .Add("textLocation", DrawingUnits.PointJson(ctx.Units.ToJob(ml.TextLocation))).Add("textHeight", AcadValues.Round(height))
                .Add("textHeightSource", p.HeightSource).Add("style", style).Add("layer", ml.Layer).Add("annotative", anno).Add("leaderLines", lines);
            if (text != p.Text) mismatches.Add("the note text reads back as '" + text + "'");
            if (!string.Equals(style, p.Style, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the style reads back as '" + style + "'");
            if (!string.Equals(ml.Layer, p.Layer, StringComparison.OrdinalIgnoreCase)) mismatches.Add("the layer reads back as '" + ml.Layer + "'");
            if (anno != p.Annotative) mismatches.Add("annotative reads back as " + anno);
            result.Readback.Add("leaderTextHeight", AcadValues.Round(ml.TextHeight, 6)).Add("leaderScale", AcadValues.Round(ml.Scale, 6));
            if (Math.Abs(height - p.Height) > ctx.LengthTolerance)
                mismatches.Add("the note's text height reads back as " + AcadValues.Round(height, 4) + ", intended " + AcadValues.Round(p.Height, 4) + " (" + p.HeightSource + ")");
            foreach (var m in mismatches) Mismatch(result, m);
            return mismatches.Count == 0;
        }
    }
}
