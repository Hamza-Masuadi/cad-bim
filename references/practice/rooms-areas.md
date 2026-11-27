# Rooms, areas and spaces

Read this before placing rooms or areas, numbering rooms, computing or reporting floor areas, or checking room data. Source tags point to `_SOURCES.md`.

Contents: 1 Rooms vs areas vs spaces · 2 Room bounding and separators · 3 Naming and numbering · 4 Area measurement standards · 5 Tags and finish data · 6 AutoCAD rooms · 7 Checks

## 1. Rooms vs areas vs spaces (Revit)

| Element | What it is | Bounded by | Used for |
|---|---|---|---|
| Room | Architectural room: name, number, area, perimeter, volume, finishes, occupancy | Room-bounding elements (walls, floors, ceilings, roofs, columns where set) and room separation lines | Room schedules, finish schedules, tags, door from/to rooms, room data sheets |
| Area | Element in an area plan, belonging to an area scheme (e.g. gross building, rentable) | Area boundary lines (drawn or picked from walls), not walls themselves | Gross and rentable area reports, planning and lease calculations |
| Space | MEP analytical space | Room-bounding elements, usually level to level | Heating and cooling loads, airflow, zones. LAWA: one space per space per discipline, spaces level-to-level, plenums as separate spaces [S75] |

Rules:
- Rooms for design and documentation, areas for measurement to a standard, spaces for engineers. Do not use rooms to report rentable or gross area if a measurement standard applies; area rules differ from wall-finish room boundaries.
- Every room needs a name and a number, including shafts, stairs, risers and cupboards if the project counts them; no duplicates (LAWA) [S75].
- Unplaced rooms (in schedules but not in the model) and "Not enclosed" rooms are errors to fix, not to ignore [S79].

## 2. Room bounding and separators

- Revit computes a room's area at a project-wide boundary location: at wall finish (default), wall centre, wall core layer, or wall core centre; it cannot be set per room (Revit help, search excerpt) [N14]. Choose it once with the user; it changes every area. Linked IFC walls always use the finish face [N14].
- Revit measures the room boundary at a computation height above the room's level, set per level; sloped walls need it adjusted [N14]. Check it when rooms under sloping walls give odd areas.
- Turn Room Bounding off for walls that must not split a room (low walls, furniture walls) and on for linked models' walls when the link should bound rooms (link instance setting).
- Room separation lines: only where there is no wall (open-plan zones, top of stairs, voids). Never draw them on top of walls: it creates the top warning "A wall and a room separation line overlap" [S23][S39].
- Upper limit and limit offset define room height for volume. Set them to the level above or the ceiling, not to the same level.
- Overlapping rooms in one enclosed region give "Multiple rooms are in the same enclosed region" (redundant room) [S79]. Delete or move the duplicate.

## 3. Naming and numbering

| Scheme | Example | Notes |
|---|---|---|
| Level digit(s) + sequence | 101, 102 … 201 | Common US practice; level 1 = 100 series |
| Level + separator + sequence | 1.01, 01-015, L02-015 | Larger projects; sorts well |
| Building + level + sequence | B-02-015 | Campuses, multi-building |
| Department + sequence | ED-012 | Healthcare and labs often follow owner standards |

Numbering rules (typical practice; the owner's standard governs):
- Number in a consistent path: from the main entrance, clockwise, or corridor by corridor with odd numbers on one side and even on the other.
- Leave gaps (e.g. step by 2 or 5) so rooms can be inserted without renumbering.
- Stacked rooms keep the same last digits on each floor where layouts repeat (101 under 201). This helps services and FM.
- Number shafts and cupboards with a suffix rule (e.g. 101A) if the owner counts them as spaces.
- Doors usually take the number of the room they open into (see `openings.md`), so finish room numbering before door numbering.
- Names: use the project's room name list (often a key schedule) so names are spelled identically; uppercase or title case per standard.

## 4. Area measurement standards

Measurement standards differ by country and purpose. Ask which one applies before you report an area as anything other than "room area at wall finish".

| Standard | Region / use | Key measures |
|---|---|---|
| RICS Code of measuring practice, 6th edition (2015) [N07] | UK and others, where IPMS is not required | GEA: measured externally at each floor level. GIA: measured to the internal face of the perimeter walls at each floor level. NIA: the usable area within a building measured to the internal face of the perimeter walls at each floor level, excluding toilets, plant, stairwells and lift wells, common circulation, internal structural walls and columns, among others |
| IPMS (International Property Measurement Standards), adopted in RICS Property Measurement 2nd edition (2018) for offices and residential [N16] | International | IPMS 1 (external, close to GEA), IPMS 2 (internal, component-based), IPMS 3 (exclusive occupation). Definitions not read first-hand; confirm with the published text |
| ANSI/BOMA office and other standards | US commercial leasing | Rentable vs usable area with load factors. Paywalled; not read |
| DIN 277 | Germany | Gross, net and functional areas. Not read |
| Owner and planning rules | Local planning applications, public clients | Often define their own "gross floor area" |

Rules:
- Gross area (external or internal) for planning, cost and building size; net or usable area for leasing and space programmes.
- Areas for planning and leases go in Revit area plans with an area scheme named after the standard, not in room areas.
- State the standard and the boundary rule next to every reported number. A number without its basis is not usable.

## 5. Tags and finish data

- Room tag shows name and number at minimum; often area (with the basis) and ceiling height in RCPs. Tags read parameters; never type room names as text [S34].
- Finish data: Revit rooms have built-in Base Finish, Floor Finish, Wall Finish and Ceiling Finish text parameters. For projects with many finish types, drive them with a key schedule (room style) so a single key sets all finishes consistently; or use project/shared parameters if the finish schedule needs more fields (wall finish north/east/south/west, ceiling height, remarks). See `interiors.md`.
- Occupancy, department, phase and fire compartment are common additional room parameters; use shared parameters if they must be tagged.

## 6. Rooms in AutoCAD

- Draw each room boundary as a **closed** polyline on the area layer (US style `A-AREA` or `A-AREA-IDEN` for tags) [S10]. Open polylines give no reliable area [S42].
- Trace the boundary on the measurement basis you were told (inside face of finish for room areas, external face for gross).
- Room tag as an attributed block (NAME, NUMBER, AREA) on the tag layer; AREA filled from the polyline by a field or by the job, never typed.
- One polyline per room, no self-intersections, no overlaps between adjacent rooms.

## 7. Checks

| Check | Signal |
|---|---|
| Every room placed and enclosed | Unplaced rooms in schedules; "Not enclosed" area; Revit room warnings [S79] |
| No redundant rooms | Two rooms in one enclosed region |
| Every room named and numbered, no duplicates | Empty name/number; duplicate numbers [S75][S23] |
| Separation lines not on walls | Overlap warning [S23] |
| Area basis stated | Area reports without the boundary rule or standard |
| Upper limit sensible | Upper limit equal to base level or far above |
| AutoCAD room polylines closed | Polylines on area layers not closed [S10] |
| Tags present | Rooms without a tag in the plans that should show them |
