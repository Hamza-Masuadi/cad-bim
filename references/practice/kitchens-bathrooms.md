# Kitchens and bathrooms

Read this before kitchen and bathroom layouts, cabinet plans and elevations, sanitaryware layouts, tiling setting-out and installation drawings. Every dimension below with a tag is in `design-data.md` (kitchens-bathrooms file) with its source and jurisdiction. Cabinet module widths, worktop heights for non-accessible kitchens and the "work triangle" are trade conventions with no public source here; trade-association planning guidelines exist but are paywalled and were not read. Compliance is the user's responsibility.

Contents: 1 Outputs · 2 Survey first · 3 Kitchen layouts · 4 Accessible and adaptable kitchens · 5 Bathroom and WC layouts · 6 Tiling setting-out · 7 Services setting-out · 8 Drawings and schedules · 9 In AutoCAD and Revit · 10 Checks

## 1. Outputs

| Drawing | Content |
|---|---|
| Measured plan | Walls, openings, services positions, ceiling height, out-of-square notes |
| Layout plan | Cabinets with codes and widths, appliances, worktop outline, sink and hob, clearances |
| Elevations, one per wall | Unit fronts, heights, worktop and splashback, wall units, tall units, appliances, sockets and switches with heights |
| Services setting-out | Water, waste, gas, power and extract positions, dimensioned from fixed references and FFL |
| Tiling layout | Tile grid, start point, cut positions, trims |
| Schedules | Cabinets, appliances, sanitaryware and taps, worktops, finishes |

## 2. Survey first

- Measure each wall at worktop height and near the ceiling; walls are rarely square or straight. Record the diagonals of the room.
- Record existing services: soil stack, waste, water, gas, boiler, consumer unit, extract routes, radiators, windows and sill heights.
- Draw the measured room as found. Show out-of-square and out-of-plumb as notes with values, and keep a filler allowance at ends of runs (typical practice).

## 3. Kitchen layouts (conventions unless tagged)

- Arrangements: single run, galley (two runs), L, U, island, peninsula.
- Keep a clear working space in front of counters. Published figures:
  - US ADA kitchens: pass-through kitchens (two entries) at least 40 in (1015 mm) between opposing counters or walls; U-shaped kitchens enclosed on three sides at least 60 in (1525 mm) (804.2) [N01] (`kitbath.us.ada.kitchen.*`).
  - England M4(3): a clear zone of 1500 mm in front of and between all kitchen fittings and appliances [N17] (`kitbath.uk.m43.kitchen_clear.min`; the M4(2) figure is in the dwellings file).
  - Massachusetts dwellings: 3 ft (914 mm) clear passageway between counter fronts and appliances or walls (1208.1 as adopted) [N21].
- Typical practice where no code applies: base units 600 mm (24") deep, worktop 600-650 mm deep, worktop height about 900 mm (36"), wall units 300-350 mm deep with 450-600 mm between worktop and wall unit. These vary by market and product; ask.
- Do not put a hob or sink hard against a tall unit or wall; leave worktop either side (typical practice; England M4(3) asks for 400 mm worktop beside tall appliances, `kitbath.uk.m43.worktop_beside_tall.min`).
- Doors and appliance doors must open without clashing with each other or with room doors.

## 4. Accessible and adaptable kitchens

| Item | US, 2010 ADA [N01] | England, AD M Vol 1 M4(3) [N17] |
|---|---|---|
| Clearance between counters | 40" (1015 mm) pass-through; 60" (1525 mm) U-shaped (804.2) | 1500 mm clear zone (3.32) |
| Accessible work surface | At least 30" (760 mm) wide, top 34" (865 mm) max (804.3) | Adjustable section at least 2200 mm long, or the user's spec (3.34) |
| Height adjustable | 29"-36" (735-915 mm) where adjustable (804.3, 606.3 exc.) | Worktop drainage adaptable between 700-950 mm (3.34) |
| Knee space | ADA 306 | 700 mm wide leg space below parts of the worktop (3.34) |
| Sink | Exception allows 29"-36" adjustable (606.3) | Sink bowl 150 mm deep max (3.34) |
| Oven | — | Oven centreline 800-900 mm above floor (3.34) |
| Storage | At least 50% of shelf space within reach ranges (804.5) | — |
| Worktop length | — | Minimum worktop lengths by bedspaces, Tables 3.3 and 3.4 (adaptable and accessible) |

## 5. Bathroom and WC layouts

- Fitting sizes for dwelling layouts in England: WC with cistern 500 x 700 mm, bath 700 x 1700 mm, basin 600 x 450 mm, hand rinse basin 350 x 200 mm (Appendix D) [N17]. The schedule prints two figures per fitting without naming them; confirm the orientation against Diagram 2.5.
- US accessible toilet rooms: WC position and clearances and grab bars (604, 609), lavatory (606), mirrors (603.3: reflecting surface bottom 40 in max over a lavatory, 35 in elsewhere), baths (607: 30 in clearance alongside), showers (608: transfer 36 x 36 in, roll-in 30 x 60 in min, alternative roll-in 36 x 60 in), compartment toe clearance 9 in (604.8.1.4) [N01]. Values are in the sanitary and kitchens-bathrooms files.
- Doors to accessible WCs open outward or leave the clear floor space clear of the swing (check the jurisdiction's rule).
- Keep the WC on the soil stack wall where possible and show the stack position (typical practice).

## 6. Tiling setting-out (typical practice)

1. Choose the start point so cuts are equal at both ends of a wall or floor and no cut tile is less than half a tile, the same rule as ceiling grids (`rcp-lighting.md` 3). Where a feature (window, basin, niche) is central, centre the grid on it.
2. Set the first full course at a visible line (worktop, bath rim, or a datum), not at an uneven floor.
3. Show the grid on the elevation, with the start point and joint width (from the product data).
4. Mark trims, movement joints and changes of tile.

## 7. Services setting-out

- Dimension every service point from two fixed references in plan and from FFL in height, and say whether the height is to centre or to the bottom.
- Sockets and switches in accessible dwellings and buildings follow the jurisdiction's height range; the US reach range is 15-48 in forward (308.2.1) [N01]. England M4(2)/M4(3) height figures are not in the data; ask.
- Gas, electrical and drainage rules (zones around baths and showers, separation from sinks) are regulated by trade and wiring rules not researched here; leave them to the installer and say so.

## 8. Drawings and schedules

- Cabinet codes: a code per type and width (e.g. `B600` base 600, `W500` wall 500, `T600` tall), keyed to a cabinet schedule (typical practice; use the supplier's codes when given).
- Elevations at 1:20 or ½" with every cabinet coded, appliance models referenced, and heights to FFL.
- Sanitaryware schedule: code, item, manufacturer and model (as specified), tap and waste, mounting height, room.

## 9. In AutoCAD and Revit

| Need | AutoCAD | Revit |
|---|---|---|
| Cabinets | Dynamic blocks with width, depth and door options, attributes for code | Casework families (wall-based or not), type per width |
| Worktop | Closed polyline with cut-outs | Countertop family or a casework part; no in-place families through the add-in |
| Elevations | 2D linework per wall, consistent datum | Interior elevation views cropped to each wall |
| Sanitaryware | Blocks with clearance zones as separate geometry | Plumbing fixture families |
| Tiling | Hatch or lines set out from the start point | Wall and floor material patterns; pattern alignment is manual |

## 10. Checks

| Check | Signal |
|---|---|
| Survey basis | Layout drawn on an assumed square room without survey dimensions |
| Clearances | Aisles below the governing figure; appliance doors clashing |
| Accessible kitchen | Missing knee space, work surface too high, storage out of reach |
| Fittings fit | Fittings smaller than the stated sizes without a product reason |
| Tiling | Cuts under half a tile at visible edges |
| Services | Service points without a datum or without two references |
