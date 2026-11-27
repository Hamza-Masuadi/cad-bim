# AutoCAD: what the engine does, and recipes

Full AutoCAD 2024 to 2027 on Windows (not LT, not Mac). 47 ops, all 2D. Field details: `jobs/autocad.md`. Loop and approval: `workflow.md`. Every op is "compiles, untested in AutoCAD" until the owner's first test round; the 13 newest (drawing, hatching, editing, data, clearances, markups, materials) are the least proven, so say "written, untested in the app" when the user relies on them.

Contents: 1 What the engine can do · 2 Rules every AutoCAD job follows · 3 Recipes (3.1 to 3.12 drawing, sheets and clean-up; 3.13 to 3.21 geometry, editing, data, checks, markups and blocks for Revit) · 4 Escape hatch · 5 Version notes

## 1. What the engine can do

| Area | Ops |
|---|---|
| Set-up from a standard | `doc.setup` (units, precision, angles, linetype scaling, annotation scales, and with `applyStandard` every layer, linetype, text and dimension style the standard defines), `layer.ensure`, `linetype.ensure`, `textstyle.ensure`, `dimstyle.ensure` |
| Plans | `wall.draw` (double-line walls from centrelines, junctions cleaned, ends capped, optional hatch), `opening.place` (door or window block cut into a `wall.draw` wall), `grid.draw`, `room.create` (closed boundary, area, tag) |
| Free 2D geometry | `draw.entities` (lines, arcs, circles, polylines with bulges, points), `hatch.add` (pattern, solid or user hatch with islands, optionally associative; returns areas) |
| Editing | `entity.transform` (move, copy, rotate, mirror, scale; arrays with `copies`), `entity.delete` (destructive, `expectCount` required), `text.replace` (find and replace with a dry run) |
| Set-out | `grid.module` (ceiling, floor or wall tile grid in a boundary, with border sizes) |
| Annotation | `dim.linear`, `dim.string`, `text.add` (model, plotted or named height), `mleader.add`, `table.create`, `revcloud.add` (revision cloud with tag) |
| Blocks and data | `block.define`, `block.insert`, `block.attrs.set`, `block.dynprops.set`, `schedule.fromBlocks` (attributes to a table and/or CSV), `block.extract` (every reference with attributes, position and room, to CSV or JSON), `block.export` (each block to its own DWG plus `placements.json`, for Revit families), `area.report` (areas by layer or room, to a table, CSV or JSON) |
| Checks | `layout.clearances` (turning circles, door swings, routes and zones against furniture and fixtures; changes nothing unless it draws the zones) |
| Layers and materials | `layerstate.ensure` (named layer states for options and presentation), `material.ensure`, `material.assign` (render materials on layers or entities) |
| Sheets and output | `layout.create`, `viewport.create`, `viewports.layout` (packs several views on a sheet), `titleblock.fill`, `plot.pdf` |
| References | `xref.attach`, `xref.repath` |
| Clean-up | `entity.setprops`, `layer.translate` (destructive), `fix.bylayer`, `fix.flattenZ`, `fix.purge` (destructive), `fix.units` |
| Escape hatch | `script.lisp` (section 4) |
| Read-only methods | `snapshot`, `audit` (63 checks in `data/checks/autocad.json`, groups such as `standards`, `layers`, `annotation`, `layouts`, `xrefs`, `cleanup`, `health`, `visual`), `capture` (`layout:<name>`, `model`, `activeView`, `activeLayout` to PNG or PDF), `probe` |

Not available (do not fake them; `limits.md`): trim, extend, fillet and offset as typed ops (use the escape hatch, section 4), sheet sets, batch work over closed drawings, comparing two drawings, binding xrefs, 3D.

## 2. Rules every AutoCAD job follows

- **Units.** Lengths are in the job's `units.length`; the engine converts with the drawing's INSUNITS. A unitless drawing refuses every op except `doc.setup` and `fix.units`. A units change (`doc.setup insUnits`, `fix.units`) should be alone or last in its job.
- **Paper sizes** (`plottedHeight`, annotative text and style heights, viewport `paperCentre`, `width`, `height`, layout `area`, `margin`, `gapX`, `gapY`): a plain number is millimetres for a metric job and inches for an imperial job, whatever the job unit. Model sizes stay in the job unit.
- **Layers.** An op uses the layer the job gives, else the standard's layer for that kind of object, else the current layer (with a warning). A missing layer is an error: create it first with `layer.ensure` in the same job. Defpoints is refused; layer 0 gives a warning in drafting ops and is refused by the sheet and clean-up ops.
- **Ensure ops** leave existing objects alone unless `update: true`.
- **Dimensions** never carry overrides; `textOverride` must keep the measured value (`"<> CLR"`). Dimensions are not associative (the API offers no association).
- **Blocks** for doors and windows: X along the wall, leaf to +Y, hinge at −X, origin at the centre or the hinge jamb. Unit-sized blocks (about 1 wide) are scaled to the opening width.
- **System variables** the job touches are restored at the end, except the ones `doc.setup` sets on purpose.
- **Selecting by handle or filter.** `entity.transform`, `entity.delete` and `material.assign` take `handles` (from `sa acad snapshot --sections selection` or an earlier op) or a `filter` (layer, DXF type, block name, space). Give `expectCount` whenever you know the number: the op fails, and the job rolls back, if the selection differs. `entity.delete` requires it. Entities on locked layers are skipped and counted (delete refuses them).
- **Polyline and hatch bulges:** segment i runs from point i to point i+1 (a closed loop has one more, back to the first); a positive bulge arcs to the right of the direction of travel, 1 is a half circle.
- **Files written by ops** (`block.extract`, `area.report`, `schedule.fromBlocks`, the `layerstate.ensure` backup) are plain names ending `.csv` or `.json`, written into the job's output folder. A rehearsal writes them to a temporary folder. An existing file fails the op unless `overwrite: true`. `block.export` is different: it writes DWGs and `placements.json` into `outputDir`, and its files appear only after the job commits.
- **Hatch patterns** come from the drawing's pattern file (`acad.pat` for imperial drawings, `acadiso.pat` for metric), or `SOLID`, or `_USER` with `spacing`.

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

A finish legend is a `table.create` with codes and descriptions; draw a swatch beside each row with `hatch.add` and a point loop (recipe 3.13).

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

Certifying is never available. Measuring turning circles, door swings and routes is `layout.clearances` (recipe 3.16); door widths and mounting heights held in attributes come out with `block.extract` (recipe 3.17). Other findings come from the snapshot, schedules and design data, and are written up as notes. Mark findings with leaders (or clouds, recipe 3.18) on a markup layer, then plot in a separate job. Never say a design "complies"; say what was measured, against which figure and jurisdiction, and what could not be told from the drawing.

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

### 3.13 Free 2D drawing and hatching (landscape, drafters, interior finish plans, kitchen and bath, retail)

Hardscape edges, worktop outlines, stand plans, tree protection areas and finish-plan floor patterns are `draw.entities` plus `hatch.add`. A later op refers to a drawn entity by position in the `handles` output (`"$edges.handles.1"` is the second entity). Here the terrace has one curved edge (bulge 0.25 on the east side) and a round tree pit left unhatched as an island.

```json
{
  "jobId": "2026-11-28T15-00-00Z-terrace-paving",
  "title": "Terrace edge, tree pit and paving hatch",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-L-SITE.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "L-SITE-EDGE" }, { "name": "L-SITE-PAVE-PATT" } ] } },
    { "id": "edges", "op": "draw.entities", "args": { "layer": "L-SITE-EDGE", "entities": [
        { "id": "terrace", "kind": "polyline", "points": [[0, 0], [480, 0], [480, 240], [0, 240]], "bulges": [0, 0.25, 0, 0], "closed": true },
        { "id": "pit", "kind": "circle", "center": [120, 120], "radius": 30 } ] } },
    { "id": "paving", "op": "hatch.add", "args": {
        "boundaries": [ { "handle": "$edges.handles.0", "islands": [ { "handle": "$edges.handles.1" } ] } ],
        "pattern": "AR-HBONE", "scale": 1, "angle": 0, "layer": "L-SITE-PAVE-PATT", "associative": true } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

Report `paving.area` (net of the island, in square inches here: divide by 144 for square feet, or use `area.report`, recipe 3.17). The readback fails if AutoCAD's hatch area differs from the computed one by more than 1%. For a hatch on a boundary that has no entity yet, give `points` and `drawBoundary: true` (needed for `associative`). The loops of one hatch must lie at one elevation (a raised outline is hatched at its own Z), and an associative hatch over a boundary on a locked layer is refused. Untested in the app: associativity on drawn boundaries and the arc direction of curved loops; check the capture.

### 3.14 Move, copy and erase: as-built updates (facility managers, drafters)

Ask the user to select what moves or goes, take the handles from `sa acad snapshot --sections selection`, and list them in the plan with the count. Copies get new handles (`copy.handles`). Erasing is destructive: name the handles and the count.

```json
{
  "jobId": "2026-11-28T15-20-00Z-asbuilt-l01",
  "title": "As-built: move partition, add two desks, remove old counter",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "ops": [
    { "id": "move", "op": "entity.transform", "args": {
        "handles": ["3B0", "3B1", "3B2"], "move": { "from": [240, 0], "to": [276, 0] }, "expectCount": 3 } },
    { "id": "copy", "op": "entity.transform", "args": {
        "handles": ["4A7"], "move": { "from": [300, 120], "to": [372, 120] }, "copy": true, "copies": 2, "expectCount": 1 } },
    { "id": "erase", "op": "entity.delete", "args": { "handles": ["41C", "41D"], "expectCount": 2 } }
  ],
  "verify": { "checks": ["ac-022.duplicate-lines"], "capture": [ { "target": "activeView" } ] }
}
```

`copies: 2` makes two copies, each one step further on (a linear array); `rotate` with `copies` makes a polar array. `mirror` keeps text readable by default. A listed handle must be an entity in model space or on a layout: an attribute, a polyline vertex or an entity inside a block definition is refused (`not_entity`). Moving wall lines does not re-clean their junctions or re-cut openings; check the capture. Untested in the app: copies rolling back cleanly in a rehearsal, and readable mirrored text for every justification.

### 3.15 Ceiling and tile grid set-out (interior designers, kitchen and bath, lighting layouts)

`grid.module` lays the grid inside the boundary and reports the cut tile at each side. `balanced` picks, per direction, a centred tile or a centred line, whichever gives the larger border. Borders under `minBorder` of a module are listed in `smallBorders`: show them to the user, who may prefer another set-out. Run it with `"draw": false` first to compare set-outs without drawing anything.

```json
{
  "jobId": "2026-11-28T15-40-00Z-rcp-grid-101",
  "title": "Office 101 ceiling grid, 24 in tiles",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-RCP-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "A-CLNG-GRID" } ] } },
    { "id": "grid", "op": "grid.module", "args": {
        "boundary": [[0, 0], [117.5, 0], [117.5, 177.5], [0, 177.5]], "module": 24, "setOut": "balanced", "minBorder": 0.5,
        "layer": "A-CLNG-GRID" } }
  ]
}
```

Report `grid.borders`, `grid.smallBorders`, `grid.fullTiles` and `grid.cutTiles`. Light fittings go at tile centres: compute them from `grid.origin` and the module, then place them in a second job with `block.insert` (recipe 3.5). Floor and wall tiles work the same with the tile size as `module` (and `moduleY` for oblong tiles, `angle` for a turned grid). More than 250,000 tiles in the boundary is refused (`grid_too_dense`).

### 3.16 Clearance checks: turning circles, door swings, routes (accessibility reviewers, kitchen and bath, space planners, retail and hospitality)

`layout.clearances` tests zones against entities on named layers and references of named blocks, and lists every intrusion, deepest first, with how far it reaches into the zone. It changes nothing unless `drawZones` asks for the outlines (put them on a non-plotting review layer). The sizes are the user's: take turning circles, door clearances and route widths from the jurisdiction the user names (`practice/accessibility-review.md`, `data/design/`), and quote the source with the result.

```json
{
  "jobId": "2026-11-28T16-00-00Z-wc-clearances",
  "title": "Accessible WC 104: turning space, door swing, corridor",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/review/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "G-ANNO-REVW", "plot": false } ] } },
    { "id": "wc104", "op": "layout.clearances", "args": {
        "zones": [
          { "id": "WC104-turning", "kind": "circle", "centre": [60, 60], "radius": 30 },
          { "id": "D104-swing", "kind": "doorSwing", "hinge": [0, 120], "leafWidth": 36, "closedAngle": 0, "swing": 90, "side": "right", "ignore": ["2F40"] },
          { "id": "Corridor", "kind": "route", "points": [[-120, 150], [240, 150]], "width": 44 } ],
        "layers": ["I-FURN", "A-WALL"], "blocks": ["WC-*", "LAV-*", "GRAB-*"],
        "drawZones": { "layer": "G-ANNO-REVW" } } }
  ],
  "verify": { "capture": [ { "target": "activeView" } ] }
}
```

Report `wc104.clear`, then each item in `wc104.intrusions` (zone, handle, layer or block, depth). Say what was measured and against which figure; never "complies". Blocks are tested by their geometry (`blockGeometry: "extents"` uses their bounding box); xrefs are skipped with a warning. `failOnIntrusion: true` makes an intrusion roll the job back, which is useful when a layout job must leave every zone clear. Untested in the app: intrusion depths on exploded block geometry.

### 3.17 Block data and area export (facility managers, space planners, BIM coordinators, accessibility reviewers)

`block.extract` lists every reference with handle, attributes, dynamic properties, position, layer and the room it sits in; `area.report` measures closed boundaries, takes holes out, and totals by layer or room. Both can write a file and change nothing else (`area.report` also writes a table when asked). Rooms are closed polylines on the given layers, named by a tag block with NAME and NUMBER attributes inside them, or by the text `room.create` writes.

```json
{
  "jobId": "2026-11-28T16-20-00Z-ffe-register",
  "title": "FF&E register and room areas",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FFE-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "assets", "op": "block.extract", "args": {
        "blocks": ["DESK-*", "CHAIR-*"], "attributes": ["CODE", "DESCRIPTION"],
        "rooms": { "layers": ["A-AREA"], "tagBlock": "ROOM-TAG" }, "fileName": "ffe-register.csv" } },
    { "id": "areas", "op": "area.report", "args": {
        "filter": { "layers": ["A-AREA"], "types": ["polyline"] }, "rooms": { "layers": ["A-AREA"], "tagBlock": "ROOM-TAG" },
        "groupBy": "room", "areaUnit": "ft2",
        "table": { "position": [1200, 1200], "title": "ROOM AREAS", "layer": "A-ANNO-SCHD" }, "fileName": "room-areas.csv" } }
  ]
}
```

Report `assets.count` and `areas.total` with the file paths. Occupancy and density figures are worked out by the agent from these counts and areas, with the source of any target figure. A bulk update from a spreadsheet goes back in with `block.attrs.set`, matching on the handles in the file. These are plain measured areas, not BOMA or IPMS measurement. If a later op in the same job fails, files already written stay on disk although the job rolled back: say so if it happens. Untested in the app: dynamic properties and room assignment, and the table layout.

### 3.18 Revision clouds and text updates, with layout and visual checks (drafters, facility managers, accessibility reviewers)

Find the count first: rehearse a copy of the job with `text.replace` `"dryRun": true` (and no `replace`), read `count` and `changes`, then put that count in `expectCount`. Text holding fields is skipped and counted, and multiline text formatting is never changed. `verify.visual` compares the model window before and after the job (`workflow.md` section 15); the overlap check reports labels the job placed on top of each other.

```json
{
  "jobId": "2026-11-28T16-40-00Z-rev-b-markup",
  "title": "Revision B clouds and title text",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "A-ANNO-REVS" } ] } },
    { "id": "cloud1", "op": "revcloud.add", "args": {
        "rect": { "min": [180, -12], "max": [276, 60] }, "offset": 6, "layer": "A-ANNO-REVS",
        "tag": { "text": "B", "block": "REV-TAG", "attribute": "REV" } } },
    { "id": "rev", "op": "text.replace", "args": {
        "find": "REV A", "replace": "REV B", "matchCase": true, "wholeWord": true, "kinds": ["text", "attributes"],
        "space": "paper", "expectCount": 4 } }
  ],
  "verify": {
    "checks": ["ac-049.annotation-overlap"],
    "visual": { "target": "model", "window": [[-120, -120], [1200, 720]] }
  }
}
```

The cloud is a closed polyline of arcs (`cloud1.cloud`), not a REVCLOUD object. The tag block is assumed to be drawn at plotted size and scaled by the current annotation scale; check it on the capture. In an AutoCAD rehearsal the visual check is always `incomplete` (nothing can be plotted before the rollback); it compares on the commit.

### 3.19 Layer states for layout options and presentation plans (space planners, interior designers)

Option A and option B furniture on their own layers, with a named layer state for each. The op saves the drawing's current layer settings with the listed changes; layers not listed are saved as they are now. `restore: true` leaves the drawing in the last state.

```json
{
  "jobId": "2026-11-28T17-00-00Z-layout-options",
  "title": "Layer states for layout options A and B",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FFE-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "layers", "op": "layer.ensure", "args": { "layers": [ { "name": "I-FURN-OPTA" }, { "name": "I-FURN-OPTB" } ] } },
    { "id": "optA", "op": "layerstate.ensure", "args": {
        "name": "OPTION A", "description": "Layout option A",
        "layers": [ { "name": "I-FURN-OPTA", "on": true, "frozen": false }, { "name": "I-FURN-OPTB", "frozen": true } ] } },
    { "id": "optB", "op": "layerstate.ensure", "args": {
        "name": "OPTION B", "description": "Layout option B",
        "layers": [ { "name": "I-FURN-OPTA", "frozen": true }, { "name": "I-FURN-OPTB", "on": true, "frozen": false } ],
        "restore": true } }
  ]
}
```

Updating an existing state needs `update: true`; the old state is exported first as a `.las` file (`backup`). A rehearsal never touches a saved state: it saves and checks a temporary one and deletes it. Viewports with frozen layers (`viewport.create`) remain the way to show both options on one sheet. Untested in the app: saving a state from layer changes made inside the open job, and the rollback of a replaced state.

### 3.20 Finish materials (interior designers, kitchen and bath)

AutoCAD materials are render materials: they show in shaded and rendered views, not on a plotted 2D plan, where the finish code reaches the drawing through tags, notes and legends (recipes 3.4, 3.7). Name each material with its finish code first. A texture path must exist and be on a shared drive (a path under a user profile is refused unless `allowLocalPath`).

```json
{
  "jobId": "2026-11-28T17-20-00Z-finish-materials",
  "title": "Finish materials CT-01 and PT-01",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FIN-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "standard": "project-standard.json",
  "ops": [
    { "id": "mats", "op": "material.ensure", "args": { "materials": [
        { "name": "CT-01 Porcelain 24x24 Light Grey", "description": "Porcelain floor tile", "color": "#D0D0D0",
          "texture": { "path": "P:/library/textures/ct01.jpg", "width": 24, "projection": "planar" } },
        { "name": "PT-01 Paint Warm White", "description": "Wall paint", "color": "#F2EEE6" } ] } },
    { "id": "floor", "op": "material.assign", "args": { "material": "CT-01 Porcelain 24x24 Light Grey", "layers": ["I-FLOR-PATT"] } }
  ]
}
```

Untested in the app, and the most likely to be wrong: the texture scale (one 24 in repeat should cover one 24 in tile; check one tile in a rendered view before relying on it).

### 3.21 Furniture blocks to Revit families (interior designers, space planners, BIM coordinators)

`block.export` writes each chosen block definition to its own DWG (base point at the origin, the drawing's INSUNITS) and a `placements.json` listing every reference: handle, layer, position, rotation, signed scale and whether it is mirrored. In Revit, `family.fromcad` makes a family from each DWG and `family.place` with `fromPlacements` puts one instance at every reference. The drawing is not changed. The op is alone in its job, and its files reach `outputDir` only when the job commits.

Rehearse first. The rehearsal writes everything to a temporary folder, opens every DWG again and reads the placements file back, then deletes it all. Read `count`, `files` and `skipped`, put `count` in `expectCount`, and commit.

```json
{
  "jobId": "2026-11-28T17-40-00Z-furniture-to-revit",
  "title": "Export furniture blocks for Revit",
  "app": "autocad",
  "document": { "path": "C:/Projects/2417/2417-I-FFE-L01.dwg", "fingerprint": "copy-from-snapshot" },
  "units": { "length": "in" },
  "ops": [
    { "id": "ffe", "op": "block.export", "args": {
        "blocks": ["DESK-*", "CHAIR-*", "BED-*"], "layers": ["I-FURN"], "expectCount": 9,
        "outputDir": "C:/Projects/2417/export/ffe-L01", "overwrite": true } }
  ]
}
```

Report `ffe.count` blocks, `ffe.references` references, and every item in `ffe.skipped` with its reason. Selection: `blocks` (wildcards allowed) and `layers` filter together; `all: true` takes every named block with a reference in model space. A block named in `blocks` with no reference is still exported, with no references. Lengths in the file are in the drawing's own units (`source.units`, `source.insunits`), not the job's. Points are WCS. Rotation is counter-clockwise from X.

Things that work differently:
- A dynamic block gives one DWG per representation actually used, named `<block>__1`, `<block>__2` in the order the references are found. Revit gets one family per size, which is what the drawing shows.
- References nested inside other blocks are counted, not listed. Xrefs are never exported, and neither are MINSERT arrays, references tilted out of the plan, or blocks with no visible geometry (each one is in `skipped`).
- File names are the block names with characters Windows refuses, and a trailing dot or space, turned into `_`. Names that come out the same get `-2`, `-3`. A block named like a Windows device (CON, NUL, COM1) is refused: rename it first.

Untested in the app: everything about this op, in particular the base point shift on blocks whose base point is not at their geometry's origin, dynamic representations, and that HANDSEED and the fingerprint really stay unchanged.

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

- One source tree builds for 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8) and 2027 (.NET 10). No op behaves differently by release; no handler uses a version-specific API.
- The .NET 8 builds are expected to load in the later 2025 and 2026 updates that run on .NET 10; this is to be confirmed in testing.
- `layout.create` and `plot.pdf` default to the stock `DWG To PDF.pc3` device; paper size names must come from its media list.
- Batch work over closed drawings (core console) is not built; open each drawing in AutoCAD instead.
