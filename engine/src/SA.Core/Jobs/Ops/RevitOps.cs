using System.Collections.Generic;
using SA.Core.Json;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>
    /// Wave-1 Revit op schemas. Lengths are in job units and converted through Revit's own unit functions by the engine.
    /// Points are project coordinates (internal origin) in plan. Element ids in results are 64-bit ids as strings.
    /// </summary>
    public static class RevitOps
    {
        /// <summary>What "paper units" means in Revit schemas; margin, gapX and gapY of viewports.layout follow it too.</summary>
        private const string Paper = "paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted";

        private static OpSchema Op(string name, string summary, FieldSpec[] fields, K[]? constraints = null, FieldSpec[]? outputs = null,
            bool ensure = false, bool destructive = false) =>
            new OpSchema(name, AppKind.Revit, summary, fields, constraints, outputs, ensure, destructive, false);

        private static readonly string[] LocationLines =
            { "wall_centerline", "core_centerline", "finish_face_exterior", "finish_face_interior", "core_face_exterior", "core_face_interior" };

        private static readonly string[] FilterOperators =
            { "equals", "not_equals", "greater", "greater_or_equal", "less", "less_or_equal", "contains", "not_contains", "begins_with", "ends_with", "has_value", "has_no_value" };

        private static FieldSpec View(string name, string description) => F.Ref(name, description + " A view id, a reference to an op that created it, or the view's exact name.");

        private static FieldSpec ElementFilter() =>
            F.Object("filter", "Select elements instead of listing ids.",
                F.String("category", "Category, by built-in name (OST_Doors) or display name (Doors).").Req(),
                F.String("level", "Only elements on this level."),
                F.String("type", "Only elements of this type (\"Family : Type\" or type name)."),
                F.Map("where", "Parameter name to required value."),
                View("view", "Only elements visible in this view."));

        private static FieldSpec Boundary(string description) => C.PointList("boundary", description).Count(3);

        private static FieldSpec[] ViewCommon() => new[]
        {
            F.String("name", "View name. When the standard sets a view naming rule, a name that does not follow it is warned about."),
            F.String("viewFamilyType", "View type to use. Defaults to the first of the right family."),
            F.String("template", "View template to assign."),
            C.Scale(),
        };

        private static FieldSpec[] Concat(params FieldSpec[][] parts)
        {
            var list = new List<FieldSpec>();
            foreach (var p in parts) list.AddRange(p);
            return list.ToArray();
        }

        private static FieldSpec[] ViewOutputs() => new[] { F.Ref("view", "Id of the new view."), F.String("name", "Name read back.") };

        public static IEnumerable<OpSchema> All()
        {
            yield return Op("level.ensure", "Create levels at elevations, with their plan views.",
                new[]
                {
                    F.List("levels", "Levels to ensure.", F.OfObject("",
                        F.String("name", "Level name.").Req(),
                        F.Length("elevation", "Elevation above the project base point.").Req(),
                        F.Bool("buildingStory", "Counts as a building storey.").Def(true),
                        F.Bool("createPlanView", "Create a floor plan for the level.").Def(true),
                        F.Bool("createCeilingView", "Create a reflected ceiling plan for the level.").Def(false),
                        F.String("levelType", "Level type."))).Req().Count(1),
                    C.Update(),
                },
                outputs: new[] { F.Map("levels", "Level name to element id.") },
                ensure: true);

            yield return Op("grid.create", "Create grids from explicit lines or an orthogonal definition.",
                new[]
                {
                    F.List("lines", "Explicit grid lines.", F.OfObject("",
                        F.String("label", "Grid name, e.g. \"A\" or \"1\".").Req(),
                        F.Point2("start", "Start point.").Req(),
                        F.Point2("end", "End point.").Req())),
                    F.Object("orthogonal", "A rectangular grid.",
                        F.Point2("origin", "Intersection of the first grid in each direction.").Req(),
                        F.List("xSpacings", "Bay widths left to right (vertical grids).", C.Len()).Req().Count(1),
                        F.List("ySpacings", "Bay depths bottom to top (horizontal grids).", C.Len()).Req().Count(1),
                        F.String("xLabelStart", "First vertical grid name.").Def("1"),
                        F.String("yLabelStart", "First horizontal grid name.").Def("A"),
                        F.List("skipLetters", "Letters never used in letter grid names (I and O read as 1 and 0). [] uses every letter.", F.Of(FieldType.String)).Def(JsonArray.FromStrings(new[] { "I", "O" })),
                        F.Length("extension", "Length past the outer grids. Defaults to 2000 mm (in job units).").NonNegative(),
                        F.Angle("rotation", "Rotation about origin.").Def(0)),
                    F.String("gridType", "Grid type."),
                },
                new[] { K.AtLeastOne("lines", "orthogonal") },
                new[] { F.Map("grids", "Grid name to element id.") });

            yield return Op("walltype.ensure", "Create a basic wall type with a compound layer build-up.",
                new[]
                {
                    F.String("name", "Wall type name, e.g. \"Interior - 100 Stud\".").Req(),
                    F.String("basedOn", "Basic wall type to duplicate. Defaults to the first basic wall type."),
                    F.Enum("function", "Wall function.", "exterior", "interior", "foundation", "retaining", "soffit", "core_shaft").Def("interior"),
                    F.List("layers", "Layers from exterior to interior.", F.OfObject("",
                        F.Enum("function", "Layer function.", "structure", "substrate", "thermal", "membrane", "finish1", "finish2").Req(),
                        F.String("material", "Material name; created plainly if missing.").Req(),
                        F.Length("thickness", "Layer thickness (0 only for membranes).").NonNegative().Req(),
                        F.Bool("core", "Inside the core boundaries. Core layers must be contiguous.").Def(false),
                        F.Bool("wraps", "Layer wraps at inserts and ends.").Def(false))).Req().Count(1),
                    F.Enum("wrapAtInserts", "Wrapping at inserts.", "none", "exterior", "interior", "both").Def("none"),
                    F.Enum("wrapAtEnds", "Wrapping at ends.", "none", "exterior", "interior").Def("none"),
                    C.Update(),
                },
                outputs: new[] { F.Ref("type", "Wall type id."), F.Length("width", "Total width read back.") },
                ensure: true);

            yield return Op("wall.create", "Create walls by type on a level, from a start and end or a path, with constraints, location line and joins.",
                new[]
                {
                    F.String("type", "Wall type name.").Req(),
                    F.String("level", "Base level.").Req(),
                    F.Point2("start", "Location line start."),
                    F.Point2("end", "Location line end."),
                    C.PointList("path", "Location line through these points, one wall per segment.").Count(2),
                    F.Bool("closed", "With path: join the last point back to the first.").Def(false),
                    F.Length("height", "Unconnected height.").Positive(),
                    F.String("topLevel", "Top constraint level, instead of a height."),
                    F.Length("topOffset", "Offset from the top level.").Def(0),
                    F.Length("baseOffset", "Offset from the base level.").Def(0),
                    F.Enum("locationLine", "Which line of the wall the points describe.", LocationLines).Def("wall_centerline"),
                    F.Bool("flip", "Flip the exterior side (exterior is on the left looking from start to end when not flipped).").Def(false),
                    F.Bool("structural", "Structural wall.").Def(false),
                    F.Enum("joins", "End joins.", "allow", "disallow").Def("allow"),
                    F.Bool("roomBounding", "Wall bounds rooms.").Def(true),
                    F.String("mark", "Mark; for a path, a number is appended per wall."),
                    F.Map("parameters", "Instance parameter name to value."),
                },
                new[] { K.ExactlyOne("start", "path"), K.Requires("start", "end"), K.ExactlyOne("height", "topLevel"), K.Requires("closed", "path") },
                new[] { C.RefList("walls", "Wall ids in order."), F.List("lengths", "Lengths read back, in order.", C.Len()) });

            foreach (var kind in new[] { "door", "window" })
            {
                bool door = kind == "door";
                yield return Op(kind + ".place", "Place a hosted " + kind + " family in a wall by offset along the wall or by point.",
                    new[]
                    {
                        F.Ref("host", "Host wall: element id, or a reference such as \"$w1\" or \"$w1.walls.2\".").Req(),
                        F.String("type", (door ? "Door type, e.g. \"Single-Flush : 0915 x 2134mm\"" : "Window type") + ": \"Family : Type\", or a type name that is unique among the category's types (an ambiguous name is refused, listing the candidates).").Req(),
                        F.Length("offset", "Distance along the wall's location line from its start to the " + kind + " centre."),
                        F.Point2("point", "A point on or near the wall; the " + kind + " is placed at its projection."),
                        F.String("level", "Level. Defaults to the host wall's base level."),
                        F.Length("sillHeight", door ? "Sill height (normally 0)." : "Sill height above the level. Defaults to the type's value.").NonNegative(),
                        F.Bool("flipHand", "Swap the hinge side.").Def(false),
                        F.Bool("flipFacing", "Swap the side it opens to or faces.").Def(false),
                        F.String("mark", "Mark."),
                        F.Map("parameters", "Instance parameter name to value."),
                    },
                    new[] { K.ExactlyOne("offset", "point") },
                    new[] { F.Ref("element", "Id of the new instance."), F.Ref("host", "Host id read back."), F.Length("offset", "Distance along the host's location line to the " + kind + " centre, read back; null when it does not project onto the wall.") });
            }

            yield return Op("floor.create", "Create a floor from a boundary or from a room's boundary.",
                new[]
                {
                    Boundary("Outer boundary points."),
                    F.Ref("fromRoom", "A room whose boundary (finish faces) is used."),
                    F.List("openings", "Inner loops to cut, each a list of points.", F.OfList(C.P2())),
                    F.String("type", "Floor type.").Req(),
                    F.String("level", "Level. Defaults to the room's level with fromRoom."),
                    F.Length("offset", "Height offset from the level.").Def(0),
                    F.Bool("structural", "Structural floor.").Def(false),
                },
                new[] { K.ExactlyOne("boundary", "fromRoom") },
                new[] { F.Ref("floor", "Floor id."), F.Number("area", "Area read back: m2 for metric job units, ft2 for imperial.") });

            yield return Op("ceiling.create", "Create a ceiling from a boundary or a room.",
                new[]
                {
                    Boundary("Boundary points."),
                    F.Ref("fromRoom", "A room whose boundary is used."),
                    F.String("type", "Ceiling type.").Req(),
                    F.String("level", "Level. Defaults to the room's level with fromRoom."),
                    F.Length("height", "Height above the level.").Req(),
                },
                new[] { K.ExactlyOne("boundary", "fromRoom") },
                new[] { F.Ref("ceiling", "Ceiling id."), F.Number("area", "Area read back.") });

            yield return Op("roof.footprint", "Create a footprint roof with slopes and overhang.",
                new[]
                {
                    Boundary("Footprint points (usually the exterior wall faces).").Req(),
                    F.String("type", "Roof type.").Req(),
                    F.String("level", "Level the roof sits on.").Req(),
                    F.Length("baseOffset", "Plate height: where the sloping planes meet the footprint, above the level. With an overhang the eaves sit lower than this by overhang x slope.").Def(0),
                    F.Length("overhang", "Overhang beyond the footprint.").NonNegative().Def(0),
                    F.Angle("slope", "Slope in degrees for every sloping edge.").Range(0, 89).Def(30),
                    F.List("edges", "Per-edge settings; edge i runs from boundary point i to i+1.", F.OfObject("",
                        F.Integer("edge", "Edge index.").NonNegative().Req(),
                        F.Bool("definesSlope", "Edge slopes (false for gable ends).").Req(),
                        F.Angle("slope", "Slope for this edge.").Range(0, 89))),
                },
                outputs: new[] { F.Ref("roof", "Roof id.") });

            yield return Op("room.place", "Place rooms at points or in every enclosed region of a level, with name, number and parameters.",
                new[]
                {
                    F.String("level", "Level.").Req(),
                    F.Point2("point", "A point inside the room."),
                    F.Bool("allEnclosed", "Place a room in every enclosed region without one."),
                    F.String("phase", "Phase the rooms belong to, by name. Defaults to the project's last phase. The tag view must show this phase."),
                    F.Bool("allowNotEnclosed", "With point: keep the room even when no boundary encloses it (area 0), with a warning. Only in the last phase. Otherwise a room that is not enclosed fails the op.").Def(false),
                    F.String("name", "Room name."),
                    F.String("number", "Room number."),
                    F.Map("parameters", "Parameter name to value (e.g. finish parameters)."),
                    F.String("upperLimit", "Upper limit level."),
                    F.Length("limitOffset", "Offset from the upper limit."),
                    F.Bool("tag", "Tag the room in a plan view of the level in the room's phase.").Def(true),
                    C.FamilyType("tagType", "Room tag type."),
                    View("view", "Plan view for the tag, showing the room's phase. Defaults to a floor plan of the level in that phase."),
                },
                new[] { K.ExactlyOne("point", "allEnclosed") },
                new[] { C.RefList("rooms", "Room ids."), F.List("areas", "Areas read back; 0 means not enclosed.", F.Of(FieldType.Number)) });

            yield return Op("room.separator", "Draw room separation lines.",
                new[]
                {
                    F.String("level", "Level.").Req(),
                    View("view", "Plan view to draw in. Defaults to the level's floor plan."),
                    F.List("lines", "Lines.", F.OfObject("",
                        F.Point2("start", "Start.").Req(),
                        F.Point2("end", "End.").Req())).Req().Count(1),
                },
                outputs: new[] { C.RefList("lines", "Separator ids.") });

            yield return Op("family.load", "Load a family file into the project.",
                new[]
                {
                    F.String("path", "Path of the .rfa file.").Req(),
                    C.StringList("types", "Load only these types. Defaults to all."),
                    F.Bool("overwriteParameters", "When the family is already loaded, reload it and overwrite its parameter values with the file's.").Def(false),
                    F.Bool("reload", "When the family is already loaded and the file differs, reload its definition (geometry, nested families) keeping parameter values. Without reload or overwriteParameters a differing file is refused rather than reloaded, even to add a missing type.").Def(false),
                },
                outputs: new[] { F.Ref("family", "Family id."), C.StringList("types", "Type names loaded.") },
                ensure: true);

            yield return Op("type.duplicate", "Duplicate a type under a new name and set type parameters.",
                new[]
                {
                    F.String("source", "Type to copy: \"Family : Type\" or a system type name.").Req(),
                    F.String("category", "Category to disambiguate the source name."),
                    F.String("newName", "Name of the new type.").Req(),
                    F.Map("parameters", "Type parameter name to value."),
                    C.Update(),
                },
                outputs: new[] { F.Ref("type", "New type id.") },
                ensure: true);

            yield return Op("param.set", "Set instance or type parameter values on listed elements or a filtered set.",
                new[]
                {
                    C.RefList("elements", "Element ids or references."),
                    ElementFilter(),
                    F.Enum("target", "Set on the elements or on their types.", "instance", "type").Def("instance"),
                    F.Map("values", "Parameter name to value. Numbers are in job units for lengths and degrees for angles; " +
                        "use { \"value\": 12, \"unit\": \"area\" } to state the unit kind (length, area, volume, angle, none).").Req(),
                    F.Bool("skipReadOnly", "Skip read-only parameters and report them instead of failing.").Def(false),
                },
                new[] { K.ExactlyOne("elements", "filter") },
                new[] { F.Integer("count", "Elements changed.") });

            yield return Op("param.shared.ensure", "Ensure shared parameters exist in the shared parameter file and are bound to categories.",
                new[]
                {
                    F.String("file", "Shared parameter file. Defaults to the one Revit has set."),
                    F.String("group", "Group in the shared parameter file.").Req(),
                    F.List("parameters", "Parameters.", F.OfObject("",
                        F.String("name", "Parameter name.").Req(),
                        F.Enum("dataType", "Data type.", "text", "integer", "number", "length", "area", "volume", "angle", "yesno", "url", "material", "multiline_text").Req(),
                        F.String("guid", "GUID to use; keeps the parameter identical across projects."),
                        F.String("description", "Tooltip text."))).Req().Count(1),
                    C.StringList("categories", "Categories to bind to.").Req().Count(1),
                    F.Enum("binding", "Instance or type binding.", "instance", "type").Def("instance"),
                    F.String("propertyGroup", "Properties palette group, e.g. \"Identity Data\" or \"Data\", or a GroupTypeId string. Labels follow Revit's UI language.").Def("Data"),
                    C.Update(),
                },
                outputs: new[] { F.Map("guids", "Parameter name to GUID.") },
                ensure: true);

            yield return Op("view.plan.create", "Create a floor plan view.",
                Concat(new[] { F.String("level", "Level.").Req(), C.Rect("crop", "Crop region in plan; omitted leaves the crop off.") }, ViewCommon()),
                outputs: ViewOutputs());

            yield return Op("view.ceiling.create", "Create a reflected ceiling plan view.",
                Concat(new[] { F.String("level", "Level.").Req(), C.Rect("crop", "Crop region in plan.") }, ViewCommon()),
                outputs: ViewOutputs());

            yield return Op("view.section.create", "Create a section from a cut line in plan.",
                Concat(new[]
                {
                    F.Point2("start", "Cut line start in plan.").Req(),
                    F.Point2("end", "Cut line end; the view looks to the left of start to end.").Req(),
                    F.Length("depth", "Far clip depth.").Positive().Req(),
                    F.Length("bottom", "Bottom of the view, as an elevation.").Req(),
                    F.Length("top", "Top of the view, as an elevation.").Req(),
                    F.Bool("flip", "Look the other way.").Def(false),
                }, ViewCommon()),
                outputs: ViewOutputs());

            yield return Op("view.elevation.create", "Create an elevation from a marker in a plan view.",
                Concat(new[]
                {
                    F.Point2("point", "Marker location.").Req(),
                    F.Enum("direction", "Direction the viewer looks, in project coordinates (north = +Y), not the face shown: \"north\" looks north and shows the south face.", "north", "south", "east", "west").Req(),
                    View("planView", "Plan view hosting the marker.").Req(),
                }, ViewCommon()),
                outputs: ViewOutputs());

            yield return Op("view.3d.create", "Create an isometric or perspective 3D view, optionally with a section box.",
                new[]
                {
                    F.String("name", "View name."),
                    F.Bool("perspective", "Perspective camera instead of isometric.").Def(false),
                    F.Point3("eye", "Camera position."),
                    F.Point3("target", "Point looked at."),
                    F.Object("sectionBox", "Section box.",
                        F.Point3("min", "Minimum corner.").Req(),
                        F.Point3("max", "Maximum corner.").Req()),
                    F.String("template", "View template."),
                },
                new[] { K.Requires("eye", "target"), K.Requires("target", "eye") },
                ViewOutputs());

            yield return Op("view.rename", "Rename views.",
                new[]
                {
                    F.List("views", "Views and their new names.", F.OfObject("",
                        View("view", "The view.").Req(),
                        F.String("name", "New name.").Req())).Req().Count(1),
                });

            yield return Op("viewtemplate.apply", "Assign a view template to views.",
                new[]
                {
                    F.List("views", "Views.", F.Of(FieldType.Ref)).Req().Count(1),
                    F.String("template", "View template name.").Req(),
                });

            yield return Op("view.filter.ensure", "Create a rule-based view filter with graphic overrides and apply it to views or templates.",
                new[]
                {
                    F.String("name", "Filter name.").Req(),
                    C.StringList("categories", "Categories the filter applies to.").Req().Count(1),
                    F.List("rules", "Rules on parameters.", F.OfObject("",
                        F.String("parameter", "Parameter name.").Req(),
                        F.Enum("operator", "Comparison.", FilterOperators).Req(),
                        F.Any("value", "Value to compare (not needed for has_value and has_no_value)."))).Req().Count(1),
                    F.Enum("logic", "How rules combine.", "and", "or").Def("and"),
                    F.List("applyTo", "Views or templates to add the filter to.", F.Of(FieldType.Ref)),
                    F.Object("overrides", "Graphic overrides where the filter is applied.",
                        F.Bool("visible", "Visibility.").Def(true),
                        F.Bool("halftone", "Halftone.").Def(false),
                        F.String("lineColor", "Projection line colour \"r,g,b\"."),
                        F.Integer("lineWeight", "Projection line weight 1-16.").Range(1, 16),
                        F.String("surfaceColor", "Surface foreground pattern colour \"r,g,b\"."),
                        F.Integer("transparency", "Surface transparency percent.").Range(0, 100)),
                    C.Update(),
                },
                outputs: new[] { F.Ref("filter", "Filter id.") },
                ensure: true);

            yield return Op("sheet.create", "Create a sheet with number, name and title block.",
                new[]
                {
                    F.String("number", "Sheet number. When the standard sets a sheet numbering rule, a new number that does not follow it is warned about; a number that differs from an existing one only by case is refused.").Req(),
                    F.String("name", "Sheet name.").Req(),
                    C.FamilyType("titleBlock", "Title block type. Without it a new sheet gets the standard's title block (titleBlock.familyType) when the standard names one, and none otherwise."),
                    F.Map("parameters", "Sheet parameter name to value (drawn by, checked by...)."),
                    C.Update(),
                },
                outputs: new[] { F.Ref("sheet", "Sheet id."), F.Ref("titleBlock", "Title block instance id.") },
                ensure: true);

            yield return Op("viewport.place", "Place a view on a sheet at a point.",
                new[]
                {
                    F.Ref("sheet", "Sheet id, reference or sheet number.").Req(),
                    View("view", "The view.").Req(),
                    F.Point2("centre", "Viewport centre in sheet coordinates (" + Paper + ").").Req(),
                    F.String("viewportType", "Viewport type (title style)."),
                    F.String("detailNumber", "Detail number."),
                },
                outputs: new[] { F.Ref("viewport", "Viewport id.") });

            yield return Op("viewports.layout", "Place several views on a sheet automatically, in aligned rows inside the drawable area.",
                Concat(new[]
                {
                    F.Ref("sheet", "Sheet id, reference or sheet number.").Req(),
                    F.List("views", "Views in reading order: view ids, references to ops that created them, or exact view names.", F.Of(FieldType.Ref)).Req().Count(1),
                    C.Rect("area", "Drawable area in sheet coordinates (" + Paper + "; margin, gapX and gapY too). Defaults to the title block's extents less margin."),
                    F.String("viewportType", "Viewport type."),
                }, C.Packing()),
                outputs: C.PackingOutputs());

            yield return Op("schedule.create", "Create a schedule with fields, filters, sorting and grouping, and totals.",
                new[]
                {
                    F.String("name", "Schedule name.").Req(),
                    F.String("category", "Category scheduled, or \"Multi-Category\" for a multi-category schedule.").Req(),
                    F.List("fields", "Columns in order.", F.OfObject("",
                        F.String("parameter", "Parameter name, or Count.").Req(),
                        F.String("heading", "Column heading. Defaults to Revit's heading for the field."),
                        F.Bool("hidden", "Hidden column (still usable to sort and filter).").Def(false),
                        F.Length("width", "Column width on the sheet (" + Paper + ").").Positive(),
                        F.Bool("total", "Calculate totals for this column.").Def(false))).Req().Count(1),
                    F.List("filters", "Filters (up to the limit Revit allows).", F.OfObject("",
                        F.String("field", "Parameter name of a field.").Req(),
                        F.Enum("operator", "Comparison.", FilterOperators).Req(),
                        F.Any("value", "Value."))).Count(null, 8),
                    F.List("sort", "Sorting and grouping, in priority order.", F.OfObject("",
                        F.String("field", "Parameter name of a field.").Req(),
                        F.Enum("order", "Order.", "ascending", "descending").Def("ascending"),
                        F.Bool("header", "Group header.").Def(false),
                        F.Bool("footer", "Group footer with count and totals.").Def(false),
                        F.Bool("blankLine", "Blank line between groups.").Def(false))),
                    F.Bool("itemize", "List every instance; false groups identical rows.").Def(true),
                    F.Bool("grandTotals", "Show grand totals.").Def(false),
                    F.Bool("includeLinks", "Include elements of linked models (refused for a schedule kind that cannot include them).").Def(false),
                    F.String("phase", "Phase."),
                    F.String("template", "Schedule view template."),
                    C.Update(),
                },
                outputs: new[] { F.Ref("schedule", "Schedule view id."), F.Integer("rowCount", "Body rows read back.") },
                ensure: true);

            yield return Op("tag.all", "Tag every untagged element of a category in a view. Elements already tagged in the view are always left alone.",
                new[]
                {
                    F.String("category", "Category to tag.").Req(),
                    View("view", "View to tag in.").Req(),
                    C.FamilyType("tagType", "Tag type. Defaults to the category's default tag."),
                    F.Bool("leader", "Add a leader.").Def(false),
                    F.Point2("offset", "Tag offset from the element's location (" + Paper + "), scaled by the view scale.").Def(C.Origin()),
                    F.Enum("orientation", "Tag orientation.", "horizontal", "vertical").Def("horizontal"),
                },
                outputs: new[] { F.Integer("tagged", "Tags created."), C.RefList("untaggable", "Elements that could not be tagged.") });

            yield return Op("dim.grids", "Dimension grids in a view: grid to grid strings and an overall dimension.",
                new[]
                {
                    View("view", "Plan view.").Req(),
                    F.Enum("direction", "Which grids.", "both", "horizontal", "vertical").Def("both"),
                    F.Length("offset", "Distance of the string from the grid ends (" + Paper + "), scaled by the view scale. Defaults to 12 mm.").Positive(),
                    F.String("dimensionType", "Linear dimension type."),
                    F.Bool("overall", "Add an overall dimension.").Def(true),
                    F.Enum("side", "Where the strings go: top-right puts them at the top of vertical grids and right of horizontal ones (in the view's up and right directions), bottom-left at the other ends, both at both.", "top-right", "bottom-left", "both").Def("top-right"),
                    F.Length("stringSpacing", "Gap between the overall string and the grid string (" + Paper + "), scaled by the view scale. Defaults to 8 mm.").Positive(),
                },
                outputs: new[] { C.RefList("dimensions", "Dimension ids.") });

            yield return Op("text.note", "Add a text note in a view.",
                new[]
                {
                    View("view", "View.").Req(),
                    F.String("text", "Note text.").Req(),
                    F.Point2("position", "Plans: project coordinates [x, y] at the level. Sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]. Sheets: sheet coordinates (" + Paper + "). Drafting views and legends: the view's own coordinates. Left alignment places the top-left corner of the text there, right the top-right, center the top-middle.").Req(),
                    F.String("type", "Text note type."),
                    F.Length("width", "Wrap width (" + Paper + ").").Positive(),
                    F.Angle("rotation", "Rotation.").Def(0),
                    F.Enum("align", "Horizontal alignment.", "left", "center", "right").Def("left"),
                },
                outputs: new[] { F.Ref("note", "Text note id.") });

            yield return Op("revision.ensure", "Create revisions and add them to sheets.",
                new[]
                {
                    F.List("revisions", "Revisions in order.", F.OfObject("",
                        F.String("description", "Description.").Req(),
                        F.String("date", "Revision date as text."),
                        F.String("issuedTo", "Issued to."),
                        F.String("issuedBy", "Issued by."),
                        F.String("code", "Expected revision number; checked against the standard's revision formats and read back. Revit assigns numbers from the numbering sequence; it cannot be set."),
                        F.Bool("issued", "Mark as issued (locks it).").Def(false))).Req().Count(1),
                    C.StringList("addToSheets", "Sheet numbers that get these revisions."),
                    C.Update(),
                },
                outputs: new[] { C.RefList("revisions", "Revision ids.") },
                ensure: true);

            yield return Op("export.pdf", "Export sheets or views to PDF.",
                new[]
                {
                    C.StringList("sheets", "Sheet numbers in order."),
                    F.List("views", "Views.", F.Of(FieldType.Ref)),
                    F.Bool("combine", "One PDF for all.").Def(false),
                    C.FileName("\"{sheetNumber} - {sheetName}.pdf\"", C.RevitFilePlaceholders),
                    C.OutputDir(),
                    F.Enum("colour", "Colour mode.", "colour", "grayscale", "black_line").Def("colour"),
                    F.Enum("rasterQuality", "Raster quality.", "low", "medium", "high", "presentation").Def("high"),
                    F.Bool("hideCropBoundaries", "Hide crop boundaries.").Def(true),
                    F.Bool("hideScopeBoxes", "Hide scope boxes.").Def(true),
                    F.Bool("hideReferencePlanes", "Hide reference planes.").Def(true),
                    C.Overwrite(),
                },
                new[] { K.AtLeastOne("sheets", "views") },
                new[] { F.List("files", "{ path, pages, items } per file; pages is null when the page count cannot be read from the file.", F.Of(FieldType.Object)) });

            yield return Op("export.dwg", "Export sheets or views to DWG with an export setup.",
                new[]
                {
                    C.StringList("sheets", "Sheet numbers."),
                    F.List("views", "Views.", F.Of(FieldType.Ref)),
                    F.String("setup", "DWG export setup name. Defaults to the active one."),
                    C.FileName("\"{sheetNumber}.dwg\"", C.RevitFilePlaceholders),
                    C.OutputDir(),
                    F.Bool("mergedViews", "Export each sheet as one file without view xrefs.").Def(true),
                    C.Overwrite(),
                },
                new[] { K.AtLeastOne("sheets", "views") },
                new[] { C.StringList("files", "Paths written."), C.StringList("companions", "Other files the export wrote next to the DWGs (view xrefs when mergedViews is false, images).") });

            yield return Op("export.ifc", "Export the model to IFC.",
                new[]
                {
                    F.String("fileName", "File name, e.g. \"{jobId}.ifc\".").Req(),
                    C.OutputDir(),
                    F.Enum("version", "IFC schema. ifc4x3 is the Reference View on 2027 and the only IFC4x3 option of 2024-2026, so the model view can differ by release; readback reports the file's.", "ifc2x3", "ifc4", "ifc4x3").Def("ifc4"),
                    View("view", "Export only elements visible in this 3D view."),
                    F.String("setup", "Not supported: named IFC export setups belong to the IFC exporter add-in and the API cannot load them by name. Giving one is refused."),
                    F.Bool("baseQuantities", "Export base quantities.").Def(false),
                    F.Enum("spaceBoundaries", "Space boundary level.", "none", "first", "second").Def("none"),
                    C.Overwrite(),
                },
                outputs: new[] { F.String("file", "Path written.") });

            yield return Op("export.image", "Export views or sheets to images.",
                new[]
                {
                    F.List("views", "Views or sheets: view ids, references, exact view names, or sheet:<number> for a sheet.", F.Of(FieldType.Ref)).Req().Count(1),
                    F.Enum("format", "Image format.", "png", "jpg").Def("png"),
                    F.Integer("pixelWidth", "Image width in pixels, 32 to 15000.").Range(32, 15000).Def(2000),
                    C.FileName("\"{view}.png\"", C.RevitFilePlaceholders),
                    C.OutputDir(),
                    C.Overwrite(),
                },
                outputs: new[] { C.StringList("files", "Paths written.") });

            yield return Op("element.delete", "Delete the listed elements only. The ids must come from a snapshot; count must match.",
                new[]
                {
                    C.RefList("ids", "Element ids to delete.").Req().Count(1),
                    F.Integer("expectCount", "Must equal the number of ids; guards against a list built by mistake.").Req(),
                    F.Bool("allowDependents", "Allow Revit to delete dependent elements too (they are listed in the report). When false any dependent deletion fails the op. Parts of a listed element (its sketch, sketch plane and sketch lines) are always deleted with it and are not counted as dependents.").Def(false),
                },
                outputs: new[] { C.RefList("deleted", "Everything deleted, dependents included.") },
                destructive: true);

            yield return Op("cleanup.unplacedRooms", "Delete unplaced (and optionally not enclosed or redundant) rooms, listed by id from a snapshot.",
                new[]
                {
                    C.RefList("ids", "Room ids to delete; each must be unplaced (or, when allowed, not enclosed or redundant).").Req().Count(1),
                    F.Bool("includeNotEnclosed", "Allow deleting placed rooms that are not enclosed.").Def(false),
                    F.Bool("includeRedundant", "Allow deleting redundant rooms.").Def(false),
                },
                outputs: new[] { C.RefList("deleted", "Rooms deleted.") },
                destructive: true);

            yield return Op("cleanup.unusedViews", "Delete views that are not on sheets, listed by id from a snapshot.",
                new[]
                {
                    C.RefList("ids", "View ids; each must be off every sheet and not a template unless includeTemplates.").Req().Count(1),
                    F.Bool("includeTemplates", "Allow deleting unused view templates.").Def(false),
                    F.Bool("keepWithDependents", "Skip views that have dependent views.").Def(true),
                    F.Bool("dryRun", "Delete nothing; list the views that would be deleted in wouldDelete.").Def(false),
                },
                outputs: new[] { C.RefList("deleted", "Views deleted."), C.RefList("skipped", "Views kept and why in warnings."), C.RefList("wouldDelete", "With dryRun: views that would be deleted.") },
                destructive: true);

            yield return Op("workset.assign", "Move elements to a workset.",
                new[]
                {
                    C.RefList("elements", "Element ids."),
                    ElementFilter(),
                    F.String("workset", "User workset name.").Req(),
                    F.Bool("create", "Create the workset if missing.").Def(false),
                },
                new[] { K.ExactlyOne("elements", "filter") },
                new[] { F.Integer("count", "Elements moved.") });
        }
    }
}
