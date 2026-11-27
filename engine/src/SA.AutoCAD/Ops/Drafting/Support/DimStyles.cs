using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Ops.Common;
using SA.Core.Json;
using SA.Core.Units;

namespace SA.AutoCAD.Ops.Drafting.Support
{
    internal sealed class DimStylePlan
    {
        public string Name = "";
        public string Path = "";
        public string BasedOn = "";
        public bool Annotative;
        public List<DimVarSetting> Settings = new List<DimVarSetting>();
        public bool Exists;
        public string State = "";
        public string Handle = "";
    }

    /// <summary>A settings map with the unit its length values are given in.</summary>
    internal sealed class DimSettingsSource
    {
        public DimSettingsSource(JsonObject settings, LengthUnit unit, string label) { Settings = settings; Unit = unit; Label = label; }
        public JsonObject Settings { get; }
        public LengthUnit Unit { get; }
        public string Label { get; }
    }

    /// <summary>Creating, comparing and updating dimension styles (dimstyle.ensure and doc.setup's applyStandard).</summary>
    internal static class DimStyles
    {
        /// <summary>The drawing's default base style: ISO-25 in metric drawings when present, else Standard, else the current style.</summary>
        internal static string DefaultBase(OpContext ctx) => Db.Read(ctx, tr =>
        {
            var db = ctx.Database;
            if (db.Measurement == MeasurementValue.Metric && !Db.SymbolId(tr, db.DimStyleTableId, "ISO-25").IsNull) return "ISO-25";
            if (!Db.SymbolId(tr, db.DimStyleTableId, "Standard").IsNull) return "Standard";
            return ((DimStyleTableRecord)tr.GetObject(db.Dimstyle, OpenMode.ForRead)).Name;
        });

        /// <summary>
        /// Builds a plan; later sources override earlier ones setting by setting (pass the standard first, the job second).
        /// Errors and warnings are returned as text so each caller can attach them to its own field.
        /// </summary>
        internal static DimStylePlan Plan(OpContext ctx, string name, string? basedOn, bool annotative, IEnumerable<DimSettingsSource> sources,
            double mmPerDrawingUnit, List<string> errors, List<string> warnings)
        {
            var p = new DimStylePlan { Name = name, Annotative = annotative, BasedOn = basedOn ?? DefaultBase(ctx) };
            if (!AcadValues.IsValidSymbolName(name, out string nameError)) errors.Add(nameError);
            if (!DraftDb.DimStyleExists(ctx, p.BasedOn))
                errors.Add("base style '" + p.BasedOn + "' is not in the drawing." + Db.Read(ctx, tr => DraftDb.Suggest(DraftDb.SymbolNames(tr, ctx.Database.DimStyleTableId), p.BasedOn)));
            var byName = new Dictionary<string, DimVarSetting>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var src in sources)
                foreach (var kv in src.Settings)
                {
                    var s = DimVars.Convert(ctx, kv.Key, kv.Value, annotative, src.Unit, mmPerDrawingUnit, warnings, out string err);
                    if (s is null) { errors.Add(src.Label + ": " + err); continue; }
                    if (!byName.ContainsKey(s.Name)) order.Add(s.Name);
                    byName[s.Name] = s;
                }
            p.Settings = order.Select(n => byName[n]).ToList();
            DimVars.PrecisionWarnings(ctx, p.Settings, warnings);
            p.Exists = Db.Read(ctx, tr => !Db.SymbolId(tr, ctx.Database.DimStyleTableId, name).IsNull);
            return p;
        }

        internal static ObjectId Create(OpContext ctx, DimStylePlan p)
        {
            var tr = ctx.Transaction;
            var db = ctx.Database;
            var baseId = Db.SymbolId(tr, db.DimStyleTableId, p.BasedOn);
            if (baseId.IsNull) throw new InvalidOperationException("Base dimension style '" + p.BasedOn + "' is missing.");
            var rec = new DimStyleTableRecord();
            DimVars.CopyAll((DimStyleTableRecord)tr.GetObject(baseId, OpenMode.ForRead), rec);
            rec.Name = p.Name;
            var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForWrite);
            var id = table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            foreach (var s in p.Settings) DimVars.Write(ctx, rec, s);
            // Annotative is an object property and needs the record in the database.
            rec.Annotative = p.Annotative ? AnnotativeStates.True : AnnotativeStates.False;
            return id;
        }

        internal static void Update(OpContext ctx, DimStylePlan p, ObjectId id)
        {
            var rec = (DimStyleTableRecord)ctx.Transaction.GetObject(id, OpenMode.ForWrite);
            foreach (var s in p.Settings) DimVars.Write(ctx, rec, s);
            rec.Annotative = p.Annotative ? AnnotativeStates.True : AnnotativeStates.False;
        }

        /// <summary>Names of the settings (and "annotative") in which an existing style differs from the plan.</summary>
        internal static List<string> Differences(OpContext ctx, Transaction tr, ObjectId id, DimStylePlan p)
        {
            var d = new List<string>();
            var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (Anno.IsAnnotative(rec) != p.Annotative) d.Add("annotative");
            foreach (var s in p.Settings)
                if (!DimVars.Holds(ctx, tr, rec, s, out string got)) d.Add(s.Name + " is " + got);
            return d;
        }

        /// <summary>Readback of one style: values read, and mismatches against the plan when it was written.</summary>
        internal static JsonObject Read(OpContext ctx, Transaction tr, ObjectId id, DimStylePlan p, List<string> mismatches)
        {
            var rec = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var values = new JsonObject();
            foreach (var s in p.Settings)
            {
                bool ok = DimVars.Holds(ctx, tr, rec, s, out string got);
                values.Set(s.Name, got);
                if (!ok && p.State != "unchanged") mismatches.Add("dimension style '" + p.Name + "': " + s.Name + " reads back as " + got);
            }
            bool anno = Anno.IsAnnotative(rec);
            if (anno != p.Annotative && p.State != "unchanged") mismatches.Add("dimension style '" + p.Name + "': annotative reads back as " + anno);
            return new JsonObject()
                .Add("name", rec.Name)
                .Add("handle", OpContext.HandleOf(id))
                .Add("state", p.State)
                .Add("annotative", anno)
                .Add("dimtxt", AcadValues.Round(rec.Dimtxt))
                .Add("dimscale", AcadValues.Round(rec.Dimscale))
                .Add("settings", values);
        }
    }
}
