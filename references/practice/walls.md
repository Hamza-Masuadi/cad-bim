# Walls

Read this before drawing walls in AutoCAD, modelling walls in Revit, creating wall types, or checking walls. Source tags point to `_SOURCES.md`.

Contents: 1 Decide first · 2 Typical thicknesses · 3 AutoCAD walls · 4 Revit walls · 5 Curtain walls, sweeps, reveals · 6 Common errors and detection

## 1. Decide these before the first wall

| Decision | Options | Why it matters |
|---|---|---|
| Wall types | Project's list of types (construction, thickness, fire rating, acoustic rating) | Every other decision (thickness, location line, tag) follows from the type. Never invent a type when the project has a list; ask. |
| Setting-out reference | Grid, structural face, centreline of stud, finish face | Determines which line you draw (CAD) or which location line you use (Revit), and what dimensions run to. |
| Heights | Floor to floor, to underside of slab, to ceiling + allowance, partial height | Drives Revit top constraints and whether partitions stop at the ceiling or go to the slab (acoustic and fire separations usually go to the slab). |
| Status | New, existing to remain, existing to demolish | CAD layer status field or Revit phase. |
| Units and precision | mm or ft-in; dimension rounding | See `dimensions-annotation.md`. |

## 2. Typical nominal thicknesses

These are typical practice, not standards. The project's wall types govern; read them from the template or ask. Actual product sizes vary by manufacturer and country.

| Construction | Metric typical | Imperial typical |
|---|---|---|
| Metal stud partition, one board each side | 70 or 92 mm stud + 12.5 or 15 mm board each side ≈ 95-125 mm | 2½" or 3⅝" stud + ⅝" gypsum each side ≈ 3¾"-4⅞" |
| Metal stud, two boards each side (fire/acoustic) | ≈ 120-150 mm | ≈ 5⅛"-6⅛" |
| Timber stud partition | 75-100 mm stud + boards ≈ 100-125 mm | 2x4 (3½") + ½" or ⅝" each side ≈ 4½"-4¾" |
| Timber frame external | 140 mm stud + sheathing + cladding ≈ 250-300 mm | 2x6 (5½") + sheathing + cladding ≈ 7"-9" |
| Concrete block (single leaf) | 100, 140, 190 or 215 mm block (country-specific) | 6", 8", 12" nominal (actual ⅜" less, e.g. 7⅝") |
| Brick leaf | 102.5 mm (UK), 90-110 mm elsewhere | 3⅝" modular |
| Masonry cavity wall | brick + cavity + insulation + block + finish ≈ 300-400 mm | brick + air space + insulation + CMU ≈ 12"-16" |
| In-situ concrete wall | 150, 200, 250, 300 mm | 6", 8", 10", 12" |
| Shaft wall | ≈ 90-150 mm | ≈ 3½"-6" |

Rule: dimension and model the **actual** thickness of the assembly, and name the type with it (e.g. "Partition - MS 92 + 2x12.5 - 142" or "GWB 3-5/8 MS 1HR - 4 7/8"). Nominal block sizes are labels, not dimensions.

## 3. Walls in AutoCAD

### 3.1 Drawing method
- Draw each wall as two parallel lines (or a closed polyline per wall segment) at the type's real thickness. Draw from the setting-out line: offset the centreline or face by the correct distances, then remove the construction line or keep it on a no-plot layer.
- Keep wall lines on wall layers and the poché on a separate hatch layer (US style e.g. `A-WALL` and `A-WALL-PATT`; partial height `A-WALL-PRHT`; status suffix `-N`, `-E`, `-D` or `NEWW/EXST/DEMO` minor groups) [S05][S09]. See `cad-standards.md`.
- One layer per wall category if the standard wants it (exterior, interior, fire-rated: `A-WALL-FIRE` [S05]). Fire-rated walls are often also shown with a rating pattern or line on a separate layer.
- Poché: solid or grey fill at 1:100 / 1/8"; material hatch per layer at 1:50 / 1/4" and larger. Hatch must be associative to closed wall boundaries.

### 3.2 Junction cleanup
A clean junction has no lines crossing inside the wall and no gaps. Check every one.

| Junction | Correct result | How |
|---|---|---|
| L (corner) | Outer lines meet at the outer corner; inner lines meet at the inner corner | Fillet radius 0 (or trim/extend) each pair |
| T (stem meets through wall) | Through wall's near face is broken over the stem's width; stem lines stop at the through wall's far side only if the stem is the dominant construction (e.g. structural wall through a partition), otherwise at the near face | Break the through wall's face line between the stem lines, trim stem lines to the face |
| X (crossing) | The dominant wall runs through; the other is broken on both sides; or both broken with four inner corners | Break and trim; never leave the crossing lines |
| Different thickness / different type | The higher-priority construction (structure, fire separation) runs through | Decide priority first: structural and fire-rated walls are continuous |
| Wall at opening | Wall lines stop at the jambs; jamb line closes the wall | See `openings.md` |

Why dominance matters: the continuous wall in plan tells the builder which element is built first and which separation must be unbroken (fire, acoustic).

### 3.3 Centreline or face
- Draw from what is dimensioned and fixed. New-build steel or concrete frame: grid and structural faces fixed, partitions set out from them. Refurbishment: existing faces fixed [S49].
- For stud partitions, offices differ: face of stud, face of board, or centreline. The AIA author advises face of board/masonry/concrete excluding applied finishes [S49]. Follow the standard; if none, ask (see `asking-the-user.md`).

## 4. Walls in Revit

### 4.1 Type vs instance
- **Type** (shared by every instance): compound structure (layers, materials, thicknesses, functions), Wall Function (exterior, interior, foundation, retaining, soffit, core-shaft), coarse fill, fire rating, Type Mark. Changing a type changes every wall of that type.
- **Instance**: location line, base constraint and offset, top constraint and offset or unconnected height, room bounding, structural usage, phase created/demolished, Mark, Comments.
- Rule: a wall that differs in construction is a different type. Do not fake a thicker wall with an offset or a second wall laid beside it unless the project deliberately models finishes as separate walls.

### 4.2 Compound structure
Layer functions and their join priority (Revit help and API reference) [N09][N13]:

| Function | Priority | Use |
|---|---|---|
| Structure | 1 (highest) | Load-bearing or principal layer: stud, block, concrete |
| Substrate | 2 | Sheathing, backing board |
| Thermal / Air layer | 3 | Insulation, cavity |
| Finish 1 | 4 | Exterior finish (usually) |
| Finish 2 | 5 (lowest) | Interior finish (usually) |
| Membrane | none, thickness 0 | Vapour/air barriers |

Rules from Autodesk's help [N09][N10]:
- Layers between the two core boundaries form the core. Only one layer inside the core can carry the structural material, and layers outside the core cannot.
- Priorities cannot rise from the core boundary towards the finish face (a finish cannot outrank the substrate inside it).
- A membrane layer has 0 thickness and cannot be in the core. Non-membrane layers are at least 4 mm / ⅛".
- Higher-priority layers join through lower-priority layers of the wall they meet. This is what makes a stud core run through a junction while the boards stop. Wrong functions give junctions where finishes cut the structure.
- Wrapping at ends and inserts controls whether finish layers return into openings and around free ends.

### 4.3 Location line
Options: Wall Centerline (default), Core Centerline, Finish Face: Exterior, Finish Face: Interior, Core Face: Exterior, Core Face: Interior [N08].

| Choose | When | Consequence |
|---|---|---|
| Core Face: Exterior or Finish Face: Exterior | External walls set out from the outer face or the structural line | Thickness changes go inward; the envelope stays put |
| Core Centerline | Structural or masonry walls on grids; walls that may change finish | Finish changes do not move the structure |
| Finish Face: Interior | Rooms with a fixed clear size (lift cores, accessible WCs, plant rooms) | Clear room size holds when the type changes |
| Core Face: Interior | Partitions dimensioned to face of stud | Matches face-of-stud dimensioning |
| Wall Centerline | Quick massing; walls drawn on ellipses (Revit requires it) [N08] | Thickness changes move both faces |

Facts: changing Location Line on an existing wall does not move it, but the wall flips about the location line, so a later flip can move it [N08]. Core-based lines measure from the core at the bottom of the wall [N09]. Pick the location line before you draw; fixing it afterwards means moving walls.

### 4.4 Base and top
- Base Constraint = the level the wall sits on; Base Offset from it. Keep offsets small; a large offset usually means the wall belongs to another level. A published custom checkset flags offsets over 200 mm [S31] (practitioner threshold; the project sets it).
- Top Constraint = "Up to level: X" (with Top Offset) or "Unconnected" (with Unconnected Height). Prefer "Up to level": the wall follows when the level moves. Use Unconnected only for free-standing, partial-height or parapet walls with a fixed height.
- Do not run one wall through many levels; split per storey or per two levels at most. Why: wrong level association in schedules, slow regeneration, and walls that cannot vary per floor [S31][S25].
- Attach Top/Base to roofs, floors or ceilings makes the wall follow the slope. Useful under sloped roofs; some owners forbid attaching walls to floors and roofs because of dependency chains (NY OGS) [S22]. Check the standard.

### 4.5 Joins
- Wall ends join automatically. Join types at a corner: butt, miter, square off. Allow/Disallow Join is per wall end.
- Disallow Join when: a wall meets a curtain wall or a column it should not merge with; walls of different phases; a partition that should stop short of a façade; when Revit's automatic join draws a wrong junction. Then close the end with the correct geometry, not with detail lines.
- Join Geometry (walls with floors, columns) changes cut display; avoid joining hundreds of elements, and note that joined geometry has gone missing in some IFC4 exports [S59, unverified].

### 4.6 Room bounding, phases
- Room Bounding (instance) is on by default for walls. Turn it off for walls that must not split a room (low walls inside a room, bulkhead walls). Use room separation lines only where no wall exists, never on top of a wall (a top warning) [S23][S39].
- Phase Created and Phase Demolished control existing/demolished/new display. Set them; do not use separate "existing" wall types as a substitute unless the project does.

## 5. Curtain walls, sweeps and reveals (brief)

- Curtain wall = a wall type with curtain grids, mullions and panels. Set grid spacing in the type (fixed distance or number) or place grid lines manually; mullion and panel types are separate.
- To insert a curtain wall in a host wall, use the type's Automatically Embed option rather than cutting an opening by hand.
- Doors in curtain walls: use the project's curtain-wall door panel family. NY OGS warns against substituting ordinary door families into curtain panels [S22].
- Wall sweeps (skirting, cornices, string courses) and reveals (grooves) can be part of the wall type (vertical structure) or placed per instance. Use them for linear profiles that follow the wall; do not model them as in-place families [S22].

## 6. Common errors and how to detect them

| Error | Why it harms | Detection signal | Fix |
|---|---|---|---|
| Open or overlapping junctions (CAD) | Wrong poché, wrong areas, builder confusion | Wall line endpoints within a small tolerance of another wall line but not meeting; lines crossing inside wall thickness | Trim/extend/fillet per 3.2 |
| Wall thickness not matching any project type | Wrong tags, schedules, costs | Distance between paired wall lines (CAD) or type width (Revit) not in the type list | Change to the correct type; ask if none fits |
| Duplicate or overlapping walls | Double quantities, join warnings | Revit warning "Highlighted walls overlap"; CAD coincident lines | Delete the duplicate after checking hosted elements |
| Walls slightly off axis | Joins, dimensions and rooms misbehave | Angle within a small epsilon of 0/90° but not equal; Revit "slightly off axis" warning [S39] | Rotate to exact angle or redraw on snaps |
| Wrong location line | Faces move when types change | Location line value vs the project rule per wall function | Re-set, then move to restore the intended face |
| Walls spanning many levels | Wrong level schedules, slow model | Base and top constraints more than two levels apart [S31] | Split at levels |
| Large base offsets | Wall belongs to another level | Base offset above project threshold [S31] | Re-host to the right level |
| Unconnected top where a level exists | Wall does not follow level changes | Top Constraint "Unconnected" on full-height walls | Set "Up to level" |
| Room separation line on a wall | Ambiguous boundary warning | Revit warning "A wall and a room separation line overlap" [S23] | Delete the separation line |
| Wall used to fake a finish layer or skirting | Double counting, odd joins | Thin wall types (< about 30 mm) beside other walls | Put the layer in the type or use a sweep (unless the project models finishes separately) |
| Partition stops at ceiling where separation needs the slab | Fire or acoustic breach | Top constraint below the slab on fire/acoustic-rated types | Raise to the slab, coordinate with ceiling |
