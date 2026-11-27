using System.Collections.Generic;
using SA.Core.Json;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>
    /// Wave-1 AutoCAD op schemas. Lengths are in job units; points are model coordinates unless a field says paper.
    /// Element ids in results are object handles.
    /// </summary>
    public static class AutoCadOps
    {
        private static OpSchema Op(string name, string summary, FieldSpec[] fields, K[]? constraints = null, FieldSpec[]? outputs = null,
            bool ensure = false, bool destructive = false, bool script = false) =>
            new OpSchema(name, AppKind.AutoCad, summary, fields, constraints, outputs, ensure, destructive, script);

        private static readonly string[] Justify = { "tl", "tc", "tr", "ml", "mc", "mr", "bl", "bc", "br" };

        public static IEnumerable<OpSchema> All()
        {
            yield return Op("doc.setup", "Set drawing units, precision, angle conventions, linetype scaling and annotation scales; optionally create everything the standard defines.",
                new[]
                {
                    F.Enum("insUnits", "Drawing insertion units (INSUNITS). Should match the job's length unit.", "mm", "cm", "m", "in", "ft"),
                    F.Enum("lengthFormat", "Linear unit display (LUNITS).", "decimal", "architectural", "engineering", "fractional", "scientific"),
                    F.Integer("lengthPrecision", "Decimal places, or the fraction denominator power for architectural (LUPREC).").Range(0, 8),
                    F.Enum("angleFormat", "Angle display (AUNITS).", "decimal_degrees", "dms", "grads", "radians", "surveyor"),
                    F.Integer("anglePrecision", "AUPREC.").Range(0, 8),
                    F.Angle("angleBase", "Direction of 0 degrees (ANGBASE), counter-clockwise from east. Left unchanged when omitted."),
                    F.Enum("angleDirection", "Positive angle direction (ANGDIR). Left unchanged when omitted.", "ccw", "cw"),
                    F.Enum("measurement", "MEASUREMENT: which hatch and linetype files apply.", "metric", "imperial"),
                    F.Number("ltScale", "Global linetype scale (LTSCALE).").Positive(),
                    F.Bool("psLtScale", "Scale linetypes in paper space viewports by the viewport scale (PSLTSCALE). Left unchanged when omitted."),
                    F.Bool("msLtScale", "Scale model space linetypes by the annotation scale (MSLTSCALE). Left unchanged when omitted."),
                    C.StringList("annotationScales", "Scales to add to the scale list, e.g. [\"1:50\", \"1:100\"]."),
                    C.Scale("currentAnnotationScale", "CANNOSCALE for model space."),
                    F.Bool("applyStandard", "Also ensure every layer, linetype, text style and dimension style the job's standard defines.").Def(false),
                    C.Update(),
                },
                outputs: new[] { F.Map("changed", "System variable name to { before, after } for every value changed.") },
                ensure: true);

            yield return Op("layer.ensure", "Create layers with colour, linetype, lineweight and plot flag; existing layers are left alone unless update is true.",
                new[]
                {
                    F.List("layers", "Layers to ensure.", F.OfObject("",
                        F.String("name", "Layer name. Checked against the standard's layer naming scheme.").Req(),
                        C.Color(),
                        F.String("linetype", "Linetype name; loaded if missing.").Def("Continuous"),
                        C.Lineweight(),
                        F.Bool("plot", "Plot the layer.").Def(true),
                        F.String("description", "Layer description."),
                        F.Integer("transparency", "Percent.").Range(0, 90),
                        F.Bool("off", "Turn the layer off.").Def(false),
                        F.Bool("frozen", "Freeze the layer.").Def(false),
                        F.Bool("locked", "Lock the layer.").Def(false))).Req().Count(1),
                    F.Bool("fromStandard", "Take colour, linetype, lineweight and plot flag from the standard's layer list when the entry omits them.").Def(true),
                    F.String("setCurrent", "Make this layer current afterwards."),
                    C.Update(),
                },
                outputs: new[] { F.Map("layers", "Layer name to its handle.") },
                ensure: true);

            yield return Op("linetype.ensure", "Load linetypes from a .lin file.",
                new[]
                {
                    F.List("linetypes", "Linetypes to load.", F.OfObject("",
                        F.String("name", "Linetype name, e.g. HIDDEN2.").Req(),
                        F.String("file", "Linetype file. Defaults to acadiso.lin for metric drawings, acad.lin for imperial."),
                        F.String("description", "Description to set."))).Req().Count(1),
                    C.Update(),
                },
                ensure: true);

            yield return Op("textstyle.ensure", "Create text styles.",
                new[]
                {
                    F.List("styles", "Text styles to ensure.", F.OfObject("",
                        F.String("name", "Style name.").Req(),
                        F.String("font", "Font file or TrueType family, e.g. \"Arial\" or \"romans.shx\".").Req(),
                        F.String("bigFont", "SHX big font, if any."),
                        F.Length("height", "Fixed height; 0 lets each text set its height. A non-annotative style takes the model height; an annotative style takes the paper height, in paper units: mm, or inches for an imperial job (stored in paper millimetres or inches by MEASUREMENT, as doc.setup applyStandard does).").NonNegative().Def(0),
                        F.Number("widthFactor", "Character width factor.").Positive().Def(1),
                        F.Angle("obliqueAngle", "Slant in degrees.").Range(-85, 85).Def(0),
                        F.Bool("annotative", "Annotative style.").Def(false))).Req().Count(1),
                    F.Bool("fromStandard", "Fill omitted settings from the standard's text style of the same name.").Def(true),
                    C.Update(),
                },
                ensure: true);

            yield return Op("dimstyle.ensure", "Create dimension styles from named dimension variable settings.",
                new[]
                {
                    F.List("styles", "Dimension styles to ensure.", F.OfObject("",
                        F.String("name", "Style name.").Req(),
                        F.String("basedOn", "Existing style to copy first. Defaults to the drawing's Standard/ISO-25."),
                        F.Map("settings", "Dimension variables by name, e.g. { \"DIMTXT\": 2.5, \"DIMASZ\": 2.5, \"DIMTXSTY\": \"SA-Text\", \"DIMBLK\": \"ArchTick\" }. Lengths are paper sizes for annotative styles."),
                        F.Bool("annotative", "Annotative style.").Def(true))).Req().Count(1),
                    F.Bool("fromStandard", "Fill omitted settings from the standard's dimension style of the same name.").Def(true),
                    F.String("setCurrent", "Make this style current afterwards."),
                    C.Update(),
                },
                ensure: true);

            yield return Op("wall.draw", "Draw double-line walls from centrelines with thickness; L, T and X junctions are cleaned so no line crosses a joint, and open ends are capped.",
                new[]
                {
                    F.List("walls", "Individual wall segments.", F.OfObject("",
                        F.String("id", "Id of this wall inside the op's result, e.g. \"north\". Defaults to w1, w2... in order.").Matching("[A-Za-z0-9_-]{1,64}"),
                        F.Point2("start", "Centreline start.").Req(),
                        F.Point2("end", "Centreline end.").Req(),
                        F.Length("thickness", "Overall thickness. Defaults to the op's thickness.").Positive(),
                        C.Layer("Layer for this wall's lines."),
                        F.String("wallType", "Wall type name from the standard, recorded on the lines (xdata) and used for layer and hatch."))),
                    F.List("chains", "Connected runs of walls through a list of points; a closed chain draws a room's walls in one go.", F.OfObject("",
                        C.PointList("points", "Centreline points in order.").Req().Count(2),
                        F.Bool("closed", "Join the last point back to the first.").Def(false),
                        F.Length("thickness", "Overall thickness. Defaults to the op's thickness.").Positive(),
                        C.Layer("Layer for this chain's lines."),
                        F.String("wallType", "Wall type name."),
                        F.String("idPrefix", "Ids for the segments become <prefix>1, <prefix>2... Defaults to c<n>_.").Matching("[A-Za-z0-9_-]{1,32}"))),
                    F.Length("thickness", "Default overall thickness for walls that give none.").Positive(),
                    C.Layer("Default layer for wall lines."),
                    F.Enum("justification", "Where the given line lies: on the centre, or on the left or right face looking from start to end.", "centre", "left", "right").Def("centre"),
                    F.Enum("cornerStyle", "Two-wall corners: mitre, or butt (the thicker wall runs through).", "mitre", "butt").Def("mitre"),
                    F.Length("tolerance", "Endpoints this close are joined and an end this close to a wall forms a T. Defaults to 1 mm.").Positive(),
                    F.Bool("capOpenEnds", "Close free wall ends with a cap line.").Def(true),
                    F.Object("hatch", "Hatch each wall piece.",
                        F.String("pattern", "Hatch pattern name, e.g. ANSI31 or SOLID.").Req(),
                        F.Number("scale", "Pattern scale.").Positive().Def(1),
                        F.Angle("angle", "Pattern angle.").Def(0),
                        C.Layer("Hatch layer.")),
                },
                new[] { K.AtLeastOne("walls", "chains") },
                new[]
                {
                    F.List("walls", "{ id, handles, length, thickness, layer } per wall, in order.", F.Of(FieldType.Object)),
                    F.List("junctions", "{ point, kind (corner, tee, cross, inline, other), walls }.", F.Of(FieldType.Object)),
                    F.List("openEnds", "{ wall, point, atStart } for every free end.", F.Of(FieldType.Object)),
                    F.List("loops", "Closed outline loops as point lists; a room gives an inner and an outer loop.", F.Of(FieldType.List)),
                });

            yield return Op("opening.place", "Place a door or window block in a wall, cut the wall lines, and set swing and handing.",
                new[]
                {
                    F.Enum("kind", "Door or window.", "door", "window").Req(),
                    F.String("block", "Block name of the door or window symbol; its width is scaled to the opening width when the block is unit-sized.").Req(),
                    F.Ref("host", "The wall: a wall.draw result (\"$w\") with wallId, or a handle of one of the wall's lines."),
                    F.String("wallId", "Which wall of a wall.draw result, e.g. \"north\"."),
                    F.Length("offset", "Distance along the wall from its start to the opening, measured to the point given by measureTo."),
                    F.Enum("measureTo", "What offset measures to.", "centre", "near_jamb").Def("centre"),
                    F.Point2("point", "Any point on the wall; the opening is centred at its projection onto the centreline."),
                    F.Length("width", "Clear opening width (structural opening for windows).").Positive().Req(),
                    F.Length("height", "Opening height, recorded as an attribute.").Positive(),
                    F.Length("sillHeight", "Window sill height above floor, recorded as an attribute.").NonNegative(),
                    F.Enum("hinge", "Door: the jamb the leaf is hinged on, as seen along the wall direction.", "start", "end").Def("start"),
                    F.Enum("swingSide", "Door: the side of the wall the leaf opens to, looking from wall start to end.", "left", "right").Def("left"),
                    F.Bool("cutWall", "Break the wall lines across the opening and add jamb lines.").Def(true),
                    C.Layer("Layer for the block reference."),
                    F.String("mark", "Door or window mark; written to the MARK attribute when present."),
                    F.Map("attributes", "Attribute tag to value.", C.Str()),
                },
                new[] { K.ExactlyOne("offset", "point"), K.Requires("offset", "host"), K.Requires("wallId", "host") },
                new[]
                {
                    F.Ref("block", "Handle of the inserted block reference."),
                    F.Point2("centre", "Opening centre on the wall centreline."),
                    F.List("jambs", "The two jamb points on the centreline.", C.P2()),
                });

            yield return Op("grid.draw", "Draw structural grid lines with bubbles and labels, from explicit lines or an orthogonal grid definition.",
                new[]
                {
                    F.List("lines", "Explicit grid lines.", F.OfObject("",
                        F.String("label", "Bubble text, e.g. \"A\" or \"1\".").Req(),
                        F.Point2("start", "Line start.").Req(),
                        F.Point2("end", "Line end.").Req())),
                    F.Object("orthogonal", "A rectangular grid.",
                        F.Point2("origin", "Intersection of the first grid in each direction.").Req(),
                        F.List("xSpacings", "Bay widths left to right; gives the vertical grid lines.", C.Len()).Req().Count(1),
                        F.List("ySpacings", "Bay depths bottom to top; gives the horizontal grid lines.", C.Len()).Req().Count(1),
                        F.String("xLabelStart", "First label of the vertical lines; following labels count on (1, 2, 3 or A, B, C).").Def("1"),
                        F.String("yLabelStart", "First label of the horizontal lines.").Def("A"),
                        F.Length("extension", "How far lines run past the outer grids. Defaults to 1.5 bubble diameters.").NonNegative(),
                        F.Angle("rotation", "Rotation of the whole grid about origin.").Def(0)),
                    F.Enum("bubbles", "Which ends get a bubble.", "start", "end", "both").Def("both"),
                    F.Length("bubbleDiameter", "Bubble diameter in model units. Defaults to 10 mm plotted at the current annotation scale.").Positive(),
                    F.String("bubbleBlock", "Block to use as bubble (with a LABEL attribute) instead of a circle and text."),
                    F.String("textStyle", "Text style for labels."),
                    F.String("linetype", "Grid linetype, e.g. CENTER2."),
                    C.Layer("Layer for grid lines and bubbles."),
                },
                new[] { K.AtLeastOne("lines", "orthogonal") },
                new[] { F.List("grids", "{ label, handles, start, end } per grid line.", F.Of(FieldType.Object)) });

            yield return Op("room.create", "Create a closed room boundary, compute its area and place a room tag.",
                new[]
                {
                    C.PointList("boundary", "Boundary points in order (closed automatically).").Count(3),
                    F.Point2("inside", "A point inside the room; the boundary is found from surrounding walls."),
                    F.Ref("from", "A wall.draw result whose inner loops are searched for the inside point. Without it the drawing's lines are used."),
                    F.String("name", "Room name, e.g. \"Kitchen\".").Req(),
                    F.String("number", "Room number, e.g. \"G.04\"."),
                    C.Layer("Layer for the boundary polyline."),
                    F.Bool("tag", "Place a room tag.").Def(true),
                    F.String("tagBlock", "Tag block with NAME, NUMBER and AREA attributes. Defaults to the standard's room tag."),
                    F.Point2("tagPoint", "Tag insertion point. Defaults to the boundary's centroid (or a point inside it)."),
                    F.Enum("areaUnit", "Unit for the AREA attribute text.", "m2", "ft2").Def("m2"),
                    F.Object("hatch", "Hatch the room.",
                        F.String("pattern", "Pattern name.").Req(),
                        F.Number("scale", "Pattern scale.").Positive().Def(1),
                        C.Layer("Hatch layer.")),
                },
                new[] { K.ExactlyOne("boundary", "inside"), K.Requires("from", "inside") },
                new[]
                {
                    F.Ref("boundaryHandle", "Handle of the closed polyline."),
                    F.Number("area", "Area in job units squared."),
                    F.Length("perimeter", "Perimeter."),
                    F.Ref("tagHandle", "Handle of the tag."),
                });

            yield return Op("dim.linear", "Add one linear dimension.",
                new[]
                {
                    F.Point2("start", "First measured point.").Req(),
                    F.Point2("end", "Second measured point.").Req(),
                    F.Length("offset", "Distance from the measured points to the dimension line; positive is to the left looking from start to end.").Req(),
                    F.Enum("orientation", "Measure the true distance, or only its horizontal or vertical component.", "aligned", "horizontal", "vertical").Def("aligned"),
                    F.String("dimStyle", "Dimension style. Defaults to the current style."),
                    C.Layer("Dimension layer."),
                    F.String("textOverride", "Replace the measured text. Avoid: overridden dimensions are flagged by the audit."),
                },
                outputs: new[] { F.Length("measurement", "Measured value read back.") });

            yield return Op("dim.string", "Add a continuous string of dimensions through a list of points, all on one line.",
                new[]
                {
                    C.PointList("points", "Points to dimension, in order along the string.").Req().Count(2),
                    F.Length("offset", "Distance from the first point to the dimension line, perpendicular to the string; positive to the left of the string direction.").Req(),
                    F.Enum("orientation", "Direction of the string.", "aligned", "horizontal", "vertical").Def("aligned"),
                    F.Bool("overall", "Also add an overall dimension from first to last point.").Def(false),
                    F.Length("overallOffset", "Offset of the overall dimension. Defaults to offset plus twice the text height."),
                    F.String("dimStyle", "Dimension style."),
                    C.Layer("Dimension layer."),
                },
                outputs: new[] { F.List("measurements", "Measured values in order.", C.Len()), F.List("handles", "Dimension handles.", C.RefItem()) });

            yield return Op("text.add", "Add single-line or multiline text at a model height, or at a plotted height for a scale.",
                new[]
                {
                    F.String("text", "The text. For multiline text, \\P starts a new paragraph.").Req(),
                    F.Point2("position", "Insertion point.").Req(),
                    F.Length("height", "Model height."),
                    F.Length("plottedHeight", "Height on paper, in paper units: mm, or inches for an imperial job; with scale gives the model height. Or use a named height from the standard via heightName."),
                    F.String("heightName", "Named text height from the standard (e.g. \"body\"), used with scale."),
                    C.Scale(),
                    F.String("style", "Text style."),
                    C.Layer("Text layer."),
                    F.Angle("rotation", "Rotation.").Def(0),
                    F.Enum("justify", "Justification (top/middle/bottom + left/centre/right). For single-line text b means the baseline, not the descender bottom.", Justify).Def("bl"),
                    F.Bool("multiline", "Create multiline text (MTEXT).").Def(false),
                    F.Length("width", "Wrap width for multiline text.").Positive(),
                    F.Bool("annotative", "Make the text annotative (height is then the paper height, in paper units: mm, or inches for an imperial job).").Def(false),
                },
                new[] { K.ExactlyOne("height", "plottedHeight", "heightName"), K.Requires("plottedHeight", "scale"), K.Requires("heightName", "scale") },
                new[] { F.Length("height", "Model height read back.") });

            yield return Op("mleader.add", "Add a multileader note.",
                new[]
                {
                    F.String("text", "Note text.").Req(),
                    F.Point2("arrowPoint", "Where the arrowhead touches.").Req(),
                    F.Point2("landingPoint", "Where the leader meets the text.").Req(),
                    F.String("style", "Multileader style."),
                    C.Layer("Leader layer."),
                });

            yield return Op("table.create", "Create a table with a title, header row and data rows.",
                new[]
                {
                    F.Point2("position", "Top-left corner.").Req(),
                    F.String("title", "Title row text; omitted means no title row."),
                    C.StringList("headers", "Column headings."),
                    F.List("rows", "Data rows; each row is a list of cell texts.", F.OfList(C.Str())).Req(),
                    F.List("columnWidths", "Width per column. Defaults to fit the text.", C.Len()),
                    F.Length("rowHeight", "Row height. Defaults to the table style.").Positive(),
                    F.String("style", "Table style."),
                    C.Layer("Table layer."),
                },
                outputs: new[] { F.Integer("rowCount", "Rows created, excluding title and header."), F.Integer("columnCount", "Columns created.") });

            yield return Op("schedule.fromBlocks", "Collect attribute values from block references into a table and/or a CSV file (door, window, furniture schedules).",
                new[]
                {
                    C.StringList("blocks", "Block names to include (dynamic blocks by their base name).").Req().Count(1),
                    C.StringList("attributes", "Attribute tags, one column each, in order.").Req().Count(1),
                    C.StringList("headers", "Column headings. Defaults to the tags."),
                    F.Enum("space", "Where to look for references.", "model", "all").Def("model"),
                    C.StringList("layers", "Only references on these layers."),
                    C.StringList("sortBy", "Tags to sort by, in priority order. Numbers in values sort numerically."),
                    F.Bool("groupIdentical", "One row per distinct combination of values, with a count column.").Def(false),
                    F.String("countHeader", "Heading of the count column.").Def("QTY"),
                    F.Object("table", "Write a table into the drawing.",
                        F.Point2("position", "Top-left corner.").Req(),
                        F.String("title", "Table title."),
                        F.String("style", "Table style."),
                        C.Layer("Table layer.")),
                    F.String("csv", "Write a CSV file with this name into the output folder."),
                },
                new[] { K.AtLeastOne("table", "csv") },
                new[] { F.Integer("rowCount", "Rows written."), F.String("csvPath", "Full path of the CSV file. A rehearsal writes it to a temporary folder instead, never over the real file."), F.Ref("table", "Table handle.") });

            yield return Op("block.define", "Define a block from simple entities, or from a drawing file.",
                new[]
                {
                    F.String("name", "Block name.").Req(),
                    F.Point2("basePoint", "Base point. Defaults to 0,0, or with fromFile to the source drawing's INSBASE.").Def(C.Origin()),
                    F.List("entities", "Block contents.", F.OfObject("",
                        F.Enum("kind", "Entity kind.", "line", "polyline", "circle", "arc", "text", "attdef").Req(),
                        C.PointList("points", "line: 2 points; polyline: 2 or more."),
                        F.Bool("closed", "polyline: closed.").Def(false),
                        F.Point2("center", "circle, arc: centre. text, attdef: insertion point."),
                        F.Length("radius", "circle, arc: radius.").Positive(),
                        F.Angle("startAngle", "arc: start angle."),
                        F.Angle("endAngle", "arc: end angle."),
                        F.String("text", "text: the text."),
                        F.String("tag", "attdef: tag."),
                        F.String("prompt", "attdef: prompt."),
                        F.String("defaultValue", "attdef: default value."),
                        F.Length("height", "text, attdef: height.").Positive(),
                        F.String("layer", "Entity layer. Defaults to 0 so the block takes the insertion's layer."))),
                    F.String("fromFile", "A .dwg file whose model space becomes the block."),
                    F.Bool("annotative", "Annotative block.").Def(false),
                    F.Enum("units", "Block insertion units.", "unitless", "mm", "cm", "m", "in", "ft"),
                    F.Bool("redefine", "Replace an existing definition of the same name (existing references update).").Def(false),
                },
                new[] { K.ExactlyOne("entities", "fromFile") },
                ensure: true);

            yield return Op("block.insert", "Insert a block at one or many points, with attribute values and dynamic properties.",
                new[]
                {
                    F.String("name", "Block name; must be defined in the drawing or be loadable from the standard's block library.").Req(),
                    F.Point2("position", "Insertion point."),
                    C.PointList("positions", "Several insertion points (one reference each)."),
                    F.Angle("rotation", "Rotation.").Def(0),
                    F.Number("scale", "Uniform scale.").Positive().Def(1),
                    C.Layer("Layer for the reference."),
                    F.Map("attributes", "Attribute tag to value.", C.Str()),
                    F.Map("dynamicProperties", "Dynamic block property name to value (lengths in job units, angles in degrees, lists by value name)."),
                },
                new[] { K.ExactlyOne("position", "positions") },
                new[] { F.List("handles", "Handles of the references, in order of positions.", C.RefItem()) });

            yield return Op("block.attrs.set", "Set attribute values on block references.",
                new[]
                {
                    C.RefList("handles", "References to change."),
                    F.String("block", "Change every reference of this block."),
                    F.Map("where", "With block: only references whose attributes have these values.", C.Str()),
                    F.Map("values", "Attribute tag to new value.", C.Str()).Req(),
                    F.Bool("failOnMissingTag", "Fail when a reference lacks a tag. When false the tag is skipped and reported.").Def(true),
                },
                new[] { K.ExactlyOne("handles", "block"), K.Requires("where", "block") });

            yield return Op("block.dynprops.set", "Set dynamic block properties (visibility state, stretch distance, flip...) on references.",
                new[]
                {
                    C.RefList("handles", "References to change."),
                    F.String("block", "Change every reference of this dynamic block."),
                    F.Map("properties", "Property name to value (lengths in job units, angles in degrees).").Req(),
                },
                new[] { K.ExactlyOne("handles", "block") });

            yield return Op("layout.create", "Create a paper space layout from a template, a page setup or a paper size, with an optional title block.",
                new[]
                {
                    F.String("name", "Layout name, e.g. the sheet number.").Req(),
                    F.String("template", "A .dwt or .dwg to copy the layout from."),
                    F.String("templateLayout", "Layout name in the template. Defaults to its first layout."),
                    F.String("pageSetup", "Named page setup to apply (from the drawing or the template)."),
                    F.String("plotDevice", "Plotter configuration.").Def("DWG To PDF.pc3"),
                    F.String("paperSize", "Canonical media name, e.g. \"ISO_full_bleed_A1_(841.00_x_594.00_MM)\" or \"ARCH_D_(36.00_x_24.00_Inches)\"."),
                    F.Enum("orientation", "Paper orientation.", "landscape", "portrait").Def("landscape"),
                    F.String("plotStyleTable", "CTB or STB file name."),
                    F.String("titleBlock", "Block to insert at the paper origin."),
                    F.Map("titleBlockAttributes", "Title block attribute values.", C.Str()),
                    F.Bool("makeCurrent", "Switch to the layout once the job has committed (report extra currentLayout). A rehearsal never switches; a failed switch is a warning and the job stays committed.").Def(false),
                    C.Update(),
                },
                new[] { K.Requires("templateLayout", "template"), K.Requires("titleBlockAttributes", "titleBlock") },
                new[] { F.Ref("layout", "Layout handle."), F.Ref("titleBlock", "Title block reference handle.") },
                ensure: true);

            yield return Op("viewport.create", "Create a paper space viewport at a scale showing a model area, lock it and freeze layers in it.",
                new[]
                {
                    F.String("layout", "Layout name.").Req(),
                    F.Point2("paperCentre", "Viewport centre on the sheet, in paper units: mm, or inches for an imperial job.").Req(),
                    F.Length("width", "Viewport width on the sheet, in paper units: mm, or inches for an imperial job.").Positive(),
                    F.Length("height", "Viewport height on the sheet, in paper units: mm, or inches for an imperial job.").Positive(),
                    F.Point2("modelCentre", "Model point at the viewport centre."),
                    C.PointList("modelWindow", "Two opposite model corners to show; sets the size from the scale when width and height are omitted.").Count(2, 2),
                    C.Scale().Req(),
                    F.Bool("lock", "Lock the display.").Def(true),
                    C.StringList("frozenLayers", "Layers frozen in this viewport only."),
                    F.Angle("twist", "View twist.").Def(0),
                    C.Layer("Viewport layer (normally a non-plotting layer)."),
                },
                new[] { K.ExactlyOne("modelCentre", "modelWindow"), K.Requires("modelCentre", "width", "height") },
                new[] { F.Ref("viewport", "Viewport handle."), F.Number("scaleRatio", "Model length per printed length read back (100 for 1:100, whatever the units).") });

            yield return Op("viewports.layout", "Place several model views on a layout automatically, in aligned rows inside the drawable area.",
                Concat(new[]
                {
                    F.String("layout", "Layout name.").Req(),
                    C.Rect("area", "Drawable area of the sheet, in paper units: mm, or inches for an imperial job (inside the title block borders). When omitted, the layout's limits are used, which is only allowed on a layout without a title block (an attributed block reference)."),
                    F.List("views", "Views in reading order.", F.OfObject("",
                        F.String("id", "Name for this view in the result.").Req(),
                        C.PointList("modelWindow", "Two opposite model corners to show.").Req().Count(2, 2),
                        C.Scale().Req(),
                        C.StringList("frozenLayers", "Layers frozen in this viewport."),
                        F.String("title", "View title text placed under the viewport."))).Req().Count(1),
                    F.Bool("lock", "Lock the viewports.").Def(true),
                    C.Layer("Viewport layer."),
                }, C.Packing()),
                outputs: C.PackingOutputs());

            yield return Op("titleblock.fill", "Fill title block attributes on layouts.",
                new[]
                {
                    C.StringList("layouts", "Layouts to fill. Defaults to every layout."),
                    F.String("block", "Title block name. Defaults to the standard's title block."),
                    F.Map("values", "Attribute tag to value for every layout. Placeholders: {layout}, {date}, {drawing}.", C.Str()),
                    F.Map("perLayout", "Layout name to { tag: value } for values that differ per sheet.", F.OfMap(C.Str())),
                    F.Bool("requireAll", "Fail when a tag the standard marks required stays empty.").Def(true),
                },
                new[] { K.AtLeastOne("values", "perLayout") },
                new[]
                {
                    F.List("filled", "{ layout, block, tag, value } per attribute written (values already equal are listed too).", F.Of(FieldType.Object)),
                    F.List("missing", "{ layout, block, tags } per title block that lacks some of the requested tags; those values were not written.", F.Of(FieldType.Object)),
                });

            yield return Op("plot.pdf", "Plot layouts to PDF with a page setup and plot style table, naming files by pattern. A job holding plot.pdf may hold only plot.pdf ops; commit drawing changes in an earlier job.",
                new[]
                {
                    C.StringList("layouts", "Layouts in order. Defaults to every layout in tab order."),
                    F.String("pageSetup", "Page setup to plot with. Defaults to each layout's own settings."),
                    F.String("plotDevice", "PDF plotter configuration.").Def("DWG To PDF.pc3"),
                    F.String("paperSize", "Canonical media name, overriding the page setup."),
                    F.String("plotStyleTable", "CTB or STB file, overriding the page setup."),
                    C.OutputDir(),
                    C.FileName("\"{drawing}-{layout}.pdf\""),
                    F.Bool("combine", "One multi-sheet PDF instead of one file per layout.").Def(false),
                    C.Overwrite(),
                },
                outputs: new[] { F.List("files", "{ layout, path, pages } per file written.", F.Of(FieldType.Object)) });

            yield return Op("xref.attach", "Attach or overlay an external reference.",
                new[]
                {
                    F.String("path", "Path of the .dwg to reference.").Req(),
                    F.String("name", "Reference name. Defaults to the file name."),
                    F.Enum("mode", "Overlay references do not nest into drawings that reference this one.", "attach", "overlay").Def("overlay"),
                    F.Point2("position", "Insertion point.").Def(C.Origin()),
                    F.Angle("rotation", "Rotation.").Def(0),
                    F.Number("scale", "Uniform scale.").Positive().Def(1),
                    F.Enum("pathType", "How the path is stored.", "relative", "full", "none").Def("relative"),
                    C.Layer("Layer for the reference."),
                },
                outputs: new[] { F.Ref("reference", "Block reference handle."), F.String("storedPath", "Path as stored.") });

            yield return Op("xref.repath", "Repair external reference paths, making them relative, or point a reference at a new file.",
                new[]
                {
                    C.StringList("names", "References to repath. Defaults to every unresolved reference."),
                    F.String("newPath", "New file for a single named reference."),
                    C.StringList("searchFolders", "Folders to search for missing files by name."),
                    F.Enum("pathType", "How paths are stored afterwards.", "relative", "full").Def("relative"),
                    F.Bool("reload", "Reload the changed references after repathing, in commit only: a rehearsal never reloads. A reload is not undone if a later op of the same job fails, so put xref.repath last or alone.").Def(true),
                },
                new[] { K.Requires("newPath", "names") },
                new[] { F.List("references", "{ name, before, after, resolved } per reference.", F.Of(FieldType.Object)) });

            yield return Op("entity.setprops", "Set layer, colour, linetype or lineweight on entities by handle list or filter.",
                new[]
                {
                    C.RefList("handles", "Entities to change."),
                    F.Object("filter", "Select entities instead of listing them.",
                        F.String("layer", "On this layer (wildcards * and ? allowed)."),
                        F.String("type", "DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, DIMENSION."),
                        F.String("block", "References of this block (with type INSERT)."),
                        F.Enum("space", "Where to look.", "model", "paper", "all").Def("model")),
                    F.String("setLayer", "New layer."),
                    C.Color(),
                    F.String("linetype", "New linetype, or \"bylayer\"."),
                    C.Lineweight(),
                    F.Number("linetypeScale", "Entity linetype scale.").Positive(),
                },
                new[] { K.ExactlyOne("handles", "filter"), K.AtLeastOne("setLayer", "color", "linetype", "lineweight", "linetypeScale") },
                new[] { F.Integer("count", "Entities changed.") });

            yield return Op("layer.translate", "Move everything from old layers to new ones (merging), creating targets from the standard and removing emptied layers.",
                new[]
                {
                    F.Map("map", "Old layer name to new layer name. Old names may use * and ? wildcards.", C.Str()).Req(),
                    F.Bool("createTargets", "Create missing target layers (from the standard's layer list when listed there).").Def(true),
                    F.Bool("deleteEmptied", "Delete old layers left empty. Layer 0, Defpoints and the current layer are never deleted.").Def(true),
                    F.Bool("includeBlocks", "Also translate entities inside block definitions.").Def(true),
                },
                outputs: new[] { F.List("moves", "{ from, to, entities } per mapping applied.", F.Of(FieldType.Object)), C.StringList("deletedLayers", "Layers deleted.") },
                destructive: true);

            yield return Op("fix.bylayer", "Set colour, linetype, lineweight and transparency of entities to ByLayer.",
                new[]
                {
                    F.List("properties", "Properties to reset.", F.OfEnum("color", "linetype", "lineweight", "transparency")).Def(JsonArray.FromStrings(new[] { "color", "linetype", "lineweight", "transparency" })),
                    C.StringList("layers", "Only entities on these layers. Defaults to all."),
                    F.Bool("includeBlocks", "Also fix entities inside block definitions (they become ByBlock where the block is meant to take the insertion's properties).").Def(true),
                    F.Enum("space", "Where to fix.", "model", "paper", "all").Def("all"),
                },
                outputs: new[] { F.Map("counts", "Property name to number of entities changed.") });

            yield return Op("fix.flattenZ", "Set stray Z coordinates and elevations to zero on 2D entities.",
                new[]
                {
                    F.Length("tolerance", "Z values within this of zero are left alone.").NonNegative().Def(0),
                    F.Bool("includeBlocks", "Also flatten inside block definitions.").Def(true),
                    F.Bool("skip3dSolids", "Reporting only: 3D solids, surfaces and meshes are never flattened either way; false lists them as \"never flattened\" instead of skipped.").Def(true),
                    F.Enum("space", "Where to flatten.", "model", "paper", "all").Def("model"),
                },
                outputs: new[] { F.Integer("count", "Entities changed.") });

            yield return Op("fix.purge", "Purge unused named objects.",
                new[]
                {
                    F.List("kinds", "What to purge. \"all\" is every kind except regapps. Visual styles are never purged (built-in ones cannot be told from user ones).", F.OfEnum("blocks", "layers", "linetypes", "textstyles", "dimstyles", "mlinestyles", "tablestyles", "mleaderstyles", "materials", "regapps", "all")).Def(JsonArray.FromStrings(new[] { "all" })),
                    C.StringList("keep", "Names never purged, e.g. layers the standard requires."),
                    F.Bool("repeat", "Repeat until nothing more can be purged (nested items).").Def(true),
                },
                outputs: new[] { F.Map("purged", "Kind to the list of names purged.") },
                destructive: true);

            yield return Op("fix.units", "Set the drawing's insertion units, optionally scaling the contents from the old unit.",
                new[]
                {
                    F.Enum("insUnits", "New insertion units.", "mm", "cm", "m", "in", "ft").Req(),
                    F.Bool("scaleContents", "Scale model space by the factor from the old to the new unit (fixes a drawing drawn in the wrong unit).").Def(false),
                    F.Point2("basePoint", "Base point for scaling.").Def(C.Origin()),
                },
                outputs: new[] { F.String("before", "Units before."), F.Number("factor", "Scale factor applied (1 when not scaled)."), F.Bool("geometryScaled", "True when model space was scaled.") });

            yield return Op("script.lisp", "Escape hatch: run an AutoLISP expression. Refused unless the run request sets allowScript. The op must be alone in its job, and the job may not carry verify.checks (verify.capture is allowed). A rehearsal checks the code but does not evaluate it; a commit runs only after an identical rehearsal (same job content) at the same document fingerprint. Its changes join the job's undo step but are not rolled back if a later step of the job fails: such a job reports status failed with error.details.scriptChangesRemain true, never rolled_back; one U removes the step. Validation is a best-effort lint: code that visibly prompts, runs or queues commands is refused.",
                new[]
                {
                    F.String("code", "AutoLISP source. Must not prompt for input.").Req(),
                    F.String("purpose", "Plain description of what the code does, recorded in the report.").Req(),
                    F.Integer("timeoutSeconds", "Recorded only: evaluation runs on AutoCAD's main thread and cannot be interrupted.").Range(1, 600).Def(60),
                },
                outputs: new[] { F.Any("value", "The value the expression returned, converted to JSON where possible.") },
                script: true);
        }

        private static FieldSpec[] Concat(FieldSpec[] a, FieldSpec[] b)
        {
            var r = new FieldSpec[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        internal static FieldSpec[] ConcatFields(FieldSpec[] a, FieldSpec[] b) => Concat(a, b);
    }
}
