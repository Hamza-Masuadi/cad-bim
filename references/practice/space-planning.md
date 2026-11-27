# Space planning, workplace and furniture layouts

Read this before test fits, blocking and stacking, desk and workstation layouts, residential furniture layouts, occupancy checks, move plans or layout options. Room sizes, furniture sizes and occupancy figures come from `design-data.md` with their source and jurisdiction; desk sizes, aisle widths and density targets have no public source and are conventions here. Compliance with codes and leases is the user's responsibility.

Contents: 1 The work and its outputs · 2 Inputs to collect · 3 Test fits · 4 Blocking and stacking · 5 Workplace layouts · 6 Residential furniture layouts · 7 Occupancy and density · 8 Options · 9 Moves and occupancy data · 10 In AutoCAD and Revit · 11 Checks

## 1. The work and its outputs

| Job | Output |
|---|---|
| Test fit | One floor plan showing a brief fits (or does not) a floor plate, with a count of desks, rooms and seats |
| Blocking and stacking | Diagrams of departments per floor (stacking) and their zones on each floor (blocking), with areas against the brief |
| Layout | A furniture and partition plan, dimensioned enough to price and set out |
| Occupancy report | People, desks and seats per room and floor, against the design figure |
| Move plan | Who sits where, by name or by team, before and after |

## 2. Inputs to collect

- The floor plate: base-build plan with cores, columns, windows, risers, escape stairs and fire doors (reference it, do not copy it; `interiors.md` 2).
- The brief: headcount now and planned, teams and adjacencies, desk ratio or sharing ratio, room list with capacities, support spaces (print, storage, lockers, tea points), special rooms.
- Constraints: escape routes and their widths, fire doors to keep clear, accessible routes, ceiling and lighting grid, raised floor grid, structural loading for heavy items (ask), lease lines.
- The jurisdiction, for occupancy and room-size figures.

## 3. Test fits

1. Mark fixed items first: cores, columns, risers, escape routes from each stair to the floor, and a primary circulation loop linking the stairs and lifts.
2. Place enclosed rooms near the core or on internal zones, and open work near the windows, unless the brief says otherwise (typical practice; daylight to desks is the usual reason).
3. Lay desks in runs aligned to the window mullion or planning grid so partitions can meet mullions.
4. Fill support spaces in the remaining zones.
5. Count desks, rooms by capacity, and seats. Compare with the brief in a table: required, provided, difference.
6. Report what does not fit and the smallest change that would make it fit; do not shrink clearances below the figures in `interiors.md` 9 to force a fit.

## 4. Blocking and stacking

- Stacking diagram: one bar per floor, split by department, with area and headcount per department. Departments that must be adjacent share a floor or a stair.
- Blocking plan: each department as a coloured zone on the floor plan, with its area.
- Areas used here are planning areas; state the measurement basis (net internal, usable or the client's standard) and do not mix bases (`area-facility-data.md`).
- Revit: rooms or areas with a Department parameter and a colour scheme by department. AutoCAD: closed polylines per zone with hatch colour by department and an area table.

## 5. Workplace layouts (conventions; confirm with the designer)

| Item | Common practice | Why |
|---|---|---|
| Desk widths | 1200, 1400, 1600 or 1800 mm (4', 5', 6') | Product ranges; there is no public standard |
| Desk depth | 600-800 mm (24"-32") | Product ranges |
| Chair zone behind a desk | 1000-1200 mm to a wall or furniture | `interiors.md` 9.2 |
| Primary aisle | 1500-1800 mm | `interiors.md` 9.2; escape routes may require more, see `design-data.md` escape file |
| Secondary aisle | 900-1200 mm | as above |
| Desk runs | Ends of runs open onto aisles; long runs broken for access | Escape travel and access |

Published figures that apply:
- Great Britain: workrooms should have at least 11 m³ per person, counting no more than 3 m of room height; this is about 4.6 m² at a 2.4 m ceiling and 3.7 m² at 3 m, before furniture [N18] (`workplace.gb.hse.*`).
- US, ADA: at least 5% of seating and standing spaces at dining surfaces and of work surfaces in a facility must be accessible (226.1) [N01] (`workplace.us.ada.work_surfaces.share.min`).
- US, OSHA (advisory): monitor at least 20 in (50 cm) from the eyes; leg clearance under a desk 20-28 in (50-72 cm) high [N19].
- US federal buildings: the GSA facilities standard (2016) set a target usable-to-gross ratio of 80 percent for new buildings [N23]. It was rescinded in February 2025; quote it only as a historic benchmark.

Accessible desks: knee and toe clearance per ADA 306 and work surface height per 902.3 (`interiors.md` 9.1); clear floor space at the desk (305).

## 6. Residential furniture layouts

- Dwellings in England: Approved Document M Volume 1 Appendix D gives the furniture sizes used to test layouts (settees, beds, dining tables by bedspaces, wardrobes) and the access zones around them [N17]. Use them as defaults when the designer gives no product sizes (`furniture.uk.adm1.*`).
- Bedroom access: M4(2) a 750 mm route; M4(3) a 1000 mm access zone to the sides and foot of the principal bed and a 1200 x 1200 mm manoeuvring square (`furniture.uk.m43.*`, `furniture.uk.m42.*`).
- Room sizes: M4(3) minimum bedroom areas and widths and combined living, dining and kitchen areas by bedspaces; NSW Apartment Design Guide bedroom, living-width and apartment areas; Massachusetts minimum room dimensions (`rooms.*`) [N17, N20, N21]. The nationally described space standard for England is in the dwellings file.
- Check door swings against furniture, and that each bedroom takes the bed size its bedspace count implies.

## 7. Occupancy and density

- Design occupancy for escape is a code figure (floor area per person by use); it is in the escape file of `design-data.md` where sourced. Workplace density targets (people per m² of NIA) are client or market figures with no public source; ask.
- Report three numbers and keep them separate: desks provided, design occupancy used for escape, and the brief headcount.
- Meeting room capacity: count seats placed, not a rule of thumb.

## 8. Options

- Keep options separate and named (Option A, Option B), each with its own count table.
- AutoCAD: options on separate layers with layer states, or in separate layouts referencing the same base.
- Revit: design options exist in the product, but the add-in cannot create them. Use separate views with filters on an option parameter, or separate model copies, and say which.

## 9. Moves and occupancy data

- Store the occupant, team and desk id on the desk itself (block attributes in AutoCAD, instance parameters in Revit), not in loose text, so the move list and the plan stay in step.
- A move plan is a table: person or team, from desk, to desk, date. Export the current data, edit it, and import it back by element id (`area-facility-data.md` 5).
- Personal data: occupant names on drawings are personal data; ask before putting names on issued sheets.

## 10. In AutoCAD and Revit

| Need | AutoCAD | Revit |
|---|---|---|
| Desks and furniture | Blocks (dynamic for sizes) on furniture layers, with attributes for code, desk id and occupant | Furniture or furniture-system families; type for product, instance for desk id and occupant |
| Repeated clusters | One block per cluster | Groups (edit one, all update) |
| Zones and departments | Closed polylines plus hatch; area from the polyline | Rooms or areas with Department; colour scheme |
| Counts | Block extraction | Schedules by type, room or level |
| Clearances | Zones as closed polylines tested for overlap | Zones from family geometry or drawn regions |

## 11. Checks

| Check | Signal |
|---|---|
| Brief met | Required vs provided table has negative differences not reported |
| Escape kept | Furniture in escape routes or reducing route widths; fire doors blocked |
| Clearances | Chair zones or aisles below the agreed figures; accessible desks without knee clearance |
| Areas on one basis | Planning areas mixing net and gross, or no basis stated |
| Data on the elements | Occupants or desk ids as loose text |
| Options separated | Two options' furniture visible in one view |
