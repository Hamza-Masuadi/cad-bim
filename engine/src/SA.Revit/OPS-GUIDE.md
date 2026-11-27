# Writing Revit op handlers

This guide is for anyone adding a handler to `src/SA.Revit`. The host (pipe, dispatch, job runner, identity,
ownership, ledger) already exists. You write one class per op. Copy the five reference handlers in `Ops/`:
`LevelEnsure`, `WallCreate`, `ParamSet`, `SheetCreate` and `ExportImage`.

Nothing here has been run inside Revit. The strongest claim any handler can make is "compiles for 2024-2027".

## 1. Build

```
source "$W/tools/env.sh"          # $W = workspace root (it contains spaces, so always quote it)
export SA_TAG=<your-tag>          # your own obj/bin folders, e.g. SA_TAG=ops-doors
cd "$W/cad-bim/engine"
for y in 2024 2025 2026 2027; do dotnet build src/SA.Revit -c Release -p:AppYear=$y; done
```

All four builds must finish with 0 errors and 0 warnings. Fix nullable warnings instead of suppressing them. An
obsolete-member warning (CS0618) means the member is deprecated, and it will become an error in a later release.
The compiler is the API check: a member that is missing in one release breaks that release's build.

## 2. Anatomy of a handler

```csharp
public sealed class DoorPlace : OpHandler                  // public or internal, sealed, parameterless ctor
{
    public override string Name => "door.place";           // exactly the Core schema name (RevitOps.cs)
    // public override bool ModifiesModel => false;        // only for read/export ops: no transaction is opened
    // public override bool AllowsFamilyDocument => true;  // only for family-editor ops

    public override void Validate(OpContext ctx, OpArgs args, List<Issue> issues) { ... }
    protected override void Apply(OpContext ctx, OpArgs args, OpResult result) { ... }
    public override void Readback(OpContext ctx, OpArgs args, OpResult result) { ... }
}
```

- **Discovery.** `HandlerRegistry` finds every concrete `IOpHandler` in the assembly by reflection. There is no
  list to edit. If a handler's `Name` has no Revit schema in Core, it is logged and ignored. If a schema has no
  handler, it appears in `ping.missingHandlers` and is refused at validation with issue code `no_handler`.
- **Lifecycle per op.** The runner calls:
  1. `Validate` in **preflight**, for every op, before anything runs. References are not resolved yet.
  2. `Validate` again in the **execute** phase, just before the op runs, with references resolved.
  3. `Execute` (the base class calls your `Apply`) inside the op's own `Transaction`.
  4. Commit. The failure preprocessor records warnings and turns errors into a rollback.
  5. `Readback`, after commit, against the regenerated model.
- A pattern that works well, used by every reference handler: write one private `Build(ctx, args, issues)` that does
  all the lookups and conversions and returns a plan, or null when there are errors. `Validate` calls it. `Apply`
  calls it again and throws when it returns errors. `Readback` may call it again to get the requested values.
- `Apply` fills `result.Created`, `Modified`, `Deleted` and `Unchanged` (element ids as strings via
  `ElementIds.ToWire`). It also fills `result.Outputs` with **exactly** the output names declared in the schema
  (`$w1.walls`, `$s1.sheet`).
- To fail an op, throw `OpFailedException(code, message, details)`. Any other exception is also caught and reported
  as `failed` with your op id, but it gives the agent a worse message.

## 3. Args and references

- `args` is Core's `OpArgs`. Use `GetString`, `GetLength` (job units, strings such as `"3.2m"` already parsed),
  `GetPoint2`, `GetObjectList` and the rest. Defaults come from the schema.
- **Preflight references.** In preflight, every `"$id..."` string in the args has been replaced by a marker object
  `{ "$pending": "$w1.walls.2" }`. Check before reading a field that may hold a reference:
  ```csharp
  if (ctx.IsPending(args, "host")) { /* skip document checks for it now; they run again in the execute phase */ }
  if (OpContext.IsPending(someJsonValue)) ...
  ```
  Ref-typed fields hold an element id string, a number or a result object. Read them with `Refs.ElementId(value)`,
  `Refs.ElementIds(value)`, `args.GetRefId(name)` or `args.GetRefIds(name)`. A whole-list reference (`"$w1.walls"`)
  resolves to an array. A bare `"$w1"` resolves to a result object, so accept both shapes (see `ParamSet.Elements`).
- In the execute phase the runner has already run `Refs.Resolve` and `JobValidator.ValidateArgs` for you.
- Issues: `OpContext.Error(issues, ctx.FieldPath(args, "type"), code, message)` and `OpContext.Warning(...)`. Use
  the codes in `RevitIssueCodes` (`not_found`, `ambiguous`, `existing_differs`, `invalid_value`, `read_only`,
  `already_exists`, `not_prechecked`, ...). The wire error for any error-severity issue is `validation_failed`.
  Warnings are reported and do not block.

## 4. Units: `RevitUnits` (`ctx.Units`)

Every conversion goes through `UnitUtils` with `ForgeTypeId` units. **Never write a factor such as 304.8, 0.3048 or
3.28084.**

| Method | Meaning |
|---|---|
| `LengthToInternal(job)`, `LengthFromInternal(feet)` | job length unit (mm, cm, m, in, ft) |
| `PointToInternal(Point2, zInternal)`, `PointFromInternal(XYZ)`, `PointJson(XYZ)` | plan points |
| `AreaToInternal` / `AreaFromInternal`, `VolumeTo/FromInternal` | m² / m³ for metric jobs, ft² / ft³ for imperial |
| `AngleToInternal(deg)` / `AngleFromInternal(rad)` | degrees |
| `TryToInternal(spec, JsonValue, out internal, out error)` | a parameter value by its spec (`Definition.GetDataType()`) |
| `FromInternal(spec, internal)` | back to the job convention |
| `LengthJson(internal)`, `RevitUnits.Num(double)` | rounded JSON numbers for readback |
| `Display(spec, internal)` | the project's own formatting, e.g. `"3000 mm"` |
| `LengthTolerance` / `LengthToleranceInternal` | 0.5 mm, for comparisons |

For a spec that is not length, area, volume or angle, `TryToInternal` reads numbers in the **project's display
unit** for that spec, and parses strings with Revit's own `UnitFormatUtils.TryParse`.

Elevations in jobs are measured above the project base point: internal Z = job value + `BasePoint.GetProjectBasePoint(doc).Position.Z`
(see `LevelEnsure`).

## 5. Declaring what you touch (ownership and deletion)

In `Validate`, call:

- `ctx.DeclareModifies(id)` for every **existing** element you will change. This includes types when you set type
  parameters, and the title block instance when you change its type.
- `ctx.DeclareDeletes(id)` for every element you will delete. Only ops whose schema is `Destructive` may delete
  elements the job did not create. A non-destructive op that declares deletes is refused.
- `ctx.DeclareModifiesCategory(category)` when you change a category's or subcategory's own settings (its material,
  line weights or colour). Categories are not elements; this declares the category's projection graphics style, an
  element of the Object Styles workset, in its place. It returns false when the category has none; Revit's check at
  commit then still applies.

The runner does not trust declarations alone:

- In a workshared model it checks ownership and central status of every declared element before anything runs.
  It checks again for elements first declared in the execute phase. A problem fails the job with `not_owned`,
  listing ids, owners and reasons. Elements created earlier in the same job are skipped.
- After each op's transaction commits, it compares the ids Revit **actually deleted** (from `DocumentChanged`)
  with the allowed set: elements created by the job, plus declared deletes for destructive ops. Anything else
  fails the job.
- Not covered: side effects Revit makes on its own (wall joins, hosted elements, rooms). Mention them in your op's
  doc comment if they matter.

Ownership `"skip"` (a job field; the default `"refuse"` is the behaviour above). The runner leaves out elements this
user cannot edit and reports each one as an `ownership_skipped` warning. It narrows the op itself, so most handlers do
nothing special:

- Explicit id lists and `filter` fields are narrowed for you. The runner expands a `filter` into the matching
  `elements`, removes from top-level reference fields the left-out ids this op declared it changes (ids it only
  reads, such as a dimension's references, stay), and validates the op again.
- If the op still declares a left-out element, the runner validates each item of top-level lists of references or
  objects on its own and drops the items that declare one. If that is not possible, it skips the whole op, gives it
  empty outputs, and skips later ops that refer to it.
- What you must do: when `Validate` finds elements itself (all walls on a level, the hosts of a door), check
  `ctx.IsLeftOut(id)` before declaring each one, and leave those out of what you change. If an element is
  essential to the op (the one family it edits, the one host it needs), declare it anyway. The runner then skips
  the op rather than half-doing it.
- `DeclareModifies` and `DeclareDeletes` never put a left-out id into the declared sets. Do not read
  `ctx.Modifies` to decide what to change.

## 6. Lookups (`Ops/Support/Lookup.cs`)

- `Lookup.Level(doc, name)`, `Lookup.Type<WallType>(doc, name, "wall type")` (accepts `"Family : Type"`),
  `Lookup.Symbol(doc, BuiltInCategory.OST_Doors, name, "door type")`, `Lookup.View(doc, value)` (id, result,
  `sheet:<number>`, view name or sheet number), `Lookup.SheetByNumber`, `Lookup.Category(doc, "OST_Doors" | "Doors", out err)`,
  `Lookup.FirstViewFamilyType(doc, ViewFamily.FloorPlan)`.
- Each returns `Found<T>`: `Ok`, `Value`, `Error` and `Code`. On a miss the error names up to five close matches. Pass
  it straight into the issue: `OpContext.Error(issues, path, found.Code, found.Error!)`.
- Matching: exact first, then case-insensitive if that gives a unique match. An ambiguous name is refused and the
  error asks for an id or `"Family : Type"`.

## 7. Parameters (`Ops/Support/ParamAccess.cs`)

- `Find(element, name, out error)`. The name can be a `BuiltInParameter` enum name, a shared parameter GUID or a
  display name. A display name shared by two parameters is refused as ambiguous.
- `TryPrepare(doc, param, value, units, out prepared, out error)` checks the value against the storage type:
  - doubles: converted by spec;
  - yes/no: `true`/`false` (or 0/1);
  - integers: must be integral;
  - text: a string, or an integer literal written as text;
  - element ids: an id, a reference, or null for none.
- `Apply(param, prepared)` returns false if Revit refused the value. Treat false as a failure.
- `Read(param, units)` returns the value in job convention, for readback.
- `Matches(param, prepared, units)` compares with tolerance.
- `KnownFor(doc, category, name, sample)` checks, before an element exists, whether a parameter name is plausible.
  It returns null for "cannot tell". Report that as a warning (`not_prechecked`), not an error.
- Check `param.IsReadOnly` in Validate.

## 8. Transactions

- **Handlers never start a `TransactionGroup`, and never start, commit or roll back a `Transaction`.** The
  runner owns them: one group per job, named `SA: <title>`, and one transaction per op. The group is assimilated on
  success (one undo step) or rolled back (rehearse, or any failure).
- When you need to start a `SubTransaction` inside your `Apply`:
  - you must commit or roll it back before returning;
  - use it only to try something and undo it, for example probing whether a placement is valid.
- Call `doc.Regenerate()` inside `Apply` when a later step in the same op reads geometry or computed values of
  something you just changed, for example after `FamilySymbol.Activate()` or before reading a new element's
  bounding box. You do not need to regenerate before readback, because readback runs after commit.
- Ops that only read or export: set `ModifiesModel => false`, and no transaction is opened. A job whose ops are all
  of this kind runs without a transaction group; in a job that also changes the model they run inside the group but
  outside any transaction. `ExportImage` shows the pattern.
- The one exception: ops that must call Revit with no transaction open (`Document.EditFamily`, the family
  document's `LoadFamily(project, options)`) implement `IOutsideTransactionOp`. The runner then runs them inside
  the job's group but opens no transaction. Such an op must be declared alone in its job (`JobValidator.AloneOps`)
  and must leave the project without an open transaction. It may start, commit and roll back transactions only in
  the family documents it opens, and it closes them without saving before it returns. `Ops/Family/FamilyEdit.cs`
  shows the pattern.
- Files written outside the model (exports, the shared parameter file) never go straight to their final path.
  Write them into `ctx.StageFolder(finalDir)` and register each with `ctx.StageFile(staged, final, overwrite)`. A
  rehearsal's staging folder is in the temp folder and is deleted after the job; in commit mode the runner moves
  the files to their final paths only after the job has committed. Readback inspects `ctx.ActualPath(final)`, and
  outputs and readback report the final path. Add artifacts only when `!ctx.IsRehearsal`.
- Lengths measured on the printed sheet ("paper units" in a schema) are read with `ctx.PaperLength`, `ctx.PaperLengthOrNull`
  and `ctx.PaperPoint`, and converted with `ctx.Paper`: millimetres, or inches for an imperial job. Pass `ctx.Paper` to
  `ReadbackChecker.Length` / `Point` for those values.
- The job's standard is `ctx.Standard`, loaded by the runner before preflight (a relative path resolves against the
  model's folder; a standard that cannot be loaded refuses the job). Do not load it yourself.
- Editing stairs and other edit scopes (`StairsEditScope`) needs its own failure preprocessor on `Commit`. Ask
  before you write one.

## 9. Readback rules

- An op is `ok` only when every value it changed has been read back from the document after commit and matches
  the request within tolerance. Use `ReadbackChecker`:
  ```csharp
  var check = new ReadbackChecker(ctx.Units);
  check.Length(rb, "length", curve.Length, requestedJobUnits, "wall 1 length");
  check.Text(rb, "type", wall.WallType.Name, requestedName);
  check.Flag(rb, "structural", actual, requested);
  check.Point(rb, "start", xyz, requestedPoint2);
  check.Fail("free-form mismatch");
  check.Apply(result);   // sets result.Ok and, on mismatch, error "readback_mismatch" listing every difference
  ```
- Write readback in job units with stable keys. Include the element ids, and say whether each element was
  `created`, `updated` or `unchanged` for ensure ops.
- Read back from the element (`doc.GetElement(id)`), never from the variables you set. Treat an element that is
  gone after commit as a failure.
- A readback mismatch rolls the whole job back with status `rolled_back`.

## 10. Ensure ops

Ensure ops (`Ensure` in the schema) never change an existing object unless `args.update` is true:

- If the existing object matches, report it in `Unchanged` and still fill `Outputs` so later refs work.
- If it differs and `update` is false, raise a **validation error** (`existing_differs`) that says what differs. Do
  not report success.
- If `update` is true, declare the modification, change the object, and put it in `Modified`.

## 11. Fact collectors (audit)

Implement `IFactCollector` (`Audit/AuditService.cs`) with a parameterless constructor. It is found automatically.

```csharp
internal sealed class DoorsCollector : IFactCollector
{
    public string Table => "doors";
    public void Collect(AuditContext ctx, FactTable table)
    {
        foreach (var e in new FilteredElementCollector(ctx.Doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType())
            table.Add(ElementIds.ToWire(e.Id)).Set("mark", e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "");
    }
}
```

- Collectors are read only: no transactions.
- Write lengths in `ctx.Units`, ids as strings, one record per element, and the element id as the record id.
- Table and field names must match the checks data. Document the fields in the class comment, as
  `WarningsCollector` and `ViewsCollector` do.
- If a collector throws, its table is left out and every check that needs it reports `not_implemented`, never
  `pass`.
- Engine-specific evaluators: implement Core's `ICheckEvaluator` with a parameterless constructor. It is registered
  next to the neutral evaluators.

## 12. Do not

- Do not save, synchronise, relinquish, reload latest, or borrow (`CheckoutElements`). Do not call
  `Document.Save*`, `SynchronizeWithCentral`, `RelinquishOwnership` or `ReloadLatest`.
- Do not look up the active document or the active UI document (`ActiveUIDocument`, `uiapp.ActiveUIDocument.Document`)
  in a handler. Use `ctx.Doc`, which is the document named in the job. `doc.ActiveView` is acceptable only for an
  explicit `"activeView"` target.
- Do not make UI calls: `TaskDialog`, `Selection.Pick*`, `PostCommand`, setting `ActiveView`, or WPF/WinForms
  windows. A dialog during a job fails the job.
- Do not hard-code unit factors, or assume that the internal unit is feet in arithmetic. Use `RevitUnits`.
- Do not use 32-bit element ids. `ElementId.IntegerValue` and `new ElementId(int)` are removed in 2026. Use
  `Compat.ElementIds` (`ToWire`, `TryParse`, `FromLong`, `ToLong`), and keep ids as strings on the wire.
- Do not write `#if` outside `Compat/`. If an API differs between releases, add a wrapper there.
- Do not reference any third-party package or copy code from another tool.
- Do not swallow failures. Every warning Revit raises is already recorded. Do not delete warnings or resolve
  errors in your handler.
- Do not delete anything you did not declare. Do not delete existing elements from a non-destructive op.
- Do not claim success without readback, and do not write readback from the request values.

## 13. Checklist for a new handler

1. The schema exists in `SA.Core/Jobs/Ops/RevitOps.cs`. If a field is wrong, request a Core change; do not work
   around it.
2. `Validate` resolves every name, checks every value, declares every touched element, and skips pending refs.
3. `Apply` changes only what was validated, fills `Created`/`Modified`/`Unchanged`, and fills `Outputs` exactly as
   the schema declares.
4. `Readback` reads every changed value back from the document and calls `check.Apply(result)`.
5. All four builds pass with 0 warnings.
6. Add a line to "behaviour to confirm in the app" in `REVIT-HOST-REPORT.md` for every assumption you could not
   verify from the API documentation.
