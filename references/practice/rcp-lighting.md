# Reflected ceiling plans and lighting layouts

Read this before drawing or checking a reflected ceiling plan (RCP), setting out a ceiling grid, or placing light fittings, switches and ceiling devices. This covers where things go on the drawing. Lighting calculations (lux levels, uniformity, glare), circuiting and electrical design are out of scope; they belong to the lighting designer or electrical engineer. Ceiling heights and grid module figures are in `design-data.md` (ceilings file) with their source; spacing rules for fittings have no public source here and are conventions.

Contents: 1 What an RCP shows · 2 Setting up the view or drawing · 3 Setting out a ceiling grid · 4 Ceilings in Revit · 5 Placing light fittings · 6 Switches and controls · 7 Coordination with engineers · 8 Schedules and legends · 9 Checks

## 1. What an RCP shows

The RCP is drawn as if the floor were a mirror: looking up, with the same orientation as the floor plan. A US federal design standard asked that reflected ceiling plans "show ceiling grid and location of all elements to be placed in the ceiling" [N23]. Typical content:

- Walls cut at the RCP cut plane; doors usually omitted, openings shown as heads.
- Ceiling types by pattern or tag, and ceiling height per area ("2700" or "9'-0"" above FFL).
- Bulkheads, soffits, coffers, changes in height with their heights; access panels.
- Light fittings with type tags, emergency fittings and exit signs.
- Diffusers and grilles, sprinkler heads, smoke and heat detectors, speakers, sensors, as coordinated from the engineers.
- A legend of ceiling types and fitting symbols.

Ceiling height minimums by jurisdiction (examples in the data): Massachusetts building code 7 ft 6 in (2286 mm) for occupiable spaces and 7 ft (2134 mm) for service rooms and furred parts (1208.2) [N21]; NSW apartments 2.7 m habitable and 2.4 m non-habitable (4C-1) [N20]; England's space standard in the dwellings file. Check the project's code.

## 2. Setting up the view or drawing

- Revit: a ceiling plan view per level. Its view direction is up from the cut plane; the Bottom of the view range is not used; view depth extends upward [N11]. Set the cut plane just below the lowest ceiling and above door heads so doors do not show as openings (typical practice; confirm with the template).
- AutoCAD: an RCP is a separate drawing or layout with the floor-plan xref filtered to walls and heads, plus ceiling layers (`A-CLNG`, `I-CLNG` and minor groups for grid, patterns and tags in NCS-style standards [S05]). Lighting blocks on the electrical or lighting layers of the standard.

## 3. Setting out a ceiling grid

Module sizes: the US unified facility guide specification lists 600 x 600 and 600 x 1200 mm units for exposed grids and 300 x 300, 600 x 600 and 600 x 1200 mm for concealed grids [N22] (`ceilings.us.ufgs.module.*`); imperial markets use 2' x 2' and 2' x 4'. The same document asks that units "less than one-half width" are minimized and that access panels be between 300 x 300 mm and 300 x 600 mm (3.1.3, 2.5) [N22].

Rule (from the border-unit requirement; the centring method is typical practice):
1. Pick the module and the direction of the long side.
2. In each direction, compute the border for two cases: a grid line on the room centre, and a tile centred on the room centre. Take the case whose border tiles are equal on both sides and at least half a module.
3. Where neither case gives half-module borders (a room width that is a whole number of modules plus a little), shift so one side gets full tiles and report the narrow border.
4. Where the room is irregular, centre on the main feature (entrance axis, window bay, light rows) and push unequal borders to the least visible side.
5. Align grids across rooms that read together (open plans, corridors joining lobbies).
6. Report the grid as: module, direction, origin point, border widths on each side.

## 4. Ceilings in Revit

- Ceilings are made by sketch or by picking the room boundary, with a type that carries the finish material and its surface pattern. Height is the offset from the level.
- The grid drawn on a ceiling is the material's model surface pattern. Its position and rotation are adjusted by hand (select a grid line and move or rotate it). The add-in found no API to align surface patterns, so it reports the grid it wants and asks the user to align it.
- Fittings hosted on the ceiling face follow the ceiling when it moves; fittings hosted on a level or work plane do not.
- Bulkheads: a ceiling at a lower height, or a wall or soffit element with a ceiling-plan representation; tag the height.

## 5. Placing light fittings (conventions; confirm with the designer)

- Use the fitting type the lighting designer or engineer specifies; the agent places, it does not choose fittings or spacing.
- In grid ceilings, put recessed fittings in whole tiles, centred, never across grid lines; in plasterboard ceilings, set out rows from the room centre or the feature with equal end spaces.
- Align rows with desks, counters or aisles they serve; keep symmetric patterns centred on the room or feature.
- Keep a consistent distance from walls for wall washers and downlights over walls; state it.
- Emergency lighting and exit signs follow the fire strategy and the engineer's layout; place them as given.
- Tag every fitting with its type code (`L1`, `L2` or the project's) keyed to the schedule.

## 6. Switches and controls

- Show switch positions on the RCP with switch legs (arcs to the fittings they control) or on a separate lighting-control plan, as the office does.
- Heights go on interior elevations or as notes; accessible controls within the reach range of the jurisdiction (US forward reach 15-48 in [N01]).
- Sensors (presence, daylight) appear on the RCP with their type; their coverage is the engineer's.

## 7. Coordination with engineers

- Engineers own diffusers, sprinklers, detectors and their spacing. Overlay their layouts (links or xrefs) and report conflicts: element in the same tile, closer than the agreed distance, or off-grid.
- Do not move engineers' elements; report them with location and a proposed position.
- Show ceiling void depth and access needs where the brief asks; above-ceiling coordination is clash work (see `qa-review.md`).

## 8. Schedules and legends

- Lighting fitting schedule: type code, description, manufacturer and model (as specified), lamp or source, wattage and colour temperature (as specified), mounting, quantity, remarks. Never fill technical data from assumption.
- Ceiling type legend: code, description, module, material and finish, height or "see plan".
- Counts come from the model or block extraction, not hand counts.

## 9. Checks

| Check | Signal |
|---|---|
| Grid set out | Border tiles under half a module; grids not aligned across connected rooms |
| Heights tagged | Ceilings or bulkheads without a height above FFL |
| Fittings on grid | Recessed fittings across grid lines or in cut tiles |
| Tags and schedule | Fittings without type tags; types in the plan missing from the schedule |
| Coordination | Fittings sharing tiles with diffusers, detectors or sprinklers |
| Hosting (Revit) | Ceiling fittings hosted on levels so they float when the ceiling moves |
| Scope | Lighting levels, circuiting or panel data claimed as checked |
