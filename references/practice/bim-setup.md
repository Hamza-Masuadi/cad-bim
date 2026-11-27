# BIM project setup (Revit)

Read this before starting or restructuring a Revit project: template, levels, grids, coordinates, links, worksets, phases, design options, browser and view naming, model splitting. Many of these decisions cannot be undone cheaply; when the snapshot does not settle them, ask (see `asking-the-user.md`).

Contents: 1 Template · 2 Levels and grids · 3 Coordinates · 4 Linking · 5 Worksets · 6 Phases · 7 Design options · 8 Browser and view naming · 9 Starting view · 10 Model splitting · 11 Checks

## 1. Template

- Start from the office or client template, never from a previous project file (it carries old content and errors).
- Keep templates minimal: load only families used on every project [S25][S36]. A heavy template is a heavy model from day one (RV-042).
- A template holds: units and rounding, line weights and patterns, object styles, materials, fill patterns, text/dimension/tag types, view templates and filters, browser organisation, sheet and title block, schedules templates, project parameters, shared parameter file reference, starting view.
- Check units first: length unit, rounding, area and volume units must match the project standard. Revit stores internally in feet; always convert through the app's units, never with a fixed factor.

## 2. Levels and grids

| Rule | Why |
|---|---|
| One level per floor someone builds to; one level per slab (NY OGS) [S22] | Levels drive plans, schedules by level, and constraints |
| Levels and grids owned by structure and copy-monitored into the other models, on a shared levels and grids workset [S22] | One source; clear Coordination Review warnings [S22] |
| Pin levels and grids once placed [S34][S45] | An accidental move shifts the whole model |
| Name levels consistently ("L00", "L01" or "LEVEL 1"), with FFL/SSL in the name if both exist | See `floors-ceilings-roofs.md` |
| Grids: numbers one way, letters the other; skip I and O (typical practice) | Avoid confusion with 1 and 0 |
| Grid extents and bubbles consistent; use scope boxes to control extents per view | Clean sheets |
| Do not delete and redraw levels or grids | Every hosted element and view loses its reference |

## 3. Coordinates

| Point | What it is | Rule |
|---|---|---|
| Internal origin | Fixed origin of the file | Keep the building near it; geometry within 20 miles (32 km) of it [S27] |
| Project base point | Project reference, often a grid intersection or building corner | Place the building near the internal origin, e.g. at a grid intersection [S34] |
| Survey point | Link to the real-world coordinate system; carries the civil/survey coordinates; true north defines its Y axis [S27] | Set from the surveyor's data with Specify Coordinates at a Point; pin it [S27][S82] |

Shared coordinates:
- One file defines shared coordinates for all others (usually the site or architectural model). **Acquire** when the coordinates live in the link; **Publish** when they live in the host [S82]. Pin both points [S82].
- Link the survey DWG "Auto - By Shared Coordinates" [S27][S82].
- Owner practice varies: NY OGS links models origin-to-origin with relative paths [S22]; DfE requires internal, project and survey origins to coincide geographically, on the national grid, true-north oriented [S14]. Ask which applies.
- Never move the building to fix a coordinate problem in a workshared model without the BIM coordinator.

## 4. Linking strategy

- Link, do not import, CAD and Revit files [S22][S35]. Imports bloat and carry junk styles (RV-012).
- Each link on its own workset (e.g. `X_Revit Link`, `X_CAD Link`), not visible by default unless needed [S22][S28].
- Pin links; use relative paths where the project allows [S22].
- Linked CAD for backgrounds: link in the current view only when it is a 2D reference for one view.
- Check phase mapping for every Revit link [S22].
- Room bounding of linked walls: turn on in the link instance only if the link should bound rooms.
- Do not explode CAD in the project (thousands of line styles and fills) [S34][S36].

## 5. Worksets

- Group elements edited together; you do not need a workset per floor [S28]. Worksets are not layers: keep them few and control visibility with view templates and filters [S34].
- Close unneeded worksets rather than hiding them [S28].
- Prefer element borrowing to checking out whole worksets [S28].
- Everyone on the same Revit build [S28].
- Example set (NY OGS, `Originator_TradeCode_Description`) [S22]: `OGS_A_Exterior`, `OGS_A_Interior`, `OGS_A_FFE` (not visible by default), `OGS_X_CAD Link`, `OGS_X_Revit Link`, `OGS_X_Point Cloud`, `OGS_X_Coordination`, `OGS_Y_Shared Levels and Grids`, `OGS_Z_Hidden`.
- FF&E on its own workset, hidden by default [S22]; entourage on hidden worksets or design options so it never reaches CDs [S34].
- See `worksharing-collaboration.md` for ownership and sync rules.

## 6. Phases

- Standard phases: Existing, then one or more construction phases. Phase names consistent across all models and matching package names [S22].
- Elements carry Phase Created and Phase Demolished; views carry a phase and a phase filter (e.g. "Show Previous + New", "Show All", "Show Complete"). Phase status decides graphics (existing, demolished, new, temporary).
- Use phases for time, not for design alternatives (that is design options) and not for graphic tricks.
- Check link phase mapping [S22].

## 7. Design options

- For local alternatives (an entrance, a stair, a façade bay), not for whole schemes [S36].
- Delete options once decided; accepted options should be made primary and the set accepted, so the model stops computing them [S23] (RV-022).
- Autodesk has a "Plan Efficient Design Options" topic [S25].

## 8. Project browser and view naming

- Separate working and coordination views from documentation views (on sheets) in the browser [S22]. A common split is by a view parameter such as "View Use" or "Sub-Discipline" (names vary by template).
- Use number prefixes in view names for sort order [S34].
- View naming (NY OGS): `LEVEL DISCIPLINE [DEMO] VIEWTYPE`, e.g. "FIRST FLOOR HVAC DEMO PLAN" [S22]. Sheet names follow "FIRST FLOOR (DISCIPLINE) PLAN" [S22].
- No default names ("Section 1", "Copy of Level 1", "Level 1(2)"); rename on creation (RV-028) [S23][S34].
- Working views: prefix with the user or "WIP", and delete before issue [S22].

## 9. Starting view

Set a lightweight drafting view (project information as text, links to standards) as the starting view; it speeds file opening [S25].

## 10. Model splitting

- Split by building, tower, parking structure, or core/shell/interior [S36]. Engineering models that keep links loaded have different limits [S25].
- No public Autodesk file-size threshold exists [S25]. Practitioner figures (e.g. 300-500 MB) are commonly quoted but unverified; treat them as prompts to discuss, not rules.
- Split along real boundaries (movement joints, separate buildings, separate packages) and agree shared levels, grids and coordinates first.

## 11. Checks

| Check | Signal |
|---|---|
| Units match the standard | Project units differ from the project's declared standard |
| Levels and grids pinned, named, monitored | Unpinned; default names; Coordination Review items pending [S22] |
| Geometry near origin | Extents beyond 32 km (20 mi) from internal origin [S27] |
| Survey point set and pinned | Survey point at internal origin on a site project; unpinned |
| Links pinned, on link worksets, relative | Unpinned links; links on user worksets; absolute paths |
| No imported CAD | Imported (not linked) CAD instances |
| Worksets reasonable | Many layer-like worksets; elements on wrong worksets [S22] |
| Phases consistent | Phase names differ from linked models; unmapped link phases |
| Design options cleared at late stages | Option sets present at construction documents |
| View names clean | Default or "Copy of" names |
