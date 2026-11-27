# Who the skill serves, and what each group can do now

"Now" means the ops exist today (recipe named; AutoCAD recipes in `autocad.md`, Revit in `revit.md`). "Wave 2" means a planned op that does not exist yet: say "not available yet" and offer the nearest thing that works now. "Out" means the skill will not do it. Practice guides are in `practice/`.

Every op still awaits its first test in the apps; say so when it matters to the user.

Out for every group: rendering and visuals, structural, MEP and civil engineering, photometrics, live links to other software, certifying code or accessibility compliance, Mac and LT.

## Architects

- **Now:** set-up from a standard (AutoCAD 3.1, Revit 3.1), walls, doors, windows and rooms (AutoCAD 3.2, Revit 3.2 and 3.3), grids and dimensions (AutoCAD 3.3, Revit 3.5), floors, ceilings and roofs (Revit 3.3, 3.13), views, sections and sheets (AutoCAD 3.8, Revit 3.4, 3.12), schedules (AutoCAD 3.6, Revit 3.6), revisions and issue (Revit 3.9, AutoCAD 3.8), audit and clean-up (AutoCAD 3.9, Revit 3.10).
- **Wave 2:** free 2D geometry, hatches, moving or deleting entities in AutoCAD, revision clouds, callouts, drafting views, detail lines, crop regions, single tags, Revit dimension strings through walls, area plans, links, purge, sheet sets, batch, drawing compare, clash detection, stairs.
- **Guides:** `walls.md`, `openings.md`, `rooms-areas.md`, `floors-ceilings-roofs.md`, `dimensions-annotation.md`, `sheets-sets.md`, `views-templates-graphics.md`, `stairs-ramps.md`.

## Interior designers

- **Now:** fit-out from a base build with status layers and demolition (AutoCAD 3.11); partitions, doors and rooms (AutoCAD 3.2, Revit 3.2); furniture layout in AutoCAD (3.5); room finish schedules (AutoCAD 3.7, Revit 3.3 and 3.6); FF&E schedules with counts (AutoCAD 3.6, Revit 3.6); FF&E data fields (Revit 3.7); material notes with leaders (AutoCAD 3.4); sheets and issue.
- **Wave 2:** placing furniture in Revit, existing and demolition phases in Revit, linking a base build in Revit, finish plans with floor patterns and skirtings, finish legends with swatches, interior elevations with finishes, RCP ceiling grids and fittings, joinery drawings, clearance checks against door swings and turning spaces, key schedules, materials and compound floor and ceiling types.
- **Out:** mood boards and visuals; aligning ceiling or floor patterns in Revit.
- **Guides:** `interiors.md`, `materials-textures.md`, `joinery-millwork.md`, `rcp-lighting.md`, `schedules-tags.md`.

## Space planners and workplace designers

- **Now:** test fits and desk runs in AutoCAD (rooms 3.2, blocks 3.5, positions computed by the agent); room data on tags (AutoCAD 3.7) or parameters (Revit 3.7); department area schedules in Revit (3.6 with Rooms); furniture standards as blocks (`block.define`) or families (Revit 3.8); presentation plans with frozen-layer viewports or view filters (AutoCAD 3.8, Revit 3.11).
- **Wave 2:** placing desks in Revit, occupancy and density counts, blocking and stacking colour plans, aisle width checks, layout options via layer states, move management and booking-system exports.
- **Out:** creating Revit design options.
- **Guides:** `space-planning.md`, `rooms-areas.md`.

## Kitchen and bathroom designers

- **Now:** measured room plans (AutoCAD 3.2 and 3.3, Revit 3.2); cabinet and sanitaryware layouts with dynamic widths in AutoCAD (3.5); cabinet, appliance and sanitaryware schedules (AutoCAD 3.6, Revit 3.6); services setting-out as attributed blocks with dimension strings; dimensioned installation sheets in AutoCAD (3.8).
- **Wave 2:** cabinets and fixtures in Revit, worktop outlines, wall elevations with tiles, tiling setting-out, clearance checks.
- **Out:** photoreal visuals; tile pattern alignment in Revit.
- **Guides:** `kitchens-bathrooms.md`, `joinery-millwork.md`.

## Retail, hospitality and exhibition designers (2D)

- **Now:** fixture plans, seating layouts, signage locations and typical rooms as blocks in AutoCAD (3.5, `block.define`), with fixture, seat, cover and sign counts (3.6); schedules in Revit (3.6).
- **Wave 2:** fixtures and seating in Revit, groups, counter and wheelchair-space checks, feature ceilings, stand elevations, shopfront details.
- **Out:** exhibition 3D models and visuals.
- **Guides:** `retail-hospitality.md`.

## Facility and property managers

- **Now:** room data register to CSV from attributes (AutoCAD 3.7), space numbering (`block.attrs.set`, `param.set`), legacy CAD conversion to the FM standard (AutoCAD 3.9), simplified plans for notices (AutoCAD 3.8 with frozen layers, Revit `export.image`), unplaced room clean-up (Revit 3.10).
- **Wave 2:** area measurement with area plans or area reports, asset registers, bulk updates from a spreadsheet, as-built moves and deletes, condition markups with clouds, Revit data and schedule export, batch audit or plot, comparing two versions.
- **Out:** live sync with FM software; handover data from 2D drawings.
- **Guides:** `area-facility-data.md`, `rooms-areas.md`.

## CAD drafters and technicians

- **Now:** set-up from a standard, plans from markups, dimensions and notes, blocks and attributes, xref attach and repair, layouts, title blocks and plotting, standards clean-up (AutoCAD 3.1 to 3.10); trim, extend, fillet and offset through the escape hatch (AutoCAD section 4); Revit set-up and plans (Revit 3.1, 3.2).
- **Wave 2:** lines, arcs, polylines and hatches as typed ops, move, copy, rotate, mirror, erase, find and replace text, revision clouds, sheet sets, Revit links and purge.
- **Out:** trim and extend in Revit.
- **Guides:** `cad-standards.md`, `drawing-conventions.md`, `mistakes-autocad.md`.

## BIM coordinators and managers

- **Now:** levels, grids and view naming (Revit 3.1, 3.11), view templates and filters (Revit 3.11), shared parameters and values (Revit 3.7), worksets (Revit 3.14), health audits and warning triage (`audit`), exports to the naming rule (Revit 3.9, AutoCAD 3.8), xref attach (AutoCAD 3.11), room and view clean-up (Revit 3.10), AutoCAD purge (3.9).
- **Wave 2:** Revit links with shared coordinates, Revit purge, element data extraction, NWC export, batch jobs, clash detection, IFC property checks.
- **Out:** BCF issue exchange; clash detection on 2D drawings.
- **Guides:** `bim-setup.md`, `worksharing-collaboration.md`, `export-exchange.md`, `qa-review.md`, `families-parameters.md`, `mistakes-revit.md`.

## Accessibility reviewers (review only)

- **Now:** accessible counts against scoping tables from schedules (AutoCAD 3.6 or 3.12, Revit 3.6); findings reports that cite the snapshot and design data; findings marked with leaders on a markup layer and plotted (AutoCAD 3.12, then a `plot.pdf` job).
- **Wave 2:** door clear widths, turning circles, route widths and mounting heights measured from the drawing or model, revision clouds.
- **Out:** certifying compliance (always the user's), stair and ramp geometry from plain 2D lines.
- **Guides:** `accessibility-review.md`, `stairs-ramps.md`.

## Landscape architects (AutoCAD 2D only)

- **Now:** site base from a survey xref with layers (AutoCAD 3.11), planting plans as symbols scaled to spread with a species attribute (3.5), planting, site furniture and external lighting schedules (3.6), setting-out dimensions and coordinate notes (3.3, 3.4).
- **Wave 2:** hardscape outlines, paving and bed hatches with areas, planting densities, tree protection areas.
- **Out:** grading, contours, earthworks, irrigation, Revit site modelling.
- **Guides:** `landscape-site.md`.

## Lighting layouts (placement only)

- **Now:** fitting, emergency light and exit sign positions as blocks where the user gives them (AutoCAD 3.5), fitting legends and schedules (AutoCAD 3.6 with `table.create`, Revit 3.6 with Lighting Fixtures), fitting tags (`block.attrs.set` and `mleader.add`, Revit `tag.all`), lighting zones by view filter (Revit 3.11).
- **Wave 2:** grid-aligned layouts on the RCP, switches and switch legs, fittings in Revit, clash checks against diffusers and detectors, zone hatches in AutoCAD.
- **Out:** photometric calculations, circuiting, panel schedules, cable sizing.
- **Guides:** `rcp-lighting.md`.
