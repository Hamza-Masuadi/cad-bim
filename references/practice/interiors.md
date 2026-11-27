# Interiors

Read this before any fit-out or interior job: base-build set-up, existing/demolition/new plans, partition and furniture layouts, finish plans and schedules, FF&E layouts and schedules, interior elevations, interior RCPs and lighting layouts, or material tags. The research found no first-hand public source for finish and FF&E schedule practice; those conventions are typical practice and the office standard governs. Accessibility and room-size values carry their source and jurisdiction in `design-data.md`; compliance is the user's responsibility.

Related guides: `space-planning.md` (furniture and workplace layouts), `kitchens-bathrooms.md`, `joinery-millwork.md`, `rcp-lighting.md`, `retail-hospitality.md`, `area-facility-data.md`, `accessibility-review.md`.

Contents: 1 Who does interior work and what they hand over · 2 Fit-out set-up · 3 Existing, demolition and new · 4 Finish codes and legend · 5 Finish plans and schedules · 6 Modelling finishes in Revit · 7 Finishes in AutoCAD · 8 FF&E · 9 Layout clearances · 10 Interior elevations · 11 RCP and lighting · 12 Material tagging · 13 Questions to ask · 14 Checks

## 1. Who does interior work and what they hand over

Interior designers work on residential interiors and commercial fit-out (offices, shops, restaurants, hotels, healthcare and education interiors). A typical drawing set (typical practice; the office set list governs):

| Drawing | Shows | Usual scale |
|---|---|---|
| Existing / survey plan | The space as found, with the base build | 1:100, 1:50 or ⅛", ¼" |
| Demolition plan | What is removed, dashed or on a demolition layer or phase | as plan |
| Partition (setting-out) plan | New walls, doors, glazed screens, dimensions to fixed references | 1:50 or ¼" |
| Furniture / FF&E plan | Loose and fixed furniture with codes | 1:50 or ¼" |
| Finish plan | Floor finishes, transitions, wall finish tags | 1:50 or ¼" |
| Reflected ceiling plan | Ceiling types and heights, grid, fixtures | 1:50 or ¼" |
| Power and data / services coordination plan | Socket, data, floor box and switch positions | 1:50 or ¼" |
| Interior elevations | Each wall of key rooms: finishes, joinery, mounting heights | 1:50, 1:20 or ¼", ½" |
| Joinery drawings | Built-in units: plans, elevations, sections, details | 1:20, 1:10, 1:5 or ½"-3" |
| Schedules | Finishes, FF&E, doors, ironmongery, sanitaryware, lighting | — |

Who owns what: the base build (structure, envelope, cores, risers) belongs to the landlord or base-build architect; the interior designer draws on top of it and must not change it. Engineers own the mechanical, electrical and fire systems even when they appear on interior drawings.

## 2. Fit-out set-up

1. Get the base-build drawings or model and a measured survey. Use the base build as a reference (an xref in AutoCAD, a link in Revit), never as a copy you edit. Why: when the landlord issues a revision the reference updates; a copy silently goes stale.
2. Check the base build against the survey on site dimensions that matter (column positions, core walls, window mullions, slab-to-soffit heights). Report differences instead of "fixing" the base drawing.
3. Agree the setting-out references: grid lines, core walls or column faces. Dimension new work to these (see `dimensions-annotation.md`).
4. Agree heights: finished floor level (raised floor or screed changes it), slab soffit, ceiling heights by area, door head height.
5. Set up layers, styles and sheets from the office or client standard (`cad-standards.md`, `bim-setup.md`). Interior layers in the US NCS format use the `I` discipline (`I-WALL`, `I-FURN`, `I-FLOR-PATT`, `I-CLNG`) [S05]; in ISO 13567 or UK formats use the project's own codes.

## 3. Existing, demolition and new

- Show three states: existing to remain, existing to be removed (demolition), and new. Never mix them on one undifferentiated layer or phase.
- AutoCAD: use the layer status field (`E` existing, `D` demolition, `N` new, `F` future) in the NCS-style format [S05, S09], or the project's own status codes. Demolition is dashed, existing is lighter, new is full weight (typical practice). Save layer states for "demolition plan" and "new plan" presentations.
- Revit: put existing elements in the Existing phase and new work in New Construction; demolish by setting the element's Phase Demolished, not by deleting it. Views show the state through their phase and phase filter (see `views-templates-graphics.md`). Why: deleting an existing wall loses the demolition record and the demolition plan.
- Demolition notes refer to the item, the extent and what happens at the junction (make good, patch, extend finish). Keep notes on the demolition plan, keyed if many.
- As-built: after construction the "new" state becomes the next project's "existing" (see `area-facility-data.md` 6).

## 4. Finish codes and legend (typical practice)

- Give every finish a short code by type and sequence: `PT-01` paint, `CPT-01` carpet, `VT-01` vinyl tile, `CT-01` ceramic tile, `WD-01` wood, `ST-01` stone, `WC-01` wall covering, `ACT-01` acoustic ceiling tile, `GWB` gypsum board ceiling, `RB-01` rubber base. Use the office prefix list when it exists.
- The finish legend lists: code, description, manufacturer and product (as specified), colour/finish, size, location notes, specification section reference.
- One code means one product, colour and finish. If any of those change, it is a new code.
- Keep the legend in one place (a legend view, a drafting view or a schedule in Revit; a table in AutoCAD) and place it on the finish plans or a finish schedule sheet.
- Product data (manufacturer, colour) is the designer's choice; never fill it in from assumption. Leave the cell blank and report it.

## 5. Finish plans and schedules

Two common formats; ask which the project uses.

| Format | Columns or content | Best for |
|---|---|---|
| Room finish schedule (per room) | Room number, name, floor, base, walls (all or N/E/S/W), ceiling, ceiling height, remarks | Most projects |
| Finish plan (graphic) | Floor finishes as patterns or tags on a plan, with transitions marked; wall finishes tagged on interior elevations | Complex interiors, retail, hospitality |

Rules:
- Room finish data comes from room parameters (Revit has Base, Floor, Wall and Ceiling Finish text parameters; add shared parameters for per-wall finishes or heights). In AutoCAD, put finish codes in attributes of the room tag block so the schedule and the plan read the same data.
- Use a key schedule (room style) to apply standard finish sets to many rooms consistently; override only exceptions.
- Show floor finish transitions in plan with a line and both codes, centred under the door leaf in the closed position unless the standard says otherwise (typical practice).
- Floor patterns on a finish plan show the laying direction and the start point for tiles and planks where it matters; the setting-out of tiles follows the same "equal borders, no slivers" rule as ceiling grids (`rcp-lighting.md` 3).
- Interior elevations carry wall finish tags, heights of dado/splashbacks, and fixture mounting heights.
- Every code on a plan or elevation appears in the legend; every legend code is used somewhere.

## 6. Modelling finishes in Revit

| Finish | Options | Pick when |
|---|---|---|
| Floor finish | Layer in the floor type; separate thin floor per room/zone; or room parameter only | Separate floors when areas and patterns must be counted per room; parameter only when a schedule is enough |
| Wall finish | Layers in the wall type; separate finish walls; parts | Layers for simple projects; finish walls or parts when finishes vary along a wall (watch double counting and joins) |
| Ceiling | Ceiling types with the finish material | Always |
| Base | Wall sweep (skirting) | Always prefer sweeps to in-place families |

- Material quantities come from material takeoffs; set material names and keynotes consistently.
- A thin finish floor created from a room boundary follows the room; recheck it after partitions move.
- Surface patterns of materials drive the drawn tile or plank pattern. The position of a model pattern in a floor or ceiling is adjusted by hand in Revit; the add-in has no way to align patterns (see `rcp-lighting.md` 4).

## 7. Finishes in AutoCAD

- Floor finishes as hatches on finish-pattern layers (`I-FLOR-PATT` or project equivalent), one hatch per finish zone, with the hatch boundary on the wall finish line. Associate the hatch with its boundary where the office does; scale the pattern to real tile size for tiles (a user-defined or real-size pattern), not to an arbitrary scale.
- Transitions as lines on a separate layer, with a leader or tag giving both codes.
- Area per finish from the hatch area (report it rounded per `area-facility-data.md`); state it is an area of the drawn hatch, not a measured floor area.

## 8. FF&E (furniture, fixtures and equipment)

- Model FF&E as loadable families in the right categories (Furniture, Furniture Systems, Casework, Specialty Equipment, Plumbing Fixtures, Lighting Fixtures). Category drives schedules and IFC (see `families-parameters.md`).
- Put FF&E on its own workset, not visible by default (NY OGS) [S22]; keep entourage on hidden worksets or design options [S34].
- Code FF&E items (e.g. `CH-01` task chair, `DK-02` desk, `ST-01` storage) with a type parameter; tag with the code; schedule by type with count per room.
- FF&E schedule columns (typical): code, description, manufacturer, model, finish/colour, dimensions, quantity, location (room), supplier/contractor responsibility (OFOI, CFCI or local equivalent), remarks.
- Show fixed equipment and items that need services (power, data, water) on coordination plans; loose furniture on furniture plans only.
- AutoCAD: furniture blocks on furniture layers (`I-FURN`, `A-FURN`, minor groups for systems `I-FURN-PNLS` [S05]), with attributes for code; count with block extraction. Dynamic blocks with width or length parameters keep one definition per product.
- Size furniture from the designer's product choice. Where none is given and the layout is a dwelling in England, the Approved Document M Volume 1 Appendix D sizes are a published default (`design-data.md`, furniture file) [N17]. Commercial furniture sizes have no public source; use the product data.
- FF&E registers that facility managers keep after handover need a stable id per item and the room it is in (`area-facility-data.md` 5).

## 9. Layout clearances

### 9.1 Code and guidance values (cite the jurisdiction when you flag them)

| Item | 2010 ADA Standards (US) [N01] | England, Approved Document M [N03, N17] |
|---|---|---|
| Wheelchair turning space | 60" (1525 mm) diameter circle, or T-shape in a 60" square with 36" (915 mm) arms and base (304.3) | Dwellings M4(3): 1500 mm circle or 1400 x 1700 mm ellipse in the living space (Appendix D) |
| Clear floor space at an element | 30" x 48" (760 x 1220 mm) min (305.3) | Dwellings M4(3): 1200 x 1200 mm manoeuvring square in bedrooms (Appendix D) |
| Accessible route width | 36" (915 mm) min; 32" (815 mm) for up to 24" (610 mm) length (403.5.1); passing spaces 60" x 60" every 200 ft (61 m) where narrower than 60" (403.5.3) | Buildings other than dwellings: corridors unobstructed width at least 1200 mm; passing places where narrower than 1800 mm (Vol 2 section 3) |
| Bed access | One sleeping area with a clear floor space on both sides of a bed, or one space between two beds (806.2.3) | M4(3): 1000 mm access zone to both sides and the foot of the principal bed; M4(2): 750 mm route (data) |
| Knee clearance | between 9" (230 mm) and 27" (685 mm) above the floor, 30" (760 mm) wide; depth 11" (280 mm) at 9" (230 mm) and 8" (205 mm) at 27" (306.3) | not quoted |
| Work and dining surfaces | Top 28"-34" (710-865 mm) above floor (902.3); 5% of seating spaces and standing spaces at dining surfaces (226.1) | not quoted |
| Reach range, unobstructed forward | 15"-48" (380-1220 mm) (308.2.1) | not quoted |
| Protruding objects | Leading edges 27"-80" (685-2030 mm) above floor protrude 4" (100 mm) max into circulation (307.2) | not quoted |
| Accessible WC (water closet) | Centreline 16"-18" (405-455 mm) from side wall (604.2); clearance 60" (1525 mm) from side wall by 56" (1420 mm) from rear wall (604.3.1); seat 17"-19" (430-485 mm) (604.4); grab bars 33"-36" (840-915 mm) (609.4) | ADM has its own WC layouts; not quoted |
| Lavatory | Rim or counter 34" (865 mm) max (606.3) | not quoted |

Kitchen, bathroom, counter and seating figures are in `kitchens-bathrooms.md` and `retail-hospitality.md`.

### 9.2 General furniture clearances (typical practice, not code; confirm with the designer)

| Situation | Typical clearance |
|---|---|
| Behind a desk chair to a wall or furniture | 1000-1200 mm (3'-4") |
| Main circulation in open offices | 1500-1800 mm (5'-6') primary aisles; 900-1200 mm (3'-4') secondary |
| In front of drawers, filing, appliances | 900-1000 mm (3'-0" to 3'-4") |
| Between dining table edge and wall, chair occupied | 900-1200 mm (3'-4") |
| Around a bed for making and access | 600-900 mm (2'-3') |
| Kitchen work aisle between counters | 1000-1200 mm (3'-6" to 4'-0") |

When a layout serves accessible use, the code values in 9.1 govern over these. Escape route widths and door widths govern over both (`design-data.md`, escape and doors files).

### 9.3 How to check a layout

1. Represent each required space as a zone: turning circles, clear floor spaces, access zones, door swings and route widths.
2. Test each zone against walls, furniture, fixtures and door swings in their final positions. A zone may overlap knee or toe space only where the source allows it (ADA 306; M4(3) allows the bedhead encroachment stated in the data).
3. Report every intrusion: the zone, what intrudes, by how much, and the source figure. Do not move furniture to "make it pass" unless asked.

## 10. Interior elevations

- One elevation per wall of each room that has joinery, fixtures, finishes changes or mounting heights; number them with the elevation marker (e.g. `1/A501` or the project's scheme).
- Show: finishes with tags, joinery outlines with codes, fixtures and appliances, sockets and switches with heights to the centre (state the datum), dado, splashback, mirror and grab bar heights, door and window openings, ceiling line and bulkheads.
- Dimension heights from finished floor level; state FFL on the sheet.
- Revit: crop each elevation to the room; use a view template for interior elevations; tag materials rather than typing their names (section 12).
- AutoCAD: draw elevations as 2D linework on elevation layers; keep each elevation's insertion point on a consistent datum so heights can be read by coordinates.

## 11. Interior RCP and lighting layout

See `rcp-lighting.md`. Short version: ceiling types and heights tagged, grid set out with equal borders, lights aligned to the grid and to the furniture they serve, switches with heights, engineers' elements coordinated not moved.

## 12. Material tagging

- Material tags in sections, elevations and details read the material's own data (description, mark or keynote), so the tag stays true if the material changes.
- Materials named per the office standard (NBS suggests a `_mtrl` suffix for material names) [S71]; one material per finish code; identity data filled (description, manufacturer, mark/keynote).
- Do not create duplicate materials differing only by name; purge unused materials.
- AutoCAD: material tags as multileaders or keynote blocks keyed to the legend.

## 13. Questions to ask before a fit-out job

| Topic | Ask | Default to propose |
|---|---|---|
| Base build | Which drawings or model are the base, and which revision? | The latest issued revision; reference it, never copy |
| States | Is there demolition? Which elements are existing to remain? | Existing + demolition + new |
| Finish data | Room finish schedule, finish plan, or both? Which codes? | Room finish schedule with the code list in section 4 |
| FF&E | Which items are in scope (loose, fixed, client-supplied)? Responsibility codes? | Loose and fixed in scope; responsibility blank until told |
| Ceilings | Ceiling types and heights per area; grid module | Ask; no default height |
| Accessibility | Which jurisdiction and which rooms must be accessible? | Ask; flag against the closest data file and say compliance is not checked |

## 14. Checks

| Check | Signal |
|---|---|
| Base build untouched | Edits inside the reference, or a copied base drawing |
| States separated | Demolished items deleted instead of phased; existing and new on one layer |
| Every room has finishes | Rooms with empty finish fields |
| Codes in the legend | Finish codes used in schedules or tags but absent from the legend, or legend codes unused |
| FF&E on its workset and categorised | FF&E elements on other worksets; furniture as Generic Models |
| Counts match | FF&E schedule counts vs tagged items |
| Clearances | Furniture inside door swings, turning spaces or required clear widths |
| Elevations complete | Rooms with joinery or fixtures but no interior elevation; heights without a datum |
| RCP coordination | Fixtures overlapping diffusers or sprinklers |
