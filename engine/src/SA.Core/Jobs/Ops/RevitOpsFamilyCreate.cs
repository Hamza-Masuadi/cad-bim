using System.Collections.Generic;
using SA.Core.Families;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>family.create: a new loadable family built from a Revit family template. Cross-field rules: <see cref="FamilyRecipe"/>.</summary>
    public static class RevitOpsFamilyCreate
    {
        public static readonly string[] FormKinds = { "extrusion", "blend", "revolution", "sweep", "sweptBlend" };
        public static readonly string[] Axes = { "x", "y", "z" };
        public static readonly string[] Sketches = { "plan", "front", "side" };
        public static readonly string[] LockFaces = { "+x", "-x", "+y", "-y", "+z", "-z" };

        private static FieldSpec PlanePair(string name, string what) =>
            F.List(name, "Two reference plane names for the " + what + ".", F.Of(FieldType.String)).Count(2, 2);

        private static FieldSpec Detail(string level) => F.Bool(level, "Shown at " + level + " detail level.").Def(true);

        public static IEnumerable<OpSchema> All()
        {
            yield return new OpSchema("family.create", AppKind.Revit,
                "Create a new loadable family (.rfa) from a Revit family template: parameters, formulas, types, reference planes, " +
                "labelled dimensions that drive the geometry, solid and void forms (extrusion, blend, revolution, sweep, swept blend) " +
                "with material parameters, subcategories and visibility, and plan symbolic lines. Every type is then made current and " +
                "regenerated (the flex test): its solid geometry's size must match the parameters named in flex, with no Revit " +
                "warning, or the op fails and nothing is kept. The family is saved as <outputDir>/<name>.rfa (put in place only when " +
                "the job commits) and loaded into the project. Must be alone in its job; verify checks and captures are allowed. A " +
                "rehearsal builds, flex-tests, saves to a temporary folder and loads inside the job's undo step, then rolls it all " +
                "back. A family of the same name already in the project, or an existing file, is refused unless overwrite is true. " +
                "Reserved plane names: \"" + FamilyRecipe.CenterLeftRight + "\" (x = 0), \"" + FamilyRecipe.CenterFrontBack +
                "\" (y = 0), \"" + FamilyRecipe.RefLevel + "\" (z = 0); they are the template's own planes and never move.",
                new[]
                {
                    F.String("name", "Family name, also the file name without .rfa. Becomes the family's name in the project.").Req(),
                    F.String("category", "Family category: built-in name (OST_Furniture) or display name (Furniture). Picks the template; when the " +
                        "language folders have no template for it, a Generic Model template is used and its category changed."),
                    F.String("template", "Absolute path of a family template (.rft) to use instead of finding one by category."),
                    F.String("outputDir", "Absolute folder for the .rfa. Default: the job's output folder, sub-folder 'families'."),
                    F.Bool("overwrite", "Replace an existing .rfa at the path, and reload a family of the same name already in the project " +
                        "(declared as a change to that family and its types).").Def(false),
                    F.Enum("parameterValues", "When overwrite reloads a family the project already has: 'family' gives existing types this " +
                        "family's values; 'project' keeps the values the project's types have.", "family", "project").Def("family"),
                    F.Bool("load", "Load the family into the project after saving it.").Def(true),
                    F.List("parameters", "Family parameters to add.",
                        F.OfObject("",
                            F.String("name", "Parameter name, used in formulas, dimensions and type values exactly as written.").Req(),
                            F.Enum("spec", "Kind of value.", RevitOpsFamilyWave2.ParameterSpecs).Req(),
                            F.Bool("instance", "Instance parameter (each placed element has its own value) rather than type parameter.").Def(false),
                            F.Enum("group", "Group in the properties palette. Default: dimensions for lengths and angles, materials for " +
                                "materials, graphics for yes/no, otherwise data.", RevitOpsFamilyWave2.ParameterGroups),
                            F.String("formula", "Revit formula (for example \"Width / 2\" or \"Height - 50 mm\"). It may name only parameters declared here. " +
                                "A parameter with a formula takes no values."),
                            F.Any("value", "Value given to every type (a type's own values win). Lengths in job units, angles in degrees, " +
                                "yes/no as true/false, materials by name (created in the family when missing), text as text."))),
                    F.List("types", "Family types, in order; the first is the family's default.",
                        F.OfObject("",
                            F.String("name", "Type name.").Req(),
                            F.Map("values", "Parameter name to value for this type (same units as parameters[].value)."))).Count(1).Req(),
                    F.List("planes", "Reference planes, placed at their starting positions; labelled dimensions then move them to each type's values.",
                        F.OfObject("",
                            F.String("name", "Plane name (unique; not a reserved name).").Req(),
                            F.Enum("axis", "The axis the plane is perpendicular to: x for left and right planes, y for front and back, z for top and bottom.", Axes).Req(),
                            F.Length("at", "Starting position along that axis from the family origin.").Req(),
                            F.Bool("pinned", "Pin the plane so dimensions never move it (a fixed offset from the origin).").Def(false))),
                    F.List("dimensions", "Dimensions between planes of one axis: two planes labelled with a length parameter (it drives their distance), " +
                        "or three or more planes with equal true (equal spacing).",
                        F.OfObject("",
                            F.List("planes", "Plane names, in order along the axis.", F.Of(FieldType.String)).Count(2).Req(),
                            F.String("parameter", "Length parameter that labels a two-plane dimension."),
                            F.Bool("equal", "Keep the segments equal (three or more planes).").Def(false))),
                    F.List("forms", "Solid and void forms. Faces are locked to planes (box, or lock), so the forms follow the dimensions.",
                        F.OfObject("",
                            F.String("id", "Name for the form in the report."),
                            F.Enum("kind", "Form kind.", FormKinds).Req(),
                            F.Bool("void", "A void that cuts the solids instead of a solid.").Def(false),
                            F.Object("box", "Extrusion only: a box between two x planes, two y planes and two z planes, every face locked to its plane.",
                                PlanePair("x", "left and right faces").Req(),
                                PlanePair("y", "front and back faces").Req(),
                                PlanePair("z", "bottom and top faces").Req()),
                            F.Enum("sketch", "Sketch plane: plan (points are [x, y], height along z), front (points [x, z], depth along y) or " +
                                "side (points [y, z], depth along x).", Sketches).Def("plan"),
                            F.Length("offset", "Position of the sketch plane along its normal (z for plan).").Def(0),
                            F.List("profile", "Closed loops (the last point joins the first). Sweep and swept blend profiles are drawn in their own " +
                                "x-y plane around the path's start.",
                                F.OfObject("",
                                    F.List("points", "Corner points.", F.Of(FieldType.Point2)).Count(3),
                                    F.Point2("centre", "Circle centre."),
                                    F.Length("radius", "Circle radius.").Positive())
                                    .With(K.ExactlyOne("points", "centre")).With(K.Requires("centre", "radius"))),
                            F.List("topProfile", "Blend and swept blend: the second (top or end) loop.", F.Of(FieldType.Point2)).Count(3),
                            F.Length("start", "Extrusion and blend: start distance from the sketch plane along its normal.").Def(0),
                            F.Length("end", "Extrusion and blend: end distance from the sketch plane along its normal."),
                            F.List("axis", "Revolution: two points of the axis, in sketch coordinates.", F.Of(FieldType.Point2)).Count(2, 2),
                            F.Angle("startAngle", "Revolution start angle.").Def(0),
                            F.Angle("endAngle", "Revolution end angle.").Def(360),
                            F.List("path", "Sweep: an open polyline in the sketch plane. Swept blend: exactly two points.", F.Of(FieldType.Point2)).Count(2),
                            F.Integer("profileAt", "Sweep: index of the path segment whose start carries the profile.").Def(0).NonNegative(),
                            F.Map("lock", "Face to plane: \"+x\", \"-x\", \"+y\", \"-y\", \"+z\" or \"-z\" (the face's outward direction) to the name of a " +
                                "plane of that axis. The face must lie on the plane's starting position.", F.Of(FieldType.String)),
                            F.String("material", "Material parameter that drives the form's material (solids only)."),
                            F.String("subcategory", "Subcategory of the family category for the form (created when missing)."),
                            F.String("visibleParameter", "Yes/no parameter that switches the form on and off."),
                            F.Object("visibility", "Where the form shows in the project.",
                                F.Bool("plan", "Shown in plan and ceiling plan views.").Def(true),
                                F.Bool("frontBack", "Shown in front and back views.").Def(true),
                                F.Bool("leftRight", "Shown in left and right views.").Def(true),
                                Detail("coarse"), Detail("medium"), Detail("fine")))).Count(1).Req(),
                    F.List("symbols", "Plan symbolic lines, drawn at the family's base and shown only in plan views.",
                        F.OfObject("",
                            F.Object("rect", "A rectangle whose four lines are locked to two x planes and two y planes.",
                                PlanePair("x", "left and right lines").Req(),
                                PlanePair("y", "front and back lines").Req()),
                            F.Object("line", "One line lying on plane 'on' (an x or y plane), running between planes 'from' and 'to' of the other axis.",
                                F.String("on", "Plane the line lies on and is locked to.").Req(),
                                F.String("from", "Plane at the line's start.").Req(),
                                F.String("to", "Plane at the line's end.").Req()),
                            F.String("subcategory", "Subcategory for the lines (created when missing)."),
                            Detail("coarse"), Detail("medium"), Detail("fine"))
                            .With(K.ExactlyOne("rect", "line"))),
                    F.Object("flex", "Expected size of the solid geometry for every type: the length parameter that the overall size along each axis must equal.",
                        F.String("x", "Length parameter for the size along x (width)."),
                        F.String("y", "Length parameter for the size along y (depth)."),
                        F.String("z", "Length parameter for the size along z (height)."),
                        F.Length("tolerance", "Allowed difference. Default: the job unit's length tolerance.").Positive()),
                },
                new[] { K.AtLeastOne("category", "template") },
                new[]
                {
                    F.Ref("family", "Id of the family in the project (when loaded)."),
                    F.String("familyName", "The family's name."),
                    F.String("file", "Final path of the .rfa."),
                    F.List("types", "Type names, in order.", F.Of(FieldType.String)),
                    F.Map("typeIds", "Type name to the id of the project type (when loaded)."),
                    F.List("flex", "Per type: expected and measured size.", F.Of(FieldType.Object)),
                },
                ensure: false, destructive: false, requiresAllowScript: false);
        }
    }
}
