# CAD standards (AutoCAD)

Read this before setting up a drawing, creating layers, styles or blocks, attaching xrefs, choosing plot styles, cleaning a file, or packaging files for issue. Layer names, colours and plot styles are project data: read them from the project's template or standard file; use the formats here only when nothing is given, and ask first.

Contents: 1 Layer naming · 2 Layer properties · 3 Colour and plot styles · 4 Units · 5 Templates · 6 Linetype scale · 7 Blocks and attributes · 8 Xrefs · 9 Purge, audit, cleanup · 10 Transmittal · 11 Checks

## 1. Layer naming

### 1.1 US National CAD Standard (AIA CAD Layer Guidelines) format
`Discipline - Major - Minor1 - Minor2 - Status`, hyphen separated [S05][S01][S02].

| Field | Length | Notes |
|---|---|---|
| Discipline | 2 chars (second char optional modifier, else `-`) | Author of the graphics, not the trade of the object [S05] |
| Major group | 4 | `WALL`, `DOOR`, `GLAZ`, `FLOR`, `CLNG`, `ANNO`, `FURN`, `EQPM`, `COLS`, `GRID`… |
| Minor group 1, 2 | 4 each, optional | User-defined allowed if 4 alphanumerics, padded with `~`, and documented [S02][S03] |
| Status | 1, optional, always last | N new, E existing to remain, D demolish, F future, T temporary, M moved, R relocated, X not in contract [S05] |

Examples [S05][S09]: `A-WALL`, `A-WALL-FIRE`, `A-DOOR-SWNG`, `A-GLAZ`, `A-FLOR-PATT`, `A-CLNG-GRID`, `A-AREA-IDEN`, `A-ANNO-DIMS`, `A-ANNO-TEXT`, `A-ANNO-NPLT`, `G-ANNO-REFR` (xrefs), `A-ANNO-TTLB` (title block), `I-FURN-PNLS`, `S-GRID`, `A-WALL-D`.

Check pattern (derived in research): `^[A-Z][A-Z-]-[A-Z0-9~]{4}(-[A-Z0-9~]{4}){0,2}(-[A-Z])?$` [S05]. Older owner standards put status in a 4-character minor group instead (`NEWW, EXST, DEMO, FUTR, TEMP, MOVE, RELO, NICN, PHS1-9`) [S09]; accept either style when the project says so. The full NCS code lists are paywalled; public owner derivatives (Calgary, Missouri, Illinois State) are not identical to NCS [S01][S09][S10].

### 1.2 ISO 13567 format
Fixed-position fields, not hyphen-delimited: agent responsible (2), element (6, a classification code such as Uniclass or CI/SfB), presentation (2), then optional status (1), sector (4), phase (1), projection (1), scale (1), work package (2), user field [S12]. `-` means "all / not subdivided", `_` means unused [S12].

UK public-sector example (DfE): `Agent-Element-Presentation-Description`, element = Uniclass code truncated to 6 characters without underscores: `A-Ss2510--CurtainWallingSystems`, `S-Ss2030--Columns` [S14]. Legacy BS 1192 "AUG v2" style, e.g. `A210G701_WALL_L1` [S13].

### 1.3 Decision rule
Use the client's standard. If none: US projects take the NCS format; UK and ISO 19650 projects take the ISO 13567 / Uniclass form the client names. Never invent a third format. Never organise layers by line weight ("THIN", "THICK") [S48 context].

## 2. Layer properties: by layer

- Colour, linetype, lineweight and transparency ByLayer on every entity [S48][S33]. Why: consultants fade an xref'd drawing by overriding its layers; global layer edits must propagate.
- Exceptions: entities inside block definitions may be ByBlock (section 7).
- Nothing on layer `0` except block definition contents; nothing on `Defpoints` except what AutoCAD puts there (dimension definition points), because Defpoints never plots [S32][S48].
- No-plot content (construction lines, viewport frames, notes to self) on a no-plot layer such as `A-ANNO-NPLT` [S09].

## 3. Colour and plot styles

| Plot style type | How it works | Notes |
|---|---|---|
| CTB (colour-dependent) | 256 styles keyed to colour; change the colour to change the output | `PSTYLEPOLICY` 1 = colour-dependent (default) [S50] |
| STB (named) | Styles assigned to layers or objects by name | `PSTYLEPOLICY` 0 = named; converting a drawing CTB→STB is one-way [S50] |

- AutoCAD Architecture templates ship `AIA Standard.ctb` (Tri-Services colour to lineweight) and `AEC Standard.stb` [S08]. Missouri's mandated pen table is in `drawing-conventions.md` [S09].
- With CTB, colour is meaning: a layer coloured outside the project's colour map plots at the wrong weight (AC-035). Use only the colours the map defines.
- Monochrome output for construction sets unless colour is part of the information; use a monochrome plot style or one with colour mapped to black [S09][S10].
- Do not mix CTB and STB drawings in one xref set without a conversion plan.

## 4. Units

| System variable | Values | Source |
|---|---|---|
| `INSUNITS` | 1 inches, 2 feet, 4 millimetres, 6 metres, 21 US survey feet (0 = unitless) | [R04] |
| `MEASUREMENT` | 0 imperial, 1 metric (selects default hatch and linetype files) | [R04] |

- Model in real units: 1 unit = 1 mm (metric) or 1 inch (US architectural) unless the project says metres [S10][S14].
- Why insertion units matter: when a block or xref is inserted, AutoCAD scales it by the ratio of its `INSUNITS` to the host's. A block drawn in mm with `INSUNITS` = 4 inserted into an inch drawing comes in at 1/25.4 size; an unset (0) value skips scaling and can bring a mm block in 25.4 times too large. Set `INSUNITS` correctly in every drawing and every block before sharing [S32][S52].
- Check the snapshot's units first; never assume metric or imperial from the file name.
- Do not use `DIMLFAC` to fake a scale in model space (AC-034) [S10].

## 5. Templates

A project template (.dwt) carries: layers per the standard, text styles (fonts and plotted heights), dimension styles (one per scale if not annotative), multileader styles, table styles, linetypes, the annotation scale list trimmed to approved scales, layouts with the title block and page setups, the plot style, units and `INSUNITS`. Start new drawings from it; do not copy layers from old jobs (pastes bring junk styles) [S32]. A standards file (.dws) can be associated with a drawing for standards checking (behaviour not verified this session).

## 6. Linetype scale

Two methods are in use; pick one per project.

| Method | Settings | Source |
|---|---|---|
| Illinois State method | `LTSCALE` = half the scale factor, `PSLTSCALE` = 0 in layouts | [S10] |
| Paper-space scaling method | `PSLTSCALE` = 1 so dashes in viewports match paper space; `MSLTSCALE` = 1 so model-space dashes follow the annotation scale; `LTSCALE` = 1 (or the value the linetype file expects) | typical practice, confirm |

Whichever you use, test: dashed and centre lines must read as dashed on the plotted sheet at every viewport scale. Metric drawings should use the metric linetype file (`acadiso.lin`), whose dash lengths are in mm.

Illinois State table values: ¼" = 1'-0" factor 48 → `LTSCALE` 24; ⅛" = 1'-0" factor 96 → 48 [S10].

## 7. Blocks and attributes

- Build block geometry on layer `0` with colour, linetype and lineweight ByBlock (or ByLayer) so the inserted block takes the properties of the layer it is inserted on [S32][S33][S10]. Exception: multi-layer blocks whose parts must keep their own layers (e.g. door leaf on `A-DOOR`, swing on `A-DOOR-SWNG`) are built on those layers deliberately.
- Name blocks by the standard (e.g. `DOOR-SGL-0900`, `WC-ACCESSIBLE`); no spaces or special characters; never anonymous copies.
- Attributes carry data (door MARK, room NUMBER, title block fields). Data lives in attributes, never in loose text near the block. Schedules are extracted from attributes.
- Insert at true size with insertion units set; never scale a block to fit unless it is designed to scale (symbols, which should be annotative instead).
- Dynamic blocks: one definition with parameters (visibility states, stretch, flip, lookup) replaces many static variants, e.g. one door block with widths and swing flip. Do not explode them; exploding loses the parameters and the count (AC-007).
- Redefine blocks centrally (from the library) rather than editing instances.

## 8. Xrefs

| Rule | Why |
|---|---|
| Overlay for background references; Attach only when the host must carry the reference when it is itself xref'd [S54] | Overlays are not carried into the next host, which prevents circular and duplicated nesting |
| Relative paths (`REFPATHTYPE`; relative is the default) [S54] | The project folder can move; full paths break. Relative paths are unavailable across drives or servers [S54] |
| Keep the folder structure when sharing [S09] | Relative paths depend on it |
| Insert at 0,0,0, scale 1, rotation 0, with shared origin and coordinates agreed between disciplines | Every consultant's drawing then overlays without moving |
| Xrefs on a dedicated layer (e.g. `G-ANNO-REFR`) [S48] | Xrefs land on the current layer and vanish when that layer is frozen |
| Nesting depth 1 [S44] | Deep chains are fragile and slow |
| Bind only for archive or issue when the recipient requires it | Binding freezes the reference and imports its layers |
| Clip xrefs rather than copying parts of them | Copies drift out of date [S42] |

Origin and coordinates: agree one origin and orientation for the project (survey grid point, or a grid intersection) and keep it in every drawing. Do not move geometry to "make it fit"; move nothing in xrefs.

## 9. Purge, audit and cleanup

- `AUDIT` on receipt of outside files and before issue [S43]. Experts disagree on routine auditing of your own files; do it when there is a reason [S86].
- `-PURGE` All for unused blocks, layers, styles, linetypes; Regapps must be purged separately (`-PURGE` R) because All does not include them [R04].
- `OVERKILL` to remove duplicate and overlapping geometry [S32].
- Flatten stray Z values in 2D drawings (AC-027) [S32].
- Remove foreign text styles, hatch patterns and colours imported with consultant content [S22].
- Save in a ready-to-plot state, preferably on a paper-space layout [S09].

## 10. Transmittal

- Package with eTransmit (or the project's tool) so xrefs, images, fonts, plot styles and data links travel with the drawing [S32].
- Include a transmittal record: file list, revision, status, date, recipient.
- Keep relative path structure inside the package.

## 11. Checks

| Check | Signal |
|---|---|
| Layer names follow the standard | Layer names failing the project pattern or approved list [S32][S41] |
| Properties ByLayer | Entities with hard-coded colour, linetype or lineweight |
| Layer 0 and Defpoints clean | Non-block entities on 0; content on Defpoints |
| Units set | `INSUNITS` 0 or not the project unit |
| Plot style correct | Wrong CTB/STB attached; colours outside the map |
| Xrefs relative, on their layer, found | Full paths, wrong layer, unresolved references |
| Purgeable items | Purgeable count > 0 at issue |
| Text and dimension styles | Count above project limit; styles not in the standard |
