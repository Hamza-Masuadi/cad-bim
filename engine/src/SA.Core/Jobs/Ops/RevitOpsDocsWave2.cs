using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SA.Core.Geometry;
using SA.Core.Json;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>The wave-2 Revit views, annotation, areas, data and schedule export ops.</summary>
    public static class RevitOpsDocsWave2
    {
        private const string Paper = "paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted";

        /// <summary>The 2D frame view-drawing ops take their points in.</summary>
        public const string Frame = "plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]";

        private static OpSchema Op(string name, string summary, FieldSpec[] fields, K[]? constraints = null, FieldSpec[]? outputs = null,
            bool ensure = false, bool destructive = false) =>
            new OpSchema(name, AppKind.Revit, summary, fields, constraints, outputs, ensure, destructive, false);

        private static FieldSpec View(string name, string description) => F.Ref(name, description + " A view id, a reference to an op that created it, or the view's exact name.");

        private static FieldSpec Loops(string name, string description) => F.List(name, description, F.OfList(C.P2()));

        private static FieldSpec[] ViewOutputs() => new[] { F.Ref("view", "Id of the new view."), F.String("name", "Name read back.") };

        public static IEnumerable<OpSchema> All()
        {
            yield return Op("data.export", "Write chosen parameters of the elements of one or more categories, with id, level, room, location and (rooms and areas) boundary loops, to a CSV or JSON file. Changes nothing in the model.",
                new[]
                {
                    C.StringList("categories", "Categories, by built-in name (OST_Doors) or display name (Doors).").Req().Count(1),
                    C.StringList("parameters", "Parameters, one column each: display name, built-in name (ROOM_AREA) or shared parameter GUID. The instance parameter is read first, then the type's."),
                    F.List("include", "Standard columns: id, uniqueId, category, family, type, level, room (number and name of the room the element is in), location (point or start and end, project coordinates), boundary (room and area loops).",
                        F.OfEnum("id", "uniqueId", "category", "family", "type", "level", "room", "location", "boundary"))
                        .Def(new JsonArray("id", "category", "family", "type", "level")),
                    F.String("level", "Only elements on this level."),
                    View("view", "Only elements visible in this view."),
                    F.Map("where", "Parameter name to required value: text compares with the value as Revit shows it, numbers in job units."),
                    F.String("phase", "Phase used for the room column. Defaults to the last phase."),
                    F.Enum("boundaryLocation", "Which boundary the boundary column follows.", "finish", "center", "core_boundary", "core_center").Def("finish"),
                    F.Enum("values", "number: lengths in job units, areas in m2 or ft2, volumes in m3 or ft3, angles in degrees, element ids as names; display: the text Revit shows.", "number", "display").Def("number"),
                    F.Enum("format", "File format. json writes { \"columns\": [...], \"rows\": [ {column: value} ] }.", "csv", "json").Def("csv"),
                    F.Enum("delimiter", "CSV field separator.", "comma", "semicolon", "tab").Def("comma"),
                    F.String("fileName", "File name. Placeholders: {drawing}, {date}, {jobId}. Defaults to \"{drawing} - data\"; .csv or .json is added when there is no extension."),
                    C.OutputDir(),
                    F.Integer("maxRows", "Refuse when more elements than this match, so a wrong filter cannot write a huge file.").Positive().Def(100000),
                    C.Overwrite(),
                },
                outputs: new[]
                {
                    F.String("file", "Path written."),
                    F.Integer("rows", "Elements written."),
                    C.StringList("columns", "Column names in order."),
                });

            yield return Op("schedule.export", "Export a schedule as delimited text with Revit's own schedule export. Changes nothing in the model.",
                new[]
                {
                    View("schedule", "Schedule to export.").Req(),
                    C.FileName("\"{view}.csv\"", "{view}, {drawing}, {date}, {jobId}"),
                    C.OutputDir(),
                    F.Enum("delimiter", "Field separator. Without an extension in fileName, comma and semicolon add .csv and tab adds .txt.", "comma", "semicolon", "tab").Def("comma"),
                    F.Enum("textQualifier", "Character put around text values.", "double_quote", "single_quote", "none").Def("double_quote"),
                    F.Enum("columnHeaders", "none, one_row (the field headings) or multiple_rows (with grouped headings).", "none", "one_row", "multiple_rows").Def("one_row"),
                    F.Bool("groupHeadersFootersBlanks", "Also export group headers, footers and blank lines.").Def(false),
                    F.Bool("title", "Also export the schedule title.").Def(false),
                    C.Overwrite(),
                },
                outputs: new[]
                {
                    F.String("file", "Path written."),
                    F.Integer("lines", "Lines in the file, headings included."),
                });

            yield return Op("schedule.key.create", "Ensure a key schedule: the category's elements get a key parameter, each key (row) sets the listed fields at once.",
                new[]
                {
                    F.String("name", "Key schedule name.").Req(),
                    F.String("category", "Category the keys are for, e.g. Rooms, Doors (built-in or display name).").Req(),
                    F.String("keyParameter", "Name of the key parameter the category's elements get, e.g. \"Room Style\". Defaults to Revit's default for the category."),
                    C.StringList("fields", "Parameters each key sets, in column order. The key name column comes first by itself.").Req().Count(1),
                    F.List("keys", "Keys (rows), in order.",
                        F.OfObject("A key.",
                            F.String("name", "Key name, unique in the schedule.").Req(),
                            F.Map("values", "Field name to value (text as shown, numbers in job units)."))),
                    C.Update(),
                },
                outputs: new[]
                {
                    F.Ref("schedule", "Key schedule id."),
                    F.Map("keys", "Key name to key element id."),
                },
                ensure: true);

            yield return Op("area.plan.create", "Create an area plan for an existing area scheme on a level.",
                new[]
                {
                    F.String("areaScheme", "Existing area scheme, e.g. \"Gross Building\" or \"Rentable\". The Revit API cannot create area schemes.").Req(),
                    F.String("level", "Level.").Req(),
                    F.String("name", "View name. Defaults to \"<level> - <area scheme>\"."),
                    F.String("template", "View template to assign."),
                    C.Scale(),
                    C.Rect("crop", "Crop the plan to this rectangle (project coordinates). Omitted leaves the crop off."),
                },
                outputs: ViewOutputs());

            yield return Op("area.place", "Draw area boundary lines and place areas (with tags) in an area plan.",
                new[]
                {
                    View("view", "Area plan.").Req(),
                    Loops("boundaries", "Closed boundary loops, each a list of points (closed automatically)."),
                    F.List("lines", "Single boundary lines.",
                        F.OfObject("A line.", F.Point2("start", "Start.").Req(), F.Point2("end", "End.").Req())),
                    F.List("areas", "Areas to place.",
                        F.OfObject("An area.",
                            F.Point2("point", "A point inside the region the boundaries enclose.").Req(),
                            F.String("name", "Area name."),
                            F.String("number", "Area number."),
                            F.Map("parameters", "Other parameter values."))),
                    F.Bool("allowNotEnclosed", "Keep an area that comes out not enclosed (area 0), with a warning. Otherwise the op fails.").Def(false),
                    F.Bool("tag", "Tag each new area in the view.").Def(true),
                    C.FamilyType("tagType", "Area tag type. Defaults to the project's default area tag."),
                },
                new[] { K.AtLeastOne("boundaries", "lines", "areas") },
                new[]
                {
                    C.RefList("lines", "Boundary line ids."),
                    C.RefList("areas", "Area ids, in the order given."),
                    F.List("values", "Areas read back (m2 or ft2), in the order given; 0 means not enclosed.", F.Of(FieldType.Number)),
                });

            yield return Op("view.drafting.create", "Create a drafting view, or a legend by duplicating an existing one.",
                new[]
                {
                    F.String("name", "View name.").Req(),
                    F.String("viewFamilyType", "Drafting view type. Defaults to the first one."),
                    F.String("template", "View template to assign."),
                    C.Scale(),
                    View("legendFrom", "Make a legend instead, by duplicating this legend view (the Revit API cannot create a legend from nothing)."),
                    F.Bool("copyContent", "With legendFrom: also copy its components and annotation.").Def(false),
                },
                new[] { K.AtMostOne("legendFrom", "viewFamilyType") },
                ViewOutputs());

            yield return Op("detail.draw", "Draw detail lines, arcs, circles, filled and masking regions and detail components in a 2D-drawable view. Points use the view's frame (" + Frame + ").",
                new[]
                {
                    View("view", "Plan, section, elevation, detail, drafting view or legend.").Req(),
                    F.String("lineStyle", "Line style (a Lines subcategory, e.g. \"Thin Lines\") for lines, arcs and circles that give none."),
                    F.List("lines", "Straight lines.",
                        F.OfObject("A line.", F.Point2("start", "Start.").Req(), F.Point2("end", "End.").Req(), F.String("lineStyle", "Line style."))),
                    F.List("polylines", "Chains of straight lines.",
                        F.OfObject("A chain.", C.PointList("points", "Vertices.").Req().Count(2), F.Bool("closed", "Join the last point to the first.").Def(false), F.String("lineStyle", "Line style."))),
                    F.List("arcs", "Arcs, counter-clockwise from startAngle to endAngle (degrees from the frame's x axis).",
                        F.OfObject("An arc.",
                            F.Point2("centre", "Centre.").Req(),
                            F.Length("radius", "Radius.").Positive().Req(),
                            F.Angle("startAngle", "Start angle.").Req(),
                            F.Angle("endAngle", "End angle.").Req(),
                            F.String("lineStyle", "Line style."))),
                    F.List("circles", "Full circles.",
                        F.OfObject("A circle.", F.Point2("centre", "Centre.").Req(), F.Length("radius", "Radius.").Positive().Req(), F.String("lineStyle", "Line style."))),
                    F.List("regions", "Filled regions.",
                        F.OfObject("A filled region.",
                            C.PointList("boundary", "Outer loop.").Req().Count(3),
                            Loops("holes", "Inner loops."),
                            F.String("type", "Filled region type. Defaults to the project's default."),
                            F.String("lineStyle", "Boundary line style."))),
                    F.List("masking", "Masking regions.",
                        F.OfObject("A masking region.",
                            C.PointList("boundary", "Outer loop.").Req().Count(3),
                            Loops("holes", "Inner loops."),
                            F.String("lineStyle", "Boundary line style."))),
                    F.List("components", "Detail components.",
                        F.OfObject("A detail component.",
                            F.String("type", "Detail item type, \"Family : Type\" or a unique type name.").Req(),
                            F.Point2("point", "Insertion point.").Req(),
                            F.Angle("rotation", "Rotation, counter-clockwise.").Def(0))),
                },
                new[] { K.AtLeastOne("lines", "polylines", "arcs", "circles", "regions", "masking", "components") },
                new[] { C.RefList("elements", "Ids of everything drawn, in the order lines, polylines (one per segment), arcs, circles, regions, masking, components.") });

            yield return Op("view.crop.set", "Set a view's crop region to a rectangle, a polygon or a room.",
                new[]
                {
                    View("view", "Plan, area plan, section, elevation, detail or callout view.").Req(),
                    C.Rect("rect", "Crop rectangle in the view's frame (" + Frame + ")."),
                    C.PointList("boundary", "Crop polygon in plan (project coordinates); plans only.").Count(3),
                    F.Ref("room", "Room: plans crop to its boundary, sections and elevations to its extent across the view and its height."),
                    F.Length("offset", "Grow the crop outward by this distance on every side.").NonNegative().Def(0),
                    F.Bool("visible", "Show the crop frame.").Def(false),
                    F.Bool("annotationCrop", "Turn the annotation crop on or off. Left as it is when omitted."),
                },
                new[] { K.ExactlyOne("rect", "boundary", "room") },
                new[]
                {
                    F.Ref("view", "View id."),
                    C.Rect("extent", "Crop extent read back, in the view's frame."),
                });

            yield return Op("view.callout.create", "Create a callout of a plan, section, elevation or detail view.",
                new[]
                {
                    View("parentView", "View the callout is drawn in: floor, ceiling or structural plan, section, elevation or detail view.").Req(),
                    C.Rect("rect", "Callout extent in the parent view's frame (" + Frame + ").").Req(),
                    F.String("name", "Callout view name.").Req(),
                    F.String("viewFamilyType", "View type: a Detail type (the default) or a type of the parent's own family."),
                    F.String("template", "View template to assign."),
                    C.Scale(),
                },
                outputs: ViewOutputs());

            yield return Op("colorfill.apply", "Ensure a colour fill scheme (by parameter, with chosen colours) and apply it to a view, with an optional legend.",
                new[]
                {
                    View("view", "Floor plan, area plan or section.").Req(),
                    F.String("category", "Category coloured: Rooms, Areas, Spaces (built-in or display name).").Def("Rooms"),
                    F.String("scheme", "Scheme name. Created by copying basedOn when missing.").Req(),
                    F.String("basedOn", "Scheme to copy when creating. Defaults to the category's first scheme (for areas, one of the view's area scheme)."),
                    F.String("parameter", "Parameter the scheme colours by, e.g. \"Department\". Needed when the scheme is created; changing it on an existing scheme clears its entries."),
                    F.String("title", "Legend title."),
                    F.Map("colors", "Parameter value as the legend shows it to colour: \"r,g,b\" or \"#RRGGBB\".", F.Of(FieldType.String)),
                    F.Point2("legendAt", "Place a colour fill legend with its top-left corner here (" + Frame + "). Omitted: no legend."),
                    C.Update(),
                },
                outputs: new[]
                {
                    F.Ref("scheme", "Scheme id."),
                    F.Ref("legend", "Legend id, or null."),
                },
                ensure: true);

            yield return Op("tag.place", "Place tags on chosen elements in a view: category tags, room and area tags, or material tags.",
                new[]
                {
                    View("view", "View to tag in.").Req(),
                    F.List("tags", "Tags to place.",
                        F.OfObject("A tag.",
                            F.Ref("element", "Element to tag.").Req(),
                            F.Point2("point", "Tag head position in the view's frame (" + Frame + ")."),
                            F.Point2("offset", "Instead of point: head offset from the element's location, in " + Paper + ", scaled by the view scale."),
                            F.String("tagType", "Tag type (\"Family : Type\"). Defaults to tagType, then the category's default tag."),
                            F.Bool("leader", "Draw a leader from the element to the head. Defaults to leader."),
                            F.Enum("orientation", "Tag orientation. Defaults to orientation.", "horizontal", "vertical"),
                            F.Bool("material", "Tag the material of the element's first face instead of the element.").Def(false))
                            .With(K.AtMostOne("point", "offset")))
                        .Req().Count(1),
                    C.FamilyType("tagType", "Default tag type."),
                    F.Point2("offset", "Default head offset for tags without point or offset (" + Paper + ").").Def(C.Origin()),
                    F.Bool("leader", "Default for leader.").Def(false),
                    F.Enum("orientation", "Default orientation.", "horizontal", "vertical").Def("horizontal"),
                    F.Bool("allowSecondTag", "Tag an element that already has a tag of the same category in the view. Otherwise that is an error.").Def(false),
                },
                outputs: new[] { C.RefList("tags", "Tag ids, in the order given.") });

            yield return Op("revision.cloud", "Draw a revision cloud for a revision in a view or on a sheet.",
                new[]
                {
                    View("view", "View or sheet (sheet:<number> works too). Not 3D views or schedules.").Req(),
                    F.Ref("revision", "Revision: id, reference, or its revision number or description (must match one revision).").Req(),
                    C.Rect("rect", "Cloud around this rectangle: the view's frame (" + Frame + "), or sheet coordinates in " + Paper + " on a sheet."),
                    C.PointList("boundary", "Cloud along this closed polygon, same coordinates as rect.").Count(3),
                    F.String("comments", "Comments parameter of the cloud."),
                    F.String("mark", "Mark parameter of the cloud."),
                },
                new[] { K.ExactlyOne("rect", "boundary") },
                new[] { F.Ref("cloud", "Revision cloud id.") });

            yield return Op("layout.clearances", "Check clearance zones (circles, rectangles, polygons, door swings, route strips, clear space at doors) against elements in a plan and report every intrusion with its depth. Changes nothing.",
                new[]
                {
                    View("view", "Plan view: elements visible in it are tested, by their plan footprint.").Req(),
                    F.List("zones", "Clearance zones, project coordinates.",
                        F.OfObject("A zone.",
                            F.String("id", "Label reported with each intrusion.").Req(),
                            F.Enum("kind", "circle: centre and radius. rect: min and max. polygon: convex points. doorSwing: hinge, leafWidth, closedAngle, swing and side (the leaf's sweep). route: points and width (a strip centred on the points). doorClearance: door, depth, width and faces (clear floor space in front of a door).",
                                "circle", "rect", "polygon", "doorSwing", "route", "doorClearance").Req(),
                            F.Point2("centre", "circle: centre."),
                            F.Length("radius", "circle: radius, e.g. 750 for a 1500 turning circle.").Positive(),
                            F.Point2("min", "rect: lower-left corner."),
                            F.Point2("max", "rect: upper-right corner."),
                            C.PointList("points", "polygon: convex outline (3 or more points); route: centreline (2 or more points).").Count(2),
                            F.Length("width", "route: clear width. doorClearance: width of the clear space (defaults to the door's width).").Positive(),
                            F.Point2("hinge", "doorSwing: hinge point."),
                            F.Length("leafWidth", "doorSwing: leaf width (the swing radius).").Positive(),
                            F.Angle("closedAngle", "doorSwing: direction from the hinge along the closed leaf."),
                            F.Angle("swing", "doorSwing: opening angle.").Range(1, 180).Def(90),
                            F.Enum("side", "doorSwing: the leaf opens counter-clockwise (left) or clockwise (right) from closedAngle.", "left", "right").Def("left"),
                            F.Ref("door", "doorClearance: the door."),
                            F.Length("depth", "doorClearance: depth of the clear space from the wall face.").Positive(),
                            F.Enum("faces", "doorClearance: the side the door faces, the other side, or both.", "facing", "back", "both").Def("both"),
                            C.RefList("ignore", "Elements never counted against this zone. A doorClearance's door and its host wall are always ignored for it.")))
                        .Req().Count(1),
                    C.StringList("against", "Categories tested, e.g. [\"Furniture\", \"Casework\", \"Walls\", \"Columns\"].").Req().Count(1),
                    C.RefList("ignore", "Elements never counted against any zone."),
                    F.Length("minHeight", "Ignore elements whose top is below this height above the view's level."),
                    F.Length("maxHeight", "Ignore elements whose bottom is above this height above the view's level."),
                    F.Length("tolerance", "Intrusions this deep or less are ignored (touching). Defaults to 0.5 mm.").NonNegative(),
                    F.Bool("failOnIntrusion", "Fail the op, and so roll back the job, when any zone is intruded.").Def(false),
                },
                outputs: new[]
                {
                    F.Integer("count", "Intrusions found."),
                    F.Bool("clear", "True when no zone is intruded."),
                    F.List("intrusions", "{ zone, element, category, name, depth, point } per element in a zone, deepest first; depth is how far the element must move to clear the zone, point a vertex of the overlap.", F.Of(FieldType.Object)),
                    F.List("zones", "{ id, kind, intrusions, maxDepth, outline } per zone; outline is the zone polygon tested (circles as their centre and radius).", F.Of(FieldType.Object)),
                });

            yield return Op("dim.string", "Add a string of aligned dimensions in a plan across the walls and grids (and, along a wall, the doors and windows) that a line crosses.",
                new[]
                {
                    View("view", "Plan view.").Req(),
                    F.Point2("start", "Start of the line the string follows.").Req(),
                    F.Point2("end", "End of that line.").Req(),
                    F.Length("offset", "Distance from the start-end line to the dimension line, perpendicular to the string; positive to the left of the string direction.").Req(),
                    F.Enum("orientation", "aligned measures along start-end; horizontal and vertical along the view's right and up directions.", "aligned", "horizontal", "vertical").Def("aligned"),
                    F.List("pick", "What is dimensioned: walls and grids the line crosses, doors and windows whose location lies on the line within their host wall's width.",
                        F.OfEnum("walls", "grids", "doors", "windows")).Def(new JsonArray("walls", "grids")),
                    F.Enum("wallFaces", "Wall faces used: both side faces, or only the exterior or interior one.", "both", "exterior", "interior").Def("both"),
                    F.Enum("openings", "Door and window references: their centre, or the two sides of their width.", "centre", "width").Def("centre"),
                    F.Bool("overall", "Also add an overall dimension from the first to the last reference.").Def(false),
                    F.Length("overallOffset", "Offset of the overall dimension. Defaults to offset plus 8 mm on paper (times the view scale) further out."),
                    F.String("dimStyle", "Linear dimension type. Defaults to the project's default."),
                },
                outputs: new[]
                {
                    F.List("measurements", "Segment values of the string read back, in order (job units).", C.Len()),
                    C.RefList("dimensions", "Dimension ids: the string, then the overall dimension."),
                });

            yield return Op("text.replace", "Find and replace text in text notes and chosen text parameters, keeping text note formatting. dryRun only counts.",
                new[]
                {
                    F.String("find", "Text to find, literally.").Req(),
                    F.String("replace", "Replacement; \"\" deletes the match. Required unless dryRun."),
                    F.Bool("matchCase", "Case-sensitive match.").Def(false),
                    F.Bool("wholeWord", "Match whole words only (letters, digits and _ form words).").Def(false),
                    F.Bool("textNotes", "Search text notes.").Def(true),
                    F.List("views", "Only text notes in these views or sheets. Defaults to every view.", F.Of(FieldType.Ref)),
                    C.StringList("parameters", "Text parameters to search too (display name, built-in name or GUID), on elements of categories."),
                    C.StringList("categories", "Categories whose elements' parameters are searched."),
                    F.Bool("includeTypes", "Also search those parameters on the elements' types.").Def(false),
                    F.Bool("dryRun", "Count and list the changes without making them.").Def(false),
                    F.Integer("expectCount", "When given, the op fails unless exactly this many elements would change.").NonNegative(),
                },
                new[] { K.Requires("parameters", "categories") },
                new[]
                {
                    F.Integer("count", "Elements changed (or that would change in a dry run)."),
                    F.Integer("matches", "Occurrences replaced (or found)."),
                    F.List("changes", "{ id, kind, field, before, after } per changed text, up to 500; kind is textNote or parameter.", F.Of(FieldType.Object)),
                    F.Integer("skippedGroups", "Elements with matches inside groups, left unchanged."),
                });
        }
    }

    /// <summary>Overlap depths between plan shapes for layout.clearances. Polygons are convex, vertices in order.</summary>
    public static class ClearanceGeometry
    {
        /// <summary>Convex hull, counter-clockwise, without collinear points (monotone chain).</summary>
        public static List<Point2> ConvexHull(IEnumerable<Point2> points)
        {
            var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (p.Count < 3) return p;
            var h = new Point2[2 * p.Count];
            int k = 0;
            for (int i = 0; i < p.Count; i++)
            {
                while (k >= 2 && (h[k - 1] - h[k - 2]).Cross(p[i] - h[k - 2]) <= 0) k--;
                h[k++] = p[i];
            }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && (h[k - 1] - h[k - 2]).Cross(p[i] - h[k - 2]) <= 0) k--;
                h[k++] = p[i];
            }
            return h.Take(k - 1).ToList();
        }

        /// <summary>True when the polygon is convex (either winding) and not degenerate.</summary>
        public static bool IsConvex(IReadOnlyList<Point2> poly, double tolerance)
        {
            if (poly.Count < 3) return false;
            int sign = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                var c = poly[(i + 2) % poly.Count];
                if (a.DistanceTo(b) <= tolerance) return false;
                double cr = (b - a).Cross(c - b);
                if (Math.Abs(cr) <= tolerance * Math.Max(1, (b - a).Length + (c - b).Length)) continue;
                int s = cr > 0 ? 1 : -1;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return sign != 0;
        }

        /// <summary>
        /// Minimum translation distance between two convex polygons (separating axis test): 0 when they are apart
        /// or only touch, otherwise how far one must move to clear the other.
        /// </summary>
        public static double PolygonDepth(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
        {
            double best = double.MaxValue;
            foreach (var poly in new[] { a, b })
                for (int i = 0; i < poly.Count; i++)
                {
                    var e = poly[(i + 1) % poly.Count] - poly[i];
                    if (e.Length == 0) continue;
                    var n = e.PerpLeft.Normalised();
                    Project(a, n, out double amin, out double amax);
                    Project(b, n, out double bmin, out double bmax);
                    double overlap = Math.Min(amax - bmin, bmax - amin);
                    if (overlap <= 0) return 0;
                    best = Math.Min(best, overlap);
                }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Minimum translation distance between a circle and a convex polygon; 0 when apart.</summary>
        public static double CircleDepth(Point2 centre, double radius, IReadOnlyList<Point2> poly)
        {
            double edge = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
                edge = Math.Min(edge, new Segment2(poly[i], poly[(i + 1) % poly.Count]).DistanceTo(centre));
            bool inside = new Polygon2(poly).Locate(centre, 0) == PointLocation.Inside;
            double depth = inside ? radius + edge : radius - edge;
            return depth > 0 ? depth : 0;
        }

        /// <summary>Axis-aligned rectangle as a counter-clockwise polygon.</summary>
        public static List<Point2> Rect(Point2 min, Point2 max) =>
            new List<Point2> { min, new Point2(max.X, min.Y), max, new Point2(min.X, max.Y) };

        /// <summary>
        /// A door swing: the convex sector from the hinge, leaf length radius, from startDeg through sweepDeg
        /// (counter-clockwise when positive), the arc split into steps of at most stepDeg.
        /// </summary>
        public static List<Point2> Sector(Point2 hinge, double radius, double startDeg, double sweepDeg, double stepDeg = 5)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDeg) / stepDeg));
            var pts = new List<Point2> { hinge };
            for (int i = 0; i <= n; i++)
            {
                double a = (startDeg + sweepDeg * i / n) * Math.PI / 180;
                pts.Add(hinge + new Point2(Math.Cos(a), Math.Sin(a)) * radius);
            }
            if (sweepDeg < 0) pts.Reverse();
            return pts;
        }

        /// <summary>
        /// A route strip as one rectangle per centreline segment, width wide. Each segment is extended by half the
        /// width at inner vertices so corners up to 90 degrees are covered.
        /// </summary>
        public static List<List<Point2>> Route(IReadOnlyList<Point2> centreline, double width)
        {
            var parts = new List<List<Point2>>();
            double h = width / 2;
            for (int i = 1; i < centreline.Count; i++)
            {
                var a = centreline[i - 1];
                var b = centreline[i];
                if ((b - a).Length == 0) continue;
                var d = (b - a).Normalised();
                if (i > 1) a = a - d * h;
                if (i < centreline.Count - 1) b = b + d * h;
                var n = d.PerpLeft * h;
                parts.Add(new List<Point2> { a - n, b - n, b + n, a + n });
            }
            return parts;
        }

        /// <summary>Rectangle from a wall segment: the location line offset by half the width each side.</summary>
        public static List<Point2> Strip(Point2 a, Point2 b, double width)
        {
            var n = (b - a).Normalised().PerpLeft * (width / 2);
            return new List<Point2> { a - n, b - n, b + n, a + n };
        }

        private static void Project(IReadOnlyList<Point2> poly, Point2 axis, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (var p in poly)
            {
                double d = p.Dot(axis);
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }
    }

    /// <summary>Literal find and replace for text.replace.</summary>
    public static class TextMatch
    {
        /// <summary>Start indexes of non-overlapping matches, left to right.</summary>
        public static List<int> FindAll(string text, string find, bool matchCase, bool wholeWord)
        {
            var hits = new List<int>();
            if (string.IsNullOrEmpty(find) || string.IsNullOrEmpty(text)) return hits;
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int i = 0;
            while (i <= text.Length - find.Length)
            {
                int at = text.IndexOf(find, i, cmp);
                if (at < 0) break;
                if (!wholeWord || (IsBoundary(text, at - 1) && IsBoundary(text, at + find.Length)))
                {
                    hits.Add(at);
                    i = at + find.Length;
                }
                else i = at + 1;
            }
            return hits;
        }

        public static string ReplaceAll(string text, string find, string replace, bool matchCase, bool wholeWord) =>
            Apply(text, FindAll(text, find, matchCase, wholeWord), find.Length, replace);

        public static string Apply(string text, IReadOnlyList<int> hits, int length, string replace)
        {
            var sb = new StringBuilder(text);
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                sb.Remove(hits[i], length);
                sb.Insert(hits[i], replace);
            }
            return sb.ToString();
        }

        private static bool IsBoundary(string text, int index) =>
            index < 0 || index >= text.Length || !(char.IsLetterOrDigit(text[index]) || text[index] == '_');
    }

    /// <summary>Delimited text (CSV and similar) writing and reading, RFC 4180 quoting.</summary>
    public static class Delimited
    {
        public static char Separator(string name) => name == "semicolon" ? ';' : name == "tab" ? '\t' : ',';

        public static string Field(string value, char separator, char quote = '"')
        {
            bool needs = value.IndexOf(separator) >= 0 || value.IndexOf(quote) >= 0 || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0
                || (value.Length > 0 && (value[0] == ' ' || value[value.Length - 1] == ' '));
            if (!needs) return value;
            string q = quote.ToString();
            return q + value.Replace(q, q + q) + q;
        }

        /// <summary>Lines joined by CRLF, each ending in CRLF.</summary>
        public static string Write(IEnumerable<IEnumerable<string>> rows, char separator)
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(string.Join(separator.ToString(), r.Select(v => Field(v ?? "", separator))));
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Parses records; quote null reads fields literally. Line breaks inside quotes stay in the field.</summary>
        public static List<List<string>> Parse(string text, char separator, char? quote = '"')
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false, any = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                any = true;
                if (inQuotes)
                {
                    if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) { field.Append(c); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }
                if (quote.HasValue && c == quote.Value && field.Length == 0) { inQuotes = true; continue; }
                if (c == separator) { row.Add(field.ToString()); field.Clear(); continue; }
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    any = false;
                    continue;
                }
                field.Append(c);
            }
            if (any)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Formats a number for a data file: invariant culture, up to 9 significant decimals.</summary>
        public static string Number(double v) => Math.Round(v, 9).ToString("0.#########", CultureInfo.InvariantCulture);
    }
}
