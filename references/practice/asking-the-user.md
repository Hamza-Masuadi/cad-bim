# Asking the user

Read this at the start of any job whose snapshot does not settle the project conventions. A professional asks a few precise questions before drawing, because guessing a standard means redoing the work. Ask once per project, record the answers in the project's standards data, and reuse them.

## 1. How to ask

- First read the snapshot: units, layers or levels, styles or types, title block, existing sheets, existing tags and schedules. Infer what you can and **state the inference** ("Layers follow the US NCS format; I'll use it") rather than asking.
- Ask only what is still open and what changes the result. Group the questions in one message, each with a proposed default, so the user can answer "yes to all".
- Never ask what the user already told you in this session.
- If the user says "just do it", use the defaults below, list them in the report as assumptions, and mark affected results "assumed".

## 2. Questions and defaults

| Topic | Question | How to infer from the snapshot | Default to propose |
|---|---|---|---|
| Standard | Which CAD/BIM standard applies (client, office, national)? | Layer names pattern; sheet numbers; parameter prefixes; file names | US NCS-style layers and UDS sheet numbers for imperial projects; ISO 13567 / ISO 19650 naming for metric UK/EU projects |
| Units | Metric (mm or m) or imperial (ft-in)? Display precision? | `INSUNITS`, `MEASUREMENT`; Revit project units | The file's own units; never convert a file's units unasked |
| Level datum | Are levels FFL or structural (SSL)? What is the project zero? | Level names; floor offsets; structural link levels | FFL levels, ground floor FFL = ±0.000 (metric) or 100'-0" (US practice); state it on sections |
| Wall types | Which wall types and thicknesses? Fire/acoustic ratings? | Types in the template; layer names; existing walls | Use existing project types only; propose new types by name and build-up, do not create silently |
| Setting-out reference | Dimension to face of finish, face of structure/board, or centreline? | Existing dimension strings | Face of structure, masonry or board, excluding applied finishes; clear dimensions to finish where codes measure clear [S49] |
| Numbering | Room numbering scheme? Door numbering (by room or sequential)? | Existing room and door marks | Rooms: level digit + two-digit sequence (101); doors: by room (101A) |
| Handing | Which door handing convention (BHMA, DIN, EN)? | Door schedule legend, existing handing values | Ask; if no answer, leave handing blank and report it |
| Scale and sheet size | Scale of each drawing and sheet size? | Existing layouts and title blocks | A1 / ARCH D; 1:100 or ⅛" plans, 1:50 or ¼" enlarged, details 1:5-1:20 or 1½"-3" |
| Title block | Which title block, and who fills it? | Blocks or families present | The one in the template; fill only fields with known data |
| Text and fonts | Standard text heights and font? | Text styles and types | 2.5 / 3.5 / 5 / 7 mm or 3/32" / ⅛" / 3/16" / ⅜"; the template's font |
| Plot style | CTB or STB, which table, colour or monochrome? | `PSTYLEPOLICY`; attached tables | The attached table; monochrome for construction sets |
| Annotation method | Annotative or per-scale text? Notes in model or paper space? | Existing annotative objects; notes location | Annotative in model space |
| Area basis | Which measurement standard and boundary rule? | Area schemes; existing area reports | Room areas at wall finish, labelled as such; ask before reporting gross/net |
| Code jurisdiction | Which building code and edition govern? | Project information, address | Ask; flag values with the closest verified source and say compliance is not checked |
| Phases | Existing/new phases? Is this demolition work? | Phases and phase filters | Existing + New Construction |
| Worksharing | Is the model workshared? Who owns what? May I sync? | Worksharing status, ownership | Never sync or relinquish; stop on elements owned by others |
| Coordinates | Shared/survey or origin-to-origin? | Survey point, links | Keep what the file has; never move base points unasked |
| Output naming | File naming rule and status/revision for this issue? | Existing exports, title block fields | ISO 19650 container name if the project uses it; otherwise SheetNumber_SheetName_Rev |
| Scope of fixes | Audit only, or also fix? Which categories? | — | Audit and report only |

## 3. Questions to ask before specific tasks

| Task | Must know before starting |
|---|---|
| Walls | Types, setting-out reference, location line rule, heights (to slab or ceiling) |
| Doors and windows | Types, sizes, numbering, handing convention, sill/head heights |
| Stairs and ramps | Floor-to-floor (FFL), stair category, code jurisdiction, width |
| Rooms | Names list, numbering scheme, area boundary rule |
| Dimensions | Reference faces, precision, string order the office uses |
| Sheets | Title block, sheet size, numbering scheme, scales, viewport layout |
| Schedules | Fields and order, sorting, which phase |
| Exports | Recipient's standard, coordinate basis, schema (IFC), naming, status |
| Audit and fix | Which standard to check against, severity gate, what may be changed |
| Fit-out | Base-build revision, demolition scope, finish codes, FF&E scope and responsibility (`interiors.md` 13) |
| Space planning | Brief (headcount, rooms, ratios), area basis, escape routes to keep, options wanted (`space-planning.md` 2) |
| Kitchens and bathrooms | Survey dimensions, products and cabinet codes, accessible or not, jurisdiction |
| RCP and lighting | Ceiling types and heights, grid module, fitting types from the engineer or lighting designer |
| Area measurement and facility data | Measurement standard, boundary rule, data fields and ids the client wants |
| Landscape plans | Survey and coordinate system, plant list and sizes, bed densities |
| Accessibility review | Jurisdiction and standard, scope of rooms, which drawings govern |

## 4. Recording answers

Write each answer into the project standards data with the date and who decided. In the job report, list the decisions used. When a later file contradicts an answer (e.g. a new title block), point it out and ask which wins.
