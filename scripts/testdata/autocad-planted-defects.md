# AutoCAD planted-defect test

Specification of the test. The job files that carry it out, their run order and the expected result per step are in
`autocad/README.md`. Goal: drawings with a known number of defects, so that an
audit with `data/checks/autocad.json` and `data/standards/us-ncs-style.json` must report exactly the planted count
for every automatic check. Counts are **findings** (one finding per record for per-record evaluators; exactly one
finding for `count_over_threshold`, `distinct_count_min` and document-level `field_range` / `field_equals` checks).

## 0. Rules

- Units: inches. Coordinates are model-space inches unless marked paper (paper inches on layout `A-101`).
- Every entity is created with colour, linetype and lineweight ByLayer, at Z = 0, unless the defect says otherwise.
- Every layer, style and block created is used by at least one entity unless the defect says it must be unused,
  because unused named objects are counted by `ac-020.purge`.
- **Baseline first.** Build the baseline B, save it, audit it, and confirm that every automatic check passes (0
  findings). If any check fails on B, fix B (not the expected counts) and re-run before planting. Record the
  baseline audit report with the test.
- Plant defects in the order listed. Several defects are deliberately built so they do not trigger other checks;
  where one defect does count for two checks this is stated and the expected table includes it.
- Checks with evaluator `manual_review` return exactly 1 finding in every drawing; the test asserts that and
  nothing more.
- Files live in one folder: `P1.dwg`, `D2.dwg`, `D3.dwg`, `XA.dwg`, `XB.dwg`, `XC.dwg` (moved into the subfolder
  `spare\` during the test), `XD.dwg`, `img.png`.

## 1. Baseline B

1. New drawing from `acad.dwt`. `doc.setup`: insUnits `in`, lengthFormat `architectural`, measurement `imperial`,
   ltScale 1, psLtScale true, msLtScale true, annotationScales `["1/4\" = 1'-0\""]`, currentAnnotationScale
   `1/4" = 1'-0"`.
2. `layer.ensure` from the standard, only these layers: `A-WALL`, `A-WALL-PATT`, `A-DOOR`, `A-AREA`,
   `A-AREA-IDEN`, `A-ANNO-DIMS`, `A-ANNO-TEXT`, `G-ANNO-TTLB`, `G-ANNO-NPLT`.
3. `textstyle.ensure` NOTES, TAGS, TITLES and `dimstyle.ensure` ARCH-ANNO from the standard.
4. Blocks (`block.define`), all entities on layer `0` with ByBlock colour, linetype and lineweight:
   `DOOR-36` = LINE (0,0)-(36,0) and ARC centre (0,0) radius 36 from 0 to 90 degrees;
   `TB-ARCHD` = closed polyline (0,0) (36,0) (36,24) (0,24) and attribute definitions SHEET_NO, SHEET_TITLE,
   PROJECT_NAME, PROJECT_NO, ISSUE_DATE, SCALE, height 0.125, text style NOTES. (`block.define` gives attribute
   definitions the current text style, Standard, which `ac-025.text-styles-listed` flags; set them to NOTES after
   defining the block.)
5. Model space:
   - LINEs on `A-WALL`: (0,0)-(240,0), (240,0)-(240,180), (240,180)-(0,180), (0,180)-(0,0).
   - Closed LWPOLYLINE on `A-WALL-PATT` (0,0) (240,0) (240,-6) (0,-6), and an associative HATCH ANSI31, scale 48,
     on `A-WALL-PATT` bounded by it.
   - Closed LWPOLYLINE on `A-AREA` (6,6) (234,6) (234,174) (6,174).
   - TEXT "OFFICE 101" on `A-AREA-IDEN`, style TAGS, annotative, paper height 0.125, at (100,90).
   - INSERT `DOOR-36` on `A-DOOR` at (60,0).
   - Rotated DIMENSION on `A-ANNO-DIMS`, style ARCH-ANNO, (0,0)-(240,0), offset -24, no text override.
   - TEXT "NOTE" on `A-ANNO-TEXT`, style NOTES, annotative, paper height 0.09375, at (20,150).
6. Layout `A-101` (create it before deleting `Layout1` and `Layout2`), paper size ARCH D 36 x 24: INSERT `TB-ARCHD`
   on `G-ANNO-TTLB` at paper (0,0) with all attributes filled, attributes in style NOTES; one viewport on `G-ANNO-NPLT`, scale 1/4" = 1'-0", showing model (−12,−36) to
   (252,192), locked; paper TEXT "FLOOR PLAN" on `A-ANNO-TEXT`, style TITLES, paper height 0.1875, at paper (2,2).
   Delete `Layout1` and `Layout2`. Make `A-101` current.
7. `fix.purge` all and regapps, repeat. Save as `B.dwg`. Audit: 0 findings on every automatic check.

## 2. P1: the main defect drawing (copy of B)

Preparation of the reference files (all from `acad.dwt`, INSUNITS 1, layer `A-WALL` from the standard):
`XB.dwg` LINE (2000,25)-(2100,25); `XA.dwg` LINE (2000,0)-(2100,0) and XB attached into XA as **Attach** with
relative path `.\XB.dwg` on layer `A-WALL`; `XD.dwg` LINE (2000,50)-(2100,50); `XC.dwg` LINE (2000,75)-(2100,75).
`img.png`: any 64 x 64 pixel PNG.

Before planting: `layer.ensure` from the standard `A-FURN`, `A-ANNO-NOTE`, `G-ANNO-REFR`; load linetypes
`HIDDEN2` and `PHANTOM2` from `acad.lin`. (`LAYOUTCREATEVIEWPORT` does not matter: `layout.create` builds the
layout itself and never adds a viewport.)

| # | Check(s) | Plant exactly |
|---|---|---|
| D01 | ac-001.layer-zero | 2 LINEs on layer `0`: (1000,0)-(1100,0) and (1000,20)-(1100,20). |
| D02 | ac-002.defpoints | 1 CIRCLE on `Defpoints`, centre (1200,0), radius 10. |
| D03 | ac-003.color-bylayer | 1 LINE on `A-WALL` (1000,40)-(1100,40) with colour 1 (red). |
| D04 | ac-003.linetype-bylayer | 1 LINE on `A-WALL` (1000,60)-(1100,60) with linetype HIDDEN2. |
| D05 | ac-003.lineweight-bylayer | 1 LINE on `A-WALL` (1000,80)-(1100,80) with lineweight 0.50 mm. |
| D06 | ac-004.dims-on-dim-layer | 1 rotated DIMENSION on `A-WALL`, style ARCH-ANNO, (1000,100)-(1100,100), offset 12, no override. |
| D07 | ac-004.text-on-wall-layer | 1 TEXT "WALL NOTE" on `A-WALL`, style NOTES, annotative, paper height 0.09375, at (1000,130). |
| D08 | ac-005.layer-names | New layer `WALLS-NEW` (colour 7, lineweight 0.30) with 1 LINE (1000,150)-(1100,150). |
| D09 | ac-042.layers-by-weight and ac-005.layer-names | New layer `THICK` (colour 7, lineweight 0.50) with 1 LINE (1000,170)-(1100,170). Counts once for each check. |
| D10 | ac-035.plot-colours | `layer.ensure` the standard layer `A-EQPM` but with colour 30 (lineweight 0.20); 1 LINE on it (1000,190)-(1100,190). |
| D11 | ac-006.block-properties | Block `BAD-BLOCK` = 1 LINE (0,0)-(10,0) on layer `A-WALL` with colour 1; insert it once on `A-FURN` at (1200,40). |
| D12 | ac-009.dim-override | 3 rotated DIMENSIONs on `A-ANNO-DIMS`, style ARCH-ANNO, offset 12: (1000,210)-(1100,210) override `10'-0"` (the defect); (1000,240)-(1100,240) override `<> TYP` and (1000,270)-(1100,270) override `EQ` (controls, not findings). |
| D13 | ac-011.dim-precision and ac-025.dim-styles-listed | Dimension style `ARCH-FINE` = copy of ARCH-ANNO with DIMDEC 4 (1/16 in); 1 rotated DIMENSION using it on `A-ANNO-DIMS` (1000,300)-(1100,300), offset 12. Counts once for each check. |
| D14 | ac-013.annotative-styles | Redefine text style TITLES with Annotative off; add 1 paper TEXT "TITLE 2" on `A-ANNO-TEXT`, style TITLES, height 0.1875, at paper (2,4). |
| D15 | ac-014.text-height | 2 TEXTs on `A-ANNO-TEXT`, style NOTES, annotative, paper height 0.1 (not a standard height), at (1000,330) and (1000,350). |
| D16 | ac-025.text-styles-listed | Text style `OLDTEXT` (font Arial, not annotative); 1 paper TEXT on `A-ANNO-TEXT` with it, height 0.09375, at paper (2,6). |
| D17 | ac-030.fonts | Change the font of text style TAGS to `romans.shx` (TAGS stays used by "OFFICE 101"). |
| D18 | ac-015.viewport-locked | Second viewport on `A-101`, layer `G-ANNO-NPLT`, scale 1/4" = 1'-0", showing model (990,−10) to (1210,710), **not locked**. |
| D19 | ac-036.viewport-scale | Third viewport on `A-101`, layer `G-ANNO-NPLT`, custom scale 1/1328.4 (vpScaleRatio 1328.4), locked. |
| D20 | ac-018.attach-nesting, ac-038.xref-depth | Attach `XA.dwg` as **Attach**, relative path, on layer `G-ANNO-REFR`, at (0,0). Its nested XB is depth 2. |
| D21 | ac-016.xref-layer, ac-017.xref-full-path | Overlay `XD.dwg` with a **full** path on layer `A-ANNO-NOTE` at (0,0). Counts once for each check. |
| D22 | ac-017.xref-unresolved | Overlay `XC.dwg`, relative path, on `G-ANNO-REFR` at (0,0); then move `XC.dwg` into the subfolder `spare\` and close and reopen P1, so its status is not found. (Moving instead of deleting keeps a copy for the `xref.repath` fix with `searchFolders`.) |
| D23 | ac-020.purge | Plant this row **last**, after a `fix.purge` of kind `regapps` only (operations above may register applications that end up unused). Then leave unused exactly: layer `A-ROOF` (from the standard, empty), linetype `PHANTOM2` (loaded above, unused), block `SA-UNUSED` (1 LINE on `0`, ByBlock) with no insert. purgeableCount = 2, one finding: `A-ROOF` is a layer the standard lists, so the count leaves it out, as `fix.purge` keeps it. |
| D24 | ac-022.duplicate-lines | 3 LINEs on `A-WALL`: (1000,400)-(1100,400), the same again, and (1100,400)-(1000,400). 2 findings. |
| D25 | ac-023.hatch-associative | 1 non-associative HATCH ANSI31, scale 48, on `A-WALL-PATT`, created from boundary points (1000,420) (1100,420) (1100,440) (1000,440) with no boundary object. |
| D26 | ac-027.flat | 1 LINE on `A-WALL` from (1000,460,0) to (1100,460,10). |
| D27 | ac-028.area-polylines-closed | 1 open LWPOLYLINE on `A-AREA`: (1000,480) (1100,480) (1100,520). |
| D28 | ac-031.polyline-width | 1 open LWPOLYLINE on `A-WALL` (1000,540) (1100,540) with constant width 2. |
| D29 | ac-033.off-axis | 1 LINE on `A-WALL` (1000,560)-(1100,560.5) (0.286 degrees off). |
| D30 | ac-040.splines | 1 SPLINE on `A-WALL` through fit points (1000,580) (1050,600) (1100,580). |
| D31 | ac-045.real-size-hatch-scale | Closed LWPOLYLINE on `A-WALL-PATT` (1000,620) (1100,620) (1100,660) (1000,660) and an associative HATCH AR-BRSTD at scale 2 on `A-WALL-PATT` bounded by it. |
| D32 | ac-032.titleblock-loose-text | 1 paper TEXT "PROJECT X" on `G-ANNO-TTLB`, style NOTES, height 0.1875, at paper (4,2). |
| D33 | ac-034.dimlfac | 1 rotated DIMENSION on `A-ANNO-DIMS`, style ARCH-ANNO, (1000,700)-(1100,700), offset 12, with a per-dimension DIMLFAC override of 2 and no text override. |
| D34 | ac-037.image-frames | Attach `img.png` on `A-ANNO-NOTE` at (1200,100), width 50; set IMAGEFRAME to 1. |
| D35 | ac-044.empty-layouts | New layout `EMPTY` (ARCH D) with no viewport and no content. |

Keep `A-101` current and save as `P1.dwg`.

Things that must not change a count, by construction: all planted LINEs have distinct coordinates (only D24
duplicates); only D29 is off axis and only D26 has Z; the xref inserts, the image and `BAD-BLOCK` are ByLayer; the
nested XB and the xref-dependent layers (`XA|A-WALL` and so on) are left out of the layer checks by
`isXrefDependent` and out of the purge count; D14, D16 and D32 are paper-space text at standard heights; D19 is
locked and D18 is at a standard scale; P1 has 2 layouts.

## 3. D2: lineweight hierarchy (copy of B)

Set the lineweight of every plotting layer, including `0` (used by the block definitions), to 0.25 mm with
`layer.ensure` and update (layer `0` by hand if the engine refuses it). Leave `Defpoints` alone: it never plots, and
`layer.ensure` with update would also reset its plot flag to the default. Save as `D2.dwg`.

## 4. D3: document settings (copy of B)

1. Build 19 more layouts like `A-101` (`A-102` ... `A-120`), each with its title block, its locked viewport at
   1/4" = 1'-0" and its paper text: 20 layouts. (`layout.create` does not clone layout content, so each copy is
   built with `layout.create`, `viewport.create` and a paper text.)
2. Set PSLTSCALE 0 and MSLTSCALE 0.
3. Set INSUNITS to 0 last (unitless; MEASUREMENT stays 0, so the engine assumes inches). In a unitless drawing the
   engine refuses every op except `fix.units` and `doc.setup`, and neither can set INSUNITS 0, so this needs a
   script.
4. Make the Model tab current (TILEMODE 1) and save as `D3.dwg`; audit with Model current.

## 5. Expected results

| Check id | P1 | D2 | D3 |
|---|---|---|---|
| ac-001.layer-zero | 2 | 0 | 0 |
| ac-002.defpoints | 1 | 0 | 0 |
| ac-003.color-bylayer | 1 | 0 | 0 |
| ac-003.linetype-bylayer | 1 | 0 | 0 |
| ac-003.lineweight-bylayer | 1 | 0 | 0 |
| ac-004.dims-on-dim-layer | 1 | 0 | 0 |
| ac-004.text-on-wall-layer | 1 | 0 | 0 |
| ac-005.layer-names | 2 | 0 | 0 |
| ac-006.block-properties | 1 | 0 | 0 |
| ac-009.dim-override | 1 | 0 | 0 |
| ac-011.dim-precision | 1 | 0 | 0 |
| ac-012.insunits | 0 | 0 | 1 |
| ac-013.annotative-styles | 1 | 0 | 0 |
| ac-014.text-height | 2 | 0 | 0 |
| ac-015.viewport-locked | 1 | 0 | 0 |
| ac-016.xref-layer | 1 | 0 | 0 |
| ac-017.xref-full-path | 1 | 0 | 0 |
| ac-017.xref-unresolved | 1 | 0 | 0 |
| ac-018.attach-nesting | 1 | 0 | 0 |
| ac-020.purge | 1 | 0 | 0 |
| ac-022.duplicate-lines | 2 | 0 | 0 |
| ac-023.hatch-associative | 1 | 0 | 0 |
| ac-025.dim-styles-listed | 1 | 0 | 0 |
| ac-025.text-styles-listed | 1 | 0 | 0 |
| ac-027.flat | 1 | 0 | 0 |
| ac-028.area-polylines-closed | 1 | 0 | 0 |
| ac-030.fonts | 1 | 0 | 0 |
| ac-031.polyline-width | 1 | 0 | 0 |
| ac-032.titleblock-loose-text | 1 | 0 | 0 |
| ac-033.off-axis | 1 | 0 | 0 |
| ac-034.dimlfac | 1 | 0 | 0 |
| ac-035.plot-colours | 1 | 0 | 0 |
| ac-036.viewport-scale | 1 | 0 | 0 |
| ac-037.image-frames | 1 | 0 | 0 |
| ac-038.xref-depth | 1 | 0 | 0 |
| ac-039.layout-count | 0 | 0 | 1 |
| ac-040.splines | 1 | 0 | 0 |
| ac-042.layers-by-weight | 1 | 0 | 0 |
| ac-043.saved-in-layout | 0 | 0 | 1 |
| ac-044.empty-layouts | 1 | 0 | 0 |
| ac-045.real-size-hatch-scale | 1 | 0 | 0 |
| ac-046.msltscale | 0 | 0 | 1 |
| ac-046.psltscale | 0 | 0 | 1 |
| ac-048.lineweight-hierarchy | 0 | 1 | 0 |
| every `manual_review` check (14) | 1 each | 1 each | 1 each |

44 automatic checks; total automatic findings: P1 42, D2 1, D3 5.

The purgeable count and `fix.purge` both leave out every name the standard lists, so the unused standard layer
`A-ROOF` (D23) is neither counted nor purged when a standard is in use. The fix job names no standard, so it also
removes `A-ROOF`; either way `ac-020.purge` passes afterwards.
