using System.Collections.Generic;
using SA.Core.Json;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>
    /// The wave-2 AutoCAD ops (drawing, editing, extraction, materials). Lengths are in job units and points are model
    /// coordinates unless a field says paper. Element ids in results are object handles.
    /// </summary>
    public static class AutoCadOpsWave2
    {
        private static OpSchema Op(string name, string summary, FieldSpec[] fields, K[]? constraints = null, FieldSpec[]? outputs = null,
            bool ensure = false, bool destructive = false) =>
            new OpSchema(name, AppKind.AutoCad, summary, fields, constraints, outputs, ensure, destructive, false);

        private const string IdPattern = "[A-Za-z0-9_-]{1,64}";

        private static readonly string[] Projections = { "planar", "box", "cylinder", "sphere" };

        private static FieldSpec Bulges(string description) => F.List("bulges", description, F.Of(FieldType.Number));

        /// <summary>The entity filter of entity.setprops, so every op selects entities the same way.</summary>
        private static FieldSpec Filter(string description) =>
            F.Object("filter", description,
                F.String("layer", "On this layer (wildcards * and ? allowed)."),
                F.String("type", "DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, HATCH."),
                F.String("block", "References of this block, by its effective name (with type INSERT or no type)."),
                F.Enum("space", "Where to look.", "model", "paper", "all").Def("model"));

        /// <summary>One closed hatch or area loop: points (with optional bulges) or a closed entity.</summary>
        private static FieldSpec Loop(bool withIslands)
        {
            var fields = new List<FieldSpec>
            {
                C.PointList("points", "Loop vertices in order; the loop is closed automatically.").Count(3),
                Bulges("Bulge per segment (segment i runs from point i to point i+1, the last back to the first); positive arcs to the right of the travel direction, 1 is a half circle. Omitted means straight."),
                F.Ref("handle", "A closed entity to use as the loop: closed polyline, circle, ellipse, closed spline."),
            };
            if (withIslands) fields.Add(F.List("islands", "Loops inside this boundary that stay unhatched.", Loop(false)));
            return F.OfObject("", fields.ToArray()).With(K.ExactlyOne("points", "handle"));
        }

        private static FieldSpec Rooms() =>
            F.Object("rooms", "Find the room each item lies in: closed polylines on these layers are room boundaries. The room's name and number come from a block reference inside it with NAME and NUMBER attributes (tagBlock when given), else from a multiline text inside it whose first two lines are name and number (as room.create writes them).",
                C.StringList("layers", "Layers of the room boundary polylines (wildcards allowed).").Req().Count(1),
                F.String("tagBlock", "Room tag block with NAME and NUMBER attributes."));

        private static FieldSpec AreaUnit() =>
            F.Enum("areaUnit", "Unit of reported areas. Defaults to m2 for metric jobs and ft2 for imperial jobs; \"job\" is the job's length unit squared.", "m2", "ft2", "job");

        public static IEnumerable<OpSchema> All()
        {
            yield return Op("draw.entities", "Draw lines, arcs, circles, polylines with bulges and points; properties stay ByLayer.",
                new[]
                {
                    F.List("entities", "Entities in drawing order.", F.OfObject("",
                        F.String("id", "Name of this entity in outputs.ids, e.g. \"edge-1\".").Matching(IdPattern),
                        F.Enum("kind", "Entity kind.", "line", "polyline", "arc", "circle", "point").Req(),
                        C.PointList("points", "line: 2 points; polyline: 2 or more; arc: 3 points (start, a point on the arc, end) instead of center, radius and angles; point: 1 point."),
                        Bulges("polyline: bulge per segment (segment i runs from point i to point i+1; a closed polyline has one more, back to the first). Positive arcs to the right of the travel direction, 1 is a half circle. Omitted means straight."),
                        F.Bool("closed", "polyline: closed.").Def(false),
                        F.Point2("center", "arc, circle: centre."),
                        F.Length("radius", "arc, circle: radius.").Positive(),
                        F.Angle("startAngle", "arc: start angle; the arc runs counter-clockwise to endAngle."),
                        F.Angle("endAngle", "arc: end angle."),
                        C.Layer("Layer for this entity. Defaults to the op's layer."))).Req().Count(1),
                    C.Layer("Layer for the entities. Defaults to the current layer."),
                },
                outputs: new[]
                {
                    F.List("handles", "Handles in the order of entities.", C.RefItem()),
                    F.Map("ids", "Entity id to handle, for entities that have an id.", C.RefItem()),
                });

            yield return Op("hatch.add", "Hatch closed boundaries (points or entity handles, with island loops) by pattern, scale and angle on a layer, optionally associative; returns handles and areas.",
                new[]
                {
                    F.List("boundaries", "Outer boundaries. Each gives points or a handle, and optional islands (loops inside it left unhatched).",
                        Loop(true)).Req().Count(1),
                    F.String("pattern", "Pattern name from the drawing's pattern file (acadiso.pat for metric drawings, acad.pat for imperial), \"SOLID\", or \"_USER\" for user-defined lines with spacing.").Req(),
                    F.Number("scale", "Pattern scale (predefined patterns).").Positive().Def(1),
                    F.Angle("angle", "Pattern angle.").Def(0),
                    F.Length("spacing", "Line spacing of a user-defined pattern (\"_USER\").").Positive(),
                    F.Bool("double", "User-defined pattern: add a second set of lines at 90 degrees.").Def(false),
                    F.Point2("origin", "Pattern origin. Defaults to the drawing origin."),
                    C.Layer("Hatch layer. Defaults to the current layer."),
                    F.Bool("associative", "Keep the hatch tied to its boundary entities. Needs handle loops, or drawBoundary for point loops.").Def(false),
                    F.Bool("drawBoundary", "Also draw a closed polyline for every point loop (on boundaryLayer), and use it as the loop.").Def(false),
                    F.String("boundaryLayer", "Layer for drawn boundaries. Defaults to the hatch layer."),
                    F.Bool("separate", "One hatch per boundary instead of one hatch holding every boundary.").Def(false),
                    F.Enum("drawOrder", "Draw order of new hatches: behind their boundary entities (as HATCH does by default), at the back of the space, or unchanged.", "behindBoundary", "back", "none").Def("behindBoundary"),
                },
                new[] { K.Requires("boundaryLayer", "drawBoundary") },
                new[]
                {
                    F.List("handles", "Hatch handles, one per hatch.", C.RefItem()),
                    F.List("areas", "Area of each hatch read back from AutoCAD, in job units squared.", F.Of(FieldType.Number)),
                    F.Number("area", "Total area, in job units squared."),
                    F.List("boundaryHandles", "Handles of drawn boundary polylines.", C.RefItem()),
                });

            yield return Op("entity.transform", "Move, copy, rotate, mirror or scale entities by handle or filter; copies get new handles.",
                new[]
                {
                    C.RefList("handles", "Entities to transform."),
                    Filter("Select entities instead of listing them. Entities on locked layers are skipped and counted."),
                    F.Object("move", "Move by the vector from one point to another.",
                        F.Point2("from", "Base point.").Req(),
                        F.Point2("to", "Where the base point goes.").Req()),
                    F.Object("rotate", "Rotate about a point.",
                        F.Point2("base", "Centre of rotation.").Req(),
                        F.Angle("angle", "Counter-clockwise angle.").Req()),
                    F.Object("scale", "Scale uniformly about a point.",
                        F.Point2("base", "Base point.").Req(),
                        F.Number("factor", "Scale factor.").Positive().Req()),
                    F.Object("mirror", "Mirror about a line.",
                        F.Point2("axisStart", "First point of the mirror line.").Req(),
                        F.Point2("axisEnd", "Second point of the mirror line.").Req(),
                        F.Enum("text", "Single-line text and attributes: kept readable (as MIRRTEXT 0 does) or mirrored.", "readable", "mirrored").Def("readable")),
                    F.Bool("copy", "Transform copies and leave the originals in place.").Def(false),
                    F.Integer("copies", "With copy: how many copies; copy n is the original transformed n times (a linear or polar array).").Range(1, 1000).Def(1),
                    F.Integer("expectCount", "When given, the op fails unless exactly this many entities are selected."),
                },
                new[] { K.ExactlyOne("handles", "filter"), K.ExactlyOne("move", "rotate", "scale", "mirror") },
                new[]
                {
                    F.List("handles", "Copies in order (copy 1 of every entity, then copy 2...), or the transformed entities when not copying.", C.RefItem()),
                    F.Integer("count", "Entities selected."),
                });

            yield return Op("entity.delete", "Erase the listed or filtered entities. Refused unless their number equals expectCount; entities on locked layers are refused.",
                new[]
                {
                    C.RefList("handles", "Entities to erase, from a snapshot or an earlier op."),
                    Filter("Select entities instead of listing them. Entities on locked layers fail the op."),
                    F.Integer("expectCount", "Must equal the number of entities selected; guards against a list or filter built by mistake.").Range(1, null).Req(),
                },
                new[] { K.ExactlyOne("handles", "filter") },
                new[] { F.List("deleted", "Handles erased.", C.RefItem()), F.Integer("count", "Entities erased.") },
                destructive: true);

            yield return Op("block.extract", "List block references with handle, effective name, attributes, dynamic properties, insertion point, rotation, scale, layer and containing room; to a CSV or JSON file and the result.",
                new[]
                {
                    C.StringList("blocks", "Block names (effective names; wildcards allowed). Defaults to every named block."),
                    F.Enum("space", "Where to look. References nested inside other blocks are counted, not listed.", "model", "paper", "all").Def("model"),
                    C.StringList("layers", "Only references on these layers (wildcards allowed)."),
                    C.StringList("attributes", "Attribute tags to list, in this column order. Defaults to every tag found."),
                    F.Bool("dynamicProperties", "List dynamic block properties (distances in job units, angles in degrees).").Def(true),
                    Rooms(),
                    F.String("fileName", "Plain file name written into the job's output folder; .csv or .json decides the format. Omitted means no file."),
                    C.Overwrite(),
                    F.Integer("maxInline", "Records returned in outputs.records (the file always has every record).").Range(0, 5000).Def(500),
                },
                outputs: new[]
                {
                    F.Integer("count", "References listed."),
                    F.List("records", "{ handle, block, layer, space, position, rotation, scale, attributes, dynamicProperties, room } per reference, up to maxInline.", F.Of(FieldType.Object)),
                    F.Bool("truncated", "True when records holds fewer than count."),
                    F.String("path", "Full path of the file. A rehearsal writes it to a temporary folder instead, never over the real file."),
                });

            yield return Op("area.report", "Areas of closed polylines, circles, hatches and regions by handle or filter, nested boundaries subtracted, grouped by layer or room; to a table, a CSV or JSON file, and the result.",
                new[]
                {
                    C.RefList("handles", "Closed entities to measure."),
                    F.Object("filter", "Select closed entities instead of listing them.",
                        C.StringList("layers", "On these layers (wildcards allowed)."),
                        F.List("types", "Entity kinds to include.", F.OfEnum("polyline", "circle", "hatch", "region", "ellipse", "spline")).Def(JsonArray.FromStrings(new[] { "polyline", "circle", "hatch", "region" })),
                        F.Enum("space", "Where to look.", "model", "paper", "all").Def("model")),
                    F.Enum("islands", "A selected boundary lying inside another selected boundary on the same layer becomes a hole in it (subtract), or counts on its own (ignore). Hatch islands are always subtracted.", "subtract", "ignore").Def("subtract"),
                    Rooms(),
                    F.Enum("groupBy", "Totals by layer, by room (needs rooms) or none.", "layer", "room", "none").Def("layer"),
                    AreaUnit(),
                    F.Integer("precision", "Decimal places in the table and file.").Range(0, 6).Def(2),
                    F.Object("table", "Write the totals as a table into the drawing.",
                        F.Point2("position", "Top-left corner.").Req(),
                        F.String("title", "Table title."),
                        F.String("style", "Table style."),
                        C.Layer("Table layer.")),
                    F.String("fileName", "Plain file name written into the job's output folder, one row per item; .csv or .json decides the format."),
                    C.Overwrite(),
                },
                new[] { K.ExactlyOne("handles", "filter") },
                new[]
                {
                    F.Number("total", "Sum of net areas, in areaUnit."),
                    F.String("areaUnit", "The unit used."),
                    F.List("items", "{ handle, type, layer, area, gross, holes, perimeter, room } per measured boundary that is not a hole.", F.Of(FieldType.Object)),
                    F.List("groups", "{ key, count, area } per layer or room.", F.Of(FieldType.Object)),
                    F.String("path", "Full path of the file. A rehearsal writes it to a temporary folder instead."),
                    F.Ref("table", "Table handle."),
                });

            yield return Op("grid.module", "Lay out a module grid (ceiling, floor or wall tiles) inside a closed boundary, set out by rule; reports border sizes and flags borders under half a module.",
                new[]
                {
                    C.PointList("boundary", "Boundary points in order (closed automatically).").Count(3),
                    F.Ref("boundaryHandle", "A closed polyline to use as the boundary (straight segments only)."),
                    F.Length("module", "Module size along the grid's X direction, e.g. 600.").Positive().Req(),
                    F.Length("moduleY", "Module size along the grid's Y direction. Defaults to module.").Positive(),
                    F.Angle("angle", "Direction of the grid's X axis.").Def(0),
                    F.Enum("setOut", "balanced picks, per direction, whichever of centreTile and centreLine gives the larger border; centreTile puts a whole tile in the middle of the boundary; centreLine puts a grid line there; origin runs the grid through the origin point.", "balanced", "centreTile", "centreLine", "origin").Def("balanced"),
                    F.Point2("origin", "A grid line intersection, with setOut origin."),
                    F.Number("minBorder", "Borders smaller than this fraction of the module are flagged.").Range(0, 1).Def(0.5),
                    C.Layer("Layer for the grid lines. Defaults to the current layer."),
                    F.Bool("draw", "Draw the grid lines, clipped to the boundary. When false the op only reports.").Def(true),
                },
                new[] { K.ExactlyOne("boundary", "boundaryHandle"), K.Requires("origin", "setOut") },
                new[]
                {
                    F.List("handles", "Grid line handles.", C.RefItem()),
                    F.Object("borders", "Cut tile size at each side in the grid's frame: { left, right, bottom, top }, in job units."),
                    F.List("smallBorders", "Sides whose border is under minBorder of the module.", C.Str()),
                    F.Point2("origin", "The grid line intersection used."),
                    F.Integer("fullTiles", "Whole tiles inside the boundary."),
                    F.Integer("cutTiles", "Cut tiles inside the boundary."),
                });

            yield return Op("layout.clearances", "Test clearance zones (circles, rectangles, convex polygons, door swings, route strips) against entities on chosen layers and references of chosen blocks in model space; reports each intrusion with its depth. Changes nothing unless drawZones is given.",
                new[]
                {
                    F.List("zones", "Clearance zones.", F.OfObject("",
                        F.String("id", "Zone name in the report.").Req().Matching(IdPattern),
                        F.Enum("kind", "circle: centre and radius. rect: min and max. polygon: convex points. doorSwing: hinge, leafWidth, closedAngle, swing and side (the leaf's sweep). route: points and width (a strip centred on the points).", "circle", "rect", "polygon", "doorSwing", "route").Req(),
                        F.Point2("centre", "circle: centre."),
                        F.Length("radius", "circle: radius, e.g. 750 for a 1500 turning circle.").Positive(),
                        F.Point2("min", "rect: lower-left corner."),
                        F.Point2("max", "rect: upper-right corner."),
                        C.PointList("points", "polygon: convex outline (3 or more points); route: centreline (2 or more points)."),
                        F.Length("width", "route: clear width.").Positive(),
                        F.Point2("hinge", "doorSwing: hinge point."),
                        F.Length("leafWidth", "doorSwing: leaf width (the swing radius).").Positive(),
                        F.Angle("closedAngle", "doorSwing: direction from the hinge along the closed leaf."),
                        F.Angle("swing", "doorSwing: opening angle.").Range(1, 180).Def(90),
                        F.Enum("side", "doorSwing: the leaf opens counter-clockwise (left) or clockwise (right) from closedAngle.", "left", "right").Def("left"),
                        C.RefList("ignore", "Entities never counted against this zone (the door that makes the swing, for example)."))).Req().Count(1),
                    C.StringList("layers", "Obstacles: entities on these layers (wildcards allowed)."),
                    C.StringList("blocks", "Obstacles: references of these blocks, by effective name (wildcards allowed)."),
                    F.Enum("blockGeometry", "Test block references by their geometry, or by their bounding box.", "geometry", "extents").Def("geometry"),
                    C.RefList("ignore", "Entities never counted against any zone."),
                    F.Length("tolerance", "Intrusions this deep or less are ignored (touching). Defaults to 0.5 mm.").NonNegative(),
                    F.Object("drawZones", "Also draw each zone's outline, for review.",
                        C.Layer("Layer for the outlines (normally a non-plotting review layer).").Req()),
                    F.Bool("failOnIntrusion", "Fail the op, and so roll back the job, when any zone is intruded.").Def(false),
                },
                new[] { K.AtLeastOne("layers", "blocks") },
                new[]
                {
                    F.Integer("count", "Intrusions found."),
                    F.Bool("clear", "True when no zone is intruded."),
                    F.List("intrusions", "{ zone, handle, type, layer, block, depth, point } per obstacle in a zone, deepest first.", F.Of(FieldType.Object)),
                    F.List("zones", "{ id, kind, intrusions, maxDepth, outline } per zone.", F.Of(FieldType.Object)),
                    F.List("outlines", "Handles of drawn zone outlines.", C.RefItem()),
                });

            yield return Op("text.replace", "Find and replace in text, multiline text, attributes, table cells and multileader text, with a dry-run count. Formatting codes in multiline text are never changed.",
                new[]
                {
                    F.String("find", "Text to find (a .NET regular expression when regex is true).").Req(),
                    F.String("replace", "Replacement; \"\" deletes the match. Required unless dryRun. With regex, $1 inserts group 1."),
                    F.Bool("matchCase", "Case-sensitive match.").Def(false),
                    F.Bool("wholeWord", "Match whole words only.").Def(false),
                    F.Bool("regex", "Treat find as a regular expression.").Def(false),
                    F.List("kinds", "What to search.", F.OfEnum("text", "mtext", "attributes", "tables", "mleaders")).Def(JsonArray.FromStrings(new[] { "text", "mtext", "attributes", "tables", "mleaders" })),
                    F.Enum("space", "Where to search.", "model", "paper", "all").Def("all"),
                    C.StringList("layers", "Only objects on these layers (wildcards allowed)."),
                    F.Bool("includeBlockDefinitions", "Also change text and attribute definitions inside block definitions (every reference shows the change; existing attribute values do not).").Def(false),
                    F.Bool("dryRun", "Count and list the changes without making them.").Def(false),
                    F.Integer("expectCount", "When given, the op fails unless exactly this many objects would change."),
                },
                outputs: new[]
                {
                    F.Integer("count", "Objects changed (or that would change in a dry run)."),
                    F.Integer("matches", "Occurrences replaced (or found)."),
                    F.List("changes", "{ handle, kind, before, after } per object, up to 500.", F.Of(FieldType.Object)),
                    F.Integer("skippedLocked", "Objects with matches on locked layers, left unchanged."),
                });

            yield return Op("revcloud.add", "Draw a revision cloud (a closed polyline of arcs bulging outward) around a rectangle, points or a closed polyline, with an optional revision tag.",
                new[]
                {
                    C.Rect("rect", "Rectangle to cloud."),
                    C.PointList("points", "Outline points in order (closed automatically).").Count(3),
                    F.Ref("handle", "A closed polyline whose vertices give the outline (bulges are ignored)."),
                    F.Length("offset", "Grow the outline outward by this much before clouding.").NonNegative().Def(0),
                    F.Length("arcLength", "Chord length of each arc. Defaults to 10 mm on paper at the current annotation scale.").Positive(),
                    C.Layer("Cloud layer (normally a revision or markup layer). Defaults to the current layer."),
                    F.Object("tag", "Revision tag outside the cloud: the block with its attribute set, or text in a triangle.",
                        F.String("text", "Revision mark, e.g. \"B\".").Req(),
                        F.Point2("position", "Tag centre. Defaults to just outside the outline's top-right corner."),
                        F.String("block", "Tag block to insert."),
                        F.String("attribute", "Attribute of the tag block that takes the text.").Def("REV"),
                        F.Length("plottedHeight", "Text height on paper, in paper units: mm, or inches for an imperial job. Defaults to 2.5 mm.").Positive(),
                        C.Layer("Tag layer. Defaults to the cloud layer.")),
                },
                new[] { K.ExactlyOne("rect", "points", "handle") },
                new[]
                {
                    F.Ref("cloud", "Cloud polyline handle."),
                    F.Integer("arcCount", "Arcs in the cloud."),
                    F.List("tagHandles", "Handles of the tag objects (block reference, or triangle and text).", C.RefItem()),
                });

            yield return Op("layerstate.ensure", "Create or update a named layer state from the drawing's current layer settings, with changes for listed layers, and optionally leave the drawing in that state.",
                new[]
                {
                    F.String("name", "Layer state name.").Req(),
                    F.String("description", "Layer state description."),
                    F.List("layers", "Settings that differ from the drawing's current ones; layers not listed are saved as they are now.", F.OfObject("",
                        F.String("name", "Layer name (wildcards * and ? allowed).").Req(),
                        F.Bool("on", "Layer on."),
                        F.Bool("frozen", "Layer frozen."),
                        F.Bool("locked", "Layer locked."),
                        F.Bool("plot", "Layer plots."),
                        C.Color(),
                        F.String("linetype", "Linetype name; must be loaded."),
                        C.Lineweight(),
                        F.Integer("transparency", "Percent.").Range(0, 90))),
                    F.List("properties", "What restoring the state sets.", F.OfEnum("on", "frozen", "locked", "plot", "color", "linetype", "lineweight", "transparency", "plotStyle"))
                        .Def(JsonArray.FromStrings(new[] { "on", "frozen", "locked", "plot", "color", "linetype", "lineweight", "transparency" })),
                    F.Bool("restore", "Leave the drawing's layers in this state afterwards. Only for a state this op creates or updates (a saved state is not restored as it is).").Def(false),
                    C.Update(),
                },
                outputs: new[]
                {
                    F.String("state", "Layer state name."),
                    F.Integer("layerCount", "Layers saved in the state."),
                    F.String("backup", "With update: the previous state exported as a .las file in the job's output folder."),
                },
                ensure: true);

            yield return Op("material.ensure", "Create render materials with description, diffuse colour and an optional image texture; existing materials are left alone unless update is true.",
                new[]
                {
                    F.List("materials", "Materials to ensure.", F.OfObject("",
                        F.String("name", "Material name, e.g. \"CT-01 Porcelain 600x600 Light Grey\" (finish code first). Global, ByLayer and ByBlock are refused.").Req(),
                        F.String("description", "Material description."),
                        C.Color(),
                        F.Object("texture", "Diffuse image map.",
                            F.String("path", "Image file; must exist on this machine at validation. Paths under a user profile are refused unless allowLocalPath.").Req(),
                            F.Length("width", "Real-world width of one image repeat.").Positive(),
                            F.Length("height", "Real-world height of one image repeat. Defaults to width.").Positive(),
                            F.Enum("projection", "Mapping projection.", Projections).Def("planar"),
                            F.Bool("allowLocalPath", "Allow a path under a user profile (it breaks on other machines).").Def(false)),
                        F.Bool("twoSided", "Render both sides of faces.").Def(true))).Req().Count(1),
                    C.Update(),
                },
                outputs: new[] { F.Map("materials", "Material name to its handle.", C.RefItem()) },
                ensure: true);

            yield return Op("material.assign", "Assign a render material to layers, or to entities by handle or filter, with an optional texture mapping on entities.",
                new[]
                {
                    F.String("material", "Material name, or \"ByLayer\" or \"ByBlock\" (entities only).").Req(),
                    C.StringList("layers", "Layers to assign it to (wildcards allowed). Xref-dependent layers are refused."),
                    C.RefList("handles", "Entities to assign it to."),
                    Filter("Select entities instead of listing them. Entities on locked layers are skipped and counted."),
                    F.Object("mapping", "Entities: texture mapping.",
                        F.Enum("projection", "Mapping projection.", Projections).Def("planar"),
                        F.Length("width", "Real-world width of one image repeat.").Positive().Req(),
                        F.Length("height", "Real-world height of one image repeat. Defaults to width.").Positive()),
                },
                new[] { K.ExactlyOne("layers", "handles", "filter") },
                new[]
                {
                    F.Integer("count", "Layers or entities changed."),
                    F.List("targets", "Handles of the layers or entities changed.", C.RefItem()),
                });
        }
    }
}
