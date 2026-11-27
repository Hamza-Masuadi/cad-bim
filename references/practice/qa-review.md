# QA review: how a senior checks a set and a model before issue

Read this before reviewing a drawing set, a DWG, or a Revit model, and before reporting findings. The mistakes catalogues (`mistakes-autocad.md`, `mistakes-revit.md`) list the individual checks; this file says in what order to look, how to rank what you find, and what to fix versus report.

Contents: 1 Principles · 2 Review order for a drawing set · 3 Review order for a Revit model · 4 Review order for a DWG · 5 Severity · 6 Fix or report · 7 Model health indicators · 8 Report format

## 1. Principles

- Review against the project's standard first, then against professional defaults. A finding needs a rule it breaks.
- Look at the whole before the parts: a wrong scale or a missing level ruins more than a hundred untidy tags.
- Measure, do not guess: counts, ids, sheet numbers, values. A finding without a location is not actionable.
- Separate errors that change what gets built from errors that change how the file behaves and from cosmetic issues.
- Never "fix" design intent quietly. A dimension that does not add up is a question for the designer, not an edit.

## 2. Review order for a drawing set (printed or PDF)

1. **Set completeness**: index vs sheets present; every sheet in the index exists and vice versa; sheet numbers follow the scheme; set order correct (`sheets-sets.md`).
2. **Title blocks**: project data, sheet number and title, scale, date, status/suitability, revision current and consistent with the revision schedule.
3. **References resolve**: every section, elevation, detail and keynote reference points to a drawing that exists with the right number. Broken references are among the most damaging errors in a set.
4. **Plans first, level by level**: grids and levels consistent across all sheets; north arrow consistent; plans aligned in the same sheet position.
5. **Dimensions**: strings complete and adding up, no overridden values, rounding per standard, elements located once (`dimensions-annotation.md`).
6. **Openings and rooms**: every door and room tagged, numbers unique, schedules matching plans.
7. **Sections and elevations**: levels and heights agree with plans; stair headroom and riser counts plausible.
8. **Graphic quality**: line weight hierarchy, text heights, hatches, nothing overlapping, legibility at reduced size [S14].
9. **Notes**: general notes present; no contradictory notes; keynotes resolve to the legend.
10. **Code-sensitive items to flag** (not certify): clear widths, stair geometry, guard heights, accessible clearances, with the jurisdiction's values.

## 3. Review order for a Revit model

1. **Open safely**: on a copy or a detached test copy for audit; never on central without the user's say (`worksharing-collaboration.md`).
2. **Setup**: units, project information, coordinates (base point, survey point, distance from origin) [S27], levels and grids pinned and consistent [S22].
3. **Warnings**: total count, grouped by type; critical ones first (`mistakes-revit.md` RV-003). Practitioners treat hundreds as a concern and 700-1,000+ as slowing the model [S23][S39][S36]; the project sets the gate.
4. **Links and imports**: CAD imported rather than linked, unpinned links, links on wrong worksets, absolute paths, phase mapping [S22].
5. **Model content**: in-place families, generic models, groups, design options, detail lines faking model elements, mass instances [S22][S23].
6. **Rooms and areas**: unplaced, not enclosed, redundant, duplicates, separation lines on walls [S79][S23].
7. **Data**: duplicate marks, empty required parameters, wrong categories, shared parameter consistency.
8. **Views and sheets**: templates on all sheet views, hidden elements, element overrides, views not on sheets, naming [S91][S22].
9. **Worksharing**: elements on wrong worksets, workset count, ownership by inactive users [S22][S87].
10. **Exports** if part of the issue: IFC from Fine views, proxies and NOTDEFINED [S59][S14].

NY OGS's issuance sequence is a good public model [S22]: back up, open with audit, review links and remove imports, purge unused, remove WIP views and sheets, tidy levels and grids, check overlapping or misplaced geometry, set detail levels, check properties and classification, check worksets.

## 4. Review order for a DWG

1. Units and `INSUNITS`; drawing at 1:1 [S32][S10].
2. Xrefs: found, relative, on their layer, nesting depth [S54][S48].
3. Layers: standard names; nothing on 0 or Defpoints; properties ByLayer [S32][S48].
4. Layouts: viewports scaled to approved scales, locked, inside the title block; title block attributes filled [S32][S10].
5. Annotation: text heights per scale, annotative scales present, dimensions not overridden or exploded [S10][S32].
6. Geometry: duplicates, Z values, open room polylines, exploded blocks and hatches [S32][S42].
7. File hygiene: purge count, audit errors, plot style attached [S09][S43].

## 5. Severity

Use these three levels. When in doubt between two, pick the higher and say why.

| Severity | Definition | Examples |
|---|---|---|
| **Critical** | The information is wrong or missing in a way that could be built wrong, priced wrong, breach a code requirement, or lose or corrupt data, or the file cannot be used by the recipient | Overridden dimension values; wrong viewport scale; broken section/detail references; wrong units causing scaled inserts; model far from origin or on wrong coordinates; duplicate door numbers on an issued schedule; unenclosed rooms in an area schedule used for leasing; IFC missing elements |
| **Major** | The file breaks the standard or the team's workflow, or will cause errors downstream, but the printed information is still right today | Content on layer 0 or Defpoints; properties not ByLayer; imported CAD; views without templates; in-place families; unpinned grids; warnings above the gate; xrefs with full paths |
| **Minor** | Craft and tidiness; no effect on what is built or on others' workflow | Unused layouts; non-standard but legible text style; default view names on working views; untidy tag placement |

Raise severity when: the item is on an issued sheet; it repeats across many elements; it hides another problem. Lower severity when the project standard explicitly accepts it.

## 6. Fix or report

| Situation | Do |
|---|---|
| User asked for an audit only | Report everything; fix nothing |
| User asked to fix a category of findings (e.g. "set everything ByLayer") | Fix that category, re-run the check, report before and after counts |
| Fix is mechanical and changes no design information (layer names, ByLayer, purge, pin, rename views, assign templates) | Fix if asked to clean up; list what changed |
| Fix changes geometry, dimensions, sizes, room boundaries, phase, or data others rely on | Report and propose; fix only after the user agrees to that specific change |
| Fix deletes elements | Report with counts and ids; delete only what the user approves; prefer the least destructive option |
| Fix touches elements owned by others or shared files | Report; never fix without ownership and the user's approval |
| Finding is a code-compliance concern | Report with the cited value and jurisdiction; never claim compliance |

## 7. Model health indicators

| Indicator | Why it matters | Source of indicator |
|---|---|---|
| Warnings (count, critical vs other, trend) | Performance, hidden errors | [S24][S23] |
| File size and its trend | Open and sync times | [S24][S46] |
| Sync times | Team productivity | [S24] |
| Revit links, CAD links, CAD imports, imported images | Weight and risk | [S24] |
| In-place families | Bloat | [S24] |
| Plan regions | Performance | [S24] |
| Model groups, detail groups | Overhead | [S46][S23] |
| Total elements, views not on sheets | Clutter | [S46] |
| Line styles, fill patterns, view templates vs firm template | Template drift | [S46] |
| Distance from internal origin | Precision | [S27] |
| Design options at late stages | Computation | [S23] |

Thresholds are set by the project or the BIM manager. Autodesk publishes the indicators but no thresholds [S24][S25]. Practitioner figures, given only as such: warnings "can pile up to 700 to 1,000" and slow the model [S23]; a practitioner aims for "a few hundred at worst" [S39]; "thousands" is the problem level [S36]; element offset from level ≤ 200 mm and walls spanning no more than two levels in one published custom checkset [S31]; geometry within 20 miles (32 km) of the origin is an Autodesk figure [S27]. No authoritative file-size threshold exists [S25].

Audit/compact cadence is disputed among experts; do not push routine audits as dogma [S86].

## 8. Report format

For each finding give: id (e.g. `RV-012`), severity, rule broken (with source or "project standard"), count, where (sheet/view/layer/element ids), fix proposed, and whether you fixed it. Lead with a three-line summary: counts by severity, the top three critical items, and what was not checked.
