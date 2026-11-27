# Openings: doors and windows

Read this before placing, numbering, tagging, scheduling or checking doors and windows. Building-code compliance (widths, egress, accessibility, guarding) is the user's responsibility; values below carry their source and jurisdiction so you can flag likely problems, not certify.

Contents: 1 Plan symbols · 2 Swing and handing · 3 Sizes · 4 Sill and head heights · 5 Marks and numbering · 6 Revit hosting · 7 Tags vs schedules · 8 Checks

## 1. Plan symbols

| Opening | Plan convention (typical practice) |
|---|---|
| Single swing door | Wall lines stop at the jambs; frame shown at the jambs (at 1:50 / ¼" and larger); leaf drawn as a line or thin rectangle at 90° open; swing as a quarter arc from the leaf tip to the closed position, thin line |
| Double door | Two leaves and two arcs meeting at the centre; unequal pairs drawn to their real leaf widths |
| Double-acting | Leaf with arcs both sides, or leaf with a short arc each side |
| Sliding | Leaf line(s) parallel to the wall, offset, with a direction arrow; no arc |
| Pocket | Leaf partly inside the wall, the pocket shown dashed |
| Bi-fold | Zig-zag leaves with small arcs or a direction arrow |
| Overhead / roller | Dashed line at the head position, label |
| Window | Wall lines become frame lines; glass shown as one or two thin lines in the frame; sill line projecting on the outside where there is an external sill |
| Opening without door | Jamb lines only; dashed head line if the head is lower than the ceiling |

Rules:
- The arc shows the leaf's path. It must not overlap fixed furniture, other leaves or equipment; if it does, the plan has a conflict to report.
- In elevation, show the hinge side with a dashed or thin "V" whose apex points to the hinge side (common US/UK convention). Some regions draw it the other way; follow the project legend.
- Keep door leaves on the door layer and arcs on the swing layer (US style `A-DOOR` and `A-DOOR-SWNG` [S05]) so swings can be turned off for furniture or area plans.

## 2. Swing and handing

Handing conventions differ by region and trade. Never assume; ask, or infer from the project's door schedule legend.

| Convention | How it is read | Labels |
|---|---|---|
| North American (ANSI/BHMA, hardware trade) | Stand on the outside (secure or corridor side). Hinges on your left = left hand; on your right = right hand. If the door swings toward you, add "reverse" [N15] | LH, RH, LHR, RHR |
| DIN (Germany and parts of northern Europe) | View from the pull side; hinge side gives left or right [N15] | DIN left / DIN right |
| BS EN / ISO style | Defined by closing direction, clockwise or anticlockwise; ironmongers write 5 and 6 (pairs 5/6) [N15]. Which face it is viewed from: confirm with the project specification | 5, 6 |

How to infer when nobody tells you: look for a legend on the door schedule or the general notes sheet; look for existing handing values in the schedule and check them against two placed doors; otherwise report handing as "not set" rather than guessing. In Revit, doors have flip controls for facing and hand; record handing in a parameter only if the schedule needs it, computed from the agreed convention.

Swing direction decisions (typical practice; the code governs):
- Egress doors serving many occupants usually swing in the direction of escape; confirm with the code.
- Doors into corridors are often recessed or swing into the room so the leaf does not block the corridor.
- WC and bathroom doors: consider outward opening or removable hardware where the user may collapse against the door; accessible WCs have their own rules.
- Doors should open against a wall (hinge side near a return), not into the room's middle, and must not clash with each other.

## 3. Sizes: nominal, clear, structural

| Term | Meaning |
|---|---|
| Nominal / leaf size | The door leaf width and height, as scheduled (e.g. 3'-0" x 7'-0", or 926 x 2040 mm) |
| Clear opening / effective clear width | What a person or wheelchair gets: measured to the face of the open leaf and the stop. This is what codes regulate |
| Frame size / coordinating size | Leaf plus frame |
| Rough opening (US) / structural opening (UK) | Hole in the wall the frame fits into: frame plus fixing tolerance. Take it from the frame manufacturer or the project detail, not from a rule of thumb |

Published clear-width minimums (cite the jurisdiction when you flag them):

| Source | Requirement |
|---|---|
| 2010 ADA Standards (US), 404.2.3 | Clear width 32" (815 mm) min, measured between the face of the door and the stop with the door open 90°; openings deeper than 24" (610 mm) need 36" (915 mm) [N01] |
| IBC 2021 (US model code), 1010.1.1 (text as reproduced by ICC) | Clear width 32" (813 mm) min; clear height 80" (2032 mm) min [N04] |
| Approved Document M Vol 2 (England, non-dwellings), Table 2 | Effective clear width, new buildings: 800 mm straight-on or off a route at least 1500 mm wide; 825 mm off a route at least 1200 mm wide; 1000 mm for external doors used by the general public [N03] |

Consequence: a 900 mm or 3'-0" leaf does not give 900 mm / 36" clear. Check clear width, not leaf width, against requirements.

ADA maneuvering clearance at a manual swinging door, front approach (404.2.4.1, Table 404.2.4.1) [N01]: pull side 60" (1525 mm) perpendicular to the door and 18" (455 mm) beyond the latch side; push side 48" (1220 mm) perpendicular, 0" beyond the latch (add 12" / 305 mm if closer and latch). Doors in series: 48" (1220 mm) plus the width of any door swinging into the space (404.2.6). Hardware 34"-48" (865-1220 mm) above finish floor (404.2.7). Thresholds ½" (13 mm) max (404.2.5).

## 4. Sill and head heights

Typical practice; confirm with the project standard and the code.

| Item | Metric typical | Imperial typical |
|---|---|---|
| Door leaf height | 1981, 2040 or 2100 mm (market-dependent) | 6'-8" or 7'-0" (8'-0" in commercial work) |
| Door head (structural or frame head) | leaf + frame ≈ 2100-2150 mm | 7'-0" to 7'-2" |
| Window head | Align with door heads unless the elevation design says otherwise | Same |
| Window sill, living spaces | 800-1100 mm | 2'-6" to 3'-6" |
| Window sill above a worktop | worktop height + splashback, ≈ 1050-1100 mm | ≈ 3'-6" |

Code points to flag (not to certify):
- England, Approved Document K, Diagram 3.1 table: minimum guarding height at opening windows (except roof windows) is 800 mm [N02]. A sill lower than that on an opening window needs guarding.
- US: window-opening fall protection and egress-window sill heights are in the IBC/IRC; values were not verified in this research. Ask the user to confirm.

In Revit, windows carry Sill Height and Head Height (instance) relative to the host level. Set heights by type where the project has standard heights so they stay consistent.

## 5. Marks and numbering

| Scheme | Example | Use when |
|---|---|---|
| By room number | Door 101, second door into room 101 = 101A, 101B | Most common in US practice; door numbers stay stable when doors are added elsewhere |
| By level and sequence | D-02-015 | Large projects, FM handover |
| Sequential | D001, D002 … | Small projects |
| Windows by type | Type W1, W2 (Type Mark) with optional instance numbers | Windows are usually scheduled by type |

Rules:
- Every door has a unique mark. Duplicate marks produce a Revit warning and an ambiguous schedule [S23][S39].
- A door takes the number of the room it opens into (the room on its swing side) or the room it serves; pick one rule per project and keep it.
- LAWA requires every space, including shafts and stairs, to have a number; door numbering by room depends on that [S75].
- Keep numbering in a parameter (Mark in Revit, an attribute in AutoCAD), never in loose text.

## 6. Revit hosting behaviour

- Doors and windows are hosted families: they cut their host wall and move with it. Deleting a wall deletes its inserts.
- What breaks hosting:
  - Deleting and redrawing a wall instead of editing it: the inserts go with it and their data is lost [S36].
  - Overlapping walls: the insert may cut one and leave the other uncut.
  - A wall too short or too thin for the family: placement fails or the opening protrudes.
  - Changing a wall to a curtain wall: ordinary inserts cannot be hosted.
  - Copy/paste between projects or levels: inserts can lose their host or level.
- Wall Opening tool cuts a hole with no door or window data. Do not use it for windows; use a window family [S22]. Use shaft openings for shafts and lifts [S22].
- From Room / To Room: Revit records which rooms a door connects (phase-dependent). Flipping a door's facing swaps them; check after flipping if the schedule uses them.
- Model sills separately from windows if the project requires it (NY OGS rule) [S22].
- Workshared models: an insert edit may need the host wall's ownership too; check ownership of both before changing.

## 7. Tags vs schedules

- Tags read a parameter (Mark, Type Mark) from the element; schedules list the same data. Change the data, never the tag text.
- Plan: tag every door with its Mark; tag windows with Type Mark. Tag in the view, not on the sheet [S22].
- Schedule: door schedule (Mark, from/to room, leaf size, type, material, fire rating, hardware set, remarks); window schedule by type. See `schedules-tags.md`.
- AutoCAD: door and window blocks carry attributes (MARK, WIDTH, HEIGHT, TYPE, FIRE, HW); the schedule is a table built from those attributes, not typed.

## 8. Checks

| Check | Signal |
|---|---|
| Every opening hosted and cutting its wall | Revit: host present; CAD: wall lines broken at the jambs |
| No overlapping inserts | Bounding boxes in the same wall overlapping |
| Door swings clear | Arc intersecting furniture, fixtures, other arcs |
| Clear width plausible against the jurisdiction's value | Leaf width minus stop and leaf thickness below the cited minimum |
| Unique marks | Duplicate Mark values |
| Every door tagged in its plan | Untagged door count per view |
| Sill and head consistent | Same type with different sill/head heights without a reason |
| Handing set where the schedule needs it | Empty handing values |
