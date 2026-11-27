# Revit job ops

Generated from the op schemas in the engine by `ReferenceDocsTests` in `engine/src/SA.Core.Tests`. Do not edit by hand: change the op schema or its example, then regenerate.

68 ops. Every example below passes `sa job lint`. Names in the examples (layers, blocks, types, views, sheets, ids) are placeholders: take the real ones from the snapshot or create them earlier in the same job. `document` is copied from the snapshot's `identity`.

How to read the field tables:

- `name*` in `sa ops` is "yes" in the Required column. `a.b` is a field of object `a`; `a[].b` is a field of each item of list `a`.
- **length**: a number in the job's `units.length`, or a string with its own unit (`"3.2m"`, `"10'-6\""`). Fields described as paper sizes take millimetres on the sheet for a metric job and inches for an imperial one, whatever the job unit.
- **degrees**: angles, counter-clockwise.
- **ref**: a `$id` reference to an earlier op's result (`"$w1"`, `"$w1.walls.0"`), or what the description allows (handle, element id, exact name).
- **ensure** ops leave an existing object alone (differences become a warning) unless `update` is true. **destructive** ops delete; list exactly what will go in the plan.

## Index

| Op | Flags | What it does |
|---|---|---|
| [`level.ensure`](#levelensure) | ensure | Create levels at elevations, with their plan views. |
| [`grid.create`](#gridcreate) |  | Create grids from explicit lines or an orthogonal definition. |
| [`walltype.ensure`](#walltypeensure) | ensure | Create a basic wall type with a compound layer build-up. |
| [`wall.create`](#wallcreate) |  | Create walls by type on a level, from a start and end or a path, with constraints, location line and joins. |
| [`door.place`](#doorplace) |  | Place a hosted door family in a wall by offset along the wall or by point. |
| [`window.place`](#windowplace) |  | Place a hosted window family in a wall by offset along the wall or by point. |
| [`floor.create`](#floorcreate) |  | Create a floor from a boundary or from a room's boundary. |
| [`ceiling.create`](#ceilingcreate) |  | Create a ceiling from a boundary or a room. |
| [`roof.footprint`](#rooffootprint) |  | Create a footprint roof with slopes and overhang. |
| [`room.place`](#roomplace) |  | Place rooms at points or in every enclosed region of a level, with name, number and parameters. |
| [`room.separator`](#roomseparator) |  | Draw room separation lines. |
| [`family.load`](#familyload) | ensure | Load a family file into the project. |
| [`type.duplicate`](#typeduplicate) | ensure | Duplicate a type under a new name and set type parameters. |
| [`param.set`](#paramset) |  | Set instance or type parameter values on listed elements or a filtered set. |
| [`param.shared.ensure`](#paramsharedensure) | ensure | Ensure shared parameters exist in the shared parameter file and are bound to categories. |
| [`view.plan.create`](#viewplancreate) |  | Create a floor plan view. |
| [`view.ceiling.create`](#viewceilingcreate) |  | Create a reflected ceiling plan view. |
| [`view.section.create`](#viewsectioncreate) |  | Create a section from a cut line in plan. |
| [`view.elevation.create`](#viewelevationcreate) |  | Create an elevation from a marker in a plan view. |
| [`view.3d.create`](#view3dcreate) |  | Create an isometric or perspective 3D view, optionally with a section box. |
| [`view.rename`](#viewrename) |  | Rename views. |
| [`viewtemplate.apply`](#viewtemplateapply) |  | Assign a view template to views. |
| [`view.filter.ensure`](#viewfilterensure) | ensure | Create a rule-based view filter with graphic overrides and apply it to views or templates. |
| [`sheet.create`](#sheetcreate) | ensure | Create a sheet with number, name and title block. |
| [`viewport.place`](#viewportplace) |  | Place a view on a sheet at a point. |
| [`viewports.layout`](#viewportslayout) |  | Place several views on a sheet automatically, in aligned rows inside the drawable area. |
| [`schedule.create`](#schedulecreate) | ensure | Create a schedule with fields, filters, sorting and grouping, and totals. |
| [`tag.all`](#tagall) |  | Tag every untagged element of a category in a view. |
| [`dim.grids`](#dimgrids) |  | Dimension grids in a view: grid to grid strings and an overall dimension. |
| [`text.note`](#textnote) |  | Add a text note in a view. |
| [`revision.ensure`](#revisionensure) | ensure | Create revisions and add them to sheets. |
| [`export.pdf`](#exportpdf) |  | Export sheets or views to PDF. |
| [`export.dwg`](#exportdwg) |  | Export sheets or views to DWG with an export setup. |
| [`export.ifc`](#exportifc) |  | Export the model to IFC. |
| [`export.image`](#exportimage) |  | Export views or sheets to images. |
| [`element.delete`](#elementdelete) | destructive | Delete the listed elements only. |
| [`cleanup.unplacedRooms`](#cleanupunplacedrooms) | destructive | Delete unplaced (and optionally not enclosed or redundant) rooms, listed by id from a snapshot. |
| [`cleanup.unusedViews`](#cleanupunusedviews) | destructive | Delete views that are not on sheets, listed by id from a snapshot. |
| [`workset.assign`](#worksetassign) |  | Move elements to a workset. |
| [`family.place`](#familyplace) |  | Place family instances of a loaded type: on a level, hosted on a wall, floor, ceiling or roof, or on a work plane. |
| [`group.place`](#groupplace) |  | Make a model group from elements, or place instances of an existing model group type. |
| [`element.transform`](#elementtransform) |  | Move, copy, rotate or mirror elements in plan. |
| [`phase.set`](#phaseset) |  | Set Phase Created and Phase Demolished on elements, and phase and phase filter on views. |
| [`compoundtype.ensure`](#compoundtypeensure) | ensure | Create a floor, ceiling or roof type with a compound layer build-up. |
| [`wallsweep.add`](#wallsweepadd) |  | Add wall sweeps (skirtings, cornices) or reveals to walls, from a sweep or reveal type. |
| [`fillpattern.ensure`](#fillpatternensure) | ensure | Create fill patterns: stacked tiles, running bond, parallel lines or raw grids. |
| [`material.ensure`](#materialensure) | ensure | Create Revit materials with identity data, graphics, surface and cut patterns and an optional render appearance. |
| [`material.assign`](#materialassign) |  | Set a material: on a layer of a wall, floor, ceiling or roof type, on a material parameter of elements, on a category, or by painting faces. |
| [`link.cad`](#linkcad) |  | Link a DWG or DXF file into a view. |
| [`link.revit`](#linkrevit) |  | Link a Revit model, optionally acquiring shared coordinates from it. |
| [`cleanup.purge`](#cleanuppurge) | destructive | Purge unused families, types, materials and other elements Revit reports as unused. |
| [`export.nwc`](#exportnwc) |  | Export the model to a Navisworks cache file. |
| [`data.export`](#dataexport) |  | Write chosen parameters of the elements of one or more categories, with id, level, room, location and (rooms and areas) boundary loops, to a CSV or JSON file. |
| [`schedule.export`](#scheduleexport) |  | Export a schedule as delimited text with Revit's own schedule export. |
| [`schedule.key.create`](#schedulekeycreate) | ensure | Ensure a key schedule: the category's elements get a key parameter, each key (row) sets the listed fields at once. |
| [`area.plan.create`](#areaplancreate) |  | Create an area plan for an existing area scheme on a level. |
| [`area.place`](#areaplace) |  | Draw area boundary lines and place areas (with tags) in an area plan. |
| [`view.drafting.create`](#viewdraftingcreate) |  | Create a drafting view, or a legend by duplicating an existing one. |
| [`detail.draw`](#detaildraw) |  | Draw detail lines, arcs, circles, filled and masking regions and detail components in a 2D-drawable view. |
| [`view.crop.set`](#viewcropset) |  | Set a view's crop region to a rectangle, a polygon or a room. |
| [`view.callout.create`](#viewcalloutcreate) |  | Create a callout of a plan, section, elevation or detail view. |
| [`colorfill.apply`](#colorfillapply) | ensure | Ensure a colour fill scheme (by parameter, with chosen colours) and apply it to a view, with an optional legend. |
| [`tag.place`](#tagplace) |  | Place tags on chosen elements in a view: category tags, room and area tags, or material tags. |
| [`revision.cloud`](#revisioncloud) |  | Draw a revision cloud for a revision in a view or on a sheet. |
| [`layout.clearances`](#layoutclearances) |  | Check clearance zones (circles, rectangles, polygons, door swings, route strips, clear space at doors) against elements in a plan and report every intrusion with its depth. |
| [`dim.string`](#dimstring) |  | Add a string of aligned dimensions in a plan across the walls and grids (and, along a wall, the doors and windows) that a line crosses. |
| [`text.replace`](#textreplace) |  | Find and replace text in text notes and chosen text parameters, keeping text note formatting. |
| [`family.edit`](#familyedit) | alone in its job | Edit a loaded family: add family parameters, set or clear formulas, create types and set type values, then load it back into the project (existing types take the new values) and read every value back from the project. |

## level.ensure

Flags: ensure.

Create levels at elevations, with their plan views.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `levels` | list<object> | yes |  | at least 1 item | Levels to ensure. |
| `levels[].name` | string | yes |  |  | Level name. |
| `levels[].elevation` | length | yes |  | length | Elevation above the project base point. |
| `levels[].buildingStory` | bool |  | `true` |  | Counts as a building storey. |
| `levels[].createPlanView` | bool |  | `true` |  | Create a floor plan for the level. |
| `levels[].createCeilingView` | bool |  | `false` |  | Create a reflected ceiling plan for the level. |
| `levels[].levelType` | string |  |  |  | Level type. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `levels` (map): Level name to element id.

Example (lint clean):

```json
{
  "jobId": "example-revit-level-ensure",
  "title": "Example: level.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "level.ensure",
      "args": {
        "levels": [
          {
            "name": "Level 01",
            "elevation": 0
          },
          {
            "name": "Level 02",
            "elevation": 3600
          }
        ]
      }
    }
  ]
}
```

## grid.create

Create grids from explicit lines or an orthogonal definition.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `lines` | list<object> |  |  |  | Explicit grid lines. |
| `lines[].label` | string | yes |  |  | Grid name, e.g. "A" or "1". |
| `lines[].start` | point2 | yes |  | length | Start point. |
| `lines[].end` | point2 | yes |  | length | End point. |
| `orthogonal` | object |  |  |  | A rectangular grid. |
| `orthogonal.origin` | point2 | yes |  | length | Intersection of the first grid in each direction. |
| `orthogonal.xSpacings` | list<length> | yes |  | at least 1 item; length | Bay widths left to right (vertical grids). |
| `orthogonal.ySpacings` | list<length> | yes |  | at least 1 item; length | Bay depths bottom to top (horizontal grids). |
| `orthogonal.xLabelStart` | string |  | `"1"` |  | First vertical grid name. |
| `orthogonal.yLabelStart` | string |  | `"A"` |  | First horizontal grid name. |
| `orthogonal.skipLetters` | list<string> |  | `["I","O"]` |  | Letters never used in letter grid names (I and O read as 1 and 0). [] uses every letter. |
| `orthogonal.extension` | length |  |  | >= 0; length | Length past the outer grids. Defaults to 2000 mm (in job units). |
| `orthogonal.rotation` | angle |  | `0` | degrees | Rotation about origin. |
| `gridType` | string |  |  |  | Grid type. |

Rules across fields: at least one of `lines`, `orthogonal`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `grids` (map): Grid name to element id.

Example (lint clean):

```json
{
  "jobId": "example-revit-grid-create",
  "title": "Example: grid.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "grid.create",
      "args": {
        "orthogonal": {
          "origin": [
            0,
            0
          ],
          "xSpacings": [
            7500,
            7500
          ],
          "ySpacings": [
            6000
          ]
        }
      }
    }
  ]
}
```

## walltype.ensure

Flags: ensure.

Create a basic wall type with a compound layer build-up.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Wall type name, e.g. "Interior - 100 Stud". |
| `basedOn` | string |  |  |  | Basic wall type to duplicate. Defaults to the first basic wall type. |
| `function` | enum(exterior\\|interior\\|foundation\\|retaining\\|soffit\\|core_shaft) |  | `"interior"` |  | Wall function. |
| `layers` | list<object> | yes |  | at least 1 item | Layers from exterior to interior. |
| `layers[].function` | enum(structure\\|substrate\\|thermal\\|membrane\\|finish1\\|finish2) | yes |  |  | Layer function. |
| `layers[].material` | string | yes |  |  | Material name; created plainly if missing. |
| `layers[].thickness` | length | yes |  | >= 0; length | Layer thickness (0 only for membranes). |
| `layers[].core` | bool |  | `false` |  | Inside the core boundaries. Core layers must be contiguous. |
| `layers[].wraps` | bool |  | `false` |  | Layer wraps at inserts and ends. |
| `wrapAtInserts` | enum(none\\|exterior\\|interior\\|both) |  | `"none"` |  | Wrapping at inserts. |
| `wrapAtEnds` | enum(none\\|exterior\\|interior) |  | `"none"` |  | Wrapping at ends. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `type` (ref): Wall type id.
- `width` (length): Total width read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-walltype-ensure",
  "title": "Example: walltype.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "walltype.ensure",
      "args": {
        "name": "Interior - 100 Stud",
        "layers": [
          {
            "function": "finish1",
            "material": "Gypsum Wall Board",
            "thickness": 12.5
          },
          {
            "function": "structure",
            "material": "Metal Stud Layer",
            "thickness": 75,
            "core": true
          },
          {
            "function": "finish2",
            "material": "Gypsum Wall Board",
            "thickness": 12.5
          }
        ]
      }
    }
  ]
}
```

## wall.create

Create walls by type on a level, from a start and end or a path, with constraints, location line and joins.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `type` | string | yes |  |  | Wall type name. |
| `level` | string | yes |  |  | Base level. |
| `start` | point2 |  |  | length | Location line start. |
| `end` | point2 |  |  | length | Location line end. |
| `path` | list<point2> |  |  | at least 2 items; length | Location line through these points, one wall per segment. |
| `closed` | bool |  | `false` |  | With path: join the last point back to the first. |
| `height` | length |  |  | > 0; length | Unconnected height. |
| `topLevel` | string |  |  |  | Top constraint level, instead of a height. |
| `topOffset` | length |  | `0` | length | Offset from the top level. |
| `baseOffset` | length |  | `0` | length | Offset from the base level. |
| `locationLine` | enum(wall_centerline\\|core_centerline\\|finish_face_exterior\\|finish_face_interior\\|core_face_exterior\\|core_face_interior) |  | `"wall_centerline"` |  | Which line of the wall the points describe. |
| `flip` | bool |  | `false` |  | Flip the exterior side (exterior is on the left looking from start to end when not flipped). |
| `structural` | bool |  | `false` |  | Structural wall. |
| `joins` | enum(allow\\|disallow) |  | `"allow"` |  | End joins. |
| `roomBounding` | bool |  | `true` |  | Wall bounds rooms. |
| `mark` | string |  |  |  | Mark; for a path, a number is appended per wall. |
| `parameters` | map |  |  |  | Instance parameter name to value. |

Rules across fields: exactly one of `start`, `path`; `start` requires `end`; exactly one of `height`, `topLevel`; `closed` requires `path`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `walls` (list<ref>): Wall ids in order.
- `lengths` (list<length>): Lengths read back, in order.

Example (lint clean):

```json
{
  "jobId": "example-revit-wall-create",
  "title": "Example: wall.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "wall.create",
      "args": {
        "type": "Interior - 100 Stud",
        "level": "Level 01",
        "start": [
          0,
          0
        ],
        "end": [
          6000,
          0
        ],
        "topLevel": "Level 02"
      }
    }
  ]
}
```

## door.place

Place a hosted door family in a wall by offset along the wall or by point.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `host` | ref | yes |  |  | Host wall: element id, or a reference such as "$w1" or "$w1.walls.2". |
| `type` | string | yes |  |  | Door type, e.g. "Single-Flush : 0915 x 2134mm": "Family : Type", or a type name that is unique among the category's types (an ambiguous name is refused, listing the candidates). |
| `offset` | length |  |  | length | Distance along the wall's location line from its start to the door centre. |
| `point` | point2 |  |  | length | A point on or near the wall; the door is placed at its projection. |
| `level` | string |  |  |  | Level. Defaults to the host wall's base level. |
| `sillHeight` | length |  |  | >= 0; length | Sill height (normally 0). |
| `flipHand` | bool |  | `false` |  | Swap the hinge side. |
| `flipFacing` | bool |  | `false` |  | Swap the side it opens to or faces. |
| `mark` | string |  |  |  | Mark. |
| `parameters` | map |  |  |  | Instance parameter name to value. |

Rules across fields: exactly one of `offset`, `point`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `element` (ref): Id of the new instance.
- `host` (ref): Host id read back.
- `offset` (length): Distance along the host's location line to the door centre, read back; null when it does not project onto the wall.

Example (lint clean):

```json
{
  "jobId": "example-revit-door-place",
  "title": "Example: door.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "w1",
      "op": "wall.create",
      "args": {
        "type": "Interior - 100 Stud",
        "level": "Level 01",
        "start": [
          0,
          0
        ],
        "end": [
          6000,
          0
        ],
        "height": 2700
      }
    },
    {
      "id": "op1",
      "op": "door.place",
      "args": {
        "host": "$w1",
        "type": "Single-Flush : 0915 x 2134mm",
        "offset": 1200,
        "mark": "D01"
      }
    }
  ]
}
```

## window.place

Place a hosted window family in a wall by offset along the wall or by point.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `host` | ref | yes |  |  | Host wall: element id, or a reference such as "$w1" or "$w1.walls.2". |
| `type` | string | yes |  |  | Window type: "Family : Type", or a type name that is unique among the category's types (an ambiguous name is refused, listing the candidates). |
| `offset` | length |  |  | length | Distance along the wall's location line from its start to the window centre. |
| `point` | point2 |  |  | length | A point on or near the wall; the window is placed at its projection. |
| `level` | string |  |  |  | Level. Defaults to the host wall's base level. |
| `sillHeight` | length |  |  | >= 0; length | Sill height above the level. Defaults to the type's value. |
| `flipHand` | bool |  | `false` |  | Swap the hinge side. |
| `flipFacing` | bool |  | `false` |  | Swap the side it opens to or faces. |
| `mark` | string |  |  |  | Mark. |
| `parameters` | map |  |  |  | Instance parameter name to value. |

Rules across fields: exactly one of `offset`, `point`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `element` (ref): Id of the new instance.
- `host` (ref): Host id read back.
- `offset` (length): Distance along the host's location line to the window centre, read back; null when it does not project onto the wall.

Example (lint clean):

```json
{
  "jobId": "example-revit-window-place",
  "title": "Example: window.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "window.place",
      "args": {
        "host": "345678",
        "type": "Fixed : 0915 x 1220mm",
        "offset": 2500,
        "sillHeight": 900,
        "mark": "W01"
      }
    }
  ]
}
```

## floor.create

Create a floor from a boundary or from a room's boundary.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundary` | list<point2> |  |  | at least 3 items; length | Outer boundary points. |
| `fromRoom` | ref |  |  |  | A room whose boundary (finish faces) is used. |
| `openings` | list<list<point2>> |  |  |  | Inner loops to cut, each a list of points. |
| `type` | string | yes |  |  | Floor type. |
| `level` | string |  |  |  | Level. Defaults to the room's level with fromRoom. |
| `offset` | length |  | `0` | length | Height offset from the level. |
| `structural` | bool |  | `false` |  | Structural floor. |

Rules across fields: exactly one of `boundary`, `fromRoom`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `floor` (ref): Floor id.
- `area` (number): Area read back: m2 for metric job units, ft2 for imperial.

Example (lint clean):

```json
{
  "jobId": "example-revit-floor-create",
  "title": "Example: floor.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "floor.create",
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
            4000
          ],
          [
            0,
            4000
          ]
        ],
        "type": "Generic 150mm",
        "level": "Level 01"
      }
    }
  ]
}
```

## ceiling.create

Create a ceiling from a boundary or a room.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundary` | list<point2> |  |  | at least 3 items; length | Boundary points. |
| `fromRoom` | ref |  |  |  | A room whose boundary is used. |
| `type` | string | yes |  |  | Ceiling type. |
| `level` | string |  |  |  | Level. Defaults to the room's level with fromRoom. |
| `height` | length | yes |  | length | Height above the level. |

Rules across fields: exactly one of `boundary`, `fromRoom`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `ceiling` (ref): Ceiling id.
- `area` (number): Area read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-ceiling-create",
  "title": "Example: ceiling.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "ceiling.create",
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
            4000
          ],
          [
            0,
            4000
          ]
        ],
        "type": "600 x 600mm Grid",
        "level": "Level 01",
        "height": 2700
      }
    }
  ]
}
```

## roof.footprint

Create a footprint roof with slopes and overhang.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `boundary` | list<point2> | yes |  | at least 3 items; length | Footprint points (usually the exterior wall faces). |
| `type` | string | yes |  |  | Roof type. |
| `level` | string | yes |  |  | Level the roof sits on. |
| `baseOffset` | length |  | `0` | length | Plate height: where the sloping planes meet the footprint, above the level. With an overhang the eaves sit lower than this by overhang x slope. |
| `overhang` | length |  | `0` | >= 0; length | Overhang beyond the footprint. |
| `slope` | angle |  | `30` | >= 0; <= 89; degrees | Slope in degrees for every sloping edge. |
| `edges` | list<object> |  |  |  | Per-edge settings; edge i runs from boundary point i to i+1. |
| `edges[].edge` | integer | yes |  | >= 0 | Edge index. |
| `edges[].definesSlope` | bool | yes |  |  | Edge slopes (false for gable ends). |
| `edges[].slope` | angle |  |  | >= 0; <= 89; degrees | Slope for this edge. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `roof` (ref): Roof id.

Example (lint clean):

```json
{
  "jobId": "example-revit-roof-footprint",
  "title": "Example: roof.footprint",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "roof.footprint",
      "args": {
        "boundary": [
          [
            0,
            0
          ],
          [
            12000,
            0
          ],
          [
            12000,
            8000
          ],
          [
            0,
            8000
          ]
        ],
        "type": "Generic - 300mm",
        "level": "Level 02",
        "slope": 30,
        "overhang": 450
      }
    }
  ]
}
```

## room.place

Place rooms at points or in every enclosed region of a level, with name, number and parameters.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `level` | string | yes |  |  | Level. |
| `point` | point2 |  |  | length | A point inside the room. |
| `allEnclosed` | bool |  |  |  | Place a room in every enclosed region without one. |
| `phase` | string |  |  |  | Phase the rooms belong to, by name. Defaults to the project's last phase. The tag view must show this phase. |
| `allowNotEnclosed` | bool |  | `false` |  | With point: keep the room even when no boundary encloses it (area 0), with a warning. Only in the last phase. Otherwise a room that is not enclosed fails the op. |
| `name` | string |  |  |  | Room name. |
| `number` | string |  |  |  | Room number. |
| `parameters` | map |  |  |  | Parameter name to value (e.g. finish parameters). |
| `upperLimit` | string |  |  |  | Upper limit level. |
| `limitOffset` | length |  |  | length | Offset from the upper limit. |
| `tag` | bool |  | `true` |  | Tag the room in a plan view of the level in the room's phase. |
| `tagType` | string |  |  | matches `.+\S\s:\s\S.+` | Room tag type. Written "Family : Type". |
| `view` | ref |  |  |  | Plan view for the tag, showing the room's phase. Defaults to a floor plan of the level in that phase. A view id, a reference to an op that created it, or the view's exact name. |

Rules across fields: exactly one of `point`, `allEnclosed`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `rooms` (list<ref>): Room ids.
- `areas` (list<number>): Areas read back; 0 means not enclosed.

Example (lint clean):

```json
{
  "jobId": "example-revit-room-place",
  "title": "Example: room.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "room.place",
      "args": {
        "level": "Level 01",
        "point": [
          3000,
          2000
        ],
        "name": "Meeting",
        "number": "1.04"
      }
    }
  ]
}
```

## room.separator

Draw room separation lines.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `level` | string | yes |  |  | Level. |
| `view` | ref |  |  |  | Plan view to draw in. Defaults to the level's floor plan. A view id, a reference to an op that created it, or the view's exact name. |
| `lines` | list<object> | yes |  | at least 1 item | Lines. |
| `lines[].start` | point2 | yes |  | length | Start. |
| `lines[].end` | point2 | yes |  | length | End. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `lines` (list<ref>): Separator ids.

Example (lint clean):

```json
{
  "jobId": "example-revit-room-separator",
  "title": "Example: room.separator",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "room.separator",
      "args": {
        "level": "Level 01",
        "lines": [
          {
            "start": [
              6000,
              0
            ],
            "end": [
              6000,
              4000
            ]
          }
        ]
      }
    }
  ]
}
```

## family.load

Flags: ensure.

Load a family file into the project.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `path` | string | yes |  |  | Path of the .rfa file. |
| `types` | list<string> |  |  |  | Load only these types. Defaults to all. |
| `overwriteParameters` | bool |  | `false` |  | When the family is already loaded, reload it and overwrite its parameter values with the file's. |
| `reload` | bool |  | `false` |  | When the family is already loaded and the file differs, reload its definition (geometry, nested families) keeping parameter values. Without reload or overwriteParameters a differing file is refused rather than reloaded, even to add a missing type. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `family` (ref): Family id.
- `types` (list<string>): Type names loaded.

Example (lint clean):

```json
{
  "jobId": "example-revit-family-load",
  "title": "Example: family.load",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "family.load",
      "args": {
        "path": "C:/Projects/2417/families/Desk-Rectangular.rfa",
        "types": [
          "1600 x 800"
        ]
      }
    }
  ]
}
```

## type.duplicate

Flags: ensure.

Duplicate a type under a new name and set type parameters.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `source` | string | yes |  |  | Type to copy: "Family : Type" or a system type name. |
| `category` | string |  |  |  | Category to disambiguate the source name. |
| `newName` | string | yes |  |  | Name of the new type. |
| `parameters` | map |  |  |  | Type parameter name to value. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `type` (ref): New type id.

Example (lint clean):

```json
{
  "jobId": "example-revit-type-duplicate",
  "title": "Example: type.duplicate",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "type.duplicate",
      "args": {
        "source": "Single-Flush : 0915 x 2134mm",
        "category": "Doors",
        "newName": "0826 x 2040mm",
        "parameters": {
          "Width": 826,
          "Height": 2040
        }
      }
    }
  ]
}
```

## param.set

Set instance or type parameter values on listed elements or a filtered set.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `elements` | list<ref> |  |  |  | Element ids or references. |
| `filter` | object |  |  |  | Select elements instead of listing ids. |
| `filter.category` | string | yes |  |  | Category, by built-in name (OST_Doors) or display name (Doors). |
| `filter.level` | string |  |  |  | Only elements on this level. |
| `filter.type` | string |  |  |  | Only elements of this type ("Family : Type" or type name). |
| `filter.where` | map |  |  |  | Parameter name to required value. |
| `filter.view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `target` | enum(instance\\|type) |  | `"instance"` |  | Set on the elements or on their types. |
| `values` | map | yes |  |  | Parameter name to value. Numbers are in job units for lengths and degrees for angles; use { "value": 12, "unit": "area" } to state the unit kind (length, area, volume, angle, none). |
| `skipReadOnly` | bool |  | `false` |  | Skip read-only parameters and report them instead of failing. |

Rules across fields: exactly one of `elements`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Elements changed.

Example (lint clean):

```json
{
  "jobId": "example-revit-param-set",
  "title": "Example: param.set",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "param.set",
      "args": {
        "filter": {
          "category": "Rooms",
          "level": "Level 01"
        },
        "values": {
          "Floor Finish": "FL1"
        }
      }
    }
  ]
}
```

## param.shared.ensure

Flags: ensure.

Ensure shared parameters exist in the shared parameter file and are bound to categories.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `file` | string |  |  |  | Shared parameter file. Defaults to the one Revit has set. |
| `group` | string | yes |  |  | Group in the shared parameter file. |
| `parameters` | list<object> | yes |  | at least 1 item | Parameters. |
| `parameters[].name` | string | yes |  |  | Parameter name. |
| `parameters[].dataType` | enum(text\\|integer\\|number\\|length\\|area\\|volume\\|angle\\|yesno\\|url\\|material\\|multiline_text) | yes |  |  | Data type. |
| `parameters[].guid` | string |  |  |  | GUID to use; keeps the parameter identical across projects. |
| `parameters[].description` | string |  |  |  | Tooltip text. |
| `categories` | list<string> | yes |  | at least 1 item | Categories to bind to. |
| `binding` | enum(instance\\|type) |  | `"instance"` |  | Instance or type binding. |
| `propertyGroup` | string |  | `"Data"` |  | Properties palette group, e.g. "Identity Data" or "Data", or a GroupTypeId string. Labels follow Revit's UI language. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `guids` (map): Parameter name to GUID.

Example (lint clean):

```json
{
  "jobId": "example-revit-param-shared-ensure",
  "title": "Example: param.shared.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "param.shared.ensure",
      "args": {
        "group": "Interiors",
        "parameters": [
          {
            "name": "Finish Code",
            "dataType": "text"
          }
        ],
        "categories": [
          "Furniture",
          "Casework"
        ]
      }
    }
  ]
}
```

## view.plan.create

Create a floor plan view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `level` | string | yes |  |  | Level. |
| `crop` | object |  |  |  | Crop region in plan; omitted leaves the crop off. |
| `crop.min` | point2 | yes |  | length | Lower-left corner. |
| `crop.max` | point2 | yes |  | length | Upper-right corner. |
| `name` | string |  |  |  | View name. When the standard sets a view naming rule, a name that does not follow it is warned about. |
| `viewFamilyType` | string |  |  |  | View type to use. Defaults to the first of the right family. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-plan-create",
  "title": "Example: view.plan.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.plan.create",
      "args": {
        "level": "Level 01",
        "name": "Level 01 - Furniture Plan",
        "scale": "1:50"
      }
    }
  ]
}
```

## view.ceiling.create

Create a reflected ceiling plan view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `level` | string | yes |  |  | Level. |
| `crop` | object |  |  |  | Crop region in plan. |
| `crop.min` | point2 | yes |  | length | Lower-left corner. |
| `crop.max` | point2 | yes |  | length | Upper-right corner. |
| `name` | string |  |  |  | View name. When the standard sets a view naming rule, a name that does not follow it is warned about. |
| `viewFamilyType` | string |  |  |  | View type to use. Defaults to the first of the right family. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-ceiling-create",
  "title": "Example: view.ceiling.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.ceiling.create",
      "args": {
        "level": "Level 01",
        "name": "Level 01 - RCP",
        "scale": "1:50"
      }
    }
  ]
}
```

## view.section.create

Create a section from a cut line in plan.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `start` | point2 | yes |  | length | Cut line start in plan. |
| `end` | point2 | yes |  | length | Cut line end; the view looks to the left of start to end. |
| `depth` | length | yes |  | > 0; length | Far clip depth. |
| `bottom` | length | yes |  | length | Bottom of the view, as an elevation. |
| `top` | length | yes |  | length | Top of the view, as an elevation. |
| `flip` | bool |  | `false` |  | Look the other way. |
| `name` | string |  |  |  | View name. When the standard sets a view naming rule, a name that does not follow it is warned about. |
| `viewFamilyType` | string |  |  |  | View type to use. Defaults to the first of the right family. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-section-create",
  "title": "Example: view.section.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.section.create",
      "args": {
        "start": [
          0,
          2000
        ],
        "end": [
          12000,
          2000
        ],
        "depth": 8000,
        "bottom": -1000,
        "top": 9000,
        "name": "Section A-A",
        "scale": "1:100"
      }
    }
  ]
}
```

## view.elevation.create

Create an elevation from a marker in a plan view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `point` | point2 | yes |  | length | Marker location. |
| `direction` | enum(north\\|south\\|east\\|west) | yes |  |  | Direction the viewer looks, in project coordinates (north = +Y), not the face shown: "north" looks north and shows the south face. |
| `planView` | ref | yes |  |  | Plan view hosting the marker. A view id, a reference to an op that created it, or the view's exact name. |
| `name` | string |  |  |  | View name. When the standard sets a view naming rule, a name that does not follow it is warned about. |
| `viewFamilyType` | string |  |  |  | View type to use. Defaults to the first of the right family. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-elevation-create",
  "title": "Example: view.elevation.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.elevation.create",
      "args": {
        "point": [
          3000,
          2000
        ],
        "direction": "north",
        "planView": "Level 01",
        "name": "Meeting - North",
        "scale": "1:50"
      }
    }
  ]
}
```

## view.3d.create

Create an isometric or perspective 3D view, optionally with a section box.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string |  |  |  | View name. |
| `perspective` | bool |  | `false` |  | Perspective camera instead of isometric. |
| `eye` | point3 |  |  | length | Camera position. |
| `target` | point3 |  |  | length | Point looked at. |
| `sectionBox` | object |  |  |  | Section box. |
| `sectionBox.min` | point3 | yes |  | length | Minimum corner. |
| `sectionBox.max` | point3 | yes |  | length | Maximum corner. |
| `template` | string |  |  |  | View template. |

Rules across fields: `eye` requires `target`; `target` requires `eye`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-3d-create",
  "title": "Example: view.3d.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.3d.create",
      "args": {
        "name": "3D - Level 01 Fit-out",
        "sectionBox": {
          "min": [
            0,
            0,
            0
          ],
          "max": [
            12000,
            8000,
            3600
          ]
        }
      }
    }
  ]
}
```

## view.rename

Rename views.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `views` | list<object> | yes |  | at least 1 item | Views and their new names. |
| `views[].view` | ref | yes |  |  | The view. A view id, a reference to an op that created it, or the view's exact name. |
| `views[].name` | string | yes |  |  | New name. |

Example (lint clean):

```json
{
  "jobId": "example-revit-view-rename",
  "title": "Example: view.rename",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.rename",
      "args": {
        "views": [
          {
            "view": "Level 1",
            "name": "Level 01 - General Plan"
          }
        ]
      }
    }
  ]
}
```

## viewtemplate.apply

Assign a view template to views.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `views` | list<ref> | yes |  | at least 1 item | Views. |
| `template` | string | yes |  |  | View template name. |

Example (lint clean):

```json
{
  "jobId": "example-revit-viewtemplate-apply",
  "title": "Example: viewtemplate.apply",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "viewtemplate.apply",
      "args": {
        "views": [
          "Level 01 - General Plan"
        ],
        "template": "Architectural Plan"
      }
    }
  ]
}
```

## view.filter.ensure

Flags: ensure.

Create a rule-based view filter with graphic overrides and apply it to views or templates.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Filter name. |
| `categories` | list<string> | yes |  | at least 1 item | Categories the filter applies to. |
| `rules` | list<object> | yes |  | at least 1 item | Rules on parameters. |
| `rules[].parameter` | string | yes |  |  | Parameter name. |
| `rules[].operator` | enum(equals\\|not_equals\\|greater\\|greater_or_equal\\|less\\|less_or_equal\\|contains\\|not_contains\\|begins_with\\|ends_with\\|has_value\\|has_no_value) | yes |  |  | Comparison. |
| `rules[].value` | any |  |  |  | Value to compare (not needed for has_value and has_no_value). |
| `logic` | enum(and\\|or) |  | `"and"` |  | How rules combine. |
| `applyTo` | list<ref> |  |  |  | Views or templates to add the filter to. |
| `overrides` | object |  |  |  | Graphic overrides where the filter is applied. |
| `overrides.visible` | bool |  | `true` |  | Visibility. |
| `overrides.halftone` | bool |  | `false` |  | Halftone. |
| `overrides.lineColor` | string |  |  |  | Projection line colour "r,g,b". |
| `overrides.lineWeight` | integer |  |  | >= 1; <= 16 | Projection line weight 1-16. |
| `overrides.surfaceColor` | string |  |  |  | Surface foreground pattern colour "r,g,b". |
| `overrides.transparency` | integer |  |  | >= 0; <= 100 | Surface transparency percent. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `filter` (ref): Filter id.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-filter-ensure",
  "title": "Example: view.filter.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.filter.ensure",
      "args": {
        "name": "FF&E - Existing to remain",
        "categories": [
          "Furniture"
        ],
        "rules": [
          {
            "parameter": "Comments",
            "operator": "equals",
            "value": "Existing to remain"
          }
        ],
        "applyTo": [
          "Level 01 - General Plan"
        ],
        "overrides": {
          "halftone": true
        }
      }
    }
  ]
}
```

## sheet.create

Flags: ensure.

Create a sheet with number, name and title block.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `number` | string | yes |  |  | Sheet number. When the standard sets a sheet numbering rule, a new number that does not follow it is warned about; a number that differs from an existing one only by case is refused. |
| `name` | string | yes |  |  | Sheet name. |
| `titleBlock` | string |  |  | matches `.+\S\s:\s\S.+` | Title block type. Without it a new sheet gets the standard's title block (titleBlock.familyType) when the standard names one, and none otherwise. Written "Family : Type". |
| `parameters` | map |  |  |  | Sheet parameter name to value (drawn by, checked by...). |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `sheet` (ref): Sheet id.
- `titleBlock` (ref): Title block instance id.

Example (lint clean):

```json
{
  "jobId": "example-revit-sheet-create",
  "title": "Example: sheet.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "sheet.create",
      "args": {
        "number": "A-101",
        "name": "Ground Floor Plan",
        "titleBlock": "A1 metric : A1"
      }
    }
  ]
}
```

## viewport.place

Place a view on a sheet at a point.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `sheet` | ref | yes |  |  | Sheet id, reference or sheet number. |
| `view` | ref | yes |  |  | The view. A view id, a reference to an op that created it, or the view's exact name. |
| `centre` | point2 | yes |  | length | Viewport centre in sheet coordinates (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted). |
| `viewportType` | string |  |  |  | Viewport type (title style). |
| `detailNumber` | string |  |  |  | Detail number. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `viewport` (ref): Viewport id.

Example (lint clean):

```json
{
  "jobId": "example-revit-viewport-place",
  "title": "Example: viewport.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "viewport.place",
      "args": {
        "sheet": "A-101",
        "view": "Level 01 - General Plan",
        "centre": [
          400,
          300
        ]
      }
    }
  ]
}
```

## viewports.layout

Place several views on a sheet automatically, in aligned rows inside the drawable area.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `sheet` | ref | yes |  |  | Sheet id, reference or sheet number. |
| `views` | list<ref> | yes |  | at least 1 item | Views in reading order: view ids, references to ops that created them, or exact view names. |
| `area` | object |  |  |  | Drawable area in sheet coordinates (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted; margin, gapX and gapY too). Defaults to the title block's extents less margin. |
| `area.min` | point2 | yes |  | length | Lower-left corner. |
| `area.max` | point2 | yes |  | length | Upper-right corner. |
| `viewportType` | string |  |  |  | Viewport type. |
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
  "jobId": "example-revit-viewports-layout",
  "title": "Example: viewports.layout",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
        "sheet": "A-101",
        "views": [
          "Level 01 - General Plan",
          "Meeting - North"
        ],
        "margin": 10,
        "gapX": 15,
        "gapY": 15
      }
    }
  ]
}
```

## schedule.create

Flags: ensure.

Create a schedule with fields, filters, sorting and grouping, and totals.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Schedule name. |
| `category` | string | yes |  |  | Category scheduled, or "Multi-Category" for a multi-category schedule. |
| `fields` | list<object> | yes |  | at least 1 item | Columns in order. |
| `fields[].parameter` | string | yes |  |  | Parameter name, or Count. |
| `fields[].heading` | string |  |  |  | Column heading. Defaults to Revit's heading for the field. |
| `fields[].hidden` | bool |  | `false` |  | Hidden column (still usable to sort and filter). |
| `fields[].width` | length |  |  | > 0; length | Column width on the sheet (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted). |
| `fields[].total` | bool |  | `false` |  | Calculate totals for this column. |
| `filters` | list<object> |  |  | at most 8 items | Filters (up to the limit Revit allows). |
| `filters[].field` | string | yes |  |  | Parameter name of a field. |
| `filters[].operator` | enum(equals\\|not_equals\\|greater\\|greater_or_equal\\|less\\|less_or_equal\\|contains\\|not_contains\\|begins_with\\|ends_with\\|has_value\\|has_no_value) | yes |  |  | Comparison. |
| `filters[].value` | any |  |  |  | Value. |
| `sort` | list<object> |  |  |  | Sorting and grouping, in priority order. |
| `sort[].field` | string | yes |  |  | Parameter name of a field. |
| `sort[].order` | enum(ascending\\|descending) |  | `"ascending"` |  | Order. |
| `sort[].header` | bool |  | `false` |  | Group header. |
| `sort[].footer` | bool |  | `false` |  | Group footer with count and totals. |
| `sort[].blankLine` | bool |  | `false` |  | Blank line between groups. |
| `itemize` | bool |  | `true` |  | List every instance; false groups identical rows. |
| `grandTotals` | bool |  | `false` |  | Show grand totals. |
| `includeLinks` | bool |  | `false` |  | Include elements of linked models (refused for a schedule kind that cannot include them). |
| `phase` | string |  |  |  | Phase. |
| `template` | string |  |  |  | Schedule view template. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `schedule` (ref): Schedule view id.
- `rowCount` (integer): Body rows read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-schedule-create",
  "title": "Example: schedule.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "schedule.create",
      "args": {
        "name": "Door Schedule",
        "category": "Doors",
        "fields": [
          {
            "parameter": "Mark"
          },
          {
            "parameter": "Type"
          },
          {
            "parameter": "Width"
          },
          {
            "parameter": "Height"
          }
        ],
        "sort": [
          {
            "field": "Mark"
          }
        ]
      }
    }
  ]
}
```

## tag.all

Tag every untagged element of a category in a view. Elements already tagged in the view are always left alone.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `category` | string | yes |  |  | Category to tag. |
| `view` | ref | yes |  |  | View to tag in. A view id, a reference to an op that created it, or the view's exact name. |
| `tagType` | string |  |  | matches `.+\S\s:\s\S.+` | Tag type. Defaults to the category's default tag. Written "Family : Type". |
| `leader` | bool |  | `false` |  | Add a leader. |
| `offset` | point2 |  | `[0,0]` | length | Tag offset from the element's location (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted), scaled by the view scale. |
| `orientation` | enum(horizontal\\|vertical) |  | `"horizontal"` |  | Tag orientation. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `tagged` (integer): Tags created.
- `untaggable` (list<ref>): Elements that could not be tagged.

Example (lint clean):

```json
{
  "jobId": "example-revit-tag-all",
  "title": "Example: tag.all",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "tag.all",
      "args": {
        "category": "Doors",
        "view": "Level 01 - General Plan"
      }
    }
  ]
}
```

## dim.grids

Dimension grids in a view: grid to grid strings and an overall dimension.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Plan view. A view id, a reference to an op that created it, or the view's exact name. |
| `direction` | enum(both\\|horizontal\\|vertical) |  | `"both"` |  | Which grids. |
| `offset` | length |  |  | > 0; length | Distance of the string from the grid ends (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted), scaled by the view scale. Defaults to 12 mm. |
| `dimensionType` | string |  |  |  | Linear dimension type. |
| `overall` | bool |  | `true` |  | Add an overall dimension. |
| `side` | enum(top-right\\|bottom-left\\|both) |  | `"top-right"` |  | Where the strings go: top-right puts them at the top of vertical grids and right of horizontal ones (in the view's up and right directions), bottom-left at the other ends, both at both. |
| `stringSpacing` | length |  |  | > 0; length | Gap between the overall string and the grid string (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted), scaled by the view scale. Defaults to 8 mm. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `dimensions` (list<ref>): Dimension ids.

Example (lint clean):

```json
{
  "jobId": "example-revit-dim-grids",
  "title": "Example: dim.grids",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "dim.grids",
      "args": {
        "view": "Level 01 - General Plan"
      }
    }
  ]
}
```

## text.note

Add a text note in a view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | View. A view id, a reference to an op that created it, or the view's exact name. |
| `text` | string | yes |  |  | Note text. |
| `position` | point2 | yes |  | length | Plans: project coordinates [x, y] at the level. Sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]. Sheets: sheet coordinates (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted). Drafting views and legends: the view's own coordinates. Left alignment places the top-left corner of the text there, right the top-right, center the top-middle. |
| `type` | string |  |  |  | Text note type. |
| `width` | length |  |  | > 0; length | Wrap width (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted). |
| `rotation` | angle |  | `0` | degrees | Rotation. |
| `align` | enum(left\\|center\\|right) |  | `"left"` |  | Horizontal alignment. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `note` (ref): Text note id.

Example (lint clean):

```json
{
  "jobId": "example-revit-text-note",
  "title": "Example: text.note",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "text.note",
      "args": {
        "view": "Level 01 - General Plan",
        "text": "ALL DIMENSIONS TO STRUCTURE",
        "position": [
          0,
          -3000
        ]
      }
    }
  ]
}
```

## revision.ensure

Flags: ensure.

Create revisions and add them to sheets.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `revisions` | list<object> | yes |  | at least 1 item | Revisions in order. |
| `revisions[].description` | string | yes |  |  | Description. |
| `revisions[].date` | string |  |  |  | Revision date as text. |
| `revisions[].issuedTo` | string |  |  |  | Issued to. |
| `revisions[].issuedBy` | string |  |  |  | Issued by. |
| `revisions[].code` | string |  |  |  | Expected revision number; checked against the standard's revision formats and read back. Revit assigns numbers from the numbering sequence; it cannot be set. |
| `revisions[].issued` | bool |  | `false` |  | Mark as issued (locks it). |
| `addToSheets` | list<string> |  |  |  | Sheet numbers that get these revisions. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `revisions` (list<ref>): Revision ids.

Example (lint clean):

```json
{
  "jobId": "example-revit-revision-ensure",
  "title": "Example: revision.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "revision.ensure",
      "args": {
        "revisions": [
          {
            "description": "Issued for planning",
            "date": "2026-12-01"
          }
        ],
        "addToSheets": [
          "A-101"
        ]
      }
    }
  ]
}
```

## export.pdf

Export sheets or views to PDF.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `sheets` | list<string> |  |  |  | Sheet numbers in order. |
| `views` | list<ref> |  |  |  | Views. |
| `combine` | bool |  | `false` |  | One PDF for all. |
| `fileName` | string |  |  |  | File name pattern. Placeholders: {sheetNumber}, {sheetName}, {revision}, {view}, {viewType}, {drawing}, {date}, {jobId}. Example: "{sheetNumber} - {sheetName}.pdf". Names are checked against the standard's file naming rule when one is set. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `colour` | enum(colour\\|grayscale\\|black_line) |  | `"colour"` |  | Colour mode. |
| `rasterQuality` | enum(low\\|medium\\|high\\|presentation) |  | `"high"` |  | Raster quality. |
| `hideCropBoundaries` | bool |  | `true` |  | Hide crop boundaries. |
| `hideScopeBoxes` | bool |  | `true` |  | Hide scope boxes. |
| `hideReferencePlanes` | bool |  | `true` |  | Hide reference planes. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Rules across fields: at least one of `sheets`, `views`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `files` (list<object>): { path, pages, items } per file; pages is null when the page count cannot be read from the file.

Example (lint clean):

```json
{
  "jobId": "example-revit-export-pdf",
  "title": "Example: export.pdf",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "export.pdf",
      "args": {
        "sheets": [
          "A-101",
          "A-102"
        ],
        "fileName": "{sheetNumber} - {sheetName}"
      }
    }
  ]
}
```

## export.dwg

Export sheets or views to DWG with an export setup.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `sheets` | list<string> |  |  |  | Sheet numbers. |
| `views` | list<ref> |  |  |  | Views. |
| `setup` | string |  |  |  | DWG export setup name. Defaults to the active one. |
| `fileName` | string |  |  |  | File name pattern. Placeholders: {sheetNumber}, {sheetName}, {revision}, {view}, {viewType}, {drawing}, {date}, {jobId}. Example: "{sheetNumber}.dwg". Names are checked against the standard's file naming rule when one is set. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `mergedViews` | bool |  | `true` |  | Export each sheet as one file without view xrefs. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Rules across fields: at least one of `sheets`, `views`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `files` (list<string>): Paths written.
- `companions` (list<string>): Other files the export wrote next to the DWGs (view xrefs when mergedViews is false, images).

Example (lint clean):

```json
{
  "jobId": "example-revit-export-dwg",
  "title": "Example: export.dwg",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "export.dwg",
      "args": {
        "sheets": [
          "A-101"
        ]
      }
    }
  ]
}
```

## export.ifc

Export the model to IFC.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `fileName` | string | yes |  |  | File name, e.g. "{jobId}.ifc". |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `version` | enum(ifc2x3\\|ifc4\\|ifc4x3) |  | `"ifc4"` |  | IFC schema. ifc4x3 is the Reference View on 2027 and the only IFC4x3 option of 2024-2026, so the model view can differ by release; readback reports the file's. |
| `view` | ref |  |  |  | Export only elements visible in this 3D view. A view id, a reference to an op that created it, or the view's exact name. |
| `setup` | string |  |  |  | Not supported: named IFC export setups belong to the IFC exporter add-in and the API cannot load them by name. Giving one is refused. |
| `baseQuantities` | bool |  | `false` |  | Export base quantities. |
| `spaceBoundaries` | enum(none\\|first\\|second) |  | `"none"` |  | Space boundary level. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `file` (string): Path written.

Example (lint clean):

```json
{
  "jobId": "example-revit-export-ifc",
  "title": "Example: export.ifc",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "export.ifc",
      "args": {
        "fileName": "2417-ARCH",
        "version": "ifc4"
      }
    }
  ]
}
```

## export.image

Export views or sheets to images.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `views` | list<ref> | yes |  | at least 1 item | Views or sheets: view ids, references, exact view names, or sheet:<number> for a sheet. |
| `format` | enum(png\\|jpg) |  | `"png"` |  | Image format. |
| `pixelWidth` | integer |  | `2000` | >= 32; <= 15000 | Image width in pixels, 32 to 15000. |
| `fileName` | string |  |  |  | File name pattern. Placeholders: {sheetNumber}, {sheetName}, {revision}, {view}, {viewType}, {drawing}, {date}, {jobId}. Example: "{view}.png". Names are checked against the standard's file naming rule when one is set. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `files` (list<string>): Paths written.

Example (lint clean):

```json
{
  "jobId": "example-revit-export-image",
  "title": "Example: export.image",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "export.image",
      "args": {
        "views": [
          "sheet:A-101"
        ],
        "pixelWidth": 3000
      }
    }
  ]
}
```

## element.delete

Flags: destructive.

Delete the listed elements only. The ids must come from a snapshot; count must match.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `ids` | list<ref> | yes |  | at least 1 item | Element ids to delete. |
| `expectCount` | integer | yes |  |  | Must equal the number of ids; guards against a list built by mistake. |
| `allowDependents` | bool |  | `false` |  | Allow Revit to delete dependent elements too (they are listed in the report). When false any dependent deletion fails the op. Parts of a listed element (its sketch, sketch plane and sketch lines) are always deleted with it and are not counted as dependents. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `deleted` (list<ref>): Everything deleted, dependents included.

Example (lint clean):

```json
{
  "jobId": "example-revit-element-delete",
  "title": "Example: element.delete",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "element.delete",
      "args": {
        "ids": [
          "412233",
          "412240"
        ],
        "expectCount": 2
      }
    }
  ]
}
```

## cleanup.unplacedRooms

Flags: destructive.

Delete unplaced (and optionally not enclosed or redundant) rooms, listed by id from a snapshot.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `ids` | list<ref> | yes |  | at least 1 item | Room ids to delete; each must be unplaced (or, when allowed, not enclosed or redundant). |
| `includeNotEnclosed` | bool |  | `false` |  | Allow deleting placed rooms that are not enclosed. |
| `includeRedundant` | bool |  | `false` |  | Allow deleting redundant rooms. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `deleted` (list<ref>): Rooms deleted.

Example (lint clean):

```json
{
  "jobId": "example-revit-cleanup-unplacedRooms",
  "title": "Example: cleanup.unplacedRooms",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "cleanup.unplacedRooms",
      "args": {
        "ids": [
          "510021",
          "510022"
        ]
      }
    }
  ]
}
```

## cleanup.unusedViews

Flags: destructive.

Delete views that are not on sheets, listed by id from a snapshot.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `ids` | list<ref> | yes |  | at least 1 item | View ids; each must be off every sheet and not a template unless includeTemplates. |
| `includeTemplates` | bool |  | `false` |  | Allow deleting unused view templates. |
| `keepWithDependents` | bool |  | `true` |  | Skip views that have dependent views. |
| `dryRun` | bool |  | `false` |  | Delete nothing; list the views that would be deleted in wouldDelete. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `deleted` (list<ref>): Views deleted.
- `skipped` (list<ref>): Views kept and why in warnings.
- `wouldDelete` (list<ref>): With dryRun: views that would be deleted.

Example (lint clean):

```json
{
  "jobId": "example-revit-cleanup-unusedViews",
  "title": "Example: cleanup.unusedViews",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "cleanup.unusedViews",
      "args": {
        "ids": [
          "620011"
        ],
        "dryRun": true
      }
    }
  ]
}
```

## workset.assign

Move elements to a workset.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `elements` | list<ref> |  |  |  | Element ids. |
| `filter` | object |  |  |  | Select elements instead of listing ids. |
| `filter.category` | string | yes |  |  | Category, by built-in name (OST_Doors) or display name (Doors). |
| `filter.level` | string |  |  |  | Only elements on this level. |
| `filter.type` | string |  |  |  | Only elements of this type ("Family : Type" or type name). |
| `filter.where` | map |  |  |  | Parameter name to required value. |
| `filter.view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `workset` | string | yes |  |  | User workset name. |
| `create` | bool |  | `false` |  | Create the workset if missing. |

Rules across fields: exactly one of `elements`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Elements moved.

Example (lint clean):

```json
{
  "jobId": "example-revit-workset-assign",
  "title": "Example: workset.assign",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "workset.assign",
      "args": {
        "filter": {
          "category": "Furniture"
        },
        "workset": "FF&E"
      }
    }
  ]
}
```

## family.place

Place family instances of a loaded type: on a level, hosted on a wall, floor, ceiling or roof, or on a work plane.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `type` | string | yes |  | matches `.+\S\s:\s\S.+` | Family type to place. Written "Family : Type". |
| `category` | string |  |  |  | Category the type must belong to (OST_Furniture or Furniture); narrows the lookup when two families share a type name. |
| `points` | list<point2> | yes |  | at least 1 item; length | Insertion points, one instance each. |
| `level` | string |  |  |  | Level the instances are placed on (level-based families) or associated with (hosted families). Defaults to the host's level for hosted families. |
| `offset` | length |  | `0` | length | Height above the level; on a wall face or a vertical reference plane, the height of the point above the level; on a horizontal face or work plane, the offset from it. |
| `host` | ref |  |  |  | Wall, floor, ceiling or roof to host the instances. |
| `face` | enum(top\\|bottom\\|exterior\\|interior) |  |  |  | With host and a face-based family: which face of the host to place on. |
| `workPlane` | string |  |  |  | Reference plane or level, by name, to place work-plane-based families on. |
| `rotation` | angle |  | `0` | degrees | Rotation about the vertical axis through each point, counter-clockwise. |
| `flipHand` | bool |  | `false` |  | Flip left/right after placing. |
| `flipFacing` | bool |  | `false` |  | Flip front/back after placing. |
| `mark` | string |  |  |  | Mark. With several points each instance gets mark-1, mark-2 ... |
| `parameters` | map |  |  |  | Instance parameter name to value, set after placing. |

Rules across fields: atMostOne `host`, `workPlane`; `face` requires `host`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `elements` (list<ref>): Instances placed, in point order.
- `count` (integer): Number placed.

Example (lint clean):

```json
{
  "jobId": "example-revit-family-place",
  "title": "Example: family.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "family.place",
      "args": {
        "type": "Desk : 1525 x 762mm",
        "points": [
          [
            1000,
            2000
          ],
          [
            3000,
            2000
          ]
        ],
        "level": "Level 1",
        "rotation": 90,
        "mark": "D"
      }
    }
  ]
}
```

## group.place

Make a model group from elements, or place instances of an existing model group type.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `elements` | list<ref> |  |  |  | Elements to group. Elements already in a group are refused. |
| `name` | string |  |  |  | Name for the new group type made from elements. |
| `groupType` | string |  |  |  | Existing model group type to place, by name. |
| `points` | list<point2> |  |  | length | Group origin for each instance placed. With elements, extra copies of the new group. |
| `level` | string |  |  |  | Level the points are on (their height is the level's elevation). |
| `rotation` | angle |  | `0` | degrees | Rotation of each placed instance about its origin, counter-clockwise. |

Rules across fields: exactly one of `elements`, `groupType`; `elements` requires `name`; `groupType` requires `points`; `points` requires `level`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `groupType` (ref): Group type made or placed.
- `group` (ref): With elements: the group made from them.
- `placed` (list<ref>): Instances placed at points.

Example (lint clean):

```json
{
  "jobId": "example-revit-group-place",
  "title": "Example: group.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "group.place",
      "args": {
        "elements": [
          "316001",
          "316002"
        ],
        "name": "Toilet core A",
        "points": [
          [
            0,
            0
          ],
          [
            12000,
            0
          ]
        ],
        "level": "Level 2"
      }
    }
  ]
}
```

## element.transform

Move, copy, rotate or mirror elements in plan.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `elements` | list<ref> | yes |  | at least 1 item | Elements to transform. Pinned elements are refused. |
| `action` | enum(move\\|copy\\|rotate\\|mirror) | yes |  |  | move or copy by vector, rotate about a vertical axis, or mirror about a vertical plane. |
| `vector` | point2 |  |  | length | move and copy: displacement in plan. |
| `dz` | length |  | `0` | length | move and copy: vertical displacement. |
| `copies` | integer |  | `1` | >= 1; <= 200 | copy: number of copies, each one vector further on. |
| `centre` | point2 |  |  | length | rotate: the vertical axis passes through this point. |
| `angle` | angle |  |  | degrees | rotate: angle, counter-clockwise. |
| `axis` | object |  |  |  | mirror: the vertical mirror plane through this line in plan. |
| `axis.start` | point2 | yes |  | length | Line start. |
| `axis.end` | point2 | yes |  | length | Line end. |
| `keepOriginal` | bool |  | `true` |  | mirror: make mirrored copies and keep the originals. When false the elements themselves are mirrored. |

Rules across fields: atMostOne `vector`, `centre`, `axis`; `centre` requires `angle`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `elements` (list<ref>): move, rotate and mirror in place: the same elements. copy and mirror copies: every new element, copies in order.
- `count` (integer): Number of ids in elements.

Example (lint clean):

```json
{
  "jobId": "example-revit-element-transform",
  "title": "Example: element.transform",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "element.transform",
      "args": {
        "elements": [
          "316001"
        ],
        "action": "rotate",
        "centre": [
          0,
          0
        ],
        "angle": 45
      }
    }
  ]
}
```

## phase.set

Set Phase Created and Phase Demolished on elements, and phase and phase filter on views.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `elements` | list<ref> |  |  |  | Elements to set phases on. |
| `filter` | object |  |  |  | Select elements instead of listing ids. |
| `filter.category` | string | yes |  |  | Category, by built-in name (OST_Doors) or display name (Doors). |
| `filter.level` | string |  |  |  | Only elements on this level. |
| `filter.type` | string |  |  |  | Only elements of this type ("Family : Type" or type name). |
| `filter.where` | map |  |  |  | Parameter name to required value. |
| `filter.view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `created` | string |  |  |  | Phase Created, by phase name. |
| `demolished` | string |  |  |  | Phase Demolished, by phase name, or "none" to clear it. |
| `views` | list<ref> |  |  |  | Views to set the phase and phase filter of. |
| `viewPhase` | string |  |  |  | Phase of the views, by name. |
| `phaseFilter` | string |  |  |  | Phase filter of the views, by name, or "none". |

Rules across fields: atMostOne `elements`, `filter`; at least one of `elements`, `filter`, `views`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `elementCount` (integer): Elements changed.
- `viewCount` (integer): Views changed.

Example (lint clean):

```json
{
  "jobId": "example-revit-phase-set",
  "title": "Example: phase.set",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "phase.set",
      "args": {
        "elements": [
          "316001"
        ],
        "created": "Existing",
        "demolished": "New Construction"
      }
    }
  ]
}
```

## compoundtype.ensure

Flags: ensure.

Create a floor, ceiling or roof type with a compound layer build-up.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `kind` | enum(floor\\|ceiling\\|roof) | yes |  |  | Type family. |
| `name` | string | yes |  |  | Type name, e.g. "Slab - 200 Concrete". |
| `basedOn` | string |  |  |  | Type of the same kind to duplicate. Defaults to the first type of that kind with a layered structure. |
| `layers` | list<object> | yes |  | at least 1 item | Layers from top to bottom. |
| `layers[].function` | enum(structure\\|substrate\\|thermal\\|membrane\\|finish1\\|finish2) | yes |  |  | Layer function. |
| `layers[].material` | string | yes |  |  | Material name; created plainly if missing. |
| `layers[].thickness` | length | yes |  | >= 0; length | Layer thickness (0 only for membranes). |
| `layers[].core` | bool |  | `false` |  | Inside the core boundaries. Core layers must be contiguous. |
| `layers[].variable` | bool |  | `false` |  | Floors and roofs: the layer that varies in thickness under shape edits. One at most. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `type` (ref): Type id.
- `thickness` (length): Total thickness read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-compoundtype-ensure",
  "title": "Example: compoundtype.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "compoundtype.ensure",
      "args": {
        "kind": "floor",
        "name": "FL 250 Concrete + Screed",
        "layers": [
          {
            "function": "finish1",
            "material": "Screed",
            "thickness": 50
          },
          {
            "function": "structure",
            "material": "Concrete",
            "thickness": 200,
            "core": true
          }
        ]
      }
    }
  ]
}
```

## wallsweep.add

Add wall sweeps (skirtings, cornices) or reveals to walls, from a sweep or reveal type.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `walls` | list<ref> |  |  |  | Walls to add to. |
| `filter` | object |  |  |  | Select elements instead of listing ids. |
| `filter.category` | string | yes |  |  | Category, by built-in name (OST_Doors) or display name (Doors). |
| `filter.level` | string |  |  |  | Only elements on this level. |
| `filter.type` | string |  |  |  | Only elements of this type ("Family : Type" or type name). |
| `filter.where` | map |  |  |  | Parameter name to required value. |
| `filter.view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `type` | string | yes |  |  | Wall sweep type (kind sweep) or reveal type (kind reveal), by name. |
| `kind` | enum(sweep\\|reveal) |  | `"sweep"` |  | Sweep or reveal. |
| `distance` | length | yes |  | >= 0; length | Height above the wall base, or below the wall top with measuredFrom top. |
| `measuredFrom` | enum(base\\|top) |  | `"base"` |  | Where distance is measured from. |
| `side` | enum(exterior\\|interior) |  | `"interior"` |  | Wall face. |
| `wallOffset` | length |  | `0` | length | Offset from the wall face. |
| `cutByInserts` | bool |  | `true` |  | Doors and windows interrupt the sweep. |
| `skipExisting` | bool |  | `true` |  | Leave a wall alone when it already has a sweep of this type on this side at this distance. |

Rules across fields: exactly one of `walls`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `sweeps` (list<ref>): Sweeps or reveals created.
- `skipped` (list<ref>): Walls that already had one.

Example (lint clean):

```json
{
  "jobId": "example-revit-wallsweep-add",
  "title": "Example: wallsweep.add",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "wallsweep.add",
      "args": {
        "walls": [
          "316010"
        ],
        "type": "Cornice : 100x100",
        "distance": 2700,
        "side": "exterior"
      }
    }
  ]
}
```

## fillpattern.ensure

Flags: ensure.

Create fill patterns: stacked tiles, running bond, parallel lines or raw grids.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `patterns` | list<object> | yes |  | at least 1 item | Patterns to ensure. |
| `patterns[].name` | string | yes |  |  | Pattern name. |
| `patterns[].target` | enum(model\\|drafting) | yes |  |  | model patterns are sized in the model; drafting patterns in paper units (millimetres on the printed sheet for a metric job, inches for an imperial one). |
| `patterns[].orientation` | enum(to_view\\|to_host\\|as_text) |  |  |  | Drafting patterns: how the pattern aligns in host layers. Defaults to to_view; model patterns always use to_host. |
| `patterns[].stack` | object |  |  |  | Stacked grid: rectangles width by height. |
| `patterns[].stack.width` | length | yes |  | > 0; length | Tile width. |
| `patterns[].stack.height` | length | yes |  | > 0; length | Tile height. |
| `patterns[].bond` | object |  |  |  | Running bond: courses of units, each course shifted by a fraction of the length. |
| `patterns[].bond.length` | length | yes |  | > 0; length | Unit length. |
| `patterns[].bond.height` | length | yes |  | > 0; length | Course height. |
| `patterns[].bond.fraction` | integer |  | `2` | >= 2; <= 6 | Shift per course is length divided by this (2 = half bond). |
| `patterns[].lines` | object |  |  |  | Parallel lines. |
| `patterns[].lines.spacing` | length | yes |  | > 0; length | Distance between lines. |
| `patterns[].lines.angle` | angle |  | `0` | degrees | Line direction, counter-clockwise from horizontal. |
| `patterns[].grids` | list<object> |  |  | at least 1 item; at most 8 items | Raw line families, as in a pattern file. |
| `patterns[].grids[].angle` | angle |  | `0` | degrees | Line direction, counter-clockwise from horizontal. |
| `patterns[].grids[].originU` | length |  | `0` | length | Origin of the first line, u (x). |
| `patterns[].grids[].originV` | length |  | `0` | length | Origin of the first line, v (y). |
| `patterns[].grids[].shift` | length |  | `0` | length | Offset along the line between successive lines. |
| `patterns[].grids[].offset` | length | yes |  | > 0; length | Distance between successive lines. |
| `patterns[].grids[].segments` | list<length> |  |  | length | Dashes (positive) and gaps (negative), alternating from a dash. Empty or missing for a continuous line. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Rules across fields: in each `patterns[]`: exactly one of `stack`, `bond`, `lines`, `grids`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `patterns` (map): Pattern name to element id.

Example (lint clean):

```json
{
  "jobId": "example-revit-fillpattern-ensure",
  "title": "Example: fillpattern.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "fillpattern.ensure",
      "args": {
        "patterns": [
          {
            "name": "Tile 300",
            "target": "model",
            "grids": [
              {
                "angle": 0,
                "offset": 300
              },
              {
                "angle": 90,
                "offset": 300
              }
            ]
          }
        ]
      }
    }
  ]
}
```

## material.ensure

Flags: ensure.

Create Revit materials with identity data, graphics, surface and cut patterns and an optional render appearance.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `materials` | list<object> | yes |  | at least 1 item | Materials to ensure. |
| `materials[].name` | string | yes |  |  | Material name. |
| `materials[].basedOn` | string |  |  |  | Material to duplicate when creating; its assets are shared until edited. |
| `materials[].identity` | object |  |  |  | Identity data. |
| `materials[].identity.description` | string |  |  |  | Description. |
| `materials[].identity.class` | string |  |  |  | Material class (the Material Browser's class). |
| `materials[].identity.category` | string |  |  |  | Material category. |
| `materials[].identity.typeComments` | string |  |  |  | Type comments. |
| `materials[].identity.manufacturer` | string |  |  |  | Manufacturer. |
| `materials[].identity.model` | string |  |  |  | Model. |
| `materials[].identity.cost` | number |  |  | >= 0 | Cost. |
| `materials[].identity.url` | string |  |  |  | URL. |
| `materials[].identity.keynote` | string |  |  |  | Keynote. |
| `materials[].identity.mark` | string |  |  |  | Mark: the finish code tagged on drawings. |
| `materials[].identity.assemblyCode` | string |  |  |  | Assembly code (Uniformat on 2024-2025). |
| `materials[].identity.classificationCode` | string |  |  |  | Classification code (OmniClass on 2024-2025). |
| `materials[].shading` | object |  |  |  | Graphics shading. |
| `materials[].shading.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Shading colour. Written "r,g,b", each 0-255. |
| `materials[].shading.transparency` | integer |  |  | >= 0; <= 100 | Transparency 0-100. |
| `materials[].shading.useRenderAppearance` | bool |  |  |  | Shade with the render appearance. New materials default to false. |
| `materials[].surface` | object |  |  |  | Surface patterns. |
| `materials[].surface.foreground` | object |  |  |  | Foreground surface pattern. |
| `materials[].surface.foreground.pattern` | string | yes |  |  | Fill pattern name, "solid" for solid fill, or "none" to clear. |
| `materials[].surface.foreground.target` | enum(model\\|drafting) |  | `"drafting"` |  | Which kind of pattern the name refers to. Model patterns stay fixed to the model; drafting patterns keep their printed size. |
| `materials[].surface.foreground.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Pattern colour. Written "r,g,b", each 0-255. |
| `materials[].surface.background` | object |  |  |  | Background surface pattern. |
| `materials[].surface.background.pattern` | string | yes |  |  | Drafting fill pattern name, "solid" for solid fill, or "none" to clear. |
| `materials[].surface.background.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Pattern colour. Written "r,g,b", each 0-255. |
| `materials[].cut` | object |  |  |  | Cut patterns. |
| `materials[].cut.foreground` | object |  |  |  | Foreground cut pattern. |
| `materials[].cut.foreground.pattern` | string | yes |  |  | Drafting fill pattern name, "solid" for solid fill, or "none" to clear. |
| `materials[].cut.foreground.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Pattern colour. Written "r,g,b", each 0-255. |
| `materials[].cut.background` | object |  |  |  | Background cut pattern. |
| `materials[].cut.background.pattern` | string | yes |  |  | Drafting fill pattern name, "solid" for solid fill, or "none" to clear. |
| `materials[].cut.background.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Pattern colour. Written "r,g,b", each 0-255. |
| `materials[].appearance` | object |  |  |  | Render appearance. Without from, the material's current appearance asset is edited (made its own first unless shareAsset). |
| `materials[].appearance.from` | string |  |  |  | Appearance asset in the model, by asset name, or the material whose asset to copy. |
| `materials[].appearance.shareAsset` | bool |  | `false` |  | Use the asset as it is, shared with the materials already using it. Otherwise the material gets its own copy, named like it. |
| `materials[].appearance.color` | string |  |  | matches `\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*` | Generic diffuse colour. Written "r,g,b", each 0-255. |
| `materials[].appearance.texture` | object |  |  |  | Generic diffuse image. |
| `materials[].appearance.texture.path` | string | yes |  |  | Image file, absolute. A path in a user profile is refused unless allowLocalPath. |
| `materials[].appearance.texture.width` | length |  |  | > 0; length | Real-world width of one repeat. |
| `materials[].appearance.texture.height` | length |  |  | > 0; length | Real-world height of one repeat. |
| `materials[].appearance.texture.offsetX` | length |  | `0` | length | Horizontal offset. |
| `materials[].appearance.texture.offsetY` | length |  | `0` | length | Vertical offset. |
| `materials[].appearance.texture.rotation` | angle |  | `0` | >= 0; <= 360; degrees | Rotation, 0 to 360 degrees. |
| `materials[].appearance.texture.repeat` | bool |  | `true` |  | Tile the image in both directions. |
| `materials[].appearance.allowLocalPath` | bool |  | `false` |  | Accept a texture path inside a user profile; other machines will not find it. |
| `materials[].thermalAsset` | string |  |  |  | Thermal asset of another material to share, by that material's name. |
| `materials[].structuralAsset` | string |  |  |  | Structural asset of another material to share, by that material's name. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `materials` (map): Material name to element id.
- `appearanceAssets` (map): Material name to appearance asset id, for materials that have one.

Example (lint clean):

```json
{
  "jobId": "example-revit-material-ensure",
  "title": "Example: material.ensure",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
            "name": "Oak Veneer",
            "identity": {
              "description": "Oak veneer panel",
              "keynote": "09 64 00"
            },
            "shading": {
              "color": "160,112,60",
              "transparency": 0
            },
            "surface": {
              "foreground": {
                "pattern": "Tile 300",
                "target": "model"
              }
            },
            "cut": {
              "foreground": {
                "pattern": "solid",
                "color": "90,58,30"
              }
            },
            "appearance": {
              "color": "160,112,60"
            }
          }
        ]
      }
    }
  ]
}
```

## material.assign

Set a material: on a layer of a wall, floor, ceiling or roof type, on a material parameter of elements, on a category, or by painting faces.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `material` | string | yes |  |  | Material name, or "none" to clear. |
| `layer` | object |  |  |  | A layer of a compound type. Changes every instance of the type. |
| `layer.type` | string | yes |  |  | Wall, floor, ceiling or roof type name. |
| `layer.kind` | enum(wall\\|floor\\|ceiling\\|roof) |  |  |  | Which kind of type; needed only when two kinds share the name. |
| `layer.index` | integer |  |  | >= 0 | Layer index, 0 at the exterior (walls) or top (floors, ceilings, roofs). |
| `layer.function` | enum(structure\\|substrate\\|thermal\\|membrane\\|finish1\\|finish2) |  |  |  | Pick the layer by function instead of index. |
| `layer.side` | enum(exterior\\|interior\\|top\\|bottom) |  |  |  | With function: take the matching layer nearest this side. |
| `parameter` | object |  |  |  | A material parameter of elements or of their types. |
| `parameter.elements` | list<ref> |  |  |  | Elements. |
| `parameter.filter` | object |  |  |  | Select elements instead of listing ids. |
| `parameter.filter.category` | string | yes |  |  | Category, by built-in name (OST_Doors) or display name (Doors). |
| `parameter.filter.level` | string |  |  |  | Only elements on this level. |
| `parameter.filter.type` | string |  |  |  | Only elements of this type ("Family : Type" or type name). |
| `parameter.filter.where` | map |  |  |  | Parameter name to required value. |
| `parameter.filter.view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `parameter.target` | enum(instance\\|type) |  | `"instance"` |  | Set the instance parameter or the type parameter. |
| `parameter.parameter` | string |  |  |  | Material parameter name. Defaults to the only material parameter, refused when there are several. |
| `category` | object |  |  |  | A category or subcategory material (Object Styles). |
| `category.category` | string | yes |  |  | Category, by built-in name or display name. |
| `category.subcategory` | string |  |  |  | Subcategory name. |
| `paint` | object |  |  |  | Paint faces of walls, floors, ceilings or roofs. "none" removes paint. |
| `paint.elements` | list<ref> | yes |  | at least 1 item | Host elements to paint. |
| `paint.faces` | list<enum(exterior\\|interior\\|top\\|bottom)> | yes |  | at least 1 item; each one of `exterior`, `interior`, `top`, `bottom` | Which faces: exterior and interior for walls, top and bottom for floors, ceilings and roofs. |
| `maxInstances` | integer |  |  | >= 0 | Type-wide and category targets (layer, type parameter, category): pass when the changed types have at most this many instances, or the category at most this many elements. Without it any instance or element needs confirmTypeWide. |
| `confirmTypeWide` | bool |  | `false` |  | Allow a type-wide or category-wide change whatever the number of instances. |

Rules across fields: exactly one of `layer`, `parameter`, `category`, `paint`; in `layer`: exactly one of `index`, `function`; in `layer`: `function` requires `side`; in `parameter`: exactly one of `elements`, `filter`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Layers, elements, types, categories or faces changed.
- `instancesAffected` (integer): Type-wide targets: instances of the changed types. Category: elements of the category.

Example (lint clean):

```json
{
  "jobId": "example-revit-material-assign",
  "title": "Example: material.assign",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
        "material": "Oak Veneer",
        "layer": {
          "type": "Interior - 138mm Partition",
          "kind": "wall",
          "function": "finish1",
          "side": "exterior"
        },
        "maxInstances": 20
      }
    }
  ]
}
```

## link.cad

Link a DWG or DXF file into a view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `path` | string | yes |  |  | DWG or DXF file. Must exist. |
| `view` | ref | yes |  |  | View to link into. With thisViewOnly the link shows only there. A view id, a reference to an op that created it, or the view's exact name. |
| `thisViewOnly` | bool |  | `true` |  | Show the link only in this view (not available in 3D views). |
| `placement` | enum(origin\\|centre\\|shared\\|point) |  | `"origin"` |  | Positioning: origin to origin, centred, by shared coordinates, or at point. |
| `point` | point2 |  |  | length | With placement point: where the file's origin goes. |
| `unit` | enum(auto\\|mm\\|cm\\|dm\\|m\\|in\\|ft\\|us_survey_ft\\|custom) |  | `"auto"` |  | Import units; auto reads the file's. |
| `scale` | number |  |  | > 0 | With unit custom: scale factor. |
| `colorMode` | enum(preserve\\|black_and_white\\|invert) |  | `"preserve"` |  | Layer colours. |
| `visibleLayersOnly` | bool |  | `true` |  | Link only layers visible in the file. |
| `correctLines` | bool |  | `true` |  | Correct lines that are slightly off axis. |
| `orientToView` | bool |  | `true` |  | Orient to the view. |
| `hideLayers` | list<string> |  |  |  | Layer names to hide in the view after linking. |
| `pin` | bool |  | `true` |  | Pin the link. |
| `allowDuplicate` | bool |  | `false` |  | Add another instance when this file is already linked into this view. |

Rules across fields: `point` requires `placement`; `scale` requires `unit`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `instance` (ref): Import instance.
- `linkType` (ref): CAD link type.
- `hiddenLayers` (list<string>): Layers hidden in the view.

Example (lint clean):

```json
{
  "jobId": "example-revit-link-cad",
  "title": "Example: link.cad",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "link.cad",
      "args": {
        "path": "C:\\Projects\\Site\\survey.dwg",
        "view": "Site",
        "unit": "m",
        "hideLayers": [
          "TEXT"
        ]
      }
    }
  ]
}
```

## link.revit

Link a Revit model, optionally acquiring shared coordinates from it. Publishing coordinates writes into the linked model and is not offered.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `path` | string | yes |  |  | RVT file. Must exist. |
| `placement` | enum(origin\\|shared\\|point) |  | `"origin"` |  | origin to origin, by shared coordinates, or at point. |
| `point` | point2 |  |  | length | With placement point: where the link's internal origin goes. |
| `elevation` | length |  | `0` | length | With placement point: height of the link's internal origin. |
| `rotation` | angle |  | `0` | degrees | With placement point: rotation about the link's origin, counter-clockwise. |
| `relative` | bool |  | `true` |  | Store the path relative to this model. |
| `worksets` | enum(all\\|none\\|last_viewed) |  | `"all"` |  | Workshared links: which worksets to open. |
| `coordinates` | enum(none\\|acquire) |  | `"none"` |  | After placing: acquire shared coordinates (and geolocation) from the link. Refused when the link is placed more than once, or when it would remove or replace this model's geographic coordinate system. |
| `pin` | bool |  | `true` |  | Pin the link. |
| `allowDuplicate` | bool |  | `false` |  | Add another instance when this file is already linked. |

Rules across fields: `point` requires `placement`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `instance` (ref): Link instance.
- `linkType` (ref): Link type.
- `typeCreated` (bool): A new link type was loaded.

Example (lint clean):

```json
{
  "jobId": "example-revit-link-revit",
  "title": "Example: link.revit",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "link.revit",
      "args": {
        "path": "C:\\Projects\\A\\structure.rvt",
        "placement": "shared",
        "worksets": "all"
      }
    }
  ]
}
```

## cleanup.purge

Flags: destructive.

Purge unused families, types, materials and other elements Revit reports as unused. List with dryRun, then delete the listed ids.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `categories` | list<string> |  |  |  | Only these categories (built-in or display names). Default: everything Revit reports as unused. |
| `dryRun` | bool |  | `false` |  | Delete nothing; list what is unused in wouldDelete. |
| `ids` | list<ref> |  |  |  | Ids to delete, taken from a dry run. Each must still be unused. |
| `expectCount` | integer |  |  | >= 0 | Must equal the number of ids. |
| `keepStandard` | bool |  | `true` |  | Skip elements the standard names as ones to keep. |

Rules across fields: `ids` requires `expectCount`; `expectCount` requires `ids`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `deleted` (list<ref>): Ids deleted.
- `wouldDelete` (list<ref>): With dryRun: unused ids, in order.
- `items` (list<object>): With dryRun: { id, name, category } per unused id.
- `remainingUnused` (integer): After deleting: how many elements are now unused (purging can free more).

Example (lint clean):

```json
{
  "jobId": "example-revit-cleanup-purge",
  "title": "Example: cleanup.purge",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "cleanup.purge",
      "args": {
        "ids": [
          "400101",
          "400102"
        ],
        "expectCount": 2
      }
    }
  ]
}
```

## export.nwc

Export the model to a Navisworks cache file. Needs the Navisworks exporter installed. Runs alone or with other exports only.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `fileName` | string | yes |  |  | File name pattern. Placeholders: {sheetNumber}, {sheetName}, {revision}, {view}, {viewType}, {drawing}, {date}, {jobId}. Example: "{drawing}.nwc". Names are checked against the standard's file naming rule when one is set. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `scope` | enum(model\\|view\\|elements) |  | `"model"` |  | Whole model, the elements visible in a view, or listed elements. |
| `view` | ref |  |  |  | With scope view: the 3D view. A view id, a reference to an op that created it, or the view's exact name. |
| `elements` | list<ref> |  |  |  | With scope elements: the elements. |
| `coordinates` | enum(shared\\|internal) |  | `"shared"` |  | Shared or internal coordinates. |
| `parameters` | enum(all\\|elements\\|none) |  | `"all"` |  | Which parameters to export. |
| `convertElementProperties` | bool |  | `false` |  | Convert element properties. |
| `exportLinks` | bool |  | `false` |  | Include linked models. |
| `exportParts` | bool |  | `false` |  | Export parts. |
| `divideByLevels` | bool |  | `true` |  | Divide the file into levels. |
| `exportRoomGeometry` | bool |  | `true` |  | Export room geometry. |
| `roomsAsAttribute` | bool |  | `true` |  | Export rooms as an attribute. |
| `elementIds` | bool |  | `true` |  | Export element ids. |
| `urls` | bool |  | `true` |  | Export URLs. |
| `findMissingMaterials` | bool |  | `true` |  | Try to find missing materials. |
| `convertLinkedCad` | bool |  | `true` |  | Convert linked CAD formats. |
| `convertLights` | bool |  | `false` |  | Convert lights. |
| `facetingFactor` | number |  | `1` | > 0 | Faceting factor. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Rules across fields: `view` requires `scope`; `elements` requires `scope`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `file` (string): Path written.

Example (lint clean):

```json
{
  "jobId": "example-revit-export-nwc",
  "title": "Example: export.nwc",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "export.nwc",
      "args": {
        "fileName": "{drawing} - coordination",
        "scope": "view",
        "view": "{3D} Coordination",
        "coordinates": "shared"
      }
    }
  ]
}
```

## data.export

Write chosen parameters of the elements of one or more categories, with id, level, room, location and (rooms and areas) boundary loops, to a CSV or JSON file. Changes nothing in the model.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `categories` | list<string> | yes |  | at least 1 item | Categories, by built-in name (OST_Doors) or display name (Doors). |
| `parameters` | list<string> |  |  |  | Parameters, one column each: display name, built-in name (ROOM_AREA) or shared parameter GUID. The instance parameter is read first, then the type's. |
| `include` | list<enum(id\\|uniqueId\\|category\\|family\\|type\\|level\\|room\\|location\\|boundary)> |  | `["id","category","family","type","level"]` | each one of `id`, `uniqueId`, `category`, `family`, `type`, `level`, `room`, `location`, `boundary` | Standard columns: id, uniqueId, category, family, type, level, room (number and name of the room the element is in), location (point or start and end, project coordinates), boundary (room and area loops). |
| `level` | string |  |  |  | Only elements on this level. |
| `view` | ref |  |  |  | Only elements visible in this view. A view id, a reference to an op that created it, or the view's exact name. |
| `where` | map |  |  |  | Parameter name to required value: text compares with the value as Revit shows it, numbers in job units. |
| `phase` | string |  |  |  | Phase used for the room column. Defaults to the last phase. |
| `boundaryLocation` | enum(finish\\|center\\|core_boundary\\|core_center) |  | `"finish"` |  | Which boundary the boundary column follows. |
| `values` | enum(number\\|display) |  | `"number"` |  | number: lengths in job units, areas in m2 or ft2, volumes in m3 or ft3, angles in degrees, element ids as names; display: the text Revit shows. |
| `format` | enum(csv\\|json) |  | `"csv"` |  | File format. json writes { "columns": [...], "rows": [ {column: value} ] }. |
| `delimiter` | enum(comma\\|semicolon\\|tab) |  | `"comma"` |  | CSV field separator. |
| `fileName` | string |  |  |  | File name. Placeholders: {drawing}, {date}, {jobId}. Defaults to "{drawing} - data"; .csv or .json is added when there is no extension. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `maxRows` | integer |  | `100000` | > 0 | Refuse when more elements than this match, so a wrong filter cannot write a huge file. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `file` (string): Path written.
- `rows` (integer): Elements written.
- `columns` (list<string>): Column names in order.

Example (lint clean):

```json
{
  "jobId": "example-revit-data-export",
  "title": "Example: data.export",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "data.export",
      "args": {
        "categories": [
          "Doors"
        ],
        "parameters": [
          "Fire Rating",
          "Mark"
        ],
        "include": [
          "id",
          "type",
          "level",
          "room"
        ],
        "level": "Level 1",
        "format": "csv",
        "fileName": "{drawing} - doors"
      }
    }
  ]
}
```

## schedule.export

Export a schedule as delimited text with Revit's own schedule export. Changes nothing in the model.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `schedule` | ref | yes |  |  | Schedule to export. A view id, a reference to an op that created it, or the view's exact name. |
| `fileName` | string |  |  |  | File name pattern. Placeholders: {view}, {drawing}, {date}, {jobId}. Example: "{view}.csv". Names are checked against the standard's file naming rule when one is set. |
| `outputDir` | string |  |  |  | Folder for the files. Defaults to the job's output folder. |
| `delimiter` | enum(comma\\|semicolon\\|tab) |  | `"comma"` |  | Field separator. Without an extension in fileName, comma and semicolon add .csv and tab adds .txt. |
| `textQualifier` | enum(double_quote\\|single_quote\\|none) |  | `"double_quote"` |  | Character put around text values. |
| `columnHeaders` | enum(none\\|one_row\\|multiple_rows) |  | `"one_row"` |  | none, one_row (the field headings) or multiple_rows (with grouped headings). |
| `groupHeadersFootersBlanks` | bool |  | `false` |  | Also export group headers, footers and blank lines. |
| `title` | bool |  | `false` |  | Also export the schedule title. |
| `overwrite` | bool |  | `false` |  | Replace files that already exist. When false an existing file fails the op. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `file` (string): Path written.
- `lines` (integer): Lines in the file, headings included.

Example (lint clean):

```json
{
  "jobId": "example-revit-schedule-export",
  "title": "Example: schedule.export",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "schedule.export",
      "args": {
        "schedule": "Door Schedule",
        "fileName": "{view}",
        "delimiter": "semicolon",
        "columnHeaders": "one_row"
      }
    }
  ]
}
```

## schedule.key.create

Flags: ensure.

Ensure a key schedule: the category's elements get a key parameter, each key (row) sets the listed fields at once.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | Key schedule name. |
| `category` | string | yes |  |  | Category the keys are for, e.g. Rooms, Doors (built-in or display name). |
| `keyParameter` | string |  |  |  | Name of the key parameter the category's elements get, e.g. "Room Style". Defaults to Revit's default for the category. |
| `fields` | list<string> | yes |  | at least 1 item | Parameters each key sets, in column order. The key name column comes first by itself. |
| `keys` | list<object> |  |  |  | Keys (rows), in order. |
| `keys[].name` | string | yes |  |  | Key name, unique in the schedule. |
| `keys[].values` | map |  |  |  | Field name to value (text as shown, numbers in job units). |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `schedule` (ref): Key schedule id.
- `keys` (map): Key name to key element id.

Example (lint clean):

```json
{
  "jobId": "example-revit-schedule-key-create",
  "title": "Example: schedule.key.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "schedule.key.create",
      "args": {
        "name": "Room Style Key",
        "category": "Rooms",
        "keyParameter": "Room Style",
        "fields": [
          "Floor Finish",
          "Wall Finish"
        ],
        "keys": [
          {
            "name": "Office",
            "values": {
              "Floor Finish": "Carpet",
              "Wall Finish": "Paint"
            }
          }
        ]
      }
    }
  ]
}
```

## area.plan.create

Create an area plan for an existing area scheme on a level.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `areaScheme` | string | yes |  |  | Existing area scheme, e.g. "Gross Building" or "Rentable". The Revit API cannot create area schemes. |
| `level` | string | yes |  |  | Level. |
| `name` | string |  |  |  | View name. Defaults to "<level> - <area scheme>". |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |
| `crop` | object |  |  |  | Crop the plan to this rectangle (project coordinates). Omitted leaves the crop off. |
| `crop.min` | point2 | yes |  | length | Lower-left corner. |
| `crop.max` | point2 | yes |  | length | Upper-right corner. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-area-plan-create",
  "title": "Example: area.plan.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "area.plan.create",
      "args": {
        "areaScheme": "Gross Building",
        "level": "Level 1",
        "scale": "1:100",
        "crop": {
          "min": [
            0,
            0
          ],
          "max": [
            30000,
            20000
          ]
        }
      }
    }
  ]
}
```

## area.place

Draw area boundary lines and place areas (with tags) in an area plan.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Area plan. A view id, a reference to an op that created it, or the view's exact name. |
| `boundaries` | list<list<point2>> |  |  |  | Closed boundary loops, each a list of points (closed automatically). |
| `lines` | list<object> |  |  |  | Single boundary lines. |
| `lines[].start` | point2 | yes |  | length | Start. |
| `lines[].end` | point2 | yes |  | length | End. |
| `areas` | list<object> |  |  |  | Areas to place. |
| `areas[].point` | point2 | yes |  | length | A point inside the region the boundaries enclose. |
| `areas[].name` | string |  |  |  | Area name. |
| `areas[].number` | string |  |  |  | Area number. |
| `areas[].parameters` | map |  |  |  | Other parameter values. |
| `allowNotEnclosed` | bool |  | `false` |  | Keep an area that comes out not enclosed (area 0), with a warning. Otherwise the op fails. |
| `tag` | bool |  | `true` |  | Tag each new area in the view. |
| `tagType` | string |  |  | matches `.+\S\s:\s\S.+` | Area tag type. Defaults to the project's default area tag. Written "Family : Type". |

Rules across fields: at least one of `boundaries`, `lines`, `areas`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `lines` (list<ref>): Boundary line ids.
- `areas` (list<ref>): Area ids, in the order given.
- `values` (list<number>): Areas read back (m2 or ft2), in the order given; 0 means not enclosed.

Example (lint clean):

```json
{
  "jobId": "example-revit-area-place",
  "title": "Example: area.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "area.place",
      "args": {
        "view": "Area Plan - Level 1",
        "boundaries": [
          [
            [
              0,
              0
            ],
            [
              20000,
              0
            ],
            [
              20000,
              15000
            ],
            [
              0,
              15000
            ]
          ]
        ],
        "areas": [
          {
            "point": [
              10000,
              7500
            ],
            "name": "GBA",
            "number": "A1"
          }
        ]
      }
    }
  ]
}
```

## view.drafting.create

Create a drafting view, or a legend by duplicating an existing one.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `name` | string | yes |  |  | View name. |
| `viewFamilyType` | string |  |  |  | Drafting view type. Defaults to the first one. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |
| `legendFrom` | ref |  |  |  | Make a legend instead, by duplicating this legend view (the Revit API cannot create a legend from nothing). A view id, a reference to an op that created it, or the view's exact name. |
| `copyContent` | bool |  | `false` |  | With legendFrom: also copy its components and annotation. |

Rules across fields: atMostOne `legendFrom`, `viewFamilyType`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-drafting-create",
  "title": "Example: view.drafting.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.drafting.create",
      "args": {
        "name": "Typical Wall Details",
        "scale": "1:5"
      }
    }
  ]
}
```

## detail.draw

Draw detail lines, arcs, circles, filled and masking regions and detail components in a 2D-drawable view. Points use the view's frame (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]).

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Plan, section, elevation, detail, drafting view or legend. A view id, a reference to an op that created it, or the view's exact name. |
| `lineStyle` | string |  |  |  | Line style (a Lines subcategory, e.g. "Thin Lines") for lines, arcs and circles that give none. |
| `lines` | list<object> |  |  |  | Straight lines. |
| `lines[].start` | point2 | yes |  | length | Start. |
| `lines[].end` | point2 | yes |  | length | End. |
| `lines[].lineStyle` | string |  |  |  | Line style. |
| `polylines` | list<object> |  |  |  | Chains of straight lines. |
| `polylines[].points` | list<point2> | yes |  | at least 2 items; length | Vertices. |
| `polylines[].closed` | bool |  | `false` |  | Join the last point to the first. |
| `polylines[].lineStyle` | string |  |  |  | Line style. |
| `arcs` | list<object> |  |  |  | Arcs, counter-clockwise from startAngle to endAngle (degrees from the frame's x axis). |
| `arcs[].centre` | point2 | yes |  | length | Centre. |
| `arcs[].radius` | length | yes |  | > 0; length | Radius. |
| `arcs[].startAngle` | angle | yes |  | degrees | Start angle. |
| `arcs[].endAngle` | angle | yes |  | degrees | End angle. |
| `arcs[].lineStyle` | string |  |  |  | Line style. |
| `circles` | list<object> |  |  |  | Full circles. |
| `circles[].centre` | point2 | yes |  | length | Centre. |
| `circles[].radius` | length | yes |  | > 0; length | Radius. |
| `circles[].lineStyle` | string |  |  |  | Line style. |
| `regions` | list<object> |  |  |  | Filled regions. |
| `regions[].boundary` | list<point2> | yes |  | at least 3 items; length | Outer loop. |
| `regions[].holes` | list<list<point2>> |  |  |  | Inner loops. |
| `regions[].type` | string |  |  |  | Filled region type. Defaults to the project's default. |
| `regions[].lineStyle` | string |  |  |  | Boundary line style. |
| `masking` | list<object> |  |  |  | Masking regions. |
| `masking[].boundary` | list<point2> | yes |  | at least 3 items; length | Outer loop. |
| `masking[].holes` | list<list<point2>> |  |  |  | Inner loops. |
| `masking[].lineStyle` | string |  |  |  | Boundary line style. |
| `components` | list<object> |  |  |  | Detail components. |
| `components[].type` | string | yes |  |  | Detail item type, "Family : Type" or a unique type name. |
| `components[].point` | point2 | yes |  | length | Insertion point. |
| `components[].rotation` | angle |  | `0` | degrees | Rotation, counter-clockwise. |

Rules across fields: at least one of `lines`, `polylines`, `arcs`, `circles`, `regions`, `masking`, `components`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `elements` (list<ref>): Ids of everything drawn, in the order lines, polylines (one per segment), arcs, circles, regions, masking, components.

Example (lint clean):

```json
{
  "jobId": "example-revit-detail-draw",
  "title": "Example: detail.draw",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "detail.draw",
      "args": {
        "view": "Typical Wall Details",
        "lines": [
          {
            "start": [
              0,
              0
            ],
            "end": [
              1000,
              0
            ]
          }
        ],
        "circles": [
          {
            "centre": [
              500,
              500
            ],
            "radius": 100
          }
        ],
        "regions": [
          {
            "boundary": [
              [
                0,
                0
              ],
              [
                200,
                0
              ],
              [
                200,
                200
              ]
            ],
            "type": "Solid Black"
          }
        ]
      }
    }
  ]
}
```

## view.crop.set

Set a view's crop region to a rectangle, a polygon or a room.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Plan, area plan, section, elevation, detail or callout view. A view id, a reference to an op that created it, or the view's exact name. |
| `rect` | object |  |  |  | Crop rectangle in the view's frame (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]). |
| `rect.min` | point2 | yes |  | length | Lower-left corner. |
| `rect.max` | point2 | yes |  | length | Upper-right corner. |
| `boundary` | list<point2> |  |  | at least 3 items; length | Crop polygon in plan (project coordinates); plans only. |
| `room` | ref |  |  |  | Room: plans crop to its boundary, sections and elevations to its extent across the view and its height. |
| `offset` | length |  | `0` | >= 0; length | Grow the crop outward by this distance on every side. |
| `visible` | bool |  | `false` |  | Show the crop frame. |
| `annotationCrop` | bool |  |  |  | Turn the annotation crop on or off. Left as it is when omitted. |

Rules across fields: exactly one of `rect`, `boundary`, `room`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): View id.
- `extent` (object): Crop extent read back, in the view's frame.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-crop-set",
  "title": "Example: view.crop.set",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.crop.set",
      "args": {
        "view": "Level 1",
        "rect": {
          "min": [
            0,
            0
          ],
          "max": [
            30000,
            20000
          ]
        },
        "offset": 500
      }
    }
  ]
}
```

## view.callout.create

Create a callout of a plan, section, elevation or detail view.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `parentView` | ref | yes |  |  | View the callout is drawn in: floor, ceiling or structural plan, section, elevation or detail view. A view id, a reference to an op that created it, or the view's exact name. |
| `rect` | object | yes |  |  | Callout extent in the parent view's frame (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]). |
| `rect.min` | point2 | yes |  | length | Lower-left corner. |
| `rect.max` | point2 | yes |  | length | Upper-right corner. |
| `name` | string | yes |  |  | Callout view name. |
| `viewFamilyType` | string |  |  |  | View type: a Detail type (the default) or a type of the parent's own family. |
| `template` | string |  |  |  | View template to assign. |
| `scale` | string |  |  | matches `\s*(\d+(\.\d+)?\s*:\s*\d+(\.\d+)?\\|.+=.+)\s*` | Drawing scale such as "1:100" or "1/4\" = 1'-0\"". |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `view` (ref): Id of the new view.
- `name` (string): Name read back.

Example (lint clean):

```json
{
  "jobId": "example-revit-view-callout-create",
  "title": "Example: view.callout.create",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "view.callout.create",
      "args": {
        "parentView": "Level 1",
        "rect": {
          "min": [
            1000,
            1000
          ],
          "max": [
            5000,
            4000
          ]
        },
        "name": "Toilet Core Callout",
        "scale": "1:20"
      }
    }
  ]
}
```

## colorfill.apply

Flags: ensure.

Ensure a colour fill scheme (by parameter, with chosen colours) and apply it to a view, with an optional legend.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Floor plan, area plan or section. A view id, a reference to an op that created it, or the view's exact name. |
| `category` | string |  | `"Rooms"` |  | Category coloured: Rooms, Areas, Spaces (built-in or display name). |
| `scheme` | string | yes |  |  | Scheme name. Created by copying basedOn when missing. |
| `basedOn` | string |  |  |  | Scheme to copy when creating. Defaults to the category's first scheme (for areas, one of the view's area scheme). |
| `parameter` | string |  |  |  | Parameter the scheme colours by, e.g. "Department". Needed when the scheme is created; changing it on an existing scheme clears its entries. |
| `title` | string |  |  |  | Legend title. |
| `colors` | map<string> |  |  |  | Parameter value as the legend shows it to colour: "r,g,b" or "#RRGGBB". |
| `legendAt` | point2 |  |  | length | Place a colour fill legend with its top-left corner here (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]). Omitted: no legend. |
| `update` | bool |  | `false` |  | Change an existing object to match. When false (default) an existing object is left alone and reported as unchanged. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `scheme` (ref): Scheme id.
- `legend` (ref): Legend id, or null.

Example (lint clean):

```json
{
  "jobId": "example-revit-colorfill-apply",
  "title": "Example: colorfill.apply",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "colorfill.apply",
      "args": {
        "view": "Level 1",
        "scheme": "Department",
        "parameter": "Department",
        "colors": {
          "Office": "#88CCEE",
          "Lab": "200,120,90"
        },
        "legendAt": [
          32000,
          20000
        ]
      }
    }
  ]
}
```

## tag.place

Place tags on chosen elements in a view: category tags, room and area tags, or material tags.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | View to tag in. A view id, a reference to an op that created it, or the view's exact name. |
| `tags` | list<object> | yes |  | at least 1 item | Tags to place. |
| `tags[].element` | ref | yes |  |  | Element to tag. |
| `tags[].point` | point2 |  |  | length | Tag head position in the view's frame (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]). |
| `tags[].offset` | point2 |  |  | length | Instead of point: head offset from the element's location, in paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted, scaled by the view scale. |
| `tags[].tagType` | string |  |  |  | Tag type ("Family : Type"). Defaults to tagType, then the category's default tag. |
| `tags[].leader` | bool |  |  |  | Draw a leader from the element to the head. Defaults to leader. |
| `tags[].orientation` | enum(horizontal\\|vertical) |  |  |  | Tag orientation. Defaults to orientation. |
| `tags[].material` | bool |  | `false` |  | Tag the material of the element's first face instead of the element. |
| `tagType` | string |  |  | matches `.+\S\s:\s\S.+` | Default tag type. Written "Family : Type". |
| `offset` | point2 |  | `[0,0]` | length | Default head offset for tags without point or offset (paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted). |
| `leader` | bool |  | `false` |  | Default for leader. |
| `orientation` | enum(horizontal\\|vertical) |  | `"horizontal"` |  | Default orientation. |
| `allowSecondTag` | bool |  | `false` |  | Tag an element that already has a tag of the same category in the view. Otherwise that is an error. |

Rules across fields: in each `tags[]`: atMostOne `point`, `offset`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `tags` (list<ref>): Tag ids, in the order given.

Example (lint clean):

```json
{
  "jobId": "example-revit-tag-place",
  "title": "Example: tag.place",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "tag.place",
      "args": {
        "view": "Level 1",
        "tags": [
          {
            "element": "316001",
            "offset": [
              10,
              10
            ],
            "leader": true
          },
          {
            "element": "316002",
            "point": [
              5000,
              3000
            ]
          }
        ]
      }
    }
  ]
}
```

## revision.cloud

Draw a revision cloud for a revision in a view or on a sheet.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | View or sheet (sheet:<number> works too). Not 3D views or schedules. A view id, a reference to an op that created it, or the view's exact name. |
| `revision` | ref | yes |  |  | Revision: id, reference, or its revision number or description (must match one revision). |
| `rect` | object |  |  |  | Cloud around this rectangle: the view's frame (plans: project coordinates [x, y]; sections, elevations and detail views: [distance along the view's right direction from the view origin, elevation above the project base point]; drafting views and legends: the view's own [x, y]), or sheet coordinates in paper units: millimetres on the printed sheet for a metric job, inches for an imperial one; a value written with a unit is converted on a sheet. |
| `rect.min` | point2 | yes |  | length | Lower-left corner. |
| `rect.max` | point2 | yes |  | length | Upper-right corner. |
| `boundary` | list<point2> |  |  | at least 3 items; length | Cloud along this closed polygon, same coordinates as rect. |
| `comments` | string |  |  |  | Comments parameter of the cloud. |
| `mark` | string |  |  |  | Mark parameter of the cloud. |

Rules across fields: exactly one of `rect`, `boundary`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `cloud` (ref): Revision cloud id.

Example (lint clean):

```json
{
  "jobId": "example-revit-revision-cloud",
  "title": "Example: revision.cloud",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "revision.cloud",
      "args": {
        "view": "Level 1",
        "revision": "Rev 1",
        "rect": {
          "min": [
            1000,
            1000
          ],
          "max": [
            4000,
            3000
          ]
        },
        "comments": "Door moved"
      }
    }
  ]
}
```

## layout.clearances

Check clearance zones (circles, rectangles, polygons, door swings, route strips, clear space at doors) against elements in a plan and report every intrusion with its depth. Changes nothing.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Plan view: elements visible in it are tested, by their plan footprint. A view id, a reference to an op that created it, or the view's exact name. |
| `zones` | list<object> | yes |  | at least 1 item | Clearance zones, project coordinates. |
| `zones[].id` | string | yes |  |  | Label reported with each intrusion. |
| `zones[].kind` | enum(circle\\|rect\\|polygon\\|doorSwing\\|route\\|doorClearance) | yes |  |  | circle: centre and radius. rect: min and max. polygon: convex points. doorSwing: hinge, leafWidth, closedAngle, swing and side (the leaf's sweep). route: points and width (a strip centred on the points). doorClearance: door, depth, width and faces (clear floor space in front of a door). |
| `zones[].centre` | point2 |  |  | length | circle: centre. |
| `zones[].radius` | length |  |  | > 0; length | circle: radius, e.g. 750 for a 1500 turning circle. |
| `zones[].min` | point2 |  |  | length | rect: lower-left corner. |
| `zones[].max` | point2 |  |  | length | rect: upper-right corner. |
| `zones[].points` | list<point2> |  |  | at least 2 items; length | polygon: convex outline (3 or more points); route: centreline (2 or more points). |
| `zones[].width` | length |  |  | > 0; length | route: clear width. doorClearance: width of the clear space (defaults to the door's width). |
| `zones[].hinge` | point2 |  |  | length | doorSwing: hinge point. |
| `zones[].leafWidth` | length |  |  | > 0; length | doorSwing: leaf width (the swing radius). |
| `zones[].closedAngle` | angle |  |  | degrees | doorSwing: direction from the hinge along the closed leaf. |
| `zones[].swing` | angle |  | `90` | >= 1; <= 180; degrees | doorSwing: opening angle. |
| `zones[].side` | enum(left\\|right) |  | `"left"` |  | doorSwing: the leaf opens counter-clockwise (left) or clockwise (right) from closedAngle. |
| `zones[].door` | ref |  |  |  | doorClearance: the door. |
| `zones[].depth` | length |  |  | > 0; length | doorClearance: depth of the clear space from the wall face. |
| `zones[].faces` | enum(facing\\|back\\|both) |  | `"both"` |  | doorClearance: the side the door faces, the other side, or both. |
| `zones[].ignore` | list<ref> |  |  |  | Elements never counted against this zone. A doorClearance's door and its host wall are always ignored for it. |
| `against` | list<string> | yes |  | at least 1 item | Categories tested, e.g. ["Furniture", "Casework", "Walls", "Columns"]. |
| `ignore` | list<ref> |  |  |  | Elements never counted against any zone. |
| `minHeight` | length |  |  | length | Ignore elements whose top is below this height above the view's level. |
| `maxHeight` | length |  |  | length | Ignore elements whose bottom is above this height above the view's level. |
| `tolerance` | length |  |  | >= 0; length | Intrusions this deep or less are ignored (touching). Defaults to 0.5 mm. |
| `failOnIntrusion` | bool |  | `false` |  | Fail the op, and so roll back the job, when any zone is intruded. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Intrusions found.
- `clear` (bool): True when no zone is intruded.
- `intrusions` (list<object>): { zone, element, category, name, depth, point } per element in a zone, deepest first; depth is how far the element must move to clear the zone, point a vertex of the overlap.
- `zones` (list<object>): { id, kind, intrusions, maxDepth, outline } per zone; outline is the zone polygon tested (circles as their centre and radius).

Example (lint clean):

```json
{
  "jobId": "example-revit-layout-clearances",
  "title": "Example: layout.clearances",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
        "view": "Level 1",
        "against": [
          "Furniture",
          "Casework",
          "Walls"
        ],
        "zones": [
          {
            "id": "WC turning",
            "kind": "circle",
            "centre": [
              2000,
              1500
            ],
            "radius": 750
          },
          {
            "id": "D1 swing",
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
              "316050"
            ]
          },
          {
            "id": "D1 clear",
            "kind": "doorClearance",
            "door": "316050",
            "depth": 1200,
            "faces": "facing"
          },
          {
            "id": "Corridor",
            "kind": "route",
            "points": [
              [
                0,
                0
              ],
              [
                10000,
                0
              ],
              [
                10000,
                5000
              ]
            ],
            "width": 1200
          }
        ]
      }
    }
  ]
}
```

## dim.string

Add a string of aligned dimensions in a plan across the walls and grids (and, along a wall, the doors and windows) that a line crosses.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `view` | ref | yes |  |  | Plan view. A view id, a reference to an op that created it, or the view's exact name. |
| `start` | point2 | yes |  | length | Start of the line the string follows. |
| `end` | point2 | yes |  | length | End of that line. |
| `offset` | length | yes |  | length | Distance from the start-end line to the dimension line, perpendicular to the string; positive to the left of the string direction. |
| `orientation` | enum(aligned\\|horizontal\\|vertical) |  | `"aligned"` |  | aligned measures along start-end; horizontal and vertical along the view's right and up directions. |
| `pick` | list<enum(walls\\|grids\\|doors\\|windows)> |  | `["walls","grids"]` | each one of `walls`, `grids`, `doors`, `windows` | What is dimensioned: walls and grids the line crosses, doors and windows whose location lies on the line within their host wall's width. |
| `wallFaces` | enum(both\\|exterior\\|interior) |  | `"both"` |  | Wall faces used: both side faces, or only the exterior or interior one. |
| `openings` | enum(centre\\|width) |  | `"centre"` |  | Door and window references: their centre, or the two sides of their width. |
| `overall` | bool |  | `false` |  | Also add an overall dimension from the first to the last reference. |
| `overallOffset` | length |  |  | length | Offset of the overall dimension. Defaults to offset plus 8 mm on paper (times the view scale) further out. |
| `dimStyle` | string |  |  |  | Linear dimension type. Defaults to the project's default. |

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `measurements` (list<length>): Segment values of the string read back, in order (job units).
- `dimensions` (list<ref>): Dimension ids: the string, then the overall dimension.

Example (lint clean):

```json
{
  "jobId": "example-revit-dim-string",
  "title": "Example: dim.string",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
        "view": "Level 1",
        "start": [
          0,
          5000
        ],
        "end": [
          20000,
          5000
        ],
        "offset": 1500,
        "pick": [
          "walls",
          "grids"
        ],
        "wallFaces": "both",
        "overall": true
      }
    }
  ]
}
```

## text.replace

Find and replace text in text notes and chosen text parameters, keeping text note formatting. dryRun only counts.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `find` | string | yes |  |  | Text to find, literally. |
| `replace` | string |  |  |  | Replacement; "" deletes the match. Required unless dryRun. |
| `matchCase` | bool |  | `false` |  | Case-sensitive match. |
| `wholeWord` | bool |  | `false` |  | Match whole words only (letters, digits and _ form words). |
| `textNotes` | bool |  | `true` |  | Search text notes. |
| `views` | list<ref> |  |  |  | Only text notes in these views or sheets. Defaults to every view. |
| `parameters` | list<string> |  |  |  | Text parameters to search too (display name, built-in name or GUID), on elements of categories. |
| `categories` | list<string> |  |  |  | Categories whose elements' parameters are searched. |
| `includeTypes` | bool |  | `false` |  | Also search those parameters on the elements' types. |
| `dryRun` | bool |  | `false` |  | Count and list the changes without making them. |
| `expectCount` | integer |  |  | >= 0 | When given, the op fails unless exactly this many elements would change. |

Rules across fields: `parameters` requires `categories`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `count` (integer): Elements changed (or that would change in a dry run).
- `matches` (integer): Occurrences replaced (or found).
- `changes` (list<object>): { id, kind, field, before, after } per changed text, up to 500; kind is textNote or parameter.
- `skippedGroups` (integer): Elements with matches inside groups, left unchanged.

Example (lint clean):

```json
{
  "jobId": "example-revit-text-replace",
  "title": "Example: text.replace",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
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
        "find": "PRELIMINARY",
        "replace": "FOR CONSTRUCTION",
        "matchCase": true,
        "wholeWord": true,
        "parameters": [
          "Comments"
        ],
        "categories": [
          "Doors"
        ],
        "dryRun": true
      }
    }
  ]
}
```

## family.edit

Flags: alone in its job.

Edit a loaded family: add family parameters, set or clear formulas, create types and set type values, then load it back into the project (existing types take the new values) and read every value back from the project. Must be alone in its job; verify checks and captures are allowed. A rehearsal makes every change in a copy of the family and reads it back there, but does not load it into the project. In-place families, system families (walls, floors...) and families Revit marks as not editable are refused; nested families are edited through their own loaded family only when they are shared. Types are never renamed or deleted.

| Field | Type | Required | Default | Limits | Description |
|---|---|---|---|---|---|
| `family` | string | yes |  |  | Name of the loaded family, or its element id. |
| `category` | string |  |  |  | Category of the family (built-in name such as OST_Doors, or display name), to tell apart families with the same name. |
| `addParameters` | list<object> |  |  |  | Family parameters to add. A parameter that already exists with the same kind and instance setting is left as it is; one that differs is refused. |
| `addParameters[].name` | string | yes |  |  | Parameter name. |
| `addParameters[].spec` | enum(length\\|area\\|volume\\|angle\\|slope\\|number\\|integer\\|currency\\|text\\|url\\|yesNo\\|material) | yes |  |  | Kind of value. |
| `addParameters[].group` | enum(data\\|dimensions\\|identityData\\|constraints\\|materials\\|text\\|graphics\\|general\\|construction\\|visibility\\|phasing) |  | `"data"` |  | Group in the properties palette. |
| `addParameters[].instance` | bool |  | `false` |  | Instance parameter (each placed element has its own value) rather than type parameter. |
| `formulas` | map<string> |  |  |  | Family parameter name to formula, in Revit's formula syntax (for example "Width / 2"). Null or "" removes the formula. |
| `values` | map |  |  |  | Values set in every type of the family (a type's own values win). Family parameter name (display name, built-in parameter name or shared parameter GUID) to value. Lengths in job units, areas and volumes in m2/m3 (ft2/ft3 for an imperial job), angles in degrees, yes/no as true/false, materials by the name of a material in the family, text as text. |
| `types` | list<object> |  |  |  | Types to change or create. |
| `types[].name` | string | yes |  |  | Type name. |
| `types[].create` | bool |  | `false` |  | Create the type when the family has none of that name. When false a missing type is an error. |
| `types[].copyFrom` | string |  |  |  | Existing type whose values a new type starts from. Defaults to the family's first type. |
| `types[].values` | map |  |  |  | Family parameter name (display name, built-in parameter name or shared parameter GUID) to value. Lengths in job units, areas and volumes in m2/m3 (ft2/ft3 for an imperial job), angles in degrees, yes/no as true/false, materials by the name of a material in the family, text as text. |

Rules across fields: at least one of `addParameters`, `formulas`, `values`, `types`.

Outputs (read with `$op1.<name>` from a later op, and in the report's `results[].outputs`):

- `family` (ref): Family id.
- `types` (map): Type name to the id of the project type, for every type of the family after the load.

Example (lint clean):

```json
{
  "jobId": "example-revit-family-edit",
  "title": "Example: family.edit",
  "app": "revit",
  "document": {
    "path": "C:/Projects/2417/2417-ARCH.rvt",
    "fingerprint": "copy-from-snapshot"
  },
  "units": {
    "length": "mm"
  },
  "ops": [
    {
      "id": "op1",
      "op": "family.edit",
      "args": {
        "family": "Single-Flush",
        "category": "OST_Doors",
        "addParameters": [
          {
            "name": "Leaf Clearance",
            "spec": "length",
            "group": "dimensions"
          }
        ],
        "formulas": {
          "Leaf Clearance": "Width - 100 mm"
        },
        "values": {
          "Fire Rating": "FD30"
        },
        "types": [
          {
            "name": "915 x 2134mm",
            "values": {
              "Width": 915,
              "Height": 2134
            }
          },
          {
            "name": "1010 x 2134mm",
            "create": true,
            "copyFrom": "915 x 2134mm",
            "values": {
              "Width": 1010
            }
          }
        ]
      }
    }
  ]
}
```
