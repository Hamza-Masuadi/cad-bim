using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.Core.Checks;
using SA.Core.Json;

namespace SA.AutoCAD.Audit
{
    /// <summary>What a fact collector may use. Read-only: the transaction is a read transaction or the job's own.</summary>
    internal sealed class FactContext
    {
        internal FactContext(Document document, Transaction transaction, Facts facts, FactsNeeds? needs, int maxRecords, ISet<string>? keepNames)
        {
            Document = document;
            Database = document.Database;
            Transaction = transaction;
            Facts = facts;
            Needs = needs;
            MaxRecords = maxRecords;
            KeepNames = keepNames;
        }

        /// <summary>Names the standard relies on (ProjectStandard.NamesToKeep); fix.purge never removes them, so they are not purgeable.</summary>
        public ISet<string>? KeepNames { get; }

        public Document Document { get; }
        public Database Database { get; }
        public Transaction Transaction { get; }
        public Facts Facts { get; }

        /// <summary>What the chosen checks read; null collects every table and field.</summary>
        public FactsNeeds? Needs { get; }

        /// <summary>Records per table before the collector stops and marks the table truncated in Facts.Meta.</summary>
        public int MaxRecords { get; }

        /// <summary>Handles of the entities the job created or modified, in a job's verify step; null in an audit.</summary>
        public ISet<string>? JobScope { get; set; }

        /// <summary>Marks the table cut at <see cref="MaxRecords"/> (both meta forms Core reads).</summary>
        public void MarkTruncated(string table, int kept, int total)
        {
            Facts.Meta.Set(table + ".truncated", true).Set(table + ".total", total);
            Facts.MarkTruncated(table, Facts.RecordsPart, kept, total, "safety ceiling of " + MaxRecords + " records");
        }

        /// <summary>The entity fields to keep, or null for all of them.</summary>
        internal Func<string, bool>? EntityFields()
        {
            var needs = Needs;
            if (needs is null || needs.NeedsAllFields("entities")) return null;
            return f => needs.NeedsField("entities", f);
        }

        private Collectors.DrawingScan? _scan;
        private Exception? _scanError;

        /// <summary>
        /// The shared read-only pass over the drawing, run on first use. If it failed, every later use throws again, so each
        /// table that depends on it is left out rather than collected from partial counts.
        /// </summary>
        internal Collectors.DrawingScan Scan
        {
            get
            {
                if (_scanError != null) throw new InvalidOperationException("The drawing scan failed: " + _scanError.Message, _scanError);
                if (_scan is null)
                {
                    bool records = Needs is null || Needs.NeedsTable("entities");
                    try { _scan = Collectors.DrawingScan.Run(Database, Transaction, records ? MaxRecords : 0, records ? EntityFields() : null); }
                    catch (Exception e) { _scanError = e; throw; }
                }
                return _scan;
            }
        }
    }

    /// <summary>
    /// Fills one Facts table from the drawing. Rules: one record per element with the element's handle as the record id;
    /// field names are camelCase and stable (checks in data files refer to them); values are JSON scalars; lengths are in
    /// drawing units (Facts.Meta["units.insUnits"] says which); never write to the drawing.
    /// </summary>
    internal interface IFactCollector
    {
        /// <summary>The facts table this collector fills, e.g. "layers".</summary>
        string Table { get; }

        void Collect(FactContext ctx, FactTable table);
    }

    /// <summary>Discovers every IFactCollector in this assembly (parameterless constructor) and runs them.</summary>
    internal static class FactCollectors
    {
        /// <summary>Entity records kept when every field is collected (about 2.3 KB each, so about 0.6 GB).</summary>
        internal const int FullRecordCeiling = 250000;

        /// <summary>
        /// Entity records kept when only the checks' fields are collected: a safety ceiling, not a working limit. A lean
        /// record (handle, the four location fields and a few check fields) takes about 1 KB of managed memory, so the
        /// ceiling holds the table near 1 GB; real drawings carry well under a million entities directly in model and
        /// paper space.
        /// </summary>
        internal const int LeanRecordCeiling = 1000000;

        /// <summary>The ceiling for <paramref name="needs"/>: the lean one unless every entity field is needed.</summary>
        internal static int CeilingFor(FactsNeeds? needs) =>
            needs != null && !needs.NeedsAllFields("entities") ? LeanRecordCeiling : FullRecordCeiling;

        private static List<IFactCollector>? _all;

        internal static IReadOnlyList<IFactCollector> All => _all ??= Discover();

        private static List<IFactCollector> Discover()
        {
            var list = new List<IFactCollector>();
            Type[] types;
            try { types = typeof(FactCollectors).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface || !typeof(IFactCollector).IsAssignableFrom(t)) continue;
                try
                {
                    var c = (IFactCollector)Activator.CreateInstance(t, true)!;
                    if (list.Any(x => x.Table == c.Table)) { Log.Warn("Two fact collectors fill '" + c.Table + "'; " + t.FullName + " skipped."); continue; }
                    list.Add(c);
                }
                catch (Exception e) { Log.Error("Fact collector " + t.FullName + " could not be created.", e); }
            }
            return list.OrderBy(c => c.Table, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Runs every collector, or with <paramref name="needs"/> only those of the tables the checks read (entity records
        /// keep only the fields they read, plus type, space, layout and layer for finding locations). A collector that
        /// throws leaves its table out (so its checks report not_implemented) and records the error in Meta["errors"],
        /// instead of reporting an empty table that could pass checks.
        /// </summary>
        internal static Facts Collect(Document document, Transaction tr, FactsNeeds? needs = null, ISet<string>? keepNames = null, ISet<string>? jobScope = null)
        {
            if (needs != null && needs.Everything) needs = null;
            var facts = new Facts("autocad");
            var db = document.Database;
            facts.Meta.Add("document.path", document.Name ?? "");
            var units = DrawingUnits.Describe(db);
            facts.Meta.Add("units.insUnits", units["insUnits"]!).Add("units.name", units["name"]!).Add("units.mmPerUnit", units["mmPerUnit"]!);
            // FACTS.md section 0: units.length is mm, cm, m, in or ft; unitless drawings fall back on MEASUREMENT and say so.
            string? lengthUnit = Collectors.FactValues.MetaLengthUnit(db, out bool assumed);
            if (lengthUnit != null) facts.Meta.Set("units.length", lengthUnit);
            if (assumed) facts.Meta.Set("units.assumed", true);
            var errors = new JsonArray();
            var ctx = new FactContext(document, tr, facts, needs, CeilingFor(needs), keepNames) { JobScope = jobScope };
            if (jobScope != null) facts.Meta.Set(LayoutEvaluators.JobScopeKey, true);
            if (needs != null) Log.Info("Collecting facts for the chosen checks: " + needs.ToJson().ToString());
            foreach (var c in All)
            {
                if (needs != null && !needs.NeedsTable(c.Table)) continue;
                var table = new FactTable(c.Table);
                try
                {
                    c.Collect(ctx, table);
                    var t = facts.Table(c.Table);
                    t.Records.AddRange(table.Records);
                }
                catch (Exception e)
                {
                    Log.Error("Fact collector '" + c.Table + "' failed.", e);
                    errors.Add(new JsonObject().Add("table", c.Table).Add("error", e.GetType().Name + ": " + e.Message));
                }
            }
            if (errors.Count > 0) facts.Meta.Add("errors", errors);
            return facts;
        }
    }
}
