using System.Collections.Generic;
using F = SA.Core.Jobs.Field;
using K = SA.Core.Jobs.FieldConstraint;

namespace SA.Core.Jobs.Ops
{
    /// <summary>The Revit family editing ops.</summary>
    public static class RevitOpsFamilyWave2
    {
        /// <summary>Kinds of value a new family parameter can hold, as family.edit names them.</summary>
        public static readonly string[] ParameterSpecs =
            { "length", "area", "volume", "angle", "slope", "number", "integer", "currency", "text", "url", "yesNo", "material" };

        /// <summary>Property palette groups a new family parameter can go in ("dimensions" is Revit's Dimensions group).</summary>
        public static readonly string[] ParameterGroups =
            { "data", "dimensions", "identityData", "constraints", "materials", "text", "graphics", "general", "construction", "visibility", "phasing" };

        private const string Values =
            "Family parameter name (display name, built-in parameter name or shared parameter GUID) to value. Lengths in job units, " +
            "areas and volumes in m2/m3 (ft2/ft3 for an imperial job), angles in degrees, yes/no as true/false, materials by the name of a " +
            "material in the family, text as text.";

        public static IEnumerable<OpSchema> All()
        {
            yield return new OpSchema("family.edit", AppKind.Revit,
                "Edit a loaded family: add family parameters, set or clear formulas, create types and set type values, then load it back " +
                "into the project (existing types take the new values) and read every value back from the project. Must be alone in its " +
                "job; verify checks and captures are allowed. A rehearsal makes every change in a copy of the family and reads it back there, " +
                "but does not load it into the project. In-place families, system families (walls, floors...) and families Revit marks as " +
                "not editable are refused; nested families are edited through their own loaded family only when they are shared. Types are " +
                "never renamed or deleted.",
                new[]
                {
                    F.String("family", "Name of the loaded family, or its element id.").Req(),
                    F.String("category", "Category of the family (built-in name such as OST_Doors, or display name), to tell apart families with the same name."),
                    F.List("addParameters", "Family parameters to add. A parameter that already exists with the same kind and instance setting is left as it is; one that differs is refused.",
                        F.OfObject("",
                            F.String("name", "Parameter name.").Req(),
                            F.Enum("spec", "Kind of value.", ParameterSpecs).Req(),
                            F.Enum("group", "Group in the properties palette.", ParameterGroups).Def("data"),
                            F.Bool("instance", "Instance parameter (each placed element has its own value) rather than type parameter.").Def(false))),
                    F.Map("formulas", "Family parameter name to formula, in Revit's formula syntax (for example \"Width / 2\"). Null or \"\" removes the formula.",
                        F.Of(FieldType.String)),
                    F.Map("values", "Values set in every type of the family (a type's own values win). " + Values),
                    F.List("types", "Types to change or create.",
                        F.OfObject("",
                            F.String("name", "Type name.").Req(),
                            F.Bool("create", "Create the type when the family has none of that name. When false a missing type is an error.").Def(false),
                            F.String("copyFrom", "Existing type whose values a new type starts from. Defaults to the family's first type."),
                            F.Map("values", Values))),
                },
                new[] { K.AtLeastOne("addParameters", "formulas", "values", "types") },
                new[]
                {
                    F.Ref("family", "Family id."),
                    F.Map("types", "Type name to the id of the project type, for every type of the family after the load."),
                },
                ensure: false, destructive: false, requiresAllowScript: false);
        }
    }
}
