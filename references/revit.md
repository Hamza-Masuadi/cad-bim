# Revit: what the engine does, and recipes

Revit 2024 to 2027 on Windows. 70 ops. Field details: `jobs/revit.md`. Loop and approval: `workflow.md`. Every op is "compiles for all four releases, untested in Revit" until the owner's first test round; the 31 newest (placement, phases, materials, links, purge, documentation and data ops, family editing, family creation and CAD-to-family conversion) are the least proven, so say "written, untested in the app" when the user relies on them.

Contents: 1 What the engine can do · 2 Rules every Revit job follows · 3 Recipes (3.1 to 3.14 model, views, sheets and clean-up; 3.15 to 3.35 placement, interiors, documentation, data, coordination, families and checks) · 4 No escape hatch · 5 Version notes

## 1. What the engine can do

| Area | Ops |
|---|---|
| Datums | `level.ensure` (with plan and ceiling views), `grid.create` (lines or an orthogonal grid, letters skip I and O) |
| Modelling | `walltype.ensure` (layered wall type with function, material and thickness; missing materials are created plain), `wall.create` (start and end, or a path, optionally closed), `door.place`, `window.place`, `floor.create` and `ceiling.create` (from points or from a room), `roof.footprint` (slopes, overhang, gable edges), `room.place` (at a point or every enclosed region, with finish parameters), `room.separator` |
| Placement and editing | `family.place` (furniture, fixtures, casework, fittings: on a level, hosted on a wall, floor, ceiling or roof face, or on a work plane), `group.place` (make a model group, or place copies of one), `element.transform` (move, copy, rotate, mirror), `phase.set` (phase created and demolished; view phase and phase filter) |
| Build-ups and finishes | `compoundtype.ensure` (floor, ceiling and roof types with layers), `wallsweep.add` (skirtings, cornices, reveals), `fillpattern.ensure`, `material.ensure` (identity, graphics, patterns, appearance), `material.assign` (type layer, material parameter, category, or paint on faces) |
| Families, types and data | `family.load`, `type.duplicate`, `family.edit` (parameters, formulas and types, reloaded and read back; alone in its job), `family.create` (new parametric families from a template, flex-tested; alone in its job; recipes in `data/families`), `param.set` (by ids or by category, level, type and parameter values), `param.shared.ensure`, `schedule.key.create` |
| Views | `view.plan.create`, `view.ceiling.create`, `view.section.create`, `view.elevation.create`, `view.3d.create`, `view.drafting.create` (or a legend by duplicating one), `view.callout.create`, `view.crop.set`, `area.plan.create`, `view.rename`, `viewtemplate.apply`, `view.filter.ensure`, `colorfill.apply` |
| Sheets and annotation | `sheet.create`, `viewport.place`, `viewports.layout`, `schedule.create`, `tag.all`, `tag.place` (chosen elements, rooms, areas or materials), `dim.grids`, `dim.string` (through walls, grids, doors and windows), `text.note`, `text.replace`, `detail.draw` (detail lines, filled and masking regions, detail components), `area.place`, `revision.ensure`, `revision.cloud` |
| Checks | `layout.clearances` (turning circles, door swings and clear space at doors, routes against furniture, fixtures and walls; changes nothing) |
| Links | `link.revit` (with acquire shared coordinates), `link.cad` |
| Output | `export.pdf`, `export.dwg`, `export.ifc`, `export.image`, `export.nwc` (Navisworks), `data.export` (any category's parameters to CSV or JSON), `schedule.export` |
| Clean-up (destructive) | `element.delete`, `cleanup.unplacedRooms`, `cleanup.unusedViews`, `cleanup.purge` (dry run first), `workset.assign` |
| Read-only methods | `snapshot`, `audit` (67 checks in `data/checks/revit.json`, groups such as `rooms`, `views`, `sheets`, `data`, `warnings`, `links`, `worksharing`, `families`, `export`, `visual`), `capture` (`activeView`, a view id or name, `sheet:<number>`, to PNG, JPG or PDF), `probe` |

Not available (do not fake them; `limits.md`): creating in-place families, design options, area schemes or a first legend view, placing legend components, aligning floor and ceiling surface patterns, trim and extend, clash detection, comparing two models, publishing shared coordinates to a link, batch work over closed models.

## 2. Rules every Revit job follows

- **Lengths** are in the job's `units.length`; areas come back in m² for metric jobs and ft² for imperial. Angles are degrees.
- **Paper sizes** (`centre`, `area`, `margin`, gaps, tag `offset`, `dim.grids` offsets) are millimetres on the printed sheet for a metric job, inches for an imperial job.
- **Names must match exactly.** Types are written `"Family : Type"`, or a type name that is unique in its category. Take names from the snapshot (`wallTypes`, `floorTypes`, `ceilingTypes`, `roofTypes`, `familiesAndTypes`, `viewFamilyTypes`, `viewTemplates`); an ambiguous name is refused with the candidates.
- **References**: `"$id.field.0"` picks one element from an earlier op (`"$walls.walls.0"`, `"$r101.rooms.0"`, `"$plan.view"`, `"$sheet.sheet"`). A bare `"$id"` refers to the whole result.
- **Ensure ops** compare an existing level, wall type, parameter or sheet with the request and refuse a difference unless `update: true`.
- **Names made in the same job.** Revit checks every op against the model as it is before anything runs. An op that names a type or family made by an earlier op of the same job fails validation ("not found"). So `walltype.ensure`, `compoundtype.ensure`, `family.load` and `type.duplicate` go in a job of their own; commit it, then use the names. Views, sheets, rooms and elements made earlier in a job are fine through `$id` references. Two exceptions: `material.assign` accepts a material made by an earlier `material.ensure`, and `material.ensure` a fill pattern made by an earlier `fillpattern.ensure`.
- **Never** save, synchronise, relinquish or close: the user does that.
- **Exports go in their own job**, with nothing else: `export.*`, `data.export` and `schedule.export`. `export.nwc` is refused next to any op that changes the model, `export.ifc` included.
- **`family.edit` is alone in its job** (`op_not_alone` otherwise); verify checks and captures are allowed. So are `family.fromcad` (recipe 3.34) and `family.create` (recipe 3.35).
- **Workshared models**: elements created go to the active workset; `workset.assign` moves them afterwards. By default a job that touches an element someone else owns is refused (`not_owned`, with the owners). `"ownership": "skip"` leaves those elements out instead (recipe 3.33, `workflow.md` section 14).
- **Files written by ops** (`data.export`, `schedule.export`, `export.nwc`) go to `outputDir` or the job's output folder; an existing file fails the op unless `overwrite: true`.

## 3. Recipes

Each recipe passed `sa job lint`. Replace the `jobId`, the `document` identity (from `snapshot`, top-level `identity`) and every family, type, view and template name with names from the snapshot. The recipes are metric, in millimetres, with the ISO-style preset copied next to the model as `project-standard.json` (Revit looks for a relative standard in the model's folder only). The `verify.checks` look at the whole model: run `sa revit audit --checks <ids>` first and drop any check the model already fails at error level. Never verify new views or grids with `rv-029.sheet-views-template` or `rv-017.datums-pinned` unless the job also applies templates or the user pins datums: they would roll the job back.

### 3.1 Levels and grid

```json
{
  "jobId": "2026-11-28T09-00-00Z-datums",
  "title": "Levels and structural grid",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "levels", "op": "level.ensure", "args": { "levels": [
        { "name": "Level 00", "elevation": 0 },
        { "name": "Level 01", "elevation": 4200, "createCeilingView": true },
        { "name": "Roof", "elevation": 8400 } ] } },
    { "id": "grid", "op": "grid.create", "args": {
        "orthogonal": { "origin": [0, 0], "xSpacings": [7500, 7500, 7500], "ySpacings": [6000, 6000] } } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

Level elevations are measured from the project base point as Revit shows them; on a model with a raised base point, check the readback.

### 3.2 Partitions, doors and a window (architects, interior designers, kitchen and bath)

Two jobs when the wall type is new (section 2): the type first, then the walls. Skip the first job when the snapshot's `wallTypes` already has the type. The closed path makes walls 0 to 3 (south, east, north, west). Door offsets run from the wall's start to the door centre.

```json
{
  "jobId": "2026-11-28T09-05-00Z-stud-type",
  "title": "Wall type Interior - 100 Stud",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "wt", "op": "walltype.ensure", "args": {
        "name": "Interior - 100 Stud",
        "layers": [
          { "function": "finish1", "material": "Gypsum Wall Board", "thickness": 12.5 },
          { "function": "structure", "material": "Metal Stud Layer", "thickness": 75, "core": true },
          { "function": "finish2", "material": "Gypsum Wall Board", "thickness": 12.5 } ] } }
  ]
}
```

```json
{
  "jobId": "2026-11-28T09-10-00Z-l00-consult",
  "title": "Level 00 consulting rooms: partitions, doors, window",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "walls", "op": "wall.create", "args": {
        "type": "Interior - 100 Stud", "level": "Level 00", "path": [[0, 0], [6000, 0], [6000, 4000], [0, 4000]],
        "closed": true, "height": 2700, "mark": "P" } },
    { "id": "split", "op": "wall.create", "args": {
        "type": "Interior - 100 Stud", "level": "Level 00", "start": [3000, 0], "end": [3000, 4000], "height": 2700 } },
    { "id": "d01", "op": "door.place", "args": {
        "host": "$walls.walls.0", "type": "Single-Flush : 0915 x 2134mm", "offset": 1500, "mark": "D01" } },
    { "id": "d02", "op": "door.place", "args": {
        "host": "$walls.walls.0", "type": "Single-Flush : 0915 x 2134mm", "offset": 4500, "flipHand": true, "mark": "D02" } },
    { "id": "w01", "op": "window.place", "args": {
        "host": "$walls.walls.2", "type": "Fixed : 0915 x 1220mm", "offset": 1500, "sillHeight": 900, "mark": "W01" } }
  ],
  "verify": { "checks": ["rv-005.duplicate-mark"], "capture": [ { "target": "activeView" } ] }
}
```

The north wall runs from (6000, 4000) to (0, 4000), so offset 1500 puts the window over the east room. Door snapping and the hinge side need a look at the capture.

### 3.3 Rooms with finishes, floors and ceilings (interior designers, facility managers)

Run after the walls exist. Finish parameters are the room's built-in `Floor Finish`, `Wall Finish`, `Base Finish` and `Ceiling Finish`; use project parameters only if the snapshot shows them.

```json
{
  "jobId": "2026-11-28T09-30-00Z-l00-rooms",
  "title": "Level 00 rooms, finishes, floors and ceilings",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "r001", "op": "room.place", "args": {
        "level": "Level 00", "point": [1500, 2000], "name": "Consult", "number": "00.01",
        "parameters": { "Floor Finish": "VIN-01", "Wall Finish": "PT-01", "Base Finish": "SKT-01", "Ceiling Finish": "CLG-01" } } },
    { "id": "r002", "op": "room.place", "args": {
        "level": "Level 00", "point": [4500, 2000], "name": "Consult", "number": "00.02",
        "parameters": { "Floor Finish": "VIN-01", "Wall Finish": "PT-01", "Base Finish": "SKT-01", "Ceiling Finish": "CLG-01" } } },
    { "id": "f001", "op": "floor.create", "args": { "fromRoom": "$r001.rooms.0", "type": "Vinyl on Screed 75mm" } },
    { "id": "f002", "op": "floor.create", "args": { "fromRoom": "$r002.rooms.0", "type": "Vinyl on Screed 75mm" } },
    { "id": "c001", "op": "ceiling.create", "args": { "fromRoom": "$r001.rooms.0", "type": "600 x 600mm Grid", "height": 2700 } },
    { "id": "c002", "op": "ceiling.create", "args": { "fromRoom": "$r002.rooms.0", "type": "600 x 600mm Grid", "height": 2700 } }
  ],
  "verify": { "checks": ["rv-005.duplicate-room-number", "rv-037.rooms"] }
}
```

A room that is not enclosed fails its op (`allowNotEnclosed` keeps it at area 0, last phase only). Changing finishes later: `param.set` with `"filter": { "category": "Rooms", "where": { "Number": "00.01" } }`. The floor and ceiling types must already exist; to make new build-ups and add skirtings, see 3.19.

### 3.4 Views, a sheet and viewports

Applying the office template at creation keeps `rv-029.sheet-views-template` passing. `area` is the drawable area inside the title block in sheet millimetres; without it the engine measures the title block and keeps 15 mm clear when it cannot.

```json
{
  "jobId": "2026-11-28T10-00-00Z-sheet-a101",
  "title": "Level 00 plan, RCP and elevation on sheet A101",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "plan", "op": "view.plan.create", "args": {
        "level": "Level 00", "name": "Level 00 - General Arrangement", "scale": "1:100", "template": "Architectural Plan" } },
    { "id": "rcp", "op": "view.ceiling.create", "args": {
        "level": "Level 00", "name": "Level 00 - Reflected Ceiling", "scale": "1:100", "template": "Architectural RCP" } },
    { "id": "elev", "op": "view.elevation.create", "args": {
        "point": [1500, 2000], "direction": "north", "planView": "$plan.view", "name": "Consult 00.01 - North",
        "scale": "1:50", "template": "Interior Elevation" } },
    { "id": "sheet", "op": "sheet.create", "args": {
        "number": "2417-ABC-00-DR-A-1001", "name": "Level 00 General Arrangement", "titleBlock": "A1 metric : A1" } },
    { "id": "vps", "op": "viewports.layout", "args": {
        "sheet": "$sheet.sheet", "views": ["$plan.view", "$rcp.view", "$elev.view"],
        "area": { "min": [20, 20], "max": [700, 574] }, "margin": 10, "gapX": 15, "gapY": 15 } }
  ],
  "verify": { "checks": ["rv-029.sheet-views-template", "rv-028.default-view-names"], "capture": [ { "target": "sheet:2417-ABC-00-DR-A-1001" } ] }
}
```

`direction` is the way the viewer looks. Views that do not fit fail the op (`failIfNotFit`); the report lists `notPlaced` when it is false. Lining a view up across sheets: `viewport.place` with the same `centre` on each sheet.

### 3.5 Tags, grid dimensions and notes

```json
{
  "jobId": "2026-11-28T10-20-00Z-annotate",
  "title": "Tag doors and rooms, dimension grids",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "tagDoors", "op": "tag.all", "args": { "category": "Doors", "view": "Level 00 - General Arrangement" } },
    { "id": "tagRooms", "op": "tag.all", "args": { "category": "Rooms", "view": "Level 00 - General Arrangement" } },
    { "id": "dims", "op": "dim.grids", "args": { "view": "Level 00 - General Arrangement", "side": "both" } },
    { "id": "note", "op": "text.note", "args": {
        "view": "Level 00 - General Arrangement", "text": "ALL DIMENSIONS TO GRID UNLESS NOTED", "position": [0, -3000] } }
  ],
  "verify": { "checks": ["rv-009.typed-tags"], "capture": [ { "target": "Level 00 - General Arrangement" } ] }
}
```

Rooms placed by `room.place` are already tagged in a plan of their level (`tag: true`), so `tag.all` for Rooms only picks up older untagged rooms. Grid dimensions need grids that cross the view; check the capture.

### 3.6 Schedules: doors, room finishes, FF&E (interior designers, FF&E specifiers, facility managers)

Fields are parameter names as Revit shows them. `itemize: false` gives one row per type with a count.

```json
{
  "jobId": "2026-11-28T10-40-00Z-schedules",
  "title": "Door, room finish and FF&E schedules",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "doors", "op": "schedule.create", "args": {
        "name": "Door Schedule", "category": "Doors",
        "fields": [ { "parameter": "Mark" }, { "parameter": "Family and Type" }, { "parameter": "Width" }, { "parameter": "Height" }, { "parameter": "Fire Rating" } ],
        "sort": [ { "field": "Mark" } ] } },
    { "id": "finishes", "op": "schedule.create", "args": {
        "name": "Room Finish Schedule", "category": "Rooms",
        "fields": [ { "parameter": "Number" }, { "parameter": "Name" }, { "parameter": "Floor Finish" }, { "parameter": "Base Finish" },
                    { "parameter": "Wall Finish" }, { "parameter": "Ceiling Finish" }, { "parameter": "Area", "total": true } ],
        "sort": [ { "field": "Number" } ], "grandTotals": true } },
    { "id": "ffe", "op": "schedule.create", "args": {
        "name": "FF&E Schedule", "category": "Furniture",
        "fields": [ { "parameter": "Family and Type" }, { "parameter": "Description" }, { "parameter": "Manufacturer" }, { "parameter": "Count" } ],
        "sort": [ { "field": "Family and Type" } ], "itemize": false, "grandTotals": true } }
  ]
}
```

The schedule can go on a sheet with `viewport.place` and out through `export.pdf`, or out to CSV for a spreadsheet with `schedule.export` (3.28). Key schedules (room styles, door types): 3.21.

### 3.7 FF&E data: shared parameters, then values

Two jobs. The shared parameter file is written only when the first job commits, so values go in the second.

```json
{
  "jobId": "2026-11-28T11-00-00Z-ffe-params",
  "title": "FF&E shared parameters",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "params", "op": "param.shared.ensure", "args": {
        "group": "Interiors",
        "parameters": [ { "name": "FFE Code", "dataType": "text" }, { "name": "FFE Supplier", "dataType": "text" } ],
        "categories": ["Furniture", "Casework"] } }
  ]
}
```

```json
{
  "jobId": "2026-11-28T11-10-00Z-ffe-values",
  "title": "FF&E codes on task chairs",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "chairs", "op": "param.set", "args": {
        "filter": { "category": "Furniture", "type": "Chair-Task : Standard" },
        "values": { "FFE Code": "CH-01", "FFE Supplier": "To be confirmed" } } }
  ]
}
```

Report `chairs.count`. Read-only parameters fail the op unless `skipReadOnly: true` (then they are counted as skipped).

### 3.8 Load a family and make a type

Two jobs: the family's types cannot be named in the job that loads it (section 2). Skip the first job when the family is already loaded.

```json
{
  "jobId": "2026-11-28T11-15-00Z-load-door",
  "title": "Load the Single-Flush door family",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "load", "op": "family.load", "args": { "path": "C:/Projects/2417/families/Single-Flush.rfa" } }
  ]
}
```

```json
{
  "jobId": "2026-11-28T11-20-00Z-door-types",
  "title": "Add 826 x 2040 door type",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "dup", "op": "type.duplicate", "args": {
        "source": "Single-Flush : 0915 x 2134mm", "category": "Doors", "newName": "0826 x 2040mm",
        "parameters": { "Width": 826, "Height": 2040 } } }
  ],
  "verify": { "checks": ["rv-027.type-copy-names"] }
}
```

Loading places nothing; to place furniture or fixtures from a loaded family, see 3.15. To change the family's own parameters, formulas or types, see 3.32.

### 3.9 Revision, then export

```json
{
  "jobId": "2026-11-28T12-00-00Z-rev-p02",
  "title": "Revision P02 on the GA sheets",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "rev", "op": "revision.ensure", "args": {
        "revisions": [ { "description": "P02 Issued for coordination", "date": "2026-12-01" } ],
        "addToSheets": ["2417-ABC-00-DR-A-1001"] } }
  ]
}
```

Exports alone, as their own job:

```json
{
  "jobId": "2026-11-28T12-10-00Z-issue-p02",
  "title": "Issue P02: PDF and DWG",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "pdf", "op": "export.pdf", "args": {
        "sheets": ["2417-ABC-00-DR-A-1001"], "fileName": "{sheetNumber}-{revision}", "colour": "black_line" } },
    { "id": "dwg", "op": "export.dwg", "args": { "sheets": ["2417-ABC-00-DR-A-1001"], "fileName": "{sheetNumber}" } }
  ]
}
```

```json
{
  "jobId": "2026-11-28T12-20-00Z-ifc",
  "title": "IFC for coordination",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "ifc", "op": "export.ifc", "args": { "fileName": "2417-ARCH", "version": "ifc4" } }
  ]
}
```

The rehearsal writes to a temporary folder; the commit writes the real files. Report each file with its page count (may be null). A sheet that is not a drawing sheet in the set makes the PDF fall back to Letter fit-to-page.

### 3.10 Audit, then clean up (BIM coordinators, facility managers)

```
sa revit audit --group health --out audit.json
sa revit audit --group rooms
sa revit snapshot --sections rooms,views
```

Take the ids from the snapshot, show them in the plan, rehearse a dry run first:

```json
{
  "jobId": "2026-11-28T13-00-00Z-cleanup",
  "title": "Delete unplaced rooms and unused working views",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "rooms", "op": "cleanup.unplacedRooms", "args": { "ids": ["510021", "510022"] } },
    { "id": "views", "op": "cleanup.unusedViews", "args": { "ids": ["620011", "620014"], "dryRun": true } }
  ],
  "verify": { "checks": ["rv-037.rooms"] }
}
```

After the user sees `views.wouldDelete`, a second job drops `dryRun`. Views with dependents are kept unless `keepWithDependents: false`. `element.delete` takes ids from a snapshot and an `expectCount` that must equal their number; dependents are refused unless `allowDependents: true`.

### 3.11 View names, templates and filters

```json
{
  "jobId": "2026-11-28T13-30-00Z-view-tidy",
  "title": "Rename default views, apply template, existing-furniture filter",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "rename", "op": "view.rename", "args": { "views": [ { "view": "Level 1", "name": "Level 00 - Furniture Plan" } ] } },
    { "id": "tmpl", "op": "viewtemplate.apply", "args": { "views": ["Level 00 - Furniture Plan"], "template": "Interior Furniture Plan" } },
    { "id": "filter", "op": "view.filter.ensure", "args": {
        "name": "FF&E - Existing to remain", "categories": ["Furniture"],
        "rules": [ { "parameter": "Comments", "operator": "equals", "value": "Existing to remain" } ],
        "applyTo": ["Level 00 - Furniture Plan"], "overrides": { "halftone": true } } }
  ],
  "verify": { "checks": ["rv-028.default-view-names"] }
}
```

`applyTo` can name view templates as well as views; a view whose template controls filters should get the filter through its template.

### 3.12 Sections and 3D views

```json
{
  "jobId": "2026-11-28T14-00-00Z-sections",
  "title": "Section A-A and a 3D fit-out view",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "secA", "op": "view.section.create", "args": {
        "start": [-1000, 2000], "end": [7000, 2000], "depth": 4000, "bottom": -500, "top": 4500,
        "name": "Section A-A", "scale": "1:50" } },
    { "id": "iso", "op": "view.3d.create", "args": {
        "name": "3D - Level 00 Fit-out", "sectionBox": { "min": [0, 0, 0], "max": [6000, 4000, 3000] } } }
  ],
  "verify": { "capture": [ { "target": "Section A-A" } ] }
}
```

### 3.13 Roof with gable ends

Edge `i` runs from boundary point `i` to `i+1`; edges 1 and 3 are the gables here.

```json
{
  "jobId": "2026-11-28T14-20-00Z-roof",
  "title": "Pitched roof, gables east and west",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "roof", "op": "roof.footprint", "args": {
        "boundary": [[0, 0], [12000, 0], [12000, 8000], [0, 8000]], "type": "Generic - 300mm", "level": "Roof",
        "slope": 35, "overhang": 450,
        "edges": [ { "edge": 1, "definesSlope": false }, { "edge": 3, "definesSlope": false } ] } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

### 3.14 Furniture onto the FF&E workset (workshared models only)

```json
{
  "jobId": "2026-11-28T14-40-00Z-worksets",
  "title": "Move furniture to the FF&E workset",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "ws", "op": "workset.assign", "args": { "filter": { "category": "Furniture" }, "workset": "FF&E" } }
  ],
  "verify": { "checks": ["rv-020.ffe-workset"] }
}
```

Use `create: true` only if the user agreed to a new workset. Never synchronise or relinquish for the user.

Recipes 3.15 to 3.33 use the newest ops: written, untested in the app. Read each readback and look at the capture before telling the user it worked.

### 3.15 Furniture, fixtures and lights (interior designers, space planners, kitchen and bath, lighting layouts)

The types must be loaded (3.8). Ceiling lights sit on the ceiling's bottom face (a face-based family, hosted on the ceiling's id from the snapshot); the basin is hosted on a wall, `offset` being its height above the level.

```json
{
  "jobId": "2026-11-28T15-00-00Z-l00-ffe",
  "title": "Level 00 desks, chairs, downlights and basin",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "desks", "op": "family.place", "args": {
        "type": "Desk : 1600 x 800mm", "category": "Furniture", "points": [[1200, 1000], [1200, 2800]],
        "level": "Level 00", "rotation": 90, "mark": "DSK" } },
    { "id": "chairs", "op": "family.place", "args": {
        "type": "Chair-Task : Standard", "category": "Furniture", "points": [[1900, 1000], [1900, 2800]],
        "level": "Level 00", "rotation": 270 } },
    { "id": "lights", "op": "family.place", "args": {
        "type": "Downlight - Round : 150mm", "category": "Lighting Fixtures", "points": [[1500, 1300], [1500, 2700]],
        "host": "520101", "face": "bottom" } },
    { "id": "basin", "op": "family.place", "args": {
        "type": "Basin-Wall Hung : 550mm", "category": "Plumbing Fixtures", "points": [[5600, 3950]],
        "host": "316020", "offset": 850, "parameters": { "Comments": "Clinical hand wash" } } }
  ],
  "verify": { "checks": ["rv-005.duplicate-mark"], "capture": [ { "target": "activeView" } ] }
}
```

Marks come out DSK-1, DSK-2. A point that is not on the host's face fails the op. Untested in the app: which offset parameter a family takes (columns and work-plane families differ), and facing after rotation; check the readback and the capture.

### 3.16 Groups for repeated layouts (space planners, interior designers)

Make a group from placed elements and copy it in one job. Placing more copies later is a second job that names the group type (it cannot be named in the job that makes it).

```json
{
  "jobId": "2026-11-28T15-20-00Z-desk-cluster",
  "title": "Desk cluster group, two copies",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "cluster", "op": "group.place", "args": {
        "elements": ["317010", "317011", "317012", "317013"], "name": "Desk Cluster 4",
        "points": [[9000, 1000], [13000, 1000]], "level": "Level 01" } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

```json
{
  "jobId": "2026-11-28T15-30-00Z-desk-cluster-l02",
  "title": "Desk Cluster 4 on Level 02",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "copies", "op": "group.place", "args": {
        "groupType": "Desk Cluster 4", "points": [[1000, 1000], [5000, 1000], [9000, 1000]], "level": "Level 02", "rotation": 0 } }
  ]
}
```

Elements already in a group are refused. Where a placed copy lands relative to the group's origin, and its rotation, are not read back (`rotationChecked: false`): check the capture.

### 3.17 As-built changes: move, copy and remove (architects, facility managers)

Ids come from the snapshot or the user's selection; list each one in the plan with what it is.

```json
{
  "jobId": "2026-11-28T15-40-00Z-asbuilt-l01",
  "title": "As-built Level 01: move door D12, copy desk, remove two cabinets",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "moveD12", "op": "element.transform", "args": { "elements": ["316031"], "action": "move", "vector": [600, 0] } },
    { "id": "desks", "op": "element.transform", "args": { "elements": ["317020"], "action": "copy", "vector": [0, 1600], "copies": 3 } },
    { "id": "remove", "op": "element.delete", "args": { "ids": ["317044", "317045"], "expectCount": 2 } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

A door moves along its wall only; pinned elements are refused. Copies keep the original's mark, so a mark check may fail afterwards: renumber with `param.set`. Walls joined to a moved wall may change too and are not listed in the report.

### 3.18 Existing, demolition and new (architects, interior designers, facility managers)

Phases and phase filters by the names in the snapshot (`phases`). The three views must exist; make them with `view.plan.create` in an earlier job.

```json
{
  "jobId": "2026-11-28T16-00-00Z-phasing-l00",
  "title": "Level 00 phasing: existing walls, two demolished, phased views",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "existing", "op": "phase.set", "args": {
        "filter": { "category": "Walls", "level": "Level 00", "where": { "Comments": "Existing" } }, "created": "Existing" } },
    { "id": "demo", "op": "phase.set", "args": {
        "elements": ["316040", "316041"], "created": "Existing", "demolished": "New Construction" } },
    { "id": "vExisting", "op": "phase.set", "args": {
        "views": ["Level 00 - Existing"], "viewPhase": "Existing", "phaseFilter": "Show All" } },
    { "id": "vDemo", "op": "phase.set", "args": {
        "views": ["Level 00 - Demolition"], "viewPhase": "New Construction", "phaseFilter": "Show Previous + Demo" } },
    { "id": "vNew", "op": "phase.set", "args": {
        "views": ["Level 00 - Proposed"], "viewPhase": "New Construction", "phaseFilter": "Show Previous + New" } }
  ],
  "verify": { "capture": [ { "target": "Level 00 - Demolition" } ] }
}
```

Phase Demolished must be later than Phase Created; `"demolished": "none"` clears it. Untested in the app: moving both phases later in one go.

### 3.19 Floor and ceiling build-ups, then skirtings (interior designers, kitchen and bath, architects)

Two jobs: the types, then the elements that use them (section 2). Layers go from top to bottom; missing materials are created plainly (dress them with 3.20).

```json
{
  "jobId": "2026-11-28T16-20-00Z-finish-types",
  "title": "Floor finish and ceiling types",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "fl", "op": "compoundtype.ensure", "args": {
        "kind": "floor", "name": "FL - Vinyl on 50 Screed",
        "layers": [
          { "function": "finish1", "material": "Vinyl Sheet", "thickness": 3 },
          { "function": "substrate", "material": "Sand Cement Screed", "thickness": 50, "core": true } ] } },
    { "id": "clg", "op": "compoundtype.ensure", "args": {
        "kind": "ceiling", "name": "CLG - 15 Plasterboard on MF",
        "layers": [
          { "function": "structure", "material": "Metal Furring", "thickness": 30, "core": true },
          { "function": "finish1", "material": "Plasterboard", "thickness": 15 } ] } }
  ]
}
```

```json
{
  "jobId": "2026-11-28T16-30-00Z-finishes-r001",
  "title": "Consult 00.01: finish floor, ceiling and skirting",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "floor", "op": "floor.create", "args": { "fromRoom": "510101", "type": "FL - Vinyl on 50 Screed" } },
    { "id": "ceiling", "op": "ceiling.create", "args": { "fromRoom": "510101", "type": "CLG - 15 Plasterboard on MF", "height": 2700 } },
    { "id": "skirting", "op": "wallsweep.add", "args": {
        "filter": { "category": "Walls", "level": "Level 00", "type": "Interior - 100 Stud" },
        "type": "Skirting - 100 x 18 MDF", "distance": 0, "side": "interior" } }
  ],
  "verify": { "capture": [ { "target": "Consult 00.01 - North" } ] }
}
```

The sweep type must be in the model. `side` is the wall's own interior face, which on a partition may face either room: check the capture and use `walls` with chosen ids where it matters. Doors cut the skirting (`cutByInserts`). Changing an existing type needs `update: true` and changes every instance of it (the warning gives the count).

### 3.20 Finish materials and fill patterns (interior designers, kitchen and bath, retail and hospitality)

The finish code goes in the material's `mark`, so a material tag shows it. `material.assign` on a type layer changes every instance: `maxInstances` stops the op if more use it than the plan said.

```json
{
  "jobId": "2026-11-28T16-40-00Z-finish-materials",
  "title": "Tile pattern, vinyl and porcelain materials, vinyl on the floor type",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "pat", "op": "fillpattern.ensure", "args": {
        "patterns": [ { "name": "Tile 600 x 600", "target": "model", "stack": { "width": 600, "height": 600 } } ] } },
    { "id": "mats", "op": "material.ensure", "args": {
        "materials": [
          { "name": "FIN - Vinyl VIN-01", "identity": { "description": "Sheet vinyl, grey", "mark": "VIN-01", "manufacturer": "To be confirmed" },
            "shading": { "color": "150,150,145" }, "appearance": { "color": "150,150,145" } },
          { "name": "FIN - Porcelain PT-02", "identity": { "description": "Porcelain tile 600 x 600", "mark": "PT-02" },
            "shading": { "color": "220,215,205" },
            "surface": { "foreground": { "pattern": "Tile 600 x 600", "target": "model", "color": "120,120,120" } },
            "appearance": { "color": "220,215,205",
              "texture": { "path": "C:/Projects/2417/textures/porcelain-pt02.jpg", "width": 600, "height": 600 } } } ] } },
    { "id": "assign", "op": "material.assign", "args": {
        "material": "FIN - Vinyl VIN-01",
        "layer": { "type": "FL - Vinyl on 50 Screed", "kind": "floor", "function": "finish1", "side": "top" },
        "maxInstances": 40 } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

A new material gets its own appearance; editing an appearance that other materials share is refused unless `shareAsset: true`. Painting one face instead of the whole type: `"paint": { "elements": [...], "faces": ["interior"] }`. Untested in the app: the pattern grid units, the library appearance a new material starts from, and how the texture size reads in a rendered view. Lining up floor tile patterns is not offered.

### 3.21 Key schedule for room styles (interior designers, facility managers)

```json
{
  "jobId": "2026-11-28T17-00-00Z-room-style-key",
  "title": "Room Style key schedule",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "key", "op": "schedule.key.create", "args": {
        "name": "Room Style Key", "category": "Rooms", "keyParameter": "Room Style",
        "fields": ["Floor Finish", "Base Finish", "Wall Finish", "Ceiling Finish"],
        "keys": [
          { "name": "Consult", "values": { "Floor Finish": "VIN-01", "Base Finish": "SKT-01", "Wall Finish": "PT-01", "Ceiling Finish": "CLG-01" } },
          { "name": "WC", "values": { "Floor Finish": "PT-02", "Base Finish": "PT-02", "Wall Finish": "WT-01", "Ceiling Finish": "CLG-02" } } ] } }
  ]
}
```

Every room of the category gets the `Room Style` parameter. Giving rooms their key (setting `Room Style` to "Consult" with `param.set`) has not been tried in the app: rehearse it and check the readback before promising it. Renaming the key parameter of an existing key schedule renames it on every room (the op warns).

### 3.22 Area plans (space planners, facility managers)

Area schemes cannot be made through the engine; use one from the snapshot. Areas are plain measured areas from the boundary drawn, not BOMA, IPMS or a rentable method.

```json
{
  "jobId": "2026-11-28T17-20-00Z-gia-l00",
  "title": "Level 00 gross area plan",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "gia", "op": "area.plan.create", "args": {
        "areaScheme": "Gross Building", "level": "Level 00", "name": "Level 00 - GIA", "scale": "1:200" } },
    { "id": "areas", "op": "area.place", "args": {
        "view": "$gia.view",
        "boundaries": [ [[0, 0], [22500, 0], [22500, 12000], [0, 12000]] ],
        "areas": [ { "point": [11250, 6000], "name": "GIA", "number": "00-GIA" } ] } }
  ],
  "verify": { "capture": [ { "target": "Level 00 - GIA" } ] }
}
```

Report `areas.values` (m²). An area that comes out not enclosed fails the op. New boundary lines can reshape areas that already exist in that plan, which the report does not list: compare the snapshot before and after.

### 3.23 Colour-fill plan by department (space planners, facility managers)

```json
{
  "jobId": "2026-11-28T17-40-00Z-dept-plan",
  "title": "Department colour plan, Level 00",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "clinical", "op": "param.set", "args": {
        "filter": { "category": "Rooms", "level": "Level 00", "where": { "Name": "Consult" } }, "values": { "Department": "Clinical" } } },
    { "id": "support", "op": "param.set", "args": {
        "filter": { "category": "Rooms", "level": "Level 00", "where": { "Name": "WC" } }, "values": { "Department": "Support" } } },
    { "id": "plan", "op": "view.plan.create", "args": { "level": "Level 00", "name": "Level 00 - Departments", "scale": "1:200" } },
    { "id": "fill", "op": "colorfill.apply", "args": {
        "view": "$plan.view", "scheme": "Department", "parameter": "Department", "title": "Department",
        "colors": { "Clinical": "#88CCEE", "Support": "200,200,200" }, "legendAt": [24000, 12000] } }
  ],
  "verify": { "capture": [ { "target": "Level 00 - Departments" } ] }
}
```

Report `clinical.count` and `support.count`. An existing scheme is left alone unless `update: true`; changing its parameter clears its colours. Untested in the app: where the legend lands on a plan whose level is not at zero.

### 3.24 Drafting detail (architects, CAD drafters and technicians)

Drafting view points are the view's own [x, y] in model millimetres; the scale makes them print smaller.

```json
{
  "jobId": "2026-11-28T18-00-00Z-skirting-detail",
  "title": "Drafting detail D-01: skirting",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "det", "op": "view.drafting.create", "args": { "name": "D-01 Skirting Detail", "scale": "1:5" } },
    { "id": "draw", "op": "detail.draw", "args": {
        "view": "$det.view", "lineStyle": "Thin Lines",
        "lines": [ { "start": [0, 0], "end": [400, 0], "lineStyle": "Wide Lines" } ],
        "polylines": [ { "points": [[0, 0], [0, 400]], "lineStyle": "Medium Lines" } ],
        "regions": [ { "boundary": [[0, 0], [18, 0], [18, 100], [0, 100]], "type": "Wood" } ],
        "components": [ { "type": "Gypsum Wallboard-Section : 12.5mm", "point": [-12.5, 0] } ] } },
    { "id": "note", "op": "text.note", "args": {
        "view": "$det.view", "text": "18 x 100 MDF SKIRTING, PAINTED", "position": [120, 60] } }
  ],
  "verify": { "capture": [ { "target": "D-01 Skirting Detail" } ] }
}
```

Filled region, line style and detail component names must exist (snapshot or ask). A legend is made by copying an existing legend view (`legendFrom`); a first legend cannot be made.
### 3.25 Crop regions, an enlarged callout and a dimension string (architects, kitchen and bath, CAD drafters)

```json
{
  "jobId": "2026-11-28T18-20-00Z-consult-enlarged",
  "title": "Crop the GA, enlarged plan of Consult 00.01, dimension string",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "crop", "op": "view.crop.set", "args": {
        "view": "Level 00 - General Arrangement", "rect": { "min": [-3000, -3000], "max": [25500, 15000] }, "annotationCrop": true } },
    { "id": "elevCrop", "op": "view.crop.set", "args": { "view": "Consult 00.01 - North", "room": "510101", "offset": 200 } },
    { "id": "callout", "op": "view.callout.create", "args": {
        "parentView": "Level 00 - General Arrangement", "rect": { "min": [-500, -500], "max": [3500, 4500] },
        "name": "Consult 00.01 - Enlarged Plan", "scale": "1:20" } },
    { "id": "dims", "op": "dim.string", "args": {
        "view": "Level 00 - General Arrangement", "start": [-1000, 0], "end": [7000, 0], "offset": -1200,
        "pick": ["walls", "doors"], "wallFaces": "both", "openings": "width", "overall": true } }
  ],
  "verify": { "capture": [ { "target": "Level 00 - General Arrangement" }, { "target": "Consult 00.01 - North" } ] }
}
```

A polygon crop (`boundary`) is for plans only. The string dimensions the walls the line crosses and, with `pick` doors, the doors on those walls; read `dims.measurements` back to the user. Untested in the app: door width references and tag positions after a crop.

### 3.26 Tags and revision clouds, checked on the view (architects, CAD drafters, BIM coordinators)

The verify checks look for tags that overlap and for job elements outside the crop; `visual` compares the view before and after (`workflow.md` section 15). None of them can roll the job back.

```json
{
  "jobId": "2026-11-28T18-40-00Z-p02-markup",
  "title": "Tag moved doors, cloud the change for P02",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "tags", "op": "tag.place", "args": {
        "view": "Level 00 - General Arrangement", "tagType": "Door Tag : Standard",
        "tags": [ { "element": "316031", "offset": [6, 6], "leader": true }, { "element": "316032", "point": [4500, -800] } ] } },
    { "id": "cloud", "op": "revision.cloud", "args": {
        "view": "Level 00 - General Arrangement", "revision": "P02",
        "rect": { "min": [3600, -1500], "max": [6400, 1000] }, "comments": "Doors D12 and D13 moved" } }
  ],
  "verify": {
    "checks": ["rv-054.annotation-overlap", "rv-055.job-outside-crop"],
    "visual": { "target": "Level 00 - General Arrangement", "pixelWidth": 2400, "expectChange": true }
  }
}
```

An element that already has a tag of that category in the view is an error unless `allowSecondTag: true`. Leadered tags are left out of the overlap check. Report `visual.diff` and attach `visual-diff.png`; "review" means look at it, not "passed". Untested in the app: where Revit puts an offset tag's head.

### 3.27 Clearance check: turning circle, door approach, route (accessibility reviewers, kitchen and bath, space planners)

Changes nothing. It reports what intrudes into each zone and by how much; it does not decide compliance. Take the sizes from the user or `practice/accessibility-review.md` and say which source they come from.

```json
{
  "jobId": "2026-11-28T19-00-00Z-wc-clearances",
  "title": "WC 00.03 and corridor clearances",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "clear", "op": "layout.clearances", "args": {
        "view": "Level 00 - General Arrangement",
        "against": ["Furniture", "Casework", "Plumbing Fixtures", "Walls"],
        "maxHeight": 2000,
        "zones": [
          { "id": "WC-turning", "kind": "circle", "centre": [8250, 1500], "radius": 750 },
          { "id": "D14-approach", "kind": "doorClearance", "door": "316034", "depth": 1500, "faces": "both" },
          { "id": "Corridor", "kind": "route", "points": [[0, 4600], [22500, 4600]], "width": 1200 } ] } }
  ]
}
```

Report `clear.clear`, then each intrusion with its zone, element and depth, deepest first. Elements are tested by their plan footprint in that view. Untested in the app: wall footprints at door openings.

### 3.28 Room data and schedule export (facility managers, BIM coordinators, interior designers)

Exports only, so in a job of their own. Nothing in the model changes.

```json
{
  "jobId": "2026-11-28T19-20-00Z-room-data",
  "title": "Room data CSV and room finish schedule CSV",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "outputDir": "C:/Projects/2417/out",
  "ops": [
    { "id": "rooms", "op": "data.export", "args": {
        "categories": ["Rooms"], "parameters": ["Number", "Name", "Department", "Area", "Floor Finish"],
        "include": ["id", "level", "boundary"], "format": "csv", "fileName": "{drawing} - rooms {date}" } },
    { "id": "finishes", "op": "schedule.export", "args": {
        "schedule": "Room Finish Schedule", "fileName": "{view} {date}", "delimiter": "comma" } }
  ]
}
```

Report each file path with `rooms.rows`. A value that starts with `=`, `+`, `-` or `@` is written as it is and may act as a formula in a spreadsheet; warn the user if the data has such values. JSON is also offered (`"format": "json"`).

### 3.29 Links: a Revit model and a CAD survey (BIM coordinators, architects)

Placement "shared" needs shared coordinates set up already. `"coordinates": "acquire"` takes them from the link instead, and is refused when it would remove or replace this model's geolocation; publishing coordinates to a link is not offered.

```json
{
  "jobId": "2026-11-28T19-40-00Z-links",
  "title": "Link structure model and site survey",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "str", "op": "link.revit", "args": {
        "path": "C:/Projects/2417/links/2417-STR.rvt", "placement": "shared", "worksets": "all", "coordinates": "none" } },
    { "id": "survey", "op": "link.cad", "args": {
        "path": "C:/Projects/2417/links/site-survey.dwg", "view": "Site", "placement": "shared", "unit": "m",
        "colorMode": "black_and_white", "hideLayers": ["TEXT", "DEFPOINTS"] } }
  ],
  "verify": { "capture": [ { "target": "Site" } ] }
}
```

A file that is already linked is refused unless `allowDuplicate: true`. Links are pinned by default. Untested in the app: the placement point of a linked DWG with unit scaling, and acquiring coordinates.

### 3.30 Purge unused, dry run first (BIM coordinators)

First job lists; nothing is deleted.

```json
{
  "jobId": "2026-11-28T20-00-00Z-purge-list",
  "title": "List unused families and materials",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "list", "op": "cleanup.purge", "args": { "categories": ["Furniture", "Doors", "Materials"], "dryRun": true } }
  ]
}
```

Show the user `list.items` (name and category per id). After they agree, a second job deletes the ids they kept, with their exact count:

```json
{
  "jobId": "2026-11-28T20-10-00Z-purge-delete",
  "title": "Purge 3 unused families and materials",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "purge", "op": "cleanup.purge", "args": { "ids": ["400101", "400102", "400215"], "expectCount": 3 } }
  ]
}
```

Each id must still be unused. An "unused" element that owns placed or annotation elements is refused, and elements the standard keeps are skipped. Report `purge.remainingUnused`: purging can free more, which is a new dry run and a new approval. Untested in the app: whether the list matches Revit's Purge Unused window.

### 3.31 Navisworks export (BIM coordinators)

Alone in its job (or with other exports that change nothing). Needs the Navisworks exporter installed in Revit.

```json
{
  "jobId": "2026-11-28T20-20-00Z-nwc",
  "title": "Navisworks cache for coordination",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "outputDir": "C:/Projects/2417/out",
  "ops": [
    { "id": "nwc", "op": "export.nwc", "args": {
        "fileName": "{drawing} - coordination", "scope": "view", "view": "{3D} Navisworks", "coordinates": "shared",
        "exportLinks": false, "overwrite": true } }
  ]
}
```

Clash detection itself happens in Navisworks, not here.

### 3.32 Edit a door family's sizes and parameters (architects, BIM coordinators, kitchen and bath)

Alone in its job. `sa revit validate` first: it opens a copy of the family to check the edit, and lists what else would change (`otherChanges`). The rehearsal edits a copy and reads it back without loading it; the commit loads it back, and existing types take the new values.

```json
{
  "jobId": "2026-11-28T20-40-00Z-door-family-sizes",
  "title": "Single-Flush: 2100 high, new 826 x 2100 type, clearance parameter",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "door", "op": "family.edit", "args": {
        "family": "Single-Flush", "category": "Doors",
        "addParameters": [ { "name": "Clear Opening", "spec": "length", "group": "dimensions" } ],
        "formulas": { "Clear Opening": "Width - 90 mm" },
        "types": [
          { "name": "0915 x 2134mm", "values": { "Height": 2100 } },
          { "name": "0826 x 2100mm", "create": true, "copyFrom": "0915 x 2134mm", "values": { "Width": 826, "Height": 2100 } } ] } }
  ],
  "verify": { "checks": ["rv-027.type-copy-names"], "capture": [ { "target": "activeView" } ] }
}
```

Every placed door of the family follows the new values: say how many (snapshot) in the plan. Types are never renamed or deleted. In-place, system and non-editable families are refused. Untested in the app, and the most fragile op: editing and loading a family inside the job's undo step; if the report warns that a family copy stayed open, ask the user to close it in Revit without saving.

### 3.33 Workshared model with borrowed elements (BIM coordinators, architects)

Validate first and show the user the `ownership` list: who owns which elements. By default the job is refused when it touches any of them. Only if the user agrees to leave those out, add `"ownership": "skip"`:

```json
{
  "jobId": "2026-11-28T21-00-00Z-l01-fire-ratings",
  "title": "Fire rating FD30 on Level 01 doors, skipping doors others own",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ownership": "skip",
  "ops": [
    { "id": "fd30", "op": "param.set", "args": {
        "filter": { "category": "Doors", "level": "Level 01", "where": { "Comments": "Fire door" } },
        "values": { "Fire Rating": "FD30" } } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

Report `fd30.count` and every `ownership_skipped` warning (`readback.ownershipSkipped` lists the ids): those doors were not changed and their owners must do it, or the user asks for them and runs a new job. An op whose elements are all left out is skipped, and so are ops that refer to it. Never borrow, synchronise or relinquish for the user.

### 3.34 AutoCAD furniture blocks to Revit families, placed where the plan has them (interior designers, space planners, BIM coordinators)

Three jobs, each committed before the next:
1. AutoCAD `block.export` (`autocad.md` recipe 3.21) writes one DWG per block and `placements.json`.
2. Revit `family.fromcad` makes one family per block from those files and loads it.
3. Revit `family.place` with `fromPlacements` puts an instance at every block reference, aligned to the same drawing linked in the model (recipe 3.29 links it).

Job 2 is alone in its job. Each family gets:
- the block's DWG as its plan symbol (view-specific, at the block's base point, in the drawing's units);
- a body: `box` (the block's extents extruded to `height`, with Width, Depth and Height parameters and a material parameter), `none`, or `import3d` (only for blocks with 3D objects);
- type parameters `CAD Block` and `CAD Source`.

Every family is flex-tested at its own size and at larger and smaller sizes, then saved as `.rfa` in `outputDir` (only when the job commits) and loaded. The rehearsal builds, tests, saves to a temporary folder and loads inside the undo step, then rolls it all back. A family name the project already has is refused unless `reload: true`.

```json
{
  "jobId": "2026-11-28T21-20-00Z-ffe-families",
  "title": "Families from the L01 furniture blocks",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "fam", "op": "family.fromcad", "args": {
        "file": "C:/Projects/2417/export/ffe-L01/placements.json",
        "blocks": ["DESK-1600", "CHAIR-TASK", "BED-DOUBLE"],
        "familyPrefix": "FFE-", "category": "Furniture", "height": 740, "symbol": "lines",
        "overrides": { "CHAIR-TASK": { "height": 900 }, "BED-DOUBLE": { "height": 450, "typeName": "1600 x 2100" } },
        "outputDir": "C:/Projects/2417/families/ffe" } }
  ]
}
```

Report `fam.families`, `fam.files`, and each `fam.flex` entry that is not ok. `symbol: "lines"` turns the import into symbolic lines in the `CAD Symbol` subcategory. Use it: Revit gives an imported DWG no subcategory, so with `"import"` its layers show only under "Imports in Families" in Object Styles. The box is hidden in plan views unless `bodyInPlan: true`, so plans show only the CAD symbol.

Job 3 places the instances. `align.cadLink` names the linked DWG (its file name or element id). The link's position, rotation and the shared-coordinate (true north) turn all carry over. `align.translate` with `rotationDeg` moves and turns the drawing's coordinates instead, in job units. Use it only when the drawing is not linked.

```json
{
  "jobId": "2026-11-28T21-40-00Z-ffe-place",
  "title": "Place the L01 furniture where the CAD plan has it",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "ffe", "op": "family.place", "args": {
        "fromPlacements": { "file": "C:/Projects/2417/export/ffe-L01/placements.json", "align": { "cadLink": "2417-I-FFE-L01.dwg" } },
        "level": "Level 01", "mark": "FFE" } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

Report `ffe.count` and every `ffe.refused` entry with its reason. Each block finds its family through the `CAD Block` parameter, or through `familyMap` (block name to `"Family : Type"`). A refused reference is one that:
- is scaled or in a layout;
- is already placed with the same type at the same point;
- uses a block with no family;
- is mirrored and its family cannot be mirrored.

A refusal does not stop the others unless `failOnRefused: true`. Mirrored references are placed mirrored, and `readback.instances` gives each instance's axes. `readback.align.linkEvidence.matched` counts the placed points that coincide with block instances seen inside the link. If it is 0, the link is probably not the drawing the file came from, or it has been moved since.

Untested in the app, and the most fragile part is the coordinate chain: whether `GetTotalTransform` of the link includes the DWG's unit scale and true north as documented. Rehearse job 3 and compare a few instances with the link by eye before committing.

### 3.35 New parametric furniture and sanitaryware families from a recipe (interior designers, hotel FF&E, kitchen and bath)

Two jobs, each committed before the next: a family made in a job cannot be named until a later job.
1. `family.create` builds a new family from a Revit family template, alone in its job. The 22 recipes in `data/families/` (beds, seating, tables, storage, WC, basins, bath, shower tray, kitchen units, worktop) are ready `args`; copy one and change only `name`, `outputDir` or the types.
2. `family.place` places it like any loaded family.

What `family.create` does: finds the template for `category` (metric first for a metric job), or uses `template`. When no template for the category exists, it uses a Generic Model template and changes the category, with a warning. It then:
- adds the parameters, formulas and types;
- draws the reference planes, and labels the dimensions so that Width, Depth and Height move the planes;
- locks every face of each `box` to its planes;
- ties materials, subcategories and visibility to the forms;
- draws the plan symbolic lines.

Then the flex test: every type is made current and regenerated, and the solid geometry must measure exactly the parameters named in `flex`. A failed type, or any Revit warning, fails the op and keeps nothing. The `.rfa` is saved in `outputDir` (put in place only when the job commits) and loaded. The rehearsal builds, tests, saves to a temporary folder and loads inside the undo step, then rolls it all back. A family name the project already has, or an existing file, is refused unless `overwrite: true`. Then `parameterValues` decides whose type values win: `family` (default) or `project`.

```json
{
  "jobId": "2026-11-28T22-00-00Z-hotel-bed-family",
  "title": "Hotel double bed family from data/families/hotel-double-bed.json",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "bed", "op": "family.create", "args": {
        "name": "Hotel Double Bed", "category": "Furniture", "outputDir": "C:/Projects/2417/families/hotel",
        "parameters": [
          {"name": "Width", "spec": "length"},
          {"name": "Depth", "spec": "length"},
          {"name": "Height", "spec": "length"},
          {"name": "Size Source", "spec": "text", "group": "identityData"},
          {"name": "Headboard Height", "spec": "length"},
          {"name": "Headboard Thickness", "spec": "length"},
          {"name": "Mattress Thickness", "spec": "length"},
          {"name": "Pillow Zone", "spec": "length"},
          {"name": "Base Height", "spec": "length", "formula": "Height - Mattress Thickness"},
          {"name": "Frame Material", "spec": "material"},
          {"name": "Mattress Material", "spec": "material"}
        ],
        "types": [
          {"name": "Double 1500x2000", "values": {"Height": 550, "Headboard Height": 1000, "Headboard Thickness": 80, "Mattress Thickness": 200, "Pillow Zone": 450, "Frame Material": "Oak", "Mattress Material": "Fabric - White", "Width": 1500, "Depth": 2000, "Size Source": "furniture.uk.adm1.double_bed_principal.width/.length; height typical, verify"}},
          {"name": "Double 1350x1900", "values": {"Height": 550, "Headboard Height": 1000, "Headboard Thickness": 80, "Mattress Thickness": 200, "Pillow Zone": 450, "Frame Material": "Oak", "Mattress Material": "Fabric - White", "Width": 1350, "Depth": 1900, "Size Source": "furniture.uk.adm1.double_bed_other.width/.length; height typical, verify"}},
          {"name": "King 1800x2000", "values": {"Height": 550, "Headboard Height": 1000, "Headboard Thickness": 80, "Mattress Thickness": 200, "Pillow Zone": 450, "Frame Material": "Oak", "Mattress Material": "Fabric - White", "Width": 1800, "Depth": 2000, "Size Source": "typical, verify"}}
        ],
        "planes": [
          {"name": "Left", "axis": "x", "at": -750},
          {"name": "Right", "axis": "x", "at": 750},
          {"name": "Front", "axis": "y", "at": -2000},
          {"name": "Top", "axis": "z", "at": 550},
          {"name": "Headboard Front", "axis": "y", "at": -80},
          {"name": "Pillow Line", "axis": "y", "at": -450},
          {"name": "Base Top", "axis": "z", "at": 350},
          {"name": "Headboard Top", "axis": "z", "at": 1000}
        ],
        "dimensions": [
          {"planes": ["Left", "Right"], "parameter": "Width"},
          {"planes": ["Left", "Center (Left/Right)", "Right"], "equal": true},
          {"planes": ["Front", "Center (Front/Back)"], "parameter": "Depth"},
          {"planes": ["Ref. Level", "Top"], "parameter": "Height"},
          {"planes": ["Headboard Front", "Center (Front/Back)"], "parameter": "Headboard Thickness"},
          {"planes": ["Pillow Line", "Center (Front/Back)"], "parameter": "Pillow Zone"},
          {"planes": ["Ref. Level", "Base Top"], "parameter": "Base Height"},
          {"planes": ["Ref. Level", "Headboard Top"], "parameter": "Headboard Height"}
        ],
        "forms": [
          {"id": "base", "kind": "extrusion", "box": {"x": ["Left", "Right"], "y": ["Front", "Headboard Front"], "z": ["Ref. Level", "Base Top"]}, "material": "Frame Material"},
          {"id": "mattress", "kind": "extrusion", "box": {"x": ["Left", "Right"], "y": ["Front", "Headboard Front"], "z": ["Base Top", "Top"]}, "material": "Mattress Material"},
          {"id": "headboard", "kind": "extrusion", "box": {"x": ["Left", "Right"], "y": ["Headboard Front", "Center (Front/Back)"], "z": ["Ref. Level", "Headboard Top"]}, "material": "Frame Material"}
        ],
        "symbols": [
          {"rect": {"x": ["Left", "Right"], "y": ["Front", "Center (Front/Back)"]}},
          {"line": {"on": "Pillow Line", "from": "Left", "to": "Right"}}
        ],
        "flex": {"x": "Width", "y": "Depth", "z": "Headboard Height"} } }
  ]
}
```

Report `bed.file`, `bed.types` and every `bed.flex` entry (each type's measured size against Width, Depth and Headboard Height). Also report the `Size Source` of each type: "typical, verify" sizes need the user's confirmation. Lint already refuses most recipe mistakes before Revit runs: an undeclared name, a formula loop, a face that does not start on its plane, a dimension that over-constrains its planes, or a size with no value in some type.

```json
{
  "jobId": "2026-11-28T22-10-00Z-hotel-beds-place",
  "title": "Place hotel double beds in rooms 301 and 302",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "beds", "op": "family.place", "args": {
        "type": "Hotel Double Bed : Double 1500x2000", "points": [[4200, 12000], [8400, 12000]], "level": "Level 03", "rotation": 0, "mark": "BED" } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

The family's origin is the middle of the headboard wall line, so place the points on the wall's inner face. Untested in the app, and the most fragile steps are locking faces to planes (`NewAlignment` in the plan and front views) and the labelled dimensions moving those planes for every type. The flex test catches both, but only Revit can show whether a recipe passes. If the report warns that a family document stayed open, ask the user to close it in Revit without saving.

## 4. No escape hatch

Revit has no script op. When no typed op covers a task, say so, offer the nearest typed op, or describe the manual steps for the user to do in Revit.

## 5. Version notes

- One source tree builds for 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) and 2027 (.NET 10). The 2025 and 2026 builds are expected to keep loading on updates that run on .NET 10; to be confirmed.
- `export.ifc`: `ifc2x3` and `ifc4` map to the same coordination views in every release; `ifc4x3` maps to `IFC4x3` in 2024 to 2026 and `IFC4x3RV` in 2027. The `setup` field is refused. Whether the installed exporter accepts the 4x3 variants is to be confirmed.
- `walltype.ensure` always requires a core layer, so it behaves the same although 2026 and 2027 allow an empty core.
- Shared parameter group labels and warning texts follow the Revit UI language; checks that match warning text are written for English.
