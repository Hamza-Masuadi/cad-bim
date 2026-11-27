using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;

namespace SA.Core.Jobs
{
    public enum AppKind { AutoCad, Revit }

    public static class AppKinds
    {
        public static string ToWire(AppKind app) => app == AppKind.AutoCad ? "autocad" : "revit";

        public static bool TryParse(string? s, out AppKind app)
        {
            if (s == "autocad") { app = AppKind.AutoCad; return true; }
            if (s == "revit") { app = AppKind.Revit; return true; }
            app = AppKind.AutoCad;
            return false;
        }
    }

    public enum FieldType
    {
        String,
        Number,
        Integer,
        /// <summary>A number in job length units, or a string such as "10'-6\"" or "3.2m".</summary>
        Length,
        /// <summary>Degrees, counter-clockwise from the +X axis unless the description says otherwise.</summary>
        Angle,
        Bool,
        /// <summary>A string from <see cref="FieldSpec.EnumValues"/>.</summary>
        Enum,
        /// <summary>[x, y], each a length.</summary>
        Point2,
        /// <summary>[x, y, z], each a length.</summary>
        Point3,
        /// <summary>An array whose items follow <see cref="FieldSpec.Item"/>.</summary>
        List,
        /// <summary>"$id" or "$id.field" naming an earlier op's result, or a literal element id (string or integer).</summary>
        Ref,
        /// <summary>A nested object with the fields in <see cref="FieldSpec.Fields"/>.</summary>
        Object,
        /// <summary>An object with free keys; values follow <see cref="FieldSpec.Item"/> when given.</summary>
        Map,
        /// <summary>Any JSON value.</summary>
        Any,
    }

    public enum UnitKind { None, Length, Angle }

    /// <summary>
    /// One field of an op's args, or the shape of a list item or map value (then <see cref="Name"/> is "").
    /// Length bounds (<see cref="Min"/>, <see cref="Max"/>) are in millimetres and converted to the job's unit when checked.
    /// </summary>
    public sealed class FieldSpec
    {
        private readonly List<FieldSpec> _fields = new List<FieldSpec>();
        private readonly List<FieldConstraint> _constraints = new List<FieldConstraint>();

        public FieldSpec(string name, FieldType type, string description)
        {
            Name = name ?? "";
            Type = type;
            Description = description ?? "";
        }

        public string Name { get; }
        public FieldType Type { get; }
        public string Description { get; }
        public bool Required { get; private set; }
        public JsonValue? Default { get; private set; }
        public IReadOnlyList<string> EnumValues { get; private set; } = Array.Empty<string>();
        public double? Min { get; private set; }
        public double? Max { get; private set; }
        public bool ExclusiveMin { get; private set; }
        public int? MinItems { get; private set; }
        public int? MaxItems { get; private set; }
        /// <summary>A regular expression a string value must match in full.</summary>
        public string? Pattern { get; private set; }
        public FieldSpec? Item { get; private set; }
        public IReadOnlyList<FieldSpec> Fields => _fields;
        /// <summary>Rules across this object's fields (only for <see cref="FieldType.Object"/>).</summary>
        public IReadOnlyList<FieldConstraint> Constraints => _constraints;

        public UnitKind Unit
        {
            get
            {
                switch (Type)
                {
                    case FieldType.Length:
                    case FieldType.Point2:
                    case FieldType.Point3: return UnitKind.Length;
                    case FieldType.Angle: return UnitKind.Angle;
                    default: return UnitKind.None;
                }
            }
        }

        public FieldSpec Req() { Required = true; return this; }
        public FieldSpec Def(JsonValue value) { Default = value; return this; }
        public FieldSpec Range(double? min, double? max) { Min = min; Max = max; return this; }
        public FieldSpec Positive() { Min = 0; ExclusiveMin = true; return this; }
        public FieldSpec NonNegative() { Min = 0; ExclusiveMin = false; return this; }
        public FieldSpec Count(int? min, int? max = null) { MinItems = min; MaxItems = max; return this; }
        public FieldSpec Matching(string pattern) { Pattern = pattern; return this; }
        internal FieldSpec WithEnum(string[] values) { EnumValues = values; return this; }
        internal FieldSpec WithItem(FieldSpec item) { Item = item; return this; }
        internal FieldSpec WithFields(IEnumerable<FieldSpec> fields) { _fields.AddRange(fields); return this; }
        public FieldSpec With(FieldConstraint constraint) { _constraints.Add(constraint); return this; }

        public FieldSpec? FindField(string name) => _fields.FirstOrDefault(f => f.Name == name);

        public static string TypeName(FieldType t)
        {
            switch (t)
            {
                case FieldType.String: return "string";
                case FieldType.Number: return "number";
                case FieldType.Integer: return "integer";
                case FieldType.Length: return "length";
                case FieldType.Angle: return "angle";
                case FieldType.Bool: return "bool";
                case FieldType.Enum: return "enum";
                case FieldType.Point2: return "point2";
                case FieldType.Point3: return "point3";
                case FieldType.List: return "list";
                case FieldType.Ref: return "ref";
                case FieldType.Object: return "object";
                case FieldType.Map: return "map";
                default: return "any";
            }
        }

        /// <summary>A short type label such as "list<point2>" or "enum(left|right)".</summary>
        public string TypeLabel()
        {
            switch (Type)
            {
                case FieldType.List: return "list<" + (Item?.TypeLabel() ?? "any") + ">";
                case FieldType.Map: return "map<" + (Item?.TypeLabel() ?? "any") + ">";
                case FieldType.Enum: return "enum(" + string.Join("|", EnumValues) + ")";
                default: return TypeName(Type);
            }
        }

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            if (Name.Length > 0) o.Add("name", Name);
            o.Add("type", TypeName(Type));
            if (Name.Length > 0) o.Add("required", Required);
            if (Default != null) o.Add("default", Default.DeepClone());
            if (EnumValues.Count > 0) o.Add("enum", JsonArray.FromStrings(EnumValues));
            if (Unit != UnitKind.None) o.Add("unit", Unit == UnitKind.Length ? "length" : "angle");
            if (Min.HasValue) o.Add(ExclusiveMin ? "exclusiveMin" : "min", Min.Value);
            if (Max.HasValue) o.Add("max", Max.Value);
            if (MinItems.HasValue) o.Add("minItems", MinItems.Value);
            if (MaxItems.HasValue) o.Add("maxItems", MaxItems.Value);
            if (Pattern != null) o.Add("pattern", Pattern);
            if (Description.Length > 0) o.Add("description", Description);
            if (Item != null) o.Add("item", Item.ToJson());
            if (_fields.Count > 0) o.Add("fields", new JsonArray(_fields.Select(f => (JsonValue)f.ToJson())));
            if (_constraints.Count > 0) o.Add("constraints", new JsonArray(_constraints.Select(c => (JsonValue)c.ToJson())));
            return o;
        }
    }

    public enum ConstraintKind
    {
        /// <summary>Exactly one of the fields must be present.</summary>
        ExactlyOne,
        /// <summary>At least one of the fields must be present.</summary>
        AtLeastOne,
        /// <summary>No more than one of the fields may be present.</summary>
        AtMostOne,
        /// <summary>If the first field is present, every other listed field must be too.</summary>
        Requires,
    }

    public sealed class FieldConstraint
    {
        public FieldConstraint(ConstraintKind kind, params string[] fields)
        {
            if (fields is null || fields.Length < 2) throw new ArgumentException("A constraint names at least two fields.");
            Kind = kind;
            Fields = fields;
        }

        public ConstraintKind Kind { get; }
        public IReadOnlyList<string> Fields { get; }

        public static FieldConstraint ExactlyOne(params string[] f) => new FieldConstraint(ConstraintKind.ExactlyOne, f);
        public static FieldConstraint AtLeastOne(params string[] f) => new FieldConstraint(ConstraintKind.AtLeastOne, f);
        public static FieldConstraint AtMostOne(params string[] f) => new FieldConstraint(ConstraintKind.AtMostOne, f);
        public static FieldConstraint Requires(string field, params string[] needs)
        {
            var all = new string[needs.Length + 1];
            all[0] = field;
            Array.Copy(needs, 0, all, 1, needs.Length);
            return new FieldConstraint(ConstraintKind.Requires, all);
        }

        public string Describe()
        {
            switch (Kind)
            {
                case ConstraintKind.ExactlyOne: return "exactly one of " + string.Join(", ", Fields);
                case ConstraintKind.AtLeastOne: return "at least one of " + string.Join(", ", Fields);
                case ConstraintKind.AtMostOne: return "at most one of " + string.Join(", ", Fields);
                default: return Fields[0] + " requires " + string.Join(", ", Fields.Skip(1));
            }
        }

        public JsonObject ToJson()
        {
            string k = Kind == ConstraintKind.ExactlyOne ? "exactlyOne" : Kind == ConstraintKind.AtLeastOne ? "atLeastOne" : Kind == ConstraintKind.AtMostOne ? "atMostOne" : "requires";
            return new JsonObject().Add("kind", k).Add("fields", JsonArray.FromStrings(Fields));
        }
    }

    /// <summary>The declared shape of one op: its args, the outputs other ops may reference, and safety flags.</summary>
    public sealed class OpSchema
    {
        private readonly FieldSpec _args;

        public OpSchema(string name, AppKind app, string summary, IEnumerable<FieldSpec> fields,
            IEnumerable<FieldConstraint>? constraints = null, IEnumerable<FieldSpec>? outputs = null,
            bool ensure = false, bool destructive = false, bool requiresAllowScript = false)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Op name is required.", nameof(name));
            Name = name;
            App = app;
            Summary = summary ?? "";
            _args = new FieldSpec("args", FieldType.Object, "").WithFields(fields);
            if (constraints != null) foreach (var c in constraints) _args.With(c);
            foreach (var c in _args.Constraints)
                foreach (var f in c.Fields)
                    if (_args.FindField(f) is null) throw new ArgumentException("Op '" + name + "' constraint names unknown field '" + f + "'.");
            var seen = new HashSet<string>();
            foreach (var f in _args.Fields)
                if (!seen.Add(f.Name)) throw new ArgumentException("Op '" + name + "' declares field '" + f.Name + "' twice.");
            Outputs = outputs?.ToList() ?? new List<FieldSpec>();
            Ensure = ensure;
            Destructive = destructive;
            RequiresAllowScript = requiresAllowScript;
        }

        public string Name { get; }
        public AppKind App { get; }
        public string Summary { get; }
        public IReadOnlyList<FieldSpec> Fields => _args.Fields;
        public IReadOnlyList<FieldConstraint> Constraints => _args.Constraints;
        /// <summary>The args as one object spec, for validation and <see cref="OpArgs"/>.</summary>
        public FieldSpec Args => _args;
        /// <summary>Named values in the op result's <c>outputs</c>, reachable as <c>$id.name</c>.</summary>
        public IReadOnlyList<FieldSpec> Outputs { get; }
        /// <summary>An "ensure" op never changes an existing object unless args.update is true.</summary>
        public bool Ensure { get; }
        /// <summary>The op deletes or replaces elements. Engines restrict it to listed, validated ids.</summary>
        public bool Destructive { get; }
        /// <summary>Refused unless the run request has params.allowScript = true. Engines also require an identical prior rehearsal before a commit (AutoCAD: see the script.lisp description).</summary>
        public bool RequiresAllowScript { get; }

        public FieldSpec? FindField(string name) => _args.FindField(name);

        public JsonObject ToJson()
        {
            var o = new JsonObject()
                .Add("name", Name)
                .Add("app", AppKinds.ToWire(App))
                .Add("summary", Summary)
                .Add("fields", new JsonArray(Fields.Select(f => (JsonValue)f.ToJson())));
            if (Constraints.Count > 0) o.Add("constraints", new JsonArray(Constraints.Select(c => (JsonValue)c.ToJson())));
            if (Outputs.Count > 0) o.Add("outputs", new JsonArray(Outputs.Select(f => (JsonValue)f.ToJson())));
            if (Ensure) o.Add("ensure", true);
            if (Destructive) o.Add("destructive", true);
            if (RequiresAllowScript) o.Add("requiresAllowScript", true);
            return o;
        }
    }

    /// <summary>Builders for <see cref="FieldSpec"/> used by the op declarations.</summary>
    public static class Field
    {
        public static FieldSpec String(string name, string description) => new FieldSpec(name, FieldType.String, description);
        public static FieldSpec Number(string name, string description) => new FieldSpec(name, FieldType.Number, description);
        public static FieldSpec Integer(string name, string description) => new FieldSpec(name, FieldType.Integer, description);
        public static FieldSpec Length(string name, string description) => new FieldSpec(name, FieldType.Length, description);
        public static FieldSpec Angle(string name, string description) => new FieldSpec(name, FieldType.Angle, description);
        public static FieldSpec Bool(string name, string description) => new FieldSpec(name, FieldType.Bool, description);
        public static FieldSpec Enum(string name, string description, params string[] values)
        {
            if (values.Length == 0) throw new ArgumentException("An enum needs values.");
            return new FieldSpec(name, FieldType.Enum, description).WithEnum(values);
        }
        public static FieldSpec Point2(string name, string description) => new FieldSpec(name, FieldType.Point2, description);
        public static FieldSpec Point3(string name, string description) => new FieldSpec(name, FieldType.Point3, description);
        public static FieldSpec Ref(string name, string description) => new FieldSpec(name, FieldType.Ref, description);
        public static FieldSpec Any(string name, string description) => new FieldSpec(name, FieldType.Any, description);
        public static FieldSpec List(string name, string description, FieldSpec item) => new FieldSpec(name, FieldType.List, description).WithItem(item);
        public static FieldSpec Map(string name, string description, FieldSpec? value = null)
        {
            var f = new FieldSpec(name, FieldType.Map, description);
            return value is null ? f : f.WithItem(value);
        }
        public static FieldSpec Object(string name, string description, params FieldSpec[] fields) =>
            new FieldSpec(name, FieldType.Object, description).WithFields(fields);

        /// <summary>The shape of a list item or map value.</summary>
        public static FieldSpec Of(FieldType type, string description = "") => new FieldSpec("", type, description);
        public static FieldSpec OfEnum(params string[] values) => new FieldSpec("", FieldType.Enum, "").WithEnum(values);
        public static FieldSpec OfObject(string description, params FieldSpec[] fields) => new FieldSpec("", FieldType.Object, description).WithFields(fields);
        public static FieldSpec OfList(FieldSpec item, string description = "") => new FieldSpec("", FieldType.List, description).WithItem(item);
        public static FieldSpec OfMap(FieldSpec? value = null, string description = "")
        {
            var f = new FieldSpec("", FieldType.Map, description);
            return value is null ? f : f.WithItem(value);
        }
    }

    /// <summary>All op schemas known to this build, keyed by app and name (a name such as viewports.layout exists for both apps).</summary>
    public sealed class OpRegistry
    {
        private readonly Dictionary<string, OpSchema> _byKey = new Dictionary<string, OpSchema>(StringComparer.Ordinal);
        private readonly List<OpSchema> _ordered = new List<OpSchema>();

        private static string Key(AppKind app, string name) => AppKinds.ToWire(app) + ":" + name;

        public void Register(OpSchema schema)
        {
            if (schema is null) throw new ArgumentNullException(nameof(schema));
            string key = Key(schema.App, schema.Name);
            if (_byKey.ContainsKey(key)) throw new ArgumentException("Op '" + schema.Name + "' is already registered for " + AppKinds.ToWire(schema.App) + ".");
            _byKey.Add(key, schema);
            _ordered.Add(schema);
        }

        /// <summary>The op for an app, or null.</summary>
        public OpSchema? Find(string name, AppKind app) => _byKey.TryGetValue(Key(app, name), out var s) ? s : null;

        /// <summary>The first op with this name for either app, or null.</summary>
        public OpSchema? Find(string name) => _ordered.FirstOrDefault(s => s.Name == name);

        public bool TryGet(string name, AppKind app, out OpSchema schema)
        {
            var s = Find(name, app);
            schema = s!;
            return s != null;
        }

        public IReadOnlyList<OpSchema> All => _ordered;

        public IEnumerable<OpSchema> ForApp(AppKind app) => _ordered.Where(s => s.App == app);

        public JsonArray ToJson(AppKind? app = null) =>
            new JsonArray(_ordered.Where(s => app is null || s.App == app).Select(s => (JsonValue)s.ToJson()));

        /// <summary>A registry with every op for both apps.</summary>
        public static OpRegistry CreateDefault()
        {
            var r = new OpRegistry();
            foreach (var s in Ops.AutoCadOps.All()) r.Register(s);
            foreach (var s in Ops.RevitOps.All()) r.Register(s);
            foreach (var s in Ops.AutoCadOpsWave2.All()) r.Register(s);
            foreach (var s in Ops.RevitOpsModelWave2.All()) r.Register(s);
            foreach (var s in Ops.RevitOpsDocsWave2.All()) r.Register(s);
            foreach (var s in Ops.RevitOpsFamilyWave2.All()) r.Register(s);
            foreach (var s in Ops.AutoCadOpsBlocks.All()) r.Register(s);
            foreach (var s in Ops.RevitOpsCadFamilies.All()) r.Register(s);
            foreach (var s in Ops.RevitOpsFamilyCreate.All()) r.Register(s);
            return r;
        }

        private static OpRegistry? _default;

        /// <summary>A shared default registry. Treat it as read-only.</summary>
        public static OpRegistry Default => _default ??= CreateDefault();
    }
}
