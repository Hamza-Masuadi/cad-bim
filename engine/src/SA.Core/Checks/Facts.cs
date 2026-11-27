using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;

namespace SA.Core.Checks
{
    /// <summary>One record of a facts table: an optional element id plus named, typed values (JSON values).</summary>
    public sealed class FactRecord
    {
        public FactRecord(string? id = null, JsonObject? fields = null)
        {
            Id = id;
            Fields = fields ?? new JsonObject();
        }

        /// <summary>AutoCAD handle or Revit element id, when the record is an element.</summary>
        public string? Id { get; }
        public JsonObject Fields { get; }

        public FactRecord Set(string name, JsonValue value)
        {
            Fields.Set(name, value);
            return this;
        }

        /// <summary>The value, or null when the field is absent. A JSON null value is returned as <see cref="JsonNull"/>.</summary>
        public JsonValue? Get(string name) => Fields[name];

        public string? GetString(string name) => Fields[name] is JsonString s ? s.Value : null;

        public double? GetNumber(string name) => Fields[name] is JsonNumber n ? n.Value : (double?)null;

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            if (Id != null) o.Add("id", Id);
            o.Add("fields", Fields);
            return o;
        }
    }

    public sealed class FactTable
    {
        public FactTable(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Table name is required.", nameof(name));
            Name = name;
        }

        public string Name { get; }
        public List<FactRecord> Records { get; } = new List<FactRecord>();

        public FactRecord Add(string? id = null)
        {
            var r = new FactRecord(id);
            Records.Add(r);
            return r;
        }
    }

    /// <summary>
    /// The neutral input to checks: named tables of records that each engine fills from its document.
    /// An engine includes every table it knows how to collect, even when empty; a missing table means
    /// "not collected" and makes a check that needs it report not_implemented.
    /// </summary>
    public sealed class Facts
    {
        private readonly Dictionary<string, FactTable> _tables = new Dictionary<string, FactTable>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();

        public Facts(string app = "")
        {
            App = app;
        }

        /// <summary>"autocad" or "revit".</summary>
        public string App { get; }
        /// <summary>Document-level values, for example "units.length".</summary>
        public JsonObject Meta { get; } = new JsonObject();

        public IEnumerable<FactTable> Tables => _order.Select(n => _tables[n]);

        public FactTable Table(string name)
        {
            if (!_tables.TryGetValue(name, out var t))
            {
                t = new FactTable(name);
                _tables.Add(name, t);
                _order.Add(name);
            }
            return t;
        }

        public bool TryGetTable(string name, out FactTable table)
        {
            if (_tables.TryGetValue(name, out var t)) { table = t; return true; }
            table = null!;
            return false;
        }

        public bool HasTable(string name) => _tables.ContainsKey(name);

        /// <summary>Meta key for cut tables and fields: <c>{ table: { what: { kept, total, reason } } }</c>.</summary>
        public const string TruncatedKey = "truncated";
        /// <summary>Meta key for fields left out of some records: <c>{ table: { field: count } }</c>.</summary>
        public const string OmittedFieldsKey = "omittedFields";
        /// <summary>The <c>what</c> of a truncation that cut whole records (any other value names a field).</summary>
        public const string RecordsPart = "records";

        /// <summary>
        /// Records in meta that <paramref name="table"/> was cut: <paramref name="what"/> is <see cref="RecordsPart"/> when
        /// records were dropped, else the name of a field left out of some records. Checks reading it report incomplete.
        /// </summary>
        public void MarkTruncated(string table, string what, long kept, long total, string? reason = null)
        {
            if (string.IsNullOrEmpty(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (string.IsNullOrEmpty(what)) throw new ArgumentException("What was cut is required.", nameof(what));
            if (!(Meta[TruncatedKey] is JsonObject all)) { all = new JsonObject(); Meta.Set(TruncatedKey, all); }
            if (!(all[table] is JsonObject t)) { t = new JsonObject(); all.Set(table, t); }
            var entry = new JsonObject().Add("kept", kept).Add("total", total);
            if (reason != null) entry.Add("reason", reason);
            t.Set(what, entry);
        }

        /// <summary>Records in meta that <paramref name="field"/> was left out of <paramref name="count"/> records of <paramref name="table"/>.</summary>
        public void MarkOmitted(string table, string field, long count = 1)
        {
            if (string.IsNullOrEmpty(table)) throw new ArgumentException("Table name is required.", nameof(table));
            if (string.IsNullOrEmpty(field)) throw new ArgumentException("Field name is required.", nameof(field));
            if (!(Meta[OmittedFieldsKey] is JsonObject all)) { all = new JsonObject(); Meta.Set(OmittedFieldsKey, all); }
            if (!(all[table] is JsonObject t)) { t = new JsonObject(); all.Set(table, t); }
            long before = t[field] is JsonNumber n && n.TryGetInt64(out long v) ? v : 0;
            t.Set(field, before + count);
        }

        /// <summary>
        /// What is missing from <paramref name="table"/>: <see cref="RecordsPart"/> when records were cut, and the names of
        /// fields cut or omitted in some records. Reads <see cref="TruncatedKey"/>, <see cref="OmittedFieldsKey"/> and the
        /// flat form <c>"&lt;table&gt;.truncated": true</c>. Empty when the table is complete as far as the engine said.
        /// </summary>
        public IReadOnlyList<string> IncompleteParts(string table)
        {
            var parts = new List<string>();
            if (Meta[table + ".truncated"] is JsonBool flat && flat.Value) parts.Add(RecordsPart);
            if (Meta[TruncatedKey] is JsonObject all && all[table] is JsonObject t)
                foreach (var kv in t)
                    if (!(kv.Value is JsonBool b && !b.Value) && !parts.Contains(kv.Key)) parts.Add(kv.Key);
            if (Meta[OmittedFieldsKey] is JsonObject om && om[table] is JsonObject o)
                foreach (var kv in o)
                    if (!(kv.Value is JsonNumber n && n.Value <= 0) && !parts.Contains(kv.Key)) parts.Add(kv.Key);
            return parts;
        }

        /// <summary>Tables with anything listed by <see cref="IncompleteParts"/>, in table order (tables not collected are skipped).</summary>
        public IEnumerable<string> IncompleteTables() => _order.Where(t => IncompleteParts(t).Count > 0);

        public JsonObject ToJson()
        {
            var tables = new JsonObject();
            foreach (var t in Tables) tables.Add(t.Name, new JsonArray(t.Records.Select(r => (JsonValue)r.ToJson())));
            return new JsonObject().Add("app", App).Add("meta", Meta).Add("tables", tables);
        }

        public static Facts Parse(JsonValue json)
        {
            var c = new JsonCursor(json);
            var f = new Facts(c.GetString("app", ""));
            var meta = c.Optional("meta");
            if (meta != null) foreach (var kv in meta.AsObject()) f.Meta.Add(kv.Key, kv.Value);
            var tables = c.Optional("tables");
            if (tables != null)
            {
                foreach (var kv in tables.Members())
                {
                    var t = f.Table(kv.Key);
                    foreach (var r in kv.Value.Items())
                        t.Records.Add(new FactRecord(r.GetStringOrNull("id"), (JsonObject?)r.Optional("fields")?.AsObject().DeepClone()));
                }
            }
            return f;
        }
    }
}
