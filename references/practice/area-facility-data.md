# Area measurement and facility data

Read this before measuring floor areas to a standard, producing area plans and area schedules, exporting room or asset data for facility management, importing data back, or updating drawings and models to as-built. Measurement rules here are from the RICS Code of measuring practice (6th edition, 2015) [N07] and the Massachusetts building code [N21], both in `design-data.md` (measurement file); the basic definitions of GEA, GIA and NIA are in `rooms-areas.md` 4. IPMS and BOMA were not read first-hand; do not apply them from memory. Lease and valuation figures are the user's and their surveyor's responsibility.

Contents: 1 Who uses this · 2 Choosing the basis · 3 Measurement rules that change numbers · 4 Measuring in Revit and AutoCAD · 5 Room, space and asset data · 6 As-built updates · 7 Handover data · 8 Checks

## 1. Who uses this

Facility and property managers, space planners and surveyors use plans as a database: rooms with numbers, names, areas, departments and occupants; assets with ids and locations. They need the drawing and the data to agree, a stated measurement basis, and updates that do not break ids.

## 2. Choosing the basis

| Basis | Typical use | Source |
|---|---|---|
| GEA | Planning, building cost | RICS [N07] |
| GIA | Building cost, valuation of industrial and retail warehouse space, residential sales in some markets | RICS [N07] |
| NIA | Office and shop letting and valuation | RICS [N07] |
| IPMS 1/2/3 | Where the client or market adopts IPMS | RICS Property Measurement [N16, X]; definitions not read |
| Client standard | Space charging, workplace metrics | The client's document; ask for it |
| Room area | Room data sheets, finishes | Revit room boundary rule (`rooms-areas.md` 1) |

Rules:
- Ask which basis applies before reporting any area as GIA, NIA or similar. Default: report room areas labelled "room area at wall finish" and say no standard was applied.
- Never mix bases in one total. A schedule states its basis in the title.
- Round as the standard or client asks; otherwise report m² to two decimals or ft² to whole numbers, and total from unrounded values.

## 3. Measurement rules that change numbers (RICS 6th edition) [N07]

| Rule | Effect | Data id |
|---|---|---|
| NIA excludes areas with headroom under 1.5 m (3.19) | Low parts of rooms under stairs or roofs drop out of NIA | `measurement.uk.rics.nia.low_headroom.exclusion` |
| GIA includes areas with headroom under 1.5 m (2.14), stated separately where needed | GIA keeps them | `measurement.uk.rics.gia.low_headroom.inclusion` |
| NIA excludes spaces under 0.25 m wide (3.20) and areas occupied by heating apparatus projecting 0.25 m or more (3.18(b)) | Narrow spaces and some radiator zones drop out | `measurement.uk.rics.nia.*` |
| Effective floor area and net sales area exclude headroom under 1.5 m (27.9, 26.5) | Shop and residential sale figures | `measurement.uk.rics.efa.*`, `nsa.*` |
| Residential agency measurement is taken at 1.5 m height (24.1, 24.6) | Measuring height | `measurement.uk.rics.rag.measure_height.max` |
| Massachusetts building code: parts of a room under 5 ft (1524 mm) do not count toward the minimum room area (1208.2 exception 2) [N21] | Code room-area check, not a letting area | `measurement.us.ma.low_ceiling.exclusion` |

Read the clause before using it; the full lists of inclusions and exclusions are long.

## 4. Measuring in Revit and AutoCAD

Revit:
- Rooms give room areas at the project's boundary rule; they are not GIA or NIA.
- Area plans measure to a standard: an area plan belongs to an area scheme (Gross Building and Rentable exist in templates). Area boundary lines can follow walls with a chosen rule; areas are placed inside boundaries and scheduled. The add-in can create area plans in existing schemes but cannot create new area schemes.
- Exclusions by headroom need a boundary at the 1.5 m line; draw it from the section geometry and say so.

AutoCAD:
- Areas from closed polylines (or hatches) on area layers, one per space or zone, with a tag carrying the number and the area. The polyline is the measurement record: say where it runs (internal face of perimeter wall, centre of party wall) and keep it there.
- Islands (columns, shafts) are subtracted as inner loops, not by hand.
- Area tables total from the polylines, never typed numbers.

## 5. Room, space and asset data

- Every room or space has a unique number and a name from the agreed list (`rooms-areas.md` 3). Add department, occupant or cost centre only where the client asks.
- Every asset (FF&E item, equipment) that facilities will manage needs: a stable id, type or product code, the room it is in, level, and the fields the client lists. A drawing handle or Revit element id is the link for round trips, not a client id; keep both.
- Export, edit, import: export the data with ids to a spreadsheet, let the user edit it, then write back by id only the columns that changed. Report rows whose ids no longer exist and new rows without ids (they need placing, not importing).
- Containment: an item's room is found from its location point; items on boundaries or outside rooms are reported, not guessed.

## 6. As-built updates

- Update to what was built, from the contractor's record drawings or a survey, and mark the drawing or model "as built" with the date and source in the title block or project information.
- Moving, adding and removing partitions, doors and furniture: edit the existing elements so ids survive; delete only what was removed, and keep demolished-phase records in Revit when the project tracks phases (`interiors.md` 3).
- Rooms whose boundaries change get new areas; keep their numbers unless the client renumbers.
- Revision clouds and notes for each changed area if the as-built set is issued as a revision.

## 7. Handover data

- COBie-style data (spaces, zones, types, components) needs clean rooms, categories, type data and ids first (`export-exchange.md` 6) [S60, X]. The add-in can write the tables as CSV for the user's COBie tool; it does not produce a validated COBie workbook.
- Facility management software imports differ per product; deliver the columns and formats the client specifies.
- IFC handover: `export-exchange.md` 4.

## 8. Checks

| Check | Signal |
|---|---|
| Basis stated | Area totals without a basis; GIA and NIA mixed |
| Boundaries | Area polylines not closed, overlapping, or not on the stated face |
| Unique numbers | Duplicate room numbers; rooms without numbers |
| Containment | Assets with no room, or in a room that does not match their tag |
| Ids preserved | As-built edits that deleted and redrew elements, losing ids |
| Round trip | Imported rows that matched no id; changed columns not in the agreed list |
