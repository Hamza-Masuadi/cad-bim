# The job loop in detail

Every change to a drawing or model goes through this loop. Read-only requests (snapshot, audit, capture, ping, status, lint, validate) need no approval. Nothing here has been run in AutoCAD or Revit yet: the engines compile for 2024 to 2027 and their behaviour is still to be confirmed in the apps, so report what the engine returned, not what should have happened.

Contents: 1 The client · 2 Snapshot · 3 Plan · 4 Approval · 5 Lint and validate · 6 Rehearse · 7 Commit · 8 Check · 9 Report · 10 Job ids, timeouts and status · 11 Escape hatches and jobs that must be alone · 12 Report statuses · 13 Error codes

## 1. The client

`sa.exe` sends one request to the engine running inside AutoCAD (`acad`) or Revit (`revit`) and prints JSON on stdout. The full command list:

```
sa sessions                                   List running engines
sa <acad|revit> ping                          Engine alive, versions, active document, busy state
sa <acad|revit> snapshot [--sections a,b] [--out file.json]
sa <acad|revit> validate job.json [--allow-script]
sa <acad|revit> run job.json [--rehearse] [--allow-script] [--timeout 600]
                 Revit only: [--fail-on-warnings] [--start-timeout <seconds>]
sa <acad|revit> status <jobId>
sa <acad|revit> audit [--checks a,b | --group g] [--standard file.json]
                 [--checks-file path [--checks-mode replace|add]] [--out file.json]
sa <acad|revit> capture --target ... --out file.png [--format png|pdf]
sa <acad|revit> probe Type.Member ...
sa job lint job.json                          Offline check, no app needed
sa ops [--app autocad|revit] [--json]         Op schemas
```

- `--pid <id>` picks a session when several copies of the app are running (`sa sessions` lists them); with one session it is implied.
- `--timeout <seconds>`: defaults are 600 for `run`, 300 for `snapshot`, `audit` and `capture`, 60 for the rest.
- Exit codes: 0 ok, 1 failed, 2 usage, 3 no session or several sessions without `--pid`, 4 connection failed, 5 timeout. `run` exits 0 only for `committed` (or, with `--rehearse`, a clean `rolled_back`).
- Revit `run` only: `--fail-on-warnings` fails an op (and rolls the job back) when Revit raises any warning during it, for jobs that must leave no new warnings; `--start-timeout <seconds>` (default 60, 5 to 3600) is how long the engine waits for Revit to become free before refusing to start the job.
- `audit --standard` and `--checks-file` paths are made absolute from the folder `sa` runs in, so pass full paths. With no `--standard` the engine uses its bundled default standard (see `standards.md`).
- Write job files as UTF-8 JSON, strict (no comments, no trailing commas).

## 2. Snapshot

One call returns what a plan needs. Take it at the start of a task and again whenever the user may have changed the file.

```
sa acad snapshot --out snap.json
sa revit snapshot --sections document,units,levels,wallTypes,familiesAndTypes,views,sheets --out snap.json
```

| App | Sections |
|---|---|
| AutoCAD | `document`, `sysvars`, `layers`, `linetypes`, `textStyles`, `dimStyles`, `blocks`, `xrefs`, `layouts`, `annotationScales`, `extents`, `selection` |
| Revit | `document`, `units`, `levels`, `grids`, `phases`, `worksets`, `designOptions`, `links`, `wallTypes`, `floorTypes`, `ceilingTypes`, `roofTypes`, `familiesAndTypes`, `viewFamilyTypes`, `views`, `viewTemplates`, `sheets`, `schedules`, `rooms`, `warnings`, `selection`, `categories` |

From the snapshot take:

- **Document identity** for the job's `document`: AutoCAD `document.path` and `document.fingerprint`; Revit the top-level `identity` (`path`, `fingerprint`). A Revit commit needs both.
- **Units.** Read them from the file, never assume. AutoCAD `document.units` (from INSUNITS) and `document.measurement`; a unitless drawing (`unitless: true`) refuses every job except `doc.setup` and `fix.units` until its units are set, so ask the user which unit it was drawn in. Revit `units` gives the project display units. Set the job's `units.length` to the file's unit unless there is a reason not to.
- **Names that already exist**: layers, blocks, styles, layouts (AutoCAD); levels, types ("Family : Type"), views, sheets, phases, worksets (Revit). Use them exactly. Revit lists are capped (`{count, truncated, items}`); ask for fewer sections if a list is truncated.
- **Selection**, when the user says "these walls" or "the selected doors".

## 3. Plan

Plan the whole task as one job (or the smallest number of jobs the rules in section 11 allow), with no app calls:

1. **Standard first.** On first use in a project, ask which standard applies (office, client or national) unless the snapshot settles it; state the inference when it does ("layers follow the US NCS format, so I'll use the US preset"). See `standards.md`. Never mix standards in one job.
2. **Read the practice guide** for the task (`references/practice/README.md` lists them) and `asking-the-user.md` for the open questions. Ask all open questions in one message, each with a proposed default.
3. **Choose ops** from `jobs/autocad.md` or `jobs/revit.md`, starting from a recipe in `autocad.md` or `revit.md`. Create what later ops need earlier in the same job (layers before walls, a wall type before walls, a sheet before viewports) and refer to results with `"$id"` or `"$id.field.0"`.
4. **Pick verify checks** that prove the result (`verify.checks`, ids from `data/checks/autocad.json` or `revit.json`) and a capture for anything visual (`verify.capture`: AutoCAD `layout:<name>`, `model`, `activeView` or `activeLayout`; Revit `activeView`, a view id or name, or `sheet:<number>`). Checks look at the **whole file**, not only what the job made, and a failing check of severity error or critical rolls the job back. So audit the chosen checks first (`sa acad audit --checks ...`): a check the file already fails is a pre-existing problem to report or fix in its own job, not a verify check for this one.
5. **Give the job a new `jobId`**: unique forever, letters, digits, `.`, `_`, `-`, up to 128 characters. Use a timestamp and a slug: `2026-11-28T09-30-00Z-l01-partitions`.

## 4. Approval

Show the plan and get **one** approval for the whole job before the commit. The plan says, in plain words:

- which file (path) and which standard and units;
- what will be created, changed or deleted, with counts ("14 walls on I-WALL, 6 doors D01 to D06, 5 rooms with tags");
- anything destructive, listed exactly: the element ids or layer names and the count (`element.delete` needs `expectCount`; `layer.translate` and `fix.purge` say what they will merge or remove);
- files that will be written and where (PDF, DWG, IFC, CSV);
- the checks that will run and what a pass means;
- assumptions made where the user gave no answer, marked as assumptions.

Rules:

- One approval covers lint, validate, rehearse, commit and the follow-up checks of that job. Do not ask again at each step.
- Ask again only if the job changes materially after approval: the rehearsal or validation shows a different count, a different element, an extra deletion, or a field you had to change. Show what changed.
- "Just do it" from the user approves the plan as you state it; still show it.
- Never save, sync with central, relinquish, or delete anything the plan did not list. The engines have no save or sync call; do not try to work around that.

## 5. Lint and validate

```
sa job lint job.json            # offline: schema, fields, types, $references, cross-field rules
sa acad validate job.json       # against the open drawing: names exist, geometry fits, nothing changes
```

Fix every error before going on. Lint also warns when `document` is missing. Lint does **not** enforce the "alone in its job" rules in section 11 or check that names exist; the engine does that at validate time.

## 6. Rehearse

```
sa acad run job.json --rehearse
```

A rehearsal runs the whole job, reads everything back, runs the verify checks and captures, then rolls back. It does not use up the job id, so the same file is committed next. Read the rehearsal report: counts, readback, warnings, check results. If anything differs from the approved plan, fix the job and, if the difference is material, show the user before committing.

Where a rehearsal differs from a commit:

- AutoCAD `plot.pdf` plots for real into a temporary folder, checks the files and deletes them; the report lists the paths the commit will write.
- AutoCAD `schedule.fromBlocks` writes its CSV to a temporary folder. Revit `export.pdf`, `export.dwg` and `export.ifc` stage files in a temporary folder and delete them.
- AutoCAD `xref.repath` never reloads in a rehearsal.
- Revit `param.shared.ensure` works on a temporary copy of the shared parameter file; the real file is written only after the job commits.
- AutoCAD `script.lisp` is checked but not evaluated in a rehearsal (section 11).
- AutoCAD `layout.create` with `makeCurrent` never switches layout in a rehearsal.

## 7. Commit

```
sa acad run job.json
```

The engine checks the document identity again, validates, runs every op inside one undo step named `SA: <title>`, reads every result back, runs `verify.checks` before closing the step (a failing check of severity error or critical rolls the job back), then captures. Any op failure rolls back the whole job.

After a commit the file has changed: use the report's `document.fingerprint` for the next job, or take a fresh snapshot (`--sections document` is enough).

## 8. Check

The verify checks in the job are the first check. For a wider check run an audit (no approval needed):

```
sa acad audit --group standards --out audit.json
sa revit audit --checks rv-005.duplicate-mark,rv-037.rooms
```

Look at the captures before reporting anything visual (`sa revit capture --target sheet:A-101 --out a101.png`).

## 9. Report

Tell the user, in this order:

1. The outcome: committed, rolled back or failed, and the undo step name ("one undo (U / Ctrl+Z) removes all of it").
2. What changed, with counts and the key readback values (lengths, areas, marks, sheet numbers, file paths).
3. What was checked and the result of each check, including `review` items (to look at by eye) and `incomplete` items (not fully checked). Say plainly what was **not** checked.
4. Warnings from the app or the engine, and dialogs the engine dismissed.
5. Assumptions used.
6. The picture, if there is one.

Honesty rules: an op is ok only when its readback matched; repeat the engine's words for anything that failed. Never call a result "compliant": design figures are defaults with a source and jurisdiction, and code compliance is the user's to confirm.

## 10. Job ids, timeouts and status

- A job id is spent by any commit attempt, whatever the outcome. A repeat is refused with `duplicate_job`. A rehearsal does not spend it.
- **Never re-send a `run` after a timeout or a broken connection.** The client says so and prints the next command. Ask instead:

```
sa acad status 2026-11-28T09-30-00Z-l01-partitions
```

| `status` answer | Meaning | What to do |
|---|---|---|
| `running` | Still going | Wait, then ask again. Do not send anything else to that app meanwhile (it answers `busy`) |
| `committed` | Done | Read the report it returns and carry on |
| `rolled_back` | Nothing changed | Fix the cause; send the corrected job with a **new** id after approval if it changed materially |
| `failed` | See the report's `error` and `failedOpId`; check `committed` and `scriptChangesRemain` (section 12) | As for rolled back, after telling the user |
| `unknown` | The engine has no record, or a commit was interrupted (`interrupted: true`, or "outcome unknown: check the drawing") | Do not re-send. Take a snapshot, tell the user what you see, and ask before doing anything |

- An engine `timeout` error whose `details.started` is `false` means the app stayed busy and the request never started: nothing ran, and the same job may be sent again. `started: true`, or no `started` at all (the client's own timeout, exit code 5), means the outcome is unknown: ask `status`.
- If the user asks to "run it again", that is a new job with a new id, and only after `status` shows the first one did not commit.

## 11. Escape hatches and jobs that must be alone

**AutoCAD `script.lisp`** (the only escape hatch in wave 1; Revit has none):

- Only when no typed op can do the task (for example trim, extend, fillet or offset edits). Say that it is the escape hatch in the plan, and show the code and its `purpose`.
- It must be the **only op in its job**, and the job may not carry `verify.checks` (`verify.capture` is allowed).
- Both `validate` and `run` need `--allow-script`. Rehearse first: the rehearsal lints the code but does not evaluate it. The commit runs only after an identical rehearsal (same job content) at the same fingerprint.
- The code must not prompt or run commands: `command`, `vl-cmdf`, `get*` input functions, `entsel` and `grread` are refused.
- **It cannot be rolled back.** Its changes join the job's undo step, so one U removes them, but if the job fails after the code ran the report says `failed` with `error.details.scriptChangesRemain: true`. Tell the user at once and offer the undo.
- It cannot be interrupted once running; `timeoutSeconds` is recorded only.
- Its evaluation has not yet been tested inside AutoCAD; it is the first item on the test list. If it is refused with `unsupported_in_release`, it has been switched off: say so and stop.

**AutoCAD `plot.pdf`** may hold only `plot.pdf` ops. Commit drawing changes in an earlier job, then plot.

**AutoCAD `fix.units`** should be alone, or the last op: later ops in the same job would convert lengths with the old units. **AutoCAD `xref.repath`** with `reload` (the default) belongs last or alone: a reload is not undone if a later op fails.

**Revit exports** (`export.pdf`, `export.dwg`, `export.image`): put them in their own job after the modelling job has committed. A job whose ops only read or export runs without a transaction group; inside a modelling job Revit may refuse the export (`export_refused`). `export.ifc` runs inside a sub-transaction that is always rolled back, so the model is not changed by it.

## 12. Report statuses

Job `status`:

| Status | Commit | Rehearsal |
|---|---|---|
| `committed` | Every op ran, read back and passed blocking checks; one undo step | — |
| `rolled_back` | Nothing changed: a readback mismatch or a blocking check (severity error or critical) rolled the job back | Success: everything ran and was rolled back |
| `failed` | An exception, an app error or a dialog stopped the job. Normally rolled back; read `committed` (true means changes were committed before a later step failed, for example post-commit readback: say "undo with U"), and `error.details.scriptChangesRemain` | Something failed. AutoCAD also reports `failed` with `error.details.rehearsalLeaked: true` when a change survived the rollback: stop and tell the user |
| `running`, `unknown` | Only from `status` (section 10) | |

Check `status` inside `checks[]`:

| Status | Meaning | How to report it |
|---|---|---|
| `pass` | The check ran on complete data and found nothing | "Checked: passed" |
| `fail` | Findings listed with element handles or ids | List them; a fail of severity error or critical blocked the commit |
| `review` | A review-by-eye item; never blocks | List it under "to check by eye" with its instructions; never count it as a pass |
| `incomplete` | Part of the data was cut at a safety ceiling, so a pass could not be proven. The report's first warning is `INCOMPLETE CHECK` (code `check_incomplete`) naming the check and the table. The job still commits | "Not fully checked" with the reason; never "passed" |
| `not_implemented` | The engine has no evaluator for this check | "Not checked" |
| `error` | The check itself failed to run | "Not checked", with the message |

Other report members: `results[]` per op (`ok`, `created`, `modified`, `deleted`, `unchanged`, `readback`, `outputs`, `error`), `summary` counts, `artifacts` (files written), `warnings` (from the app or engine; Revit warnings carry element ids), `dialogs` (Revit dialogs dismissed and how), `undo`, `failedOpId`, `timingMs`, and `extra.currentLayout` when an AutoCAD layout switch was made.

## 13. Error codes

| Code | Meaning | What to do |
|---|---|---|
| `bad_request`, `schema_invalid` | The request or job is malformed | Run `sa job lint`, fix, retry (nothing ran) |
| `validation_failed` | The job does not fit the open file (missing name, geometry that does not fit, ambiguous type); `details.issues` lists each | Fix the job; re-approve if the fix is material |
| `wrong_document` | Another file is active or the path does not match | Ask the user to bring the right file forward, or re-snapshot |
| `document_changed` | The file changed since the snapshot | Re-snapshot, re-check the plan, then send with the new fingerprint |
| `duplicate_job` | The job id was used before | Ask `status` for that id; never reuse ids |
| `busy` | Another job is running in that app | Wait and ask `status` for the running job |
| `not_owned` | Revit workshared model: elements belong to another user, or the local copy is stale; details list owners | Tell the user who owns what; never borrow, sync or relinquish |
| `read_only_document` | The file is read-only | Tell the user |
| `op_failed` | An op failed while running; `failedOpId` and `error` say why | Report it; the job was rolled back unless the report says otherwise |
| `rolled_back` | Readback or a blocking check failed | Report the mismatch or the findings |
| `timeout` | No answer in time | Section 10 |
| `unsupported_in_release` | The feature is switched off or absent in this release | Say so; do not work around it |
| `internal` | Engine error | Report the message and the job id |
