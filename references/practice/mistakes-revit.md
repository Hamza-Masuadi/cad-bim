# Revit: junior mistakes catalogue

Read this when auditing or cleaning a Revit model, or before issuing from one. Each entry has a stable id (`RV-001` …) that machine-readable checks use; never renumber. Entries are grouped by topic, so ids are not in numeric order; there are 53 (RV-001 to RV-053), none skipped. Severity is professional judgment (see `qa-review.md` section 5): **C** critical, **M** major, **m** minor. Detection: **Auto** = a check can decide; **Heur** = heuristic for a human to confirm; **UI** = an app setting, not in the model. Thresholds marked "project" come from the project or BIM manager; practitioner figures are given only as such.

Contents: 1 Families and content · 2 Warnings and data · 3 Views and annotation · 4 Links, coordinates, worksharing · 5 Modelling · 6 Rooms and spaces · 7 Exports and issue

## 1. Families and content

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-001 | Overusing in-place families | Each copy is a new definition; bloat; cannot become loadable | Count of in-place families; several with identical geometry | Replace with a loadable family | M | Auto | S23 S35 S36 S22 S37 |
| RV-002 | In-place element on a wrong or generic category | Missing from schedules and tags; wrong IFC | In-place family in Generic Models | Recategorise or rebuild as loadable in the right category | M | Auto | S22 S70 |
| RV-023 | Overusing groups; arrays left grouped | Overhead, odd behaviour, bloat | Model and detail group counts; unused group types; array groups | Ungroup where not needed; purge unused group types | M | Auto | S23 S35 S36 S25 |
| RV-025 | Over-modelling small detail | File weight and time for no value | Family file size and geometry complexity; solids under ~100 mm / 4" (practitioner guidance) | Simplify; use symbolic lines and detail levels | m | Heur | S34 S35 S36 |
| RV-026 | Unvetted manufacturer or downloaded families | Bloat, junk and advertising parameters | Large family files; high parameter counts; non-standard parameter names | Clean or replace with office content | M | Heur | S34 S35 |
| RV-027 | Duplicated types not renamed ("Wall 2 2 2") | Type chaos; wrong selections | Type names with default copy suffixes or repeated trailing numbers | Rename to the naming standard; merge duplicates | m | Auto | S37 |
| RV-035 | Mass families used for non-massing elements | Wrong category and schedules | Mass instances in late-stage models | Replace with proper elements | M | Auto | S22 |
| RV-039 | Wrong family category | Missing from schedules, tags, filters; wrong IFC/COBie | Category vs name keywords; a "Generic Models - To Reclassify" schedule | Change category in the family or rebuild | M | Heur | S70 |
| RV-040 | Nested families not shared when they must be scheduled | Sub-components cannot be tagged or scheduled | Nested family not shared while schedules need it | Make the nested family shared, reload | m | Heur | S69 |
| RV-041 | Copy/paste between projects leaving orphaned families | Duplicates and renamed types | Families or types with "1"/"2" suffixes after paste | Merge to the original types; purge | m | Heur | S75 |
| RV-042 | Over-populated template | Starting bloat | Loaded family count or unused families in a new project | Strip the template; load on demand | m | Auto | S36 S25 |
| RV-050 | Non-parametric families or text disguised as tags in received models | Model cannot be scheduled or maintained | Families with no type parameters; text notes near untagged elements | Report to the sender; replace where agreed | M | Heur | S38 |

## 2. Warnings and data

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-003 | Ignoring warnings | Performance and stability loss; hidden errors | Warning count and types, trended (practitioners cite 700-1,000+ as harmful; project sets the gate) | Triage by type; fix the top types first | M | Auto | S23 S35 S36 S39 |
| RV-005 | Duplicate Mark or Number values | Schedules and tags ambiguous | Duplicate value warnings; duplicate Mark scan | Renumber per the scheme | C | Auto | S23 S39 |
| RV-006 | Elements slightly off axis | Joins, dimensions and rooms misbehave | "Slightly off axis" warnings; directions within a small angle of 0/90° | Rotate to exact angles | M | Auto | S39 |
| RV-053 | Too many unresolved warnings at issue (no QA gate) | Poor deliverables; audits fail | Warnings above the project gate at publish | Set a gate; triage before issue | M | Auto | S84 S36 |

## 3. Views and annotation

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-007 | Hide in View instead of filters and templates | Invisible state confuses others | Views with elements hidden by element | Unhide; control with filters or templates | M | Auto | S37 S34 S47 S91 |
| RV-008 | Graphic overrides and filled regions to fake the model | Drawings do not match the model; brittle | Per-element override counts; filled regions over model elements | Fix the model or the type; remove overrides | M | Heur | S47 |
| RV-009 | Typed text instead of tags or dimensions | Annotation goes stale | Text notes matching mark or room-name patterns | Replace with tags | C | Heur | S34 S38 |
| RV-010 | Text legends or plan notes instead of keynotes | Numbering errors | Legends with numbered text; no keynote tags | Move to keynotes or schedule-driven notes | m | Heur | S34 |
| RV-011 | Detail lines drawing model things (stairs, walls) | Absent in 3D, schedules, coordination | Detail or model lines in plan views forming stair or wall patterns | Model the element | M | Heur | S34 |
| RV-028 | Unnamed or temporary views piling up | Browser clutter; file weight | Default names ("Section 1", "Copy of"); views not on sheets | Rename or delete with approval | m | Auto | S23 S35 S34 |
| RV-029 | Sheet views without view templates | Inconsistent graphics | Views on sheets with no template | Assign the right template | M | Auto | S91 S22 |
| RV-045 | Revit's 1/256" dimension precision left on | Unbuildable precision on drawings | Dimension type rounding finer than ½" (imperial architectural) or the metric rule | Set rounding per `dimensions-annotation.md` | M | Auto | S49 |
| RV-046 | Annotating on sheets instead of views | Notes detach from the views they reference | Text notes and detail items owned by sheets | Move into the views | m | Auto | S22 |
| RV-047 | Entourage and planting in documentation views | Junk in construction documents; slow | Entourage/planting visible in sheet views | Hide via template or workset | m | Auto | S34 |

## 4. Links, coordinates, worksharing

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-012 | Importing CAD instead of linking | Memory and corruption risk; junk styles | Imported (not linked) CAD instances | Delete import; link instead | M | Auto | S22 S35 S36 S34 S37 |
| RV-013 | Exploding CAD in the project | Thousands of line styles, fills, text types | Spike in line style, fill pattern and text type counts; names matching DWG layers | Purge; rebuild from a link | M | Auto | S34 S36 |
| RV-014 | CAD links unpinned, off the CAD workset, absolute path | Accidental moves; no visibility control; broken paths | Unpinned links; link not on the link workset; full paths | Pin; move to workset; relative paths | M | Auto | S22 |
| RV-015 | Revit links not placed per the agreed basis, not relative, not on their own workset | Misaligned federated models | Link transform vs agreed basis; path type; workset | Re-place by shared coordinates or origin per BEP; fix path and workset | C | Heur | S22 S28 |
| RV-016 | Building far from the internal origin | Precision and coordinate problems | Geometry more than 20 mi / 32 km from internal origin (Autodesk figure) | Relocate with the BIM coordinator; never unasked in a shared model | C | Auto | S27 S34 |
| RV-017 | Grids and levels unpinned; moved or rotated by accident | Whole model shifts | Unpinned grids and levels | Pin; check positions against the structural model | M | Auto | S34 S45 |
| RV-018 | "Drag elements on selection" left on | Accidental moves | n/a (UI setting) | Advise the user to turn it off | m | UI | S34 |
| RV-019 | Worksets used like layers | Complexity without benefit | Workset count above project norm; layer-like names | Consolidate; use filters and templates | m | Heur | S34 |
| RV-020 | Elements on the wrong workset | Visibility, loading and permissions wrong | Category vs workset rules (links not on link worksets; furniture not on FF&E) | Move elements (needs ownership) | M | Heur | S22 S75 |
| RV-043 | Not syncing or relinquishing; working directly in central | Borrowing conflicts; lost work | Elements owned by inactive users; long gaps between syncs | Advise the user; never sync or relinquish unasked | M | Heur | S29 S87 |
| RV-044 | Team on different Revit builds | Worksharing problems | Build numbers in the worksharing log | Align builds | M | Auto | S28 |
| RV-049 | Copy/Monitor coordination alerts ignored | Levels and grids drift between disciplines | Pending coordination review items | Review and resolve each alert | M | Heur | S22 |

## 5. Modelling

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-021 | Wrong phase; unmapped link phases | Existing/new shown wrong; quantities wrong | Phase created/demolished vs view phase; link phase mapping unset | Correct phases; map link phases | C | Heur | S22 |
| RV-022 | Design options left after decisions | Extra computation; confusion | Option sets present at late stages | Accept primary, delete options (with approval) | m | Auto | S23 S36 |
| RV-024 | Over-constraining; locked dimensions in the project | Recalculation overhead; constraint errors | Locked dimension constraint count; "constraints not satisfied" warnings | Remove project-level locks | m | Heur | S23 S35 S36 S22 |
| RV-030 | Walls spanning many levels | Slow updates; wrong level association | Base and top constraints more than two levels apart (published checkset) | Split walls at levels | M | Auto | S31 S25 S22 |
| RV-031 | Elements placed far off their level | Level schedules and coordination wrong | Offset from host level above project threshold (200 mm in one published checkset) | Re-host to the right level | M | Auto | S31 S22 |
| RV-032 | Wall openings used as windows | No window data; wrong schedules | Openings in walls with no hosted door or window | Replace with window families | M | Heur | S22 |
| RV-033 | Ordinary door families substituted into curtain panels | Wrong categories and behaviour | Door category in curtain-wall panel positions not using the project's curtain door family | Use the curtain-wall door panel family | m | Heur | S22 |
| RV-034 | Walls attached to floors or roofs where the standard forbids it | Unwanted dependencies | Walls with top or base attached to floors or roofs | Detach; set constraints or profiles (only where the owner rule applies) | m | Auto | S22 |
| RV-036 | Deleting and redrawing instead of modifying | Loses element identity; orphans hosted items | Element ids churn between versions; orphaned hosted elements | Edit existing elements | M | Heur | S36 |

## 6. Rooms and spaces

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-004 | Room separation lines overlapping walls | Ambiguous room boundaries; a top warning | Warning "A wall and a room separation line overlap" | Delete the separation line | m | Auto | S23 S39 |
| RV-037 | Unenclosed or redundant rooms | Wrong areas; "Not enclosed" in schedules | Room warnings (not enclosed; multiple rooms in one region); unplaced rooms | Close boundaries; delete or place rooms (approval for deletes) | C | Auto | S79 |
| RV-038 | Duplicate spaces; unassigned MEP components | Loads and systems wrong | Several spaces at one room location; components in no system | Report to the MEP author; fix in own model only | M | Auto | S75 |

## 7. Exports and issue

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| RV-048 | Too many imported images and plan regions | Health penalties; slow | Raster image and plan region counts above project norm | Remove unused images; merge plan regions | m | Auto | S24 S23 S25 |
| RV-051 | IFC exported from views at Coarse or Medium | Elements silently missing from IFC | Export view detail level not Fine | Export from a Fine 3D export view | C | Auto | S59 S22 |
| RV-052 | No IFC class mapping: proxies and NOTDEFINED | Downstream tools cannot classify elements | Proxy and NOTDEFINED counts in the export; custom families without IFC export class | Map categories; set IFC export class and predefined type | M | Auto | S59 S14 S58 |
