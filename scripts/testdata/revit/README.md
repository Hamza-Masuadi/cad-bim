# Revit planted-defect test: job files

These jobs carry out `../revit-planted-defects.md`. Run them in Revit on Windows with the engine loaded, from the
`scripts/testdata` folder in PowerShell, with `sa` on the PATH. Every file passes `sa job lint`. Revit has fewer
modelling ops than the spec needs, so many defects are manual steps (listed below).

## Before you start

- Test folder `C:\SA-Test\revit\` holding the files from the spec's section 0: `imp.dwg`, `cad-a.dwg`, `cad-b.dwg`,
  `cad-c.dwg`, `link-a.rvt`, `link-b.rvt`, `link-c.rvt`, `img.png`, `Desk.rfa`, `Chair.rfa`, an entourage family and
  `SA-Generic-Box.rfa`. The jobs name this folder in `document.path` and in `family.load`. If you use another folder,
  edit the `family.load` paths. The stamp step fixes `document.path` for you.
- Pick a run tag, for example `$tag = '20261128'`. Job ids are unique forever once committed.
- A Revit commit needs the live path and fingerprint, and the fingerprint changes after every commit. So stamp each
  job right before you run it:

```powershell
.\stamp-job.ps1 -App revit -Job .\revit\<file>.json -RunTag $tag
sa revit run .\run\<file>.json --rehearse
sa revit run .\run\<file>.json
```

  The fingerprint taken before the rehearsal stays valid for the commit.
- The engine never saves. Save where this README says so.
- Planting jobs carry no `verify`, because a planted defect of severity error or critical would roll the job back.
  Prove the defects with `sa revit audit`. Fix jobs verify what they fix.
- `sa revit audit` uses the bundled `us-ncs-style.json` for this feet-and-inches project, including its workset and
  view patterns.
- Names that come from the default imperial architectural template: wall type `Generic - 8"`, door type
  `Single-Flush : 36" x 84"`, floor type `Generic 12"`, view template `Architectural Plan`. If your release names
  them differently, edit the job before stamping. If Purge Unused in RB removed `Generic 12"`, use the floor type
  that is left (in `21-r1-plant-model`).

## Run order

### RB, the baseline

| Step | Job or action | Expected |
|---|---|---|
| 1 | **Manual:** new project from the default imperial architectural template. Project Units, Length: feet and fractional inches, rounding 1/2". Collaborate > Worksharing (keep `Shared Levels and Grids` and `Workset1`). Rename `Workset1` to `A_Interior`; create `A_FFE`, `X_CAD Link`, `X_Revit Link A`, `X_Revit Link B`; make `A_Interior` active. Save as `RB.rvt` in the test folder. | |
| 2 | `10-rb-datums` | Levels 1-4 at 0', 12', 24', 36'; grids 1, 2, A, B; levels and grids on `Shared Levels and Grids` |
| 3 | `11-rb-model` | 4 walls, room OFFICE 101, door Mark D1, Desk family loaded, plan `L1 - Plan` with template `Architectural Plan` on sheet A-101 |
| 4 | **Manual:** place one desk at (20,15) on workset `A_FFE`, Elevation from Level 0. Pin every level and grid. Check that `Architectural Plan` does not hide Entourage. Optionally give A-101 the template's title block (no check reads it). Delete every view not on a sheet. Purge Unused until nothing is left. Set every dimension type's Units Format to "Use project settings". Review Warnings must be empty. Save. | |
| 5 | `sa revit audit --out RB-audit.json` | 0 findings on all 40 automatic checks; 1 on each of the 22 `manual_review` checks. If not, fix RB and audit again. |
| 6 | **Manual:** for each of R1, RW and RF, open `RB.rvt` with Detach from Central > Preserve Worksets, then Save As `R1.rvt` (`RW.rvt`, `RF.rvt`) in the test folder as a central model. | |

### R1, the main defect model (open `R1.rvt`)

| Step | Job or action | Expected |
|---|---|---|
| 7 | `20-r1-plant-views` | P18 views `L1 - Plan Copy 1` and `Section 1`, P06, P17, P19 (`L2 - Plan` on A-102, no template), P26, P27, P29; `SA-Generic-Box` loaded. If `L2 - Plan` got its view type's default template, remove the template by hand. If `IFC Export` is at Fine detail, set it to Medium. |
| 8 | `21-r1-plant-model` | P03 (second door, Mark D1, Comments P03), P04 (room 101 "P04 ROOM"), P20, the P22 floor and wall |
| 9 | `22-r1-plant-rooms` | P24 (b) one not-enclosed room, (c) one redundant room |
| 10 | **Manual**, in the spec's order: P01, P02 (place the box), P05, P07-P13, P14 (second desk; also set its Comments to `P14`), P15, P16, P21, P22 (Attach Top of the 10' wall to the floor), P23, P24 (a) (one unplaced room), P25, P28, P30, P31, P32. Save. | |
| 11 | `sa revit audit --out R1-audit.json` | the R1 column of the spec's section 5: 41 asserted findings; `rv-003.warning-types` not asserted |
| 12 | `30-r1-fix-by-filter` | verify passes: rv-005.duplicate-mark, rv-005.duplicate-room-number, rv-020.ffe-workset, rv-028.default-view-names, rv-029.sheet-views-template |
| 13 | Collect ids for 31: `sa revit audit --checks rv-014.cad-link-workset,rv-015.revit-link-own-workset,rv-037.rooms --out ids-audit.json` and `sa revit snapshot --sections views --out ids-views.json` | Each finding's `recordId` is the id: the cad-c.dwg link (rv-014), the second link on `X_Revit Link B` (rv-015), and three rooms (rv-037; their order does not matter). From the views, take the ids of `L1 - Plan Working`, `IFC Export` and `OFFICE SECTION`. |
| 14 | Stamp 31 with the ids, then rehearse and commit as usual: `.\stamp-job.ps1 -App revit -Job .\revit\31-r1-fix-by-id.json -RunTag $tag -Set @{ CAD_C_LINK_ID='…'; LINK_DUP_ID='…'; ROOM_UNPLACED_ID='…'; ROOM_NOT_ENCLOSED_ID='…'; ROOM_REDUNDANT_ID='…'; VIEW_SECTION_ID='…'; VIEW_PLAN_WORKING_ID='…'; VIEW_IFC_EXPORT_ID='…' }` | verify passes: rv-014.cad-link-workset, rv-015.revit-link-own-workset, rv-028.views-not-on-sheets, rv-037.rooms |
| 15 | Save. `sa revit audit --out R1-fixed-audit.json` | the 28 findings in "R1 after the fixes" below |

### RW (open `RW.rvt`)

| Step | Job or action | Expected |
|---|---|---|
| 16 | `40-rw-plant` | The job report lists the two Revit warnings it recorded. |
| 17 | Review Warnings, then save. `sa revit audit --out RW-audit.json` | exactly two warnings; rv-003.warning-types 2, rv-004.separation-overlap 1, rv-006.off-axis 1, rv-053.warning-gate 1, every other automatic check 0. If Review Warnings shows no off-axis warning, lower the angle in `40-rw-plant` (on a fresh RW) and record it. If neither warning persists after the engine commit (the engine dismisses warnings during its transaction), draw W1 and W2 by hand and record that. |

### RF (open `RF.rvt`)

| Step | Job or action | Expected |
|---|---|---|
| 18 | `50-rf-far`. Save. `sa revit audit --out RF-audit.json` | rv-016.far-from-origin 1, every other asserted check 0 |
| 19 | Only if Revit refused 50: on a fresh RF copy, copy `revit\rf-1km-check.json` into the test folder and run `51-rf-fallback-1km`. Save. `sa revit audit --out RF-audit.json` | 51's report shows `rv-016.far-from-origin-1km` failing (severity warning, so the job still commits). The audit uses the bundled checks, so rv-016.far-from-origin is 0 there. Record that the fallback was used. |

## Which job plants which defect

| Defect | Check | Planted by | Fixed by |
|---|---|---|---|
| P01 | rv-001, rv-002, rv-052 | manual (no op makes in-place families) | manual |
| P02 | rv-052 | 20 `family.load` + manual placement (no op places family instances) | manual |
| P03 | rv-005.duplicate-mark | 21 `wall.create` + `door.place` | 30 `param.set` |
| P04 | rv-005.duplicate-room-number | 21 `wall.create` + `room.place` | 30 `param.set` |
| P05 | rv-007.hidden-elements | manual (no op hides elements in a view) | manual |
| P06 | rv-009.typed-tags | 20 `text.note` | manual |
| P07 | rv-012.cad-imported | manual (no op imports or links CAD) | manual |
| P08, P09 | rv-014.cad-link-pinned, rv-014.cad-link-path | manual | manual |
| P10 | rv-014.cad-link-workset | manual | 31 `workset.assign` |
| P11 | rv-015.revit-link-path | manual (no op links Revit models) | manual |
| P12 | rv-015.revit-link-own-workset | manual | 31 `workset.assign` (creates `X_Revit Link C`) |
| P13 | rv-017.datums-pinned | manual (no op pins or unpins) | manual |
| P14 | rv-020.ffe-workset | manual placement, Comments `P14` | 30 `workset.assign` (filter on Comments) |
| P15 | rv-022.design-options | manual (no op for design options) | manual |
| P16 | rv-023 (2) | manual (no op for groups) | manual |
| P17 | rv-027.type-copy-names | 20 `type.duplicate` | manual |
| P18 | rv-028.default-view-names (2) | 20 `view.plan.create`, `view.section.create` | 30 `view.rename` |
| P19 | rv-029.sheet-views-template | 20 `view.plan.create`, `sheet.create`, `viewport.place` | 30 `viewtemplate.apply` |
| P20 | rv-030.walls-spanning-levels | 21 `wall.create` | manual |
| P21 | rv-031.offset-from-level | manual (instance placement) | manual |
| P22 | rv-034.walls-attached | 21 `floor.create` + `wall.create`, manual Attach Top | manual |
| P23 | rv-035.mass-instances | manual (no op for masses) | manual |
| P24 | rv-037.rooms (3) | (a) manual (no op makes an unplaced room); (b), (c) 22 | 31 `cleanup.unplacedRooms` |
| P25 | rv-038.duplicate-spaces | manual (no op for spaces) | manual |
| P26 | rv-051.ifc-view-fine | 20 `view.3d.create` | manual |
| P27 | rv-042.unused-families | 20 `family.load` | manual |
| P28 | rv-045.dim-precision | manual (no op sets a dimension type's units format) | manual |
| P29 | rv-046.annotation-on-sheets | 20 `text.note` on `sheet:A-101` | manual |
| P30 | rv-047.entourage-in-documents | manual (instance placement) | manual |
| P31 | rv-048.images | manual (no op inserts images) | manual |
| P32 | rv-048.plan-regions | manual (no op for plan regions) | manual |
| — | rv-028.views-not-on-sheets (1, from P18 and P26) | 20 | 31 `element.delete` (the section, which `cleanup.unusedViews` skips because its marker shows on a sheeted view) + `cleanup.unusedViews` |
| W1 | rv-004.separation-overlap | 40 `room.separator` | manual |
| W2 | rv-006.off-axis | 40 `wall.create` | manual |
| F1 | rv-016.far-from-origin | 50 `wall.create` (or 51) | manual |

## R1 after the fixes (step 15)

rv-001.in-place-count 1, rv-002.in-place-generic 1, rv-007.hidden-elements 1, rv-009.typed-tags 1,
rv-012.cad-imported 1, rv-014.cad-link-path 1, rv-014.cad-link-pinned 1, rv-015.revit-link-path 1,
rv-017.datums-pinned 2, rv-022.design-options 1, rv-023.group-count 1, rv-023.unused-group-types 1,
rv-027.type-copy-names 1, rv-030.walls-spanning-levels 1, rv-031.offset-from-level 1, rv-034.walls-attached 1,
rv-035.mass-instances 1, rv-038.duplicate-spaces 1, rv-042.unused-families 1, rv-045.dim-precision 1,
rv-046.annotation-on-sheets 1, rv-047.entourage-in-documents 1, rv-048.images 1, rv-048.plan-regions 1,
rv-052.generic-ifc-class 2, rv-053.warning-gate 1 (at least one warning). Total 28.

The 13 that drop to 0 are the 12 findings fixed by ops, plus rv-051.ifc-view-fine. `IFC Export` is one of the views
31 deletes, so rv-051 has nothing left to read.

## Notes

- `11-rb-model` creates sheets without a title block. The template's title block name differs between releases,
  and no automatic check reads it.
- `51-rf-fallback-1km` resolves `rf-1km-check.json` against the model's folder, which is why the file must be
  copied there. The job adds the check (`checksMode: add`); the bundled checks are unchanged.
