# AutoCAD job ops

Generated from the op schemas in the engine by `ReferenceDocsTests` in `engine/src/SA.Core.Tests`. Do not edit by hand: change the op schema or its example, then regenerate.

47 ops. Every example below passes `sa job lint`. Names in the examples (layers, blocks, types, views, sheets, ids) are placeholders: take the real ones from the snapshot or create them earlier in the same job. `document` is copied from the snapshot's `identity`.

How to read the field tables:

- `name*` in `sa ops` is "yes" in the Required column. `a.b` is a field of object `a`; `a[].b` is a field of each item of list `a`.
- **length**: a number in the job's `units.length`, or a string with its own unit (`"3.2m"`, `"10'-6\""`). Fields described as paper sizes take millimetres on the sheet for a metric job and inches for an imperial one, whatever the job unit.
- **degrees**: angles, counter-clockwise.
- **ref**: a `$id` reference to an earlier op's result (`"$w1"`, `"$w1.walls.0"`), or what the description allows (handle, element id, exact name).
- **ensure** ops leave an existing object alone (differences become a warning) unless `update` is true. **destructive** ops delete; list exactly what will go in the plan.

## Index

| Op | Flags | What it does |
|---|---|---|
| [`doc.setup`](#docsetup) | ensure | Set drawing units, precision, angle conventions, linetype scaling and annotation scales; optionally create everything the standard defines. |
| [`layer.ensure`](#layerensure) | ensure | Create layers with colour, linetype, lineweight and plot flag; existing layers are left alone unless update is true. |
| [`linetype.ensure`](#linetypeensure) | ensure | Load linetypes from a .lin file. |
| [`textstyle.ensure`](#textstyleensure) | ensure | Create text styles. |
| [`dimstyle.ensure`](#dimstyleensure) | ensure | Create dimension styles from named dimension variable settings. |
| [`wall.draw`](#walldraw) |  | Draw double-line walls from centrelines with thickness; L, T and X junctions are cleaned so no line crosses a joint, and open ends are capped. |
| [`opening.place`](#openingplace) |  | Place a door or window block in a wall, cut the wall lines, and set swing and handing. |
| [`grid.draw`](#griddraw) |  | Draw structural grid lines with bubbles and labels, from explicit lines or an orthogonal grid definition. |
| [`room.create`](#roomcreate) |  | Create a closed room boundary, compute its area and place a room tag. |
| [`dim.linear`](#dimlinear) |  | Add one linear dimension. |
| [`dim.string`](#dimstring) |  | Add a continuous string of dimensions through a list of points, all on one line. |
| [`text.add`](#textadd) |  | Add single-line or multiline text at a model height, or at a plotted height for a scale. |
| [`mleader.add`](#mleaderadd) |  | Add a multileader note. |
| [`table.create`](#tablecreate) |  | Create a table with a title, header row and data rows. |
| [`schedule.fromBlocks`](#schedulefromblocks) |  | Collect attribute values from block references into a table and/or a CSV file (door, window, furniture schedules). |
| [`block.define`](#blockdefine) | ensure | Define a block from simple entities, or from a drawing file. |
| [`block.insert`](#blockinsert) |  | Insert a block at one or many points, with attribute values and dynamic properties. |
| [`block.attrs.set`](#blockattrsset) |  | Set attribute values on block references. |
| [`block.dynprops.set`](#blockdynpropsset) |  | Set dynamic block properties (visibility state, stretch distance, flip...) on references. |
| [`layout.create`](#layoutcreate) | ensure | Create a paper space layout from a template, a page setup or a paper size, with an optional title block. |
| [`viewport.create`](#viewportcreate) |  | Create a paper space viewport at a scale showing a model area, lock it and freeze layers in it. |
| [`viewports.layout`](#viewportslayout) |  | Place several model views on a layout automatically, in aligned rows inside the drawable area. |
| [`titleblock.fill`](#titleblockfill) |  | Fill title block attributes on layouts. |
| [`plot.pdf`](#plotpdf) | alone in its job (only `plot.pdf` ops) | Plot layouts to PDF with a page setup and plot style table, naming files by pattern. |
| [`xref.attach`](#xrefattach) |  | Attach or overlay an external reference. |
| [`xref.repath`](#xrefrepath) |  | Repair external reference paths, making them relative, or point a reference at a new file. |
| [`entity.setprops`](#entitysetprops) |  | Set layer, colour, linetype or lineweight on entities by handle list or filter. |
| [`layer.translate`](#layertranslate) | destructive | Move everything from old layers to new ones (merging), creating targets from the standard and removing emptied layers. |
| [`fix.bylayer`](#fixbylayer) |  | Set colour, linetype, lineweight and transparency of entities to ByLayer. |
| [`fix.flattenZ`](#fixflattenz) |  | Set stray Z coordinates and elevations to zero on 2D entities. |
| [`fix.purge`](#fixpurge) | destructive | Purge unused named objects. |
| [`fix.units`](#fixunits) |  | Set the drawing's insertion units, optionally scaling the contents from the old unit. |
| [`script.lisp`](#scriptlisp) | needs `--allow-script`, alone in its job | Escape hatch: run an AutoLISP expression. |
| [`draw.entities`](#drawentities) |  | Draw lines, arcs, circles, polylines with bulges and points; properties stay ByLayer. |
| [`hatch.add`](#hatchadd) |  | Hatch closed boundaries (points or entity handles, with island loops) by pattern, scale and angle on a layer, optionally associative; returns handles and areas. |
| [`entity.transform`](#entitytransform) |  | Move, copy, rotate, mirror or scale entities by handle or filter; copies get new handles. |
| [`entity.delete`](#entitydelete) | destructive | Erase the listed or filtered entities. |
| [`block.extract`](#blockextract) |  | List block references with handle, effective name, attributes, dynamic properties, insertion point, rotation, scale, layer and containing room; to a CSV or JSON file and the result. |
| [`area.report`](#areareport) |  | Areas of closed polylines, circles, hatches and regions by handle or filter, nested boundaries subtracted, grouped by layer or room; to a table, a CSV or JSON file, and the result. |
| [`grid.module`](#gridmodule) |  | Lay out a module grid (ceiling, floor or wall tiles) inside a closed boundary, set out by rule; reports border sizes and flags borders under half a module. |
| [`layout.clearances`](#layoutclearances) |  | Test clearance zones (circles, rectangles, convex polygons, door swings, route strips) against entities on chosen layers and references of chosen blocks in model space; reports each intrusion with its depth. |
| [`text.replace`](#textreplace) |  | Find and replace in text, multiline text, attributes, table cells and multileader text, with a dry-run count. |
| [`revcloud.add`](#revcloudadd) |  | Draw a revision cloud (a closed polyline of arcs bulging outward) around a rectangle, points or a closed polyline, with an optional revision tag. |
| [`layerstate.ensure`](#layerstateensure) | ensure | Create or update a named layer state from the drawing's current layer settings, with changes for listed layers, and optionally leave the drawing in that state. |
| [`material.ensure`](#materialensure) | ensure | Create render materials with description, diffuse colour and an optional image texture; existing materials are left alone unless update is true. |
| [`material.assign`](#materialassign) |  | Assign a render material to layers, or to entities by handle or filter, with an optional texture mapping on entities. |
| [`block.export`](#blockexport) | alone in its job | Write each chosen block definition to its own DWG (origin at the block's base point, the drawing's INSUNITS) and a placements.json (format cadbim.placements/1) listing every reference with position, rotation, scale and layer, for family.fromcad and family.place in Revit. |

## doc.setup

Flags: ensure.

Set drawing units, precision, angle conventions, linetype scaling and annotation scales; optionally create everything the standard defines.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `insUnits` | enum(mm\\|cm\\|m\\|in\\|ft) |  |  |  | Drawing insertion units (INSUNITS). Should match the job's length unit. |
| `lengthFormat` | enum(decimal\\|architectural\\|engineering\\|fractional\\|scientific) |  |  |  | Linear unit display (LUNITS). |
| `lengthPrecision` | integer |  |  | >= 0; <= 8 | Decimal places, or the fraction denominator power for architectural (LUPREC). |
| `angleFormat` | enum(decimal_degrees\\|dms\\|grads\\|radians\\|surveyor) |  |  |  | Angle display (AUNITS). |
| `anglePrecision` | integer |  |  | >= 0; <= 8 | AUPREC. |
| `angleBase` | angle |  |  | degrees | Direction of 0 degrees (ANGBASE), counter-clockwise from east. Left unchanged when omitted. |
| `angleDirection` | enum(ccw\\|cw) |  |  |  | Positive angle direction (ANGDIR). Left unchanged when omitted. |
| `measurement` | enum(metric\\|imperial) |  |  |  | MEASUREMENT: which hatch and linetype files apply. |
| `ltScale` | number |  |  | > 0 | Global linetype scale (LTSCALE). |
| `psLtScale` | bool |  |  |  | Scale linetypes in paper space viewports by the viewport scale (PSLTSCALE). Left unchanged when omitted. |
| `msLtScale` | bool |  |  |  | Scale model space linetypes by the annotation scale (MSLTSCALE). Left unchanged when omitted. |
| `annotationScales` | list<string> |  |  |  | Scales to add to the scale list, e.g. ["1:50", "1:100"]. |
| `currentAnnotationScale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | CANNOSCALE for model space. |
| `applyStandard` | bool |  | `false` |  | Also ensure every layer, linetype, text style and dimension style the job's standard defines. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `changed` (map): System variable name to { before, after } for every value changed.

Example (lint clean):

```json
{
  "jobId": "example-autocad-doc-setup",
  "title": "Example: doc.setup",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "doc.setup",
      "args": {
        "insUnits": "mm",
        "lengthFormat": "decimal",
        "lengthPrecision": 0,
        "measurement": "metric",
        "annotationScales": [
          "1:50",
          "1:100"
        ],
        "currentAnnotationScale": "1:100",
        "applyStandard": true
      }
    }
  ]
}
```

## layer.ensure

Flags: ensure.

Create layers with colour, linetype, lineweight and plot flag; existing layers are left alone unless update is true.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `layers` | list<object> | yes |  | at least 1 item | Layers to ensure. |
| `layers[].name` | string | yes |  |  | Layer name. Checked against the standard's layer naming scheme. |
| `layers[].color` | any |  |  |  | ACI number 1-255, "bylayer", "byblock", "#RRGGBB" or "r,g,b". |
| `layers[].linetype` | string |  | `"Continuous"` |  | Linetype name; loaded if missing. |
| `layers[].lineweight` | any |  |  |  | Millimetres from the standard set (0.13, 0.18, 0.25, 0.35, 0.50, 0.70...), or "bylayer", "byblock", "default". |
| `layers[].plot` | bool |  | `true` |  | Plot the layer. |
| `layers[].description` | string |  |  |  | Layer description. |
| `layers[].transparency` | integer |  |  | >= 0; <= 90 | Percent. |
| `layers[].off` | bool |  | `false` |  | Turn the layer off. |
| `layers[].frozen` | bool |  | `false` |  | Freeze the layer. |
| `layers[].locked` | bool |  | `false` |  | Lock the layer. |
| `fromStandard` | bool |  | `true` |  | Take colour, linetype, lineweight and plot flag from the standard's layer list when the entry omits them. |
| `setCurrent` | string |  |  |  | Make this layer current afterwards. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `layers` (map): Layer name to its handle.

Example (lint clean):

```json
{
  "jobId": "example-autocad-layer-ensure",
  "title": "Example: layer.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "layer.ensure",
      "args": {
        "layers": [
          {
            "name": "A-WALL",
            "color": 1,
            "lineweight": 0.5
          },
          {
            "name": "A-DOOR",
            "color": 2
          }
        ]
      }
    }
  ]
}
```

## linetype.ensure

Flags: ensure.

Load linetypes from a .lin file.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `linetypes` | list<object> | yes |  | at least 1 item | Linetypes to load. |
| `linetypes[].name` | string | yes |  |  | Linetype name, e.g. HIDDEN2. |
| `linetypes[].file` | string |  |  |  | Linetype file. Defaults to acadiso.lin for metric drawings, acad.lin for imperial. |
| `linetypes[].description` | string |  |  |  | Description to set. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Example (lint clean):

```json
{
  "jobId": "example-autocad-linetype-ensure",
  "title": "Example: linetype.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "linetype.ensure",
      "args": {
        "linetypes": [
          {
            "name": "HIDDEN"
          },
          {
            "name": "CENTER"
          }
        ]
      }
    }
  ]
}
```

## textstyle.ensure

Flags: ensure.

Create text styles.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `styles` | list<object> | yes |  | at least 1 item | Text styles to ensure. |
| `styles[].name` | string | yes |  |  | Style name. |
| `styles[].font` | string | yes |  |  | Font file or TrueType family, e.g. "Arial" or "romans.shx". |
| `styles[].bigFont` | string |  |  |  | SHX big font, if any. |
| `styles[].height` | length |  | `0` | >= 0; length | Fixed height; 0 lets each text set its height. A non-annotative style takes the model height; an annotative style takes the paper height, in paper units: mm, or inches for an imperial job (stored in paper millimetres or inches by MEASUREMENT, as doc.setup applyStandard does). |
| `styles[].widthFactor` | number |  | `1` | > 0 | Character width factor. |
| `styles[].obliqueAngle` | angle |  | `0` | >= -85; <= 85; degrees | Slant in degrees. |
| `styles[].annotative` | bool |  | `false` |  | Annotative style. |
| `fromStandard` | bool |  | `true` |  | Fill omitted settings from the standard's text style of the same name. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Example (lint clean):

```json
{
  "jobId": "example-autocad-textstyle-ensure",
  "title": "Example: textstyle.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "textstyle.ensure",
      "args": {
        "styles": [
          {
            "name": "SA-NOTES",
            "font": "Arial",
            "height": 2.5,
            "annotative": true
          }
        ]
      }
    }
  ]
}
```

## dimstyle.ensure

Flags: ensure.

Create dimension styles from named dimension variable settings.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `styles` | list<object> | yes |  | at least 1 item | Dimension styles to ensure. |
| `styles[].name` | string | yes |  |  | Style name. |
| `styles[].basedOn` | string |  |  |  | Existing style to copy first. Defaults to the drawing's Standard/ISO-25. |
| `styles[].settings` | map |  |  |  | Dimension variables by name, e.g. { "DIMTXT": 2.5, "DIMASZ": 2.5, "DIMTXSTY": "SA-Text", "DIMBLK": "ArchTick" }. Lengths are paper sizes for annotative styles. |
| `styles[].annotative` | bool |  | `true` |  | Annotative style. |
| `fromStandard` | bool |  | `true` |  | Fill omitted settings from the standard's dimension style of the same name. |
| `setCurrent` | string |  |  |  | Make this style current afterwards. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Example (lint clean):

```json
{
  "jobId": "example-autocad-dimstyle-ensure",
  "title": "Example: dimstyle.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "dimstyle.ensure",
      "args": {
        "styles": [
          {
            "name": "SA-DIM-MM",
            "settings": {
              "DIMTXT": 2.5,
              "DIMDEC": 0
            }
          }
        ]
      }
    }
  ]
}
```

## wall.draw

Draw double-line walls from centrelines with thickness; L, T and X junctions are cleaned so no line crosses a joint, and open ends are capped.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `walls` | list<object> |  |  |  | Individual wall segments. |
| `walls[].id` | string |  |  | matches `[A-Za-z0-9_-]{1,64}` | Id of this wall inside the op's result, e.g. "north". Defaults to w1, w2... in order. |
| `walls[].start` | point2 | yes |  | length | Centreline start. |
| `walls[].end` | point2 | yes |  | length | Centreline end. |
| `walls[].thickness` | length |  |  | > 0; length | Overall thickness. Defaults to the op's thickness. |
| `walls[].layer` | string |  |  |  | Layer for this wall's lines. |
| `walls[].wallType` | string |  |  |  | Wall type name from the standard, recorded on the lines (xdata) and used for layer and hatch. |
| `chains` | list<object> |  |  |  | Connected runs of walls through a list of points; a closed chain draws a room's walls in one go. |
| `chains[].points` | list<point2> | yes |  | at least 2 items; length | Centreline points in order. |
| `chains[].closed` | bool |  | `false` |  | Join the last point back to the first. |
| `chains[].thickness` | length |  |  | > 0; length | Overall thickness. Defaults to the op's thickness. |
| `chains[].layer` | string |  |  |  | Layer for this chain's lines. |
| `chains[].wallType` | string |  |  |  | Wall type name. |
| `chains[].idPrefix` | string |  |  | matches `[A-Za-z0-9_-]{1,32}` | Ids for the segments become <prefix>1, <prefix>2... Defaults to c<n>_. |
| `thickness` | length |  |  | > 0; length | Default overall thickness for walls that give none. |
| `layer` | string |  |  |  | Default layer for wall lines. |
| `justification` | enum(centre\\|left\\|right) |  | `"centre"` |  | Where the given line lies: on the centre, or on the left or right face looking from start to end. |
| `cornerStyle` | enum(mitre\\|butt) |  | `"mitre"` |  | Two-wall corners: mitre, or butt (the thicker wall runs through). |
| `tolerance` | length |  |  | > 0; length | Endpoints this close are joined and an end this close to a wall forms a T. Defaults to 1 mm. |
| `capOpenEnds` | bool |  | `true` |  | Close free wall ends with a cap line. |
| `hatch` | object |  |  |  | Hatch each wall piece. |
| `hatch.pattern` | string | yes |  |  | Hatch pattern name, e.g. ANSI31 or SOLID. |
| `hatch.scale` | number |  | `1` | > 0 | Pattern scale. |
| `hatch.angle` | angle |  | `0` | degrees | Pattern angle. |
| `hatch.layer` | string |  |  |  | Hatch layer. |

Rules across fields: at least one of `walls`, `chains`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `walls` (list<object>): { id, handles, length, thickness, layer } per wall, in order.
- `junctions` (list<object>): { point, kind (corner, tee, cross, inline, other), walls }.
- `openEnds` (list<object>): { wall, point, atStart } for every free end.
- `loops` (list<list>): Closed outline loops as point lists; a room gives an inner and an outer loop.

Example (lint clean):

```json
{
  "jobId": "example-autocad-wall-draw",
  "title": "Example: wall.draw",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "wall.draw",
      "args": {
        "thickness": 100,
        "layer": "A-WALL",
        "chains": [
          {
            "points": [
              [
                0,
                0
              ],
              [
                4000,
                0
              ],
              [
                4000,
                3000
              ],
              [
                0,
                3000
              ]
            ],
            "closed": true,
            "idPrefix": "rm"
          }
        ]
      }
    }
  ]
}
```

## opening.place

Place a door or window block in a wall, cut the wall lines, and set swing and handing.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `kind` | enum(door\\|window) | yes |  |  | Door or window. |
| `block` | string | yes |  |  | Block name of the door or window symbol; its width is scaled to the opening width when the block is unit-sized. |
| `host` | ref |  |  |  | The wall: a wall.draw result ("$w") with wallId, or a handle of one of the wall's lines. |
| `wallId` | string |  |  |  | Which wall of a wall.draw result, e.g. "north". |
| `offset` | length |  |  | length | Distance along the wall from its start to the opening, measured to the point given by measureTo. |
| `measureTo` | enum(centre\\|near_jamb) |  | `"centre"` |  | What offset measures to. |
| `point` | point2 |  |  | length | Any point on the wall; the opening is centred at its projection onto the centreline. |
| `width` | length | yes |  | > 0; length | Clear opening width (structural opening for windows). |
| `height` | length |  |  | > 0; length | Opening height, recorded as an attribute. |
| `sillHeight` | length |  |  | >= 0; length | Window sill height above floor, recorded as an attribute. |
| `hinge` | enum(start\\|end) |  | `"start"` |  | Door: the jamb the leaf is hinged on, as seen along the wall direction. |
| `swingSide` | enum(left\\|right) |  | `"left"` |  | Door: the side of the wall the leaf opens to, looking from wall start to end. |
| `cutWall` | bool |  | `true` |  | Break the wall lines across the opening and add jamb lines. |
| `layer` | string |  |  |  | Layer for the block reference. |
| `mark` | string |  |  |  | Door or window mark; written to the MARK attribute when present. |
| `attributes` | map<string> |  |  |  | Attribute tag to value. |

Rules across fields: exactly one of `offset`, `point`; `offset` requires `host`; `wallId` requires `host`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `block` (ref): Handle of the inserted block reference.
- `centre` (point2): Opening centre on the wall centreline.
- `jambs` (list<point2>): The two jamb points on the centreline.

Example (lint clean):

```json
{
  "jobId": "example-autocad-opening-place",
  "title": "Example: opening.place",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "walls",
      "op": "wall.draw",
      "args": {
        "thickness": 100,
        "layer": "A-WALL",
        "walls": [
          {
            "id": "w1",
            "start": [
              0,
              0
            ],
            "end": [
              5000,
              0
            ]
          }
        ]
      }
    },
    {
      "id": "op1",
      "op": "opening.place",
      "args": {
        "kind": "door",
        "block": "DOOR-SINGLE",
        "host": "$walls",
        "wallId": "w1",
        "offset": 1200,
        "width": 900,
        "layer": "A-DOOR",
        "mark": "D01"
      }
    }
  ]
}
```

## grid.draw

Draw structural grid lines with bubbles and labels, from explicit lines or an orthogonal grid definition.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `lines` | list<object> |  |  |  | Explicit grid lines. |
| `lines[].label` | string | yes |  |  | Bubble text, e.g. "A" or "1". |
| `lines[].start` | point2 | yes |  | length | Line start. |
| `lines[].end` | point2 | yes |  | length | Line end. |
| `orthogonal` | object |  |  |  | A rectangular grid. |
| `orthogonal.origin` | point2 | yes |  | length | Intersection of the first grid in each direction. |
| `orthogonal.xSpacings` | list<length> | yes |  | at least 1 item; length | Bay widths left to right; gives the vertical grid lines. |
| `orthogonal.ySpacings` | list<length> | yes |  | at least 1 item; length | Bay depths bottom to top; gives the horizontal grid lines. |
| `orthogonal.xLabelStart` | string |  | `"1"` |  | First label of the vertical lines; following labels count on (1, 2, 3 or A, B, C). |
| `orthogonal.yLabelStart` | string |  | `"A"` |  | First label of the horizontal lines. |
| `orthogonal.extension` | length |  |  | >= 0; length | How far lines run past the outer grids. Defaults to 1.5 bubble diameters. |
| `orthogonal.rotation` | angle |  | `0` | degrees | Rotation of the whole grid about origin. |
| `bubbles` | enum(start\\|end\\|both) |  | `"both"` |  | Which ends get a bubble. |
| `bubbleDiameter` | length |  |  | > 0; length | Bubble diameter in model units. Defaults to 10 mm plotted at the current annotation scale. |
| `bubbleBlock` | string |  |  |  | Block to use as bubble (with a LABEL attribute) instead of a circle and text. |
| `textStyle` | string |  |  |  | Text style for labels. |
| `linetype` | string |  |  |  | Grid linetype, e.g. CENTER2. |
| `layer` | string |  |  |  | Layer for grid lines and bubbles. |

Rules across fields: at least one of `lines`, `orthogonal`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `grids` (list<object>): { label, handles, start, end } per grid line.

Example (lint clean):

```json
{
  "jobId": "example-autocad-grid-draw",
  "title": "Example: grid.draw",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "grid.draw",
      "args": {
        "orthogonal": {
          "origin": [
            0,
            0
          ],
          "xSpacings": [
            6000,
            6000
          ],
          "ySpacings": [
            7500
          ]
        },
        "layer": "S-GRID"
      }
    }
  ]
}
```

## room.create

Create a closed room boundary, compute its area and place a room tag.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundary` | list<point2> |  |  | at least 3 items; length | Boundary points in order (closed automatically). |
| `inside` | point2 |  |  | length | A point inside the room; the boundary is found from surrounding walls. |
| `from` | ref |  |  |  | A wall.draw result whose inner loops are searched for the inside point. Without it the drawing's lines are used. |
| `name` | string | yes |  |  | Room name, e.g. "Kitchen". |
| `number` | string |  |  |  | Room number, e.g. "G.04". |
| `layer` | string |  |  |  | Layer for the boundary polyline. |
| `tag` | bool |  | `true` |  | Place a room tag. |
| `tagBlock` | string |  |  |  | Tag block with NAME, NUMBER and AREA attributes. Defaults to the standard's room tag. |
| `tagPoint` | point2 |  |  | length | Tag insertion point. Defaults to the boundary's centroid (or a point inside it). |
| `areaUnit` | enum(m2\\|ft2) |  | `"m2"` |  | Unit for the AREA attribute text. |
| `hatch` | object |  |  |  | Hatch the room. |
| `hatch.pattern` | string | yes |  |  | Pattern name. |
| `hatch.scale` | number |  | `1` | > 0 | Pattern scale. |
| `hatch.layer` | string |  |  |  | Hatch layer. |

Rules across fields: exactly one of `boundary`, `inside`; `from` requires `inside`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `boundaryHandle` (ref): Handle of the closed polyline.
- `area` (number): Area in job units squared.
- `perimeter` (length): Perimeter.
- `tagHandle` (ref): Handle of the tag.

Example (lint clean):

```json
{
  "jobId": "example-autocad-room-create",
  "title": "Example: room.create",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "room.create",
      "args": {
        "boundary": [
          [
            0,
            0
          ],
          [
            4000,
            0
          ],
          [
            4000,
            3000
          ],
          [
            0,
            3000
          ]
        ],
        "name": "Meeting",
        "number": "1.04",
        "layer": "A-AREA"
      }
    }
  ]
}
```

## dim.linear

Add one linear dimension.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `start` | point2 | yes |  | length | First measured point. |
| `end` | point2 | yes |  | length | Second measured point. |
| `offset` | length | yes |  | length | Distance from the measured points to the dimension line; positive is to the left looking from start to end. |
| `orientation` | enum(aligned\\|horizontal\\|vertical) |  | `"aligned"` |  | Measure the true distance, or only its horizontal or vertical component. |
| `dimStyle` | string |  |  |  | Dimension style. Defaults to the current style. |
| `layer` | string |  |  |  | Dimension layer. |
| `textOverride` | string |  |  |  | Replace the measured text. Avoid: overridden dimensions are flagged by the audit. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `measurement` (length): Measured value read back.

Example (lint clean):

```json
{
  "jobId": "example-autocad-dim-linear",
  "title": "Example: dim.linear",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "dim.linear",
      "args": {
        "start": [
          0,
          0
        ],
        "end": [
          4000,
          0
        ],
        "offset": 800,
        "orientation": "horizontal"
      }
    }
  ]
}
```

## dim.string

Add a continuous string of dimensions through a list of points, all on one line.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `points` | list<point2> | yes |  | at least 2 items; length | Points to dimension, in order along the string. |
| `offset` | length | yes |  | length | Distance from the first point to the dimension line, perpendicular to the string; positive to the left of the string direction. |
| `orientation` | enum(aligned\\|horizontal\\|vertical) |  | `"aligned"` |  | Direction of the string. |
| `overall` | bool |  | `false` |  | Also add an overall dimension from first to last point. |
| `overallOffset` | length |  |  | length | Offset of the overall dimension. Defaults to offset plus twice the text height. |
| `dimStyle` | string |  |  |  | Dimension style. |
| `layer` | string |  |  |  | Dimension layer. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `measurements` (list<length>): Measured values in order.
- `handles` (list<ref>): Dimension handles.

Example (lint clean):

```json
{
  "jobId": "example-autocad-dim-string",
  "title": "Example: dim.string",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "dim.string",
      "args": {
        "points": [
          [
            0,
            0
          ],
          [
            1200,
            0
          ],
          [
            2100,
            0
          ],
          [
            4000,
            0
          ]
        ],
        "offset": 800,
        "orientation": "horizontal",
        "overall": true
      }
    }
  ]
}
```

## text.add

Add single-line or multiline text at a model height, or at a plotted height for a scale.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `text` | string | yes |  |  | The text. For multiline text, \P starts a new paragraph. |
| `position` | point2 | yes |  | length | Insertion point. |
| `height` | length |  |  | length | Model height. |
| `plottedHeight` | length |  |  | length | Height on paper, in paper units: mm, or inches for an imperial job; with scale gives the model height. Or use a named height from the standard via heightName. |
| `heightName` | string |  |  |  | Named text height from the standard (e.g. "body"), used with scale. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |
| `style` | string |  |  |  | Text style. |
| `layer` | string |  |  |  | Text layer. |
| `rotation` | angle |  | `0` | degrees | Rotation. |
| `justify` | enum(tl\\|tc\\|tr\\|ml\\|mc\\|mr\\|bl\\|bc\\|br) |  | `"bl"` |  | Justification (top/middle/bottom + left/centre/right). For single-line text b means the baseline, not the descender bottom. |
| `multiline` | bool |  | `false` |  | Create multiline text (MTEXT). |
| `width` | length |  |  | > 0; length | Wrap width for multiline text. |
| `annotative` | bool |  | `false` |  | Make the text annotative (height is then the paper height, in paper units: mm, or inches for an imperial job). |

Rules across fields: exactly one of `height`, `plottedHeight`, `heightName`; `plottedHeight` requires `scale`; `heightName` requires `scale`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `height` (length): Model height read back.

Example (lint clean):

```json
{
  "jobId": "example-autocad-text-add",
  "title": "Example: text.add",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "text.add",
      "args": {
        "text": "ALL DIMENSIONS IN MM",
        "position": [
          0,
          -2000
        ],
        "plottedHeight": 2.5,
        "scale": "1:100"
      }
    }
  ]
}
```

## mleader.add

Add a multileader note.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `text` | string | yes |  |  | Note text. |
| `arrowPoint` | point2 | yes |  | length | Where the arrowhead touches. |
| `landingPoint` | point2 | yes |  | length | Where the leader meets the text. |
| `style` | string |  |  |  | Multileader style. |
| `layer` | string |  |  |  | Leader layer. |

Example (lint clean):

```json
{
  "jobId": "example-autocad-mleader-add",
  "title": "Example: mleader.add",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "mleader.add",
      "args": {
        "text": "Skirting type SK1",
        "arrowPoint": [
          1000,
          50
        ],
        "landingPoint": [
          1600,
          700
        ]
      }
    }
  ]
}
```

## table.create

Create a table with a title, header row and data rows.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `position` | point2 | yes |  | length | Top-left corner. |
| `title` | string |  |  |  | Title row text; omitted means no title row. |
| `headers` | list<string> |  |  |  | Column headings. |
| `rows` | list<list<string>> | yes |  |  | Data rows; each row is a list of cell texts. |
| `columnWidths` | list<length> |  |  | length | Width per column. Defaults to fit the text. |
| `rowHeight` | length |  |  | > 0; length | Row height. Defaults to the table style. |
| `style` | string |  |  |  | Table style. |
| `layer` | string |  |  |  | Table layer. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `rowCount` (integer): Rows created, excluding title and header.
- `columnCount` (integer): Columns created.

Example (lint clean):

```json
{
  "jobId": "example-autocad-table-create",
  "title": "Example: table.create",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "table.create",
      "args": {
        "position": [
          6000,
          3000
        ],
        "title": "FINISH LEGEND",
        "headers": [
          "Code",
          "Finish"
        ],
        "rows": [
          [
            "FL1",
            "Oak strip floor"
          ],
          [
            "FL2",
            "Porcelain tile"
          ]
        ]
      }
    }
  ]
}
```

## schedule.fromBlocks

Collect attribute values from block references into a table and/or a CSV file (door, window, furniture schedules).

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `blocks` | list<string> | yes |  | at least 1 item | Block names to include (dynamic blocks by their base name). |
| `attributes` | list<string> | yes |  | at least 1 item | Attribute tags, one column each, in order. |
| `headers` | list<string> |  |  |  | Column headings. Defaults to the tags. |
| `space` | enum(model\\|all) |  | `"model"` |  | Where to look for references. |
| `layers` | list<string> |  |  |  | Only references on these layers. |
| `sortBy` | list<string> |  |  |  | Tags to sort by, in priority order. Numbers in values sort numerically. |
| `groupIdentical` | bool |  | `false` |  | One row per distinct combination of values, with a count column. |
| `countHeader` | string |  | `"QTY"` |  | Heading of the count column. |
| `table` | object |  |  |  | Write a table into the drawing. |
| `table.position` | point2 | yes |  | length | Top-left corner. |
| `table.title` | string |  |  |  | Table title. |
| `table.style` | string |  |  |  | Table style. |
| `table.layer` | string |  |  |  | Table layer. |
| `csv` | string |  |  |  | Write a CSV file with this name into the output folder. |

Rules across fields: at least one of `table`, `csv`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `rowCount` (integer): Rows written.
- `csvPath` (string): Full path of the CSV file. A rehearsal writes it to a temporary folder instead, never over the real file.
- `table` (ref): Table handle.

Example (lint clean):

```json
{
  "jobId": "example-autocad-schedule-fromBlocks",
  "title": "Example: schedule.fromBlocks",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "schedule.fromBlocks",
      "args": {
        "blocks": [
          "DOOR-SINGLE"
        ],
        "attributes": [
          "MARK",
          "WIDTH"
        ],
        "sortBy": [
          "MARK"
        ],
        "csv": "door-schedule.csv"
      }
    }
  ]
}
```

## block.define

Flags: ensure.

Define a block from simple entities, or from a drawing file.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Block name. |
| `basePoint` | point2 |  | `[0,0]` | length | Base point. Defaults to 0,0, or with fromFile to the source drawing's INSBASE. |
| `entities` | list<object> |  |  |  | Block contents. |
| `entities[].kind` | enum(line\\|polyline\\|circle\\|arc\\|text\\|attdef) | yes |  |  | Entity kind. |
| `entities[].points` | list<point2> |  |  | length | line: 2 points; polyline: 2 or more. |
| `entities[].closed` | bool |  | `false` |  | polyline: closed. |
| `entities[].center` | point2 |  |  | length | circle, arc: centre. text, attdef: insertion point. |
| `entities[].radius` | length |  |  | > 0; length | circle, arc: radius. |
| `entities[].startAngle` | angle |  |  | degrees | arc: start angle. |
| `entities[].endAngle` | angle |  |  | degrees | arc: end angle. |
| `entities[].text` | string |  |  |  | text: the text. |
| `entities[].tag` | string |  |  |  | attdef: tag. |
| `entities[].prompt` | string |  |  |  | attdef: prompt. |
| `entities[].defaultValue` | string |  |  |  | attdef: default value. |
| `entities[].height` | length |  |  | > 0; length | text, attdef: height. |
| `entities[].layer` | string |  |  |  | Entity layer. Defaults to 0 so the block takes the insertion's layer. |
| `fromFile` | string |  |  |  | A .dwg file whose model space becomes the block. |
| `annotative` | bool |  | `false` |  | Annotative block. |
| `units` | enum(unitless\\|mm\\|cm\\|m\\|in\\|ft) |  |  |  | Block insertion units. |
| `redefine` | bool |  | `false` |  | Replace an existing definition of the same name (existing references update). |

Rules across fields: exactly one of `entities`, `fromFile`.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-define",
  "title": "Example: block.define",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.define",
      "args": {
        "name": "NORTH-ARROW",
        "entities": [
          {
            "kind": "circle",
            "center": [
              0,
              0
            ],
            "radius": 500
          },
          {
            "kind": "polyline",
            "points": [
              [
                0,
                500
              ],
              [
                -200,
                -300
              ],
              [
                0,
                -100
              ],
              [
                200,
                -300
              ]
            ],
            "closed": true
          }
        ]
      }
    }
  ]
}
```

## block.insert

Insert a block at one or many points, with attribute values and dynamic properties.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Block name; must be defined in the drawing or be loadable from the standard's block library. |
| `position` | point2 |  |  | length | Insertion point. |
| `positions` | list<point2> |  |  | length | Several insertion points (one reference each). |
| `rotation` | angle |  | `0` | degrees | Rotation. |
| `scale` | number |  | `1` | > 0 | Uniform scale. |
| `layer` | string |  |  |  | Layer for the reference. |
| `attributes` | map<string> |  |  |  | Attribute tag to value. |
| `dynamicProperties` | map |  |  |  | Dynamic block property name to value (lengths in job units, angles in degrees, lists by value name). |

Rules across fields: exactly one of `position`, `positions`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `handles` (list<ref>): Handles of the references, in order of positions.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-insert",
  "title": "Example: block.insert",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.insert",
      "args": {
        "name": "CHAIR-01",
        "positions": [
          [
            1000,
            1000
          ],
          [
            2000,
            1000
          ]
        ],
        "layer": "I-FURN"
      }
    }
  ]
}
```

## block.attrs.set

Set attribute values on block references.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | References to change. |
| `block` | string |  |  |  | Change every reference of this block. |
| `where` | map<string> |  |  |  | With block: only references whose attributes have these values. |
| `values` | map<string> | yes |  |  | Attribute tag to new value. |
| `failOnMissingTag` | bool |  | `true` |  | Fail when a reference lacks a tag. When false the tag is skipped and reported. |

Rules across fields: exactly one of `handles`, `block`; `where` requires `block`.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-attrs-set",
  "title": "Example: block.attrs.set",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.attrs.set",
      "args": {
        "block": "ROOM-TAG",
        "where": {
          "NUMBER": "1.04"
        },
        "values": {
          "FINISH": "FL1"
        }
      }
    }
  ]
}
```

## block.dynprops.set

Set dynamic block properties (visibility state, stretch distance, flip...) on references.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | References to change. |
| `block` | string |  |  |  | Change every reference of this dynamic block. |
| `properties` | map | yes |  |  | Property name to value (lengths in job units, angles in degrees). |

Rules across fields: exactly one of `handles`, `block`.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-dynprops-set",
  "title": "Example: block.dynprops.set",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.dynprops.set",
      "args": {
        "block": "DESK-DYN",
        "properties": {
          "Visibility1": "Left"
        }
      }
    }
  ]
}
```

## layout.create

Flags: ensure.

Create a paper space layout from a template, a page setup or a paper size, with an optional title block.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Layout name, e.g. the sheet number. |
| `template` | string |  |  |  | A .dwt or .dwg to copy the layout from. |
| `templateLayout` | string |  |  |  | Layout name in the template. Defaults to its first layout. |
| `pageSetup` | string |  |  |  | Named page setup to apply (from the drawing or the template). |
| `plotDevice` | string |  | `"DWG To PDF.pc3"` |  | Plotter configuration. |
| `paperSize` | string |  |  |  | Canonical media name, e.g. "ISO_full_bleed_A1_(841.00_x_594.00_MM)" or "ARCH_D_(36.00_x_24.00_Inches)". |
| `orientation` | enum(landscape\\|portrait) |  | `"landscape"` |  | Paper orientation. |
| `plotStyleTable` | string |  |  |  | CTB or STB file name. |
| `titleBlock` | string |  |  |  | Block to insert at the paper origin. |
| `titleBlockAttributes` | map<string> |  |  |  | Title block attribute values. |
| `makeCurrent` | bool |  | `false` |  | Switch to the layout once the job has committed (report extra currentLayout). A rehearsal never switches; a failed switch is a warning and the job stays committed. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Rules across fields: `templateLayout` requires `template`; `titleBlockAttributes` requires `titleBlock`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `layout` (ref): Layout handle.
- `titleBlock` (ref): Title block reference handle.

Example (lint clean):

```json
{
  "jobId": "example-autocad-layout-create",
  "title": "Example: layout.create",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "layout.create",
      "args": {
        "name": "A-101",
        "paperSize": "ISO_full_bleed_A1_(841.00_x_594.00_MM)",
        "titleBlock": "TB-A1",
        "titleBlockAttributes": {
          "SHEET_NO": "A-101"
        }
      }
    }
  ]
}
```

## viewport.create

Create a paper space viewport at a scale showing a model area, lock it and freeze layers in it.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `layout` | string | yes |  |  | Layout name. |
| `paperCentre` | point2 | yes |  | length | Viewport centre on the sheet, in paper units: mm, or inches for an imperial job. |
| `width` | length |  |  | > 0; length | Viewport width on the sheet, in paper units: mm, or inches for an imperial job. |
| `height` | length |  |  | > 0; length | Viewport height on the sheet, in paper units: mm, or inches for an imperial job. |
| `modelCentre` | point2 |  |  | length | Model point at the viewport centre. |
| `modelWindow` | list<point2> |  |  | at least 2 items; at most 2 items; length | Two opposite model corners to show; sets the size from the scale when width and height are omitted. |
| `scale` | string | yes |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |
| `lock` | bool |  | `true` |  | Lock the display. |
| `frozenLayers` | list<string> |  |  |  | Layers frozen in this viewport only. |
| `twist` | angle |  | `0` | degrees | View twist. |
| `layer` | string |  |  |  | Viewport layer (normally a non-plotting layer). |

Rules across fields: exactly one of `modelCentre`, `modelWindow`; `modelCentre` requires `width`, `height`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `viewport` (ref): Viewport handle.
- `scaleRatio` (number): Model length per printed length read back (100 for 1:100, whatever the units).

Example (lint clean):

```json
{
  "jobId": "example-autocad-viewport-create",
  "title": "Example: viewport.create",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "viewport.create",
      "args": {
        "layout": "A-101",
        "paperCentre": [
          400,
          300
        ],
        "modelWindow": [
          [
            0,
            0
          ],
          [
            30000,
            20000
          ]
        ],
        "scale": "1:100"
      }
    }
  ]
}
```

## viewports.layout

Place several model views on a layout automatically, in aligned rows inside the drawable area.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `layout` | string | yes |  |  | Layout name. |
| `area` | object |  |  |  | Drawable area of the sheet, in paper units: mm, or inches for an imperial job (inside the title block borders). When omitted, the layout's limits are used, which is only allowed on a layout without a title block (an attributed block reference). |
| `area.min` | point2 | yes |  | length | Lower-left corner. |
| `area.max` | point2 | yes |  | length | Upper-right corner. |
| `views` | list<object> | yes |  | at least 1 item | Views in reading order. |
| `views[].id` | string | yes |  |  | Name for this view in the result. |
| `views[].modelWindow` | list<point2> | yes |  | at least 2 items; at most 2 items; length | Two opposite model corners to show. |
| `views[].scale` | string | yes |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |
| `views[].frozenLayers` | list<string> |  |  |  | Layers frozen in this viewport. |
| `views[].title` | string |  |  |  | View title text placed under the viewport. |
| `lock` | bool |  | `true` |  | Lock the viewports. |
| `layer` | string |  |  |  | Viewport layer. |
| `margin` | length |  | `0` | >= 0; length | Clear space inside the drawable area on every side, in paper units. |
| `gapX` | length |  | `0` | >= 0; length | Horizontal gap between views, in paper units. |
| `gapY` | length |  | `0` | >= 0; length | Vertical gap between rows, in paper units. |
| `rowAlign` | enum(left\\|centre\\|distribute) |  | `"left"` |  | Horizontal placement of each row. |
| `itemAlign` | enum(top\\|middle\\|bottom) |  | `"top"` |  | Vertical placement of views inside a row. |
| `failIfNotFit` | bool |  | `true` |  | Fail the op when any view does not fit. When false, views that fit are placed and the rest are listed in outputs.notPlaced. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `placements` (list<object>): One per placed view: { id, viewport, centre [x, y], min [x, y], max [x, y], row }.
- `notPlaced` (list<object>): Views that did not fit: { id, reason } with reason larger_than_area or no_space.

Example (lint clean):

```json
{
  "jobId": "example-autocad-viewports-layout",
  "title": "Example: viewports.layout",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "viewports.layout",
      "args": {
        "layout": "A-101",
        "area": {
          "min": [
            20,
            20
          ],
          "max": [
            700,
            574
          ]
        },
        "views": [
          {
            "id": "plan",
            "modelWindow": [
              [
                0,
                0
              ],
              [
                30000,
                20000
              ]
            ],
            "scale": "1:100",
            "title": "GROUND FLOOR PLAN"
          },
          {
            "id": "detail",
            "modelWindow": [
              [
                0,
                0
              ],
              [
                3000,
                2000
              ]
            ],
            "scale": "1:20",
            "title": "DETAIL 1"
          }
        ],
        "margin": 10,
        "gapX": 15,
        "gapY": 15
      }
    }
  ]
}
```

## titleblock.fill

Fill title block attributes on layouts.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `layouts` | list<string> |  |  |  | Layouts to fill. Defaults to every layout. |
| `block` | string |  |  |  | Title block name. Defaults to the standard's title block. |
| `values` | map<string> |  |  |  | Attribute tag to value for every layout. Placeholders: {layout}, {date}, {drawing}. |
| `perLayout` | map<map<string>> |  |  |  | Layout name to { tag: value } for values that differ per sheet. |
| `requireAll` | bool |  | `true` |  | Fail when a tag the standard marks required stays empty. |

Rules across fields: at least one of `values`, `perLayout`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `filled` (list<object>): { layout, block, tag, value } per attribute written (values already equal are listed too).
- `missing` (list<object>): { layout, block, tags } per title block that lacks some of the requested tags; those values were not written.

Example (lint clean):

```json
{
  "jobId": "example-autocad-titleblock-fill",
  "title": "Example: titleblock.fill",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "titleblock.fill",
      "args": {
        "values": {
          "PROJECT": "2417 Harbour Offices"
        },
        "perLayout": {
          "A-101": {
            "SHEET_TITLE": "GROUND FLOOR PLAN"
          }
        }
      }
    }
  ]
}
```

## plot.pdf

Flags: alone in its job (only `plot.pdf` ops).

Plot layouts to PDF with a page setup and plot style table, naming files by pattern. A job holding plot.pdf may hold only plot.pdf ops; commit drawing changes in an earlier job.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `layouts` | list<string> |  |  |  | Layouts in order. Defaults to every layout in tab order. |
| `pageSetup` | string |  |  |  | Page setup to plot with. Defaults to each layout's own settings. |
| `plotDevice` | string |  | `"DWG To PDF.pc3"` |  | PDF plotter configuration. |
| `paperSize` | string |  |  |  | Canonical media name, overriding the page setup. |
| `plotStyleTable` | string |  |  |  | CTB or STB file, overriding the page setup. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `fileName` | string |  |  |  | File name pattern. Placeholders: {drawing}, {layout}, {sheetNumber}, {sheetName}, {view}, {date}, {jobId}. Example: "{drawing}-{layout}.pdf". Names are checked against the standard's file naming rule when one is set. |
| `combine` | bool |  | `false` |  | One multi-sheet PDF instead of one file per layout. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `files` (list<object>): { layout, path, pages } per file written.

Example (lint clean):

```json
{
  "jobId": "example-autocad-plot-pdf",
  "title": "Example: plot.pdf",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "plot.pdf",
      "args": {
        "layouts": [
          "A-101",
          "A-102"
        ],
        "fileName": "{drawing}-{layout}.pdf"
      }
    }
  ]
}
```

## xref.attach

Attach or overlay an external reference.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `path` | string | yes |  |  | Path of the .dwg to reference. |
| `name` | string |  |  |  | Reference name. Defaults to the file name. |
| `mode` | enum(attach\\|overlay) |  | `"overlay"` |  | Overlay references do not nest into drawings that reference this one. |
| `position` | point2 |  | `[0,0]` | length | Insertion point. |
| `rotation` | angle |  | `0` | degrees | Rotation. |
| `scale` | number |  | `1` | > 0 | Uniform scale. |
| `pathType` | enum(relative\\|full\\|none) |  | `"relative"` |  | How the path is stored. |
| `layer` | string |  |  |  | Layer for the reference. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `reference` (ref): Block reference handle.
- `storedPath` (string): Path as stored.

Example (lint clean):

```json
{
  "jobId": "example-autocad-xref-attach",
  "title": "Example: xref.attach",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "xref.attach",
      "args": {
        "path": "C:/Projects/2417/xref/2417-BASE.dwg",
        "mode": "overlay"
      }
    }
  ]
}
```

## xref.repath

Repair external reference paths, making them relative, or point a reference at a new file.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `names` | list<string> |  |  |  | References to repath. Defaults to every unresolved reference. |
| `newPath` | string |  |  |  | New file for a single named reference. |
| `searchFolders` | list<string> |  |  |  | Folders to search for missing files by name. |
| `pathType` | enum(relative\\|full) |  | `"relative"` |  | How paths are stored afterwards. |
| `reload` | bool |  | `true` |  | Reload the changed references after repathing, in commit only: a rehearsal never reloads. A reload is not undone if a later op of the same job fails, so put xref.repath last or alone. |

Rules across fields: `newPath` requires `names`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `references` (list<object>): { name, before, after, resolved } per reference.

Example (lint clean):

```json
{
  "jobId": "example-autocad-xref-repath",
  "title": "Example: xref.repath",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "xref.repath",
      "args": {
        "searchFolders": [
          "C:/Projects/2417/xref"
        ],
        "pathType": "relative"
      }
    }
  ]
}
```

## entity.setprops

Set layer, colour, linetype or lineweight on entities by handle list or filter.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | Entities to change. |
| `filter` | object |  |  |  | Select entities instead of listing them. |
| `filter.layer` | string |  |  |  | On this layer (wildcards * and ? allowed). |
| `filter.type` | string |  |  |  | DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, DIMENSION. |
| `filter.block` | string |  |  |  | References of this block (with type INSERT). |
| `filter.space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. |
| `setLayer` | string |  |  |  | New layer. |
| `color` | any |  |  |  | ACI number 1-255, "bylayer", "byblock", "#RRGGBB" or "r,g,b". |
| `linetype` | string |  |  |  | New linetype, or "bylayer". |
| `lineweight` | any |  |  |  | Millimetres from the standard set (0.13, 0.18, 0.25, 0.35, 0.50, 0.70...), or "bylayer", "byblock", "default". |
| `linetypeScale` | number |  |  | > 0 | Entity linetype scale. |

Rules across fields: exactly one of `handles`, `filter`; at least one of `setLayer`, `color`, `linetype`, `lineweight`, `linetypeScale`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Entities changed.

Example (lint clean):

```json
{
  "jobId": "example-autocad-entity-setprops",
  "title": "Example: entity.setprops",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "entity.setprops",
      "args": {
        "filter": {
          "layer": "WALLS",
          "space": "model"
        },
        "setLayer": "A-WALL"
      }
    }
  ]
}
```

## layer.translate

Flags: destructive.

Move everything from old layers to new ones (merging), creating targets from the standard and removing emptied layers.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `map` | map<string> | yes |  |  | Old layer name to new layer name. Old names may use * and ? wildcards. |
| `createTargets` | bool |  | `true` |  | Create missing target layers (from the standard's layer list when listed there). |
| `deleteEmptied` | bool |  | `true` |  | Delete old layers left empty. Layer 0, Defpoints and the current layer are never deleted. |
| `includeBlocks` | bool |  | `true` |  | Also translate entities inside block definitions. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `moves` (list<object>): { from, to, entities } per mapping applied.
- `deletedLayers` (list<string>): Layers deleted.

Example (lint clean):

```json
{
  "jobId": "example-autocad-layer-translate",
  "title": "Example: layer.translate",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "layer.translate",
      "args": {
        "map": {
          "WALLS": "A-WALL",
          "DOORS": "A-DOOR"
        }
      }
    }
  ]
}
```

## fix.bylayer

Set colour, linetype, lineweight and transparency of entities to ByLayer.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `properties` | list<enum(color\\|linetype\\|lineweight\\|transparency)> |  | `["color","linetype","lineweight","transparency"]` | each one of `color`, `linetype`, `lineweight`, `transparency` | Properties to reset. |
| `layers` | list<string> |  |  |  | Only entities on these layers. Defaults to all. |
| `includeBlocks` | bool |  | `true` |  | Also fix entities inside block definitions (they become ByBlock where the block is meant to take the insertion's properties). |
| `space` | enum(model\\|paper\\|all) |  | `"all"` |  | Where to fix. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `counts` (map): Property name to number of entities changed.

Example (lint clean):

```json
{
  "jobId": "example-autocad-fix-bylayer",
  "title": "Example: fix.bylayer",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "fix.bylayer",
      "args": {}
    }
  ]
}
```

## fix.flattenZ

Set stray Z coordinates and elevations to zero on 2D entities.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `tolerance` | length |  | `0` | >= 0; length | Z values within this of zero are left alone. |
| `includeBlocks` | bool |  | `true` |  | Also flatten inside block definitions. |
| `skip3dSolids` | bool |  | `true` |  | Reporting only: 3D solids, surfaces and meshes are never flattened either way; false lists them as "never flattened" instead of skipped. |
| `space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to flatten. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Entities changed.

Example (lint clean):

```json
{
  "jobId": "example-autocad-fix-flattenZ",
  "title": "Example: fix.flattenZ",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "fix.flattenZ",
      "args": {}
    }
  ]
}
```

## fix.purge

Flags: destructive.

Purge unused named objects.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `kinds` | list<enum(blocks\\|layers\\|linetypes\\|textstyles\\|dimstyles\\|mlinestyles\\|tablestyles\\|mleaderstyles\\|materials\\|regapps\\|all)> |  | `["all"]` | each one of `blocks`, `layers`, `linetypes`, `textstyles`, `dimstyles`, `mlinestyles`, `tablestyles`, `mleaderstyles`, `materials`, `regapps`, `all` | What to purge. "all" is every kind except regapps. Visual styles are never purged (built-in ones cannot be told from user ones). |
| `keep` | list<string> |  |  |  | Names never purged, e.g. layers the standard requires. |
| `repeat` | bool |  | `true` |  | Repeat until nothing more can be purged (nested items). |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `purged` (map): Kind to the list of names purged.

Example (lint clean):

```json
{
  "jobId": "example-autocad-fix-purge",
  "title": "Example: fix.purge",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "fix.purge",
      "args": {
        "kinds": [
          "all"
        ],
        "keep": [
          "TB-A1"
        ]
      }
    }
  ]
}
```

## fix.units

Set the drawing's insertion units, optionally scaling the contents from the old unit.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `insUnits` | enum(mm\\|cm\\|m\\|in\\|ft) | yes |  |  | New insertion units. |
| `scaleContents` | bool |  | `false` |  | Scale model space by the factor from the old to the new unit (fixes a drawing drawn in the wrong unit). |
| `basePoint` | point2 |  | `[0,0]` | length | Base point for scaling. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `before` (string): Units before.
- `factor` (number): Scale factor applied (1 when not scaled).
- `geometryScaled` (bool): True when model space was scaled.

Example (lint clean):

```json
{
  "jobId": "example-autocad-fix-units",
  "title": "Example: fix.units",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "fix.units",
      "args": {
        "insUnits": "mm"
      }
    }
  ]
}
```

## script.lisp

Flags: needs `--allow-script`, alone in its job.

Escape hatch: run an AutoLISP expression. Refused unless the run request sets allowScript. The op must be alone in its job, and the job may not carry verify.checks (verify.capture is allowed). A rehearsal checks the code but does not evaluate it; a commit runs only after an identical rehearsal (same job content) at the same document fingerprint. Its changes join the job's undo step but are not rolled back if a later step of the job fails: such a job reports status failed with error.details.scriptChangesRemain true, never rolled_back; one U removes the step. Validation is a best-effort lint: code that visibly prompts, runs or queues commands is refused.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `code` | string | yes |  |  | AutoLISP source. Must not prompt for input. |
| `purpose` | string | yes |  |  | Plain description of what the code does, recorded in the report. |
| `timeoutSeconds` | integer |  | `60` | >= 1; <= 600 | Recorded only: evaluation runs on AutoCAD's main thread and cannot be interrupted. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `value` (any): The value the expression returned, converted to JSON where possible.

Example (lint clean):

```json
{
  "jobId": "example-autocad-script-lisp",
  "title": "Example: script.lisp",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "script.lisp",
      "args": {
        "code": "(entmake '((0 . \"LINE\") (8 . \"A-ANNO\") (10 0.0 0.0 0.0) (11 1000.0 0.0 0.0)))",
        "purpose": "Draw one 1000 mm line on A-ANNO (test of the escape hatch)"
      }
    }
  ]
}
```

## draw.entities

Draw lines, arcs, circles, polylines with bulges and points; properties stay ByLayer.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `entities` | list<object> | yes |  | at least 1 item | Entities in drawing order. |
| `entities[].id` | string |  |  | matches `[A-Za-z0-9_-]{1,64}` | Name of this entity in outputs.ids, e.g. "edge-1". |
| `entities[].kind` | enum(line\\|polyline\\|arc\\|circle\\|point) | yes |  |  | Entity kind. |
| `entities[].points` | list<point2> |  |  | length | line: 2 points; polyline: 2 or more; arc: 3 points (start, a point on the arc, end) instead of center, radius and angles; point: 1 point. |
| `entities[].bulges` | list<number> |  |  |  | polyline: bulge per segment (segment i runs from point i to point i+1; a closed polyline has one more, back to the first). Positive arcs to the right of the travel direction, 1 is a half circle. Omitted means straight. |
| `entities[].closed` | bool |  | `false` |  | polyline: closed. |
| `entities[].center` | point2 |  |  | length | arc, circle: centre. |
| `entities[].radius` | length |  |  | > 0; length | arc, circle: radius. |
| `entities[].startAngle` | angle |  |  | degrees | arc: start angle; the arc runs counter-clockwise to endAngle. |
| `entities[].endAngle` | angle |  |  | degrees | arc: end angle. |
| `entities[].layer` | string |  |  |  | Layer for this entity. Defaults to the op's layer. |
| `layer` | string |  |  |  | Layer for the entities. Defaults to the current layer. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `handles` (list<ref>): Handles in the order of entities.
- `ids` (map<ref>): Entity id to handle, for entities that have an id.

Example (lint clean):

```json
{
  "jobId": "example-autocad-draw-entities",
  "title": "Example: draw.entities",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "draw.entities",
      "args": {
        "layer": "A-ANNO",
        "entities": [
          {
            "id": "edge-1",
            "kind": "line",
            "points": [
              [
                0,
                0
              ],
              [
                1000,
                0
              ]
            ]
          },
          {
            "kind": "polyline",
            "points": [
              [
                0,
                0
              ],
              [
                1000,
                0
              ],
              [
                1000,
                1000
              ]
            ],
            "bulges": [
              0,
              0.5,
              0
            ],
            "closed": true
          },
          {
            "kind": "arc",
            "center": [
              0,
              0
            ],
            "radius": 500,
            "startAngle": 0,
            "endAngle": 90
          },
          {
            "kind": "arc",
            "points": [
              [
                0,
                0
              ],
              [
                500,
                200
              ],
              [
                1000,
                0
              ]
            ]
          },
          {
            "kind": "circle",
            "center": [
              2000,
              0
            ],
            "radius": 250,
            "layer": "A-ANNO-SYMB"
          },
          {
            "kind": "point",
            "points": [
              [
                3000,
                0
              ]
            ]
          }
        ]
      }
    }
  ]
}
```

## hatch.add

Hatch closed boundaries (points or entity handles, with island loops) by pattern, scale and angle on a layer, optionally associative; returns handles and areas.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundaries` | list<object> | yes |  | at least 1 item | Outer boundaries. Each gives points or a handle, and optional islands (loops inside it left unhatched). |
| `boundaries[].points` | list<point2> |  |  | at least 3 items; length | Loop vertices in order; the loop is closed automatically. |
| `boundaries[].bulges` | list<number> |  |  |  | Bulge per segment (segment i runs from point i to point i+1, the last back to the first); positive arcs to the right of the travel direction, 1 is a half circle. Omitted means straight. |
| `boundaries[].handle` | ref |  |  |  | A closed entity to use as the loop: closed polyline, circle, ellipse, closed spline. |
| `boundaries[].islands` | list<object> |  |  |  | Loops inside this boundary that stay unhatched. |
| `boundaries[].islands[].points` | list<point2> |  |  | at least 3 items; length | Loop vertices in order; the loop is closed automatically. |
| `boundaries[].islands[].bulges` | list<number> |  |  |  | Bulge per segment (segment i runs from point i to point i+1, the last back to the first); positive arcs to the right of the travel direction, 1 is a half circle. Omitted means straight. |
| `boundaries[].islands[].handle` | ref |  |  |  | A closed entity to use as the loop: closed polyline, circle, ellipse, closed spline. |
| `pattern` | string | yes |  |  | Pattern name from the drawing's pattern file (acadiso.pat for metric drawings, acad.pat for imperial), "SOLID", or "_USER" for user-defined lines with spacing. |
| `scale` | number |  | `1` | > 0 | Pattern scale (predefined patterns). |
| `angle` | angle |  | `0` | degrees | Pattern angle. |
| `spacing` | length |  |  | > 0; length | Line spacing of a user-defined pattern ("_USER"). |
| `double` | bool |  | `false` |  | User-defined pattern: add a second set of lines at 90 degrees. |
| `origin` | point2 |  |  | length | Pattern origin. Defaults to the drawing origin. |
| `layer` | string |  |  |  | Hatch layer. Defaults to the current layer. |
| `associative` | bool |  | `false` |  | Keep the hatch tied to its boundary entities. Needs handle loops, or drawBoundary for point loops. |
| `drawBoundary` | bool |  | `false` |  | Also draw a closed polyline for every point loop (on boundaryLayer), and use it as the loop. |
| `boundaryLayer` | string |  |  |  | Layer for drawn boundaries. Defaults to the hatch layer. |
| `separate` | bool |  | `false` |  | One hatch per boundary instead of one hatch holding every boundary. |
| `drawOrder` | enum(behindBoundary\\|back\\|none) |  | `"behindBoundary"` |  | Draw order of new hatches: behind their boundary entities (as HATCH does by default), at the back of the space, or unchanged. |

Rules across fields: `boundaryLayer` requires `drawBoundary`; in each `boundaries[]`: exactly one of `points`, `handle`; in each `boundaries[].islands[]`: exactly one of `points`, `handle`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `handles` (list<ref>): Hatch handles, one per hatch.
- `areas` (list<number>): Area of each hatch read back from AutoCAD, in job units squared.
- `area` (number): Total area, in job units squared.
- `boundaryHandles` (list<ref>): Handles of drawn boundary polylines.

Example (lint clean):

```json
{
  "jobId": "example-autocad-hatch-add",
  "title": "Example: hatch.add",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "hatch.add",
      "args": {
        "boundaries": [
          {
            "points": [
              [
                0,
                0
              ],
              [
                4000,
                0
              ],
              [
                4000,
                3000
              ],
              [
                0,
                3000
              ]
            ],
            "islands": [
              {
                "handle": "2A1"
              }
            ]
          }
        ],
        "pattern": "ANSI31",
        "scale": 10,
        "angle": 45,
        "layer": "A-HATCH",
        "drawBoundary": true,
        "boundaryLayer": "A-HATCH-BDRY",
        "associative": true
      }
    }
  ]
}
```

## entity.transform

Move, copy, rotate, mirror or scale entities by handle or filter; copies get new handles.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | Entities to transform. |
| `filter` | object |  |  |  | Select entities instead of listing them. Entities on locked layers are skipped and counted. |
| `filter.layer` | string |  |  |  | On this layer (wildcards * and ? allowed). |
| `filter.type` | string |  |  |  | DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, HATCH. |
| `filter.block` | string |  |  |  | References of this block, by its effective name (with type INSERT or no type). |
| `filter.space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. |
| `move` | object |  |  |  | Move by the vector from one point to another. |
| `move.from` | point2 | yes |  | length | Base point. |
| `move.to` | point2 | yes |  | length | Where the base point goes. |
| `rotate` | object |  |  |  | Rotate about a point. |
| `rotate.base` | point2 | yes |  | length | Centre of rotation. |
| `rotate.angle` | angle | yes |  | degrees | Counter-clockwise angle. |
| `scale` | object |  |  |  | Scale uniformly about a point. |
| `scale.base` | point2 | yes |  | length | Base point. |
| `scale.factor` | number | yes |  | > 0 | Scale factor. |
| `mirror` | object |  |  |  | Mirror about a line. |
| `mirror.axisStart` | point2 | yes |  | length | First point of the mirror line. |
| `mirror.axisEnd` | point2 | yes |  | length | Second point of the mirror line. |
| `mirror.text` | enum(readable\\|mirrored) |  | `"readable"` |  | Single-line text and attributes: kept readable (as MIRRTEXT 0 does) or mirrored. |
| `copy` | bool |  | `false` |  | Transform copies and leave the originals in place. |
| `copies` | integer |  | `1` | >= 1; <= 1000 | With copy: how many copies; copy n is the original transformed n times (a linear or polar array). |
| `expectCount` | integer |  |  |  | When given, the op fails unless exactly this many entities are selected. |

Rules across fields: exactly one of `handles`, `filter`; exactly one of `move`, `rotate`, `scale`, `mirror`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `handles` (list<ref>): Copies in order (copy 1 of every entity, then copy 2...), or the transformed entities when not copying.
- `count` (integer): Entities selected.

Example (lint clean):

```json
{
  "jobId": "example-autocad-entity-transform",
  "title": "Example: entity.transform",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "entity.transform",
      "args": {
        "handles": [
          "2A1",
          "2A2"
        ],
        "rotate": {
          "base": [
            0,
            0
          ],
          "angle": 90
        },
        "copy": true,
        "copies": 3
      }
    }
  ]
}
```

## entity.delete

Flags: destructive.

Erase the listed or filtered entities. Refused unless their number equals expectCount; entities on locked layers are refused.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | Entities to erase, from a snapshot or an earlier op. |
| `filter` | object |  |  |  | Select entities instead of listing them. Entities on locked layers fail the op. |
| `filter.layer` | string |  |  |  | On this layer (wildcards * and ? allowed). |
| `filter.type` | string |  |  |  | DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, HATCH. |
| `filter.block` | string |  |  |  | References of this block, by its effective name (with type INSERT or no type). |
| `filter.space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. |
| `expectCount` | integer | yes |  | >= 1 | Must equal the number of entities selected; guards against a list or filter built by mistake. |

Rules across fields: exactly one of `handles`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `deleted` (list<ref>): Handles erased.
- `count` (integer): Entities erased.

Example (lint clean):

```json
{
  "jobId": "example-autocad-entity-delete",
  "title": "Example: entity.delete",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "entity.delete",
      "args": {
        "filter": {
          "layer": "TEMP-*",
          "type": "LINE"
        },
        "expectCount": 12
      }
    }
  ]
}
```

## block.extract

List block references with handle, effective name, attributes, dynamic properties, insertion point, rotation, scale, layer and containing room; to a CSV or JSON file and the result.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `blocks` | list<string> |  |  |  | Block names (effective names; wildcards allowed). Defaults to every named block. |
| `space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. References nested inside other blocks are counted, not listed. |
| `layers` | list<string> |  |  |  | Only references on these layers (wildcards allowed). |
| `attributes` | list<string> |  |  |  | Attribute tags to list, in this column order. Defaults to every tag found. |
| `dynamicProperties` | bool |  | `true` |  | List dynamic block properties (distances in job units, angles in degrees). |
| `rooms` | object |  |  |  | Find the room each item lies in: closed polylines on these layers are room boundaries. The room's name and number come from a block reference inside it with NAME and NUMBER attributes (tagBlock when given), else from a multiline text inside it whose first two lines are name and number (as room.create writes them). |
| `rooms.layers` | list<string> | yes |  | at least 1 item | Layers of the room boundary polylines (wildcards allowed). |
| `rooms.tagBlock` | string |  |  |  | Room tag block with NAME and NUMBER attributes. |
| `fileName` | string |  |  |  | Plain file name written into the job's output folder; .csv or .json decides the format. Omitted means no file. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |
| `maxInline` | integer |  | `500` | >= 0; <= 5000 | Records returned in outputs.records (the file always has every record). |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): References listed.
- `records` (list<object>): { handle, block, layer, space, position, rotation, scale, attributes, dynamicProperties, room } per reference, up to maxInline.
- `truncated` (bool): True when records holds fewer than count.
- `path` (string): Full path of the file. A rehearsal writes it to a temporary folder instead, never over the real file.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-extract",
  "title": "Example: block.extract",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.extract",
      "args": {
        "blocks": [
          "Door-*"
        ],
        "attributes": [
          "MARK",
          "WIDTH"
        ],
        "rooms": {
          "layers": [
            "A-AREA"
          ]
        },
        "fileName": "doors.csv",
        "overwrite": true
      }
    }
  ]
}
```

## area.report

Areas of closed polylines, circles, hatches and regions by handle or filter, nested boundaries subtracted, grouped by layer or room; to a table, a CSV or JSON file, and the result.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `handles` | list<ref> |  |  |  | Closed entities to measure. |
| `filter` | object |  |  |  | Select closed entities instead of listing them. |
| `filter.layers` | list<string> |  |  |  | On these layers (wildcards allowed). |
| `filter.types` | list<enum(polyline\\|circle\\|hatch\\|region\\|ellipse\\|spline)> |  | `["polyline","circle","hatch","region"]` | each one of `polyline`, `circle`, `hatch`, `region`, `ellipse`, `spline` | Entity kinds to include. |
| `filter.space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. |
| `islands` | enum(subtract\\|ignore) |  | `"subtract"` |  | A selected boundary lying inside another selected boundary on the same layer becomes a hole in it (subtract), or counts on its own (ignore). Hatch islands are always subtracted. |
| `rooms` | object |  |  |  | Find the room each item lies in: closed polylines on these layers are room boundaries. The room's name and number come from a block reference inside it with NAME and NUMBER attributes (tagBlock when given), else from a multiline text inside it whose first two lines are name and number (as room.create writes them). |
| `rooms.layers` | list<string> | yes |  | at least 1 item | Layers of the room boundary polylines (wildcards allowed). |
| `rooms.tagBlock` | string |  |  |  | Room tag block with NAME and NUMBER attributes. |
| `groupBy` | enum(layer\\|room\\|none) |  | `"layer"` |  | Totals by layer, by room (needs rooms) or none. |
| `areaUnit` | enum(m2\\|ft2\\|job) |  |  |  | Unit of reported areas. Defaults to m2 for metric jobs and ft2 for imperial jobs; "job" is the job's length unit squared. |
| `precision` | integer |  | `2` | >= 0; <= 6 | Decimal places in the table and file. |
| `table` | object |  |  |  | Write the totals as a table into the drawing. |
| `table.position` | point2 | yes |  | length | Top-left corner. |
| `table.title` | string |  |  |  | Table title. |
| `table.style` | string |  |  |  | Table style. |
| `table.layer` | string |  |  |  | Table layer. |
| `fileName` | string |  |  |  | Plain file name written into the job's output folder, one row per item; .csv or .json decides the format. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Rules across fields: exactly one of `handles`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `total` (number): Sum of net areas, in areaUnit.
- `areaUnit` (string): The unit used.
- `items` (list<object>): { handle, type, layer, area, gross, holes, perimeter, room } per measured boundary that is not a hole.
- `groups` (list<object>): { key, count, area } per layer or room.
- `path` (string): Full path of the file. A rehearsal writes it to a temporary folder instead.
- `table` (ref): Table handle.

Example (lint clean):

```json
{
  "jobId": "example-autocad-area-report",
  "title": "Example: area.report",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "area.report",
      "args": {
        "filter": {
          "layers": [
            "A-AREA"
          ],
          "types": [
            "polyline",
            "hatch"
          ]
        },
        "groupBy": "layer",
        "areaUnit": "m2",
        "table": {
          "position": [
            0,
            -5000
          ],
          "title": "Areas"
        },
        "fileName": "areas.json"
      }
    }
  ]
}
```

## grid.module

Lay out a module grid (ceiling, floor or wall tiles) inside a closed boundary, set out by rule; reports border sizes and flags borders under half a module.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundary` | list<point2> |  |  | at least 3 items; length | Boundary points in order (closed automatically). |
| `boundaryHandle` | ref |  |  |  | A closed polyline to use as the boundary (straight segments only). |
| `module` | length | yes |  | > 0; length | Module size along the grid's X direction, e.g. 600. |
| `moduleY` | length |  |  | > 0; length | Module size along the grid's Y direction. Defaults to module. |
| `angle` | angle |  | `0` | degrees | Direction of the grid's X axis. |
| `setOut` | enum(balanced\\|centreTile\\|centreLine\\|origin) |  | `"balanced"` |  | balanced picks, per direction, whichever of centreTile and centreLine gives the larger border; centreTile puts a whole tile in the middle of the boundary; centreLine puts a grid line there; origin runs the grid through the origin point. |
| `origin` | point2 |  |  | length | A grid line intersection, with setOut origin. |
| `minBorder` | number |  | `0.5` | >= 0; <= 1 | Borders smaller than this fraction of the module are flagged. |
| `layer` | string |  |  |  | Layer for the grid lines. Defaults to the current layer. |
| `draw` | bool |  | `true` |  | Draw the grid lines, clipped to the boundary. When false the op only reports. |

Rules across fields: exactly one of `boundary`, `boundaryHandle`; `origin` requires `setOut`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `handles` (list<ref>): Grid line handles.
- `borders` (object): Cut tile size at each side in the grid's frame: { left, right, bottom, top }, in job units.
- `smallBorders` (list<string>): Sides whose border is under minBorder of the module.
- `origin` (point2): The grid line intersection used.
- `fullTiles` (integer): Whole tiles inside the boundary.
- `cutTiles` (integer): Cut tiles inside the boundary.

Example (lint clean):

```json
{
  "jobId": "example-autocad-grid-module",
  "title": "Example: grid.module",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "grid.module",
      "args": {
        "boundary": [
          [
            0,
            0
          ],
          [
            6000,
            0
          ],
          [
            6000,
            4500
          ],
          [
            0,
            4500
          ]
        ],
        "module": 600,
        "setOut": "centreTile",
        "layer": "A-CLNG-GRID"
      }
    }
  ]
}
```

## layout.clearances

Test clearance zones (circles, rectangles, convex polygons, door swings, route strips) against entities on chosen layers and references of chosen blocks in model space; reports each intrusion with its depth. Changes nothing unless drawZones is given.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `zones` | list<object> | yes |  | at least 1 item | Clearance zones. |
| `zones[].id` | string | yes |  | matches `[A-Za-z0-9_-]{1,64}` | Zone name in the report. |
| `zones[].kind` | enum(circle\\|rect\\|polygon\\|doorSwing\\|route) | yes |  |  | circle: centre and radius. rect: min and max. polygon: convex points. doorSwing: hinge, leafWidth, closedAngle, swing and side (the leaf's sweep). route: points and width (a strip centred on the points). |
| `zones[].centre` | point2 |  |  | length | circle: centre. |
| `zones[].radius` | length |  |  | > 0; length | circle: radius, e.g. 750 for a 1500 turning circle. |
| `zones[].min` | point2 |  |  | length | rect: lower-left corner. |
| `zones[].max` | point2 |  |  | length | rect: upper-right corner. |
| `zones[].points` | list<point2> |  |  | length | polygon: convex outline (3 or more points); route: centreline (2 or more points). |
| `zones[].width` | length |  |  | > 0; length | route: clear width. |
| `zones[].hinge` | point2 |  |  | length | doorSwing: hinge point. |
| `zones[].leafWidth` | length |  |  | > 0; length | doorSwing: leaf width (the swing radius). |
| `zones[].closedAngle` | angle |  |  | degrees | doorSwing: direction from the hinge along the closed leaf. |
| `zones[].swing` | angle |  | `90` | >= 1; <= 180; degrees | doorSwing: opening angle. |
| `zones[].side` | enum(left\\|right) |  | `"left"` |  | doorSwing: the leaf opens counter-clockwise (left) or clockwise (right) from closedAngle. |
| `zones[].ignore` | list<ref> |  |  |  | Entities never counted against this zone (the door that makes the swing, for example). |
| `layers` | list<string> |  |  |  | Obstacles: entities on these layers (wildcards allowed). |
| `blocks` | list<string> |  |  |  | Obstacles: references of these blocks, by effective name (wildcards allowed). |
| `blockGeometry` | enum(geometry\\|extents) |  | `"geometry"` |  | Test block references by their geometry, or by their bounding box. |
| `ignore` | list<ref> |  |  |  | Entities never counted against any zone. |
| `tolerance` | length |  |  | >= 0; length | Intrusions this deep or less are ignored (touching). Defaults to 0.5 mm. |
| `drawZones` | object |  |  |  | Also draw each zone's outline, for review. |
| `drawZones.layer` | string | yes |  |  | Layer for the outlines (normally a non-plotting review layer). |
| `failOnIntrusion` | bool |  | `false` |  | Fail the op, and so roll back the job, when any zone is intruded. |

Rules across fields: at least one of `layers`, `blocks`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Intrusions found.
- `clear` (bool): True when no zone is intruded.
- `intrusions` (list<object>): { zone, handle, type, layer, block, depth, point } per obstacle in a zone, deepest first.
- `zones` (list<object>): { id, kind, intrusions, maxDepth, outline } per zone.
- `outlines` (list<ref>): Handles of drawn zone outlines.

Example (lint clean):

```json
{
  "jobId": "example-autocad-layout-clearances",
  "title": "Example: layout.clearances",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "layout.clearances",
      "args": {
        "zones": [
          {
            "id": "turn-1",
            "kind": "circle",
            "centre": [
              1500,
              1500
            ],
            "radius": 750
          },
          {
            "id": "door-1",
            "kind": "doorSwing",
            "hinge": [
              0,
              0
            ],
            "leafWidth": 900,
            "closedAngle": 0,
            "swing": 90,
            "side": "left",
            "ignore": [
              "2A1"
            ]
          },
          {
            "id": "route",
            "kind": "route",
            "points": [
              [
                0,
                0
              ],
              [
                5000,
                0
              ]
            ],
            "width": 1200
          }
        ],
        "layers": [
          "A-FURN"
        ],
        "blocks": [
          "WC-*"
        ],
        "drawZones": {
          "layer": "A-REVIEW"
        }
      }
    }
  ]
}
```

## text.replace

Find and replace in text, multiline text, attributes, table cells and multileader text, with a dry-run count. Formatting codes in multiline text are never changed.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `find` | string | yes |  |  | Text to find (a .NET regular expression when regex is true). |
| `replace` | string |  |  |  | Replacement; "" deletes the match. Required unless dryRun. With regex, $1 inserts group 1. |
| `matchCase` | bool |  | `false` |  | Case-sensitive match. |
| `wholeWord` | bool |  | `false` |  | Match whole words only. |
| `regex` | bool |  | `false` |  | Treat find as a regular expression. |
| `kinds` | list<enum(text\\|mtext\\|attributes\\|tables\\|mleaders)> |  | `["text","mtext","attributes","tables","mleaders"]` | each one of `text`, `mtext`, `attributes`, `tables`, `mleaders` | What to search. |
| `space` | enum(model\\|paper\\|all) |  | `"all"` |  | Where to search. |
| `layers` | list<string> |  |  |  | Only objects on these layers (wildcards allowed). |
| `includeBlockDefinitions` | bool |  | `false` |  | Also change text and attribute definitions inside block definitions (every reference shows the change; existing attribute values do not). |
| `dryRun` | bool |  | `false` |  | Count and list the changes without making them. |
| `expectCount` | integer |  |  |  | When given, the op fails unless exactly this many objects would change. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Objects changed (or that would change in a dry run).
- `matches` (integer): Occurrences replaced (or found).
- `changes` (list<object>): { handle, kind, before, after } per object, up to 500.
- `skippedLocked` (integer): Objects with matches on locked layers, left unchanged.

Example (lint clean):

```json
{
  "jobId": "example-autocad-text-replace",
  "title": "Example: text.replace",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "text.replace",
      "args": {
        "find": "Rev A",
        "replace": "Rev B",
        "wholeWord": true,
        "kinds": [
          "text",
          "mtext",
          "attributes"
        ],
        "expectCount": 4
      }
    }
  ]
}
```

## revcloud.add

Draw a revision cloud (a closed polyline of arcs bulging outward) around a rectangle, points or a closed polyline, with an optional revision tag.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `rect` | object |  |  |  | Rectangle to cloud. |
| `rect.min` | point2 | yes |  | length | Lower-left corner. |
| `rect.max` | point2 | yes |  | length | Upper-right corner. |
| `points` | list<point2> |  |  | at least 3 items; length | Outline points in order (closed automatically). |
| `handle` | ref |  |  |  | A closed polyline whose vertices give the outline (bulges are ignored). |
| `offset` | length |  | `0` | >= 0; length | Grow the outline outward by this much before clouding. |
| `arcLength` | length |  |  | > 0; length | Chord length of each arc. Defaults to 10 mm on paper at the current annotation scale. |
| `layer` | string |  |  |  | Cloud layer (normally a revision or markup layer). Defaults to the current layer. |
| `tag` | object |  |  |  | Revision tag outside the cloud: the block with its attribute set, or text in a triangle. |
| `tag.text` | string | yes |  |  | Revision mark, e.g. "B". |
| `tag.position` | point2 |  |  | length | Tag centre. Defaults to just outside the outline's top-right corner. |
| `tag.block` | string |  |  |  | Tag block to insert. |
| `tag.attribute` | string |  | `"REV"` |  | Attribute of the tag block that takes the text. |
| `tag.plottedHeight` | length |  |  | > 0; length | Text height on paper, in paper units: mm, or inches for an imperial job. Defaults to 2.5 mm. |
| `tag.layer` | string |  |  |  | Tag layer. Defaults to the cloud layer. |

Rules across fields: exactly one of `rect`, `points`, `handle`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `cloud` (ref): Cloud polyline handle.
- `arcCount` (integer): Arcs in the cloud.
- `tagHandles` (list<ref>): Handles of the tag objects (block reference, or triangle and text).

Example (lint clean):

```json
{
  "jobId": "example-autocad-revcloud-add",
  "title": "Example: revcloud.add",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "revcloud.add",
      "args": {
        "rect": {
          "min": [
            0,
            0
          ],
          "max": [
            3000,
            2000
          ]
        },
        "offset": 200,
        "layer": "A-REVS",
        "tag": {
          "text": "B",
          "block": "RevTag"
        }
      }
    }
  ]
}
```

## layerstate.ensure

Flags: ensure.

Create or update a named layer state from the drawing's current layer settings, with changes for listed layers, and optionally leave the drawing in that state.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Layer state name. |
| `description` | string |  |  |  | Layer state description. |
| `layers` | list<object> |  |  |  | Settings that differ from the drawing's current ones; layers not listed are saved as they are now. |
| `layers[].name` | string | yes |  |  | Layer name (wildcards * and ? allowed). |
| `layers[].on` | bool |  |  |  | Layer on. |
| `layers[].frozen` | bool |  |  |  | Layer frozen. |
| `layers[].locked` | bool |  |  |  | Layer locked. |
| `layers[].plot` | bool |  |  |  | Layer plots. |
| `layers[].color` | any |  |  |  | ACI number 1-255, "bylayer", "byblock", "#RRGGBB" or "r,g,b". |
| `layers[].linetype` | string |  |  |  | Linetype name; must be loaded. |
| `layers[].lineweight` | any |  |  |  | Millimetres from the standard set (0.13, 0.18, 0.25, 0.35, 0.50, 0.70...), or "bylayer", "byblock", "default". |
| `layers[].transparency` | integer |  |  | >= 0; <= 90 | Percent. |
| `properties` | list<enum(on\\|frozen\\|locked\\|plot\\|color\\|linetype\\|lineweight\\|transparency\\|plotStyle)> |  | `["on","frozen","locked","plot","color","linetype","lineweight","transparency"]` | each one of `on`, `frozen`, `locked`, `plot`, `color`, `linetype`, `lineweight`, `transparency`, `plotStyle` | What restoring the state sets. |
| `restore` | bool |  | `false` |  | Leave the drawing's layers in this state afterwards. Only for a state this op creates or updates (a saved state is not restored as it is). |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `state` (string): Layer state name.
- `layerCount` (integer): Layers saved in the state.
- `backup` (string): With update: the previous state exported as a .las file in the job's output folder.

Example (lint clean):

```json
{
  "jobId": "example-autocad-layerstate-ensure",
  "title": "Example: layerstate.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "layerstate.ensure",
      "args": {
        "name": "Furniture plan",
        "description": "Furniture on",
        "layers": [
          {
            "name": "A-FURN*",
            "on": true,
            "frozen": false,
            "color": 3,
            "lineweight": 0.25,
            "transparency": 30
          }
        ],
        "restore": true
      }
    }
  ]
}
```

## material.ensure

Flags: ensure.

Create render materials with description, diffuse colour and an optional image texture; existing materials are left alone unless update is true.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `materials` | list<object> | yes |  | at least 1 item | Materials to ensure. |
| `materials[].name` | string | yes |  |  | Material name, e.g. "CT-01 Porcelain 600x600 Light Grey" (finish code first). Global, ByLayer and ByBlock are refused. |
| `materials[].description` | string |  |  |  | Material description. |
| `materials[].color` | any |  |  |  | ACI number 1-255, "bylayer", "byblock", "#RRGGBB" or "r,g,b". |
| `materials[].texture` | object |  |  |  | Diffuse image map. |
| `materials[].texture.path` | string | yes |  |  | Image file; must exist on this machine at validation. Paths under a user profile are refused unless allowLocalPath. |
| `materials[].texture.width` | length |  |  | > 0; length | Real-world width of one image repeat. |
| `materials[].texture.height` | length |  |  | > 0; length | Real-world height of one image repeat. Defaults to width. |
| `materials[].texture.projection` | enum(planar\\|box\\|cylinder\\|sphere) |  | `"planar"` |  | Mapping projection. |
| `materials[].texture.allowLocalPath` | bool |  | `false` |  | Allow a path under a user profile (it breaks on other machines). |
| `materials[].twoSided` | bool |  | `true` |  | Render both sides of faces. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `materials` (map<ref>): Material name to its handle.

Example (lint clean):

```json
{
  "jobId": "example-autocad-material-ensure",
  "title": "Example: material.ensure",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "material.ensure",
      "args": {
        "materials": [
          {
            "name": "CT-01 Porcelain 600x600 Light Grey",
            "color": "#D0D0D0",
            "texture": {
              "path": "P:/library/ct01.jpg",
              "width": 600,
              "projection": "planar"
            }
          }
        ]
      }
    }
  ]
}
```

## material.assign

Assign a render material to layers, or to entities by handle or filter, with an optional texture mapping on entities.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `material` | string | yes |  |  | Material name, or "ByLayer" or "ByBlock" (entities only). |
| `layers` | list<string> |  |  |  | Layers to assign it to (wildcards allowed). Xref-dependent layers are refused. |
| `handles` | list<ref> |  |  |  | Entities to assign it to. |
| `filter` | object |  |  |  | Select entities instead of listing them. Entities on locked layers are skipped and counted. |
| `filter.layer` | string |  |  |  | On this layer (wildcards * and ? allowed). |
| `filter.type` | string |  |  |  | DXF entity name, e.g. LINE, LWPOLYLINE, INSERT, TEXT, MTEXT, HATCH. |
| `filter.block` | string |  |  |  | References of this block, by its effective name (with type INSERT or no type). |
| `filter.space` | enum(model\\|paper\\|all) |  | `"model"` |  | Where to look. |
| `mapping` | object |  |  |  | Entities: texture mapping. |
| `mapping.projection` | enum(planar\\|box\\|cylinder\\|sphere) |  | `"planar"` |  | Mapping projection. |
| `mapping.width` | length | yes |  | > 0; length | Real-world width of one image repeat. |
| `mapping.height` | length |  |  | > 0; length | Real-world height of one image repeat. Defaults to width. |

Rules across fields: exactly one of `layers`, `handles`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Layers or entities changed.
- `targets` (list<ref>): Handles of the layers or entities changed.

Example (lint clean):

```json
{
  "jobId": "example-autocad-material-assign",
  "title": "Example: material.assign",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "material.assign",
      "args": {
        "material": "CT-01 Porcelain 600x600 Light Grey",
        "handles": [
          "2A1"
        ],
        "mapping": {
          "projection": "planar",
          "width": 600
        }
      }
    }
  ]
}
```

## block.export

Flags: alone in its job.

Write each chosen block definition to its own DWG (origin at the block's base point, the drawing's INSUNITS) and a placements.json (format cadbim.placements/1) listing every reference with position, rotation, scale and layer, for family.fromcad and family.place in Revit. The drawing is not changed. Alone in its job; files are put in place only when the job commits.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `blocks` | list<string> |  |  |  | Block names (effective names; * and ? wildcards allowed). A dynamic block gives one entry per representation actually referenced. A named block with no reference is exported with an empty references list. |
| `layers` | list<string> |  |  |  | Only references on these layers (wildcards allowed), and only blocks that have one. With blocks, both must match. |
| `all` | bool |  |  |  | Every named block with a reference in model space (and in layouts when excludeLayouts is false). Only true is accepted. |
| `excludeXrefs` | bool |  | `true` |  | Xrefs and blocks that belong to an xref are never exported; false is refused. |
| `excludeLayouts` | bool |  | `true` |  | Only references in model space. False also lists references in paper space layouts, with the layout name as space. |
| `expectCount` | integer |  |  | >= 1 | When given, the op fails unless exactly this many blocks (DWG files) would be written. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Rules across fields: at least one of `blocks`, `layers`, `all`; at most one of `blocks`, `all`; at most one of `layers`, `all`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Blocks exported (DWG files).
- `references` (integer): References listed in placements.json.
- `files` (list<string>): DWG file names in placements order.
- `placements` (string): Full path of placements.json. A rehearsal writes everything to a temporary folder instead, never into outputDir.
- `outputDir` (string): The folder the files go to.
- `skipped` (list<object>): { name, handle, reason } per block (no handle) or reference left out: no visible geometry, a MINSERT array, a reference not parallel to the XY plane.

Example (lint clean):

```json
{
  "jobId": "example-autocad-block-export",
  "title": "Example: block.export",
  "app": "autocad",
  "document": {
    "path": "C:/Projects/2417/2417-A-PLAN-L01.dwg",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "block.export",
      "args": {
        "blocks": [
          "BED-*",
          "CHAIR-*",
          "DESK-1600"
        ],
        "layers": [
          "I-FURN"
        ],
        "expectCount": 6,
        "outputDir": "C:/Projects/2417/export/furniture",
        "overwrite": true
      }
    }
  ]
}
```
