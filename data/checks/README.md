# Audit checks

| File | What |
|---|---|
| `autocad.json` | 63 checks for the 48 mistakes in `references/practice/mistakes-autocad.md` (49 automatic, 14 review by eye). |
| `revit.json` | 67 checks for the 53 mistakes in `references/practice/mistakes-revit.md` (45 automatic, 22 review by eye). |
| `FACTS.md` | The contract for engine builders: every facts table and field the checks read, and the evaluators SA.Core does not have yet. |

Both files follow `data/schemas/checks.schema.json` and are described in `data/FORMATS.md` section 2. Validate a
checks file with `sa checks lint <file>`; the core tests check both against the schema.

## Ids

Every mistake of the practice guides has at least one check, and every check id starts with the mistake id in lower
case: `ac-001.layer-zero`, `rv-037.rooms`. The schema requires lower-case dotted ids, so `AC-001` itself cannot be
an id; the part before the first dot is always the guide's id, never renumbered. A mistake with several independent
signals has one check per signal (`ac-003.color-bylayer`, `ac-003.linetype-bylayer`, `ac-003.lineweight-bylayer`),
and a mistake that is only partly automatic has an automatic check for the part that can be decided plus a
`manual_review` check for the rest (`ac-034.dimlfac` and `ac-034.not-full-size`). A report groups findings by the
id prefix to show one line per mistake.

## Automatic checks and review by eye

- **Automatic** checks use the neutral evaluators of SA.Core where they fit, and the new evaluators listed in
  `FACTS.md` section 3 where they do not. A check whose facts table or evaluator an engine lacks reports
  `not_implemented`, never pass. A check whose facts table the engine cut (a size limit, unreadable values) reports
  `incomplete` instead of pass.
- **Review by eye** checks use the `manual_review` evaluator and carry the group `manual`. They always return one
  finding saying what to look at; report writers list them in a separate "review by eye" section and leave them out
  of pass and fail counts. A mistake is review by eye when its guide signal is a heuristic with no precise test
  (exploded blocks and dimensions, fudged geometry), a process (eTransmit), a UI setting (Revit "drag elements on
  selection"), or needs data outside the open file (worksharing logs, other files, the office template). Accuracy
  wins over coverage: a vague automatic signal would be worse than an honest review item.

## Groups

Run a subset with `sa <acad|revit> audit --group <name>`. Topic groups: `setup`, `units`, `layers`, `properties`,
`plotting`, `annotation`, `styles`, `blocks`, `xrefs`, `layouts`, `sheets`, `geometry`, `cleanup`, `rooms`, `health`,
`issue` (AutoCAD); `families`, `data`, `warnings`, `views`, `annotation`, `sheets`, `links`, `coordinates`,
`worksharing`, `modelling`, `rooms`, `health`, `export`, `setup`, `cleanup`, `issue` (Revit). Kind groups on top:

| Group | Meaning |
|---|---|
| `manual` | Review-by-eye item (`manual_review`). |
| `heuristic` | Automatic, but the signal finds candidates a person should confirm (layers named THIN, typed text that looks like a tag). |
| `report-only` | A count or threshold with no authoritative source: it reports, and the project sets the limit. |
| `issue` | Worth running before every issue. |
| `standards` | Depends on the project standard's naming. |

## How severity is assigned

The guides grade each mistake C (critical), M (major) or m (minor) using `references/practice/qa-review.md`
section 5. The checks map them as follows:

| Guide | Check severity | When |
|---|---|---|
| C | `critical` | The signal decides the mistake. |
| M | `error` | The signal decides the mistake. |
| m | `warning` | The signal decides the mistake. |
| any | one step lower, at most `warning` | `heuristic` checks: the finding is a candidate, not a verdict. |
| any | `info` | `report-only` checks, and checks whose harm depends on facts the model does not hold (project stage, owner rules). |
| any | the guide's level | `manual_review` items: the person reviewing decides. |

Raise severity in the project's copy for issued sheets or widespread occurrence (qa-review.md section 5).

## Thresholds with no authoritative source

Warning counts, file size, layout counts, group counts, offsets from level and the like have no published limit
from Autodesk or a standards body; the figures in the guides come from practitioners or one published checkset.
Such checks ship **report-only**: severity `info`, group `report-only`, and either `max` 0 (so any occurrence is
reported with its count, for example `rv-053.warning-gate`) or the cited figure with the source in the check's
`source` (for example 200 mm in `rv-031.offset-from-level`). The project sets its own limit and severity. No check
measures file size, because there is no figure to defend.

## How a project changes checks

The engine loads one checks file per app and refuses a check id defined twice, so a project works on its own copy:

1. Copy `autocad.json` or `revit.json` into the project's folder and have the engine load that copy: the `audit`
   request takes `checksFile` (and `standard`) params, which replace the bundled checks (or add to them with
   `checksMode: "add"`); see `data/FORMATS.md` section 2. A job's `verify` takes the same `checksFile` and
   `checksMode`.
2. **Turn a check off**: delete it from the copy, or run with `--group` / `--checks` to select what to run.
3. **Change a threshold**: edit the check's `params` (`max` in `rv-053.warning-gate`, `imperialMinIn` in
   `ac-011.dim-precision`, `equals` in `ac-046.psltscale` for the Illinois State linetype method).
4. **Change severity**: edit `severity` (for example raise `rv-053.warning-gate` to `error` once the gate is set).
5. **Project naming rules** live in the standard, not in the checks: layer patterns (`patterns.dimensionLayer`,
   `wallLayer`, `areaLayer`, `titleBlockLayer`, `widthAllowedLayer`, `splineAllowedLayer`), workset patterns
   (`cadLinkWorkset`, `revitLinkWorkset`, `ffeWorkset`), the IFC export view pattern (`ifcExportView`), and code
   lists (`plotColours`, `xrefLayers`, `approvedFonts`). A standard used with these checks must define all of them;
   both presets in `data/standards/` do.

Keep the check ids unchanged in the copy so reports stay comparable across projects.
