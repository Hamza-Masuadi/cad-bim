# AutoCAD: what the engine does, and recipes

Full AutoCAD 2024 to 2027 on Windows (not LT, not Mac). 33 ops, all 2D. Field details: `jobs/autocad.md`. Loop and approval: `workflow.md`. Every op is "compiles, untested in AutoCAD" until the owner's first test round.

Contents: 1 What the engine can do · 2 Rules every AutoCAD job follows · 3 Recipes · 4 Escape hatch · 5 Version notes

## 1. What the engine can do

| Area | Ops |
|---|---|
| Set-up from a standard | `doc.setup` (units, precision, angles, linetype scaling, annotation scales, and with `applyStandard` every layer, linetype, text and dimension style the standard defines), `layer.ensure`, `linetype.ensure`, `textstyle.ensure`, `dimstyle.ensure` |
| Plans | `wall.draw` (double-line walls from centrelines, junctions cleaned, ends capped, optional hatch), `opening.place` (door or window block cut into a `wall.draw` wall), `grid.draw`, `room.create` (closed boundary, area, tag) |
| Annotation | `dim.linear`, `dim.string`, `text.add` (model, plotted or named height), `mleader.add`, `table.create` |
| Blocks and data | `block.define`, `block.insert`, `block.attrs.set`, `block.dynprops.set`, `schedule.fromBlocks` (attributes to a table and/or CSV) |
| Sheets and output | `layout.create`, `viewport.create`, `viewports.layout` (packs several views on a sheet), `titleblock.fill`, `plot.pdf` |
| References | `xref.attach`, `xref.repath` |
| Clean-up | `entity.setprops`, `layer.translate` (destructive), `fix.bylayer`, `fix.flattenZ`, `fix.purge` (destructive), `fix.units` |
| Escape hatch | `script.lisp` (section 4) |
| Read-only methods | `snapshot`, `audit` (58 checks in `data/checks/autocad.json`, groups such as `standards`, `layers`, `annotation`, `layouts`, `xrefs`, `cleanup`, `health`), `capture` (`layout:<name>`, `model`, `activeView`, `activeLayout` to PNG or PDF), `probe` |

Not in wave 1 (do not fake them; see `users.md`): free 2D geometry (lines, arcs, polylines), stand-alone hatches, move/copy/rotate/mirror/erase, find-and-replace text, revision clouds, layer states, module grids, clearance checks, block data extraction, area reports, sheet sets, batch over closed drawings, drawing compare.

## 2. Rules every AutoCAD job follows

- **Units.** Lengths are in the job's `units.length`; the engine converts with the drawing's INSUNITS. A unitless drawing refuses every op except `doc.setup` and `fix.units`. A units change (`doc.setup insUnits`, `fix.units`) should be alone or last in its job.
- **Paper sizes** (`plottedHeight`, annotative text and style heights, viewport `paperCentre`, `width`, `height`, layout `area`, `margin`, `gapX`, `gapY`): a plain number is millimetres for a metric job and inches for an imperial job, whatever the job unit. Model sizes stay in the job unit.
- **Layers.** An op uses the layer the job gives, else the standard's layer for that kind of object, else the current layer (with a warning). A missing layer is an error: create it first with `layer.ensure` in the same job. Defpoints is refused; layer 0 gives a warning in drafting ops and is refused by the sheet and clean-up ops.
- **Ensure ops** leave existing objects alone unless `update: true`.
- **Dimensions** never carry overrides; `textOverride` must keep the measured value (`"<> CLR"`). Dimensions are not associative (the API offers no association).
- **Blocks** for doors and windows: X along the wall, leaf to +Y, hinge at −X, origin at the centre or the hinge jamb. Unit-sized blocks (about 1 wide) are scaled to the opening width.
- **System variables** the job touches are restored at the end, except the ones `doc.setup` sets on purpose.

## 3. Recipes

Each recipe is one job (or a short sequence of jobs) that passed `sa job lint`. Replace the `jobId` with a fresh one, `document` with the snapshot's identity, and placeholder names (blocks, layouts, paths) with real ones. The recipes use the US-style preset copied into the project folder as `project-standard.json`, in inches; the same ops work in millimetres with the ISO-style preset. The `verify.checks` look at the whole drawing, not only the new work: run `sa acad audit --checks <ids>` first and drop any check the drawing already fails at error level, or the job rolls back for an old problem.

### 3.1 Set up a drawing from the standard (drafters, BIM coordinators)

Alone in its job because it sets the units. Ask for the standard first (`standards.md`). Checks: units and linetype scaling.

```json
{
  "jobId": "2026-11-28T09-00-00Z-setup",
  "title": "Drawing set-up from the project standard",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "setup", "op": "doc.setup", "args": {
        "insUnits": "in", "lengthFormat": "architectural", "lengthPrecision": 4,
        "measurement": "imperial", "psLtScale": true, "msLtScale": true,
        "annotationScales": ["1/8\" = 1'-0\"", "1/4\" = 1'-0\"", "1/2\" = 1'-0\""],
        "currentAnnotationScale": "1/8\" = 1'-0\"", "applyStandard": true } }
  ],
  "verify": { "checks": ["ac-012.insunits", "ac-046.msltscale", "ac-046.psltscale"] }
}
```

### 3.2 Walls, doors, windows and rooms (architects, interior designers, kitchen and bath, drafters)

Walls from centrelines; the chain closes the room, the extra wall makes two T junctions. Openings must sit inside one wall segment, away from junctions and other openings. Rooms trace the `wall.draw` loops.

```json
{
  "jobId": "2026-11-28T09-10-00Z-l01-offices",
  "title": "Level 1 offices: walls, doors, window, rooms",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [
        { "name": "A-WALL" }, { "name": "A-DOOR" }, { "name": "A-GLAZ" }, { "name": "A-AREA" } ] } },
    { "id": "walls", "op": "wall.draw", "args": {
        "thickness": 4.875, "layer": "A-WALL",
        "chains": [ { "points": [[0, 0], [240, 0], [240, 180], [0, 180]], "closed": true, "idPrefix": "rm" } ],
        "walls": [ { "id": "split", "start": [120, 0], "end": [120, 180] } ] } },
    { "id": "d101", "op": "opening.place", "args": {
        "kind": "door", "block": "DOOR-SINGLE", "host": "$walls", "wallId": "rm1", "offset": 60,
        "width": 36, "hinge": "start", "swingSide": "left", "layer": "A-DOOR", "mark": "101" } },
    { "id": "d102", "op": "opening.place", "args": {
        "kind": "door", "block": "DOOR-SINGLE", "host": "$walls", "wallId": "rm1", "offset": 180,
        "width": 36, "hinge": "end", "swingSide": "left", "layer": "A-DOOR", "mark": "102" } },
    { "id": "w01", "op": "opening.place", "args": {
        "kind": "window", "block": "WINDOW-FIXED", "host": "$walls", "wallId": "rm2", "offset": 90,
        "width": 48, "sillHeight": 30, "layer": "A-GLAZ", "mark": "W01" } },
    { "id": "r101", "op": "room.create", "args": {
        "inside": [60, 90], "from": "$walls", "name": "OFFICE", "number": "101", "layer": "A-AREA", "areaUnit": "ft2" } },
    { "id": "r102", "op": "room.create", "args": {
        "inside": [180, 90], "from": "$walls", "name": "MEETING", "number": "102", "layer": "A-AREA", "areaUnit": "ft2" } }
  ],
  "verify": { "checks": ["ac-001.layer-zero", "ac-022.duplicate-lines"], "capture": [ { "target": "activeView" } ] }
}
```

Report: wall lengths and junction count from `walls`, opening marks, room areas from `r101.area` and `r102.area`. The wall hatch, if any, is not cut at openings (warning).

### 3.3 Grid and grid dimensions

```json
{
  "jobId": "2026-11-28T09-20-00Z-grid",
  "title": "Structural grid with dimension strings",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "S-GRID" }, { "name": "A-ANNO-DIMS" } ] } },
    { "id": "grid", "op": "grid.draw", "args": {
        "orthogonal": { "origin": [0, 0], "xSpacings": [360, 360, 360], "ySpacings": [300, 300] },
        "layer": "S-GRID" } },
    { "id": "dimX", "op": "dim.string", "args": {
        "points": [[0, 600], [360, 600], [720, 600], [1080, 600]], "offset": 96,
        "orientation": "horizontal", "overall": true, "dimStyle": "ARCH-ANNO", "layer": "A-ANNO-DIMS" } },
    { "id": "dimY", "op": "dim.string", "args": {
        "points": [[1080, 0], [1080, 300], [1080, 600]], "offset": -96,
        "orientation": "vertical", "overall": true, "dimStyle": "ARCH-ANNO", "layer": "A-ANNO-DIMS" } }
  ],
  "verify": { "checks": ["ac-009.dim-override", "ac-004.dims-on-dim-layer"] }
}
```

`offset` is positive to the left of the string direction: the horizontal string runs left to right, so +96 puts it above; the vertical string runs upwards, so −96 puts it on the right.

### 3.4 Notes and leaders at plotted height

```json
{
  "jobId": "2026-11-28T09-30-00Z-notes",
  "title": "General notes and finish leaders",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "A-ANNO-NOTE" } ] } },
    { "id": "n1", "op": "text.add", "args": {
        "text": "GENERAL NOTES\\P1. ALL DIMENSIONS TO FACE OF STUD.\\P2. VERIFY ALL DIMENSIONS ON SITE.",
        "position": [0, -60], "heightName": "body", "scale": "1/8\" = 1'-0\"", "multiline": true,
        "width": 240, "annotative": true, "style": "NOTES", "layer": "A-ANNO-NOTE" } },
    { "id": "l1", "op": "mleader.add", "args": {
        "text": "CPT-01 CARPET TILE", "arrowPoint": [60, 90], "landingPoint": [100, 140], "layer": "A-ANNO-NOTE" } }
  ],
  "verify": { "checks": ["ac-014.text-height"] }
}
```

### 3.5 Furniture, FF&E, fixture, planting and lighting layout (interior designers, space planners, kitchen and bath, retail and hospitality, landscape, lighting)

The agent computes the positions (desk runs, seating, planting at spread). Dynamic blocks take `dynamicProperties`; values are checked against the block's allowed values before anything changes.

```json
{
  "jobId": "2026-11-28T10-00-00Z-ffe-layout",
  "title": "Open office desks and chairs",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FFE-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "I-FURN" } ] } },
    { "id": "desks", "op": "block.insert", "args": {
        "name": "DESK-DYN", "positions": [[300, 120], [372, 120], [444, 120]], "layer": "I-FURN",
        "attributes": { "CODE": "FF-01", "DESCRIPTION": "DESK 72 x 30" },
        "dynamicProperties": { "Width": 72 } } },
    { "id": "chairs", "op": "block.insert", "args": {
        "name": "CHAIR-TASK", "positions": [[336, 96], [408, 96], [480, 96]], "rotation": 180, "layer": "I-FURN",
        "attributes": { "CODE": "FF-02", "DESCRIPTION": "TASK CHAIR" } } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

### 3.6 Schedules from block attributes (FF&E, door, fixture, planting, signage schedules)

Run after the blocks are in the drawing (a separate job from the layout). `groupIdentical` gives one row per distinct combination with a count column.

```json
{
  "jobId": "2026-11-28T10-10-00Z-ffe-schedule",
  "title": "FF&E schedule",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FFE-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "A-ANNO-SCHD" } ] } },
    { "id": "ffe", "op": "schedule.fromBlocks", "args": {
        "blocks": ["DESK-DYN", "CHAIR-TASK"], "attributes": ["CODE", "DESCRIPTION"],
        "headers": ["CODE", "DESCRIPTION"], "sortBy": ["CODE"], "groupIdentical": true,
        "table": { "position": [1200, 600], "title": "FF&E SCHEDULE", "layer": "A-ANNO-SCHD" },
        "csv": "ffe-schedule.csv" } },
    { "id": "doors", "op": "schedule.fromBlocks", "args": {
        "blocks": ["DOOR-SINGLE"], "attributes": ["MARK", "WIDTH", "HEIGHT"],
        "headers": ["DOOR", "WIDTH", "HEIGHT"], "sortBy": ["MARK"],
        "table": { "position": [1200, 300], "title": "DOOR SCHEDULE", "layer": "A-ANNO-SCHD" } } }
  ]
}
```

Nested references are counted, not read; only references placed directly in model space (or every layout with `space: "all"`) give rows.

### 3.7 Room finish schedule (interior designers, facility managers)

Write finish codes onto the room tags, then schedule them. The tag block must already carry the finish attribute tags (here `FLOOR`, `WALLS`, `CEILING`); if it does not, say so: redefining a block does not add attributes to existing references (there is no attribute sync in the API), so the tags would have to be re-placed, which needs the user's agreement.

```json
{
  "jobId": "2026-11-28T10-20-00Z-finish-schedule",
  "title": "Room finish schedule",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FIN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "f101", "op": "block.attrs.set", "args": {
        "block": "ROOM-TAG", "where": { "NUMBER": "101" },
        "values": { "FLOOR": "CPT-01", "WALLS": "PT-01", "CEILING": "ACT-01" } } },
    { "id": "f102", "op": "block.attrs.set", "args": {
        "block": "ROOM-TAG", "where": { "NUMBER": "102" },
        "values": { "FLOOR": "LVT-01", "WALLS": "PT-02", "CEILING": "GYP-01" } } },
    { "id": "sched", "op": "schedule.fromBlocks", "args": {
        "blocks": ["ROOM-TAG"], "attributes": ["NUMBER", "NAME", "FLOOR", "WALLS", "CEILING"],
        "sortBy": ["NUMBER"],
        "table": { "position": [1200, 900], "title": "ROOM FINISH SCHEDULE", "layer": "A-ANNO-SCHD" },
        "csv": "room-finish-schedule.csv" } }
  ]
}
```

A finish legend is a `table.create` with codes and descriptions; hatch swatches in the legend are wave 2.

### 3.8 Sheets: layout, viewports, title block, then plot

Two jobs: `plot.pdf` must be alone. `area` is the drawable area inside the title block, in paper inches here (millimetres for a metric job); without it the layout's limits are used, which is refused when the layout has a title block.

```json
{
  "jobId": "2026-11-28T11-00-00Z-sheet-a101",
  "title": "Sheet A-101",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "lay", "op": "layout.create", "args": {
        "name": "A-101", "paperSize": "ARCH_D_(36.00_x_24.00_Inches)", "plotStyleTable": "monochrome.ctb",
        "titleBlock": "TB-ARCH-D", "titleBlockAttributes": { "SHEET_NO": "A-101", "SHEET_TITLE": "LEVEL 1 FLOOR PLAN" } } },
    { "id": "vps", "op": "viewports.layout", "args": {
        "layout": "A-101", "area": { "min": [1, 1], "max": [30, 23] },
        "views": [
          { "id": "plan", "modelWindow": [[-120, -120], [1200, 720]], "scale": "1/8\" = 1'-0\"", "title": "LEVEL 1 FLOOR PLAN" },
          { "id": "enl", "modelWindow": [[0, 0], [240, 180]], "scale": "1/4\" = 1'-0\"", "title": "ENLARGED OFFICE PLAN" } ],
        "margin": 0.5, "gapX": 1, "gapY": 1 } },
    { "id": "tb", "op": "titleblock.fill", "args": {
        "layouts": ["A-101"],
        "values": { "PROJECT_NAME": "HARBOUR OFFICES", "PROJECT_NO": "2417", "SCALE": "AS NOTED", "ISSUE_DATE": "{date}" } } }
  ],
  "verify": { "checks": ["ac-015.viewport-locked", "ac-036.viewport-scale"], "capture": [ { "target": "layout:A-101" } ] }
}
```

```json
{
  "jobId": "2026-11-28T11-05-00Z-plot-a101",
  "title": "Plot A-101",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "pdf", "op": "plot.pdf", "args": {
        "layouts": ["A-101"], "plotStyleTable": "monochrome.ctb", "fileName": "{sheetNumber} - {sheetName}.pdf" } }
  ]
}
```

Look at the capture before reporting. The plot report lists each file with its page count; an existing file fails the op unless `overwrite: true`. Paper size names are the device's canonical media names (`ISO_full_bleed_A1_(841.00_x_594.00_MM)`, `ARCH_D_(36.00_x_24.00_Inches)`).

### 3.9 Audit, then fix (drafters, BIM coordinators, facility managers converting legacy CAD)

Audit first (no approval needed), show the ranked findings, agree what to fix, then one fix job. `layer.translate` and `fix.purge` are destructive: list the layer map and the purge kinds in the plan. With a standard in use, `fix.purge` keeps every name the standard relies on, and `ac-020.purge` does not count those names, so an empty standard layer is neither removed nor reported.

```
sa acad audit --group standards --out audit.json
sa acad audit --group cleanup
```

```json
{
  "jobId": "2026-11-28T11-30-00Z-cleanup",
  "title": "Standards clean-up",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/received/consultant-plan.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "translate", "op": "layer.translate", "args": {
        "map": { "WALLS": "A-WALL", "DOORS": "A-DOOR", "FURN*": "I-FURN" } } },
    { "id": "bylayer", "op": "fix.bylayer", "args": {} },
    { "id": "flat", "op": "fix.flattenZ", "args": {} },
    { "id": "purge", "op": "fix.purge", "args": { "kinds": ["all"] } }
  ],
  "verify": { "checks": ["ac-003.color-bylayer", "ac-003.linetype-bylayer", "ac-003.lineweight-bylayer", "ac-027.flat", "ac-020.purge"] }
}
```

Report before and after counts from each op's readback. Entities on locked layers are skipped and counted; solids, splines and 3D polylines are never flattened. A drawing drawn in the wrong unit needs `fix.units` with `scaleContents: true`, alone in its own job.

### 3.10 Xref repair (drafters, BIM coordinators)

Alone, because a reload is not undone if a later op fails. Without `names` it repairs every unresolved reference; a reference whose file is not found is reported and left as it was.

```json
{
  "jobId": "2026-11-28T11-45-00Z-xref-repair",
  "title": "Repair xref paths",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "ops": [
    { "id": "repath", "op": "xref.repath", "args": {
        "searchFolders": ["C:/Projects/2417/xref", "C:/Projects/2417/consultants"], "pathType": "relative" } }
  ],
  "verify": { "checks": ["ac-017.xref-unresolved", "ac-017.xref-full-path"] }
}
```

The rehearsal does not reload; the commit does. Binding xrefs is not available.

### 3.11 Fit-out from a base build, existing and demolition (interior designers; landscape site bases)

Reference the base build as an overlay on the standard's xref layer, then set up the status layers. Walls to be demolished: ask the user to select them, take the handles from `sa acad snapshot --sections selection`, and move them in a second job.

```json
{
  "jobId": "2026-11-28T12-00-00Z-fitout-base",
  "title": "Fit-out base and status layers",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-PLAN-L03.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [
        { "name": "G-ANNO-REFR" }, { "name": "I-WALL" }, { "name": "A-WALL-E" }, { "name": "A-WALL-D" } ] } },
    { "id": "base", "op": "xref.attach", "args": {
        "path": "C:/Projects/2417/xref/2417-BASE-L03.dwg", "mode": "overlay", "layer": "G-ANNO-REFR" } }
  ],
  "verify": { "checks": ["ac-017.xref-unresolved"] }
}
```

```json
{
  "jobId": "2026-11-28T12-10-00Z-demo-walls",
  "title": "Mark walls for demolition",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-PLAN-L03.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "ops": [
    { "id": "demo", "op": "entity.setprops", "args": { "handles": ["2F1A", "2F1B", "2F20"], "setLayer": "A-WALL-D" } }
  ]
}
```

Whether the rehearsal of `xref.attach` leaves an unreferenced definition behind is still to be confirmed in AutoCAD; check the rehearsal report for `rehearsalLeaked` before committing.

### 3.12 Review markups and counts (accessibility reviewers, facility managers)

Measuring and certifying are not available: the reviewer's findings come from the snapshot, schedules and design data, and are written up as notes. Mark findings with leaders on a markup layer, then plot in a separate job. Never say a design "complies"; say what was measured, against which figure and jurisdiction, and what could not be told from the drawing.

```json
{
  "jobId": "2026-11-28T13-00-00Z-review-notes",
  "title": "Accessibility review notes",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/review/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "A-ANNO-REVC" } ] } },
    { "id": "n1", "op": "mleader.add", "args": {
        "text": "R1: DOOR 102 CLEAR WIDTH NOT SHOWN - CANNOT TELL", "arrowPoint": [180, 0], "landingPoint": [220, -40],
        "layer": "A-ANNO-REVC" } },
    { "id": "count", "op": "schedule.fromBlocks", "args": {
        "blocks": ["WC-ACCESSIBLE", "WC-STANDARD"], "attributes": ["TYPE"], "groupIdentical": true,
        "csv": "wc-count.csv" } }
  ]
}
```

## 4. Escape hatch: `script.lisp`

For an edit no typed op covers (trim, extend, fillet, offset). Rules in `workflow.md` section 11: alone in its job, no `verify.checks`, `--allow-script` on validate and run, rehearsal first (code checked, not run), cannot be rolled back if the job fails afterwards, no prompts or commands. Show the code in the plan.

```json
{
  "jobId": "2026-11-28T14-00-00Z-offset-boundary",
  "title": "Offset site boundary 3 ft inwards",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-L-SITE.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "ops": [
    { "id": "lisp", "op": "script.lisp", "args": {
        "code": "(progn (vl-load-com) (vla-Offset (vlax-ename->vla-object (handent \"3A7\")) -36.0))",
        "purpose": "Offset the site boundary polyline (handle 3A7) by 36 in to the inside" } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

```
sa acad validate job.json --allow-script
sa acad run job.json --rehearse --allow-script
sa acad run job.json --allow-script
```

The sign of an offset distance depends on the polyline's direction; say so in the plan and check the capture. Evaluating LISP inside the engine has not been tested in AutoCAD yet; it is the first item on the test plan.

## 5. Version notes

- One source tree builds for 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) and 2027 (.NET 10). No op behaves differently by release in wave 1; the drafting, sheet and fix handlers use no version-specific API.
- The .NET 8 builds are expected to load in the later 2025 and 2026 updates that run on .NET 10; this is to be confirmed in testing.
- `layout.create` and `plot.pdf` default to the stock `DWG To PDF.pc3` device; paper size names must come from its media list.
- Batch work over closed drawings (core console) is not built in wave 1; open each drawing in AutoCAD instead.
