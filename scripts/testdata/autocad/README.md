# AutoCAD planted-defect test: job files

These jobs carry out `../autocad-planted-defects.md`. Run them in AutoCAD on Windows with the engine loaded, from
the `scripts/testdata` folder in PowerShell, with `sa` on the PATH. Every file passes `sa job lint`. The script
jobs (`*-script.json`) each carry the one warning lint always gives `script.lisp`.

## Before you start

- Test folder `C:\SA-Test\autocad\` with an empty subfolder `spare\` and `img.png` (any 64 x 64 PNG). The jobs name
  this folder in `document.path`. Another folder also works: the stamp step writes in the live path, as long as
  the file names match.
- Pick a run tag, for example `$tag = '20261128'`. Job ids are unique forever once committed, so a repeat run
  needs a new tag.
- Run every job in three steps: stamp, rehearse, commit. Stamping writes the live drawing path and the run tag into
  a copy under `.\run\`, then lints the copy:

```powershell
.\stamp-job.ps1 -App acad -Job .\autocad\<file>.json -RunTag $tag
sa acad run .\run\<file>.json --rehearse
sa acad run .\run\<file>.json
```

  For a script job, add `--allow-script` to both `run` lines. A rehearsal does not evaluate the LISP, and the
  commit must follow an identical rehearsal. The commit's readback value lists every planted item with `ok` or
  `FAILED: <message>`. Read it, and stop if anything failed.
- The engine never saves. Save (QSAVE) where this README says so.
- Planting jobs carry no `verify`: a planted defect fails its check, and a failing check of severity error or
  critical would roll the job back. Prove the defects with `sa acad audit` instead. Fix jobs verify the checks
  they fix. A rehearsal or commit that ends without `rolled_back` (rehearsal) or `committed` (commit) exits
  non-zero.
- `sa acad audit` uses the bundled `us-ncs-style.json` for this inch drawing. It has no flag to choose a standard.

## Run order

| Step | Drawing | Job or action | Expected |
|---|---|---|---|
| 1 | XB.dwg | New drawing from `acad.dwt`, save as `XB.dwg`. Run `01-xb-setup`, `02-xb-line-script` (script). Save. | one LINE (2000,25)-(2100,25) on A-WALL |
| 2 | XD.dwg | Same with `03-xd-setup`, `04-xd-line-script`. Save. | LINE at y = 50 |
| 3 | XC.dwg | Same with `05-xc-setup`, `06-xc-line-script`. Save. | LINE at y = 75 |
| 4 | XA.dwg | New drawing, save as `XA.dwg`. Run `07-xa-setup-xref` (attaches XB, relative), `08-xa-line-script`. Save. | LINE at y = 0, XB attached |
| 5 | B.dwg | New drawing from `acad.dwt`, save as `B.dwg`. Run `10-b-setup`, `11-b-model`, `12-b-layout`, `13-b-lines-paper-script` (script), `14-b-purge-verify`. Save. | 14 verifies all 44 automatic checks: it must commit with every check passing |
| 6 | B.dwg | `sa acad audit --out B-audit.json` | 0 findings on all 44 automatic checks; 1 finding on each of the 14 `manual_review` checks. Keep the report with the test. |
| 7 | | Close B. In Explorer copy `B.dwg` to `P1.dwg`, `D2.dwg` and `D3.dwg`. Open `P1.dwg`. | |
| 8 | P1.dwg | Run `20-p1-prep`, `21-p1-plant-ops`, `22-p1-plant-xrefs`, `23-p1-plant-script` (script), `24-p1-plant-block-insert`. Save. | 23's readback lists D01-D05, D08-D11, D14, D16, D24-D30, D32-D34 as `ok` |
| 9 | P1.dwg | **Manual (D22):** move `XC.dwg` from the test folder into `spare\`. Close P1 and open it again, so XC is "not found". | XREF palette shows XC as Not Found |
| 10 | P1.dwg | Run `25-p1-plant-purge-items` (D23, last). Keep `A-101` current. Save. | |
| 11 | P1.dwg | `sa acad audit --out P1-audit.json` | exactly the P1 column of the spec's section 5: 42 findings over 38 automatic checks, 0 on the other 6 |
| 12 | P1.dwg | Run `30-p1-fix-props`. | verify passes: ac-001, ac-002, ac-003 (3), ac-004 (2), ac-005, ac-013, ac-016, ac-027, ac-030, ac-042 |
| 13 | P1.dwg | Run `31-p1-fix-xref-path`. | verify passes: ac-017.xref-full-path |
| 14 | P1.dwg | Run `32-p1-fix-xref-missing`, then `sa acad audit --checks ac-017.xref-unresolved` | XC found in `spare\`; the audit shows 0 findings |
| 15 | P1.dwg | Run `33-p1-fix-purge`. Save. | verify passes: ac-020.purge |
| 16 | P1.dwg | `sa acad audit --out P1-fixed-audit.json` | the 24 findings in "P1 after the fixes" below; 0 on every other automatic check |
| 17 | D2.dwg | Open `D2.dwg`. Run `40-d2-lineweights`. Save. `sa acad audit --out D2-audit.json` | ac-048.lineweight-hierarchy 1, every other automatic check 0 |
| 18 | D3.dwg | Open `D3.dwg`. Run `50-d3-layouts`, then `51-d3-script` (script; it sets INSUNITS 0, so run it last). | 20 layouts, PSLTSCALE 0, MSLTSCALE 0, INSUNITS 0 |
| 19 | D3.dwg | **Manual:** click the Model tab (TILEMODE 1). Save. `sa acad audit --out D3-audit.json` | ac-012.insunits 1, ac-039.layout-count 1, ac-043.saved-in-layout 1, ac-046.msltscale 1, ac-046.psltscale 1; every other automatic check 0 |
| 20 | D3.dwg | Run `52-d3-fix`. Save. `sa acad audit --checks ac-012.insunits,ac-046.msltscale,ac-046.psltscale,ac-039.layout-count,ac-043.saved-in-layout` | verify passes: ac-012, ac-046 (2). The audit still shows ac-039 1 and ac-043 1, because both have only manual fixes |

## Which job plants which defect

| Defect | Check | Planted by | Fixed by |
|---|---|---|---|
| D01 | ac-001.layer-zero (2) | 23 script | 30 `entity.setprops` (filter: LINE on layer 0) |
| D02 | ac-002.defpoints | 23 script | 30 `entity.setprops` |
| D03-D05 | ac-003 colour, linetype, lineweight | 23 script | 30 `fix.bylayer` |
| D06 | ac-004.dims-on-dim-layer | 21 `dim.linear` | 30 `entity.setprops` |
| D07 | ac-004.text-on-wall-layer | 21 `text.add` | 30 `entity.setprops` |
| D08, D09 | ac-005.layer-names (2), ac-042.layers-by-weight | 21 `layer.ensure` + 23 script (lines) | 30 `layer.translate` |
| D10 | ac-035.plot-colours | 21 `layer.ensure` + 23 script (line) | manual fix only |
| D11 | ac-006.block-properties | 23 script (block) + 24 `block.insert` | manual fix only |
| D12 | ac-009.dim-override | 21 `dim.linear` x 3 | manual fix only |
| D13 | ac-011.dim-precision, ac-025.dim-styles-listed | 21 `dimstyle.ensure` + `dim.linear` | manual fix only |
| D14 | ac-013.annotative-styles | 21 `textstyle.ensure` + 23 script (paper text) | 30 `textstyle.ensure` |
| D15 | ac-014.text-height (2) | 21 `text.add` x 2 | manual fix only |
| D16 | ac-025.text-styles-listed | 21 `textstyle.ensure` + 23 script (paper text) | manual fix only |
| D17 | ac-030.fonts | 21 `textstyle.ensure` | 30 `textstyle.ensure` |
| D18 | ac-015.viewport-locked | 21 `viewport.create` | manual fix only |
| D19 | ac-036.viewport-scale | 21 `viewport.create` | manual fix only |
| D20 | ac-018.attach-nesting, ac-038.xref-depth | 22 `xref.attach` | manual fix only |
| D21 | ac-016.xref-layer, ac-017.xref-full-path | 22 `xref.attach` | 30 `entity.setprops`, 31 `xref.repath` |
| D22 | ac-017.xref-unresolved | 22 `xref.attach` + manual step 9 | 32 `xref.repath` |
| D23 | ac-020.purge | 25 | 33 `fix.purge` |
| D24-D30 | ac-022 (2), ac-023, ac-027, ac-028, ac-031, ac-033, ac-040 | 23 script | ac-027: 30 `fix.flattenZ`; others manual only |
| D31 | ac-045.real-size-hatch-scale | 21 `room.create` with hatch | manual fix only |
| D32 | ac-032.titleblock-loose-text | 23 script | manual fix only |
| D33 | ac-034.dimlfac | 23 script | manual fix only |
| D34 | ac-037.image-frames | 23 script | manual fix only |
| D35 | ac-044.empty-layouts | 21 `layout.create` | manual fix only |
| D2 | ac-048.lineweight-hierarchy | 40 `layer.ensure` | manual fix only |
| D3 | ac-012, ac-039, ac-043, ac-046 (2) | 50 (layouts, linetype scaling), 51 script (paper titles, INSUNITS 0), manual step 19 (Model tab) | 52 `fix.units` + `doc.setup` (ac-012, ac-046) |

Script jobs plant what no op can make: plain LINEs and CIRCLEs with property overrides, open polylines, a SPLINE, a
non-associative HATCH, paper-space TEXT (`text.add` writes to model space only), a block with a coloured line on a
named layer (`block.define` puts layer-0 content ByBlock), a per-dimension DIMLFAC, a raster image, and INSUNITS 0
(the `fix.units` and `doc.setup` enums exclude unitless).

## P1 after the fixes (step 16)

ac-006.block-properties 1, ac-009.dim-override 1, ac-011.dim-precision 1, ac-014.text-height 2,
ac-015.viewport-locked 1, ac-018.attach-nesting 1, ac-022.duplicate-lines 2, ac-023.hatch-associative 1,
ac-025.dim-styles-listed 1, ac-025.text-styles-listed 1, ac-028.area-polylines-closed 1, ac-031.polyline-width 1,
ac-032.titleblock-loose-text 1, ac-033.off-axis 1, ac-034.dimlfac 1, ac-035.plot-colours 1, ac-036.viewport-scale 1,
ac-037.image-frames 1, ac-038.xref-depth 1, ac-040.splines 1, ac-044.empty-layouts 1, ac-045.real-size-hatch-scale 1.
Total 24 (42 planted minus the 18 fixed by ops).

## Notes

- `33-p1-fix-purge` names no standard, so it also removes the unused standard layer `A-ROOF` from D23. With a
  standard the layer would be kept and not counted: the purgeable count leaves out the names the standard lists,
  exactly as `fix.purge` does. Either way `ac-020.purge` passes afterwards.
- `32-p1-fix-xref-missing` has no `verify`. `xref.repath` reloads only on commit, so a rehearsal would still see XC
  as unresolved and the verify would fail the rehearsal.
- `13-b-lines-paper-script` sets the TB-ARCHD attribute definitions and the A-101 title block attributes to style
  NOTES. `block.define` gives attribute definitions the current style (Standard), which
  `ac-025.text-styles-listed` would flag in B. It also deletes `Layout1` and `Layout2`, because no op deletes a
  layout.
- If `10-b-setup` or `40-d2-lineweights` is refused on a layer that is not in the standard (layer `0` in D2), set
  that layer by hand and record it.
- If step 6 shows any finding, fix B and run it again before planting. Do not change the expected counts.
