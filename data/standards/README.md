# Project standards presets

Two starting points for a project standard, in the format of `data/schemas/standard.schema.json`
(`data/FORMATS.md` section 1). Neither is an official standard. Both are style presets built from public owner
standards and public guidance, with every sourced value tagged by its source id. Source ids resolve in
`references/practice/_SOURCES.md` and `research/05-expert-practice-and-standards.md`. The paywalled standards
(the US National CAD Standard, ISO 13567, ISO 19650-2 and its UK National Annex) were not read, and nothing here
presents their content as official.

| File | Use for | Units | Starter layers |
|---|---|---|---|
| `us-ncs-style.json` | US projects: AIA/NCS-style layers, Uniform Drawing System sheet ids, imperial scales | inches | 46 |
| `iso19650-uk-style.json` | UK and ISO 19650 projects: Uniclass layers in the DfE form of ISO 13567, ISO 19650 container names, suitability and revision codes, metric scales | millimetres | 36 |

Both define every pattern and code list the audit checks need (`data/checks/README.md`, "How a project changes
checks").

## What each preset holds

**us-ncs-style**: the layer scheme (discipline designator with optional modifier, 4-character major group, up to
two 4-character minor groups, 1-character status), discipline, modifier, major group, minor group and status code
lists, 46 starter layers for architecture (A), interiors (I), grids (S) and general (G), three Arial text styles, an
annotative architectural dimension style (feet and inches, 1/4 in), UDS sheet ids (`A-101`, `AD101`) with sheet type
digits and the set order, title block field names, issue descriptions, 20 imperial scales, plotted text heights from
3/32 in to 1 in, ARCH and ANSI sheet sizes, the Missouri colour-to-width map as the plot colour list.

**iso19650-uk-style**: the DfE layer form `Agent-Element-Presentation-Description` with Uniclass codes
(`A-Ss2510--FramedWallSystems`), ISO 19650 discipline (NA.3.7) and form (NA.3.6) code lists, 36 starter layers,
three Arial text styles, an annotative millimetre dimension style, container naming
`Project-Originator-Functional-Spatial-Form-Discipline-Number` (with the optional description, suitability and
revision used for files outside the CDE), sheet numbers as full container names, suitability codes S0-S7 (S5
withdrawn), A, B and CR, revision formats P01.01 / P01 / C01, 10 metric scales, plotted text heights 1.8-20 mm, ISO A
sheet sizes.

The `notes` member of each file says, list by list, what is complete and what is partial.

## Sourced versus default

Sourced (with the source in the file's notes or in the entry's description, name or use):

- Layer name formats; discipline, status and sheet type code lists; the US major group list (Calgary's complete
  derivative list plus the Missouri codes it lacks); ISO 19650 discipline and form codes; Uniclass titles.
- Sheet id format and set order; container naming fields and field rules; suitability and revision codes.
- Scales and their uses; plotted text heights; sheet sizes; the Arial font; DIMTXT and the dimension precision
  rule; the Missouri colour-to-width table; the ISO 128 lineweight series.

Partial lists, which a project must extend: US discipline modifiers and minor groups (only codes seen in public
examples); the Uniclass element field (only the table prefix is validated, not that the code exists); US issue
status codes.

**Defaults, not sourced. A project should override them:**

- Which starter layers exist, and **every starter layer colour, linetype and lineweight**. The US colours are
  chosen so a layer's lineweight equals what Missouri's pen table plots for its colour, and the ISO lineweights are
  values of the ISO 128 series picked by role, but the assignment of colour and weight to each layer is a
  judgement, not a rule.
- Text style and dimension style names and their settings other than those named as sourced.
- Title block field names (the content follows `references/practice/sheets-sets.md` section 2).
- The `patterns` (which layers count as dimension, wall, area or title block layers for the checks; workset and
  export view names) and the `xrefLayers` and ISO `plotColours` code lists.
- US `statusCodes` (issue descriptions) and `revisionCodes` (numbered revisions).
- `layerRoles` (the viewport, xref and title block layers), `viewNaming` (the same rule as `patterns.viewName`) and
  the empty `purge.keep`. Neither preset sets `titleBlock.blockName` or `titleBlock.familyType`: the title block is
  the project's own.

Not included because no public source was found: a US file naming rule, the legacy UK two-character Type and Role
lists, the full ISO 13567 nine-field codes, NCS v7 lists.

## Deriving a project standard

### From a preset

1. Copy the preset into the project folder and give it a new `id`, `name` and `version`.
2. Replace the defaults the project already has an answer for: the client's layer list, colours and lineweights
   (match the project's CTB or STB), text and dimension styles, title block attribute tags, issue codes.
3. Add the project's own codes to the partial lists (minor groups, discipline modifiers, Uniclass codes in use)
   and any owner variants (for example Missouri's sheet type 7 = reflected ceiling plans, DfE's A0-A6 and B0-B5).
4. Set the patterns to the project's layer and workset names, the xref layer, the approved fonts and the plot
   colour map. Set `layerRoles`, `viewNaming`, the title block's `blockName` (AutoCAD) or `familyType` (Revit,
   `Family : Type`) and any names `purge.keep` must protect.
5. Add a `fileNaming` rule to the US copy if the project has one.
6. Validate: `python3 data/schemas/validate.py data/schemas/standard.schema.json <file>`, then load it in the engine,
   whose self-check reports layer names that do not parse and naming examples that fail.

### From an existing template drawing (.dwt or a clean .dwg)

1. Open the template and take a snapshot (`sa acad snapshot`), which lists layers with colour, linetype,
   lineweight and plot flag, text styles, dimension styles, layouts and the title block attributes.
2. Decide the naming scheme the layer names already follow (`us` or `iso13567`) and start from the matching
   preset, so the code lists come with it.
3. Replace `layers` with the template's layers. Keep layer `0` and `Defpoints` out of the list. Lineweights must be
   AutoCAD lineweights in millimetres; colours ACI numbers or `#RRGGBB`.
4. Replace `textStyles` and `dimStyles` with the template's styles: font, height, width factor, annotative; for
   dimension styles the DIM variables that differ from the defaults (`DIMLUNIT`, `DIMDEC`, `DIMRND`, `DIMTXT`,
   `DIMASZ`, `DIMBLK`, `DIMTXSTY`...).
5. Set `scales` to the template's annotation scale list after trimming it to approved scales, and `titleBlock` to the
   title block's attribute tags.
6. Run the audit against the template with the new standard. Layer names that fail the scheme either need adding
   to the code lists (if the project accepts them) or translating; decide with the user before changing the
   template.
7. Validate as above.

For a Revit template the same steps apply to view template names, sheet numbering, worksets and the patterns; layer
lists and DIM variables matter only for DWG export setups.
