# Who the skill serves, and what each group can do now

"Now" means the ops exist (recipe named; AutoCAD recipes in `autocad.md`, Revit in `revit.md`). "Out" means the skill will not do it: say so and offer the nearest thing that works (`limits.md`). Practice guides are in `practice/`.

Every op still awaits its first test in the apps. The newest ones (AutoCAD 3.13 to 3.20, Revit 3.15 to 3.33) are the least proven: say "written, untested in the app" when the user relies on them.

Out for every group: rendering and visuals, structural, MEP and civil engineering, photometrics, live links to other software, certifying code or accessibility compliance, batch work over closed files, comparing two files, Mac and LT.

## Architects

- **Now:** set-up from a standard (AutoCAD 3.1, Revit 3.1), walls, doors, windows and rooms (AutoCAD 3.2, Revit 3.2 and 3.3), grids and dimensions (AutoCAD 3.3, Revit 3.5), dimension strings through walls and openings (Revit 3.25), floors, ceilings and roofs (Revit 3.3, 3.13, 3.19), views, sections and sheets (AutoCAD 3.8, Revit 3.4, 3.12), crops and enlarged callouts (Revit 3.25), drafting details (Revit 3.24), free 2D drawing and hatches (AutoCAD 3.13), as-built moves and removals (AutoCAD 3.14, Revit 3.17), existing, demolition and new phases (Revit 3.18), tags and revision clouds (AutoCAD 3.18, Revit 3.26), family edits (Revit 3.32), links (Revit 3.29), schedules (AutoCAD 3.6, Revit 3.6), revisions and issue (Revit 3.9, AutoCAD 3.8), audit and clean-up (AutoCAD 3.9, Revit 3.10, 3.30).
- **Out:** stairs and ramps as typed ops, design options, in-place families, sheet sets, clash detection (export to Navisworks instead, Revit 3.31).
- **Guides:** `walls.md`, `openings.md`, `rooms-areas.md`, `floors-ceilings-roofs.md`, `dimensions-annotation.md`, `sheets-sets.md`, `views-templates-graphics.md`, `stairs-ramps.md`.

## Interior designers

- **Now:** fit-out from a base build with status layers and demolition (AutoCAD 3.11) or phases (Revit 3.18); partitions, doors and rooms (AutoCAD 3.2, Revit 3.2); furniture layout (AutoCAD 3.5, Revit 3.15) and repeated groups (Revit 3.16); linking a base build (Revit 3.29); finish plans with floor patterns (AutoCAD 3.13, Revit 3.20), floor and ceiling build-ups and skirtings (Revit 3.19); finish materials with codes (AutoCAD 3.20, Revit 3.20); finish legends with hatch swatches (AutoCAD 3.7 with 3.13); interior elevations with tags (Revit 3.4, 3.25, 3.26); RCP ceiling grids (AutoCAD 3.15) and fittings (AutoCAD 3.5, Revit 3.15); joinery drawings in 2D (AutoCAD 3.13, Revit 3.24); clearance checks against door swings and turning spaces (AutoCAD 3.16, Revit 3.27); room finish schedules and key schedules (AutoCAD 3.7, Revit 3.3, 3.6, 3.21); FF&E schedules and data (AutoCAD 3.6, Revit 3.6, 3.7); material notes with leaders (AutoCAD 3.4); sheets and issue.
- **Out:** mood boards and visuals; aligning ceiling or floor patterns in Revit.
- **Guides:** `interiors.md`, `materials-textures.md`, `joinery-millwork.md`, `rcp-lighting.md`, `schedules-tags.md`.

## Space planners and workplace designers

- **Now:** test fits and desk runs (AutoCAD rooms 3.2 and blocks 3.5, Revit 3.15 and groups 3.16, positions computed by the agent); room data on tags (AutoCAD 3.7) or parameters (Revit 3.7); department areas and area plans (Revit 3.6, 3.22; AutoCAD 3.17); occupancy and density counts worked out from block and area exports (AutoCAD 3.17, Revit 3.28); blocking and stacking colour plans (Revit 3.23; AutoCAD hatches 3.13); aisle and route width checks (AutoCAD 3.16, Revit 3.27); layout options with layer states (AutoCAD 3.19); presentation plans with frozen-layer viewports or view filters (AutoCAD 3.8, Revit 3.11); moves (AutoCAD 3.14, Revit 3.17); CSV exports for a booking or move system (AutoCAD 3.17, Revit 3.28).
- **Out:** creating Revit design options; live links to booking systems.
- **Guides:** `space-planning.md`, `rooms-areas.md`.

## Kitchen and bathroom designers

- **Now:** measured room plans (AutoCAD 3.2 and 3.3, Revit 3.2); cabinet and sanitaryware layouts with dynamic widths (AutoCAD 3.5) or families (Revit 3.15); worktop outlines (AutoCAD 3.13); tiling set-out (AutoCAD 3.15) and tile materials (Revit 3.20); wall elevations with tags (Revit 3.4, 3.25); clearance checks (AutoCAD 3.16, Revit 3.27); cabinet, appliance and sanitaryware schedules (AutoCAD 3.6, Revit 3.6); services setting-out as attributed blocks with dimension strings; family size edits (Revit 3.32); dimensioned installation sheets (AutoCAD 3.8).
- **Out:** photoreal visuals; tile pattern alignment in Revit.
- **Guides:** `kitchens-bathrooms.md`, `joinery-millwork.md`.

## Retail, hospitality and exhibition designers (2D)

- **Now:** fixture plans, seating layouts, signage locations and typical rooms as blocks (AutoCAD 3.5, `block.define`) or families and groups (Revit 3.15, 3.16), with fixture, seat, cover and sign counts (AutoCAD 3.6, 3.17; Revit 3.6); counter and wheelchair-space checks (AutoCAD 3.16, Revit 3.27); feature ceiling set-outs (AutoCAD 3.15); stand plans and shopfront details in 2D (AutoCAD 3.13, Revit 3.24); finish materials (AutoCAD 3.20, Revit 3.20).
- **Out:** exhibition 3D models and visuals.
- **Guides:** `retail-hospitality.md`.

## Facility and property managers

- **Now:** room data register to CSV from attributes (AutoCAD 3.7, 3.17) or the model (Revit 3.28); asset registers (AutoCAD 3.17, Revit 3.28); area measurement (AutoCAD 3.17, Revit 3.22); bulk updates from a spreadsheet (`block.attrs.set`, `param.set`); as-built moves and removals (AutoCAD 3.14, Revit 3.17); condition markups with clouds (AutoCAD 3.18, Revit 3.26); space numbering; legacy CAD conversion to the FM standard (AutoCAD 3.9); simplified plans for notices (AutoCAD 3.8 with frozen layers, Revit `export.image`); unplaced room clean-up (Revit 3.10).
- **Out:** live sync with FM software; handover data from 2D drawings; BOMA or IPMS measurement; batch audit or plot over closed files; comparing two versions.
- **Guides:** `area-facility-data.md`, `rooms-areas.md`.

## CAD drafters and technicians

- **Now:** set-up from a standard, plans from markups, dimensions and notes, blocks and attributes, xref attach and repair, layouts, title blocks and plotting, standards clean-up (AutoCAD 3.1 to 3.10); lines, arcs, polylines and hatches (AutoCAD 3.13); move, copy, rotate, mirror, erase (AutoCAD 3.14); find and replace text and revision clouds (AutoCAD 3.18); layer states (AutoCAD 3.19); trim, extend, fillet and offset through the escape hatch (AutoCAD section 4); Revit set-up, plans, drafting details and links (Revit 3.1, 3.2, 3.24, 3.29) and purge (Revit 3.30).
- **Out:** sheet sets; trim and extend in Revit.
- **Guides:** `cad-standards.md`, `drawing-conventions.md`, `mistakes-autocad.md`.

## BIM coordinators and managers

- **Now:** levels, grids and view naming (Revit 3.1, 3.11), view templates and filters (Revit 3.11), shared parameters and values (Revit 3.7), worksets (Revit 3.14), work in workshared models with elements others have borrowed (Revit 3.33), links with shared coordinates (Revit 3.29), purge with a dry run (Revit 3.30), element data extraction (Revit 3.28), Navisworks export (Revit 3.31), family edits (Revit 3.32), sheet and view layout checks (Revit 3.26), health audits and warning triage (`audit`), exports to the naming rule (Revit 3.9, AutoCAD 3.8), xref attach (AutoCAD 3.11), room and view clean-up (Revit 3.10), AutoCAD purge (3.9).
- **Out:** clash detection itself (Navisworks does that), IFC property checks, BCF issue exchange, batch jobs, borrowing or synchronising for the user.
- **Guides:** `bim-setup.md`, `worksharing-collaboration.md`, `export-exchange.md`, `qa-review.md`, `families-parameters.md`, `mistakes-revit.md`.

## Accessibility reviewers (review only)

- **Now:** accessible counts against scoping tables from schedules (AutoCAD 3.6 or 3.12, Revit 3.6); door clear space, turning circles and route widths measured from the drawing or model (AutoCAD 3.16, Revit 3.27); findings reports that cite the snapshot and design data; findings marked with leaders and revision clouds on a markup layer and plotted (AutoCAD 3.12, 3.18; Revit 3.26).
- **Out:** certifying compliance (always the user's); mounting heights measured in 3D (only read from parameter data, Revit 3.28); stair and ramp geometry from plain 2D lines.
- **Guides:** `accessibility-review.md`, `stairs-ramps.md`.

## Landscape architects (AutoCAD 2D only)

- **Now:** site base from a survey xref with layers (AutoCAD 3.11), hardscape outlines, paving and bed hatches with areas (3.13, 3.17), planting plans as symbols scaled to spread with a species attribute (3.5), planting densities from bed areas and symbol counts (3.17), tree protection areas (3.13), planting, site furniture and external lighting schedules (3.6), setting-out dimensions and coordinate notes (3.3, 3.4).
- **Out:** grading, contours, earthworks, irrigation, Revit site modelling.
- **Guides:** `landscape-site.md`.

## Lighting layouts (placement only)

- **Now:** fitting, emergency light and exit sign positions as blocks where the user gives them (AutoCAD 3.5) or families on ceilings (Revit 3.15); grid-aligned layouts on the RCP (AutoCAD 3.15, then 3.5); switches and switch legs drawn as blocks and arcs (AutoCAD 3.5, 3.13); spacing checks against diffusers and detectors with clearance zones (AutoCAD 3.16, Revit 3.27); zone hatches (AutoCAD 3.13); fitting legends and schedules (AutoCAD 3.6 with `table.create`, Revit 3.6 with Lighting Fixtures); fitting tags (`block.attrs.set` and `mleader.add`, Revit `tag.all`, 3.26); lighting zones by view filter (Revit 3.11).
- **Out:** photometric calculations, circuiting, panel schedules, cable sizing.
- **Guides:** `rcp-lighting.md`.
