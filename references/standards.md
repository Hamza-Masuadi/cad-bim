# Project standards: presets, choosing one, making your own

A project standard is one JSON file (format: `data/FORMATS.md` section 1, schema `data/schemas/standard.schema.json`). It holds the layer scheme and starter layers, text and dimension styles, plotted text heights, approved scales, sheet and file naming, title block field names, issue and revision codes, and the patterns the audit checks use. Ops read it for default layers, styles, heights and names; `audit` and `verify` check against it.

## 1. Ask first

On first use in a project, ask which standard applies (office, client, national or none) unless the file settles it, and say how you inferred it ("the layers follow the A-WALL style, so I'll use the US preset"). Ask in one short question with the likely answer offered. Never mix standards in one job. Phrasing and follow-ups: `practice/asking-the-user.md`.

## 2. The two presets

Neither is an official standard: both are style presets built from public owner standards and guidance. The paywalled standards (US National CAD Standard, ISO 13567, ISO 19650-2 and its UK annex) were not read; never present a preset as the official standard.

| Preset | For | Units | What it gives |
|---|---|---|---|
| `us-ncs-style.json` | US projects | inches | NCS-style layers (`A-WALL`, `A-DOOR`, `A-GLAZ`, `A-FURN`, `A-AREA-IDEN`, `A-ANNO-DIMS`, `I-FURN`, `I-CASE`, `I-FLOR-PATT`, `S-GRID`, `G-ANNO-TTLB`, status layers such as `A-WALL-E` and `A-WALL-D`), text styles `NOTES`, `TAGS`, `TITLES`, dimension style `ARCH-ANNO`, sheet ids like `A-101`, title block fields (`PROJECT_NAME`, `PROJECT_NO`, `SHEET_TITLE`, `SHEET_NO`, `SCALE`, `ISSUE_DATE`, `REVISION`, `DRAWN_BY`...), 20 imperial scales, plotted heights 3/32 in to 1 in |
| `iso19650-uk-style.json` | UK and ISO 19650 projects | millimetres | Uniclass layers in the form `A-Ss2510--FramedWallSystems`, dimension style `MM-ANNO`, container names (`Project-Originator-Functional-Spatial-Form-Discipline-Number`) used as sheet numbers, suitability codes (S0 to S7, A, B, CR) and revisions (P01, C01), title block fields (`PROJECT`, `DRAWING_TITLE`, `DRAWING_NUMBER`, `SUITABILITY`, `REVISION`, `SCALE`...), 10 metric scales, plotted heights 1.8 to 20 mm |

Layer colours, linetypes and lineweights, style settings and title block field names in both presets are defaults, not sourced rules: a project should replace them with its own. Each preset's `notes` member says which lists are complete and which are partial. Exact names: open the preset file in `data/standards/`.

## 3. How a job finds the standard

A job names its standard with `"standard": "<file>"`. A file that cannot be found or does not validate refuses the job (`standard_missing`, `standard_invalid`); there is no silent fallback.

- **AutoCAD** tries a relative path against the drawing's folder, then `%LOCALAPPDATA%\CadBim\`, then the installed bundle.
- **Revit** resolves a relative path against the model's folder only.
- **Simplest:** put the project's file next to the drawing or model as `project-standard.json` and write `"standard": "project-standard.json"` (all recipes do this). An absolute path also works.
- **No standard named:** ops fall back to the current layer or style with a warning; `audit` and `verify` use the bundled preset for the file's units (US preset for inches or feet, ISO preset otherwise). Say which one was used.
- **Audit with a project standard:** `sa acad audit --group standards --standard C:/Projects/2417/project-standard.json` (the client makes the path absolute from its own folder, so give the full path).

## 4. Changing or importing your own standard

Editing a standard file is a file change in the project folder, not a job: show the user what will change and get the same one approval.

### Start from a preset

1. Copy the matching preset next to the drawing or model as `project-standard.json`; give it a new `id`, `name` and `version`.
2. Replace the defaults the project already answers: the client's layer list, colours and lineweights (matching its CTB or STB), text and dimension styles, title block attribute tags (AutoCAD `titleBlock.blockName`, Revit `titleBlock.familyType` as `Family : Type`), issue codes.
3. Add the project's own codes to the partial lists (minor groups, discipline modifiers, Uniclass codes in use).
4. Set the `patterns` to the project's layer, workset and view names, the xref layer and the plot colour map; set `layerRoles`, `viewNaming` and the names `purge.keep` must protect.
5. Validate: `python3 data/schemas/validate.py data/schemas/standard.schema.json project-standard.json`. Then run an audit with `--standard`: the engine reports layer names that do not parse and naming examples that fail.

### Derive from an office template (.dwt or a clean .dwg)

1. Open the template and run `sa acad snapshot --sections layers,linetypes,textStyles,dimStyles,annotationScales,layouts,blocks`.
2. Pick the preset whose layer scheme the names already follow (`us` or `iso13567`), so the code lists come with it.
3. Replace `layers` with the template's layers (leave out `0` and `Defpoints`; lineweights are AutoCAD lineweights in mm, colours ACI numbers or `#RRGGBB`), and `textStyles` and `dimStyles` with the template's styles.
4. Set `scales` to the template's approved annotation scales and `titleBlock` to the title block's attribute tags.
5. Audit the template against the new file. Names that fail either join the code lists (if the project accepts them) or get translated later with `layer.translate`; decide with the user.
6. Validate as above.

For a Revit template the same steps apply to view template names, sheet numbering, worksets and patterns; layer lists and DIM variables matter only for DWG export.

### Applying it to a drawing

`doc.setup` with `applyStandard: true` creates every layer, linetype, text style and dimension style the standard defines, leaving existing ones alone (AutoCAD recipe 3.1). Existing content on wrong layers is moved with `layer.translate` after an audit (AutoCAD recipe 3.9). In Revit, levels, grids, view names, templates and sheets follow the standard through the ops that create them; view and sheet names that break its naming rule are warned about.
