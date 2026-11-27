# Floors, ceilings and roofs

Read this before creating levels, slabs, floor finishes, ceilings, reflected ceiling plans (RCPs) or roofs, or before checking them. Source tags point to `_SOURCES.md`.

Contents: 1 Levels and datums · 2 Floors and slabs · 3 Ceilings · 4 Reflected ceiling plans · 5 Roofs · 6 Checks

## 1. Levels and datums

| Term | Meaning | Who uses it |
|---|---|---|
| FFL (finished floor level) | Top of the floor finish | Architects, interiors, door thresholds, stair maths |
| SSL / TOS (structural slab level, top of slab) | Top of the structural slab, below screed and finish | Structural engineers |
| AFF (above finished floor) | Heights measured from FFL, US usage ("9'-0" AFF") | Ceilings, mounting heights |
| Project datum | The building's zero (often ground-floor FFL = ±0.000, or a US-style 100'-0") | Everyone on the project |
| Survey datum | Height above a national datum (e.g. sea level datum) | Site, civil, survey |

Decision rules:
- Ask which datum the project uses and whether level values are FFL or SSL (see `asking-the-user.md`). Write it on every section ("Levels are FFL above project datum ±0.000 = xx.xxx AOD").
- Revit: one level per floor that someone builds to. NY OGS sets one level per slab, with levels and grids owned by structure and copy-monitored into the architectural model [S22]. When the architectural model uses FFL and structure uses SSL, keep both sets clearly named (e.g. "L02 FFL", "L02 SSL") rather than offsetting every element.
- Do not create levels for every ceiling height or sill; use offsets. Extra levels create extra plan views and confuse level-based schedules.
- The difference FFL − SSL is the floor build-up (screed, raised floor, finish). Record it per zone; it drives stair risers, door undercuts and ceiling voids.

## 2. Floors and slabs

### 2.1 What to model
- Structural slab: a floor type with the structural layer(s), usually by the structural engineer, or modelled by the architect when there is no structural model.
- Build-up and finish: either as layers in one compound floor type (simple projects) or as a separate thin finish floor per room or zone (projects with many finishes, raised floors, or where structure is linked). Pick one approach per project and say which.
- Why separate finish floors: the structural model owns the slab; the architect still needs finish areas and FFL. Why one compound floor: fewer elements and no gaps.

### 2.2 Revit floor rules
- A floor's top sits at its level plus Height Offset. Compound layers hang below the top. So a floor type with 75 mm screed and 250 mm slab placed at "L02 FFL" puts the slab top 75 mm below FFL, which is correct only if FFL is the level.
- Sketch boundaries on wall faces or core faces consistently. Floors should stop at the inside face of external walls or run under them depending on the construction detail; follow the project's wall-floor detail.
- Openings: shafts for vertical openings through several floors (lifts, risers, stairs) [S22]; floor edits for single-floor holes.
- Falls: shape editing (points, split lines) or a sloped floor via slope arrow; for wet areas and balconies keep falls in the finish or screed layer (variable layer), not by tilting the slab.
- Avoid sketches with many splines and complex boundaries; they are expensive [S23].
- Slab edges, edge beams and upstands as floor edge (slab edge) elements following the edge.

### 2.3 AutoCAD
- Slabs appear only in sections and as edges in plan. Show slab edges, set-downs and openings in plan with the overhead or hidden linetype as appropriate. Mark level changes with spot levels and a step line.

## 3. Ceilings

- Ceiling height is set as an offset above the room's level (FFL), e.g. 2700 mm or 9'-0" AFF. Coordinate the void above with structure and services before fixing it.
- Revit ceilings: basic (no build-up, quick) or compound (grid, tile, board). Created by picking a room's boundary (automatic) or sketched. Rooms bound by ceilings for volume when Room Bounding is on.
- Partition heights follow ceilings: partitions often stop at the ceiling plus a small allowance, but fire, acoustic and security walls usually go to the slab (see `walls.md`).
- Bulkheads (soffits) as ceiling edges at a different height or as wall types with soffit function; tag their height.

## 4. Reflected ceiling plans (RCP)

The RCP is drawn as if the floor were a mirror: looking up, so it has the same orientation as the floor plan.

Revit facts [N11]: a reflected ceiling plan's view direction is up from the cut plane; the Bottom setting of the view range is not used; view depth extends upward from the cut plane. Typical practice is to set the RCP cut plane just below the lowest ceiling and above door heads so doors do not show as openings (confirm with the template).

### 4.1 What an RCP shows
- Walls cut at the RCP cut plane (doors usually omitted; openings shown as headers).
- Ceiling types by pattern or tag; ceiling heights tagged per area ("2700" or "9'-0"").
- Bulkheads, soffits and changes in height with their heights.
- Light fixtures, diffusers and grilles, sprinkler heads, smoke detectors, speakers, access panels, as coordinated from the engineers' models.
- A legend of ceiling types, finishes and fixture symbols.

### 4.2 Setting out a ceiling grid (typical practice; confirm with the designer)

`rcp-lighting.md` 3 has the full method and the sourced module sizes and border rule.
1. Pick the grid module: common tile sizes are 600 x 600 or 600 x 1200 mm, and 2' x 2' or 2' x 4' (market-dependent).
2. Centre the grid on the room in each direction, choosing between "tile centred" and "grid line centred" so that the border tiles on opposite sides are equal and no border tile is less than about half a tile. Why: thin slivers look wrong and are hard to install.
3. Where the room is irregular, centre on the dominant feature (main entrance axis, window bay, light rows) and accept unequal borders at the back of house side.
4. Align lights and diffusers to the grid, centred in tiles; align rows across adjoining rooms where they read together (corridors, open offices).
5. In Revit, move and rotate the grid by selecting a grid line of the ceiling; face-hosted fixtures follow the ceiling if it moves.

## 5. Roofs

### 5.1 Types
| Roof | How defined | Revit method |
|---|---|---|
| Hip | All edges slope | Footprint with every edge "defines slope" |
| Gable | Two edges slope, gable ends do not | Footprint; clear "defines slope" on gable edges |
| Shed / mono-pitch | One edge slopes | Footprint, one sloping edge (or slope arrow) |
| Flat (low slope) | No sloping edges; falls in the build-up | Footprint with no sloping edges, falls via shape editing or a variable-thickness layer |
| Barrel, extruded profile | Profile swept along a length | Extrusion roof |
| Complex form | Surface of a mass | Roof by face |

### 5.2 Footprint and slope rules
- Footprint edges sit at the wall line you choose (wall face, core, or with an overhang). The roof's base is its level plus Base Offset (the plate or eaves level); slope is entered per sloping edge.
- Slope notation: metric drawings usually give degrees (e.g. 30°) or a percentage; US drawings give rise in 12 (e.g. 6:12 or 6/12). Convert exactly: 6:12 = 26.57°.
- Overhang: set per edge or for the whole sketch. Fascia, gutter and soffit as roof edge elements (fascia, gutter) and soffit elements.
- Walls under a sloped roof: attach the wall tops to the roof (where the project allows attaching; NY OGS forbids attaching walls to floors and roofs) [S22]. Otherwise set wall top constraints and edit profiles.
- Flat roof falls: the minimum fall is a code and specification matter (not verified here); model it in the insulation or screed layer and confirm the value with the user.

### 5.3 AutoCAD
- Roof plans: ridges, hips and valleys as lines; slope arrows with the slope value pointing downhill; gutters and downpipes; overhangs dashed in floor plans.

## 6. Checks

| Check | Signal |
|---|---|
| Levels named and typed consistently | Mixed FFL/SSL naming; levels without a plan view; duplicate elevations |
| Floors at the right level | Floor on a level but with a large offset (> project threshold, e.g. 200 mm in one published checkset [S31]) |
| No overlapping floors | Two floor elements occupying the same area on the same level |
| Shafts through all floors | A lift or riser shaft opening missing on one level |
| Ceiling heights tagged on every RCP area | Ceiling with no height tag |
| Ceiling grid border tiles | Border tile less than half a module |
| Roof slopes consistent | Two roofs meant to match with different slopes |
| Walls under roofs | Gaps between wall tops and roof soffit in sections |
