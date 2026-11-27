---
name: cad-bim
description: Does real drafting and BIM work inside full AutoCAD and Revit 2024 to 2027 on Windows through the sa.exe command-line client, as approved, rehearsed, undoable jobs. Draws walls, doors, windows, rooms, grids, dimensions, notes and blocks; sets up drawings and models from a project standard; builds levels, views, sheets, viewports, title blocks and schedules (door, room finish, FF&E, fixtures); plots and exports PDF, DWG and IFC; audits against CAD and BIM standards and fixes layers, units, xrefs, purge, unplaced rooms and unused views. Use when the user asks to draw, draft, model, lay out, schedule, tag, dimension, set up sheets, plot, export, audit or clean up a DWG drawing or Revit model, or mentions AutoCAD, Revit, layers, title blocks, viewports, xrefs, families, worksets or a CAD or BIM standard. For architects, interior designers, space planners, kitchen and bath designers, retail and hospitality designers, facility managers, drafters, BIM coordinators, landscape (2D) and accessibility reviewers.
---

# CAD BIM

The agent drives AutoCAD and Revit through `sa.exe`. Every change is a **job**: a JSON file of typed ops that runs in one undo step, is read back, and is checked before it commits. Use only the commands, ops and fields listed in `references/workflow.md` and `references/jobs/`; never invent one. If no op does what the user asks, say so (see `references/limits.md`).

## The job loop

1. **Snapshot.** `sa <acad|revit> snapshot`. Take the document identity, units and existing names from it. Read units from the file, never assume them.
2. **Standard.** On first use in a project, ask which standard applies unless the file settles it (`references/standards.md`). One standard per job.
3. **Plan the whole job.** Pick ops, starting from a recipe in `references/autocad.md` or `references/revit.md`. Choose verify checks that the file already passes (audit them first). Split work that must be alone (exports, plots, scripts, unit changes, xref repath).
4. **Show the plan and get ONE approval.** List the file, the standard, the units, each change in plain words, anything destructive, and what will be checked. Ask again only if the plan changes materially.
5. **Lint and validate.** `sa job lint job.json`, then `sa <app> validate job.json`. Fix every error.
6. **Rehearse.** `sa <app> run job.json --rehearse` (same file; the job id is not spent).
7. **Commit.** `sa <app> run job.json`, the same file. Afterwards the file has a new fingerprint: take it from the report for the next job.
8. **Check.** Read the report, run `audit` where it helps, `capture` the result and look at it.
9. **Report.** What changed (from the readback), what was checked and how, warnings, anything unchecked.

Read-only requests (snapshot, audit, capture, status, lint) need no approval. Full rules: `references/workflow.md`.

## Rules that are never bent

- **One approval per job**, before anything is written. Destructive ops (`layer.translate`, `fix.purge`, `element.delete`, `cleanup.*`, `workset.assign`) are named in the plan with what they remove or move.
- **Never re-send a job after a timeout.** Ask `sa <app> status <jobId>`. "Unknown" with `interrupted` means outcome unknown: check the file before doing anything.
- **A new job id for every new job.** A committed id is spent forever.
- **Never save, synchronise, relinquish or close** the file. The user does that.
- **Escape hatch** (`script.lisp`, AutoCAD only): alone in its job, code shown in the plan, `--allow-script`, rehearsed first. Revit has none.

## Honesty

- Say exactly what was checked: which checks ran, which passed, which need a person (`review`) and which could not finish (`incomplete`, always reported first). Never call an `incomplete` check a pass.
- Report what the engine returned, not what should have happened. The engines are written and compile for 2024 to 2027 but have not yet been tested in the apps; say so when it matters.
- Code, accessibility and fire compliance stay with the user. Design figures come with their source and jurisdiction; never say a design "complies" or is "certified".
- Say "not available yet" for wave-2 tasks (free 2D geometry, hatches, move/copy/erase, Revit furniture placement, clearance checks, data export, links, purge in Revit and more; `references/users.md`), and offer what works now.

## Which file to open

Work out the app first (the user's words, the file extension: `.dwg` is AutoCAD, `.rvt` and `.rfa` are Revit, or the open session), then open only that app's files. An AutoCAD job never reads `revit.md`, `jobs/revit.md` or a Revit-only guide, and the reverse. Open the other app's files only when the job involves both apps, for example a DWG linked into Revit.

| Task | Open |
|---|---|
| Any job: commands, statuses, error codes, timeouts | `references/workflow.md` |
| AutoCAD capabilities and recipes | `references/autocad.md` |
| Revit capabilities and recipes | `references/revit.md` |
| Exact fields, defaults, outputs and an example per op | `references/jobs/autocad.md`, `references/jobs/revit.md` |
| Which standard, presets, importing an office standard | `references/standards.md` |
| What a user group can do now and what waits | `references/users.md` |
| Out of reach, per-op limits, unproven behaviour | `references/limits.md` |
| How to ask the user, and when not to | `references/practice/asking-the-user.md` |

## Practice guides (`references/practice/`)

Open the one that matches the task before planning; `README.md` there indexes them all and labels each AutoCAD, Revit or Both. In the table below, guides marked (A) are AutoCAD only and (R) Revit only.

| Work | Guide |
|---|---|
| Walls, openings, rooms and areas | `walls.md`, `openings.md`, `rooms-areas.md` |
| Floors, ceilings, roofs, stairs | `floors-ceilings-roofs.md`, `stairs-ramps.md` |
| Dimensions, notes, drawing conventions | `dimensions-annotation.md`, `drawing-conventions.md` |
| Sheets, views, templates, graphics | `sheets-sets.md`, `views-templates-graphics.md` (R) |
| Schedules, tags, families, parameters | `schedules-tags.md`, `families-parameters.md` (R) |
| Interiors, FF&E, joinery | `interiors.md`, `joinery-millwork.md` |
| Materials, finishes and textures | `materials-textures.md` |
| Workplace, kitchens and baths, retail and hospitality | `space-planning.md`, `kitchens-bathrooms.md`, `retail-hospitality.md` |
| RCPs and lighting layouts | `rcp-lighting.md` |
| Facility data and area measurement | `area-facility-data.md` |
| Landscape (2D) | `landscape-site.md` (A) |
| Accessibility review | `accessibility-review.md` |
| CAD standards, BIM set-up, worksharing, export | `cad-standards.md` (A), `bim-setup.md` (R), `worksharing-collaboration.md` (R), `export-exchange.md` |
| QA, common mistakes | `qa-review.md`, `mistakes-autocad.md` (A), `mistakes-revit.md` (R) |
| Design figures with sources | `design-data.md` |
