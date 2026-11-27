# Schedules and tags

Read this before creating or checking schedules, tags, sheet lists, or AutoCAD tables built from block data. The principle: data lives on the element; tags and schedules only display it. Source tags point to `_SOURCES.md`.

Contents: 1 Schedule types · 2 Building a schedule · 3 Tags vs text · 4 Tag-all practice · 5 Standard schedules · 6 AutoCAD equivalent · 7 Checks

## 1. Schedule types (Revit)

| Type | What it lists | Use |
|---|---|---|
| Element schedule (schedule/quantities) | Instances or types of a category | Door, window, room, wall, furniture schedules |
| Multi-category schedule | Several categories | QA schedules (e.g. all elements with empty Mark) [S70] |
| Key schedule | Rows of named keys that fill several parameters at once | Room styles (finishes per room type), door hardware sets; key schedules can use shared parameters in recent releases (What's New title only, unverified) |
| Material takeoff | Quantities by material | Finish areas, volumes for cost |
| Sheet list | Sheets | Drawing index [S22] |
| View list | Views | QA: views not on sheets, views without templates |
| Note block | Instances of a generic annotation | Numbered notes |
| Keynote legend | Keynotes used | Per sheet or set [S66] |

## 2. Building a schedule

- **Fields**: only what the reader needs; order as the reader scans (identifier first: Mark, then location, then size, then properties, then remarks).
- **Filters**: restrict by level, phase, type or a parameter (e.g. exclude "Not in contract"). Up to a limited number of filter conditions, combined with AND.
- **Sorting/grouping**: sort by identifier; group by level or type with headers; turn "itemize every instance" off for counts by type.
- **Calculated values**: formulas (e.g. area × rate, width − 2 × frame) and percentages. Keep units consistent; a formula in a text field will not calculate.
- **Totals**: counts and sums where meaningful (areas, quantities), not on identifiers.
- **Formatting**: headers in the project text type; column widths that fit the sheet; units and rounding per project.
- **Phase**: schedules have a phase; set it to the phase being documented, or demolished doors will appear in the new-work schedule.
- **Links**: include elements in links when the schedule must cover linked models (sheet index generated across models, NY OGS) [S22].
- Use schedule templates (view templates for schedules) for consistency [S22].
- Schedules are a fast editing surface: change parameters in the schedule, and the model updates. Use that for bulk data entry.

## 3. Tags vs text

- Tags read a parameter and update when the data changes. Typed text goes stale (RV-009) [S34][S38].
- Never type a door number, room name, area, wall type or keynote as text next to an element.
- A tag shows what the schedule shows. If the tag is wrong, fix the parameter.
- Tags are view-specific annotation; place them in views, not on sheets [S22]. Tagging works in 2D views or locked 3D views [R04].
- Tag labels that need custom data use shared parameters [S68].

## 4. Tag-all practice

1. Apply the view template first, so only the intended categories are visible.
2. Tag all untagged elements of the categories required in that view type (e.g. GA plan: doors, rooms, wall types where required; RCP: ceilings, heights; furniture plan: furniture if required).
3. Choose the tag type and orientation for the view's scale; leaders off unless the tag cannot sit on the element.
4. After tagging, check for collisions (tags over walls, tags over tags) and move them; at small scales consider fewer tags and a schedule instead.
5. Report elements that could not be tagged (no tag family loaded, not visible in the view, linked without tag support).

## 5. Standard schedules (conventions; adjust to the project)

### 5.1 Door schedule
| Column | Notes |
|---|---|
| Mark | Unique; often room-based (101A) |
| From room / To room or Room | Phase-dependent |
| Leaf width x height x thickness | Type values |
| Leaf type / material / finish | |
| Frame type / material / finish | |
| Glazing / vision panel | |
| Fire rating | Type parameter |
| Hardware set | Key to the hardware schedule |
| Handing | Per agreed convention (see `openings.md`) |
| Undercut / acoustic rating / security | Project-specific |
| Remarks, detail references | Head, jamb, sill details |

### 5.2 Window schedule
By type (Type Mark), with count, width x height, sill and head height, frame material and finish, glazing (U-value, safety glazing), operation, fire rating, remarks.

### 5.3 Room schedule and room finish schedule
Number, name, level, area (with basis), department, occupancy, floor finish, base, wall finish (by wall or all), ceiling finish and height, remarks. Drive finishes with a key schedule when many rooms share them. See `rooms-areas.md` and `interiors.md`.

### 5.4 Wall type schedule or legend
Wall types are usually shown in a legend with a drawn section of each type, its tag, thickness, fire and acoustic rating, rather than an instance schedule.

### 5.5 Sheet list (drawing index)
Sheet number, sheet name, current revision, revision date, issue status. Sorted by discipline order and number (OGS uses sort parameters) [S22].

### 5.6 QA schedules
Multi-category schedule with Category, Family, Type, Mark and Comments filtered to empty or default values; Generic Models schedule [S70]; view list filtered to views not on sheets.

## 6. AutoCAD equivalent: attributes and tables

- Put data in block attributes: door block with MARK, WIDTH, HEIGHT, TYPE, FIRE, HW; room tag with NUMBER, NAME, AREA.
- Build schedules as AutoCAD tables filled from the attributes (data extraction or the job's own extraction), never typed. Keep the table linked or regenerate it on every change; a static table is as stale as typed text.
- Use fields for computed values in tags (area from a closed polyline) so tags update.
- Title blocks: attributes or fields linked to sheet properties [S10].
- Export the same data to CSV when the user wants a spreadsheet.
- Table style per standard: header, title and data cell styles at the standard plotted heights.

## 7. Checks

| Check | Signal |
|---|---|
| Untagged elements | Count of elements in a category with no tag in the documentation views |
| Typed text pretending to be tags | Text notes whose content matches marks or room names [S34][S38] |
| Duplicate marks | Revit duplicate value warnings; duplicate attribute values |
| Empty required fields | Schedule rows with blanks in required columns |
| Phase set wrong | Demolished elements in new-work schedules |
| AutoCAD tables static | Tables not linked to data and differing from block attributes |
| Sheet list complete | Sheets missing from the index or index rows with no sheet |
