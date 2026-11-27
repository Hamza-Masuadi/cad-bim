# Writing AutoCAD op handlers

For whoever adds the remaining handlers to `SA.AutoCAD`. Read `ARCHITECTURE.md` (the contract) first. The five
reference handlers are `Ops/LayerEnsure.cs`, `Ops/TextStyleEnsure.cs`, `Ops/WallDraw.cs`, `Ops/TextAdd.cs` and
`Ops/ScriptLisp.cs`; copy their shape.

Nothing here has been run in AutoCAD. The bar is: compiles for 2024-2027 with 0 warnings, and is correct by
construction.

## 1. Where things are

| Folder | What |
|---|---|
| `Host/` | Entry point, pipe server, main-thread queue, job runner, ledger, document identity, units, system variable scope. You should not need to change it. |
| `Compat/` | The only place for `#if Y20xx`. If an API differs between releases, add a small static helper here. |
| `Ops/` | One class per op. `Ops/Common/` holds shared helpers (`Db`, `AcadValues`, `LinetypeSupport`, `AnnotationScales`). |
| `Audit/` | Fact collectors (`IFactCollector`), check catalog loading, the audit service. |
| `Snapshot/`, `Capture/` | The snapshot and capture methods. |

## 2. The handler base class

```csharp
internal sealed class DimLinear : OpHandler<DimLinear.Plan>
{
    public override string Name => "dim.linear";          // exactly the schema name in SA.Core AutoCadOps

    internal sealed class Plan { /* what you intend to create, in job units */ }

    public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) { /* no changes */ }
    protected override Plan Apply(OpContext ctx, OpArgs args, OpResult result) { /* make the change */ }
    protected override bool Readback(OpContext ctx, OpArgs args, OpResult result, Plan plan) { /* re-read */ }
}
```

- The class is found by reflection: any non-abstract `IOpHandler` in the assembly with a parameterless constructor.
  Nothing to register. A name that is not an AutoCAD schema in Core, or a duplicate, is logged and skipped at start.
- An op without a handler fails job validation with issue `no_handler`. It never fails at execution.
- Handlers are created once and only called on AutoCAD's main thread. Keep no per-job state in fields: pass it from
  `Apply` to `Readback` through the plan object.
- `Execute` (in the base) calls `Apply`, then `Readback`. The op is ok only if `Readback` returns true. Use
  `Mismatch(result, "why")` to fail with a reason (it returns false).
- Write the planning logic once, as a static `Plan(ctx, args, issues)` method. Call it from `Validate`, and call it
  again at the start of `Apply` followed by `ThrowIfErrors(Name, issues)`. The document may differ from what
  validation saw (earlier ops have run), and Apply sees the resolved args.
- Override `AllowsUnitlessDrawing => true` only for ops that set INSUNITS (`fix.units`, `doc.setup`).

## 3. The context (`OpContext`)

| Member | Use |
|---|---|
| `Document`, `Database` | The job's drawing. Found once by path when the job started. Never use `MdiActiveDocument`, `HostApplicationServices.WorkingDatabase` or `Application.DocumentManager` in a handler. |
| `Transaction` | The job's single top-level transaction. It exists only during execution: Validate gets an exception if it touches it. Never start, commit or abort a transaction of your own in `Apply`, and never call `Commit`/`Abort` on this one. |
| `IsValidating` | True during validation. |
| `Units` | `DrawingUnits`: `ToDrawing(double)`, `ToDrawing(Point2)` (gives `Point3d`), `ToDrawing2d`, `ToJob(...)`, `Factor`. |
| `JobUnit`, `LengthTolerance` | The job's length unit and the readback tolerance in that unit (0.5 mm). |
| `Standard` | The job's `ProjectStandard`, or null. Loaded before validation; a missing or invalid file is a validation error. |
| `Plan` | `PlannedNames`: names earlier ops will create. `Validate` adds what it will create (`ctx.Plan.Add(PlannedNames.Layer, name)`), so a later op in the same job can use it. |
| `SetSysVar(name, value)` | Changes a system variable for this job only. The runner restores it in a finally block. Never call `Application.SetSystemVariable` directly. |
| `Warn(text)` | A report warning (source "engine") tied to the current op. |
| `IsRef(args, field)` | True while a field still holds a `"$ref"` (during validation). Skip document checks on it. |
| `IdOf(handle)`, `HandleOf(id)` | Handle string to `ObjectId` (Null when missing or erased), and back. |
| `Mode`, `AllowScript`, `Job`, `CurrentOp` | Run mode, the request's allowScript flag, the job, the op. |

## 4. Reading args and resolving refs

- The runner resolves references (`Refs.Resolve`) and re-checks the args against the schema before `Apply`. You
  always get typed args through Core's `OpArgs`:
  `GetString`, `GetStringOrNull`, `GetBool`, `GetInt`, `GetNumber`, `GetLength[OrNull]` (job units; strings such as
  `"3.2m"` are parsed), `GetAngle` (degrees), `GetPoint2[OrNull]`, `GetPoint2List`, `GetObject`, `GetObjectList`,
  `GetMap`, `GetRefId`, `GetRefIds`. Defaults come from the schema.
- `Has(name)` is true only when the job gave the field. Use it when "omitted" means something (for example "take it
  from the standard", see `LayerEnsure`): `Get*` returns the schema default for an omitted field.
- During validation, refs are unresolved strings. A typed getter on a ref field throws `OpArgsException`; the runner
  turns that into a `ref_deferred` warning and the checks run at execution instead. Prefer `ctx.IsRef(args, "x")`.
- Ref fields: `GetRefId("host")` returns the handle (from a literal, or the `id` of an earlier op's result);
  `GetRefIds` returns every handle. Resolve to objects with `ctx.IdOf(handle)` and treat `ObjectId.Null` as an error.
- Issues: `OpContext.Error(issues, args, "field", "code", "message")` and `OpContext.Warning(...)`. Use lower-case
  codes such as `layer_missing`, `textstyle_missing`, `pattern_missing`. Paths come from `args`, so nested items
  (`args.GetObjectList("layers")[i]`) report `$.ops[0].args.layers[2].color`.

## 5. Units

- Everything in args is in job units. Convert at the API boundary only: `ctx.Units.ToDrawing(...)` when writing,
  `ctx.Units.ToJob(...)` when reading back. Readback and outputs are in job units.
- Angles are degrees in args; AutoCAD wants radians (`deg * Math.PI / 180`).
- Unitless drawings (INSUNITS 0) are refused before your handler runs, unless every op in the job allows it.
- Schema length bounds are in mm and were checked by Core already; do not re-check them.

## 6. Creating entities correctly

```csharp
var tr = ctx.Transaction;
var ms = Db.ModelSpace(tr, ctx.Database, OpenMode.ForWrite);   // owner block table record
var line = new Line(ctx.Units.ToDrawing(a), ctx.Units.ToDrawing(b));
line.SetDatabaseDefaults(ctx.Database);    // current colour/linetype/etc. defaults of THIS database
line.Layer = layerName;                    // set properties you control explicitly
var id = Db.Append(ctx, ms, line);         // AppendEntity + tr.AddNewlyCreatedDBObject(entity, true)
result.Created.Add(OpContext.HandleOf(id));
```

- Every new object goes into its owner (`BlockTableRecord.AppendEntity`, `SymbolTable.Add`, `DBDictionary.SetAt`)
  and is then registered with `tr.AddNewlyCreatedDBObject(obj, true)`. Forgetting the second call leaks the object
  outside the transaction: rehearsal can no longer roll it back.
- Never use `Database.LoadLineTypeFile`, `Database.Insert`, `WblockCloneObjects`, LISP or COM to create things
  unless you have confirmed their changes are rolled back by `Transaction.Abort`; rehearsal relies on it.
  `LinetypeSupport` shows the alternative: build the record yourself inside the transaction.
- Layers: check the layer exists (`Db.LayerExists(ctx, name)`, which counts planned names while validating) and fail
  validation with `layer_missing` otherwise. Never create a layer as a side effect; that is `layer.ensure`'s job.
  When no layer is given, use the current layer (`Db.CurrentLayerName(ctx)`).
- Properties that need the object to be in the database (Annotative, `AdjustAlignment`, hatch patterns and loops,
  xdata with a registered app) are set after `Db.Append`.
- Annotative objects: the annotation scale must exist in the drawing (`AnnotationScales.Find`), else fail validation
  (`annotation_scale_missing`). Switch CANNOSCALE with `ctx.SetSysVar("CANNOSCALE", name)`, create the object, set
  `Annotative = AnnotativeStates.True`. `TextAdd` is the reference.
- Xdata: `Db.EnsureRegApp(ctx, Db.RegAppName)` first, then `entity.XData = new ResultBuffer(...)` starting with
  `(1001, "CADBIM")`.
- Text styles, linetypes, dimension styles: look up by name with `Db.SymbolId(tr, tableId, name)`; it returns
  `ObjectId.Null` for missing or erased records.
- Paper space: get the layout's `BlockTableRecordId` from the `Layout` object (by name through the layout
  dictionary of `ctx.Database`); do not use `LayoutManager.Current` for anything but switching layouts, and if you
  switch, switch back in a finally block.

## 7. Readback rules

- Re-open every object you created or changed through `ctx.Transaction` (`tr.GetObject(id, OpenMode.ForRead)`) and
  compare what AutoCAD stored with the plan: geometry within `ctx.LengthTolerance` in job units, angles with
  `Tolerance.AngleEqual`, names case-insensitively, flags exactly.
- Fill `result.Readback` with the values read back (job units), not with the values you intended to write.
- Fill `result.Outputs` with exactly the output names the schema declares (`sa ops --json`); later ops reference
  them as `$opId.name`.
- Ensure ops: an existing object with `update` false is added to `result.Unchanged`, is never opened for write, and
  any difference from the request is a warning, not a failure. With `update` true it goes to `result.Modified`.
- After commit, the runner re-opens every handle in Created and Modified in a fresh transaction; a missing one marks
  the job failed. You do not need to do that yourself.

## 8. Adding a fact collector

```csharp
internal sealed class BlocksCollector : IFactCollector
{
    public string Table => "blocks";
    public void Collect(FactContext ctx, FactTable table)
    {
        // ctx.Transaction is read-only for you; ctx.Database is the drawing.
        // table.Add(handle).Set("name", ...).Set("instances", ...);
        // Stop at ctx.MaxRecords and call ctx.MarkTruncated(Table, total).
    }
}
```

- Discovered by reflection like handlers. One table per collector; field names camelCase and stable, because check
  definitions in data files name them. Lengths in drawing units (Facts.Meta has `units.insUnits`).
- If `Collect` throws, the table is left out and checks that need it report `not_implemented`. Never return an
  empty table for "could not collect": an empty table passes checks.
- Engine-specific check evaluators: implement Core's `ICheckEvaluator` anywhere in the assembly with a
  parameterless constructor; `Audit/AuditService.cs` registers them next to Core's neutral ones.
- Check definitions are data: `*.json` in `<bundle>\Contents\data\checks\` or `%LOCALAPPDATA%\CadBim\checks\`.

## 9. Do not

- Do not send command strings (`SendStringToExecute`, `Editor.Command`, `acedCommand`, `(command ...)`), and do not
  use COM. Use the database API. If an operation truly needs a command, stop and raise it in the report instead.
- Do not look up the active document, the working database or the layout manager to find "the drawing". Use
  `ctx.Document` / `ctx.Database`.
- Do not modify an existing object in an ensure op unless `args.GetBool("update")` is true.
- Do not delete anything except objects created by the same job, or, for destructive ops, the exact handles the job
  listed (and check `expectCount` against the number of handles).
- Do not start, commit or abort transactions in a handler, and do not lock or unlock the document.
- Do not change system variables except through `ctx.SetSysVar`.
- Do not catch exceptions to hide them. Let them propagate; the runner rolls back the job and reports the op.
- Do not prompt (Editor.Get*), show dialogs or wait for the user.
- Do not use `#if` outside `Compat/`.
- Do not add package references. The engine uses only the BCL, the AutoCAD API and SA.Core.

## 10. Build

From the workspace root:

```
source tools/env.sh
export SA_TAG=<your tag>          # your own obj/bin folders, so parallel builders do not collide
cd cad-bim/engine
for y in 2024 2025 2026 2027; do dotnet build src/SA.AutoCAD -c Release -p:AppYear=$y; done
```

All four must end with `0 Warning(s)` and `0 Error(s)`. The compiler is the API check: a member that does not exist
in one release fails that year's build. Output: `engine/bin-<tag>/autocad/<year>/`.
