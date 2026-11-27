# Revit planted-defect test

Specification of the test. The job files that carry it out, the manual steps, the run order and the expected result
per step are in `revit/README.md`. Goal: models with a known number of defects, so that an
audit with `data/checks/revit.json` and `data/standards/us-ncs-style.json` (for its workset and view patterns) must
report exactly the planted count for every automatic check. Counts are **findings**: one per record for per-record
evaluators, one per warning type for `group_count_over_threshold`, exactly one for `count_over_threshold` and for
document-level `field_range` checks.

## 0. Rules

- Units: feet and inches; coordinates are (x, y) in feet on Level 1 unless stated.
- Wall type `Generic - 8"` (or the template's nearest generic wall) for every wall; walls are room bounding.
- **Baseline first.** Build the baseline RB, audit it, and confirm 0 findings on every automatic check. If a check
  fails on RB (template content differs between releases), fix RB, not the expected counts, and record the baseline
  report with the test.
- Plant in the order listed; the order matters where noted.
- Checks with evaluator `manual_review` return exactly 1 finding in every model; the test asserts that and nothing
  more.
- Warnings: Revit raises warnings for some defects (duplicate marks, rooms, spaces). Model R1 therefore asserts
  `rv-053.warning-gate` only as "1" (there is at least one warning) and does not assert `rv-003.warning-types`;
  model RW holds exactly two known warnings and asserts both.
- Files in one folder: `RB.rvt`, `R1.rvt`, `RW.rvt`, `RF.rvt`, CAD files `imp.dwg`, `cad-a.dwg`, `cad-b.dwg`,
  `cad-c.dwg` (each one LINE (0,0)-(10,0) in feet, INSUNITS feet), Revit links `link-a.rvt`, `link-b.rvt`,
  `link-c.rvt` (each a non-workshared model with one wall), `img.png` (any 64 x 64 PNG), a furniture family
  (`Desk.rfa` from the default library or any furniture family), a second furniture family `Chair.rfa`, an RPC
  entourage family (`RPC Male.rfa` or any family of category Entourage), and `SA-Generic-Box.rfa` (made for the
  test: category Generic Models, one 1' cube extrusion, IFC export parameters left blank).

## 1. Baseline RB

1. New project from the release's default imperial architectural template. Project Units, Length: feet and
   fractional inches, rounding to the nearest 1/2".
2. Levels `Level 1` 0', `Level 2` 12', `Level 3` 24', `Level 4` 36' (create missing ones with plan views); grids `1`
   at x = 0 and `2` at x = 40 (from y = −10 to y = 40), `A` at y = 0 and `B` at y = 30 (from x = −10 to x = 50). Pin all
   levels and grids.
3. Enable worksharing. Worksets: `Shared Levels and Grids` (levels and grids), rename `Workset1` to `A_Interior`,
   create `A_FFE`, `X_CAD Link`, `X_Revit Link A`, `X_Revit Link B`. Active workset `A_Interior` unless stated.
4. Walls Level 1 to Level 2 (top constraint Level 2, offsets 0) on `A_Interior`: (0,0)-(40,0), (40,0)-(40,30),
   (40,30)-(0,30), (0,30)-(0,0).
5. Room in the rectangle: Name `OFFICE`, Number `101`.
6. One door (template single flush door) in wall (0,0)-(40,0) at x = 10, Mark `D1` (set explicitly, so P03 can copy
   it).
7. Load `Desk.rfa`; one desk on `A_FFE` at (20,15), Elevation from Level 0.
8. Floor plan `L1 - Plan` of Level 1 with the template's architectural plan view template applied; make sure the
   template does not hide Entourage. Sheet `A-101` (a title block is optional: its name differs between releases
   and no automatic check reads it); place `L1 - Plan` on it.
9. Delete every other view that is not on a sheet (other level plans, ceiling plans, elevations and their markers,
   3D views, legends, schedules).
10. Purge Unused, repeated until nothing is left (note which floor type survives: P22 needs one). Then set every remaining dimension type's Units Format to "Use
    project settings".
11. Review Warnings shows none. Save. Audit: 0 findings on every automatic check.

## 2. R1: the main defect model (copy of RB)

| # | Check(s) | Plant exactly |
|---|---|---|
| P01 | rv-001.in-place-count, rv-002.in-place-generic, rv-052.generic-ifc-class | In-place family `IP-Generic-Block`, category Generic Models: one 2' x 2' x 2' extrusion at (60,0). In-place family `IP-Counter`, category Casework: one 6' x 2' x 3' extrusion at (60,10). (rv-001 is one count finding; rv-002 counts IP-Generic-Block; rv-052 counts its type.) |
| P02 | rv-052.generic-ifc-class | Load `SA-Generic-Box.rfa` and place 1 instance at (70,0), Elevation from Level 0. Second rv-052 finding. |
| P03 | rv-005.duplicate-mark | New wall (0,−60)-(20,−60), Level 1 to Level 2, and a second door of the same type in it at 10' from its start, Mark `D1`, Comments `P03`. (A new wall because a job can name an existing wall only by an id from a snapshot; Comments lets the fix find the door.) |
| P04 | rv-005.duplicate-room-number | Walls (0,−40)-(20,−40)-(20,−20)-(0,−20)-(0,−40); place a room in it, Name `P04 ROOM`, Number `101`. |
| P18 | rv-028.default-view-names | **Before P05:** a floor plan of Level 1 named `L1 - Plan Copy 1` with the `L1 - Plan` view template (the name a plain Duplicate gives; no op duplicates a view), and a section across the room, (20,−5) to (20,35), named `Section 1`. Leave both names and do not place them on sheets. |
| P05 | rv-007.hidden-elements | In `L1 - Plan`, Hide in View > Elements: the `IP-Counter` instance and the `SA-Generic-Box` instance (2 elements, 1 view). |
| P06 | rv-009.typed-tags | Text note `101` in `L1 - Plan` at (50,50). |
| P07 | rv-012.cad-imported | Import CAD `imp.dwg` into `L1 - Plan` (Import, not Link), origin to origin. |
| P08 | rv-014.cad-link-pinned | Link CAD `cad-a.dwg`, relative path, on workset `X_CAD Link`, **unpinned**. |
| P09 | rv-014.cad-link-path | Link CAD `cad-b.dwg`, **absolute** path, on `X_CAD Link`, pinned. |
| P10 | rv-014.cad-link-workset | Link CAD `cad-c.dwg`, relative path, on workset **`A_Interior`**, pinned. |
| P11 | rv-015.revit-link-path | Link `link-a.rvt` origin to origin, **absolute** path, on `X_Revit Link A`. |
| P12 | rv-015.revit-link-own-workset | Link `link-b.rvt` and `link-c.rvt` origin to origin, relative paths, **both** on `X_Revit Link B` (1 finding: the second link). |
| P13 | rv-017.datums-pinned | Unpin grid `B` and level `Level 3` (2 findings). |
| P14 | rv-020.ffe-workset | A second desk at (30,15) on workset **`A_Interior`**, Elevation from Level 0, Comments `P14` (so the fix can find it). |
| P15 | rv-022.design-options | Design option set `Option Set 1` with its primary option and one secondary option, both empty. |
| P16 | rv-023.group-count, rv-023.unused-group-types | Desk on `A_FFE` at (80,0), grouped as model group `G-Used` (1 instance). Another desk on `A_FFE` at (80,10), grouped as `G-Unused`, then delete that group instance (the group type stays). |
| P17 | rv-027.type-copy-names | Duplicate the wall type and name the copy `SA Wall 2 2` (no instances). |
| P19 | rv-029.sheet-views-template | Floor plan `L2 - Plan` of Level 2 with **no** view template (if its view type assigns a default template, remove it); place it on new sheet `A-102`. |
| P20 | rv-030.walls-spanning-levels | Wall (0,−80)-(20,−80), base Level 1, top constraint Level 4 (levelSpan 3). |
| P21 | rv-031.offset-from-level | Desk on `A_FFE` at (30,−80) with Elevation from Level 1'-0" (304.8 mm). |
| P22 | rv-034.walls-attached | Floor on Level 2 with boundary (40,−100)-(60,−100)-(60,−90)-(40,−90); wall (40,−95)-(60,−95) base Level 1, unconnected height 10', then Attach Top to that floor. |
| P23 | rv-035.mass-instances | In-place mass (Massing & Site > In-Place Mass) `IP-Mass`, one 10' cube at (100,0). It is also an in-place family; rv-001 stays one finding. |
| P24 | rv-037.rooms | (a) One unplaced room (a new row in a room schedule, or `NewRoom(phase)`). (b) Walls (20,−120)-(0,−120)-(0,−140)-(20,−140), open on the side x = 20; place a room at (10,−130), which is not enclosed. (c) Walls (40,−140)-(60,−140)-(60,−120)-(40,−120)-(40,−140) and a divider (50,−140)-(50,−120); place rooms at (45,−130) and (55,−130); then delete the divider, so the second room is redundant. (The engine's `room.place` refuses to create a redundant room, hence the divider.) 3 findings. Keep the auto-assigned numbers (all unique). |
| P25 | rv-038.duplicate-spaces | Two MEP spaces in room `101` (Analyze > Space). 1 finding. |
| P26 | rv-051.ifc-view-fine | 3D view named `IFC Export`, Detail Level anything but Fine (a new 3D view's default), not on a sheet. |
| P27 | rv-042.unused-families | Load `Chair.rfa` and place no instance. |
| P28 | rv-045.dim-precision | Duplicate a linear dimension type as `SA Linear 1-256`; Units Format: not project settings, feet and fractional inches, rounding 1/256". No instance needed. |
| P29 | rv-046.annotation-on-sheets | Text note `GENERAL NOTES` placed on sheet `A-101` (on the sheet, not in a view). |
| P30 | rv-047.entourage-in-documents | Load the RPC entourage family; place 1 instance in room `101` at (10,20) on `A_Interior`, visible in `L1 - Plan`. |
| P31 | rv-048.images | Insert `img.png` as an image on sheet `A-101`. |
| P32 | rv-048.plan-regions | One plan region in `L1 - Plan`, (2,2)-(10,10). |

Things that must not change a count, by construction: grouped desks and the P21 desk are on `A_FFE`; only the P21
desk has an elevation offset; in-place families and the generic box sit outside every room and have no level offset;
only the P20 wall spans more than two levels (the P22 wall's attached top is at Level 2); the copied view was made
before elements were hidden; `L2 - Plan` shows no entourage (it is on Level 1); `GENERAL NOTES` does not look like a
mark; rooms other than P04 keep unique numbers; all links and imports sit at the origin; the P03 wall and the P24 walls
are Level 1 to Level 2 and hold no room that counts elsewhere.

## 3. RW: warnings (copy of RB)

| # | Check(s) | Plant exactly |
|---|---|---|
| W1 | rv-004.separation-overlap | Room separation line on Level 1 from (40,5) to (40,25), on the location line of wall (40,0)-(40,30). Revit raises one "A wall and a room separation line overlap" warning. |
| W2 | rv-006.off-axis | Wall (0,−40) to (20, −40 + 20 x tan 0.05 degrees), i.e. 0.0175' (about 0.21") of rise over 20'. Revit raises one "slightly off axis" warning. If it does not at this angle, reduce the angle until it does and record the angle in the job. |

After planting, Review Warnings must list exactly these two warnings.

## 4. RF: far from the origin (copy of RB)

| # | Check | Plant exactly |
|---|---|---|
| F1 | rv-016.far-from-origin | Wall from (108,000, 0) to (108,020, 0) feet, about 32.9 km from the internal origin (more than the 32,186,880 mm limit). If the release refuses to create geometry that far, instead run the test with a copy of the check whose `max` is 1,000,000 (1 km) and place the wall at (3,300, 0)-(3,320, 0) feet (about 1.006 km), and record which variant was used. |

RF does not assert `rv-003.warning-types` or `rv-053.warning-gate` (Revit may warn about the distance).

## 5. Expected results

"n/a" = not asserted (see the warning rule in section 0).

| Check id | R1 | RW | RF |
|---|---|---|---|
| rv-001.in-place-count | 1 | 0 | 0 |
| rv-002.in-place-generic | 1 | 0 | 0 |
| rv-003.warning-types | n/a | 2 | n/a |
| rv-004.separation-overlap | 0 | 1 | 0 |
| rv-005.duplicate-mark | 1 | 0 | 0 |
| rv-005.duplicate-room-number | 1 | 0 | 0 |
| rv-006.off-axis | 0 | 1 | 0 |
| rv-007.hidden-elements | 1 | 0 | 0 |
| rv-009.typed-tags | 1 | 0 | 0 |
| rv-012.cad-imported | 1 | 0 | 0 |
| rv-014.cad-link-path | 1 | 0 | 0 |
| rv-014.cad-link-pinned | 1 | 0 | 0 |
| rv-014.cad-link-workset | 1 | 0 | 0 |
| rv-015.revit-link-own-workset | 1 | 0 | 0 |
| rv-015.revit-link-path | 1 | 0 | 0 |
| rv-016.far-from-origin | 0 | 0 | 1 |
| rv-017.datums-pinned | 2 | 0 | 0 |
| rv-020.ffe-workset | 1 | 0 | 0 |
| rv-022.design-options | 1 | 0 | 0 |
| rv-023.group-count | 1 | 0 | 0 |
| rv-023.unused-group-types | 1 | 0 | 0 |
| rv-027.type-copy-names | 1 | 0 | 0 |
| rv-028.default-view-names | 2 | 0 | 0 |
| rv-028.views-not-on-sheets | 1 | 0 | 0 |
| rv-029.sheet-views-template | 1 | 0 | 0 |
| rv-030.walls-spanning-levels | 1 | 0 | 0 |
| rv-031.offset-from-level | 1 | 0 | 0 |
| rv-034.walls-attached | 1 | 0 | 0 |
| rv-035.mass-instances | 1 | 0 | 0 |
| rv-037.rooms | 3 | 0 | 0 |
| rv-038.duplicate-spaces | 1 | 0 | 0 |
| rv-042.unused-families | 1 | 0 | 0 |
| rv-045.dim-precision | 1 | 0 | 0 |
| rv-046.annotation-on-sheets | 1 | 0 | 0 |
| rv-047.entourage-in-documents | 1 | 0 | 0 |
| rv-048.images | 1 | 0 | 0 |
| rv-048.plan-regions | 1 | 0 | 0 |
| rv-051.ifc-view-fine | 1 | 0 | 0 |
| rv-052.generic-ifc-class | 2 | 0 | 0 |
| rv-053.warning-gate | 1 | 1 | n/a |
| every `manual_review` check (22) | 1 each | 1 each | 1 each |

40 automatic checks. Asserted automatic findings: R1 41 (39 checks asserted), RW 5 (40), RF 1 (38).
