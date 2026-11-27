using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SA.Core.Jobs;
using SA.Core.Json;
using SA.Core.Validation;
using SA.Revit.Compat;
using SA.Revit.Ops.Support;

namespace SA.Revit.Ops
{
    /// <summary>
    /// sheet.create (ensure): a sheet with number, name, optional title block and parameters. A sheet with the same
    /// number that already matches is reported unchanged; one that differs is refused unless update is true, in
    /// which case its name, title block type and parameters are changed. Without titleBlock a new sheet gets the
    /// standard's title block (titleBlock.familyType) when the standard names one, and none otherwise. A new number
    /// that does not follow the standard's sheet numbering is warned about; a number that matches an existing sheet
    /// only when case is ignored is refused, since Revit's sheet lookup ignores case and readback does not.
    /// </summary>
    public sealed class SheetCreate : OpHandler
    {
        public override string Name => "sheet.create";

        private static readonly char[] Forbidden = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        private sealed class Plan
        {
            public string Number = "";
            public string SheetName = "";
            public FamilySymbol? TitleBlock;
            public ViewSheet? Existing;
            public FamilyInstance? ExistingTitleBlock;
            public bool Update;
            public List<string> Differences = new List<string>();
        }

        internal static FamilyInstance? TitleBlockOn(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().FirstOrDefault();

        private static Plan? Build(OpContext ctx, OpArgs args, List<Issue> issues)
        {
            var doc = ctx.Doc;
            var p = new Plan { Number = args.GetString("number").Trim(), SheetName = args.GetString("name").Trim() };
            bool ok = true;
            if (p.Number.Length == 0) { OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Invalid, "sheet number is empty"); ok = false; }
            if (p.SheetName.Length == 0) { OpContext.Error(issues, ctx.FieldPath(args, "name"), RevitIssueCodes.Invalid, "sheet name is empty"); ok = false; }
            foreach (var f in new[] { "number", "name" })
            {
                string v = f == "number" ? p.Number : p.SheetName;
                int bad = v.IndexOfAny(Forbidden);
                if (bad >= 0) { OpContext.Error(issues, ctx.FieldPath(args, f), RevitIssueCodes.Invalid, "'" + v[bad] + "' is not allowed in a sheet " + f); ok = false; }
            }
            if (args.GetStringOrNull("titleBlock") is string tb)
            {
                var s = Lookup.Symbol(doc, BuiltInCategory.OST_TitleBlocks, tb, "title block");
                if (!s.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "titleBlock"), s.Code, s.Error!); ok = false; }
                else p.TitleBlock = s.Value;
            }
            else if (ctx.Standard?.TitleBlockFamilyType is string stb && Lookup.SheetByNumber(doc, p.Number) is null)
            {
                var s = Lookup.Symbol(doc, BuiltInCategory.OST_TitleBlocks, stb, "title block");
                if (!s.Ok) { OpContext.Error(issues, ctx.FieldPath(args, "titleBlock"), s.Code, "the standard's title block '" + stb + "' is not usable: " + s.Error + "; load it or give titleBlock"); ok = false; }
                else p.TitleBlock = s.Value;
            }
            if (!ok) return null;

            p.Existing = Lookup.SheetByNumber(doc, p.Number);
            if (p.Existing != null && !string.Equals(p.Existing.SheetNumber, p.Number, StringComparison.Ordinal))
            {
                OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Mismatch,
                    "sheet '" + p.Existing.SheetNumber + "' exists (numbers differ only by case); use that exact number");
                return null;
            }
            if (p.Existing is null && ctx.Standard?.SheetNumbering != null)
            {
                var sn = ctx.Standard.ParseSheetId(p.Number);
                if (!sn.Valid) OpContext.Warning(issues, ctx.FieldPath(args, "number"), IssueCodes.NameInvalid, "sheet number does not follow the standard's sheet numbering: " + sn.Explain());
            }
            bool update = args.GetBool("update");
            var parameters = args.GetMap("parameters");
            if (p.Existing != null)
            {
                if (p.Existing.IsPlaceholder)
                {
                    OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Exists, "sheet number '" + p.Number + "' belongs to a placeholder sheet");
                    return null;
                }
                p.ExistingTitleBlock = TitleBlockOn(doc, p.Existing);
                if (!string.Equals(p.Existing.Name, p.SheetName, StringComparison.Ordinal)) p.Differences.Add("name is '" + p.Existing.Name + "'");
                if (p.TitleBlock != null && (p.ExistingTitleBlock is null || p.ExistingTitleBlock.Symbol.Id != p.TitleBlock.Id))
                    p.Differences.Add("title block is " + (p.ExistingTitleBlock is null ? "none" : "'" + Lookup.FamilyTypeName(p.ExistingTitleBlock.Symbol) + "'"));
                foreach (var kv in parameters)
                {
                    var prm = ParamAccess.Find(p.Existing, kv.Key, out string? err);
                    if (prm is null) { OpContext.Error(issues, JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key), RevitIssueCodes.NotFound, err!); ok = false; continue; }
                    if (!ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out string perr))
                    { OpContext.Error(issues, JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key), RevitIssueCodes.Invalid, perr); ok = false; continue; }
                    if (!ParamAccess.Matches(prm, pv, ctx.Units))
                    {
                        if (prm.IsReadOnly) { OpContext.Error(issues, JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key), RevitIssueCodes.ReadOnly, "parameter '" + kv.Key + "' is read-only"); ok = false; }
                        p.Differences.Add("'" + kv.Key + "' is " + ParamAccess.Read(prm, ctx.Units).ToJson());
                    }
                }
                if (p.Differences.Count > 0)
                {
                    if (update)
                    {
                        p.Update = true;
                        ctx.DeclareModifies(p.Existing.Id);
                        if (p.ExistingTitleBlock != null) ctx.DeclareModifies(p.ExistingTitleBlock.Id);
                    }
                    else
                    {
                        OpContext.Error(issues, ctx.FieldPath(args, "number"), RevitIssueCodes.Mismatch,
                            "sheet '" + p.Number + "' exists and differs (" + string.Join("; ", p.Differences) + "); set update:true to change it");
                        ok = false;
                    }
                }
            }
            else
            {
                var sample = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().FirstOrDefault(s => !s.IsPlaceholder);
                foreach (var kv in parameters)
                {
                    var known = ParamAccess.KnownFor(doc, BuiltInCategory.OST_Sheets, kv.Key, sample);
                    string at = JsonPath.Key(ctx.FieldPath(args, "parameters"), kv.Key);
                    if (known == false) { OpContext.Error(issues, at, RevitIssueCodes.NotFound, "sheets have no parameter '" + kv.Key + "'"); ok = false; }
                    else if (known is null) OpContext.Warning(issues, at, RevitIssueCodes.Unchecked, "parameter '" + kv.Key + "' could not be checked before the sheet exists");
                }
            }
            return ok ? p : null;
        }

        public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) => Build(ctx, args, issues);

        protected override void Apply(OpContext ctx, OpArgs args, OpResult result)
        {
            var issues = new List<Issue>();
            var p = Build(ctx, args, issues) ?? throw new OpFailedException("validation_failed", string.Join("; ", issues.Where(i => i.IsError).Select(i => i.Message)));
            var doc = ctx.Doc;
            ViewSheet sheet;
            FamilyInstance? tbi = null;
            if (p.Existing != null && !p.Update)
            {
                result.Unchanged.Add(ElementIds.ToWire(p.Existing.Id));
                result.Outputs.Add("sheet", ElementIds.ToWire(p.Existing.Id));
                var t = p.ExistingTitleBlock;
                result.Outputs.Add("titleBlock", t is null ? (JsonValue)JsonValue.Null : ElementIds.ToWire(t.Id));
                return;
            }
            if (p.TitleBlock != null && !p.TitleBlock.IsActive) { p.TitleBlock.Activate(); doc.Regenerate(); }
            if (p.Existing is null)
            {
                sheet = ViewSheet.Create(doc, p.TitleBlock?.Id ?? ElementId.InvalidElementId);
                sheet.SheetNumber = p.Number;
                sheet.Name = p.SheetName;
                result.Created.Add(ElementIds.ToWire(sheet.Id));
                tbi = TitleBlockOn(doc, sheet);
                if (tbi != null) result.Created.Add(ElementIds.ToWire(tbi.Id));
            }
            else
            {
                sheet = p.Existing;
                if (!string.Equals(sheet.Name, p.SheetName, StringComparison.Ordinal)) sheet.Name = p.SheetName;
                tbi = p.ExistingTitleBlock;
                if (p.TitleBlock != null)
                {
                    if (tbi != null && tbi.Symbol.Id != p.TitleBlock.Id) { tbi.ChangeTypeId(p.TitleBlock.Id); result.Modified.Add(ElementIds.ToWire(tbi.Id)); }
                    else if (tbi is null)
                    {
                        tbi = doc.Create.NewFamilyInstance(XYZ.Zero, p.TitleBlock, sheet);
                        result.Created.Add(ElementIds.ToWire(tbi.Id));
                    }
                }
                result.Modified.Insert(0, ElementIds.ToWire(sheet.Id));
            }
            foreach (var kv in args.GetMap("parameters"))
            {
                var prm = ParamAccess.Find(sheet, kv.Key, out string? err) ?? throw new OpFailedException("parameter_not_found", err ?? kv.Key);
                if (prm.IsReadOnly) throw new OpFailedException("parameter_read_only", "parameter '" + kv.Key + "' is read-only on sheets");
                if (!ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out string perr)) throw new OpFailedException("parameter_value", kv.Key + ": " + perr);
                if (!ParamAccess.Apply(prm, pv)) throw new OpFailedException("parameter_value", "Revit refused the value for '" + kv.Key + "'");
            }
            result.Outputs.Add("sheet", ElementIds.ToWire(sheet.Id));
            result.Outputs.Add("titleBlock", tbi is null ? (JsonValue)JsonValue.Null : ElementIds.ToWire(tbi.Id));
        }

        public override void Readback(OpContext ctx, OpArgs args, OpResult result)
        {
            var doc = ctx.Doc;
            var check = new ReadbackChecker(ctx.Units);
            string? idText = result.Outputs.GetStringOrNull("sheet");
            if (idText is null || !ElementIds.TryParse(idText, out var id) || !(doc.GetElement(id) is ViewSheet sheet))
            {
                check.Fail("the sheet was not found after the op");
                check.Apply(result);
                return;
            }
            var rb = result.Readback;
            rb.Add("id", idText);
            check.Text(rb, "number", sheet.SheetNumber, args.GetString("number").Trim());
            check.Text(rb, "name", sheet.Name, args.GetString("name").Trim());
            var tbi = TitleBlockOn(doc, sheet);
            string? wantTb = null;
            if (args.GetStringOrNull("titleBlock") is string tb)
            {
                var s = Lookup.Symbol(doc, BuiltInCategory.OST_TitleBlocks, tb, "title block");
                wantTb = s.Ok ? Lookup.FamilyTypeName(s.Value!) : tb;
            }
            else if (result.Created.Contains(idText) && ctx.Standard?.TitleBlockFamilyType is string stb)
            {
                var s = Lookup.Symbol(doc, BuiltInCategory.OST_TitleBlocks, stb, "title block");
                wantTb = s.Ok ? Lookup.FamilyTypeName(s.Value!) : stb;
            }
            check.Text(rb, "titleBlock", tbi is null ? null : Lookup.FamilyTypeName(tbi.Symbol), wantTb);
            var po = new JsonObject();
            foreach (var kv in args.GetMap("parameters"))
            {
                var prm = ParamAccess.Find(sheet, kv.Key, out string? err);
                if (prm is null) { check.Fail(err ?? kv.Key); continue; }
                po.Add(kv.Key, ParamAccess.Read(prm, ctx.Units));
                if (ParamAccess.TryPrepare(doc, prm, kv.Value, ctx.Units, out var pv, out _) && !ParamAccess.Matches(prm, pv, ctx.Units))
                    check.Fail("parameter '" + kv.Key + "' reads " + ParamAccess.Read(prm, ctx.Units).ToJson() + ", requested " + kv.Value.ToJson());
            }
            if (po.Count > 0) rb.Add("parameters", po);
            rb.Add("state", result.Created.Contains(idText) ? "created" : result.Modified.Contains(idText) ? "updated" : "unchanged");
            check.Apply(result);
        }
    }
}
