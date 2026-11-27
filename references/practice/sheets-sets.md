# Sheets and drawing sets

Read this before creating sheets, numbering them, placing views or viewports, building a drawing index, issuing revisions, or naming deliverables. Source tags point to `_SOURCES.md`.

Contents: 1 Sheet sizes · 2 Title block content · 3 Sheet numbering · 4 Set order and index · 5 Placing views · 6 Scales by drawing type · 7 Revisions · 8 Status and issue codes · 9 Checks

## 1. Sheet sizes

| ISO A (mm) [N06] | ARCH (in) [S81][S10] | ANSI (in) [S81] |
|---|---|---|
| A0 841 x 1189 | ARCH A 9 x 12 | ANSI A 8.5 x 11 |
| A1 594 x 841 | ARCH B 12 x 18 | ANSI B 11 x 17 |
| A2 420 x 594 | ARCH C 18 x 24 | ANSI C 17 x 22 |
| A3 297 x 420 | ARCH D 24 x 36 | ANSI D 22 x 34 |
| A4 210 x 297 | ARCH E 36 x 48; ARCH E1 30 x 42 | ANSI E 34 x 44 |

ISO A sizes keep the √2 ratio, so an A1 sheet printed on A3 is exactly half scale [N06]. Use that: a 1:100 plan on A1 reads as 1:200 on A3, and a graphic scale bar stays correct. ARCH and ANSI sizes do not share one ratio; reductions are not exact.

Common choices (typical practice): A1 or ARCH D (24 x 36) for construction sets; A3 or 11 x 17 for reduced sets and reports. NY OGS mandates a 24 x 36 horizontal title block [S22]. One sheet size per set unless the standard allows otherwise.

## 2. Title block content

Use the project's title block family or block unchanged; do not modify its structure (NY OGS rule) [S22]. Fill it from parameters or attributes, never with loose text [S10].

| Field | Notes |
|---|---|
| Project name, number, address | From project information |
| Client / owner | |
| Practice and consultants | Logos and contact blocks |
| Sheet title | "LEVEL 02 FLOOR PLAN", not "OVERALL" [S22] |
| Sheet number | Per section 3 |
| Scale | "As indicated" when several scales are on the sheet |
| Date, drawn by, checked by, approved by | |
| Revision and revision history | From the revision tool [S22] |
| Status / suitability code | ISO 19650 projects (section 8) |
| File or container name | ISO 19650 projects: the full container name |
| North arrow, key plan | In the title block with on/off parameters (NY OGS) [S22] |
| Professional seal / stamp area | Where the jurisdiction requires it |

AutoCAD: title block attributes in a block separate from the border (ISU) [S10]; insert in paper space at 1:1.

## 3. Sheet numbering

### 3.1 US Uniform Drawing System (NCS)
Format: discipline designator (1-2 letters; hyphen fills an empty second position) + sheet type digit + two-digit sequence, e.g. `A-101`, `AD101`, `A-501` [S06].

| Digit | Sheet type [S04][S06] |
|---|---|
| 0 | General (symbols legend, notes) |
| 1 | Plans |
| 2 | Elevations |
| 3 | Sections |
| 4 | Large-scale views (enlarged plans, elevations, sections) |
| 5 | Details |
| 6 | Schedules and diagrams |
| 7 | User defined (often typical details) |
| 8 | User defined |
| 9 | 3D representations |

Owners override this: Missouri sets 7 = reflected ceiling plans [S09]; NY OGS uses its own type digits and a discipline-type-floor-sector pattern (A-111 = first floor, sector 1) [S22]. Gaps in the sequence are allowed so sheets can be inserted [S06]. Sheets run from general to specific within a discipline [S06].

Common discipline letters [S01]: G general, C civil, L landscape, S structural, A architectural, I interiors, Q equipment, F fire protection, P plumbing, M mechanical, E electrical, T telecommunications.

### 3.2 ISO 19650 (UK National Annex) container names
Fields: `Project-Originator-Functional-Spatial-Form-Discipline-Number`, e.g. `ABCDE-XYZ-ZZ-02-D-A-0001` [S14][S17].

| Field | Rule (DfE, aligned to the NA) [S14] |
|---|---|
| Originator | 3 characters per organisation |
| Functional breakdown | 2 characters: ZZ multiple, XX none, 00 site, 01… buildings |
| Spatial breakdown | 2 characters per level (e.g. 00, 01, ZZ, XX) |
| Form | D drawing, G diagram, I image, L list, M model, T text, V video/audio |
| Discipline | A architecture, I interior architecture (DfE allocation), S structural, M mechanical, E electrical, L landscape, Z multiple … |
| Number | Exactly 4 digits (project rule) |

Older projects use the 2018 field names and two-character type codes (`DR` drawing, `M3` 3D model) [S17][S83]. Status and revision are metadata, not part of the name, except for files held outside the CDE [S14][S21]. The project's information standard is the authority.

## 4. Set order and drawing index

- US discipline order (UDS): Cover, G, H, V, B, C, L, S, A, I, Q, F, P, D, M, E, W, T, R, X, Z, O [S06]. NY OGS uses a variant [S22].
- Within architecture: general (0), plans (1), elevations (2), sections (3), enlarged (4), details (5), schedules (6), user-defined (7, 8), 3D (9).
- The drawing index (sheet list) is a schedule generated from the sheets, never typed. NY OGS generates it from a coordination model with "include elements in links" and sorts with sheet discipline and sort-order parameters [S22].
- AutoCAD: index as a table filled from layout or sheet-set properties.

## 5. Placing views and viewports

Rules:
1. Every viewport sits inside the title block's drawable area, clear of the title strip, notes column and margins.
2. Plans of different levels go in the same position on every sheet, with grids aligned, so the set can be flipped through.
3. One scale per viewport; scale stated in the view title.
4. View titles centred under the view (NY OGS) or left-aligned per standard; title line runs to the end of the view name or scale bar [S22].
5. Keep views from touching; leave room for annotation that extends past the crop (grid bubbles, dimensions).
6. Lock viewports after setting scale (AutoCAD) [S32][S33].
7. Split large plans with dependent views and scope boxes, with match lines and a key plan [S22].
8. Arrange details in a grid on detail sheets, numbered consistently (left to right, top to bottom, or the reverse per standard).
9. Use the space; avoid large blank areas and avoid crowding against the border [S44].

## 6. Scales by drawing type

| Drawing | Metric (DfE purposes [S14]; others typical) | Imperial (typical practice) |
|---|---|---|
| Site plan | 1:200, 1:500, 1:1250 | 1" = 20', 1" = 30', 1" = 50' |
| General arrangement plans | 1:100 (shape, layout) [S14] | ⅛" = 1'-0" |
| Plans for setting out, junctions | 1:50 (junctions) [S14] | ¼" = 1'-0" |
| Enlarged plans (toilets, stairs, kitchens) | 1:20, 1:50 | ½" = 1'-0", ¼" = 1'-0" |
| Elevations, building sections | 1:100, 1:50 | ⅛" or ¼" = 1'-0" |
| Wall sections | 1:20 | ¾" = 1'-0" |
| Details | 1:20, 1:10, 1:5, 1:2 (assembly details) [S14] | 1½" = 1'-0", 3" = 1'-0" |
| Interior elevations | 1:50, 1:20 | ¼" or ½" = 1'-0" |
| Models | 1:1 always [S14] | 1:1 |

Only approved scales; never custom viewport scales such as 1:1328.4 [S44].

## 7. Revisions

- Use the application's revision tool: revision clouds with tags, a revision schedule in the title block. Never type revision text (NY OGS) [S22].
- Revit numbering: per project (a revision keeps one number on every sheet) or per sheet (sequential on each sheet). Changing it later renumbers existing, even issued, revisions, so decide before entering any [S67]. NY OGS requires per sheet [S22].
- Every changed item gets a cloud on the sheet where it appears, tagged with the revision it belongs to. Remove or hide clouds of earlier revisions per the office rule, but keep the revision history line.
- ISO 19650 revision codes: `P01.01` work in progress, `P01` shared (preliminary), `C01` published (contractual) [S15][S14].
- AutoCAD: clouds on the revision layer (`*-ANNO-REVC` [S09]), triangle tag block with a REV attribute, and the revision block in the title block updated on every change [S10].

## 8. Status and issue codes

ISO 19650 UK NA suitability codes [S15]:

| State | Code | Meaning |
|---|---|---|
| Work in progress | S0 | Initial status |
| Shared | S1 | Suitable for coordination |
| | S2 | Suitable for information |
| | S3 | Suitable for review and comment |
| | S4 | Suitable for stage approval |
| | S6 | Suitable for PIM authorization |
| | S7 | Suitable for AIM authorization |
| Published | A1, A2 … | Authorized and accepted |
| | B1, B2 … | Partial sign-off with comments |
| Published (record) | CR | As constructed record |

S5 is withdrawn. Clients redefine codes (DfE redefines S4/S5 and adds stage-linked A0-A6, B0-B5) [S14]. US practice uses issue descriptions instead (e.g. "Schematic Design", "Design Development", "Construction Documents", "Issued for Permit", "Issued for Construction", "Addendum 01", "Bulletin 01"); take the project's list.

## 9. Checks

| Check | Signal |
|---|---|
| Sheet numbers follow the scheme | Number fails the project pattern; duplicates; wrong type digit for content |
| Index matches the set | Sheet in set but not in index, or the reverse |
| Viewports inside drawable area | Viewport outline outside the title block's printable zone |
| Viewport scale approved | Scale not in the project list |
| Viewports locked (AutoCAD) | Unlocked viewport |
| Plans aligned across sheets | Same grid intersection at different sheet positions |
| Title block fields filled | Empty required fields |
| Revision clouds tagged and listed | Cloud with no tag; revision in schedule with no cloud |
| Container names valid (ISO 19650) | Name fails the field pattern |
