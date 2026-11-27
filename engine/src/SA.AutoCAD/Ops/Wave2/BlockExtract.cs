using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using SA.AutoCAD.Host;
using SA.AutoCAD.Ops.Common;
using SA.AutoCAD.Ops.Drafting.Support;
using SA.AutoCAD.Ops.Sheets.Support;
using SA.AutoCAD.Ops.Wave2.Support;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;

namespace SA.AutoCAD.Ops.Wave2
{
    /// <summary>
    /// block.extract: lists block references in model and/or paper space (xref references and anonymous blocks
    /// excluded; dynamic references by their effective name). Attribute values include constant attributes; dynamic
    /// property values are in job units and degrees. References nested in other block definitions are counted only.
    /// The drawing is not changed; the optional file goes to the job's output folder (a temporary folder in a rehearsal).
    /// </summary>
    internal sealed class BlockExtract : OpHandler<BlockExtract.Plan>
    {
        public override string Name => "block.extract";

        internal sealed class Record
        {
            public string Handle = "", Block = "", Layer = "", Space = "";
            public JsonArray Position = new JsonArray();
            public double X, Y, Rotation, Sx, Sy, Sz;
            public Dictionary<string, string> Attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string, JsonValue>> Dynamic = new List<KeyValuePair<string, JsonValue>>();
            public Room? Room;
        }

        internal sealed class Plan
        {
            public List<Record> Records = new List<Record>();
            public List<string> Tags = new List<string>();
            public List<string> DynNames = new List<string>();
            public int Nested;
            public OutputFile? File;
            public bool HasRooms;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        private static Plan Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var p = new Plan();
            var blockNames = args.GetStringList("blocks");
            foreach (var b in blockNames.Where(n => !SheetDb.HasWildcard(n)))
                if (!ctx.IsRef(args, "blocks") && !DraftDb.BlockExists(ctx, b))
                    OpContext.Error(issues, args, "blocks", "block_missing", DraftDb.MissingBlock(ctx, b));
            var blocks = blockNames.Select(SheetDb.Wildcard).ToList();
            var layers = Targets.Patterns(args, "layers");
            var wanted = args.GetStringList("attributes");
            bool dyn = args.GetBool("dynamicProperties");
            var rooms = RoomFinder.Plan(ctx, args, issues);
            p.HasRooms = rooms != null;
            p.File = OutputFile.Plan(ctx, args, issues);
            string space = args.GetString("space");

            Db.Read(ctx, tr =>
            {
                foreach (var s in SheetDb.Spaces(tr, ctx.Database, space, false, false))
                {
                    var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || id.ObjectClass.DxfName != "INSERT") continue;
                        if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br) || !Include(tr, br, blocks, layers, out string name)) continue;
                        var r = new Record
                        {
                            Handle = OpContext.HandleOf(id), Block = name, Layer = br.Layer, Space = s.Name,
                            Rotation = AcadValues.Round(Num.Deg(br.Rotation)),
                            Sx = AcadValues.Round(br.ScaleFactors.X), Sy = AcadValues.Round(br.ScaleFactors.Y), Sz = AcadValues.Round(br.ScaleFactors.Z),
                            Attributes = BlockSupport.Read(tr, br),
                        };
                        var at = ctx.Units.ToJob(br.Position);
                        r.X = DrawingUnits.Round(at.X); r.Y = DrawingUnits.Round(at.Y);
                        r.Position = DrawingUnits.PointJson(at);
                        if (dyn)
                            foreach (var dp in DynProps.Properties(br))
                            {
                                if (dp.PropertyName == "Origin" || r.Dynamic.Any(kv => kv.Key == dp.PropertyName)) continue;
                                r.Dynamic.Add(new KeyValuePair<string, JsonValue>(dp.PropertyName, DynProps.ToJson(ctx, dp, br)));
                            }
                        if (rooms != null && s.Kind == "model") r.Room = rooms.At(at);
                        p.Records.Add(r);
                    }
                }
                foreach (var s in SheetDb.Spaces(tr, ctx.Database, "", true, false).Where(x => x.IsBlock))
                {
                    var btr = (BlockTableRecord)tr.GetObject(s.Id, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                        if (!id.IsErased && id.ObjectClass.DxfName == "INSERT" && tr.GetObject(id, OpenMode.ForRead) is BlockReference br && Include(tr, br, blocks, layers, out _)) p.Nested++;
                }
                return 0;
            });

            p.Tags = wanted.Count > 0 ? wanted : p.Records.SelectMany(r => r.Attributes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            p.DynNames = p.Records.SelectMany(r => r.Dynamic.Select(d => d.Key)).Distinct(StringComparer.Ordinal).ToList();
            if (ctx.IsValidating)
            {
                if (wanted.Count > 0)
                {
                    var found = new HashSet<string>(p.Records.SelectMany(r => r.Attributes.Keys), StringComparer.OrdinalIgnoreCase);
                    var missing = wanted.Where(t => !found.Contains(t)).ToList();
                    if (missing.Count > 0 && p.Records.Count > 0)
                        OpContext.Warning(issues, args, "attributes", "attributes_missing", "no listed reference has " + string.Join(", ", missing) + "; those columns stay empty");
                }
                if (p.Records.Count == 0)
                    OpContext.Warning(issues, args, "blocks", "nothing_found", "no block references match" + (p.Nested > 0 ? " (" + p.Nested + " are nested inside other blocks and are not listed)" : ""));
            }
            return p;
        }

        private static bool Include(Transaction tr, BlockReference br, List<Regex> blocks, List<Regex> layers, out string name)
        {
            name = "";
            var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (def.IsFromExternalReference || def.IsLayout) return false;
            name = SheetDb.EffectiveName(tr, br);
            if (name.StartsWith("*", StringComparison.Ordinal)) return false;
            if (blocks.Count > 0 && !Targets.AnyMatch(blocks, name)) return false;
            if (layers.Count > 0 && !Targets.AnyMatch(layers, br.Layer)) return false;
            return true;
        }

        private static JsonObject ToJson(Record r, Plan p)
        {
            var attrs = new JsonObject();
            foreach (var t in p.Tags) attrs.Add(t, r.Attributes.TryGetValue(t, out var v) ? (JsonValue)v : JsonValue.Null);
            var dyn = new JsonObject();
            foreach (var kv in r.Dynamic) dyn.Add(kv.Key, kv.Value);
            return new JsonObject().Add("handle", r.Handle).Add("block", r.Block).Add("layer", r.Layer).Add("space", r.Space)
                .Add("position", r.Position).Add("rotation", r.Rotation).Add("scale", new JsonArray(r.Sx, r.Sy, r.Sz))
                .Add("attributes", attrs).Add("dynamicProperties", dyn).Add("room", RoomFinder.Json(r.Room));
        }

        private static string Text(JsonValue v) =>
            v is JsonString s ? s.Value : v is JsonNumber n ? n.Value.ToString("R", CultureInfo.InvariantCulture) : v.IsNull ? "" : v.ToJson();

        protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues);
            ThrowIfErrors(Name, issues);
            int max = args.GetInt("maxInline");
            var records = new JsonArray(p.Records.Take(max).Select(r => (JsonValue)ToJson(r, p)));
            if (p.File != null)
            {
                if (p.File.IsJson) p.File.WriteJson(ctx, new JsonObject().Add("count", p.Records.Count).Add("records", new JsonArray(p.Records.Select(r => (JsonValue)ToJson(r, p)))));
                else
                {
                    var headers = new List<string> { "handle", "block", "layer", "space", "x", "y", "rotation", "scaleX", "scaleY", "scaleZ" };
                    headers.AddRange(p.Tags);
                    headers.AddRange(p.DynNames);
                    if (p.HasRooms) headers.AddRange(new[] { "roomNumber", "roomName" });
                    var rows = p.Records.Select(r =>
                    {
                        var row = new List<string> { r.Handle, r.Block, r.Layer, r.Space, F(r.X), F(r.Y), F(r.Rotation), F(r.Sx), F(r.Sy), F(r.Sz) };
                        row.AddRange(p.Tags.Select(t => r.Attributes.TryGetValue(t, out var v) ? v : ""));
                        row.AddRange(p.DynNames.Select(n => r.Dynamic.Where(d => d.Key == n).Select(d => Text(d.Value)).FirstOrDefault() ?? ""));
                        if (p.HasRooms) row.AddRange(new[] { r.Room?.Number ?? "", r.Room?.Name ?? "" });
                        return (IReadOnlyList<string>)row;
                    });
                    p.File.WriteCsv(ctx, headers, rows);
                }
            }
            result.Outputs.Add("count", p.Records.Count).Add("records", records).Add("truncated", p.Records.Count > max)
                .Add("path", p.File != null ? (JsonValue)p.File.Path : JsonValue.Null);
            return p;
        }

        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan p)
        {
            try
            {
                var mismatches = new List<string>();
                var tr = ctx.Transaction;
                int gone = p.Records.Count(r => { var id = ctx.IdOf(r.Handle); return id.IsNull || !(tr.GetObject(id, OpenMode.ForRead) is BlockReference); });
                if (gone > 0) mismatches.Add(gone + " listed references are no longer in the drawing");
                result.Readback.Add("references", p.Records.Count).Add("nestedSkipped", p.Nested).Add("columns", JsonArray.FromStrings(p.Tags.Concat(p.DynNames)));
                p.File?.Verify(result.Readback, mismatches, p.Records.Count + 1);
                foreach (var m in mismatches) Mismatch(result, m);
                return mismatches.Count == 0;
            }
            finally { p.File?.Cleanup(ctx); }
        }
    }
}
