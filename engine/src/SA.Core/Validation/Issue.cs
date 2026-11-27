using System;
using System.Collections.Generic;
using SA.Core.Json;

namespace SA.Core.Validation
{
    public enum IssueSeverity { Error, Warning }

    /// <summary>Issue codes produced by static validation. Engines may add document-level codes of their own.</summary>
    public static class IssueCodes
    {
        public const string Json = "json";
        public const string Required = "required";
        public const string UnknownField = "unknown_field";
        public const string Type = "type";
        public const string Enum = "enum";
        public const string Range = "range";
        public const string Pattern = "pattern";
        public const string Length = "length";
        public const string TooFew = "too_few";
        public const string TooMany = "too_many";
        public const string Constraint = "constraint";
        public const string UnknownOp = "unknown_op";
        public const string WrongApp = "wrong_app";
        public const string DuplicateId = "duplicate_id";
        public const string BadId = "bad_id";
        public const string RefInvalid = "ref_invalid";
        public const string RefUnknown = "ref_unknown";
        public const string RefForward = "ref_forward";
        public const string RefNotAllowed = "ref_not_allowed";
        public const string UnknownCheck = "unknown_check";
        public const string Script = "script";
        public const string DocumentMissing = "document_missing";

        // ----- Engine (document-level) codes. Engines emit these; clients can match on the constants. -----

        /// <summary>The engine has no handler for the op.</summary>
        public const string NoHandler = "no_handler";
        /// <summary>A reference to an earlier op is checked only when the op runs (preflight saw it unresolved).</summary>
        public const string RefDeferred = "ref_deferred";
        public const string ValidationError = "validation_error";
        public const string ReadbackMismatch = "readback_mismatch";
        public const string ReadOnly = "read_only";
        public const string NotFound = "not_found";
        public const string Ambiguous = "ambiguous";
        public const string ExistingDiffers = "existing_differs";
        public const string InvalidValue = "invalid_value";
        public const string AlreadyExists = "already_exists";
        public const string FamilyDocument = "family_document";
        public const string NotAllowed = "not_allowed";
        public const string NotPrechecked = "not_prechecked";
        public const string UnitsUnitless = "units_unitless";
        public const string UnitsUnsupported = "units_unsupported";
        public const string StandardMissing = "standard_missing";
        public const string StandardInvalid = "standard_invalid";
        public const string ScriptNotAllowed = "script_not_allowed";
        public const string ScriptNotAlone = "script_not_alone";
        public const string ScriptNotRehearsed = "script_not_rehearsed";
        public const string LispSyntax = "lisp_syntax";
        public const string LispInteractive = "lisp_interactive";
        public const string LispSsget = "lisp_ssget";
        public const string LayerMissing = "layer_missing";
        public const string LayerFrozen = "layer_frozen";
        public const string LayerNameInvalid = "layer_name_invalid";
        public const string LinetypeMissing = "linetype_missing";
        public const string LineweightInvalid = "lineweight_invalid";
        public const string ColorInvalid = "color_invalid";
        public const string DuplicateLayer = "duplicate_layer";
        public const string DuplicateStyle = "duplicate_style";
        public const string TextStyleMissing = "textstyle_missing";
        public const string FontMissing = "font_missing";
        public const string FontUnverified = "font_unverified";
        public const string BigFontNeedsShx = "bigfont_needs_shx";
        public const string NameInvalid = "name_invalid";
        public const string HeightInvalid = "height_invalid";
        public const string ScaleInvalid = "scale_invalid";
        public const string AnnotationScaleMissing = "annotation_scale_missing";
        public const string TextEmpty = "text_empty";
        public const string TextMultiline = "text_multiline";
        public const string TextHeightUnknown = "text_height_unknown";
        public const string ThicknessMissing = "thickness_missing";
        public const string PatternMissing = "pattern_missing";
        public const string WidthIgnored = "width_ignored";
        public const string CaptureInvalid = "capture_invalid";
        /// <summary>Prefix of wall network issues: "wall_" + <c>WallNetwork</c> issue code (wall_overlap, wall_duplicate...).</summary>
        public const string WallPrefix = "wall_";
    }

    /// <summary>One validation finding. <see cref="Path"/> is a JSON path such as <c>$.ops[1].args.start</c>.</summary>
    public sealed class Issue
    {
        public Issue(string path, string code, string message, IssueSeverity severity = IssueSeverity.Error)
        {
            Path = path ?? JsonPath.Root;
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? "";
            Severity = severity;
        }

        public string Path { get; }
        public string Code { get; }
        public string Message { get; }
        public IssueSeverity Severity { get; }
        public bool IsError => Severity == IssueSeverity.Error;

        public JsonObject ToJson() => new JsonObject()
            .Add("path", Path)
            .Add("code", Code)
            .Add("severity", Severity == IssueSeverity.Error ? "error" : "warning")
            .Add("message", Message);

        public static Issue Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            string sev = c.GetString("severity", "error");
            if (sev != "error" && sev != "warning") throw c.Required("severity").Fail("expected 'error' or 'warning'");
            return new Issue(c.GetString("path", JsonPath.Root), c.GetNonEmptyString("code"), c.GetString("message", ""),
                sev == "error" ? IssueSeverity.Error : IssueSeverity.Warning);
        }

        public override string ToString() => (IsError ? "error " : "warning ") + Code + " at " + Path + ": " + Message;

        public static JsonArray ToJson(IEnumerable<Issue> issues)
        {
            var a = new JsonArray();
            foreach (var i in issues) a.Add(i.ToJson());
            return a;
        }
    }
}
