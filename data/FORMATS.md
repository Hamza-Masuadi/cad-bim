# Data formats

Five kinds of JSON file drive the engine. Each has a JSON Schema (draft 2020-12) in `schemas/` and a small,
complete example in `schemas/examples/`. Copy an example and edit it rather than starting from nothing.

Check a file before committing it:

```
python3 data/schemas/validate.py --all                                   # every example and data file
python3 data/schemas/validate.py data/schemas/standard.schema.json my-standard.json
sa job lint job.json                                                      # a job, including every op's args
```

The engine's own loaders are stricter than the schemas in a few places (they compile regular expressions, check
that code lists named by `codeList` exist, that a scale's name agrees with its ratio, and so on), so a file that
passes the schema can still be refused at load time. Their messages name the exact place, for example
`$.layers[4].lineweight: 0.33 is not a standard lineweight (mm)`.

General rules for every file:

- Strict JSON: no comments, no trailing commas, UTF-8. Unknown members are errors, so a misspelt key is caught.
- `"$schema"` may appear at the top of any file to point an editor at the schema; it is ignored otherwise.
- Lengths in jobs are in the job's unit; lengths in standards say their unit (`units.length`, or per entry).

---

## 1. Project standard (`standard.schema.json`)

One file per project or office standard. Examples: `standard.us.example.json` (US-style) and
`standard.iso.example.json` (ISO 13567 layers, ISO 19650 names).

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | Short identifier, e.g. `iso19650-uk`. |
| `name` | yes | Human name. |
| `version`, `description`, `jurisdiction`, `notes` | no | Free text. |
| `units.length` | no | Default model unit of the projects using it: `mm`, `cm`, `m`, `in` or `ft`. |
| `codeLists` | no | Named lists of codes that other sections refer to with `"codeList": "<name>"`, so a list is written once. |
| `layerNaming` | no | The layer naming scheme (below). |
| `layers` | no | The layer list. |
| `layerRoles` | no | The layer for each object the engines place without a layer from the job (below). |
| `textStyles`, `dimStyles` | no | Named styles. |
| `sheetNumbering`, `fileNaming`, `viewNaming` | no | Naming rules (below). `viewNaming` is for view names. |
| `statusCodes` | no | Suitability or issue status codes, e.g. `S2`, `CD`. |
| `revisionCodes` | no | Revision code formats. |
| `scales`, `textHeights` | no | Allowed drawing scales and plotted text heights. |
| `titleBlock` | no | The title block: block name, Revit type, field names. |
| `purge` | no | `keep[]`: names a purge must keep (below). |
| `patterns` | no | Named regular expressions that checks can use (`standardPattern`). |

### Codes

Wherever codes are listed, each entry is either a string (`"A"`) or an object
`{ "code": "A", "name": "Architectural", "description": "..." }`. Codes are case-sensitive and must be unique in
their list. A field gives its codes inline with `"codes": [...]` or by reference with `"codeList": "disciplines"`,
never both.

### `layerNaming` with `"scheme": "us"`

Delimited names: discipline designator, major group, up to N minor groups, optional status. `A-WALL-FULL-N`,
`AD-WALL`.

| Member | Meaning |
|---|---|
| `separator` | One character, default `-`. |
| `case` | `upper` (default), `lower` or `any`. |
| `discipline.codes` / `codeList` | One-character discipline codes (A, S, M...). |
| `discipline.modifiers` | Optional second character of the designator (`D` in `AD-`). Omit if not used. |
| `majorGroup.length`, `codes` | Major groups, normally four characters (WALL, DOOR). |
| `minorGroup.length`, `maxCount`, `codes`, `allowOther` | Minor groups; `maxCount` 2 allows `A-WALL-FULL-PATT`. With `allowOther: true` a minor group not in the list is accepted if it has the right length and is alphanumeric. Omit `minorGroup` to forbid minor groups. |
| `status.codes` | One-character status codes, written last (`-N`, `-E`, `-D`). A final one-character field is always read as status. |

### `layerNaming` with `"scheme": "iso13567"`

Fixed-position fields with no separators: `A_22____E_R` is agent `A_`, element `22____`, presentation `E_`,
status `R`.

| Member | Meaning |
|---|---|
| `filler` | The character that pads fields and marks an unused field, default `_`. |
| `case` | As above. |
| `fields[]` | In order. Each has `name`, `length` (characters), `required` (mandatory fields must come first), `codes` or `codeList`, and `match`. |
| `fields[].match` | `exact` (default): the value without trailing filler must be a code. `prefix`: the value must start with a code; used for classification codes refined to the right (`221___` matches code `22`). |
| `userDefined.allowed`, `maxLength` | Characters after the last fixed field form the user-defined field. |

A name may stop after any whole field once the mandatory fields are present. An optional field that is not used
is written as filler when a later field follows.

### `layers[]`

| Member | Required | Meaning |
|---|---|---|
| `name` | yes | Should parse under `layerNaming`; the loader's self-check reports names that do not. |
| `color` | yes | ACI number 1-255, `"#RRGGBB"` or `"r,g,b"`. |
| `linetype` | no | Default `Continuous`. |
| `lineweight` | no | Millimetres from the AutoCAD set (0.00, 0.05, 0.09, 0.13, 0.15, 0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, 2.11) or `"default"`. |
| `plot` | no | Default true. |
| `transparency` | no | 0-90. |
| `description` | no | |

### `layerRoles`

`viewport` (viewport frames; the self-check reports it when the layer plots), `xref` (external reference inserts)
and `titleBlock` (the title block insert), each a layer name, plus `description`. When `layers` is not empty each
role layer must be in it, and the xref and title block layers must plot. A role left out leaves the choice to the
engine.

### `textStyles[]` and `dimStyles[]`

Text style: `name`, `font` (TrueType family or `.shx` file), `height` (0 = set per text), `widthFactor` (default 1),
`obliqueAngle` (degrees), `annotative`, `use` (free text). `height` is a plain number in the standard's
`units.length`; when the standard has no `units.length` the engines read it in the job's length unit. For an
annotative style it is the paper height.

Dimension style: `name`, `description`, `annotative`, and `settings`, a map of named settings. In AutoCAD these are
dimension variable names (`DIMTXT`, `DIMASZ`, `DIMBLK`, `DIMTXSTY`...); Revit engines read type parameter names.
Values are numbers, strings or booleans. For annotative styles sizes are paper sizes.

### Naming rules: `sheetNumbering`, `fileNaming` and `viewNaming`

A name is a sequence of fields joined by separators.

| Member | Meaning |
|---|---|
| `fields[]` | In order. |
| `fields[].name` | Field name, used in messages and results (`role`, `number`). |
| `fields[].separator` | Text written before this field when an earlier field is present (`-`). May be empty (`A101`). |
| `fields[].codes` / `codeList` | Allowed values. |
| `fields[].pattern` | Regular expression the whole field must match, e.g. `[0-9]{4}`. |
| `fields[].length`, `minLength`, `maxLength` | Character limits. |
| `fields[].optional` | The field and its separator may be left out. |
| `case` | `upper`, `lower` or `any`. |
| `extensions` | For file names: allowed extensions with the dot (`.dwg`). Compared without regard to case. |
| `examples` | Names that must parse; the self-check tests them. |

A field with neither codes nor pattern accepts letters and digits. Parsing tries every split, so empty separators
and optional fields work; when a name fails, the message names the field and the character position.

### `statusCodes`, `revisionCodes`

`statusCodes` is a code list. `revisionCodes.formats[]` has `name`, `pattern` (regular expression for the whole
code, e.g. `P[0-9]{2}(\.[0-9]{1,2})?`), `description` and an `example` that must match.

### `scales[]` and `textHeights[]`

Scale: `name` such as `1:100` or `1/4" = 1'-0"`; `ratio` (model length per paper length: 100, or 48) is computed
from the name when omitted and must agree with it when given; `use` is free text.

Text height: `name` (`body`, `title`), `plotted` height and its `unit` (default `mm`). The model height of text is
`plotted × ratio`, converted to the drawing unit: 2.5 mm at 1:100 is 250 mm; 3/32" at 1/4" = 1'-0" is 4.5".

### `scaleRules`

Optional. Without it no rule on combining scales applies.

| Member | Meaning |
|---|---|
| `oneScalePerSheet` | Every view (Revit) or viewport (AutoCAD) on one sheet or layout must share one scale. Default false. |
| `exemptViewTypes[]` | Revit view types left out, as `ViewType` names (`Legend`, `DraftingView`, `Detail`), compared without case. |
| `exemptSheets` | Regular expression for sheet numbers (Revit) or layout names (AutoCAD) left out, e.g. `"^(00|G-).*"`. |
| `description` | Free text. |

### `titleBlock`

| Member | Meaning |
|---|---|
| `blockName` | The AutoCAD block name of the title block. |
| `familyType` | The Revit title block type, written `Family : Type` (spaces around the colon). |
| `fields[]` | `name` (the attribute tag or parameter name), `description`, `required` and `source` (where the value comes from: `sheet.number`, `sheet.name`, `project.name`, `issue.date`, `revision.current`...). |
| `description` | Free text. |

All are optional.

### `purge`

`keep[]` lists names (blocks, layers, styles, linetypes) a purge never removes, besides the names the standard
already gives: its layers and their linetypes, text and dimension styles, `layerRoles` and `titleBlock.blockName`.
Compared without regard to case. SA.Core `ProjectStandard.NamesToKeep()` returns the whole set.

---

## 2. Checks (`checks.schema.json`)

```json
{ "version": 1, "description": "...", "checks": [ { ... } ] }
```

| Member | Required | Meaning |
|---|---|---|
| `id` | yes | Unique, lower case and dotted: `layers.naming`. |
| `app` | yes | `autocad`, `revit` or `both`. |
| `title` | yes | One line saying what passes. |
| `severity` | yes | `info`, `warning`, `error` or `critical`. |
| `why` | no | Why it matters, for the person reading the report. |
| `evaluator` | yes | The evaluator's name. A name the engine does not implement reports `not_implemented`, never pass. |
| `params` | no | Evaluator parameters. |
| `fix` | no | `{ "kind": "op", "op": "fix.bylayer", "args": { ... }, "note": "..." }` or `{ "kind": "manual", "instructions": "..." }`. |
| `groups` | no | Tags used to run a set of checks: `sa acad audit --group standards`. |
| `source` | no | Where the rule comes from. |

Op fix templates: a string that is exactly `"{{name}}"` is replaced by the finding's data value `name` keeping its
JSON type; `{{name}}` inside a longer string is replaced by its text; `{{recordId}}` is the element the finding is
about. Every neutral finding carries `field` and `value` in its data.

### Facts

Engines collect the document into named tables of records (each record has an optional element `id` and named
values). Table and field names are agreed between the checks file and the engine's collectors; an engine always
includes every table it knows how to collect, even empty. A missing table means "not collected" and the check
reports `not_implemented`.

### Cut facts: the `incomplete` status

When an engine stops collecting part of a table (a record limit, a call limit, a value it could not read), it says
so in the facts `meta`, in either form:

- `"truncated": { "<table>": { "<part>": { "kept": n, "total": n, "reason": "..." } } }`, where part `records` means
  records were dropped and any other part is a field cut on some records;
  `"omittedFields": { "<table>": { "<field>": <records without it> } }`. SA.Core `Facts.MarkTruncated` and
  `Facts.MarkOmitted` write these.
- `"<table>.truncated": true` with optional `"<table>.total": n`: records were dropped.

A check on such a table never passes. When its table lost records, or lost a field the check reads (its `field`,
`where` and `whereNot` keys, `keys`, `properties`, the `if`, `height`, `scale`, `system`, `label` and `name` fields and the
evaluator's defaults; for an evaluator SA.Core does not know, any field), a result that would pass is `incomplete`
instead, and every result carries `"incomplete": true` and `"incompleteReason"`. A fail stays `fail`: its findings
are real, there may be more. `CheckSummary` counts `incomplete` on its own.

### Neutral evaluators (built into SA.Core)

Every one takes `table`, an optional `where` object (field = value pairs a record must all match) and an optional
`whereNot` object (field = value pairs a record must all differ from; a record without the field passes).

| Evaluator | Params | Finding for |
|---|---|---|
| `names_match_scheme` | `field`, `scheme` (`layer`, `sheet`, `file`, `view`), `ignore[]` | values that do not parse under the standard's scheme; `error` when the standard has no rule for it |
| `field_matches_pattern` | `field`, `pattern` or `standardPattern`, `ignoreCase`, `negate` | values not matching (or, with negate, matching) the whole pattern |
| `count_over_threshold` | `max` | one finding when more than `max` records match |
| `field_equals` | `field`, `equals` or `in[]`, `negate` | records whose field equals the value (with negate: differs; a missing field never equals) |
| `field_missing` | `field`, `emptyIsMissing` (default true) | absent, null or blank values |
| `duplicates_by_key` | `keys[]`, `precision`, `ignoreCase` | every record after the first that shares all key values; numbers are rounded to `precision` first |
| `value_in_standard_list` | `field`, `list` (`layers`, `textStyles`, `dimStyles`, `scales`, `statusCodes`, `codeList:<name>`), `ignoreCase` (default true), `ignore[]` | values not in the standard's list |
| `field_range` | `field`, `min`, `max`, `skipMissing` (false) | numbers outside the range, and non-numbers; absent or null values too unless `skipMissing` |
| `text_height_standard` | `heightField` (`height`), `scaleField` (`scale`), `unit`, `tolerance` (0.02 relative) | text whose plotted height (model height / scale ratio) is not a standard text height |

| `conditional_field_equals` | `field`, `equals` or `in[]`, `negate`, `ifField`, `ifPattern` or `ifStandardPattern`, `ifNegate`, `ignoreCase` | among records whose `ifField` matches the whole pattern (XOR `ifNegate`), those whose `field` equals the value (XOR `negate`); with no `field`, every such record |
| `field_within` | `field`, `min`, `max`, `minExclusive`, `maxExclusive` | numbers inside the interval (the inverse of `field_range`); non-numbers are ignored |
| `distinct_count_min` | `field`, `min` | one finding when fewer than `min` distinct values occur (none when no record matches) |
| `scale_in_standard` | `field`, `tolerance` (0.001 relative), `skipMissing` (false) | scale ratios that are not one of the standard's `scales`, and non-positive values; absent values too unless `skipMissing` |
| `units_match_standard` | `field` | unit symbols other than the standard's `units.length` (data `expected`); without one, only `unitless` |
| `dim_precision_min` | `field` (`stepMm`), `systemField` (`unitSystem`), `imperialMinIn`, `metricMinMm`, `tolerance` (1e-6) | display steps finer than the minimum for the record's unit system, and records that cannot be judged |
| `matches_standard_entry` | `list` (`layers`, `textStyles`, `dimStyles`), `nameField` (`name`), `properties` (record field to standard property), `ignoreCase` (true) | records named in the list whose properties differ from those the entry gives (colours normalised, numbers within 1e-6) |
| `group_count_over_threshold` | `key`, `labelField`, `max` | one finding per group of equal `key` values larger than `max`, largest first |
| `manual_review` | `instructions` | always one finding: a review-by-eye item (below). Reads no facts |
| `extents_overlap` | `groupBy[]`, `box[]` (`minX`, `minY`, `maxX`, `maxY`), `minOverlap` (0.1 of the smaller box), `jobOnly` (true), `labelField` | one finding per pair of boxes in the same group that overlap by more than `minOverlap`; after a job, only pairs with an element the job touched |
| `sheet_scales_uniform` | `groupField`, `scaleField`, `typeField`, `tolerance` (0.001) | one finding per sheet or layout mixing scales, when the standard's `scaleRules.oneScalePerSheet` is true |

The exact semantics of the nine before the layout evaluators are in `checks/FACTS.md` section 3, of the last two in
section 4. Engines register their own evaluators
(`wall_open_ends`, `rooms_unplaced`...) under other names.

### Review-by-eye items

A check whose evaluator is `manual_review` (or whose every finding has `data.review = true`) is a review item. Its
check result carries `"review": true` and `"reviewInstructions"`. By default its `status` stays `fail`, so readers
that know only the original statuses still work; an engine that sets `CheckRunner.ReviewStatus` writes
`"status": "review"` instead. Either way report writers list review items in a separate "review by eye" section and
leave them out of pass and fail counts (SA.Core `CheckSummary` does this).

### Choosing the checks file and the standard for an audit

The `audit` request (and the client) take these params, read by SA.Core `AuditOptions`:

| Param | Meaning |
|---|---|
| `checks` | Check ids to run. |
| `group` or `groups` | One group, or a list. With neither `checks` nor a group, every check for the app runs. |
| `checksFile` | A checks file, a folder of them (every `*.json`, in name order) or a list. Relative paths are resolved against the document's folder. Replaces the bundled checks unless `checksMode` is `add`. `catalog` is accepted as an alias. |
| `checksMode` | `replace` (default) or `add` (load the bundled checks first, then these; an id defined twice is refused file by file). |
| `standard` | A standards file, resolved the same way. |
| `includeFacts` | Return the collected facts with the findings. |

---

## 3. Design data (`design-data.schema.json`)

One file per topic in `data/design/`: `{ "topic", "retrieved", "entries": [...] }`, `retrieved` as `YYYY-MM-DD`.
Each entry:

| Member | Meaning |
|---|---|
| `id` | `topic.jurisdiction.thing[.qualifier]`, lower case, unique across all files. |
| `quantity` | What is measured, in words. |
| `value` | The figure in the metric unit. `null` for a rule-only entry, which must then have a `formula`. |
| `min`, `max` | Limits in the same unit, or `null` where there is none. A minimum-only rule has `min` = `value` and `max` null. |
| `unit` | Metric unit: `mm`, `m`, `m2`, `s`, `deg`, `N`, `percent`, `count`, `persons`, `risers`, `scale factor`, `mm per person`, or `1:n` for a gradient, where the number is n (so a larger number is gentler). |
| `imperial` | `{ "value", "unit", "converted" }`: the US figure; `converted` is true when computed from the metric one, false when the source states it. |
| `applies_to` | The situation the rule covers. |
| `jurisdiction` | Where it applies, naming the code edition. |
| `source` | `{ "title", "publisher", "edition", "clause", "url" }`. |
| `note` | Anything a designer must know; may be empty. |
| `confidence` | `read_in_source` (read in the cited document) or `secondary` (taken from a restatement). |
| `formula` | Optional; required when `value` is null. Plain text such as `ceil(0.02 * total)`. |

These figures are defaults with their source. Code compliance is the user's to confirm.

---

## 4. Job (`job.schema.json`)

What the agent sends with `run` or `validate`.

| Member | Required | Meaning |
|---|---|---|
| `jobId` | yes | Unique forever: letters, digits, `.`, `_`, `-`, up to 128 characters. A repeat is refused. |
| `title` | no | Names the single undo step `SA: <title>`. |
| `app` | yes | `autocad` or `revit`. |
| `document` | recommended | `path` and `fingerprint` copied from the snapshot. Without it lint warns. |
| `units.length` | yes | `mm`, `cm`, `m`, `in` or `ft`. Every length in the job is in this unit unless written with its own, e.g. `"3.2m"` or `"10'-6\""`. Angles are degrees. |
| `standard`, `outputDir`, `description` | no | |
| `ops[]` | yes | `{ "id", "op", "args", "note" }`, run in order. Any failure rolls back the whole job. |
| `verify` | no | `checks` (ids to run after the ops), `capture` (`[{ "target": "activeView" }]`), and `checksFile` and `checksMode` as for an audit (section 2; the `catalog` alias is not accepted here). With `checksFile`, check ids not in the bundled checks are warned about, not refused, since the file defines them. `visual`: a before/after picture comparison (below). |

### `verify.visual`

The engine captures `target` before the first op and after the last, compares the two PNGs pixel by pixel and adds
the check `visual.diff` (severity warning; it never rolls a job back) plus the files `visual-before.png`,
`visual-after.png` and `visual-diff.png` to the report's `artifacts` (targets `visual:before`, `visual:after`,
`visual:diff`) in the job's output folder.

| Member | Required | Meaning |
|---|---|---|
| `target` | yes | As a capture target. AutoCAD: `model`, `activeView`, `activeLayout`, `layout:<name>`. Revit: `activeView`, a view name or a view id. |
| `window` | no | AutoCAD, `model` only: `[[x1, y1], [x2, y2]]` in drawing units; default the drawing extents plus 5%. |
| `pixelWidth` | no | Revit: image width, 32 to 15000 (default 2000). |
| `maxUnexpectedChange` | no | Fraction of the image's pixels allowed to change outside the expected region, 0 to 1 (default 0.002). |
| `threshold` | no | Per-channel difference, 0 to 255, below which a pixel counts as unchanged (default 32). |
| `padding` | no | Pixels added around each touched element's extents to make the expected region, 0 to 1000 (default 16). |
| `expectChange` | no | `true`: something must change. `false`: nothing may change (every changed pixel is unexpected). Left out: something must change when the job created elements that show in the target. |

The expected region is the extents of every element the job created or modified, projected into the image, each
padded. Verdicts:

- `fail`: nothing changed although a change was expected; or more than `maxUnexpectedChange` changed outside the
  expected region and the engine knows exactly which part of the target the image shows and where every touched
  element was (or `expectChange` is false).
- `review`: too much changed outside, but the image frame or some touched element could not be placed (a deleted
  element, an element modified without earlier extents, a non-rectangular crop, a model shown through layout
  viewports). Open the diff: magenta is change outside the expected region (green boxes), red is change inside it.
- `incomplete`: no comparison was possible (a rehearsal in AutoCAD, which cannot plot inside its transaction; a
  capture that failed; images of different sizes because the crop or view changed).
- `pass` otherwise.

The check's findings list each changed region with change outside the expected region: `box` (image pixels),
`worldBox` (target coordinates, when known), `pixels`, `unexpectedPixels`.

Op ids start with a letter or `_`. Args follow the op's schema: `sa ops` lists them all, `sa ops --json` gives the
full declarations, and `sa job lint` checks a job against them offline.

Field types in op args: `string`, `number`, `integer`, `length` (number or length string), `angle` (degrees),
`bool`, `enum`, `point2` `[x, y]`, `point3` `[x, y, z]`, `list`, `map` (free keys), `object` (fixed fields) and
`ref`. Rules across fields (`exactly one of offset, point`) are listed with each op.

References: a string starting with `$` refers to an earlier op's result. `"$w1"` is the whole result;
`"$w1.walls.0.id"` walks into it (numbers index lists). A name after the op id is looked up in the result, then in
its `outputs`, then in its `readback`. Write `"$$"` for a literal text starting with `$`.

---

## 5. Report (`report.schema.json`)

What `run` returns and `status` reads back: `jobId`, `status` (`committed`, `rolled_back`, `failed`, `running`,
`unknown`), `mode` (`commit` or `rehearse`), `validation` (issues with JSON `path`, `code`, `severity`,
`message`), `results` (one per op: `ok`, element ids `created`/`modified`/`deleted`/`unchanged`, `readback` values in
job units, named `outputs`, `error`), `summary` counts, `checks` (status `pass`, `fail`, `not_implemented`,
`error`, `review` or `incomplete`, with findings, instantiated fixes, the `review` flag and the `incomplete` flag), `artifacts` (files written),
`warnings` (from the app or the engine: `source`, `text`, `opId`, and optionally `code` such as the Revit failure
definition GUID, `severity` and `elements` ids), `undo`, `failedOpId` and `error` for a failed job, and `timingMs`
per phase.

Optional members: `committed` (true when the changes were committed even though a later step such as post-commit
readback failed, false when nothing was changed), `document` (`path`, `fingerprint` after the job and
`fingerprintBefore`; a follow-up job carries the new fingerprint), `dialogs` (each `kind`, `dialogId`, `message`,
`dismissedWith`, `accepted`: dialogs the application showed during the job and how they were dismissed) and
`engine` (release and version), and `visual` (the `verify.visual` summary: `target`, `status`, `reason` when
incomplete, the frame, `changedFraction`, `unexpectedFraction`, `regions` and `files`). SA.Core keeps any other
top-level member it reads in `Report.Extra`.

---

## 6. Error details

An error response is `{ "code", "message", "details" }` with a code from the closed list. Agreed `details` keys:

| Code | Key | Meaning |
|---|---|---|
| `timeout` | `started` | `false`: the engine never started the request (the application stayed busy past the start deadline), nothing ran and it is safe to send again. `true`: it started and its outcome is unknown, so ask `status`. Absent: the client's own timeout, outcome unknown. `ran` is an older spelling with the same meaning. |

An op is `ok` only when its readback matched the request within tolerance.
