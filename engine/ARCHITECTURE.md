# Engine architecture contract

This file is the contract every part of the engine is written against. If code and this file disagree, fix one of them in the same change and say so in STATUS.md.

Nothing here has been run inside AutoCAD or Revit. "Compiles" is the strongest claim any part may make.

## 1. Goals, in priority order

1. **Never damage a drawing or model.** Wrong-document writes, double-applied jobs, half-applied jobs and silent failures are the failures to design out.
2. **Never report success that was not read back.**
3. **One call per job.** A job with 500 operations is one request, one undo step, one report.
4. **Typed and validated.** A job is rejected before any change if any field is wrong.
5. **Zero third-party runtime dependencies** inside the host apps. Other add-ins load their own copies of common libraries and conflicts are a known cause of load failures. The engines reference only the BCL, the Autodesk API and SA.Core. That is why SA.Core has its own small JSON reader and writer.

## 2. Solution layout

```
engine/
  ARCHITECTURE.md
  Directory.Build.props        Shared settings, build matrix, per-tag output folders
  CadBim.sln
  src/
    SA.Core/                   netstandard2.0. No Autodesk references. Pure logic.
    SA.Core.Tests/             net10.0, xunit. Runs anywhere.
    SA.Cli/                    net48 console exe named sa.exe (net48 ships with Windows 10/11).
    SA.AutoCAD/                AutoCAD engine. Built once per release.
    SA.Revit/                  Revit engine. Built once per release.
  build.ps1 / build.sh         Build every variant into engine/bin
  install/                     Per-user install and uninstall on Windows: install.ps1 (everything),
                               Install-AutoCAD.ps1, Install-Revit.ps1
  bin/                         Build output (created by the build)
```

## 3. Build matrix

| AppYear | AutoCAD package | Revit package | Target framework | Define |
|---|---|---|---|---|
| 2024 | AutoCAD.NET 24.3.0 | Nice3point.Revit.Api.RevitAPI(+UI) 2024.3.60 | net48 | `Y2024` |
| 2025 | AutoCAD.NET 25.0.1 | 2025.4.60 | net8.0-windows | `Y2025` |
| 2026 | AutoCAD.NET 25.1.0 | 2026.4.10 | net8.0-windows | `Y2026` |
| 2027 | AutoCAD.NET 26.0.0 | 2027.4.0 | net10.0-windows | `Y2027` |

- Select with `-p:AppYear=2026`. Output goes to `engine/bin/<app>/<AppYear>/`.
- Later updates of the 2025 and 2026 apps run on .NET 10. A net8 assembly loads on a .NET 10 runtime, so the net8 builds are intended to serve both. This is an assumption to confirm in testing; record it in STATUS.md.
- Also define cumulative symbols: `Y2025_OR_GREATER`, `Y2026_OR_GREATER`, `Y2027_OR_GREATER`. Use `#if` only inside each engine's `Compat/` folder. Everything else calls the compat layer.
- All Autodesk package references use `ExcludeAssets="runtime"` so no Autodesk file is ever copied to the output.
- `EnableWindowsTargeting=true` so the Windows targets build on macOS.
- Concurrent builds: `Directory.Build.props` must read an optional `SA_TAG` environment variable and append it to the `obj` and `bin` paths, so several builders can compile the same project at once without clobbering each other. Final builds use no tag.
- Use the SDK through `tools/env.sh` in the workspace root (it sets `DOTNET_ROOT`, a workspace-local NuGet folder and turns telemetry off).

Every source file must compile for all four years. The compiler is the API check: a member that does not exist in one release is a build error there.

## 4. The wire protocol

- **Transport:** a Windows named pipe, one per app process: `sa-<app>-<pid>` where app is `acad` or `revit`. The pipe must be restricted to the current user. Message framing is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON. Maximum message size 64 MB; larger is rejected.
- **Discovery:** on start the engine writes `%LOCALAPPDATA%\CadBim\sessions\<app>-<pid>.json` containing pipe name, app, release year, product version, process id, start time. It deletes the file on shutdown. The client ignores session files whose process no longer exists.
- **One request, one response per connection.** No streaming. Long jobs are followed with `status`.

Request:
```json
{ "v": 1, "id": "req-uuid", "method": "run", "params": { } }
```
Response:
```json
{ "v": 1, "id": "req-uuid", "ok": true, "result": { } }
{ "v": 1, "id": "req-uuid", "ok": false, "error": { "code": "wrong_document", "message": "...", "details": { } } }
```

Methods (identical names in both engines):

| Method | Purpose | Changes the document |
|---|---|---|
| `ping` | Engine alive, versions, active document identity, busy state | No |
| `snapshot` | Everything the planner needs, as one document. `params.sections` narrows it | No |
| `validate` | Full validation of a job against the live document | No |
| `run` | Validate, then execute. `params.mode` is `rehearse` (always rolled back) or `commit` | Only in `commit` |
| `status` | Look up a job by id: `unknown`, `running`, `committed`, `rolled_back`, `failed`, with its report if finished | No |
| `audit` | Run checks by id or group and return findings | No |
| `capture` | Export a view, layout or sheet as PNG or PDF into the job's output folder | No |
| `probe` | Report whether named API types and members exist in the running release | No |

Error codes are a closed list defined in `SA.Core/Protocol/ErrorCodes.cs`: `bad_request`, `schema_invalid`, `validation_failed`, `wrong_document`, `document_changed`, `duplicate_job`, `busy`, `not_owned`, `read_only_document`, `op_failed`, `rolled_back`, `timeout`, `unsupported_in_release`, `internal`.

## 5. Jobs

```json
{
  "jobId": "2026-11-27T10-15-00Z-walls-01",
  "title": "Ground floor partitions",
  "app": "revit",
  "document": { "path": "C:/proj/model.rvt", "fingerprint": "..." },
  "units": { "length": "mm" },
  "standard": "data/standards/iso19650-uk.json",
  "ops": [
    { "id": "w1", "op": "wall.create", "args": { "type": "Interior - 100mm", "level": "Level 0",
        "start": [0, 0], "end": [6000, 0], "height": 3000 } },
    { "id": "d1", "op": "door.place", "args": { "host": "$w1", "type": "Single 900x2100", "offset": 1200 } }
  ],
  "verify": { "checks": ["walls.open_ends", "walls.duplicates"], "capture": [ { "target": "activeView" } ] }
}
```

Rules:

- `jobId` is unique forever. The engine persists finished job ids (see section 8) and answers `duplicate_job` to a repeat.
- `document.path` and `document.fingerprint` come from the snapshot. Mismatch gives `wrong_document` or `document_changed` and nothing runs. The fingerprint is cheap and changes when the document changes materially; each engine defines its own (documented in its `Host/DocumentIdentity.cs`).
- `units.length` is one of `mm`, `cm`, `m`, `in`, `ft`. All lengths in `args` are plain numbers in that unit, or strings such as `"10'-6\""` or `"3.2m"` parsed by `SA.Core.Units`. Angles are degrees. Engines convert at the boundary only, through the app's own unit functions in Revit and through the drawing's insertion units in AutoCAD.
- `"$id"` refers to the result of an earlier op in the same job. `"$id.field"` picks a field.
- Ops run in order. Any failure rolls back the whole job.
- The agent never sends free-form code in a job. The escape hatch is a separate op (`script.lisp` in AutoCAD) that is refused unless `params.allowScript` is true, and always runs in `rehearse` first.

Report (the result of `run`, and what `status` returns):
```json
{
  "jobId": "...", "status": "committed", "mode": "commit",
  "validation": [ ],
  "results": [ { "id": "w1", "op": "wall.create", "ok": true, "created": ["123456"], "modified": [], "readback": { "length": 6000, "type": "Interior - 100mm" } } ],
  "summary": { "created": 2, "modified": 0, "deleted": 0 },
  "checks": [ { "id": "walls.open_ends", "status": "pass", "findings": [] } ],
  "artifacts": [ { "kind": "png", "path": "C:/.../out/view.png" } ],
  "warnings": [ { "source": "app", "text": "..." } ],
  "undo": "One undo step named 'SA: Ground floor partitions'",
  "timingMs": { "validate": 12, "execute": 340, "verify": 80 }
}
```

- Every op result carries `readback`: values read from the document after the op, in job units. The op is `ok` only if readback matches what was requested within tolerance. A mismatch fails the job.
- Element identity in reports: AutoCAD uses the object handle as a string; Revit uses the element id as a string (64-bit) plus `uniqueId` where useful.
- Output files go under `%LOCALAPPDATA%\CadBim\jobs\<jobId>\` unless the job gives `outputDir`.

## 6. SA.Core

Namespaces and responsibilities:

| Namespace | Contents |
|---|---|
| `SA.Core.Json` | Minimal JSON reader and writer to and from a `JsonValue` tree. Strict, UTF-8, no reflection. |
| `SA.Core.Protocol` | Request, response, error codes, framing helpers over a `Stream`. |
| `SA.Core.Jobs` | Job and report models, `$ref` resolution, the op registry (`OpSchema`: name, app, fields with type, required, default, description, unit kind). |
| `SA.Core.Validation` | Static validation of a job against op schemas (types, required, enums, ranges, refs). Returns a list of `Issue { path, code, message }`. Document-level validation lives in the engines. |
| `SA.Core.Units` | Length parsing and formatting for metric and imperial, conversion, tolerance compare. |
| `SA.Core.Geometry` | 2D points, segments, polygons. Wall centreline network to cleaned double-line outlines with L, T and X junctions. Polygon area and closure tests. Duplicate and overlap detection. Rectangle packing for viewports on a sheet. |
| `SA.Core.Standards` | Load a standards file. Parse and validate layer names (US-style and ISO 13567-style), sheet ids, file names against a naming rule. Scale tables and plotted text heights. |
| `SA.Core.Checks` | Check definitions loaded from data, a neutral `Facts` model each engine fills, evaluators keyed by name, findings with severity. A check whose evaluator an engine does not implement is reported as `not_implemented`, never as pass. A check whose facts table was cut (facts meta `truncated` or `omittedFields`) is reported as `incomplete` when it would pass. |

Op schemas are declared in Core for both apps (`SA.Core/Jobs/Ops/AutoCadOps.cs`, `RevitOps.cs`) so `sa job lint` can validate a job without the app running, and so `references/jobs/` can be generated from them. Engines register a handler per schema and refuse to start a job naming an op without a handler.

## 7. Engine structure (same shape in both)

```
SA.<App>/
  Host/          Entry point, pipe server, session file, main-thread queue, job runner, job ledger, document identity
  Compat/        The only place for #if by year
  Snapshot/      Builders for each snapshot section
  Ops/           One class per op: Validate(ctx, args) then Execute(ctx, args) returning OpResult with readback
  Audit/         Fact collectors and engine-specific evaluators
  Capture/       Image and PDF export
```

Handler contract:
```csharp
public interface IOpHandler {
    string Name { get; }
    void Validate(OpContext ctx, JsonValue args, List<Issue> issues);   // no changes, document-level checks
    OpResult Execute(OpContext ctx, JsonValue args);                     // inside the job's transaction
}
```

### Threading
- The pipe server runs on a background thread and only enqueues work.
- **AutoCAD:** work is drained on the application's idle event and executed in the document's command context, with the document locked. One job is one top-level transaction; the whole job is one undo step.
- **Revit:** work is raised through an external event. One job is one transaction group, assimilated on success and rolled back on any failure, named `SA: <title>`.
- A request that arrives while a job runs gets `busy`. `ping` and `status` are answered from the background thread without touching the API.

### Safety rules both engines implement
1. Check document identity before validation and again immediately before the first change.
2. Refuse read-only documents. In Revit, refuse family documents unless the op is a family op; in a workshared model, check ownership of every element the job will modify or delete and fail with `not_owned` listing owners. Never call save, synchronise or relinquish.
3. Revit: install a failures preprocessor on every transaction. Warnings are collected into the report. Errors roll the job back. Subscribe to the dialog-showing event for the duration of a job and dismiss with the safe default, recording each one.
4. AutoCAD: no command prompts for geometry. Use the database API. Where a command is unavoidable (plotting through the plot API is preferred over the command), suppress dialogs and restore every system variable in a `finally`.
5. "Ensure" ops never modify an existing object unless `args.update` is true.
6. `rehearse` runs everything, collects the report, then rolls back.
7. Deleting is allowed only for elements created by the same job, or when the op is an explicit delete op and the job was validated with the ids listed.
8. Every exception is caught at the job runner, the job is rolled back, and the report says `failed` with the op id and message.

### Revit: ops outside a transaction (family.edit)
Revit allows `Document.EditFamily` and the family document's `LoadFamily(project, options)` only while the project has no open transaction. Handlers that need this implement `IOutsideTransactionOp`. The runner runs them inside the job's transaction group but opens no transaction for them. It fails the op if the project is modifiable before or after the op. Such ops must be alone in their job (`JobValidator.AloneOps`, error `op_not_alone`); verify checks and captures still run. `family.edit` tries every change in a family copy that it rolls back during preflight. A rehearsal edits a copy and reads it back without loading. A commit loads the copy, so the group's single undo step holds the reload, and reads the values back from the project's types. Design notes: `reports/FAMILY-WORKSHARING-REPORT.md`.

### Revit: ownership mode
The job field `"ownership"` is `"refuse"` (the default, rule 2) or `"skip"`. In skip mode, declared elements that fail the ownership check are left out instead of refusing the job. The runner narrows each op generically. It expands `filter` into `elements` and strips from top-level reference fields the left-out ids that this op declared it changes (ids it only reads stay). It validates the op again, and if needed validates each list item alone and drops the items that declare a left-out element. When none of that works, it skips the whole op. Each left-out element is a warning `ownership_skipped` with its id, category, owner, workset and reason, and is listed in the op's `readback.ownershipSkipped`. An op left with nothing to do is skipped with a warning, and ops that refer to it are skipped too. Created elements are never affected. `validate` and `rehearse` list the ownership problems in both modes. The check of elements first declared when an op runs (after references resolve) still runs. In refuse mode it fails the job as before. In skip mode a new problem leaves those elements out the same way, and if the narrowed op reaches further elements that are not editable, the op is skipped rather than half done. Handler duties: `src/SA.Revit/OPS-GUIDE.md` section 5.

### Visual verification (`verify.visual`)
Each engine's `Capture/VisualService.cs` captures the target to PNG before the first op and after the last, then hands both files, the target's frame in its own coordinates, and the extents of the elements the job touched to `SA.Core.Imaging.VisualCheck`. That class decodes the PNGs (SA.Core's own codec, no packages), runs the pixel diff, writes `visual-diff.png` and returns the `visual.diff` check, the artifacts and the report's `visual` summary. AutoCAD plots outside the job's transaction: before it starts, and after it commits; a rehearsal cannot plot, so the check is `incomplete`. Revit exports inside the transaction group, after the captures. The check never rolls a job back. The layout checks (annotation overlap, content outside the crop or viewports, mixed scales) are ordinary data checks over the `annotations` and `jobPlacement` facts tables (`data/checks/FACTS.md` 1.8, 1.9, 2.7, 2.8). Design notes: `reports/VISUAL-CHECK-REPORT.md`.

## 8. Job ledger
Finished job ids and their reports are stored outside the document, in `%LOCALAPPDATA%\CadBim\ledger\<app>\<jobId>.json`, written after commit or rollback. `status` reads from memory first, then the ledger. Nothing is written into the user's drawing or model for bookkeeping.

## 9. Client (`sa.exe`)

```
sa sessions                              List running engines
sa <acad|revit> ping
sa <acad|revit> snapshot [--sections a,b] [--out file.json]
sa <acad|revit> validate job.json
sa <acad|revit> run job.json [--rehearse] [--timeout 600]
sa <acad|revit> status <jobId>
sa <acad|revit> audit [--checks a,b | --group g] [--out file.json]
sa <acad|revit> capture --target ... --out file.png
sa <acad|revit> probe Type.Member ...
sa job lint job.json                     Offline schema validation, no app needed
sa ops [--app revit] [--json]            List op schemas
```
- `--pid` selects a session when several are running; with one session it is implied. With several and no `--pid`, the client lists them and exits non-zero.
- Output is JSON on stdout. Exit code 0 only when `ok` is true and, for `run`, status is `committed` (or `rolled_back` with `--rehearse`).
- On timeout the client prints the job id and tells the caller to use `status`. It never re-sends a `run`.

## 10. Install layout (Windows, per user, no admin)

- AutoCAD: `%APPDATA%\Autodesk\ApplicationPlugins\CadBim.bundle\` with `PackageContents.xml` listing one component entry per release pointing at `Contents\<year>\SA.AutoCAD.dll`, and the bundled checks and standards in `Contents\data\checks\` and `Contents\data\standards\`.
- Revit: `%APPDATA%\Autodesk\Revit\Addins\<year>\CadBim.addin` pointing at `%LOCALAPPDATA%\CadBim\engine\revit\<year>\`, which holds `SA.Revit.dll` for that year and `checks\` and `standards\`.
- Client and data: `sa.exe` in `%LOCALAPPDATA%\CadBim\bin\` (optionally on the user PATH), the skill's `data\` folder in `%LOCALAPPDATA%\CadBim\data\`. `%LOCALAPPDATA%\CadBim\checks\` and `standards\` are left to the user: AutoCAD loads them as well as its bundled copy.
- `install\install.ps1` runs both per-app installers and installs the client and data. Each installs only for releases it finds, never elevates, supports `-Uninstall`, and prints what it did.

## 11. Code rules

- C# `latest`, nullable enabled, warnings as errors in Core.
- No third-party packages in SA.Core, SA.AutoCAD, SA.Revit or SA.Cli. Test project may use xunit.
- Comments only where a reader would otherwise be misled: API quirks, units, why a workaround exists.
- No file anywhere in the skill names another tool, project, skill or author.
- When unsure how an API behaves, read the official documentation; when it is still unclear, write the conservative version and add an entry to STATUS.md under "behaviour to confirm in the app".
- Never claim an op works. Claim it compiles for the listed years.
