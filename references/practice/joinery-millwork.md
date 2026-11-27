# Joinery and millwork drawings

Read this before drawing built-in units: reception desks, kitchens, wardrobes, shelving, wall panelling, bar counters, retail display units, vanities. "Joinery" (UK) and "millwork" or "casework" (US) mean the same work. No first-hand public source was found for joinery drawing practice; everything here is typical practice and the office or fabricator standard governs. The fabricator's shop drawings, not the designer's drawings, set the final construction.

Contents: 1 What the designer draws · 2 Codes and references · 3 Plans, elevations and sections · 4 Details · 5 Materials and hardware · 6 Accessibility at counters · 7 In AutoCAD and Revit · 8 Checks

## 1. What the designer draws

| Drawing | Scale (typical) | Shows |
|---|---|---|
| Location plan | as the floor plan | Each unit outlined and coded |
| Unit plan | 1:20 or ½" | Outline, worktop, doors and drawers, appliances, dimensions to walls |
| Elevations | 1:20 or ½" | Fronts, door and drawer divisions, handles, heights, materials |
| Sections | 1:10 or 1" to 1½" | Carcass, worktop, plinth, fixings to wall, services |
| Details | 1:5, 1:2 or 3" to full size | Edges, joints, junctions with walls, floors and ceilings |
| Schedule | — | Unit code, description, location, materials, hardware, finish, remarks |

Design intent is what the designer controls: sizes, appearance, materials and finishes, functional requirements (loads, services, access). The fabricator chooses joints and internal construction unless the drawings say otherwise; say "or fabricator's equivalent" where the designer does not care.

## 2. Codes and references

- Code each unit (`J01`, `MW-01`, or the office scheme) and use the code on the location plan, elevations, sections and schedule.
- Elevation and section markers on the unit plan point to the sheets where they are drawn.
- Repeated units share a code; handed versions get a suffix (`J01`, `J01-H`) rather than a note.

## 3. Plans, elevations and sections

- Dimension overall sizes, divisions and heights; give clear internal sizes only where they matter (appliances, bins, files).
- Heights from finished floor level; state FFL on the sheet.
- Show the wall and floor the unit sits on, with the scribe or filler allowance to uneven walls.
- Show services passing through or into the unit: sockets, data, water, waste, ventilation, with positions.
- Sections cut through the most informative places (worktop overhang, plinth, wall fixing, glass).

## 4. Details

- Draw edge profiles, junctions with walls and ceilings (shadow gaps, scribes), and changes of material.
- Use hatches for materials consistently with the project's hatch legend (`drawing-conventions.md`).
- Keep detail sheets referenced: every detail is called up from an elevation or section, and every callout leads to a detail.

## 5. Materials and hardware

- Material codes match the finish legend (`interiors.md` 4): one code per board, laminate, veneer, stone or metal and its finish.
- Hardware (hinges, runners, handles, locks) by type and code in the schedule, from the specification; do not pick products.
- Glass: type and thickness as specified; mark safety glass where the specification requires it.

## 6. Accessibility at counters

US 2010 ADA: sales and service counters need an accessible portion; for parallel approach at least 36 in (915 mm) long and at most 36 in (915 mm) high; for forward approach at least 30 in (760 mm) long and 36 in high max, with knee and toe space (904.4) [N01]. Check-out aisle counters 38 in (965 mm) max with a 2 in lip (904.3.2) [N01]. Values are in the fitout and reach files of `design-data.md`. Other jurisdictions are not quoted here.

## 7. In AutoCAD and Revit

| Need | AutoCAD | Revit |
|---|---|---|
| Unit on plans | Block per unit on a casework layer (`I-CASE`, `A-FLOR-CASE` or the standard's) | Casework family instance |
| Elevations and sections | 2D linework and hatches per unit, at the detail scale | Section and elevation views of the model, plus detail lines and filled regions; or a drafting view |
| Details | 2D linework, hatches, dimensions | Drafting views, or callouts with detail components |
| Schedule | Table from block attributes | Casework schedule |

Revit note: in-place families cannot be created through the add-in. Built-in units are loadable casework families, or drafted details where the model need not carry them.

## 8. Checks

| Check | Signal |
|---|---|
| Codes consistent | A code on the plan with no elevation or schedule entry, or the reverse |
| References complete | Section or detail markers pointing to nothing |
| Heights | Heights without FFL; counters over the accessible height where an accessible portion is needed |
| Services | Appliances or sinks without service positions |
| Materials | Material codes not in the finish legend |
