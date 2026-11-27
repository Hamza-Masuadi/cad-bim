# Accessibility review

Read this before reviewing drawings or a model for accessibility: measuring door widths, routes, turning spaces, sanitary layouts, counters, reach and mounting heights, and marking up findings. The agent measures and flags against published figures with their source and jurisdiction (`design-data.md`); it does not certify compliance. Code compliance stays with the user and their access consultant or authority.

Contents: 1 What a review delivers · 2 Before starting · 3 What to measure · 4 Measuring from drawings and models · 5 How to flag · 6 Marking up · 7 Limits · 8 Checks

## 1. What a review delivers

- A findings list: each item with location (room, grid, sheet), what was measured, the figure measured, the governing figure with source, clause and jurisdiction, and a status (meets, below, cannot tell).
- Marked-up drawings: clouds and notes keyed to the findings list.
- A note of what was not reviewed (items not drawn, heights not shown, jurisdictions not in the data).

## 2. Before starting

- Ask the jurisdiction and the standard (for example the 2010 ADA Standards, Approved Document M Volume 1 or 2, a state code). If the data has no file for it, say so; do not substitute another jurisdiction's figures silently.
- Ask which parts are in scope: public areas, accessible rooms, a percentage of rooms, the whole building.
- Ask which drawings are current, and whether the model or the drawings govern.

## 3. What to measure

| Item | Where the figures are |
|---|---|
| Door clear widths and manoeuvring space | `design-data.md` doors file; `openings.md` |
| Route and corridor widths, passing places | routes file; `interiors.md` 9.1 |
| Turning spaces and clear floor spaces | routes, furniture files; `interiors.md` 9.1 |
| Ramps and stairs: gradient, rise, going, handrails | ramps and stairs files; `stairs-ramps.md` |
| WCs, bathrooms and showers | sanitary and kitchens-bathrooms files; `kitchens-bathrooms.md` 5 |
| Kitchens | kitchens-bathrooms file; `kitchens-bathrooms.md` 4 |
| Counters, work and dining surfaces | reach, fitout and workplace files; `retail-hospitality.md` 3, 4 |
| Reach ranges and control heights | reach file |
| Assembly wheelchair spaces | fitout file; `retail-hospitality.md` 6 |
| Lifts and parking | lifts and parking files |
| Bedrooms in dwellings and hotels | furniture and rooms files; `space-planning.md` 6 |

## 4. Measuring from drawings and models

- Measure clear dimensions to the faces the source names (clear opening of a door, between handrails, between projecting fittings). Drawn wall lines may be structure, not finish; say which you measured (`dimensions-annotation.md`).
- Turning spaces and clear floor spaces: place the zone and test it against walls, fittings, furniture and door swings in their drawn positions (`interiors.md` 9.3).
- Heights: read them from elevations, sections, block attributes or family parameters. If a height is not drawn or recorded, the status is "cannot tell", not "meets".
- Revit: read element parameters (door widths, stair riser and tread values, ramp slope, mounting offsets); measure from geometry only when no parameter holds the value.
- AutoCAD: read block attributes and dynamic properties; plain 2D linework of stairs and ramps carries no riser or gradient data, so ask for the section or the model.

## 5. How to flag

- One finding per item and location. Wording: "Door D1.04: clear width about 760 mm measured between stop and leaf face; 2010 ADA 404.2.3 requires 32 in (815 mm) min (US). Below by about 55 mm."
- Give the measured value with its precision and method ("from drawing at 1:50, ±10 mm").
- Never write "compliant" or "approved"; write "meets the stated figure".
- Where the data marks a figure secondary or a convention, say so.

## 6. Marking up

- Cloud each finding on the plan or elevation, with a tag keyed to the findings list, on a markup layer or as a revision.
- Keep markups off the design layers and out of the model elements; do not change the design unless asked.
- Issue the marked-up PDF and the findings list together.

## 7. Limits

- The review is as good as the drawings: unshown items (handrail extensions, door closer forces, surfaces, signage, visual contrast, lighting) cannot be checked from geometry. List them as not reviewed.
- Operational items (furniture moved after handover, management of routes) are outside a drawing review.
- Jurisdictions not in the data are not reviewed against remembered figures.

## 8. Checks

| Check | Signal |
|---|---|
| Jurisdiction stated | Findings without source, clause and jurisdiction |
| Method stated | Measured values without precision or method |
| Unknowns honest | Heights or items not drawn marked as meeting the figure |
| Markups separate | Markups on design layers or as edits to the model |
| No certification | "Compliant", "approved" or "passes code" in the report |
