using SA.Core.Json;
using F = SA.Core.Jobs.Field;

namespace SA.Core.Jobs.Ops
{
    /// <summary>Field shapes shared by several op declarations.</summary>
    internal static class Common
    {
        public static FieldSpec Update() =>
            F.Bool("update", "Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged.").Def(false);

        public static FieldSpec Str() => F.Of(FieldType.String);
        public static FieldSpec P2() => F.Of(FieldType.Point2);
        public static FieldSpec Len() => F.Of(FieldType.Length);
        public static FieldSpec RefItem(string description = "") => F.Of(FieldType.Ref, description);

        public static FieldSpec StringList(string name, string description) => F.List(name, description, Str());
        public static FieldSpec RefList(string name, string description) => F.List(name, description, RefItem());
        public static FieldSpec PointList(string name, string description) => F.List(name, description, P2());

        public static FieldSpec Layer(string description = "Layer name. Defaults to the standard's layer for this kind of object, else the current layer.") =>
            F.String("layer", description);

        public static FieldSpec Color() =>
            F.Any("color", "ACI number 1-255, \"bylayer\", \"byblock\", \"#RRGGBB\" or \"r,g,b\".");

        public static FieldSpec Lineweight() =>
            F.Any("lineweight", "Millimetres from the standard set (0.13, 0.18, 0.25, 0.35, 0.50, 0.70...), or \"bylayer\", \"byblock\", \"default\".");

        public static FieldSpec Scale(string name = "scale", string description = "Drawing scale such as \"1:100\" or \"1/4\\\" = 1'-0\\\"\".") =>
            F.String(name, description).Matching(@"\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?|.+=.+)\s*");

        /// <summary>"Family : Type" as Revit shows it in the type selector.</summary>
        public static FieldSpec FamilyType(string name, string description) =>
            F.String(name, description + " Written \"Family : Type\".").Matching(@".+\S\s:\s\S.+");

        public static FieldSpec Rect(string name, string description) =>
            F.Object(name, description,
                F.Point2("min", "Lower-left corner.").Req(),
                F.Point2("max", "Upper-right corner.").Req());

        public static FieldSpec OutputDir() =>
            F.String("outputDir", "Folder for the files. Defaults to the job's output folder.");

        /// <summary>Placeholders the AutoCAD engine expands in file names (plot.pdf).</summary>
        public const string AutoCadFilePlaceholders = "{drawing}, {layout}, {sheetNumber}, {sheetName}, {view}, {date}, {jobId}";

        /// <summary>Placeholders the Revit engine expands in file names (export ops): {drawing} is the model's name, {revision} the sheet's current revision, {viewType} the view type name.</summary>
        public const string RevitFilePlaceholders = "{sheetNumber}, {sheetName}, {revision}, {view}, {viewType}, {drawing}, {date}, {jobId}";

        public static FieldSpec FileName(string example) => FileName(example, AutoCadFilePlaceholders);

        public static FieldSpec FileName(string example, string placeholders) =>
            F.String("fileName", "File name pattern. Placeholders: " + placeholders + ". Example: " + example + ". Names are checked against the standard's file naming rule when one is set.");

        public static FieldSpec Overwrite() => F.Bool("overwrite", "Replace files that already exist. When false an existing file fails the op.").Def(false);

        public static FieldSpec[] Packing() => new[]
        {
            F.Length("margin", "Clear space inside the drawable area on every side, in paper units.").NonNegative().Def(0),
            F.Length("gapX", "Horizontal gap between views, in paper units.").NonNegative().Def(0),
            F.Length("gapY", "Vertical gap between rows, in paper units.").NonNegative().Def(0),
            F.Enum("rowAlign", "Horizontal placement of each row.", "left", "centre", "distribute").Def("left"),
            F.Enum("itemAlign", "Vertical placement of views inside a row.", "top", "middle", "bottom").Def("top"),
            F.Bool("failIfNotFit", "Fail the op when any view does not fit. When false, views that fit are placed and the rest are listed in outputs.notPlaced.").Def(true),
        };

        public static FieldSpec[] PackingOutputs() => new[]
        {
            F.List("placements", "One per placed view: { id, viewport, centre [x, y], min [x, y], max [x, y], row }.", F.Of(FieldType.Object)),
            F.List("notPlaced", "Views that did not fit: { id, reason } with reason larger_than_area or no_space.", F.Of(FieldType.Object)),
        };

        public static JsonValue Origin() => new JsonArray(0, 0);
    }
}
