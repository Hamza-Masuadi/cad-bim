# Limits: what is out of reach, and where each op stops

Say these plainly when they apply. What each user group can do now is in `users.md`.

Contents: 1 Out of reach · 2 Not yet proven · 3 AutoCAD op limits · 4 Revit op limits · 5 Audit limits · 6 Blind spots of the visual and layout checks

## 1. Out of reach

| Thing | Why |
|---|---|
| Revit LT, AutoCAD LT, AutoCAD for Mac | No API (LT) or the engine cannot load (Mac) |
| Civil, deep MEP and structural engineering | Outside building design scope |
| Revit's own Interference Check, clash detection | No API runs it; `export.nwc` hands the model to Navisworks |
| Editing DWG files without AutoCAD running | No licence-clean way to write DWG |
| Batch work over closed drawings or models, comparing two files | The engines work on the one open file |
| Turning IFC back into editable Revit elements | Linked IFC is not editable and import loses information |
| Running visual-programming graphs from outside Revit | No outside interface |
| Rendering, photoreal images, VR, mood boards | Different tools; materials can be set up, not rendered |
| Certifying code or accessibility compliance | The skill measures against stated figures and flags issues; legal compliance stays with the user and their consultants |
| Anything that needs a click in a dialog | The engine cannot answer dialogs; a dialog during a job fails it |
| Cloud-hosted models beyond what the open desktop model allows | Needs cloud sign-in and paid automation |
| Grading, contours, earthworks, irrigation, Revit site modelling | Need surface modelling and civil tools; landscape stays 2D in AutoCAD |
| Lighting calculations, circuiting, panel schedules | Engineering software; the skill places fittings only |
| MEP sizing and systems for interiors | Engineers own it |
| AutoCAD 3D modelling, including exhibition stands | The AutoCAD engine is 2D |
| AutoCAD trim, extend, fillet, offset as typed ops; sheet sets; binding xrefs | No typed op; trim to fillet only through `script.lisp` |
| Revit in-place families, design options, area schemes | No API creates them |
| Revit legend components, or a first legend view | No API places them; a legend can only be copied from an existing one |
| Aligning floor and ceiling surface patterns in Revit | No API member found; the skill can say what grid it wants |
| Publishing shared coordinates to a linked Revit model | It writes into the other team's file; acquiring is offered instead |
| Borrowing elements, synchronising, relinquishing | The user and the element's owner do these |
| FF&E procurement, pricing, specification writing, catalogues | Not drawing or modelling work |
| Live links to facility management software | Needs each product's own interface; CSV and JSON export is offered |
| A validated COBie workbook, BCF issue exchange | Need other tools or platforms |
| BOMA or IPMS measurement | Not in the data; area ops give plain measured areas |
| Saving, synchronising, relinquishing, closing | The user does these, always |

## 2. Not yet proven

Nothing has been run in AutoCAD or Revit. All 114 ops (46 AutoCAD, 68 Revit) compile for 2024 to 2027 and pass offline tests; their behaviour in the apps is still to be confirmed. Report what the engine returned, never "this works". Say "written, untested in the app" when the user relies on one of the newer ops below. Items most likely to need a fix after the first test round:

- **AutoCAD, older ops:** evaluating `script.lisp` code inside the engine; whether a rehearsal leaves anything behind (`rehearsalLeaked`); rolling back `xref.attach`; plotting while the job is open; building a layout by hand in `layout.create`; the text height of leaders with scaled styles.
- **AutoCAD, newer ops:**
  - `layerstate.ensure`: saving a state from layer changes made inside the open job; the rollback of a replaced state.
  - `material.ensure` and `material.assign`: the texture scale (one repeat per tile, or far off).
  - `entity.transform`: copies rolling back in a rehearsal; mirrored text readable for every justification.
  - `hatch.add`: associativity on drawn boundaries, arc direction of curved loops, the draw order table in a rehearsal, area within 1%.
  - `text.replace`: table cells, multiline attributes, formatting kept exactly.
  - `revcloud.add`: the scale of the tag block.
  - `area.report`: the table layout and the area cross-check on ellipses, splines and regions.
  - `layout.clearances`: depths on exploded block geometry; validation over blocks with attributes.
  - `block.extract`: dynamic properties and room assignment.
  - `grid.module`: rotated grids, cut tiles on irregular boundaries.
  - `draw.entities`, `entity.delete`: bulge direction on closed polylines; delete on a locked layer fails.
- **Revit, older ops:** grid references in `dim.grids`; measuring viewports in `viewports.layout` and the title block drawable-area guess (falls back to the sheet edge less 15 mm); doors and windows staying where placed (a drift over 0.5 mm fails the op); the not-enclosed versus redundant room rule; PDF and DWG export inside the job's transaction group (`export_refused` if Revit refuses); IFC export inside a rolled-back sub-transaction; roof edge offsets; level elevations on a model with a raised base point; the .NET 8 builds on .NET 10 updates.
- **Revit, newer ops:**
  - `family.edit`: editing and reloading a family inside the job's undo step (if Revit refuses, the op fails with nothing changed); project types taking the new values; load warnings reaching the report; formula text read back as written; new types starting from `copyFrom`; whether a reload deletes nested types; whether opening the family copy during checks counts as a document change.
  - `link.revit` acquire: shared positions and geolocation read back after acquiring.
  - `material.ensure`: appearance edits inside the job and their rollback, the library Generic appearance, texture units.
  - `fillpattern.ensure`: grid angle units, dash signs, updating an existing pattern.
  - `schedule.key.create`: one key per row, values on new fields; assigning keys to elements afterwards.
  - `dim.string`: door and window width references, walls whose location line is off centre.
  - `text.replace`: formatting and paragraph breaks in notes.
  - `layout.clearances`: door openings left open in wall footprints, furniture footprints, the height band.
  - `export.nwc`: exporter installed or not, file naming, view scope, coordinates.
  - `cleanup.purge`: the list against Revit's Purge Unused window; what depends on unused elements.
  - `family.place`: face-based and work-plane families, the offset parameter each family exposes, facing after rotation.
  - `view.crop.set`, `view.callout.create`, `colorfill.apply`, `tag.place`, `area.place`, `revision.cloud`: rotated views, callouts of sections, legend position, tag head positions, material and room tags, clouds on sheets.
  - `link.cad`: centred placement, this-view-only in sections, hidden layers, unit scaling.
  - `wallsweep.add`, `material.assign` paint, `element.transform` mirror and copy of hosted elements, `phase.set` with both phases changed, `group.place`, `compoundtype.ensure`: lower risk.
  - `ownership: "skip"`: owner and workset in the warnings, filters expanded the same way the op would select, joins that change a left-out wall reported.
  - `verify.visual`: image export inside the job, and whether it shows exactly the crop.

## 3. AutoCAD op limits

| Op | Limit |
|---|---|
| any op | 2D only. A unitless drawing refuses everything but `doc.setup` and `fix.units`. Defpoints is refused as a target layer |
| `wall.draw` | Cleans L, T and X junctions; endpoints within `tolerance` (default 1 mm) are joined, so near-misses larger than that stay open |
| `opening.place` | Hosts a `wall.draw` result or a wall line by handle; curved and polyline walls are not hosts; does not cut the wall hatch; refused past wall ends, across junctions or overlapping another opening |
| `room.create` | Traces lines, arcs and polylines only, not inside blocks or xrefs; islands (columns, shafts) are not subtracted |
| `dim.linear`, `dim.string` | Not associative (the managed API offers no association); no dimension overrides allowed |
| `block.define` (`fromFile`) | Re-creates lines, arcs, circles, polylines, text and attribute definitions; a source with hatches, MText, nested blocks, dimensions or splines is refused |
| `block.attrs.set` | Redefining a block does not add new attributes to existing references (no attribute sync) |
| `block.dynprops.set` | Values are checked against the block's allowed list; a parameter's range limits are not exposed, so an out-of-range value is caught only by the readback |
| `schedule.fromBlocks` | Reads references in model space (or every layout with `space: "all"`); nested references are counted, not read |
| `linetype.ensure` | Shape elements (`[SHAPE,file.shx]`) are refused |
| `viewports.layout` | Needs `area` when the layout has a title block; paper sizes in mm (inches for imperial jobs) |
| `layout.create` | Takes only plot settings from a template layout; `makeCurrent` switches after commit (`extra.currentLayout`) |
| `plot.pdf` | Only with other `plot.pdf` ops; the rehearsal really plots, to a temporary folder; existing files need `overwrite` |
| `xref.attach` | Bind is not available |
| `xref.repath` | Reloads only on commit and the reload is not undone; keep it last or alone |
| `fix.bylayer` | Entities on locked layers, viewports, anonymous and xref blocks are skipped and counted; on layer 0 inside a block, entities become ByBlock |
| `fix.purge` | `all` excludes registered applications (name them to purge them) and never purges visual styles |
| `fix.flattenZ` | Flattens only entities lying in the XY plane; 3D polylines, splines, 3D faces, regions, solids, surfaces, meshes and entities on locked layers are skipped and reported |
| `fix.units` | Alone or last; scales content only with `scaleContents` |
| `script.lisp` | Alone, no verify checks, `--allow-script`, rehearsal checks code but does not run it; no prompts (`command`, `vl-cmdf`, `get*`, `entsel`, `grread` refused); changes cannot be rolled back if the job fails after it |
| `draw.entities` | Lines, polylines (with bulges), arcs, circles and points in 2D only; no splines, ellipses or 3D |
| `hatch.add` | Loops of one hatch at one elevation; associative needs entity boundaries or `drawBoundary`; associative over a locked-layer boundary refused; readback fails if the area is off by more than 1% |
| `entity.transform` | Top-level entities in model space or a layout only (not attributes, vertices or block contents); wall junctions and openings are not re-cleaned after a move; mirrored text kept readable |
| `entity.delete` | Handles or a filter with `expectCount`; locked layers fail |
| `grid.module` | More than 250,000 tiles is refused; draws grid lines clipped to the boundary, or only reports with `draw: false` |
| `layout.clearances` | Changes nothing unless `drawZones`; xrefs are skipped with a warning; `tolerance` 0 or more; blocks by geometry or by bounding box |
| `block.extract`, `area.report` | Plain measured areas, not BOMA or IPMS; plain `.csv` or `.json` names (Windows device names refused); files written before a later op fails stay on disk |
| `revcloud.add` | A closed polyline of arcs, not a REVCLOUD object; the tag block is scaled as if drawn at plotted size |
| `text.replace` | Text with fields is skipped and counted; a match cannot run across a formatting code; table cells that are merged, linked or hold fields are skipped |
| `layerstate.ensure` | Saves current layer settings with the listed changes; an existing state needs `update: true` and is backed up to a `.las` file first; a rehearsal uses a temporary state |
| `material.ensure`, `material.assign` | Render materials only: they show in shaded and rendered views, not on a plotted 2D plan; texture paths in a user profile refused unless `allowLocalPath`; existing materials keep their mode |

## 4. Revit op limits

| Op | Limit |
|---|---|
| any op | Names must match the model exactly; no script escape hatch; a type or family made by the job cannot be named until a later job (`revit.md` section 2) |
| `walltype.ensure` | Basic walls only, with a core layer; missing materials are created plain (dress them with `material.ensure`) |
| `door.place`, `window.place` | Hosted families in walls only; the op fails if the host or position changes after placement |
| `room.place` | A not-enclosed room fails the op unless `allowNotEnclosed` (last phase only); redundant rooms fail; with `allEnclosed`, regions under 0.5 m² are skipped and reported |
| `roof.footprint` | Footprint roofs only; one slope per edge |
| `family.load` | Loads only (place with `family.place`). A differing file is refused unless `reload` or `overwriteParameters` |
| `param.shared.ensure` | The shared parameter file is written only after commit; parameter group labels follow the Revit UI language |
| `view.elevation.create` | `direction` is the way the viewer looks ("north" shows the south face of what is in front) |
| `viewports.layout` | Views that do not fit fail the op unless `failIfNotFit: false`; lining up views across sheets uses `viewport.place` with the same centre |
| `revision.ensure` | Revit assigns revision numbers; they can be read, not set |
| `tag.all` | Never tags an element twice in a view |
| `export.pdf` | Page count may come back null; mixed sets with non-sheet views fall back to Letter fit-to-page |
| `export.ifc` | `setup` refused; `ifc4x3` maps per release (see `revit.md` section 5) |
| exports | In their own job; files are staged and a rehearsal deletes them; existing files need `overwrite` |
| `cleanup.unusedViews` | Views with dependents are kept by default; use `dryRun` first |
| `element.delete` | Ids from a snapshot, `expectCount` must match; dependents refused unless `allowDependents` |
| `workset.assign` | Workshared models only; user worksets only |
| `family.place` | Types must be loaded; one host or one work plane per op; points must lie on the host face |
| `group.place` | Model groups only; elements already in a group refused; rotation of placed copies not read back |
| `element.transform` | Plan moves, copies, rotations and mirrors; pinned elements refused; walls joined to a moved wall are not listed; copies keep their marks |
| `phase.set` | Phases and phase filters by name only; it does not create phases |
| `compoundtype.ensure` | Floors, ceilings and roofs; `update: true` changes every instance (warned with the count) |
| `wallsweep.add` | The sweep or reveal type must exist; profiles are not created |
| `fillpattern.ensure` | One name per op; update of an existing pattern may need a rename fallback |
| `material.ensure` | A shared appearance is not edited unless `shareAsset: true`; with `from`, an existing own copy is not re-copied from a new source; a colour hidden by an image is warned about |
| `material.assign` | Type layers and categories need `maxInstances` or `confirmTypeWide`; in a workshared model a category material is checked through the owner of its Object Styles entry, which is still to be confirmed in the app |
| `link.revit` | Acquire only (no publish); acquire refused when the link is placed more than once or the geolocation would be removed or replaced |
| `link.cad` | DWG and DXF; one view unless `thisViewOnly: false` |
| `cleanup.purge` | Dry run first, then exact ids and `expectCount`; refused when an unused element owns placed or annotation elements; skipped whole in ownership skip mode if any id is left out |
| `export.nwc` | Needs the Navisworks exporter; refused next to any op that changes the model; view scope needs a 3D view |
| `data.export` | Changes nothing; refuses more than `maxRows`; values starting with `=`, `+`, `-` or `@` are written as they are (may act as spreadsheet formulas) |
| `schedule.export` | Revit's own schedule export; with `textQualifier: "none"` a heading containing the delimiter fails the check falsely |
| `schedule.key.create` | Renaming the key parameter of an existing key schedule renames it on every element; key assignment to elements is not done by this op |
| `area.plan.create`, `area.place` | Existing area schemes only; new boundary lines can reshape existing areas without listing them; plain measured areas |
| `view.drafting.create` | Legends only by copying an existing legend |
| `detail.draw` | Names of line styles, filled region types and detail components must exist |
| `view.crop.set` | Polygon crops in plans only |
| `colorfill.apply` | Rooms, areas and spaces; changing the scheme's parameter clears its colours |
| `tag.place` | One tag per element and category in a view unless `allowSecondTag`; room tags need the room on the view's level and phase |
| `revision.cloud` | Not in 3D views or schedules |
| `layout.clearances` | Changes nothing; plan footprints in one view; reports intrusions, not compliance |
| `dim.string` | Plans only; walls and grids the line crosses, doors and windows on those walls |
| `text.replace` | Elements inside groups are skipped and counted; elements left out in ownership skip mode are skipped |
| `family.edit` | Alone in its job; no in-place, system or non-editable families; types never renamed or deleted; shared nested families keep the project's copy |
| `ownership: "skip"` | Leaves out only what Revit reports as owned or out of date; side effects Revit makes on its own may still be refused at commit |

## 5. Audit limits

- Checks marked manual or heuristic return `review` (someone must look) and never pass on their own. A check that could not read everything it needs returns `incomplete`, listed first with `INCOMPLETE CHECK`; never report it as a pass.
- Revit warning and room-state checks match English warning text; on other UI languages they may miss.
- Verify checks look at the whole file, not only the job's changes, except the overlap and crop checks (section 6).
- Accessibility findings are measurements against stated figures, never certification. `layout.clearances` measures turning circles, door approaches and routes; the figures and the verdict are the user's.

## 6. Blind spots of the visual and layout checks

- AutoCAD rehearsals cannot compare pictures (`incomplete`); only the commit does.
- Change the job itself causes outside its elements is flagged: a changed style or layer that repaints everything, an associative dimension that follows a wall, a room tag that follows a room. Set `expectChange` or raise `maxUnexpectedChange`.
- Changes too thin or faint for the threshold (light grey, hairlines) are not seen.
- AutoCAD model content seen through layout viewports is placed per viewport rectangle; clipped viewports are judged by their rectangle. Only untwisted plan views are supported.
- Revit non-rectangular crops make the picture frame unreliable (`review`, not `fail`).
- Overlap is box against box: rotated text has a larger box than its letters, so slanted labels can report overlap that is not there. Text against linework is not checked.
- Revit tags with leaders are not checked for overlap; Revit dimension text boxes are estimates.
- Job placement checks cover plans only in Revit and model space only in AutoCAD; sections, elevations and 3D views are not checked. Revit annotation is not checked against crops.
- The three pictures have fixed names: two jobs sharing an output folder overwrite each other's.
