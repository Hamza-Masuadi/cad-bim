using System;
using System.Collections.Generic;
using System.Linq;
using SA.Core.Json;

namespace SA.Core.Checks
{
    /// <summary>
    /// The tables and fields a set of checks reads (<see cref="CheckInputs.Needs"/>), so a collector can gather only those.
    /// A needed table still needs every record, with its id: checks count and compare records, so only fields may be
    /// left out. A check whose evaluator SA.Core does not define may read anything: it makes <see cref="Everything"/> true
    /// (it names no table) or needs every field of its table. Facts meta is always needed.
    /// </summary>
    public sealed class FactsNeeds
    {
        private readonly Dictionary<string, HashSet<string>> _fields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly HashSet<string> _allFields = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        private readonly List<string> _unknown = new List<string>();

        /// <summary>True when some check may read any table: collect everything, as without a selection.</summary>
        public bool Everything { get; private set; }

        /// <summary>Tables named by the checks, in first-seen order.</summary>
        public IReadOnlyList<string> Tables => _order;

        /// <summary>Ids of checks whose evaluator SA.Core does not define; their needs are assumed, not known.</summary>
        public IReadOnlyList<string> UnknownChecks => _unknown;

        public bool NeedsTable(string table) => Everything || _fields.ContainsKey(table);

        /// <summary>True when every field of <paramref name="table"/> is needed.</summary>
        public bool NeedsAllFields(string table) => _allFields.Contains(table) || (Everything && !_fields.ContainsKey(table));

        public bool NeedsField(string table, string field) =>
            NeedsAllFields(table) || (_fields.TryGetValue(table, out var f) && f.Contains(field));

        /// <summary>The named fields needed from <paramref name="table"/>; see <see cref="NeedsAllFields"/> first.</summary>
        public IReadOnlyCollection<string> FieldsOf(string table) =>
            _fields.TryGetValue(table, out var f) ? (IReadOnlyCollection<string>)f.OrderBy(x => x, StringComparer.Ordinal).ToList() : new string[0];

        internal void Add(string table, IEnumerable<string> fields)
        {
            Table(table).UnionWith(fields);
        }

        internal void AddUnknown(string checkId, string? table)
        {
            _unknown.Add(checkId);
            if (table is null) Everything = true;
            else { Table(table); _allFields.Add(table); }
        }

        private HashSet<string> Table(string table)
        {
            if (!_fields.TryGetValue(table, out var f))
            {
                _fields[table] = f = new HashSet<string>(StringComparer.Ordinal);
                _order.Add(table);
            }
            return f;
        }

        /// <summary><c>{ "everything": bool, "tables": { "t": ["f"...] or "*" }, "unknownChecks": [...] }</c>, for logs and reports.</summary>
        public JsonObject ToJson()
        {
            var tables = new JsonObject();
            foreach (var t in _order)
                tables.Add(t, _allFields.Contains(t) ? (JsonValue)new JsonString("*") : JsonArray.FromStrings(FieldsOf(t)));
            return new JsonObject().Add("everything", Everything).Add("tables", tables).Add("unknownChecks", JsonArray.FromStrings(_unknown));
        }
    }
}
