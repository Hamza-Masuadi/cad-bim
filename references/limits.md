# Limits: what is out of reach, and where each op stops

Say these plainly when they apply. "Not available yet" (wave 2) and "out of reach" are different answers: the first is planned, the second is not. Wave-2 lists per user group are in `users.md`.

Contents: 1 Out of reach · 2 Not yet proven · 3 AutoCAD op limits · 4 Revit op limits · 5 Audit limits

## 1. Out of reach

| Thing | Why |
|---|---|
| Revit LT, AutoCAD LT, AutoCAD for Mac | No API (LT) or the engine cannot load (Mac) |
| Civil, deep MEP and structural engineering | Outside building design scope |
| Revit's own Interference Check | No API runs it |
| Editing DWG files without AutoCAD running | No licence-clean way to write DWG |
| Turning IFC back into editable Revit elements | Linked IFC is not editable and import loses information |
| Running visual-programming graphs from outside Revit | No outside interface |
| Rendering, photoreal images, VR, mood boards | Different tools |
| Certifying code or accessibility compliance | The skill applies defaults and flags issues; legal compliance stays with the user and their consultants |
| Anything that needs a click in a dialog | The engine cannot answer dialogs; a dialog during a job fails it |
| Cloud-hosted models beyond what the open desktop model allows | Needs cloud sign-in and paid automation |
| Grading, contours, earthworks, irrigation, Revit site modelling | Need surface modelling and civil tools; landscape stays 2D in AutoCAD |
| Lighting calculations, circuiting, panel schedules | Engineering software; the skill places fittings only |
| MEP sizing and systems for interiors | Engineers own it |
| AutoCAD 3D modelling, including exhibition stands | The AutoCAD engine is 2D |
| Revit in-place families, design options, area schemes | No API creates them |
| Revit legend components, or a first legend view | No API places them |
| Aligning floor and ceiling surface patterns in Revit | No API member found; the skill can say what grid it wants |
| FF&E procurement, pricing, specification writing, catalogues | Not drawing or modelling work |
| Live links to facility management software | Needs each product's own interface |
| A validated COBie workbook, BCF issue exchange | Need other tools or platforms |
| BOMA or IPMS measurement | Not in the data |
| Saving, synchronising, relinquishing, closing | The user does these, always |

## 2. Not yet proven

Nothing has been run in AutoCAD or Revit. All 72 ops compile for 2024 to 2027 and pass offline tests; their behaviour in the apps is still to be confirmed. Report what the engine returned, never "this works". Items most likely to need a fix after the first test round:

- **AutoCAD:** evaluating `script.lisp` code inside the engine (first on the test plan); whether a rehearsal leaves anything behind (reported as `rehearsalLeaked`); rolling back `xref.attach`; plotting while the job is open; building a layout by hand in `layout.create`; the text height of leaders with scaled styles.
- **Revit:** grid references in `dim.grids`; measuring viewports in a temporary transaction in `viewports.layout` and the title block drawable-area guess (falls back to the sheet edge less 15 mm); doors and windows staying exactly where placed (a drift over 0.5 mm fails the op); the not-enclosed versus redundant room rule; PDF and DWG export inside the job's transaction group (`export_refused` if Revit refuses); IFC export inside a rolled-back sub-transaction; roof edge offsets; level elevations on a model with a raised base point; the .NET 8 builds on .NET 10 updates.

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

## 4. Revit op limits

| Op | Limit |
|---|---|
| any op | Names must match the model exactly; no script escape hatch |
| `walltype.ensure` | Basic walls only, with a core layer; missing materials are created plain (no appearance or patterns; material ops are wave 2, see `practice/materials-textures.md`) |
| `door.place`, `window.place` | Hosted families in walls only; the op fails if the host or position changes after placement |
| `room.place` | A not-enclosed room fails the op unless `allowNotEnclosed` (last phase only); redundant rooms fail; with `allEnclosed`, regions under 0.5 m² are skipped and reported |
| `roof.footprint` | Footprint roofs only; one slope per edge |
| `family.load` | Loads only; placing instances is wave 2. A differing file is refused unless `reload` or `overwriteParameters` |
| `param.shared.ensure` | The shared parameter file is written only after commit; parameter group labels follow the Revit UI language |
| `view.elevation.create` | `direction` is the way the viewer looks ("north" shows the south face of what is in front) |
| `viewports.layout` | Views that do not fit fail the op unless `failIfNotFit: false`; lining up views across sheets uses `viewport.place` with the same centre |
| `revision.ensure` | Revit assigns revision numbers; they can be read, not set |
| `tag.all` | Never tags an element twice in a view |
| `export.pdf` | Page count may come back null; mixed sets with non-sheet views fall back to Letter fit-to-page |
| `export.ifc` | `setup` refused; `ifc4x3` maps per release (see `revit.md` section 5) |
| exports | In their own job; rehearsal writes to a temporary folder; existing files need `overwrite` |
| `cleanup.unusedViews` | Views with dependents are kept by default; use `dryRun` first |
| `element.delete` | Ids from a snapshot, `expectCount` must match; dependents refused unless `allowDependents` |
| `workset.assign` | Workshared models only; user worksets only |

## 5. Audit limits

- Checks marked manual or heuristic return `review` (someone must look) and never pass on their own. A check that could not read everything it needs returns `incomplete`, listed first with `INCOMPLETE CHECK`; never report it as a pass.
- Revit warning and room-state checks match English warning text; on other UI languages they may miss.
- Verify checks look at the whole file, not only the job's changes.
- Accessibility findings are measurements against stated figures, never certification. Measuring clear widths, turning circles and routes from the drawing is wave 2.
