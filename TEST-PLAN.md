# Test plan for the owner

Nothing in this skill has run in AutoCAD or Revit. Every part is "compiles, untested" until a line in this file says
Pass. Run on copies and scratch files only, never on live project files. Save and close every other drawing and model
before Block 1: some of those tests can crash the app.

How to use this file:

- Section 1 gets the engines onto a Windows machine and loaded.
- Section 2 is the run sheet: the order to work in. It points at items in sections 3, 4 and 5.
- Section 3 is the base test table (T0-T25).
- Section 4 is every "behaviour to confirm in the app" from the engine reports, merged, one item each.
- Section 5 is the planted-defect runs.
- Write Pass, Fail or a note in each item. For every Fail, collect what section 1.7 lists.

Item ids: `T` base tests, `CX` both apps (install and load), `CA` AutoCAD, `CR` Revit. "Settles" says which file or
decision the result feeds; the agent that picks up your results updates those files.

---

## 1. Setup

### 1.1 Machines

- Windows 10 or 11, full AutoCAD (not LT) and Revit (not LT).
- Ideally two machines or two installs: **2024** (net48 engines) and **2027** (.NET 10 engines). They are the two
  ends of the build matrix. Run 2025 and 2026 afterwards (T22, CX-02).
- Note the exact update of each app (Help > About). The 2025 and 2026 engines are .NET 8 builds that are meant to
  load in the later updates of those releases that run on .NET 10 (CX-02).
- One normal user account, not elevated. A second Windows user is needed only for CA-06 and CR-07.
- A working folder for the tests: `C:\sa-test\` (jobs, captures, outputs). The examples below use it.

### 1.2 Get the engines

`cad-bim/engine/bin/` already holds built engines (dated 2026-11-28): `autocad\2024..2027`,
`revit\2024..2027` and `cli\sa.exe`. Use them. Rebuild only if a fix lands:

- Windows, with the .NET 10 SDK installed: `cd cad-bim\engine; .\build.ps1 -Test` (never run on Windows
  before; note any failure). Output goes to `engine\bin\`.
- macOS or Linux, from the workspace root: `source tools/env.sh; cd cad-bim/engine; ./build.sh`.
  Then copy the whole `cad-bim` folder to the Windows machine.

### 1.3 Install

The installers have never run on Windows (OWNER-DECISIONS item 17). In PowerShell, not elevated, from the copied
`cad-bim` folder:

```
powershell -ExecutionPolicy Bypass -File engine\install\install.ps1 -AddToPath
```

- It runs `Install-AutoCAD.ps1` and `Install-Revit.ps1` (each installs only the releases it detects; `-Years 2027`
  limits it, `-Force` installs AutoCAD releases it cannot detect), copies `sa.exe` to
  `%LOCALAPPDATA%\CadBim\bin\` and the data to `%LOCALAPPDATA%\CadBim\data\`.
- Close AutoCAD first: the AutoCAD installer refuses while AutoCAD has the bundle loaded.
- Open a **new** terminal afterwards so `sa` is on the PATH. Check: `sa --help` prints the usage text.
- Uninstall later with `install.ps1 -Uninstall` (logs, sessions and the ledger stay).

Expected files:

| What | Where |
|---|---|
| AutoCAD bundle | `%APPDATA%\Autodesk\ApplicationPlugins\CadBim.bundle\` with `PackageContents.xml`, `Contents\<year>\SA.AutoCAD.dll`, `Contents\data\checks\`, `Contents\data\standards\` |
| Revit manifest | `%APPDATA%\Autodesk\Revit\Addins\<year>\CadBim.addin` |
| Revit engine | `%LOCALAPPDATA%\CadBim\engine\revit\<year>\SA.Revit.dll`, `SA.Core.dll`, `checks\`, `standards\` |
| Client | `%LOCALAPPDATA%\CadBim\bin\sa.exe` |
| Data | `%LOCALAPPDATA%\CadBim\data\` |

### 1.4 Confirm the engine loaded

- **AutoCAD:** start AutoCAD, open a drawing, type `SAENGINE`. It prints the engine state. If AutoCAD shows a
  security prompt for loading the add-in, choose to load it and note the prompt's wording.
- **Revit:** start Revit and open a model. If Revit asks about an unsigned add-in, choose "Always Load" and note it.
  Revit shows no engine UI; the engine is loaded when the session file below exists.
- **Both:** `sa sessions` lists one entry per running app (pipe name, app, release, product version, process id).
  `sa acad ping` and `sa revit ping` answer `"ok": true` with versions and the active document.
- If nothing appears: read the log (1.5); for Revit also the journal
  (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\`, newest file).

### 1.5 Where things land

All under `%LOCALAPPDATA%\CadBim\`:

| Folder | Contents |
|---|---|
| `logs\acad-<pid>.log`, `logs\revit-<pid>.log` | Engine log (AutoCAD rolls at 2 MB, Revit at 4 MB, 4 old files kept) |
| `sessions\acad-<pid>.json`, `sessions\revit-<pid>.json` | Written when the engine listens, deleted on normal exit |
| `ledger\acad\<jobId>.json`, `ledger\revit\<jobId>.json` | Final report of every committed job; `ledger\<app>\rehearse\<jobId>.json` for rehearsals |
| `jobs\<jobId>\` | Job outputs (PDFs, captures, CSVs) unless the job sets `outputDir` |
| `%TEMP%` | Rehearsal staging (AutoCAD plots: `%TEMP%\SA-rehearse\<job>\<op>`); deleted afterwards |

### 1.6 Running a job by hand

1. Snapshot the document you will change and keep it:
   `sa acad snapshot --sections document --out C:\sa-test\snap.json` (AutoCAD: copy `document.path` and
   `document.fingerprint`), or `sa revit snapshot --sections document --out C:\sa-test\snap.json` (Revit: copy the
   top-level `identity.path` and `identity.fingerprint`).
2. Put both into the job's `document`. The fingerprint changes after every commit, so take a new snapshot before
   each committing job.
3. `sa job lint C:\sa-test\job.json` (offline; expect `"ok": true`).
4. `sa acad run C:\sa-test\job.json --rehearse > C:\sa-test\<id>-rehearse.json`, then read it.
5. `sa acad run C:\sa-test\job.json > C:\sa-test\<id>-commit.json`. `echo $LASTEXITCODE` gives the exit code:
   0 ok, 1 failed, 2 usage, 3 no or ambiguous session, 4 connection, 5 client timeout.
6. A committed job id is spent forever. Give each new committing job a new `jobId` (letters, digits, `.`, `_`, `-`).
   A rehearsal does not spend the id, so rehearse then commit the same file.

Other commands: `sa <acad|revit> validate job.json [--allow-script]`, `status <jobId>`,
`audit [--checks a,b | --group g] [--standard file.json] [--checks-file path [--checks-mode replace|add]] [--out file.json]`,
`capture --target ... --out file [--format png|pdf]`,
`probe Type.Member`, `sa ops --app autocad|revit` (every op and its fields), `--pid <id>` when two sessions of the
same app run, `--timeout <seconds>` (client wait; defaults: run 600, snapshot, audit and capture 300, others 60).
Revit `run` also takes `--fail-on-warnings` and `--start-timeout <seconds>` (how long a job may wait to start; default 60).

Job templates used below. JSON paths need `/` or `\\`. Replace the `<...>` values.

**J-A-LISP** (AutoCAD; for CA-01):
```json
{ "jobId": "t-acad-lisp-01", "title": "LISP smoke test", "app": "autocad",
  "document": { "path": "<document.path>", "fingerprint": "<document.fingerprint>" },
  "units": { "length": "mm" },
  "ops": [ { "id": "s1", "op": "script.lisp", "args": { "code": "(+ 1 2)", "purpose": "Return 3, change nothing" } } ] }
```
Second job `t-acad-lisp-02`: code `"(entmake '((0 . \"LINE\") (10 0.0 0.0 0.0) (11 1000.0 0.0 0.0)))"`, purpose
`"Draw one line"`.

**J-A-WALLS** (AutoCAD; 20 walls on a metric drawing, five closed rooms of four walls):
```json
{ "jobId": "t04-acad-01", "title": "Twenty walls", "app": "autocad",
  "document": { "path": "<document.path>", "fingerprint": "<document.fingerprint>" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "lay", "op": "layer.ensure", "args": { "layers": [ { "name": "A-WALL" } ] } },
    { "id": "w", "op": "wall.draw", "args": { "thickness": 200, "layer": "A-WALL", "chains": [
      { "points": [[0,0],[4000,0],[4000,3000],[0,3000]], "closed": true },
      { "points": [[5000,0],[9000,0],[9000,3000],[5000,3000]], "closed": true },
      { "points": [[10000,0],[14000,0],[14000,3000],[10000,3000]], "closed": true },
      { "points": [[15000,0],[19000,0],[19000,3000],[15000,3000]], "closed": true },
      { "points": [[20000,0],[24000,0],[24000,3000],[20000,3000]], "closed": true } ] } } ] }
```
**J-A-FAIL** (AutoCAD; fails on purpose after every op has run): J-A-WALLS plus a third op `w2`, a copy of `w` with
only the first chain, and `"verify": { "checks": ["ac-022.duplicate-lines"] }`. The duplicate lines fail a check of
severity error, which rolls the job back.

**J-A-PLOT** (AutoCAD; alone in its job):
```json
{ "jobId": "t-acad-plot-01", "title": "Plot one layout", "app": "autocad",
  "document": { "path": "<document.path>", "fingerprint": "<document.fingerprint>" },
  "units": { "length": "mm" },
  "ops": [ { "id": "p1", "op": "plot.pdf", "args": { "layouts": ["<layout name>"], "outputDir": "C:/sa-test/plot" } } ] }
```

**J-R-WALLS** (Revit; 20 walls): five `wall.create` ops, ids `w1`..`w5`, each
`{ "type": "<a wall type from snapshot wallTypes>", "level": "Level 1", "path": [[X,0],[X+4000,0],[X+4000,3000],[X,3000]], "closed": true, "height": 3000 }`
with X = 0, 5000, 10000, 15000, 20000, `"app": "revit"`, `"units": { "length": "mm" }`, document from `identity`.

**J-R-FAIL** (Revit): J-R-WALLS plus `{ "id": "g1", "op": "grid.create", "args": { "lines": [ { "label": "SA1", "start": [0,-2000], "end": [0,12000] } ] } }`
and `"verify": { "checks": ["rv-017.datums-pinned"] }`. New grids are not pinned, so the check fails with severity
error and the job rolls back.

**J-R-WARN** (Revit): two `wall.create` ops with the same type, level, `start` [0,0], `end` [6000,0] and height.
Revit raises its "walls overlap" warning; the job should still commit and record it.

**J-R-PDF** (Revit; export only): one op `{ "id": "e1", "op": "export.pdf", "args": { "sheets": ["<sheet number>"], "outputDir": "C:/sa-test/pdf" } }`.

### 1.7 What to send back on a failure

1. The report JSON (the file you redirected `sa` output into), and the rehearsal report if there was one.
2. The capture or exported file, if any, and a screenshot of the app.
3. App release and update (Help > About) and the output of `sa <app> ping`.
4. The engine log from `logs\` for that process id, plus the session file and `ledger\<app>\<jobId>.json`.
5. For a crash: the crash dialog's details, the Windows Event Viewer entry (Windows Logs > Application), and for
   Revit the newest journal file.
6. The job file and the snapshot you built it from.

---

## 2. Run order

Most valuable and most fragile first. Do Blocks 0 and 1 on 2024 and on 2027 before anything else.

**Block 0: loaded at all**

| # | Item | One line |
|---|---|---|
| 1 | T0, CX-01, CX-03 | Install; each app loads its engine |
| 2 | T1 | AutoCAD snapshot answers; nothing left on the command line |
| 3 | T2 | Revit snapshot answers; Revit stays responsive |

**Block 1: could crash the app or lose work**

| # | Item | One line |
|---|---|---|
| 4 | CA-01 | `script.lisp` evaluation (undocumented native call): no crash, value returned, one U removes it |
| 5 | CA-02 | `script.lisp` refusals: alone, no checks, rehearsal first |
| 6 | CA-03 | `plot.pdf` alone in its job: refusal, rehearsal into temp, commit, multi-page |
| 7 | CA-04 | `capture` plots a layout: file written, layout restored, fingerprint unchanged |
| 8 | CR-01 | Revit staged exports: rehearsal leaves no file; commit publishes after the job |
| 9 | CR-02 | Revit export inside a modelling job (open transaction group) |
| 10 | CR-03 | Revit IFC export inside a rolled-back sub-transaction |
| 11 | CR-04 | Shared parameter file written only after commit |
| 12 | CR-05, T7 | Revit dialog guard and failure handling; AutoCAD plot dialogs |
| 13 | CA-05, CR-06 | Main-thread queue, busy, start deadlines, client timeout |
| 14 | CA-06, CR-07 | Pipe security and owner checks |
| 15 | T4, CA-07, CR-08 | One undo step; rehearsal leaves no trace |
| 16 | T5 | Failure rolls everything back |
| 17 | T3, CA-08, CR-09 | Wrong-document guard; no false "document changed" |
| 18 | T6, CA-09, CR-10 | No double apply; ledger and status |

**Block 2: other rehearsals whose rollback is unproven**

| # | Item | One line |
|---|---|---|
| 19 | CA-10 | Header variables after a rehearsal (doc.setup, fix.units) |
| 20 | CA-11 | DIMBLK restore after a dimension style rehearsal |
| 21 | CA-12 | `xref.attach` rehearsal leaves no xref definition |
| 22 | CA-13 | `layout.create`: hand-built layout, abort, makeCurrent |
| 23 | CA-15 | Unitless drawing refused |
| 24 | CX-02, CX-04 | .NET runtime on later updates; session file on exit and crash |

**Block 3: base tests** T8, T9, T12, T13, T14, T15, T16, T17, then section 5 (T10, T11), then T18, T19, T20, T22.

**Block 4: remaining confirm items** in section 4 order: CA-14 to CA-38 (skip those already done), then CR-11 to
CR-35.

**Block 5:** T21, T23, T24, T25 (mostly not testable yet; see section 3).

---

## 3. Base tests

Updated from plan section 8 to what wave 1 actually has. Wave 2 tasks (A28-A35, R33-R49, materials) have no ops
yet and are not tests. Every job runs as in section 1.6.

| # | Test | Steps | You should see | Pass / Fail / Notes |
|---|---|---|---|---|
| T0 | Build and install | 1.2 and 1.3; then 1.4 in each installed release | Installer prints what it copied per release; `SAENGINE` prints the state; `sa sessions` lists AutoCAD and Revit; no load error in the log or on start | |
| T1 | AutoCAD bridge | New drawing from `acadiso.dwt`. `sa acad ping`, then `sa acad snapshot --out C:\sa-test\t1.json` | Answer within seconds; 12 sections (document, sysvars, layers, linetypes, textStyles, dimStyles, blocks, xrefs, layouts, annotationScales, extents, selection), none with `error`; command line idle; no undo step added | |
| T2 | Revit bridge | Open a sample model. `sa revit ping`, `sa revit snapshot --out C:\sa-test\t2.json` | 22 sections, `identity` present, any failing section listed in `errors` without stopping the rest; Revit responsive; no undo step | |
| T3 | Wrong document | AutoCAD: snapshot drawing A, open drawing B and make it active, commit a J-A-WALLS job for A. Then make A active, draw one line by hand, commit the same job again. Revit: see CR-09 | AutoCAD: first run `wrong_document`, second `document_changed`; nothing drawn in either. Revit: a job binds to the model named by path even when another is active (CR-09) | |
| T4 | One undo step | J-A-WALLS (commit); look at the Undo list; press U once. Revit: J-R-WALLS, Undo once | One entry named `SA: Twenty walls`; one U removes every wall line and the new `A-WALL` layer together. Revit: one undo item `SA: <title>`; one Undo removes all 20 walls | |
| T5 | Failure rollback | Snapshot (note counts in `layers` and `extents`), run J-A-FAIL (commit). Revit: snapshot `categories`, run J-R-FAIL | AutoCAD: `status: rolled_back`, `error.code` `rolled_back`, the failing check named; snapshot counts identical; no undo step with content. Revit: `rolled_back`, wall and grid counts identical | |
| T6 | No double apply | Commit a job, then run the same file again (commit). Then rehearse a new job, commit it, rehearse it again | Second commit refused with `duplicate_job`, nothing changed; rehearse-then-commit of one id works | |
| T7 | Dialog safety | Revit: J-R-WARN (commit). AutoCAD: J-A-PLOT (commit) | Revit: `committed`, the overlap warning in `warnings` with source `app`, no warning dialog shown, Revit not frozen. AutoCAD: PDF written, no plot dialog or progress window left open, `BACKGROUNDPLOT` back to its old value | |
| T8 | Speed | 100 walls: J-A-WALLS with 25 chains; J-R-WALLS with 25 ops. Time from Enter to report; also read `timingMs` | Times recorded per app and release. Target under 30 s; you judge | |
| T9 | Units | J-A-WALLS on a metric drawing (`acadiso.dwt`) and on an imperial one (`acad.dwt`, job units `in`, values in inches). Revit: J-R-WALLS on a metric and an imperial template | Lengths in readback and in the app equal the request in each file's own units (measure one wall with DIST / a dimension) | |
| T10 | Audit finds planted defects | Section 5 | Exactly the counts in the spec's "Expected results" per check | |
| T11 | Fix | AutoCAD on P1: `fix.bylayer`, `fix.flattenZ`, `fix.purge`, `layer.translate`, then audit again. Revit on R1: `cleanup.unplacedRooms` and `cleanup.unusedViews` (first with `dryRun: true`), then audit. Revit purge is wave 2 | Fixed checks at 0; all other counts unchanged; readback `before`/`after` counts match what you see | |
| T12 | Setup from a standard | AutoCAD blank drawing: `doc.setup` with `applyStandard: true`, once per preset (`data/standards/us-ncs-style.json`, `iso19650-uk-style.json`). Revit blank model: `level.ensure`, `grid.create` | Layers, linetypes, text and dimension styles match the preset; levels and grids as requested, grid heads at top and left (CR-18) | |
| T13 | Walls and openings | AutoCAD: `wall.draw` (with an L, a T and an X junction) then `opening.place` door and window. Revit: `wall.create`, `door.place`, `window.place` | AutoCAD: no line crossing a junction, wall faces cut at the jambs; Revit: openings hosted at the requested offset (CR-14); counts in the report match the model | |
| T14 | Rooms and schedules | AutoCAD: `room.create` for two rooms, `schedule.fromBlocks` to a table and CSV. Revit: `room.place` for two rooms, `schedule.create` | Areas match a hand check (AutoCAD AREA, Revit room properties) within 0.5 %; schedule rows match the model | |
| T15 | Sheets | AutoCAD: `layout.create` (title block), `viewports.layout`, `titleblock.fill` in one job; then J-A-PLOT for those layouts in a **separate** job. Revit: `sheet.create`, `viewports.layout` in one job; then J-R-PDF in a separate job | Viewports inside the drawable area at the asked scale, no overlaps; PDFs named to the rule, one per sheet, at the sheet's paper size | |
| T16 | Annotation scale | AutoCAD: `doc.setup` scales 1:50 and 1:100, `text.add` annotative with `plottedHeight: 2.5`, two viewports at 1:50 and 1:100 (`viewport.create`), plot both | Text measures 2.5 mm on paper in both viewports (CA-17) | |
| T17 | Dimensions | AutoCAD: `dim.linear` and `dim.string`. Revit: `dim.grids`. Revit wall dimension strings are wave 2 (R47) | Values equal the measured distance; no text override; Revit witness lines on every grid (CR-27) | |
| T18 | Workshared model | Local copy of a workshared model; a second user takes ownership of one wall; run `param.set` on that wall | `not_owned` listing the owner and workset; nothing changed; no sync, borrow or relinquish (CR-24) | |
| T19 | Families and parameters | `family.load` (new family, then the same again, then with `reload: true`), `type.duplicate`, `param.shared.ensure` (CR-04), `param.set` | Every change read back; unchanged family reported unchanged; reload keeps values. Family editing (R14) has no op in wave 1 | |
| T20 | Xrefs and blocks | `block.define`, `block.insert` of a dynamic block, `block.dynprops.set`; `xref.attach` (overlay, relative); rename the xref file's folder, then `xref.repath` with `searchFolders` | Dynamic parameter set; path stored relative; repathed reference resolves; a missing one reported unresolved and left unchanged | |
| T21 | Batch | Not built: no batch op in wave 1. Drop for now | | |
| T22 | Version spread | T1, T2, T4, T13, T15 on the other release(s) | Same results; differences noted with release | |
| T23 | Escape hatch | AutoLISP: CA-01. Python: not built in wave 1 (Revit has no script op). Drop the Python half | See CA-01 | |
| T24 | Second tier | A7 stairs, A19 sheet sets, A26 compare, R8 stairs, R12 model from CAD, R14 family edit, R29 clash, F1-F3: no ops or scripts exist yet. Drop until built | | |
| T25 | Skill triggering | Not yet: `SKILL.md` is not assembled. Run after skill assembly: ten requests, half unrelated to CAD | The skill loads for the CAD ones only | |

---

## 4. Confirm in the app

Merged from `engine/AUTOCAD-HOST-REPORT.md` §7, `engine/REVIT-HOST-REPORT.md` §11, `engine/reports/*.md`,
`engine/reports/review/*.md` (Resolution sections and deferred items), `engine/reports/review/OWNER-DECISIONS.md` and
`engine/reports/review/HANDOFF-*.md`. Source short names: AH = AutoCAD host report §7, AD = AutoCAD drafting report,
AS = AutoCAD sheets report, AA = AutoCAD audit report §5, RH = Revit host report §11, RM = Revit modelling report,
RD = Revit docs report §6, RO = Revit output and audit report §6, RV-AH/RV-AO/RV-RH/RV-RO = the review files'
finding ids, OD = OWNER-DECISIONS item.

"Both" in the release column means 2024 and 2027 first, then 2025 and 2026.

### 4.1 Install and load (both apps)

**CX-01 · Installers on Windows** · both apps · all releases · OD 17, INTEGRATION-TODO
- Steps: run 1.3. Run it again (re-install). Run `-Uninstall`. Then install again.
- Expect: each run prints what it did per release; re-install leaves one copy of everything (no `checks\checks`);
  uninstall removes the bundle, manifest, engine, `bin` and `data` and leaves `logs`, `sessions`, `ledger`. The
  AutoCAD installer refuses while AutoCAD runs.
- Settles: STATUS.md "Installers" row; `engine/install/*.ps1`.
- Result:

**CX-02 · .NET runtime and release mapping** · both · 2025 and 2026 later updates (on .NET 10), 2027 · AH 13, AH 18, RH 9, ARCHITECTURE §3
- Steps: on each release and update you have, start the app, `sa <app> ping`.
- Expect: engine loads; `ping` shows the right release year; Revit finds `SA.Core.dll` beside `SA.Revit.dll`
  (no `FileNotFoundException` in the log).
- Settles: STATUS.md "Known assumptions" (net8 builds on .NET 10 updates). Record the update numbers tested.
- Result:

**CX-03 · Bundle and manifest details** · both · all · AH 14, RH 15
- Steps: AutoCAD: check the log names only that release's DLL. Revit 2027: confirm the engine loads from
  `%APPDATA%\Autodesk\Revit\Addins\2027`. If Windows blocks the unsigned DLL (Smart App Control), note the message.
- Expect: AutoCAD loads at start-up, one DLL per release; Revit 2027 reads the per-user manifest folder.
- Settles: REVIT-HOST-REPORT §11.15 (2027 folder; whether signing is needed); `Install-AutoCAD.ps1` PackageContents.
- Result:

**CX-04 · Session file on exit and crash** · both · one release · AH 17
- Steps: close the app normally; check `sessions\`. Start it again, end it in Task Manager; run `sa sessions`.
- Expect: normal exit deletes the file; after the kill a stale file remains and `sa sessions` does not list it.
- Settles: AUTOCAD-HOST-REPORT §7.17.
- Result:

### 4.2 AutoCAD

**CA-01 · script.lisp evaluation (run first)** · AutoCAD · 2024, 2027, then 2025, 2026 · AD 1, AH 7, RV-AH AH-02
- Steps: save everything, scratch drawing from `acadiso.dwt`. J-A-LISP: `sa acad validate job.json --allow-script`,
  then `run --rehearse --allow-script`, then `run --allow-script`. New snapshot, then `t-acad-lisp-02` the same way.
  Press U once.
- Expect: validate ok with a `script_not_rehearsed` warning; rehearsal `rolled_back` and evaluates nothing (no line
  after the second rehearsal); commit of 01 returns value 3; commit of 02 draws one line 1000 long; AutoCAD does
  not crash; one U removes the line.
- If AutoCAD crashes: stop, send 1.7, do not repeat on other releases until fixed.
- Settles: AH-02. A crash means `script.lisp` is disabled (answers `unsupported_in_release`) until moved into a
  registered command (`Ops/ScriptLisp.cs`, `Compat/LispBridge.cs`); STATUS.md.
- Result:

**CA-02 · script.lisp refusals** · AutoCAD · one release · RV-AH AH-01, AH-09, AH-15, HANDOFF-AUTOCAD
- Steps: `validate --allow-script` each of: (a) J-A-LISP plus a `layer.ensure` op; (b) J-A-LISP with
  `"verify": {"checks": ["ac-001.layer-zero"]}`; (e) code `(command "LINE")`. Validate J-A-LISP without
  `--allow-script` (d). Commit a new job id with `run --allow-script` without rehearsing it first (c).
  `sa job lint` on (a) and (b).
- Expect: (a) `script_not_alone`; (b) `script_with_checks`; (c) `script_not_rehearsed`; (d) `script_not_allowed`;
  (e) refused at validation. Lint passes (a) and (b) (Core lint does not check them yet).
- Settles: HANDOFF-AUTOCAD "Lint script_not_alone and script_with_checks".
- Result:

**CA-03 · plot.pdf alone** · AutoCAD · 2024, 2027 · AS plot.pdf 1-5, AD 18, RV-AO AO-07, OD 14
- Steps: drawing with two paper layouts. (a) validate J-A-PLOT with a `layer.ensure` op added. (b) J-A-PLOT
  rehearse; look in `C:\sa-test\plot` and `%TEMP%\SA-rehearse`. (c) commit. (d) new id, both layouts,
  `"combine": true`. (e) repeat (c) under a new id without `overwrite`. Check `BACKGROUNDPLOT` and the current layout
  before and after each run.
- Expect: (a) `plot_not_alone`; (b) report lists the paths the commit would write, no file in either folder;
  (c) `committed`, file starts `%PDF-`, at the layout's paper size; (d) one PDF with two pages; (e) refused
  because the file exists. AutoCAD responsive throughout, current layout and `BACKGROUNDPLOT` unchanged, layout plot
  settings unchanged.
- Not testable with this build: whether plotting inside an open job sees uncommitted objects (AO-07). That needs a
  test build that allows a mixed job.
- Settles: AUTOCAD-SHEETS-REPORT plot.pdf items 2-5; OD 14 stays until a test build checks AO-07.
- Result:

**CA-04 · capture** · AutoCAD · 2024, 2027 · AH 6, AD 6, RV-AH AH-05, AH-12
- Steps: drawing in model space with a layout `L1`. Snapshot `document`. `sa acad capture --target layout:L1 --out C:\sa-test\cap.pdf`,
  then `--target model --out C:\sa-test\cap.png`. Snapshot again. Check the Undo list and whether closing asks to save.
- Expect: both files exist (PNG with pixel size); model space still current; fingerprint unchanged; no `SA: capture`
  undo step. Record whether DBMOD changed (save prompt): not known.
- Settles: AH-05; AUTOCAD-HOST-REPORT §7.6 (device names `DWG To PDF.pc3`, `PublishToWeb PNG.pc3`).
- Result:

**CA-05 · Main-thread queue, busy and timeouts** · AutoCAD · 2024, 2027 · AH 3, AUTOCAD-HOST-REPORT §2
- Steps: (a) open the OPTIONS dialog and leave it open; `sa acad ping`; `sa acad snapshot`; wait. (b) still open,
  commit a J-A-WALLS job; wait over 120 s; `sa acad status <jobId>`; close the dialog; check the drawing. (c) start
  the LINE command and leave it waiting for a point; `sa acad snapshot`; press Esc within 45 s. (d) run a 100-wall
  job with `--timeout 1`, then `sa acad status <jobId>` until done.
- Expect: (a) ping answers at once; snapshot answers `timeout` after about 45 s with `details.started: false`;
  (b) `timeout` after about 120 s, status `unknown`, no walls appear after closing the dialog; (c) snapshot answers
  after Esc; (d) exit code 5 with "Do not send it again", then status `committed`.
- Settles: AUTOCAD-HOST-REPORT §7.3 (Idle drain, WM_NULL wake).
- Result:

**CA-06 · Pipe security** · AutoCAD · 2024 (net48), 2027 · AH 4, AD 7, RV-AH AH-13
- Steps: `sa acad ping` as the same user. Then as a second Windows user: copy the session file into that user's
  `%LOCALAPPDATA%\CadBim\sessions\` so the client finds the pipe, and run `sa.exe acad ping` there. Run
  AutoCAD elevated once and `sa acad ping` from a normal terminal.
- Expect: same user connects; the other user is refused (connection error, exit 4); the engine never rejects its own
  pipe. Elevated case: record what happens.
- Settles: AUTOCAD-HOST-REPORT §7.4.
- Result:

**CA-07 · One undo step; rehearsal leaves no trace** · AutoCAD · 2024, 2027 · AH 1-2, AD 2, RV-AH AH-04
- Steps: T4 first. Then rehearse J-A-WALLS (new id) with a layer name not in the drawing; check the layer list,
  entity count (snapshot `extents`, `layers`) and the Undo list.
- Expect: rehearsal `rolled_back` with no `rehearsalLeaked`; no new layer or entity; Undo list empty or one empty
  step labelled "... (rehearsal)".
- If every rehearsal reports a leak: the leak detector is wrong (`JobRunner.CheckRehearsal`).
- Settles: AUTOCAD-HOST-REPORT §7.1-7.2; AH-04.
- Result:

**CA-08 · No false "document changed"** · AutoCAD · 2024, 2027 · AH 5
- Steps: snapshot; then, one at a time with a fresh snapshot between: save, REGEN, switch to a layout and back,
  zoom and pan, select and deselect objects; after each, `validate` a job built on the earlier snapshot.
- Expect: validate passes after each (fingerprint unchanged). Note any action that changes it.
- Settles: AUTOCAD-HOST-REPORT §7.5; `Host/DocumentIdentity.cs` ignore list.
- Result:

**CA-09 · Ledger and status** · AutoCAD · one release · RV-AH AH-07, AH-08, HANDOFF-AUTOCAD
- Steps: commit a job; restart AutoCAD; `sa acad status <jobId>`. Open two AutoCAD sessions; commit the same new
  job id in each with `--pid`.
- Expect: status after restart `committed` from the ledger; the second session answers `duplicate_job`.
- Settles: AH-07, AH-08.
- Result:

**CA-10 · Header variables after a rehearsal** · AutoCAD · 2024, 2027 · AD 3, AS fix.units 1
- Steps: note INSUNITS, LTSCALE, ANGBASE, CLAYER. Rehearse `doc.setup` changing `insUnits`, `ltScale`, `angleBase`;
  rehearse `fix.units` with `scaleContents: false`. Read the variables again.
- Expect: all unchanged; both rehearsals `rolled_back` without `rehearsalLeaked`.
- Settles: AUTOCAD-DRAFTING-REPORT item 3; AUTOCAD-SHEETS-REPORT fix.units 1.
- Result:

**CA-11 · DIMBLK restore** · AutoCAD · one release · AD 4, RV-AO AO-09
- Steps: drawing where `DIMBLK` reads empty. Rehearse `dimstyle.ensure` with a built-in arrow `_DOT`. Read `DIMBLK`;
  check the block list for `_DOT`.
- Expect: `DIMBLK` empty again. Whether the arrow block survives: not known (the leak detector reports it if so).
- Settles: AO-09.
- Result:

**CA-12 · xref.attach rehearsal** · AutoCAD · 2024, 2027 · AS xref.attach 1-3, RV-AO "not verifiable"
- Steps: saved host drawing and `x.dwg` beside it. Rehearse `xref.attach` of `x.dwg`; open the External References
  palette. Then commit it.
- Expect: after rehearsal no xref listed and no `rehearsalLeaked`; after commit the xref is Resolved, stored path
  `.\x.dwg`, overlay.
- Settles: the biggest rehearsal risk in the sheets set; if it leaks, xref.attach must stop rehearsing for real.
- Result:

**CA-13 · layout.create** · AutoCAD · 2024, 2027 · AS layout.create 1-6, AD 20, OD 15, OD 29, RV-AO "not verifiable"
- Steps: (a) rehearse a `layout.create` with `titleBlock`; check the tabs. (b) commit with `makeCurrent: true`;
  open the tab; plot it (CA-03). (c) press U once. (d) a layout in a metre drawing with a non-1:1 page setup.
- Expect: (a) no new tab; (b) a working tab with paper viewport, device, media, style table as asked, report
  `extra.currentLayout`, editor on the new layout; (c) one U removes the switch and the layout; (d) plot scale the
  right way round.
- Settles: AUTOCAD-SHEETS-REPORT layout.create 1-6; OD 15/29.
- Result:

**CA-14 · viewport.create and viewports.layout** · AutoCAD · both · AS viewport.create 1-5, AS viewports.layout 2, AD 9
- Steps: on a layout that is not current, `viewport.create` at 1:50 with two frozen layers; rehearse first. Then
  `viewports.layout` with titles; and once on a layout with no title block, without `area`.
- Expect: viewport on and showing the asked model window at 1:50 when the tab is opened; layers frozen only there;
  rehearsal leaves no viewport and no freeze; titles under each viewport; without `area` the layout's limits are used.
- Settles: AUTOCAD-SHEETS-REPORT viewport.create 1-5; AUTOCAD-DRAFTING-REPORT item 9.
- Result:

**CA-15 · Unitless drawing** · AutoCAD · one release · AUTOCAD-HOST-REPORT §3
- Steps: drawing with `INSUNITS` 0. Validate J-A-WALLS. Then a job with only `fix.units` (`units: "mm"`).
- Expect: walls refused with `units_unitless`; `fix.units` allowed.
- Settles: unitless rule.
- Result:

**CA-16 · Paper units** · AutoCAD · one release · AD 22, OD 12, OD 28
- Steps: job units `m` on a millimetre drawing, A3 layout: `viewport.create` `paperCentre [200,150]`, `width 180`,
  `height 120`.
- Expect: viewport centred at 200 mm, 150 mm on the sheet, 180 x 120 mm.
- Settles: OD 28.
- Result:

**CA-17 · Annotative text** · AutoCAD · both · AH 8, AH 9, AA 2, RV-AO AO-01/02
- Steps: `text.add` DBText and MText, annotative, `plottedHeight 2.5`, scale 1:100; also justified DBText.
- Expect: one scale (1:100) on each; model height 250 mm; justification as asked; T16 plots 2.5 mm.
- Settles: AUTOCAD-HOST-REPORT §7.8-7.9; AUTOCAD-AUDIT-REPORT item 2.
- Result:

**CA-18 · mleader.add height** · AutoCAD · one release · AD 8, RV-AO AO-05
- Steps: leader with a non-annotative style of scale 2, then an annotative style. REGEN.
- Expect: text at the requested height; `leaderTextHeight` x `leaderScale` equals it; unchanged after REGEN.
- Settles: AO-05.
- Result:

**CA-19 · Dimensions** · AutoCAD · one release · AD 12
- Steps: `dim.linear` horizontal, vertical, aligned; `dim.string`; once annotative, once not.
- Expect: measured values equal the distances; dimension lines where asked; `associative: false` in readback.
- Settles: AUTOCAD-DRAFTING-REPORT item 12.
- Result:

**CA-20 · opening.place and wall hatch** · AutoCAD · one release · AD 11, AH 11, RV-AO AO-12
- Steps: `wall.draw` with `hatch` ANSI31; `opening.place` a door; move and rotate the door by hand; place another
  opening overlapping the door's new position.
- Expect: hatch follows the wall pieces at the given scale and angle; the second opening is refused as overlapping.
- Settles: AO-12; AUTOCAD-HOST-REPORT §7.11.
- Result:

**CA-21 · room.create with an arc** · AutoCAD · one release · AD 17, RV-AO AO-14
- Steps: room closed by an arc wall; `room.create` with `inside`; compare with AREA of the same boundary.
- Expect: area slightly under AREA's (10° chords); difference recorded.
- Settles: AUTOCAD-DRAFTING-REPORT item 17.
- Result:

**CA-22 · block.define fromFile** · AutoCAD · one release · AD 10, RV-AO AO-16
- Steps: title block drawing with INSBASE off the origin and centred attributes; `block.define` with `fromFile`.
- Expect: base point and attribute justification as in the source.
- Settles: AO-16.
- Result:

**CA-23 · Dynamic block properties** · AutoCAD · one release · AD 13, RV-AO "not verifiable"
- Steps: `block.dynprops.set` inside range on a reference scaled 2; then outside a parameter's range.
- Expect: in range: value as asked on the scaled reference; out of range: op fails, job rolled back.
- Settles: AUTOCAD-DRAFTING-REPORT item 13; DynProps X-scale rule.
- Result:

**CA-24 · titleblock.fill** · AutoCAD · one release · AS titleblock.fill 1-2
- Steps: fill a multiline attribute and plain ones; look without REGEN.
- Expect: readback passes (no added formatting codes); shown updated at once.
- Settles: AUTOCAD-SHEETS-REPORT titleblock.fill.
- Result:

**CA-25 · table.create and schedule.fromBlocks** · AutoCAD · one release · AD 16, RV-AO AO-15
- Steps: table with a merged title row; schedule to CSV; rehearse the schedule first.
- Expect: row heights sensible, title merged; CSV opens in a spreadsheet with correct characters (²); rehearsal
  leaves no CSV.
- Settles: AUTOCAD-DRAFTING-REPORT item 16; AO-15.
- Result:

**CA-26 · grid.draw** · AutoCAD · one release · AD 15
- Steps: grid at 1:100 and at 1:50.
- Expect: bubble text half the bubble diameter; lines end at the bubble edge.
- Result:

**CA-27 · Linetypes built in the transaction** · AutoCAD · one release · AH 10, AD 14
- Steps: `linetype.ensure` DASHED and GAS_LINE in one drawing; LINETYPE-load the same in another; compare.
- Expect: identical display.
- Result:

**CA-28 · TrueType text styles** · AutoCAD · one release · AH 12
- Steps: `textstyle.ensure` with a TrueType family, an SHX file, a missing font, and annotative.
- Expect: TrueType and SHX styles display; missing font refused cleanly; annotative style flagged in STYLE dialog.
- Result:

**CA-29 · xref.repath** · AutoCAD · one release · AS xref.repath 1-2
- Steps: rehearse a repath (check the stored path afterwards); commit with `reload: true`; U once.
- Expect: rehearsal leaves the path; commit resolves it; one U undoes the repath.
- Result:

**CA-30 · layer.translate** · AutoCAD · one release · AS layer.translate 1-2, RV-AH AH-03
- Steps: `layer.ensure` TMP, put lines on it, then in one job `layer.translate` TMP to A-WALL with
  `deleteEmptied: true`; also a source layer frozen in a viewport.
- Expect: TMP deleted, job `committed` (not failed); frozen-in-viewport layer kept with a reason.
- Result:

**CA-31 · fix.purge and purgeable counts** · AutoCAD · 2024, 2027 · AD 21, AA 3, OD 16, OD 27, HANDOFF-CORE
- Steps: drawing with an unused material, an unused visual style, unused blocks, a block whose only content uses a
  layer. `sa acad audit --checks ac-020.purge --out a.json`; open PURGE and compare. `fix.purge` `"all"` with
  `repeat: true`.
- Expect: `purgeableByKind` matches PURGE's list per kind (visual styles: compare and record); a material used by a
  layer kept; visual styles never erased; after the fix the layer freed by the erased block is gone.
- Settles: OD 16 (test-plan item), FACTS.md 1.1 visual style exclusions.
- Result:

**CA-32 · fix.bylayer and entity.setprops** · AutoCAD · one release · AS fix.bylayer, AS entity.setprops
- Steps: entities with transparency ByBlock/explicit; `fix.bylayer`. `entity.setprops` with filter type `DIMENSION`
  on aligned, rotated, radial and angular dimensions.
- Expect: Properties palette shows ByLayer after the fix; every dimension subtype matched.
- Result:

**CA-33 · fix.flattenZ** · AutoCAD · one release · AS fix.flattenZ 1-2, RV-AO AO-04
- Steps: dimension and associative hatch at Z 100; block reference with attributes at Z 100; `fix.flattenZ`.
- Expect: all at Z 0 (LIST); hatch still associative; attributes moved with the block.
- Result:

**CA-34 · fix.units scaling** · AutoCAD · one release · AS fix.units 2-3, RV-AO AO-03
- Steps: inch drawing with non-annotative dimensions, hatch and dashed lines; `fix.units` to mm with `scaleContents`.
- Expect: geometry x25.4; dimension text and arrows, hatch scale and linetype look as before; anything not scaled
  listed under `notScaled`.
- Result:

**CA-35 · Snapshot details** · AutoCAD · one release · AH 15-16, AA 1, RV-AH AH-10
- Steps: select three objects, `sa acad snapshot --sections selection,layouts,xrefs`; check the selection is still
  there. Layout never activated with two viewports; layout with an unresolved xref.
- Expect: selection lists the three handles and is unchanged; viewports per layout exclude the overall viewport;
  paper size in the layout's paper units; unresolved xref shows its status.
- Result:

**CA-36 · Audit facts** · AutoCAD · one release · AA 5-8, AA 10
- Steps: a twisted viewport over non-annotative model text; arcs with normal (0,0,-1) (from a DXF); a table with cell
  styles; a raster image with IMAGEFRAME 0, 1, 2; a layout never set up. `sa acad audit --out a.json`; read the
  findings (sa.exe cannot ask for the raw facts).
- Expect: ac-014 ratio from the viewport; ac-027 lists the flipped-normal arcs (by design); table text styles
  found; ac-037 matches IMAGEFRAME; ac-036 does not flag the never-set-up layout. Wrong results: note which.
- Result:

**CA-37 · Large drawing** · AutoCAD · 2027 · AA 9, AD 19, OD 26
- Steps: drawing with about 200,000 entities. `sa acad audit --out a.json` (time it, watch AutoCAD's memory).
  Commit a small job with `"verify": {"checks": ["ac-001.layer-zero"]}`.
- Expect: audit completes (time and memory recorded); verify passes or fails without `incomplete`; memory rise about
  1 KB per entity at most. The `INCOMPLETE CHECK` half needs a test build with a low ceiling: not testable by hand.
- Settles: OD 26 ceilings.
- Result:

**CA-38 · Script job that ran and then failed** · AutoCAD · n/a · AD 5, RV-AH AH-01
- Not testable by hand: no way to make a script job fail after its code ran. Listed so it is not forgotten.

### 4.3 Revit

**CR-01 · Staged exports and rehearsal** · Revit · 2024, 2027 · RV-RH RH-01, RO 1, RH 2, H2
- Steps: (a) J-R-PDF rehearse; look in `C:\sa-test\pdf` and `%TEMP%`. (b) commit the same file. (c) new id, same
  sheet, no `overwrite`. (d) new id with `"overwrite": true`. (e) the same with `export.dwg` (one sheet) and
  `export.image` (`pixelWidth 1600`).
- Expect: (a) `rolled_back`, no artifacts, no file in the folder, staging deleted; (b) `committed`, file starts
  `%PDF-`; (c) refused, file exists; (d) file replaced; (e) `.dwg` starts `AC10`, image 1600 px wide within 2 px.
  An export-only job opens no transaction group (nothing in the Undo list).
- Settles: RH-01; REVIT-OUTPUT-AUDIT-REPORT §6.1 for export-only jobs.
- Result:

**CR-02 · Export inside a modelling job** · Revit · 2024, 2027 · RH 2, RO 1, RO 19
- Steps: one job: `wall.create` then `export.pdf` of a sheet showing that level's plan; commit. Then the same with a
  failing verify (add `"verify": {"checks": ["rv-017.datums-pinned"]}` and a `grid.create`).
- Expect: either the PDF is written after the job commits and shows the new wall, or the op fails with
  `export_refused` and the job rolls back. Which one: not known. With the failing check: no PDF published.
- Settles: REVIT-HOST-REPORT §11.2; whether skill guidance must keep exports in their own job.
- Result:

**CR-03 · IFC export** · Revit · 2024, 2026, 2027 · RO 2, RO 8, RV-RH RH-10
- Steps: 3D view; `export.ifc` with `version` `ifc2x3`, `ifc4`, `ifc4x3` (three jobs); check the Undo list.
- Expect: files start `ISO-10303-21`; readback `schema` in the right family and `modelView` reported; model
  unchanged; at most one empty undo item. Record the `FILE_SCHEMA` strings and the `modelView` per release (2027
  Reference View vs 2024-2026): exact strings not known.
- Settles: REVIT-OUTPUT-AUDIT-REPORT §6.2, §6.8; RH-10.
- Result:

**CR-04 · Shared parameter file staging** · Revit · 2024, 2027 · RV-RO RO-09, RM param.shared.ensure 1-3, HANDOFF-REVIT
- Steps: create `C:\sa-test\sa-params.txt` with Manage > Shared Parameters > Create. `Get-FileHash` it. Job:
  `param.shared.ensure` `file` that path, `group` "SA Test", parameter `SA_Test_Text` `text`, categories
  `["OST_Walls"]`. (a) rehearse; hash. (b) commit with a failing verify (add `grid.create` and `rv-017.datums-pinned`);
  hash. (c) commit alone (new id); hash. Check Manage > Shared Parameters still shows your original file each time.
- Expect: (a) and (b) hash unchanged; (c) file now holds `SA_Test_Text`, Walls carry the parameter; Revit's shared
  parameter file setting unchanged after every run. In a non-English UI `propertyGroup` "Data" fails unless a
  GroupTypeId is given (note the UI language).
- Settles: RO-09; HANDOFF-REVIT guidance line.
- Result:

**CR-05 · Dialogs and failures** · Revit · 2024, 2027 · RH 3, RH 17, RO 19, RV-RH RH-12
- Steps: T7 (J-R-WARN). Then Manage > Review Warnings; `sa revit snapshot --sections warnings`. Note any dialog that
  appears in any test.
- Expect: no warning balloon or dialog during the job; the warning stays in Review Warnings and in the snapshot
  count; any dismissed dialog is listed in the report's `dialogs` and fails the job.
- Decide: whether failing the whole job on any dialog (for example one raised by export) is too strict. Record the
  dialogs seen.
- Settles: REVIT-HOST-REPORT §11.3, §11.17.
- Result:

**CR-06 · External event, busy and timeouts** · Revit · 2024, 2027 · RH 7, RV-RH RH-02
- Steps: (a) open Options (modal); `sa revit ping`; commit a J-R-WALLS job; while waiting, `sa revit status <jobId>`
  from a second terminal; wait over 60 s; close Options. (b) enter floor sketch mode; `sa revit snapshot`; finish
  the sketch within 60 s. (c) 100-wall job with `--timeout 1`, then `status` until done.
- Expect: (a) ping answers at once; status shows the job queued or running, never "nothing was committed"; after
  about 60 s `timeout` with `details.started: false`; no walls appear after closing. (b) snapshot answers after the
  sketch ends. (c) exit 5, then `committed`.
- Settles: REVIT-HOST-REPORT §11.7; RH-02.
- Result:

**CR-07 · Pipe on Windows** · Revit · 2024 (net48), 2027 · RH 8, RV-RH RH-11
- Steps: as CA-06 (same user, second user, Revit elevated with `sa` not elevated). Close Revit and check the log for
  shutdown errors.
- Expect: same user connects, other user refused; clean shutdown. Elevated case: record (mandatory-label behaviour
  untested). The 60 s write deadline needs a client that stops reading: not testable by hand.
- Settles: REVIT-HOST-REPORT §11.8.
- Result:

**CR-08 · One undo step and change tracking** · Revit · 2024, 2027 · RH 1, RH 6
- Steps: T4. Rehearse J-R-WALLS, then commit the same file without a new snapshot. Read both reports' `warnings`.
- Expect: one undo item; the commit after the rehearsal is accepted (no `document_changed`); no warning "No
  DocumentChanged event arrived" except on `export.ifc` ops.
- Settles: REVIT-HOST-REPORT §11.1, §11.6.
- Result:

**CR-09 · Document binding** · Revit · 2024, 2027 · T3, RH 10, RV-RH RH-03, RH-07
- Steps: open models A and B (also try two files named `model.rvt` in different folders). Snapshot A; make B active;
  commit a job for A. `sa revit ping` after editing B. Snapshot A, edit A by hand, commit a job on the old snapshot.
  Workshared copy: Reload Latest, then validate a job on the earlier snapshot.
- Expect: the job runs in A (walls appear in A, not B); ping names the real active model; edited A gives
  `document_changed`. Reload Latest: record whether it changes the fingerprint (not known).
- Settles: REVIT-HOST-REPORT §11.10; RH-03 note.
- Result:

**CR-10 · Ledger and status** · Revit · one release · RV-RH RH-06, RH-13
- Steps: as CA-09 (restart then status; same id twice). Also job ids `x` (rehearse) and `x.rehearse` (commit).
- Expect: `committed` after restart; second commit `duplicate_job`; the two ids do not collide.
- Result:

**CR-11 · Level elevations with a raised base point** · Revit · 2024, 2027 · RH 4, RV-RO RO-07 (deferred)
- Steps: model whose project base point is at Z = 1000 mm. `level.ensure` `Level X` at elevation 3000.
- Expect: the level shows 3000 relative to the project base point and readback passes. If readback fails, send the
  report: it holds both readings.
- Settles: RO-07.
- Result:

**CR-12 · Roof edge offset** · Revit · 2024, 2027 · RM roof.footprint 1-3, RV-RO RO-08 (deferred)
- Steps: `roof.footprint` on a 10 x 6 m footprint, slope 30°, overhang 600; once all hipped, once with two gable
  edges (mixed slopes).
- Expect: eaves 600 out, plate line at the footprint at level + `baseOffset`; gables read correctly. Sketch the roof
  and compare edge offsets by hand.
- Settles: RO-08.
- Result:

**CR-13 · Room states and phases** · Revit · 2024, 2027 · RM room.place 1-4, RM cleanup.unplacedRooms, RH 16, RV-RO RO-01, OD 6, OD 18
- Steps: place a room in an enclosed space; a second at a point in the same space; one in an open area with and
  without `allowNotEnclosed`; one with `phase` set to an earlier phase; `allEnclosed` on a level with a tiny closet.
  Then `cleanup.unplacedRooms` on an unplaced, a not-enclosed and a redundant room.
- Expect: second room refused (`redundant_room` or point already in a room); open area refused unless
  `allowNotEnclosed` (last phase only); earlier phase room in that phase; regions under 0.5 m² skipped and listed;
  cleanup removes only the states allowed by its flags.
- Settles: the room-state rule (RM top risk 1).
- Result:

**CR-14 · Door and window placement** · Revit · 2024, 2027 · RM door.place 1-5
- Steps: `door.place` at an offset on a wall with a base offset and a top constrained to a level; `window.place`
  with and without `sillHeight`; a family without width parameters.
- Expect: insert exactly at the offset (within 0.5 mm) or the op fails; sill as asked or the family default; the
  width-less family warns before placement.
- Settles: RM top risk 2.
- Result:

**CR-15 · Wall location line** · Revit · one release · RH 5
- Steps: `wall.create` with `locationLine` `finish_face_exterior`, once flipped; two walls joined at a corner.
- Expect: the chosen face lies on the points; joins do not move end points (if they do, the op fails: a false
  negative, note it).
- Result:

**CR-16 · Floors and ceilings** · Revit · one release · RM floor.create 1-2, RM ceiling.create 1
- Steps: `floor.create` `fromRoom` on a room with an arc wall; floor edges joined to walls; `ceiling.create` at 2700.
- Expect: area within 0.5 % of the sketch; ceiling at 2700 without a regenerate.
- Result:

**CR-17 · walltype.ensure** · Revit · one release · RM walltype.ensure 1-3
- Steps: new type with a new material, core, shell layers, no end wrapping.
- Expect: type created, material created, build-up read back; no error at the structural material index.
- Result:

**CR-18 · grid.create** · Revit · one release · RM grid.create 1-2
- Steps: orthogonal 3 x 2 grid; then a job naming an existing grid.
- Expect: heads at top and left; existing name refused at validation.
- Result:

**CR-19 · room.separator** · Revit · one release · RM room.separator 1-2
- Steps: separator across a room; a second overlapping it; a wall from another level passing through.
- Expect: first placed and splits the room; overlap refused; record whether the other-level wall is ignored.
- Result:

**CR-20 · family.load** · Revit · one release · RM family.load 1-2, RV-RO RO-02
- Steps: load a family; load it again; load a changed copy without and with `reload: true`; a family with nested
  shared families.
- Expect: second load reported unchanged; changed copy without reload fails `family_differs`; with reload values
  kept; no dialog.
- Result:

**CR-21 · type.duplicate** · Revit · one release · RM type.duplicate 1, RV-RO RO-10
- Steps: duplicate a Basic Wall type to a name that exists in Stacked Wall.
- Expect: refused as `exists`, or accepted if Revit allows it; record which Revit allows by hand.
- Result:

**CR-22 · element.delete** · Revit · one release · RM element.delete 1-2
- Steps: delete a wall with a door (`allowDependents` false, then true); a floor; a stair; a view.
- Expect: dependents refused, then listed and deleted; no `unexpected_dependents` for sketch parts.
- Result:

**CR-23 · cleanup.unusedViews** · Revit · one release · RD 9, OD 9, OD 24
- Steps: `dryRun: true`, then for real, on views with section and callout markers on sheeted views.
- Expect: dry run lists `wouldDelete` and deletes nothing; real run deletes them without `unexpected_deletion`.
- Result:

**CR-24 · Worksharing** · Revit · 2024, 2027 · RH 12, RM workset.assign 1-2, T18
- Steps: T18; `workset.assign` of an element into a workset another user made editable; a host moved with its
  hosted door.
- Expect: `not_owned` as in T18; the editable-by-other case refused cleanly at commit; hosted door keeps its workset.
- Result:

**CR-25 · Image export and capture** · Revit · one release · RH 11, RV-RH RH-08, RH-09
- Steps: `sa revit capture --target activeView --out C:\sa-test\v.png`; `--format pdf` with a `.pdf` out;
  `export.image` with `fileName` `"{view}.png"` and `format jpg`.
- Expect: one file per export; width within 2 px; `.jpg` extension once (no `.png.jpg`); active view restored.
- Result:

**CR-26 · PDF pages, paper size, DWG names** · Revit · one release · RO 5-7
- Steps: `export.pdf` of an A1 and an A3 sheet separately, then `combine` with a plain view; `export.dwg` of one
  view named `X`, then `mergedViews: false`.
- Expect: each sheet keeps its size; the combined set warns and uses Letter for the view; `pages` a number or null
  (record which); `X.dwg` written, companion files listed.
- Result:

**CR-27 · dim.grids** · Revit · 2024, 2027 · RD 1, OD 20
- Steps: plan with grids of different lengths; `dim.grids` `side` `both`; a rotated view; scope-boxed grids.
- Expect: witness lines to every grid; strings clear of the bubbles. Rotated and scope-boxed: record.
- Result:

**CR-28 · Sheets and viewports** · Revit · one release · RD 2-3, RD 12, RD 14, OD 23
- Steps: `viewports.layout` of three views on an office title block; a schedule on the sheet; first viewport in a
  model with none.
- Expect: viewports inside the drawable area, no overlap; rehearsal leaves no viewport and no numbering gap;
  schedule centred where asked. Title blocks with nested families: pass `area` if the derived one is wrong.
- Result:

**CR-29 · Views** · Revit · one release · RD 4-5, RD 13, RD 15-16, RH 14
- Steps: `view.elevation.create` rotated; `view.section.create` with `depth`; `view.3d.create` isometric;
  `text.note` in a section; `view.rename` to a name used by another view type; `sheet.create` names with each
  forbidden character.
- Expect: marker rotation and section far clip as asked; note at the given position; name rules match Revit's own
  (record any character Revit allows that the engine refuses).
- Result:

**CR-30 · schedule.create** · Revit · one release · RD 6-7, OD 22
- Steps: room schedule with headings and grouping; a category with no schedule in the model; `"Multi-Category"`.
- Expect: row count read back matches the rows shown; unschedulable fields fail the job (rolled back) when no sample
  schedule exists.
- Result:

**CR-31 · tag.all** · Revit · one release · RD 8, RV-RO RO-03
- Steps: view with one element already double-tagged and some untaggable elements.
- Expect: untagged elements tagged, untaggable listed, existing double tag only warned.
- Result:

**CR-32 · View templates and filters** · Revit · one release · RD 10-11
- Steps: `viewtemplate.apply` with a template controlling scale; `view.filter.ensure` with rules identical to a
  filter made in the UI.
- Expect: template-controlled scale reported as controlled; the UI filter compares equal (no false difference).
- Result:

**CR-33 · Audit facts** · Revit · one release · RO 9-18, RO 20
- Steps: covered by the R1, RW and RF runs (section 5). Also: a non-English Revit for rv-004, rv-006 and the
  redundant-room rule.
- Expect: planted counts; on non-English Revit those three may miss (text matching): record.
- Result:

**CR-34 · Large model cost** · Revit · 2027 · RH 13, RO 3-4, RV-RH RH-05, OD 25
- Steps: model over 500 MB. Time `sa revit snapshot`, `sa revit audit`, and a small job with
  `"verify": {"checks": ["rv-007.hidden-elements"]}`.
- Expect: times recorded; Revit stays usable; entourage of Level 1 not reported for a Level 2 plan.
- Result:

**CR-35 · Legend by duplication** · Revit · n/a · OD 39
- Not testable: no wave 1 op duplicates a legend. Test when the op exists.

---

## 5. Planted-defect runs (T10, T11)

The job files and their instructions will be in `scripts/testdata/autocad/README.md` and
`scripts/testdata/revit/README.md`. At the time of writing those folders do not exist yet; until they do, build the
files by hand from the specifications `scripts/testdata/autocad-planted-defects.md` and
`scripts/testdata/revit-planted-defects.md`.

1. Build the baseline (AutoCAD `B.dwg`, Revit `RB.rvt`) and audit it:
   `sa acad audit --out C:\sa-test\B-audit.json`, `sa revit audit --out C:\sa-test\RB-audit.json`.
   Every automatic check must be at 0. If one is not, fix the baseline as the spec says (not the expected counts),
   and keep the report.
2. Build each defect file (AutoCAD `P1`, `D2`, `D3` plus the reference files; Revit `R1`, `RW`, `RF`) and audit it
   the same way, open in the app.
3. Compare the findings per check with the spec's section 5 "Expected results". `manual_review` checks report
   status `review` with exactly 1 finding. No automatic check may report `not_implemented`. A check reporting
   `incomplete` is a Fail of this test (the files are small).
4. `sa.exe` cannot name a standard for `audit`; the engines then use the bundled default for imperial files,
   `us-ncs-style.json`, which is what the specs assume. Keep the test files imperial.
5. T11: run the fix ops on copies of P1 and R1 and audit again (section 3, T11).
6. Send back every audit JSON and a list of checks whose count differs, with both numbers.

Settles: AUTOCAD-AUDIT-REPORT §4-5 and REVIT-OUTPUT-AUDIT-REPORT §5-6 (the counts and the facts assumptions behind
them).
