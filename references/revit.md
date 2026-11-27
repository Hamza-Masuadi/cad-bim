# Revit: what the engine does, and recipes

Revit 2024 to 2027 on Windows. 39 ops. Field details: `jobs/revit.md`. Loop and approval: `workflow.md`. Every op is "compiles for all four releases, untested in Revit" until the owner's first test round.

Contents: 1 What the engine can do · 2 Rules every Revit job follows · 3 Recipes · 4 No escape hatch · 5 Version notes

## 1. What the engine can do

| Area | Ops |
|---|---|
| Datums | `level.ensure` (with plan and ceiling views), `grid.create` (lines or an orthogonal grid, letters skip I and O) |
| Modelling | `walltype.ensure` (layered wall type with function, material and thickness; missing materials are created plain), `wall.create` (start and end, or a path, optionally closed), `door.place`, `window.place`, `floor.create` and `ceiling.create` (from points or from a room), `roof.footprint` (slopes, overhang, gable edges), `room.place` (at a point or every enclosed region, with finish parameters), `room.separator` |
| Families, types and data | `family.load`, `type.duplicate`, `param.set` (by ids or by category, level, type and parameter values), `param.shared.ensure` |
| Views | `view.plan.create`, `view.ceiling.create`, `view.section.create`, `view.elevation.create`, `view.3d.create`, `view.rename`, `viewtemplate.apply`, `view.filter.ensure` |
| Sheets and annotation | `sheet.create`, `viewport.place`, `viewports.layout`, `schedule.create`, `tag.all`, `dim.grids`, `text.note`, `revision.ensure` |
| Output | `export.pdf`, `export.dwg`, `export.ifc`, `export.image` |
| Clean-up (destructive) | `element.delete`, `cleanup.unplacedRooms`, `cleanup.unusedViews`, `workset.assign` |
| Read-only methods | `snapshot`, `audit` (62 checks in `data/checks/revit.json`, groups such as `rooms`, `views`, `sheets`, `data`, `warnings`, `links`, `worksharing`, `families`, `export`), `capture` (`activeView`, a view id or name, `sheet:<number>`, to PNG, JPG or PDF), `probe` |

Not in wave 1 (do not fake them; see `users.md`): placing furniture, fixtures or any free family instance, groups, move/copy/rotate, phases on existing elements, materials and compound types beyond `walltype.ensure`, drafting views and detail lines, callouts, crop regions, colour fills, single tags, revision clouds, area plans, links, purge, schedule or data export to CSV or Excel, Navisworks export, keynote legends, clash detection, model compare.

## 2. Rules every Revit job follows

- **Lengths** are in the job's `units.length`; areas come back in m² for metric jobs and ft² for imperial. Angles are degrees.
- **Paper sizes** (`centre`, `area`, `margin`, gaps, tag `offset`, `dim.grids` offsets) are millimetres on the printed sheet for a metric job, inches for an imperial job.
- **Names must match exactly.** Types are written `"Family : Type"`, or a type name that is unique in its category. Take names from the snapshot (`wallTypes`, `floorTypes`, `ceilingTypes`, `roofTypes`, `familiesAndTypes`, `viewFamilyTypes`, `viewTemplates`); an ambiguous name is refused with the candidates.
- **References**: `"$id.field.0"` picks one element from an earlier op (`"$walls.walls.0"`, `"$r101.rooms.0"`, `"$plan.view"`, `"$sheet.sheet"`). A bare `"$id"` refers to the whole result.
- **Ensure ops** compare an existing level, wall type, parameter or sheet with the request and refuse a difference unless `update: true`.
- **Never** save, synchronise, relinquish or close: the user does that.
- **Exports go in their own job**, with nothing else.
- **Workshared models**: elements created go to the active workset; `workset.assign` moves them afterwards.

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

The closed path makes walls 0 to 3 (south, east, north, west). Door offsets run from the wall's start to the door centre.

```json
{
  "jobId": "2026-11-28T09-10-00Z-l00-consult",
  "title": "Level 00 consulting rooms: partitions, doors, window",
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
          { "function": "finish2", "material": "Gypsum Wall Board", "thickness": 12.5 } ] } },
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

A room that is not enclosed fails its op (`allowNotEnclosed` keeps it at area 0, last phase only). Changing finishes later: `param.set` with `"filter": { "category": "Rooms", "where": { "Number": "00.01" } }`.

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

Exporting a schedule to CSV or Excel is wave 2; the schedule can go on a sheet with `viewport.place` and out through `export.pdf`.

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

```json
{
  "jobId": "2026-11-28T11-20-00Z-door-types",
  "title": "Load door family, add 826 x 2040 type",
  "app": "revit",
  "document": { "path": "C:/Projects/2417/2417-ARCH.rvt", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "mm" },
  "ops": [
    { "id": "load", "op": "family.load", "args": { "path": "C:/Projects/2417/families/Single-Flush.rfa" } },
    { "id": "dup", "op": "type.duplicate", "args": {
        "source": "Single-Flush : 0915 x 2134mm", "category": "Doors", "newName": "0826 x 2040mm",
        "parameters": { "Width": 826, "Height": 2040 } } }
  ],
  "verify": { "checks": ["rv-027.type-copy-names"] }
}
```

Loading places nothing; placing furniture or fixtures from the family is wave 2.

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

## 4. No escape hatch

Revit has no script op in wave 1. When no typed op covers a task, say so, offer the nearest typed op, or describe the manual steps for the user to do in Revit.

## 5. Version notes

- One source tree builds for 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) and 2027 (.NET 10). The 2025 and 2026 builds are expected to keep loading on updates that run on .NET 10; to be confirmed.
- `export.ifc`: `ifc2x3` and `ifc4` map to the same coordination views in every release; `ifc4x3` maps to `IFC4x3` in 2024 to 2026 and `IFC4x3RV` in 2027. The `setup` field is refused in wave 1. Whether the installed exporter accepts the 4x3 variants is to be confirmed.
- `walltype.ensure` always requires a core layer, so it behaves the same although 2026 and 2027 allow an empty core.
- Shared parameter group labels and warning texts follow the Revit UI language; checks that match warning text are written for English.
