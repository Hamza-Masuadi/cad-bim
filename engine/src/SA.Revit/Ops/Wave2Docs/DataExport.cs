using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SA.Core.Jobs;
using SA.Core.Jobs.Ops;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Docs;
using SA.Revit.Ops.Docs.Support;
using SA.Revit.Ops.Support;
using SA.Revit.Ops.Wave2Docs.Support;

namespace SA.Revit.Ops.Wave2Docs
{
    /// <summary>
    /// data.export: element data to a CSV or JSON file, written into the op's staging folder and published after the
    /// job (OutputSupport). Changes nothing, so no transaction is opened.
    ///
    /// <para><b>Rows.</b> Non-type elements of the categories (FilteredElementCollector, in the view when view is
    /// given), on the level (Element.LevelId), matching every where entry, ordered by category then id. More than
    /// maxRows elements is refused in Validate.</para>
    ///
    /// <para><b>Columns.</b> id, uniqueId, category, family, type, level; room gives roomNumber and roomName
    /// (FamilyInstance.get_Room(phase), else Document.GetRoomAtPoint(anchor, phase); phase defaults to the last);
    /// location gives x, y, z and, for curve elements, x2, y2, z2 (project coordinates, z above the project base
    /// point); boundary gives the room or area boundary loops (GetBoundarySegments with boundaryLocation) as JSON
    /// text [[[x, y], ...], ...]. Then one column per parameter: the instance parameter, else the type's
    /// (ParamAccess.Find). values number reads by ParamAccess.Read (job units; element ids as the element's name),
    /// display by Parameter.AsValueString. A parameter found on no element is warned about.</para>
    ///
    /// <para><b>File.</b> CSV is RFC 4180 quoting, CRLF lines, UTF-8 with a byte order mark so spreadsheets read
    /// accents; JSON is { "columns": [...], "rows": [ { column: value } ] }, UTF-8. Readback re-reads the file and
    /// checks the header (or columns) and the row count.</para>
    /// </summary>
    public sealed class DataExport : OpHandler
    {
        public override string Name => "data.export";
        public override bool ModifiesModel => false;

        private static readonly string[] Allowed = { "drawing", "date", "jobId" };

        private sealed class Plan
        {
            public List<Element> Elements = new List<Element>();
            public List<string> Include = new List<string>();
            public List<string> Parameters = new List<string>();
            public List<string> Columns = new List<string>();
            public Phase? Phase;
            public SpatialElementBoundaryLocation Boundary;
            public bool Display;
            public string Path = "";
            public bool Json;
        }

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan
            {
                Include = args.GetStringList("include"),
                Parameters = args.GetStringList("parameters"),
                Display = args.GetString("values") == "display",
                Json = args.GetString("format") == "json",
            };
            switch (args.GetString("boundaryLocation"))
            {
                case "center": p.Boundary = SpatialElementBoundaryLocation.Center; break;
                case "core_boundary": p.Boundary = SpatialElementBoundaryLocation.CoreBoundary; break;
                case "core_center": p.Boundary = SpatialElementBoundaryLocation.CoreCenter; break;
                default: p.Boundary = SpatialElementBoundaryLocation.Finish; break;
            }
            var cats = new List<Category>();
            var names = args.GetStringList("categories");
            for (int i = 0; i < names.Count; i++)
            {
                var c = DocsUtil.Category(doc, names[i], JsonPath.Index(ctx.FieldPath(args, "categories"), i), issues);
                if (c != null && cats.All(x => x.Id != c.Id)) cats.Add(c);
            }
            Level? level = null;
            if (args.GetStringOrNull("level") is string ln)
            {
                var l = Lookup.Level(doc, ln);
                if (!l.Ok) OpContext.Error(issues, ctx.FieldPath(args, "level"), l.Code, l.Error!);
                level = l.Value;
            }
            View? view = null;
            bool pending = false;
            if (args.Has("view")) view = DocsUtil.View(ctx, args.Get("view"), ctx.FieldPath(args, "view"), issues, out pending);
            if (view != null && !FilteredElementCollector.IsViewValidForElementIteration(doc, view.Id))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "view"), RevitIssueCodes.Invalid, "view '" + view.Name + "' (" + view.ViewType + ") has no elements of its own to list; give a plan, section, elevation, 3D or drafting view, or omit view");
                view = null;
            }
            if (args.GetStringOrNull("phase") is string ph)
            {
                var all = new List<Phase>();
                foreach (Phase x in doc.Phases) all.Add(x);
                var f = Lookup.ByName(all, ph, "phase");
                if (!f.Ok) OpContext.Error(issues, ctx.FieldPath(args, "phase"), f.Code, f.Error!);
                p.Phase = f.Value;
            }
            else if (doc.Phases.Size > 0) p.Phase = doc.Phases.get_Item(doc.Phases.Size - 1) as Phase;
            p.Path = FileTarget.Plan(ctx, args, issues, args.GetStringOrNull("fileName") ?? "{drawing} - data", Allowed, null, p.Json ? ".json" : ".csv", p.Json ? new string[0] : new[] { ".txt" }) ?? "";
            if (pending || DocsUtil.HasErrors(issues)) return null;

            var where = args.GetMap("where");
            foreach (var c in cats)
            {
                var col = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
                foreach (var e in col.OfCategoryId(c.Id).WhereElementIsNotElementType())
                {
                    if (level != null && e.LevelId != level.Id) continue;
                    if (where.Count > 0 && !where.All(kv => Matches(ctx, e, kv.Key, kv.Value))) continue;
                    p.Elements.Add(e);
                }
            }
            p.Elements = p.Elements.OrderBy(e => e.Category?.Name ?? "", StringComparer.Ordinal).ThenBy(e => ElementIds.ToLong(e.Id)).ToList();
            int max = args.GetInt("maxRows");
            if (p.Elements.Count > max)
                OpContext.Error(issues, ctx.FieldPath(args, "maxRows"), RevitIssueCodes.Invalid, p.Elements.Count + " elements match, more than maxRows " + max + "; narrow the filter or raise maxRows");
            if (p.Elements.Count == 0)
                OpContext.Warning(issues, ctx.FieldPath(args, "categories"), RevitIssueCodes.NotFound, "no element matches; the file will have the header only");
            for (int i = 0; i < p.Parameters.Count; i++)
                if (p.Elements.Count > 0 && !p.Elements.Any(e => Param(e, p.Parameters[i]) != null))
                    OpContext.Warning(issues, JsonPath.Index(ctx.FieldPath(args, "parameters"), i), RevitIssueCodes.NotFound, "no exported element has a parameter '" + p.Parameters[i] + "'; the column will be empty");
            foreach (var inc in p.Include)
                switch (inc)
                {
                    case "room": p.Columns.Add("roomNumber"); p.Columns.Add("roomName"); break;
                    case "location": p.Columns.AddRange(new[] { "x", "y", "z", "x2", "y2", "z2" }); break;
                    default: p.Columns.Add(inc); break;
                }
            foreach (var prm in p.Parameters)
            {
                if (p.Columns.Contains(prm)) OpContext.Error(issues, ctx.FieldPath(args, "parameters"), RevitIssueCodes.Invalid, "column '" + prm + "' would appear twice");
                p.Columns.Add(prm);
            }
            if (p.Columns.Count == 0)
                OpContext.Error(issues, ctx.FieldPath(args, "include"), RevitIssueCodes.Invalid, "no column to export; give include and/or parameters");
            return DocsUtil.HasErrors(issues) ? null : p;
        }

        private static Parameter? Param(Element e, string name)
        {
            var prm = ParamAccess.Find(e, name, out _);
            if (prm != null) return prm;
            var t = e.Document.GetElement(e.GetTypeId());
            return t is null ? null : ParamAccess.Find(t, name, out _);
        }

        private static bool Matches(OpContext ctx, Element e, string name, JsonValue want)
        {
            var prm = Param(e, name);
            if (prm is null) return false;
            if (want is JsonString s)
                return string.Equals(prm.AsValueString() ?? prm.AsString() ?? "", s.Value, StringComparison.Ordinal) || string.Equals(prm.AsString() ?? "", s.Value, StringComparison.Ordinal);
            return ParamAccess.TryPrepare(ctx.Doc, prm, want, ctx.Units, out var pv, out _) && ParamAccess.Matches(prm, pv, ctx.Units);
        }

        private static JsonValue Value(OpContext ctx, Plan p, Parameter? prm)
        {
            if (prm is null) return JsonValue.Null;
            if (p.Display) return prm.AsValueString() ?? prm.AsString() ?? "";
            if (prm.StorageType == StorageType.ElementId)
            {
                var id = prm.AsElementId();
                if (!ElementIds.IsValid(id)) return JsonValue.Null;
                return ctx.Doc.GetElement(id)?.Name ?? (JsonValue)ElementIds.ToWire(id);
            }
            return ParamAccess.Read(prm, ctx.Units);
        }

        private static JsonObject Row(OpContext ctx, Plan p, Element e)
        {
            var doc = ctx.Doc;
            var row = new JsonObject();
            var type = doc.GetElement(e.GetTypeId()) as ElementType;
            double baseZ = DocsUtil.BaseZ(doc);
            foreach (var inc in p.Include)
                switch (inc)
                {
                    case "id": row.Set("id", ElementIds.ToWire(e.Id)); break;
                    case "uniqueId": row.Set("uniqueId", e.UniqueId); break;
                    case "category": row.Set("category", e.Category?.Name ?? ""); break;
                    case "family": row.Set("family", type?.FamilyName ?? ""); break;
                    case "type": row.Set("type", type?.Name ?? ""); break;
                    case "level": row.Set("level", Lookup.LevelName(doc, e.LevelId)); break;
                    case "room":
                    {
                        Room? r = e as Room;
                        if (r is null && p.Phase != null)
                        {
                            r = (e as FamilyInstance)?.get_Room(p.Phase);
                            if (r is null && TagAll.Anchor(e, null!) is XYZ at) r = doc.GetRoomAtPoint(at, p.Phase);
                        }
                        row.Set("roomNumber", r?.Number ?? (JsonValue)JsonValue.Null);
                        row.Set("roomName", r?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? (JsonValue)JsonValue.Null);
                        break;
                    }
                    case "location":
                    {
                        XYZ? a = null, b = null;
                        if (e.Location is LocationPoint lp) a = lp.Point;
                        else if (e.Location is LocationCurve lc && lc.Curve != null) { a = lc.Curve.GetEndPoint(0); b = lc.Curve.GetEndPoint(1); }
                        foreach (var kv in new[] { new { P = a, S = "" }, new { P = b, S = "2" } })
                        {
                            row.Set("x" + kv.S, kv.P is null ? JsonValue.Null : ctx.Units.LengthJson(kv.P.X));
                            row.Set("y" + kv.S, kv.P is null ? JsonValue.Null : ctx.Units.LengthJson(kv.P.Y));
                            row.Set("z" + kv.S, kv.P is null ? JsonValue.Null : ctx.Units.LengthJson(kv.P.Z - baseZ));
                        }
                        break;
                    }
                    case "boundary":
                    {
                        var loops = new JsonArray();
                        if (e is SpatialElement se)
                            foreach (var segs in se.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = p.Boundary }) ?? new List<IList<BoundarySegment>>())
                            {
                                var loop = new JsonArray();
                                foreach (var s in segs)
                                {
                                    var t = s.GetCurve().Tessellate();
                                    for (int i = 0; i < t.Count - 1; i++) loop.Add(ctx.Units.PointJson(t[i]));
                                }
                                loops.Add(loop);
                            }
                        row.Set("boundary", p.Json ? (JsonValue)loops : loops.ToJson());
                        break;
                    }
                }
            foreach (var name in p.Parameters) row.Set(name, Value(ctx, p, Param(e, name)));
            return row;
        }

        private static string Cell(JsonValue v)
        {
            switch (v)
            {
                case JsonNull _: return "";
                case JsonString s: return s.Value;
                case JsonBool b: return b.Value ? "true" : "false";
                case JsonNumber n: return Delimited.Number(n.Value);
                default: return v.ToJson();
            }
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw DocsUtil.Invalid(issues);
            var rows = p.Elements.Select(e => Row(ctx, p, e)).ToList();
            byte[] bytes;
            if (p.Json)
            {
                var doc = new JsonObject().Add("columns", JsonArray.FromStrings(p.Columns)).Add("rows", new JsonArray(rows.Cast<JsonValue>()));
                bytes = new UTF8Encoding(false).GetBytes(doc.ToJson(true));
            }
            else
            {
                char sep = Delimited.Separator(args.GetString("delimiter"));
                var lines = new List<IEnumerable<string>> { p.Columns };
                lines.AddRange(rows.Select(r => p.Columns.Select(c => Cell(r[c] ?? JsonValue.Null))));
                bytes = new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(Delimited.Write(lines, sep))).ToArray();
            }
            FileTarget.Write(ctx, p.Path, bytes, args.GetBool("overwrite"));
            FileTarget.Artifact(ctx, p.Json ? "json" : "csv", p.Path, null);
            result.Outputs.Set("file", p.Path);
            result.Outputs.Set("rows", rows.Count);
            result.Outputs.Set("columns", JsonArray.FromStrings(p.Columns));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var check = new ReadbackChecker(ctx.Units);
            string path = result.Outputs.GetStringOrNull("file") ?? "";
            long want = (result.Outputs["rows"] as JsonNumber)?.TryGetInt64(out long w) == true ? w : -1;
            var columns = (result.Outputs["columns"] as JsonArray ?? new JsonArray()).Select(x => x.AsString()).ToList();
            var bytes = FileTarget.ReadBack(ctx, result, path, check);
            if (bytes != null)
            {
                string text = Compat.Wave2DocsCompat.Decode(bytes, out string enc);
                result.Readback.Set("encoding", enc);
                List<string> header;
                int rows;
                if (args.GetString("format") == "json")
                {
                    var o = JsonValue.Parse(text) as JsonObject;
                    header = (o?["columns"] as JsonArray ?? new JsonArray()).Select(x => x.AsString()).ToList();
                    rows = (o?["rows"] as JsonArray)?.Count ?? -1;
                }
                else
                {
                    var recs = Delimited.Parse(text, Delimited.Separator(args.GetString("delimiter")));
                    header = recs.Count > 0 ? recs[0] : new List<string>();
                    rows = recs.Count - 1;
                }
                result.Readback.Set("rows", rows);
                if (rows != want) check.Fail("file " + path + " has " + rows + " data row(s), " + want + " were written");
                if (!header.SequenceEqual(columns, StringComparer.Ordinal)) check.Fail("file " + path + " header is [" + string.Join(", ", header) + "], expected [" + string.Join(", ", columns) + "]");
            }
            check.Apply(result);
        }
    }
}
