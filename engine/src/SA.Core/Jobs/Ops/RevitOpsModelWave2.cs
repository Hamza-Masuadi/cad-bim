using System.Collections.Generic;
using SA.Core.Json;
using C = SA.Core.Jobs.Ops.Common;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>
    /// The wave-2 Revit modelling, placement, materials, links and purge ops. Same conventions as the wave-1 Revit
    /// schemas: lengths in job units, points in project coordinates (internal origin) in plan, ids as strings.
    /// </summary>
    public static class RevitOpsModelWave2
    {
        private const string Paper = "paper units (millimetres on the printed sheet for a metric job, inches for an imperial one)";

        public const string RgbPattern = @"\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*";

        public static readonly string[] LayerFunctions = { "structure", "substrate", "thermal", "membrane", "finish1", "finish2" };

        private static OpSchema Op(string name, string summary, FieldSpec[] fields, K[]? constraints = null, FieldSpec[]? outputs = null,
            bool ensure = false, bool destructive = false) =>
            new OpSchema(name, AppKind.Revit, summary, fields, constraints, outputs, ensure, destructive, false);

        private static FieldSpec View(string name, string description) => F.Ref(name, description + " A view id, a reference to an op that created it, or the view's exact name.");

        private static FieldSpec ElementFilter() =>
            F.Object("filter", "Select elements instead of listing ids.",
                F.String("category", "Category, by built-in name (OST_Doors) or display name (Doors).").Req(),
                F.String("level", "Only elements on this level."),
                F.String("type", "Only elements of this type (\"Family : Type\" or type name)."),
                F.Map("where", "Parameter name to required value."),
                View("view", "Only elements visible in this view."));

        private static FieldSpec Rgb(string name, string description) =>
            F.String(name, description + " Written \"r,g,b\", each 0-255.").Matching(RgbPattern);

        private static FieldSpec Pattern(string name, string description, bool withTarget) =>
            withTarget
                ? F.Object(name, description,
                    F.String("pattern", "Fill pattern name, \"solid\" for solid fill, or \"none\" to clear.").Req(),
                    F.Enum("target", "Which kind of pattern the name refers to. Model patterns stay fixed to the model; drafting patterns keep their printed size.", "model", "drafting").Def("drafting"),
                    Rgb("color", "Pattern colour."))
                : F.Object(name, description,
                    F.String("pattern", "Drafting fill pattern name, \"solid\" for solid fill, or \"none\" to clear.").Req(),
                    Rgb("color", "Pattern colour."));

        public static IEnumerable<OpSchema> All()
        {
            yield return Op("family.place", "Place family instances of a loaded type: on a level, hosted on a wall, floor, ceiling or roof, or on a work plane.",
                new[]
                {
                    C.FamilyType("type", "Family type to place.").Req(),
                    F.String("category", "Category the type must belong to (OST_Furniture or Furniture); narrows the lookup when two families share a type name."),
                    C.PointList("points", "Insertion points, one instance each.").Req().Count(1),
                    F.String("level", "Level the instances are placed on (level-based families) or associated with (hosted families). Defaults to the host's level for hosted families."),
                    F.Length("offset", "Height above the level; on a wall face or a vertical reference plane, the height of the point above the level; on a horizontal face or work plane, the offset from it.").Def(0),
                    F.Ref("host", "Wall, floor, ceiling or roof to host the instances."),
                    F.Enum("face", "With host and a face-based family: which face of the host to place on.", "top", "bottom", "exterior", "interior"),
                    F.String("workPlane", "Reference plane or level, by name, to place work-plane-based families on."),
                    F.Angle("rotation", "Rotation about the vertical axis through each point, counter-clockwise.").Def(0),
                    F.Bool("flipHand", "Flip left/right after placing.").Def(false),
                    F.Bool("flipFacing", "Flip front/back after placing.").Def(false),
                    F.String("mark", "Mark. With several points each instance gets mark-1, mark-2 ..."),
                    F.Map("parameters", "Instance parameter name to value, set after placing."),
                },
                new[] { K.AtMostOne("host", "workPlane"), K.Requires("face", "host") },
                new[] { C.RefList("elements", "Instances placed, in point order."), F.Integer("count", "Number placed.") });

            yield return Op("group.place", "Make a model group from elements, or place instances of an existing model group type.",
                new[]
                {
                    C.RefList("elements", "Elements to group. Elements already in a group are refused."),
                    F.String("name", "Name for the new group type made from elements."),
                    F.String("groupType", "Existing model group type to place, by name."),
                    C.PointList("points", "Group origin for each instance placed. With elements, extra copies of the new group."),
                    F.String("level", "Level the points are on (their height is the level's elevation)."),
                    F.Angle("rotation", "Rotation of each placed instance about its origin, counter-clockwise.").Def(0),
                },
                new[] { K.ExactlyOne("elements", "groupType"), K.Requires("elements", "name"), K.Requires("groupType", "points"), K.Requires("points", "level") },
                new[]
                {
                    F.Ref("groupType", "Group type made or placed."),
                    F.Ref("group", "With elements: the group made from them."),
                    C.RefList("placed", "Instances placed at points."),
                });

            yield return Op("element.transform", "Move, copy, rotate or mirror elements in plan.",
                new[]
                {
                    C.RefList("elements", "Elements to transform. Pinned elements are refused.").Req().Count(1),
                    F.Enum("action", "move or copy by vector, rotate about a vertical axis, or mirror about a vertical plane.", "move", "copy", "rotate", "mirror").Req(),
                    F.Point2("vector", "move and copy: displacement in plan."),
                    F.Length("dz", "move and copy: vertical displacement.").Def(0),
                    F.Integer("copies", "copy: number of copies, each one vector further on.").Range(1, 200).Def(1),
                    F.Point2("centre", "rotate: the vertical axis passes through this point."),
                    F.Angle("angle", "rotate: angle, counter-clockwise."),
                    F.Object("axis", "mirror: the vertical mirror plane through this line in plan.",
                        F.Point2("start", "Line start.").Req(),
                        F.Point2("end", "Line end.").Req()),
                    F.Bool("keepOriginal", "mirror: make mirrored copies and keep the originals. When false the elements themselves are mirrored.").Def(true),
                },
                new[] { K.AtMostOne("vector", "centre", "axis"), K.Requires("centre", "angle") },
                new[]
                {
                    C.RefList("elements", "move, rotate and mirror in place: the same elements. copy and mirror copies: every new element, copies in order."),
                    F.Integer("count", "Number of ids in elements."),
                });

            yield return Op("phase.set", "Set Phase Created and Phase Demolished on elements, and phase and phase filter on views.",
                new[]
                {
                    C.RefList("elements", "Elements to set phases on."),
                    ElementFilter(),
                    F.String("created", "Phase Created, by phase name."),
                    F.String("demolished", "Phase Demolished, by phase name, or \"none\" to clear it."),
                    F.List("views", "Views to set the phase and phase filter of.", F.Of(FieldType.Ref)),
                    F.String("viewPhase", "Phase of the views, by name."),
                    F.String("phaseFilter", "Phase filter of the views, by name, or \"none\"."),
                },
                new[]
                {
                    K.AtMostOne("elements", "filter"), K.AtLeastOne("elements", "filter", "views"),
                },
                new[] { F.Integer("elementCount", "Elements changed."), F.Integer("viewCount", "Views changed.") });

            yield return Op("compoundtype.ensure", "Create a floor, ceiling or roof type with a compound layer build-up.",
                new[]
                {
                    F.Enum("kind", "Type family.", "floor", "ceiling", "roof").Req(),
                    F.String("name", "Type name, e.g. \"Slab - 200 Concrete\".").Req(),
                    F.String("basedOn", "Type of the same kind to duplicate. Defaults to the first type of that kind with a layered structure."),
                    F.List("layers", "Layers from top to bottom.", F.OfObject("",
                        F.Enum("function", "Layer function.", LayerFunctions).Req(),
                        F.String("material", "Material name; created plainly if missing.").Req(),
                        F.Length("thickness", "Layer thickness (0 only for membranes).").NonNegative().Req(),
                        F.Bool("core", "Inside the core boundaries. Core layers must be contiguous.").Def(false),
                        F.Bool("variable", "Floors and roofs: the layer that varies in thickness under shape edits. One at most.").Def(false))).Req().Count(1),
                    C.Update(),
                },
                outputs: new[] { F.Ref("type", "Type id."), F.Length("thickness", "Total thickness read back.") },
                ensure: true);

            yield return Op("wallsweep.add", "Add wall sweeps (skirtings, cornices) or reveals to walls, from a sweep or reveal type.",
                new[]
                {
                    C.RefList("walls", "Walls to add to."),
                    ElementFilter(),
                    F.String("type", "Wall sweep type (kind sweep) or reveal type (kind reveal), by name.").Req(),
                    F.Enum("kind", "Sweep or reveal.", "sweep", "reveal").Def("sweep"),
                    F.Length("distance", "Height above the wall base, or below the wall top with measuredFrom top.").NonNegative().Req(),
                    F.Enum("measuredFrom", "Where distance is measured from.", "base", "top").Def("base"),
                    F.Enum("side", "Wall face.", "exterior", "interior").Def("interior"),
                    F.Length("wallOffset", "Offset from the wall face.").Def(0),
                    F.Bool("cutByInserts", "Doors and windows interrupt the sweep.").Def(true),
                    F.Bool("skipExisting", "Leave a wall alone when it already has a sweep of this type on this side at this distance.").Def(true),
                },
                new[] { K.ExactlyOne("walls", "filter") },
                new[] { C.RefList("sweeps", "Sweeps or reveals created."), C.RefList("skipped", "Walls that already had one.") });

            yield return Op("fillpattern.ensure", "Create fill patterns: stacked tiles, running bond, parallel lines or raw grids.",
                new[]
                {
                    F.List("patterns", "Patterns to ensure.", F.OfObject("",
                        F.String("name", "Pattern name.").Req(),
                        F.Enum("target", "model patterns are sized in the model; drafting patterns in " + Paper + ".", "model", "drafting").Req(),
                        F.Enum("orientation", "Drafting patterns: how the pattern aligns in host layers. Defaults to to_view; model patterns always use to_host.", "to_view", "to_host", "as_text"),
                        F.Object("stack", "Stacked grid: rectangles width by height.",
                            F.Length("width", "Tile width.").Positive().Req(),
                            F.Length("height", "Tile height.").Positive().Req()),
                        F.Object("bond", "Running bond: courses of units, each course shifted by a fraction of the length.",
                            F.Length("length", "Unit length.").Positive().Req(),
                            F.Length("height", "Course height.").Positive().Req(),
                            F.Integer("fraction", "Shift per course is length divided by this (2 = half bond).").Range(2, 6).Def(2)),
                        F.Object("lines", "Parallel lines.",
                            F.Length("spacing", "Distance between lines.").Positive().Req(),
                            F.Angle("angle", "Line direction, counter-clockwise from horizontal.").Def(0)),
                        F.List("grids", "Raw line families, as in a pattern file.", F.OfObject("",
                            F.Angle("angle", "Line direction, counter-clockwise from horizontal.").Def(0),
                            F.Length("originU", "Origin of the first line, u (x).").Def(0),
                            F.Length("originV", "Origin of the first line, v (y).").Def(0),
                            F.Length("shift", "Offset along the line between successive lines.").Def(0),
                            F.Length("offset", "Distance between successive lines.").Positive().Req(),
                            F.List("segments", "Dashes (positive) and gaps (negative), alternating from a dash. Empty or missing for a continuous line.", C.Len()))).Count(1, 8))
                        .With(K.ExactlyOne("stack", "bond", "lines", "grids"))).Req().Count(1),
                    C.Update(),
                },
                outputs: new[] { F.Map("patterns", "Pattern name to element id.") },
                ensure: true);

            yield return Op("material.ensure", "Create Revit materials with identity data, graphics, surface and cut patterns and an optional render appearance.",
                new[]
                {
                    F.List("materials", "Materials to ensure.", F.OfObject("",
                        F.String("name", "Material name.").Req(),
                        F.String("basedOn", "Material to duplicate when creating; its assets are shared until edited."),
                        F.Object("identity", "Identity data.",
                            F.String("description", "Description."),
                            F.String("class", "Material class (the Material Browser's class)."),
                            F.String("category", "Material category."),
                            F.String("typeComments", "Type comments."),
                            F.String("manufacturer", "Manufacturer."),
                            F.String("model", "Model."),
                            F.Number("cost", "Cost.").NonNegative(),
                            F.String("url", "URL."),
                            F.String("keynote", "Keynote."),
                            F.String("mark", "Mark: the finish code tagged on drawings."),
                            F.String("assemblyCode", "Assembly code (Uniformat on 2024-2025)."),
                            F.String("classificationCode", "Classification code (OmniClass on 2024-2025).")),
                        F.Object("shading", "Graphics shading.",
                            Rgb("color", "Shading colour."),
                            F.Integer("transparency", "Transparency 0-100.").Range(0, 100),
                            F.Bool("useRenderAppearance", "Shade with the render appearance. New materials default to false.")),
                        F.Object("surface", "Surface patterns.",
                            Pattern("foreground", "Foreground surface pattern.", true),
                            Pattern("background", "Background surface pattern.", false)),
                        F.Object("cut", "Cut patterns.",
                            Pattern("foreground", "Foreground cut pattern.", false),
                            Pattern("background", "Background cut pattern.", false)),
                        F.Object("appearance", "Render appearance. Without from, the material's current appearance asset is edited (made its own first unless shareAsset).",
                            F.String("from", "Appearance asset in the model, by asset name, or the material whose asset to copy."),
                            F.Bool("shareAsset", "Use the asset as it is, shared with the materials already using it. Otherwise the material gets its own copy, named like it.").Def(false),
                            Rgb("color", "Generic diffuse colour."),
                            F.Object("texture", "Generic diffuse image.",
                                F.String("path", "Image file, absolute. A path in a user profile is refused unless allowLocalPath.").Req(),
                                F.Length("width", "Real-world width of one repeat.").Positive(),
                                F.Length("height", "Real-world height of one repeat.").Positive(),
                                F.Length("offsetX", "Horizontal offset.").Def(0),
                                F.Length("offsetY", "Vertical offset.").Def(0),
                                F.Angle("rotation", "Rotation, 0 to 360 degrees.").Range(0, 360).Def(0),
                                F.Bool("repeat", "Tile the image in both directions.").Def(true)),
                            F.Bool("allowLocalPath", "Accept a texture path inside a user profile; other machines will not find it.").Def(false)),
                        F.String("thermalAsset", "Thermal asset of another material to share, by that material's name."),
                        F.String("structuralAsset", "Structural asset of another material to share, by that material's name."))).Req().Count(1),
                    C.Update(),
                },
                outputs: new[]
                {
                    F.Map("materials", "Material name to element id."),
                    F.Map("appearanceAssets", "Material name to appearance asset id, for materials that have one."),
                },
                ensure: true);

            yield return Op("material.assign", "Set a material: on a layer of a wall, floor, ceiling or roof type, on a material parameter of elements, on a category, or by painting faces.",
                new[]
                {
                    F.String("material", "Material name, or \"none\" to clear.").Req(),
                    F.Object("layer", "A layer of a compound type. Changes every instance of the type.",
                        F.String("type", "Wall, floor, ceiling or roof type name.").Req(),
                        F.Enum("kind", "Which kind of type; needed only when two kinds share the name.", "wall", "floor", "ceiling", "roof"),
                        F.Integer("index", "Layer index, 0 at the exterior (walls) or top (floors, ceilings, roofs).").NonNegative(),
                        F.Enum("function", "Pick the layer by function instead of index.", LayerFunctions),
                        F.Enum("side", "With function: take the matching layer nearest this side.", "exterior", "interior", "top", "bottom"))
                        .With(K.ExactlyOne("index", "function")).With(K.Requires("function", "side")),
                    F.Object("parameter", "A material parameter of elements or of their types.",
                        C.RefList("elements", "Elements."),
                        ElementFilter(),
                        F.Enum("target", "Set the instance parameter or the type parameter.", "instance", "type").Def("instance"),
                        F.String("parameter", "Material parameter name. Defaults to the only material parameter, refused when there are several."))
                        .With(K.ExactlyOne("elements", "filter")),
                    F.Object("category", "A category or subcategory material (Object Styles).",
                        F.String("category", "Category, by built-in name or display name.").Req(),
                        F.String("subcategory", "Subcategory name.")),
                    F.Object("paint", "Paint faces of walls, floors, ceilings or roofs. \"none\" removes paint.",
                        C.RefList("elements", "Host elements to paint.").Req().Count(1),
                        F.List("faces", "Which faces: exterior and interior for walls, top and bottom for floors, ceilings and roofs.", F.OfEnum("exterior", "interior", "top", "bottom")).Req().Count(1)),
                    F.Integer("maxInstances", "Type-wide and category targets (layer, type parameter, category): pass when the changed types have at most this many instances, or the category at most this many elements. Without it any instance or element needs confirmTypeWide.").NonNegative(),
                    F.Bool("confirmTypeWide", "Allow a type-wide or category-wide change whatever the number of instances.").Def(false),
                },
                new[] { K.ExactlyOne("layer", "parameter", "category", "paint") },
                new[]
                {
                    F.Integer("count", "Layers, elements, types, categories or faces changed."),
                    F.Integer("instancesAffected", "Type-wide targets: instances of the changed types. Category: elements of the category."),
                });

            yield return Op("link.cad", "Link a DWG or DXF file into a view.",
                new[]
                {
                    F.String("path", "DWG or DXF file. Must exist.").Req(),
                    View("view", "View to link into. With thisViewOnly the link shows only there.").Req(),
                    F.Bool("thisViewOnly", "Show the link only in this view (not available in 3D views).").Def(true),
                    F.Enum("placement", "Positioning: origin to origin, centred, by shared coordinates, or at point.", "origin", "centre", "shared", "point").Def("origin"),
                    F.Point2("point", "With placement point: where the file's origin goes."),
                    F.Enum("unit", "Import units; auto reads the file's.", "auto", "mm", "cm", "dm", "m", "in", "ft", "us_survey_ft", "custom").Def("auto"),
                    F.Number("scale", "With unit custom: scale factor.").Positive(),
                    F.Enum("colorMode", "Layer colours.", "preserve", "black_and_white", "invert").Def("preserve"),
                    F.Bool("visibleLayersOnly", "Link only layers visible in the file.").Def(true),
                    F.Bool("correctLines", "Correct lines that are slightly off axis.").Def(true),
                    F.Bool("orientToView", "Orient to the view.").Def(true),
                    C.StringList("hideLayers", "Layer names to hide in the view after linking."),
                    F.Bool("pin", "Pin the link.").Def(true),
                    F.Bool("allowDuplicate", "Add another instance when this file is already linked into this view.").Def(false),
                },
                new[] { K.Requires("point", "placement"), K.Requires("scale", "unit") },
                new[] { F.Ref("instance", "Import instance."), F.Ref("linkType", "CAD link type."), C.StringList("hiddenLayers", "Layers hidden in the view.") });

            yield return Op("link.revit", "Link a Revit model, optionally acquiring shared coordinates from it. Publishing coordinates writes into the linked model and is not offered.",
                new[]
                {
                    F.String("path", "RVT file. Must exist.").Req(),
                    F.Enum("placement", "origin to origin, by shared coordinates, or at point.", "origin", "shared", "point").Def("origin"),
                    F.Point2("point", "With placement point: where the link's internal origin goes."),
                    F.Length("elevation", "With placement point: height of the link's internal origin.").Def(0),
                    F.Angle("rotation", "With placement point: rotation about the link's origin, counter-clockwise.").Def(0),
                    F.Bool("relative", "Store the path relative to this model.").Def(true),
                    F.Enum("worksets", "Workshared links: which worksets to open.", "all", "none", "last_viewed").Def("all"),
                    F.Enum("coordinates", "After placing: acquire shared coordinates (and geolocation) from the link. Refused when the link is placed more than once, or when it would remove or replace this model's geographic coordinate system.", "none", "acquire").Def("none"),
                    F.Bool("pin", "Pin the link.").Def(true),
                    F.Bool("allowDuplicate", "Add another instance when this file is already linked.").Def(false),
                },
                new[] { K.Requires("point", "placement") },
                new[] { F.Ref("instance", "Link instance."), F.Ref("linkType", "Link type."), F.Bool("typeCreated", "A new link type was loaded.") });

            yield return Op("cleanup.purge", "Purge unused families, types, materials and other elements Revit reports as unused. List with dryRun, then delete the listed ids.",
                new[]
                {
                    C.StringList("categories", "Only these categories (built-in or display names). Default: everything Revit reports as unused."),
                    F.Bool("dryRun", "Delete nothing; list what is unused in wouldDelete.").Def(false),
                    C.RefList("ids", "Ids to delete, taken from a dry run. Each must still be unused."),
                    F.Integer("expectCount", "Must equal the number of ids.").NonNegative(),
                    F.Bool("keepStandard", "Skip elements the standard names as ones to keep.").Def(true),
                },
                new[] { K.Requires("ids", "expectCount"), K.Requires("expectCount", "ids") },
                new[]
                {
                    C.RefList("deleted", "Ids deleted."),
                    C.RefList("wouldDelete", "With dryRun: unused ids, in order."),
                    F.List("items", "With dryRun: { id, name, category } per unused id.", F.Of(FieldType.Object)),
                    F.Integer("remainingUnused", "After deleting: how many elements are now unused (purging can free more)."),
                },
                destructive: true);

            yield return Op("export.nwc", "Export the model to a Navisworks cache file. Needs the Navisworks exporter installed. Runs alone or with other exports only.",
                new[]
                {
                    C.FileName("\"{drawing}.nwc\"", C.RevitFilePlaceholders).Req(),
                    C.OutputDir(),
                    F.Enum("scope", "Whole model, the elements visible in a view, or listed elements.", "model", "view", "elements").Def("model"),
                    View("view", "With scope view: the 3D view."),
                    C.RefList("elements", "With scope elements: the elements."),
                    F.Enum("coordinates", "Shared or internal coordinates.", "shared", "internal").Def("shared"),
                    F.Enum("parameters", "Which parameters to export.", "all", "elements", "none").Def("all"),
                    F.Bool("convertElementProperties", "Convert element properties.").Def(false),
                    F.Bool("exportLinks", "Include linked models.").Def(false),
                    F.Bool("exportParts", "Export parts.").Def(false),
                    F.Bool("divideByLevels", "Divide the file into levels.").Def(true),
                    F.Bool("exportRoomGeometry", "Export room geometry.").Def(true),
                    F.Bool("roomsAsAttribute", "Export rooms as an attribute.").Def(true),
                    F.Bool("elementIds", "Export element ids.").Def(true),
                    F.Bool("urls", "Export URLs.").Def(true),
                    F.Bool("findMissingMaterials", "Try to find missing materials.").Def(true),
                    F.Bool("convertLinkedCad", "Convert linked CAD formats.").Def(true),
                    F.Bool("convertLights", "Convert lights.").Def(false),
                    F.Number("facetingFactor", "Faceting factor.").Positive().Def(1.0),
                    C.Overwrite(),
                },
                new[] { K.Requires("view", "scope"), K.Requires("elements", "scope") },
                new[] { F.String("file", "Path written.") });
        }
    }
}
