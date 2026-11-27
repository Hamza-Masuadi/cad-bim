# Landscape site and planting plans

Read this before drawing 2D landscape plans in AutoCAD: hardscape layouts, planting plans, planting schedules, site furniture and setting-out. Grading, contours, levels design, earthworks, drainage and irrigation design are out of scope (they need surface modelling and engineering tools), and so is Revit site modelling. No first-hand public source was found for landscape drawing conventions; everything here is typical practice and the office standard governs. Tree protection, planting density and species choices are the landscape architect's.

Contents: 1 Outputs · 2 Base and layers · 3 Hardscape · 4 Planting plans · 5 Planting schedules · 6 Site furniture and lighting · 7 Setting out · 8 Checks

## 1. Outputs

| Drawing | Shows |
|---|---|
| Site base | Survey, boundaries, existing trees, buildings, levels as given (not designed) |
| General arrangement | Hard and soft areas, structures, key dimensions |
| Hardscape plan | Paving types, kerbs, edges, steps and walls, with codes and areas |
| Planting plan | Trees and shrubs as symbols with codes, planting beds with mixes and densities |
| Planting schedule | Species, size at planting, quantity, spacing or density |
| Setting-out plan | Coordinates or dimensions for key points |
| Site furniture plan | Benches, bins, bollards, cycle stands, signage, with codes |

## 2. Base and layers

- Reference the survey as an xref; do not edit it. Keep survey coordinates; do not move the drawing to the origin (`cad-standards.md`).
- Use the landscape discipline of the layer standard (`L-` in NCS-style formats [S05], or the project's ISO 13567 codes), with separate layers for hardscape, planting, trees, existing trees to retain or remove, site furniture and text.
- Work in metres or millimetres as the survey does; never rescale a survey drawing to change units.

## 3. Hardscape

- Draw each surface type as a closed boundary on its layer, hatched with its pattern and tagged with its code; areas come from the boundary.
- Edges, kerbs, channels and walls as lines or polylines with their own codes.
- Steps and ramps outlines only; gradients and levels come from the engineer or the levels design (ramps and steps in `stairs-ramps.md` apply to their geometry).
- Accessible routes across the site follow the jurisdiction's route rules (`design-data.md` routes and ramps files).

## 4. Planting plans

- Trees and shrubs as blocks: one block per species or code, scaled to the canopy spread at the stated stage (planting or maturity; say which), with an attribute for the code. The insertion point is the stem position.
- Existing trees: retain or remove, shown differently, with the survey tag.
- Planting beds: closed boundaries with a hatch, tagged with the mix code and the density (plants per m²) or spacing.
- Numbers of plants in beds = bed area x density, rounded up; the schedule shows the area used.
- Tree protection areas around retained trees are set by the arborist's survey; draw what they give, do not calculate them from a rule not in the data.

## 5. Planting schedules

Columns (typical): code, botanical name, common name (optional), size at planting (height, girth or container), form, spacing or density, quantity, remarks. Counts of trees and shrubs come from block counts; bed quantities from area and density. Species data is the landscape architect's; never fill it from assumption.

## 6. Site furniture and lighting

- Blocks with codes and attributes; schedule by code with counts.
- External lighting positions as blocks; lighting levels and electrical design are out of scope.

## 7. Setting out

- Give coordinates (in the survey grid) for key points, or dimensions from fixed features (building corners, boundaries). State the coordinate system.
- Curves by centre and radius, or by offset points at regular intervals.

## 8. Checks

| Check | Signal |
|---|---|
| Survey untouched | Edits in the survey xref; drawing moved off survey coordinates |
| Closed boundaries | Hatches or beds without closed boundaries; overlapping surfaces |
| Codes | Blocks or areas without codes; codes not in the schedule |
| Counts | Schedule quantities that do not match block counts or area x density |
| Scope | Grades, falls or drainage drawn as if designed |
