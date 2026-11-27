using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Geometry;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.Core.Jobs
{
    public sealed class OpArgsException : Exception
    {
        public OpArgsException(string path, string message) : base(path + ": " + message)
        {
            Path = path;
        }

        public string Path { get; }
    }

    /// <summary>
    /// Typed reading of an op's args after validation and reference resolution. Absent fields fall back to the schema
    /// default. Lengths come back as numbers in the job's length unit, angles in degrees; engines convert to document
    /// units at the boundary.
    /// </summary>
    public sealed class OpArgs
    {
        private readonly FieldSpec _spec;

        public OpArgs(FieldSpec objectSpec, JsonObject args, LengthUnit unit, string path = "$.args")
        {
            _spec = objectSpec ?? throw new ArgumentNullException(nameof(objectSpec));
            Raw = args ?? throw new ArgumentNullException(nameof(args));
            Unit = unit;
            Path = path;
        }

        public static OpArgs For(OpSchema schema, JsonObject resolvedArgs, LengthUnit unit, string path = "$.args") =>
            new OpArgs(schema.Args, resolvedArgs, unit, path);

        public JsonObject Raw { get; }
        public LengthUnit Unit { get; }
        public string Path { get; }

        private FieldSpec Spec(string name) =>
            _spec.FindField(name) ?? throw new ArgumentException("The schema has no field '" + name + "'.", nameof(name));

        private string P(string name) => JsonPath.Key(Path, name);

        /// <summary>True when the job gave the field (a non-null value); defaults do not count.</summary>
        public bool Has(string name)
        {
            Spec(name);
            var v = Raw[name];
            return v != null && !v.IsNull;
        }

        /// <summary>
        /// True when the field's raw value is, or contains, an unresolved reference string (<c>"$w1"</c>, <c>"$w1.walls"</c>)
        /// or a preflight pending marker (<see cref="PendingRefs"/>). Use it to skip document checks until the op runs.
        /// The field need not be in the schema.
        /// </summary>
        public bool IsRef(string name)
        {
            var v = Raw[name];
            return v != null && (Refs.Find(v, "$").Count > 0 || PendingRefs.IsPending(v));
        }

        /// <summary>True when the field holds a preflight pending marker (see <see cref="PendingRefs.Mark"/>).</summary>
        public bool IsPending(string name) => PendingRefs.IsPending(Raw[name]);

        /// <summary>The given value, else the schema default, else null.</summary>
        public JsonValue? Get(string name)
        {
            var f = Spec(name);
            var v = Raw[name];
            if (v != null && !v.IsNull) return v;
            return f.Default;
        }

        private JsonValue Need(string name) => Get(name) ?? throw new OpArgsException(P(name), "is required");

        public string GetString(string name) => AsString(Need(name), P(name));
        public string? GetStringOrNull(string name) { var v = Get(name); return v is null ? null : AsString(v, P(name)); }

        public double GetNumber(string name) => AsNumber(Need(name), P(name));
        public double? GetNumberOrNull(string name) { var v = Get(name); return v is null ? (double?)null : AsNumber(v, P(name)); }

        public int GetInt(string name)
        {
            var v = Need(name);
            if (v is JsonNumber n && n.TryGetInt64(out long l) && l >= int.MinValue && l <= int.MaxValue) return (int)l;
            throw new OpArgsException(P(name), "expected an integer");
        }

        public bool GetBool(string name)
        {
            var v = Get(name);
            if (v is null) return false;
            if (v is JsonBool b) return b.Value;
            throw new OpArgsException(P(name), "expected true or false");
        }

        public double GetLength(string name) => ToLength(Need(name), Unit, P(name));
        public double? GetLengthOrNull(string name) { var v = Get(name); return v is null ? (double?)null : ToLength(v, Unit, P(name)); }

        public double GetAngle(string name) => ToAngle(Need(name), P(name));
        public double? GetAngleOrNull(string name) { var v = Get(name); return v is null ? (double?)null : ToAngle(v, P(name)); }

        public Point2 GetPoint2(string name) => ToPoint2(Need(name), Unit, P(name));
        public Point2? GetPoint2OrNull(string name) { var v = Get(name); return v is null ? (Point2?)null : ToPoint2(v, Unit, P(name)); }
        public Point3 GetPoint3(string name) => ToPoint3(Need(name), Unit, P(name));
        public Point3? GetPoint3OrNull(string name) { var v = Get(name); return v is null ? (Point3?)null : ToPoint3(v, Unit, P(name)); }

        public List<Point2> GetPoint2List(string name)
        {
            var v = Get(name);
            var list = new List<Point2>();
            if (v is null) return list;
            var a = v as JsonArray ?? throw new OpArgsException(P(name), "expected a list of points");
            for (int i = 0; i < a.Count; i++) list.Add(ToPoint2(a[i], Unit, JsonPath.Index(P(name), i)));
            return list;
        }

        public List<string> GetStringList(string name)
        {
            var v = Get(name);
            var list = new List<string>();
            if (v is null) return list;
            var a = v as JsonArray ?? throw new OpArgsException(P(name), "expected a list of strings");
            for (int i = 0; i < a.Count; i++) list.Add(AsString(a[i], JsonPath.Index(P(name), i)));
            return list;
        }

        public List<double> GetLengthList(string name)
        {
            var v = Get(name);
            var list = new List<double>();
            if (v is null) return list;
            var a = v as JsonArray ?? throw new OpArgsException(P(name), "expected a list of lengths");
            for (int i = 0; i < a.Count; i++) list.Add(ToLength(a[i], Unit, JsonPath.Index(P(name), i)));
            return list;
        }

        /// <summary>A nested object field, read with its own field specs. Null when absent and without default.</summary>
        public OpArgs? GetObject(string name)
        {
            var f = Spec(name);
            var v = Get(name);
            if (v is null) return null;
            var o = v as JsonObject ?? throw new OpArgsException(P(name), "expected an object");
            return new OpArgs(f, o, Unit, P(name));
        }

        /// <summary>A list of objects, each read with the list's item spec.</summary>
        public List<OpArgs> GetObjectList(string name)
        {
            var f = Spec(name);
            var v = Get(name);
            var list = new List<OpArgs>();
            if (v is null) return list;
            var a = v as JsonArray ?? throw new OpArgsException(P(name), "expected a list");
            var item = f.Item ?? throw new OpArgsException(P(name), "the schema gives no item shape");
            for (int i = 0; i < a.Count; i++)
            {
                var o = a[i] as JsonObject ?? throw new OpArgsException(JsonPath.Index(P(name), i), "expected an object");
                list.Add(new OpArgs(item, o, Unit, JsonPath.Index(P(name), i)));
            }
            return list;
        }

        public List<KeyValuePair<string, JsonValue>> GetMap(string name)
        {
            var v = Get(name);
            if (v is null) return new List<KeyValuePair<string, JsonValue>>();
            var o = v as JsonObject ?? throw new OpArgsException(P(name), "expected an object");
            return o.ToList();
        }

        /// <summary>The element id of a ref field after resolution (see <see cref="Refs.ElementId"/>).</summary>
        public string? GetRefId(string name) => Refs.ElementId(Get(name));

        /// <summary>All element ids of a ref field or a list of refs after resolution.</summary>
        public List<string> GetRefIds(string name) => Refs.ElementIds(Get(name));

        private static string AsString(JsonValue v, string path) =>
            v is JsonString s ? s.Value : throw new OpArgsException(path, "expected a string");

        private static double AsNumber(JsonValue v, string path) =>
            v is JsonNumber n ? n.Value : throw new OpArgsException(path, "expected a number");

        /// <summary>A length value (number in <paramref name="unit"/>, or a string with or without a unit) in <paramref name="unit"/>.</summary>
        public static double ToLength(JsonValue v, LengthUnit unit, string path = "$")
        {
            if (v is JsonNumber n) return n.Value;
            if (v is JsonString s)
            {
                if (Length.TryParse(s.Value, unit, out double d, out string err)) return d;
                throw new OpArgsException(path, err);
            }
            throw new OpArgsException(path, "expected a length");
        }

        public static double ToAngle(JsonValue v, string path = "$")
        {
            if (v is JsonNumber n) return n.Value;
            if (v is JsonString s && Angle.TryParseDegrees(s.Value, out double d)) return d;
            throw new OpArgsException(path, "expected an angle in degrees");
        }

        public static Point2 ToPoint2(JsonValue v, LengthUnit unit, string path = "$")
        {
            if (v is JsonArray a && a.Count == 2)
                return new Point2(ToLength(a[0], unit, JsonPath.Index(path, 0)), ToLength(a[1], unit, JsonPath.Index(path, 1)));
            throw new OpArgsException(path, "expected [x, y]");
        }

        public static Point3 ToPoint3(JsonValue v, LengthUnit unit, string path = "$")
        {
            if (v is JsonArray a && a.Count == 3)
                return new Point3(ToLength(a[0], unit, JsonPath.Index(path, 0)), ToLength(a[1], unit, JsonPath.Index(path, 1)), ToLength(a[2], unit, JsonPath.Index(path, 2)));
            throw new OpArgsException(path, "expected [x, y, z]");
        }
    }
}
