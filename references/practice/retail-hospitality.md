# Retail, hospitality and exhibition interiors

Read this before shop fit-out plans, fixture plans, restaurant and bar seating layouts, hotel guest room layouts, signage plans and exhibition stand drawings. The accessibility figures are from the 2010 ADA Standards [N01] and are in `design-data.md` (fitout, reach and kitchens-bathrooms files); other jurisdictions are not quoted. Fixture sizes, seating densities and room sizes for hotels and shops have no public source here; they come from the brand standard or the operator, so ask for it. Compliance is the user's responsibility.

Contents: 1 Brand and operator standards · 2 Retail fixture plans · 3 Counters and check-outs · 4 Restaurants and bars · 5 Hotel guest rooms · 6 Assembly seating · 7 Signage and graphics · 8 Exhibition stands · 9 Checks

## 1. Brand and operator standards

Most retail and hospitality work follows a brand or operator manual: fixture types, finishes, sign families, guest room layouts, back-of-house rules. Ask for it first. Where it is silent, use the project's code and the conventions here, and report what was assumed.

## 2. Retail fixture plans

- Each fixture type as a block or family with a code; the fixture schedule gives code, description, size, finish, quantity, and who supplies it.
- Fixtures sit on a planning grid aligned to the shopfront or column grid so runs line up (typical practice).
- Aisles between fixtures are accessible routes where the public goes: US 36 in (915 mm) minimum with passing spaces (403.5) [N01]; escape routes may need more (`design-data.md` escape file).
- Show the sales area separately from stock and back of house; the RICS net sales area rule is in `area-facility-data.md` 3.
- Fixtures that need power or data appear on the services plan with positions.

## 3. Counters and check-outs (US, 2010 ADA) [N01]

| Item | Requirement | Clause |
|---|---|---|
| Check-out aisle counter | 38 in (965 mm) max high; counter edge protection 2 in max above the surface on the aisle side | 904.3.2 |
| Sales and service counter, parallel approach | An accessible portion at least 36 in (915 mm) long, 36 in high max, with a clear floor space for parallel approach | 904.4.1 |
| Sales and service counter, forward approach | At least 30 in (760 mm) long, 36 in high max, with knee and toe space | 904.4.2 |

The accessible portion extends the same depth as the counter (904.4). Check-out aisle numbers by count of aisles are in 904.3 and the scoping chapter; read the table before quoting it.

## 4. Restaurants and bars

- Seating layout: tables and chairs as blocks or families; count covers from placed seats.
- At least 5% of seating spaces at dining surfaces must be accessible in the US (226.1), with dining surfaces 28-34 in (710-865 mm) high and knee and toe clearance (902) [N01].
- Keep routes to accessible seats, WCs and exits clear of chair zones (chair pushed back about 450-600 mm from the table edge, typical practice).
- Bar counters: show a lowered accessible portion where the code requires one; do not assume a height for other jurisdictions.
- Kitchens and servery: `kitchens-bathrooms.md`; commercial kitchen equipment layouts are the kitchen designer's.

## 5. Hotel guest rooms

- Design one typical room per type as a block (AutoCAD) or group (Revit) and repeat it; edit the definition, not each copy.
- Accessible rooms: the number by room count is set by the code's scoping table (ADA 224); the room itself needs a clear floor space on both sides of a bed or one between two beds (806.2.3) [N01], an accessible bathroom (806.2.4), and routes through the room.
- Mirror pairs (handed rooms) are separate definitions or mirrored instances; check that mirrored text and tags read correctly.
- Keep services positions (risers, bathroom pods) aligned floor to floor.

## 6. Assembly seating (US, 2010 ADA) [N01]

- Wheelchair spaces: 36 in (915 mm) wide for one, 33 in (840 mm) each for two adjacent (802.1.2); 48 in (1220 mm) deep entered from front or rear, 60 in (1525 mm) entered from the side (802.1.3).
- Spaces adjoin an accessible route and must not overlap the required width of circulation paths (802.1.4, 802.1.5).
- Numbers of spaces and companion seats are set by the scoping table (221); read it before quoting.

## 7. Signage and graphics

- Each sign type as a block or family with a code; a location plan and a sign schedule (code, message, location, mounting height, size, quantity).
- Mounting heights for tactile signs and room signs are code figures in the US (703); they are not in the data yet, so quote them only after reading 703.

## 8. Exhibition stands

- 2D drawings are in scope: plan with stand boundary, walls, counters and graphics; elevations with heights and graphic panels; a fixture and graphics schedule.
- Organisers' rules (maximum heights, set-back from aisles, rigging) come from the event manual; ask for it.
- 3D stand models and visuals are out of scope: the AutoCAD engine draws 2D, and rendering is excluded.

## 9. Checks

| Check | Signal |
|---|---|
| Brand standard used | Fixtures or sizes chosen without the brand or operator manual, not reported as assumed |
| Accessible routes | Aisles below the route width; chairs or fixtures in routes |
| Counters | No accessible portion; accessible portion too high or too short |
| Seating counts | Accessible seats under the required share; covers counted by hand |
| Repeated rooms | Typical rooms exploded or edited one by one |
| Scope | Exhibition 3D or visuals promised |
