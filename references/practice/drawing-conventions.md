# Drawing conventions

Read this before you draw, annotate or judge any 2D output: plans, sections, elevations, details, sheets. Source tags like [S10] point to `_SOURCES.md`. The project or office standard always overrides what is here; this file is the default when the standard is silent.

Contents: 1 Principles · 2 Line weights · 3 Line types · 4 Text heights · 5 Symbols · 6 Hatches · 7 Model space vs paper space · 8 Quick checks

## 1. Principles

- Draw everything at full size (1:1) in model space. Scale happens only at the viewport or view. Why: dimensions measure real geometry, and the same model serves every scale [S10][S14].
- Decide the plotted result first (sheet size, scale, plotted text height), then derive model-space sizes from it. A drawing is judged on paper, not on screen.
- Graphic hierarchy carries meaning: what is cut is heaviest, what is seen in projection is medium, what is beyond, hidden or textural is lightest. A reader should be able to tell cut from seen with no colour.
- Keep every property ByLayer (AutoCAD) or by object style (Revit) unless there is a reason. Why: consultants fade an xref'd plan by overriding its layers; hard-coded colours and weights defeat that [S48].

## 2. Line weights

### 2.1 The series

ISO 128-20 sets line widths from the series **0.13, 0.18, 0.25, 0.35, 0.5, 0.7, 1.0, 1.4, 2.0 mm** (ratio about 1:√2), with extra-wide : wide : narrow in the ratio **4:2:1** [N05]. Use the series values; do not invent 0.3 or 0.45.

US public example (Missouri FMDC, colour to plotted width, the only exact public US table found) [S09]:

| Colour | 1 Red | 2 Yellow | 3 Green | 4 Cyan | 5 Blue | 6 Magenta | 7 Black/White | 8 Gray |
|---|---|---|---|---|---|---|---|---|
| Width (in) | .008 | .006 | .004 | .002 | .010 | .016 | .012 | .020 |

Illinois State examples: columns 0.012", area identifiers 0.010", area lines 0.006", doors, ceilings and elevations 0.002" [S10]. The pattern: cut structure heavy, background light.

### 2.2 What gets which weight (typical practice, confirm with the project standard)

| Role | Plan 1:100 / 1/8" | Plan 1:50 / 1/4" | Detail 1:5-1:10 / 1½"-3" |
|---|---|---|---|
| Cut structure and walls (outline of cut) | 0.5 | 0.7 | 0.7-1.0 |
| Cut secondary (partitions, cut window frames) | 0.35 | 0.5 | 0.5 |
| Projection: elements seen below the cut (sills, worktops, stair treads, doors leaves) | 0.25 | 0.25-0.35 | 0.35 |
| Beyond, overhead, hidden | 0.18 dashed | 0.18-0.25 dashed | 0.25 dashed |
| Hatch, poché pattern, furniture, fixtures | 0.13-0.18 | 0.18 | 0.18 |
| Dimensions, leaders, grid lines | 0.18 | 0.18 | 0.18 |
| Text | about h/10 (2.5 mm text → 0.25) [S80, unverified] | same | same |
| Site/building outline in site plans; elevation ground line | 0.7-1.0 | n/a | n/a |

Decision rules:
- Thicker lines read as nearer and more important. If a projected line looks heavier than a cut line, the hierarchy is wrong.
- At small scales (1:200, 1/16") drop one step on every row or the plan clogs.
- In elevations, use profile weight for the building outline and ground line, medium for major planes stepping forward, light for surface detail.
- In Revit, set weights in Object Styles (by category, cut and projection separately) and the Line Weights table (pen number to width per view scale). Do not override per element to fix a weight problem.

## 3. Line types

| Line | Meaning | Typical use |
|---|---|---|
| Continuous | Visible edge | Everything visible |
| Dashed (hidden) | Hidden or below | Hidden edges, objects below the cut that should still read |
| Long dash short dash (overhead) | Above the cut plane | Bulkheads, upper cabinets, skylights, roof edges over |
| Chain (centre) | Centre or axis | Centrelines, grid lines (often chain with bubbles), symmetry |
| Long dash double short dash (phantom) | Alternative position, property line | Boundaries, future work, moved items |
| Break line (zigzag) | Drawing continues | Cut-off of a long element, stair break |

Rules: linetype scale must make dashes read at the plotted scale. AutoCAD: see `cad-standards.md` section 6. Revit: line patterns scale with the view automatically; never draw dashes as separate short lines.

## 4. Text heights

### 4.1 Plotted heights

| Use | Metric (ISO 3098 series; DfE practice) [S80][S14] | Imperial (US owner practice) [S10][S22] |
|---|---|---|
| General notes, dimensions, tags | 2.5 mm (1.8 mm allowed for dense notes) | 3/32" |
| Room names, sub-headings | 3.5 mm | 1/8" |
| View titles | 5 mm | 3/16"-5/16" (5/16" on ARCH C/D/E at ISU) |
| Sheet number in title block | 7 mm or larger | 3/8" (C), 1/2" (D), 1" (E) at ISU |

ISO 3098 heights are 1.8, 2.5, 3.5, 5, 7, 10, 14, 20 mm [S80, unverified first-hand]. DfE requires text to stay legible when printed reduced to A3 [S14]: check 1.8 mm text on A1 sheets (it becomes about 1.3 mm at A3, which is too small).

### 4.2 Model-space text height for non-annotative text

Model height = plotted height × scale factor. In Revit and with AutoCAD annotative objects this is automatic; you only need the table to check or to draw non-annotative text.

Metric (1 unit = 1 mm), heights in mm:

| Scale | 1.8 | 2.5 | 3.5 | 5 | 7 |
|---|---|---|---|---|---|
| 1:5 | 9 | 12.5 | 17.5 | 25 | 35 |
| 1:10 | 18 | 25 | 35 | 50 | 70 |
| 1:20 | 36 | 50 | 70 | 100 | 140 |
| 1:50 | 90 | 125 | 175 | 250 | 350 |
| 1:100 | 180 | 250 | 350 | 500 | 700 |
| 1:200 | 360 | 500 | 700 | 1000 | 1400 |
| 1:500 | 900 | 1250 | 1750 | 2500 | 3500 |

Imperial (1 unit = 1 inch), from Illinois State's scaling chart [S10]:

| Scale | Factor | 3/32" plotted | 1/8" plotted |
|---|---|---|---|
| 3" = 1'-0" | 4 | 0.375" | 0.5" |
| 1½" = 1'-0" | 8 | 0.75" | 1" |
| 1" = 1'-0" | 12 | 1.125" | 1.5" |
| ¾" = 1'-0" | 16 | 1.5" | 2" |
| ½" = 1'-0" | 24 | 2.25" | 3" |
| ¼" = 1'-0" | 48 | 4.5" | 6" |
| 3/16" = 1'-0" | 64 | 6" | 8" |
| 1/8" = 1'-0" | 96 | 9" | 12" |
| 1/16" = 1'-0" | 192 | 18" | 24" |
| 1" = 20' | 240 | 22.5" | 30" |
| 1" = 50' | 600 | 56.25" | 75" |
| 1" = 100' | 1200 | 112.5" | 150" |

Engineering scales 1" = n' have factor n × 12 [S11].

### 4.3 Fonts

Use the project's font. Public examples: Arial (DfE, NY OGS) [S14][S22]. Prefer TrueType over SHX for searchable PDFs [S33][S41]. One font family per set; vary height and weight, not typeface.

## 5. Symbols

Plotted symbol sizes below are typical practice; take the office's annotation families or blocks when they exist.

| Symbol | Content | Placement rules |
|---|---|---|
| Section mark | Bubble split horizontally: drawing number on top, sheet number below; arrow or filled triangle shows view direction; cut line through the plan | Cut line runs fully through what it cuts; the arrow points the way the viewer looks. Bubble about 10-12 mm / 3/8"-1/2" plotted. Every mark must resolve to a drawing that exists. |
| Elevation mark | Bubble (often with 1-4 pointers) giving drawing/sheet references for each direction | Place outside the building for exterior elevations; inside the room for interior elevations, one pointer per wall drawn. |
| Detail callout | Boundary (circle, rounded rectangle) around the area enlarged, with a reference bubble on a leader | The boundary encloses exactly what the detail shows. |
| North arrow | Arrow with "N"; project north and true north if they differ | One per plan sheet or per plan view; same orientation on all plans. NY OGS puts it in the title block with on/off parameters [S22]. |
| Graphic (bar) scale | Bar with divisions in drawing units | Put one on any drawing that may be reproduced at another size. A bar scale stays true when a sheet is printed reduced; a written ratio does not. |
| Level / elevation datum | Target or triangle with level name and height (e.g. "Level 02  +3.600" or "LEVEL 2  EL. 112'-0"") | On sections and elevations at each floor; say whether the value is FFL or SSL (see `floors-ceilings-roofs.md`). |
| Spot level (plan) | Cross or target with value | For falls, thresholds, landscape. |
| Grid bubble | Circle with grid label at one or both ends of the grid line | Numbers in one direction, letters in the other. Skip I and O (they read as 1 and 0); typical practice. Bubbles outside the building, aligned in a row. |
| Revision cloud and tag | Cloud around the change, triangle or hexagon with revision number | See `sheets-sets.md`. |
| Door, window, room, wall-type tags | See `openings.md`, `rooms-areas.md`, `schedules-tags.md` | |
| Match line | Heavy chain line labelled "MATCH LINE - SEE n/Axxx" | Where a plan is split across sheets; use Revit dependent views and scope boxes [S22]. |
| Break line | Zigzag | Long elements cut short; stairs at the cut plane. |

## 6. Hatches and material poché

Hatch conventions are national and office-specific. Use the table as a default and replace it with the project's legend.

| Material | In section (cut) | In elevation / surface | AutoCAD shipped pattern often used (typical) |
|---|---|---|---|
| Concrete (in situ) | Stipple with aggregate triangles | Light stipple or none | AR-CONC |
| Masonry (brick) | Diagonal hatch at 45° | Brick coursing | ANSI31 (section), AR-BRSTD (elevation) |
| Concrete block | Diagonal cross-hatch | Block coursing | ANSI37 / AR-B816 |
| Insulation (batt) | Wavy batt symbol | n/a | INSUL |
| Rigid insulation | Cross-hatch or dots, light | n/a | project pattern |
| Timber (sawn, cut) | Cross grain (X in a box) for dimension lumber; end grain rings for large sections | Grain lines | project pattern |
| Plywood / board | Layered lines | n/a | project pattern |
| Steel | Solid fill (small scale) or tight 45° | n/a | SOLID / ANSI32 |
| Earth | Earth hatch | n/a | EARTH |
| Gravel / hardcore | Pebbles | n/a | GRAVEL |
| Sand, plaster, render | Fine stipple | Fine stipple | AR-SAND |
| Glass | Thin lines, no hatch | Diagonal reflection strokes (optional) | none |
| Gypsum board | Light stipple or none at small scale | none | AR-SAND or none |

Rules:
- Wall poché in plans: at 1:100 / 1/8" use solid or dark-grey fill for cut walls; at 1:50 / 1/4" and larger show the build-up. Why: at small scale the build-up is unreadable; at large scale it is the information.
- Scaled patterns (ANSI31 and similar) take the drawing scale factor. Real-size patterns (AR-BRSTD, AR-B816) are drawn in real units and are never scaled [S10].
- Hatches must be associative and on a hatch layer, never exploded [S32].
- In Revit, poché comes from the material's cut pattern and the wall type's coarse-scale fill; do not draw filled regions over walls to fake it [S47].

## 7. What belongs in model space and paper space (AutoCAD)

| Model space | Paper space (layouts) |
|---|---|
| All building geometry at 1:1 | Title block and border (attributed block, in the layout) |
| Hatches | Viewports (locked after scaling) [S32][S33] |
| Dimensions (ISU practice) or annotative dimensions | Sheet-wide general notes, key plan, legends (project setting) |
| Room tags, door/window tags (annotative) | Revision block |
| Xrefs | Drawing titles and scale labels (often) |

Practice is split on notes: some offices keep all notes in model space as annotative objects, others put them in paper space [S10][S32]. Make it a project setting; ask if the drawing gives no clear signal. Never dimension model geometry from paper space unless the standard requires it; if you do, the dimensions must be associative to the model.

In Revit the split is: annotation belongs in views, not on sheets [S22]. Only title block content, revision schedules and sheet-level notes live on the sheet.

## 8. Quick checks before you call a drawing done

1. Cut lines are heavier than projection lines, and projection heavier than hatch.
2. Every text item is a standard plotted height for its use.
3. Every section, elevation and detail mark points to a drawing that exists, with the right number.
4. North arrow present on plans and consistent across sheets.
5. Hatches associative, on the right layer, at the right scale.
6. Nothing plots that should not (construction lines, viewport frames on a no-plot layer, image frames) [S44].
