# Facts contract for `autocad.json` and `revit.json`

This is the contract between the checks in this folder and the engines that collect facts. It lists every facts
table the checks read, every field with its type and meaning, how to obtain it in the application, and every
evaluator the checks name that SA.Core does not have yet.

AutoCAD: 9 tables (`document`, `layers`, `entities`, `blocks`, `xrefs`, `styles`, `layouts`, `annotations`,
`jobPlacement`).
Revit: 8 tables (`document`, `warnings`, `views`, `elements`, `types`, `links`, `annotations`, `jobPlacement`).
Layout evaluators: 2 (`extents_overlap`, `sheet_scales_uniform`, section 4), used by both apps.
New evaluators: 9 (`conditional_field_equals`, `field_within`, `distinct_count_min`, `scale_in_standard`,
`units_match_standard`, `dim_precision_min`, `matches_standard_entry`, `group_count_over_threshold`,
`manual_review`). AutoCAD uses all of them except `group_count_over_threshold`; Revit uses
`conditional_field_equals`, `dim_precision_min`, `group_count_over_threshold` and `manual_review`.

## 0. Rules for every table

- A table is a list of records `{ "id": <optional string>, "fields": { ... } }` (SA.Core `Facts`). Collect every table
  you can, even when it is empty. A table you cannot collect must be left out, so its checks report
  `not_implemented`.
- `id` is the AutoCAD handle (hex string) or the Revit element id (integer written as a string). Fix templates use it
  as `{{recordId}}`.
- Field types: `string`, `number` (JSON number), `int` (JSON number with no fraction), `bool` (JSON true/false),
  `string[]` (JSON array of strings).
- **Absent, not null.** When a field does not apply to a record (a text height on a line), leave it out. The neutral
  evaluators treat an absent field as "never equals" and `duplicates_by_key` skips records with an absent key.
  Write `null` only where this document says so.
- Enumerated strings are lower case exactly as written here (`"bylayer"`, `"model"`, `"fine"`). Names (layers,
  styles, views, types, worksets) are written exactly as stored in the document.
- `where` in a check compares with JSON equality, so a bool must be a JSON bool and an int must not be written as a
  string. `whereNot` is the opposite: every listed field must differ from its value, and an absent field passes.
- **Collect what the checks read.** `CheckInputs.Needs(checks)` (SA.Core) lists the tables and fields a set of checks
  reads. A collector may leave out other tables and fields, never records of a needed table: checks count records.
- Lengths are in the unit the field name says (`...Mm` in millimetres). Fields without a unit in their name are in
  drawing units (AutoCAD) or are counts or ratios.
- **Facts meta.** Both engines set `meta["units.length"]` to `mm`, `cm`, `m`, `in` or `ft` (the
  `text_height_standard` evaluator reads it). AutoCAD: from `INSUNITS`; when `INSUNITS` is 0 (unitless) use `in` if
  `MEASUREMENT` is 0 and `mm` if it is 1, and set `meta["units.assumed"] = true`; for any other unit (yards, miles,
  survey feet...) leave `units.length` out. Revit: the display unit of the project's Length format, mapped to the
  nearest of the five (feet-and-inches -> `ft`, inches -> `in`, millimetres -> `mm`, centimetres -> `cm`, metres -> `m`).
- **Cut tables.** When a table or a field is not collected in full (a record limit, a call limit, values that could
  not be read), say so in the meta: `truncated` / `omittedFields` (SA.Core `Facts.MarkTruncated`, `Facts.MarkOmitted`)
  or the flat `"<table>.truncated": true` with `"<table>.total"`. Checks that read the cut part then report
  `incomplete`, never pass (`data/FORMATS.md` section 2, "Cut facts").
- **Job scope.** When facts are collected to verify a job, both engines set `meta["jobScope"] = true` and mark the
  records of elements the job created or modified with `byJob: true` (tables `annotations`; `jobPlacement` holds
  only such elements). `extents_overlap` then reports only pairs with at least one such record. In an audit the meta
  member is absent.
- Entries marked † use an API member that was not opened in the research (see research/05 "Could not verify");
  confirm it while implementing and say so in the engine's report if it differs.

---

## 1. AutoCAD tables

Collect from the open document's `Database`. "Model and paper space" means the `*Model_Space` block and every
`*Paper_Space*` layout block, not block definitions and not entities nested inside inserts.

### 1.1 `document`: exactly one record, no `id`

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `insUnits` | string | `Database.Insunits` mapped: Undefined `unitless`, Inches `in`, Feet `ft`, Millimeters `mm`, Centimeters `cm`, Meters `m`; any other value as its enum name in lower case (`yards`, `usSurveyFeet`). |
| `measurement` | int | `Database.Measurement` as 0 (imperial) or 1 (metric). |
| `ltScale` | number | `Database.Ltscale`. |
| `psLtScale` | int | `Database.Psltscale` as 1 or 0. |
| `msLtScale` | int | `Database.MsLtScale` as 1 or 0. |
| `tileMode` | int | 1 when model space is the current tab (`Database.TileMode` true), else 0. Read the state at collection time; for a file opened from disk this is the saved state. |
| `currentLayout` | string | Read from the database, so it does not depend on the active document: `Model` when `Database.TileMode` is true, else the `LayoutName` of the layout of the `*Paper_Space` block (`BlockTableRecord.LayoutId`); `""` when there is none. |
| `imageFrame` | int | Value of `IMAGEFRAME`: 0 frames off, 1 shown and plotted, 2 shown not plotted. For the active document `Application.GetSystemVariable("IMAGEFRAME")`; for a side database read `RasterVariables` from the named object dictionary `ACAD_IMAGE_VARS` (absent dictionary = 1, the default)†. |
| `hasRasterImages` | bool | true when model or paper space holds at least one `RasterImage` entity. |
| `purgeableCount` | int | Number of named objects one purge pass would remove: collect the ids of every record of the block table (excluding layout blocks and xref blocks), layer, linetype, text style, dimension style and registered application tables and of the `ACAD_MLINESTYLE`, `ACAD_TABLESTYLE`, `ACAD_MLEADERSTYLE`, `ACAD_MATERIAL` and `ACAD_VISUALSTYLE` dictionaries (PURGE removes multileader styles, materials and visual styles too); leave out xref-dependent records and records of unresolved xrefs; remove the objects PURGE never offers (layer `0`, `Defpoints`, the current layer; linetypes `ByLayer`, `ByBlock`, `Continuous`; text style `Standard`; dimension style `Standard`; the current text, dimension, multiline and table styles; regapp `ACAD`; multiline style `Standard`; table style `Standard`; multileader style `Standard`; materials `ByLayer`, `ByBlock` and `Global`; the visual styles AutoCAD defines itself and those used by a viewport or the current view†); call `Database.Purge(ids)` once and count what is left, leaving out names the run's standard relies on (`ProjectStandard.NamesToKeep`, the names `fix.purge` never removes). One pass only: objects that become purgeable after another is purged are not counted. |
| `purgeableByKind` | object | The same count split by kind: `{ "blocks": n, "layers": n, "linetypes": n, "textstyles": n, "dimstyles": n, "mlinestyles": n, "tablestyles": n, "mleaderstyles": n, "materials": n, "visualstyles": n, "regapps": n }` (keys match the `fix.purge` kinds; `mleaderstyles`, `materials` and `visualstyles` are not `fix.purge` kinds yet). |

### 1.2 `layers`: one record per layer table record (including xref-dependent layers); `id` = layer handle

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `LayerTableRecord.Name`. |
| `colorKey` | string | `LayerTableRecord.Color`: ACI colour as its number (`"7"`); a true colour or colour-book colour as `"r,g,b"` with no spaces. |
| `colorIndex` | int | ACI number; absent for true and book colours. |
| `linetype` | string | Name of the `LinetypeTableRecord` of `LinetypeObjectId`. |
| `lineweight` | number or string | `LineWeight` in millimetres (`LineWeight030` -> 0.3; enum value / 100); `ByLineWeightDefault` -> `"default"`. |
| `plottable` | bool | `IsPlottable`, except `Defpoints`, which is always false because AutoCAD never plots it. |
| `isFrozen`, `isOff`, `isLocked` | bool | `IsFrozen`, `IsOff`, `IsLocked`. |
| `isXrefDependent` | bool | `IsDependent` (name contains `|`). |
| `entityCount` | int | Entities on this layer in model space, paper space and all non-xref block definitions (count while scanning; do not rely on `IsUsed`). |
| `isUsed` | bool | `entityCount > 0`. |
| `description` | string | `Description`; absent when empty. |

### 1.3 `entities`: one record per entity in model and paper space; `id` = entity handle

Leave out: each layout's overall paper-space viewport, attributes, entities inside block
definitions and inside xref databases. `Viewport.Number` is 1 for the overall viewport only while its layout is
current; on other layouts it gives no number. So the overall viewport is: the viewport numbered 1; else the first id
of `Layout.GetViewports()`; else the first viewport in the layout block's entity order.

Fields for every entity:

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `type` | string | DXF class name, upper case: `entity.GetRXClass().DxfName` (`LINE`, `LWPOLYLINE`, `POLYLINE`, `ARC`, `CIRCLE`, `SPLINE`, `TEXT`, `MTEXT`, `DIMENSION` for every dimension subclass, `HATCH`, `INSERT`, `VIEWPORT`, `IMAGE`, `MULTILEADER`...). |
| `space` | string | `model` or `paper`. |
| `layout` | string | `Model` or the layout name. |
| `layer` | string | `Entity.Layer`, as stored. |
| `color` | string | `Entity.Color`: `bylayer`, `byblock`, the ACI number as text (`"1"`), or `"r,g,b"` for true and book colours. |
| `linetype` | string | `bylayer` or `byblock` (compare `Entity.Linetype` without case), else the linetype name. |
| `lineweight` | number or string | `bylayer`, `byblock`, `default` (`ByLineWeightDefault`), else millimetres. |
| `isText` | bool | true for `TEXT` and `MTEXT`, false for everything else. |
| `isPolyline` | bool | true for `LWPOLYLINE` and for `POLYLINE` that is a `Polyline2d`; false otherwise. |
| `isFlat` | bool | Only for 2D entity types (omit for 3D solids, surfaces, meshes, regions, 3D polylines, point clouds): true when every defining point (`StartPoint`, `EndPoint`, `Center`, `Position`, vertices, fit and control points, `Elevation`) has \|Z\| <= 1e-6 drawing units and, where the entity has a `Normal`, it is (0,0,1) within 1e-9. For `INSERT`: `Position.Z` and `Normal`. For paper-space entities still fill it (paper space is 2D). |

Fields by type (leave out on other types):

| Type | Field | Type | Meaning and how to obtain |
|---|---|---|---|
| `TEXT`, `MTEXT` | `textHeight` | number | Annotative text: its paper height (the height of its annotation scale representation divided by that scale, which is the same for every representation unless edited separately); otherwise `DBText.Height` / `MText.TextHeight` in drawing units. |
| | `textScaleRatio` | number | Model length per paper length in the same unit. Paper-space text and annotative text: 1. Non-annotative model-space text: the `vpScaleRatio` of the viewports (any layout) whose model-space view rectangle contains the text's insertion point, when they all have the same ratio; otherwise (none, or several different ratios) the ratio of the current annotation scale `Database.Cannoscale` = `DrawingUnits / PaperUnits` x (model unit in mm / paper unit in mm), where the paper unit is the inch when `MEASUREMENT` = 0 and the millimetre when 1. |
| | `scaleSource` | string | `paper`, `annotative`, `viewport` or `annoscale`: which rule gave `textScaleRatio`. |
| | `textStyle` | string | Name of the text style. |
| | `isAnnotative` | bool | `Annotative == AnnotativeStates.True`. |
| `LINE` | `x1`, `y1`, `x2`, `y2` | number | Endpoints in WCS, ordered so that (`x1`,`y1`) <= (`x2`,`y2`) comparing x first, then y; a reversed copy of a line then has the same values. |
| | `offOrthoDeg` | number | Angle of the XY projection from the WCS X axis, folded to [0, 45]: `a = atan2(dy, dx)` in degrees modulo 90, `offOrthoDeg = min(a, 90 - a)`. Omit for lines whose XY length is below 1e-9. |
| `LWPOLYLINE`, 2D `POLYLINE` | `isClosed` | bool | `Closed`. |
| | `hasWidth` | bool | `Polyline.ConstantWidth > 0`, or any vertex start or end width > 0 (`GetStartWidthAt`, `GetEndWidthAt`; for `Polyline2d` `DefaultStartWidth`, `DefaultEndWidth` and each `Vertex2d` width). |
| `DIMENSION` | `textOverride` | string | `Dimension.DimensionText`; `""` when there is no override. |
| | `dimStyle` | string | `Dimension.DimensionStyleName`. |
| | `dimLfac` | number | `Dimension.Dimlfac` (includes a per-dimension override). |
| | `measurement` | number | `Dimension.Measurement`. |
| `HATCH` | `isAssociative` | bool | `Hatch.Associative`. |
| | `patternName` | string | `Hatch.PatternName`, upper case. |
| | `patternScale` | number | `Hatch.PatternScale`. |
| `INSERT` | `blockName` | string | Effective block name (`DynamicBlockTableRecord` name for dynamic blocks). |
| | `isXref` | bool | The block is an xref (`IsFromExternalReference` or `IsFromOverlayReference`). |
| `VIEWPORT` | `isLocked` | bool | `Viewport.Locked`. |
| | `vpScaleRatio` | number | Model length per paper length in the same unit: `(1 / Viewport.CustomScale)` x (model unit in mm / paper unit in mm), where the model unit comes from `INSUNITS` (with the same unitless fallback as the meta) and the paper unit is 25.4 mm when the layout's `PlotPaperUnits` is Inches and 1 mm otherwise. 1/4" = 1'-0" in an inch drawing is 48; 1:100 in a millimetre drawing is 100; 1:100 in a metre drawing with millimetre paper space is 100. |

### 1.4 `blocks`: one record per block definition, except layout blocks; `id` = block table record handle

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `BlockTableRecord.Name`. |
| `isXref` | bool | `IsFromExternalReference` or `IsFromOverlayReference`. For xrefs leave the entity counts at 0. |
| `isXrefDependent` | bool | `SymbolTableRecord.IsDependent`: the block came in with an xref (`XREF|NAME`). |
| `isAnonymous` | bool | `IsAnonymous` (dimension blocks, dynamic block representations, anonymous groups). |
| `isDynamic` | bool | `IsDynamicBlock`. |
| `entityCount` | int | Entities in the definition (attribute definitions included). |
| `hardPropertyEntities` | int | Entities in the definition whose colour is not ByBlock or ByLayer, or whose linetype is not ByBlock or ByLayer, or whose lineweight is not ByBlock or ByLayer (`ByLineWeightDefault` counts as hard). |
| `nonZeroLayerEntities` | int | Entities in the definition on a layer other than `0` (informational; no check fails on it alone). |
| `referenceCount` | int | `GetBlockReferenceIds(true, false).Count`. |
| `units` | string | `BlockTableRecord.Units`, mapped like `document.insUnits`. |

### 1.5 `xrefs`: one record per node of the host's xref graph except the host; `id` = handle of the first insert in the host for depth-1 references, absent for nested ones

Obtain with `Database.GetHostDwgXrefGraph(true)`; nodes are `XrefGraphNode`.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `XrefGraphNode.Name`. |
| `depth` | int | Shortest number of edges from the host node: 1 = referenced by the host itself, 2 = referenced by a depth-1 xref, and so on. |
| `isOverlay` | bool | The reference is an overlay: `BlockTableRecord.IsFromOverlayReference` of the node's block (for a nested node, opened in the database of the xref that references it). `XrefGraphNode` has no overlay member, so leave the field out when the block cannot be opened. |
| `path` | string | Stored path (`BlockTableRecord.PathName`). |
| `pathType` | string | `full` when the stored path is rooted (drive letter, UNC `\\`, or `/`), `relative` when it starts with `.\`, `..\`, `./` or `../`, `none` when it is a bare file name. |
| `status` | string | `XrefGraphNode.XrefStatus`: `resolved`, `unloaded`, `unreferenced`, `not_found` (FileNotFound), `unresolved`. |
| `containsReferences` | bool | The node has child nodes (`NumOut > 0`). Unresolved nodes: false. |
| `insertLayer` | string | Layer of the first insert of this xref in the host (depth 1 only). |
| `insertCount` | int | Number of inserts of this xref in the host (depth 1 only). |

### 1.6 `styles`: one record per text style and per dimension style; `id` = style handle

Leave out xref-dependent styles and text style records that are shape files (`IsShapeFile`).

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `kind` | string | `text` (`TextStyleTableRecord`) or `dim` (`DimStyleTableRecord`). |
| `name` | string | Record name. |
| `isUsed` | bool | Text style: referenced by any TEXT, MTEXT, ATTRIB, ATTDEF, MULTILEADER or table in model space, paper space or any block definition, or as `DIMTXSTY` of a dimension style that is itself used. Dimension style: referenced by any dimension, leader or tolerance anywhere, including inside block definitions. |
| `annotative` | bool | `Annotative == AnnotativeStates.True`. |
| `font` | string | Text only: `Font.TypeFace` when not empty (TrueType family, e.g. `Arial`), else `FileName` (e.g. `romans.shx`). |
| `isShx` | bool | Text only: the font is an SHX file. |
| `height` | number | Text only: `TextSize` (0 = variable). |
| `dimlunit`, `dimdec`, `dimrnd`, `dimlfac`, `dimtxt` | number | Dim only: `Dimlunit`, `Dimdec`, `Dimrnd`, `Dimlfac`, `Dimtxt`. |
| `dimtxsty` | string | Dim only: name of the `Dimtxsty` text style. |
| `stepMm` | number | Dim only: smallest increment the style displays, in millimetres. `step` in drawing units = `10^-DIMDEC` for DIMLUNIT 1, 2, 3 and 6 (for 3, engineering, the step is in inches) and `1 / 2^DIMDEC` for DIMLUNIT 4 (architectural) and 5 (fractional); if `DIMRND > 0`, `step = max(step, DIMRND)`. `stepMm = step / abs(DIMLFAC) x` (drawing unit in mm, using the meta unit and its fallback). Linear settings only; alternate units are ignored. |
| `unitSystem` | string | Dim only: `imperial` when DIMLUNIT is 3, 4 or 5 or the drawing unit is `in` or `ft`; otherwise `metric`. |

### 1.7 `layouts`: one record per paper-space layout (not Model); `id` = layout handle

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `Layout.LayoutName`. |
| `tabOrder` | int | `Layout.TabOrder`. |
| `viewportCount` | int | Viewports in the layout block other than the overall paper viewport (found as in 1.3). |
| `paperSize` | string | `Layout.CanonicalMediaName`. |
| `plotStyleTable` | string | `Layout.CurrentStyleSheet`; absent when none. |

### 1.8 `annotations`: one record per visible text box in model and paper space; `id` = entity handle

Visible means not `Visible == false` and not on a frozen or off layer. The overall paper viewport is skipped. Boxes
are axis-aligned in the space's coordinates (WCS for model space, paper units for a layout). Records are capped at
the collector's record ceiling; failures to read a box are counted and the records part is marked cut.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `type` | string | `TEXT`, `MTEXT`, `ATTRIB`, `DIMENSION` or `MULTILEADER` (DXF name). |
| `space` | string | `model` or `paper`. |
| `layout` | string | `Model` or the layout name. |
| `layer` | string | Entity layer. |
| `minX`, `minY`, `maxX`, `maxY` | number | Text box. TEXT: `GeometricExtents` (empty strings skipped). MTEXT: `GetBoundingPoints()` (the actual text, not the column box). ATTRIB: as TEXT, or the bounding points of `MTextAttribute` for multiline attributes; invisible attributes skipped. DIMENSION: union of the MText/DBText parts of `Explode` (the text, not the lines). MULTILEADER with `ContentType == MTextContent`: bounding points of `MText`. |
| `ownerId` | string | ATTRIB only: handle of the owning block reference. |
| `byJob` | bool | Only with job scope: the job created or modified the entity. |

### 1.9 `jobPlacement`: one record per model-space entity the job created or modified; `id` = entity handle

Collected only when verifying a job. In an audit the table is empty and marked cut ("no job"), so its checks report
`incomplete`. Floating viewports that are on, not perspective and of positive size are compared with the entity's
`GeometricExtents`, transformed into each viewport's display coordinates as in 1.3.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `type`, `space`, `layout`, `layer` | string | As in 1.3 (`space` is always `model`). |
| `viewportCount` | int | Floating viewports considered, in all layouts. |
| `shown` | string | `noViewports` when the drawing has none; `none` when no viewport shows any of it (outside every window, or its layer is frozen in each viewport that would show it, `IsLayerFrozenInViewport`); `full` when at least one viewport shows all of it; `partial` when the viewports that show it all cut it. A non-rectangular viewport clip is judged by its rectangle. |
| `viewportsFull`, `viewportsPartial` | int | Viewports showing it whole, and cut. |
| `layoutsShowing` | string[] | Layouts of the viewports that show any of it. |

---

## 2. Revit tables

Collect from the open `Document` (not from links). Category values are `BuiltInCategory` enum names
(`OST_Walls`), so checks do not depend on the UI language. Convert internal lengths (feet) to millimetres with
x 304.8 or `UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters)`.

### 2.1 `document`: exactly one record, no `id`

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `title` | string | `Document.Title`. |
| `isWorkshared` | bool | `Document.IsWorkshared`. |
| `extentsMaxDistanceMm` | number | Largest distance, in mm, from the internal origin (0,0,0) to any corner of `get_BoundingBox(null)` of any model element in the document. Model elements = elements whose category `CategoryType` is Model, not view-specific, not element types; leave out levels, grids, reference planes, scope boxes, the project base point and survey point, cameras and link instances. 0 when there are none. |
| `designOptionSetCount` | int | Elements of category `OST_DesignOptionSets`. |
| `designOptionCount` | int | `DesignOption` elements. |
| `groupInstanceCount` | int | `Group` instances (model and detail; attached detail groups included). |
| `unusedGroupTypeCount` | int | `GroupType` elements (model and detail) with no `Group` instance. |
| `unusedFamilyCount` | int | Loadable families (`Family`, not `IsInPlace`, `IsEditable`) none of whose `FamilySymbol`s has an instance and that are not used as a type by another element (tags, title blocks, profiles: check `FamilySymbol` ids referenced by placed elements and by types such as `DimensionType`/`WallType` sweeps†). |
| `importedImageCount` | int | `ImageType` elements (raster images loaded in the project). |
| `planRegionCount` | int | Elements of category `OST_PlanRegion`. |
| `lineStyleCount` | int | Subcategories of `OST_Lines` (informational, for review of rv-013). |
| `fillPatternCount` | int | `FillPatternElement` elements (informational). |
| `textTypeCount` | int | `TextNoteType` elements (informational). |

### 2.2 `warnings`: one record per warning; no `id` (already being built)

From `Document.GetWarnings()`; one `FailureMessage` per record.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `description` | string | `GetDescriptionText()`, as Revit writes it (the checks match English text). |
| `failureDefinitionId` | string | `GetFailureDefinitionId().Guid.ToString()`. Lets a project match by id instead of text. |
| `severity` | string | `GetSeverity()` lower case: `warning`, `error`, `documentcorruption`. |
| `elementIds` | string[] | `GetFailingElements()` then `GetAdditionalElements()`, as element id strings. |
| `elementCount` | int | Length of `elementIds`. |

### 2.3 `views`: one record per view; `id` = view element id (already being built)

Include every `View` except `ViewSheet`, revision schedules inside title blocks (`ViewSchedule.IsTitleblockRevisionSchedule`), and views of type `ProjectBrowser`, `SystemBrowser`, `Internal`, `Undefined`. Templates are included with `isTemplate` true.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `View.Name`. |
| `viewType` | string | `View.ViewType` enum name (`FloorPlan`, `Section`, `ThreeD`...). |
| `isTemplate` | bool | `View.IsTemplate`. |
| `isSchedule` | bool | The view is a `ViewSchedule`. |
| `isDependent` | bool | `GetPrimaryViewId() != InvalidElementId`. |
| `onSheet` | bool | Placed on any sheet: a `Viewport` with this `ViewId`, or a `ScheduleSheetInstance` with this `ScheduleId`. |
| `sheetNumber` | string | Number of the sheet it is on; absent when not on a sheet. |
| `hasTemplate` | bool | `ViewTemplateId != InvalidElementId`; for a dependent view, that of its primary view. |
| `templateApplicable` | bool | true for view types that take a view template in practice: FloorPlan, CeilingPlan, Elevation, Section, Detail, ThreeD, AreaPlan, EngineeringPlan, Walkthrough; false for legends, schedules, drafting views, rendering views and the rest. |
| `hiddenElementCount` | int | Elements hidden in this view one by one: count elements `e` (not element types) with `e.IsHidden(view)` true. Elements hidden by category or filter are not counted. Templates: 0. |
| `detailLevel` | string | `View.DetailLevel`: `coarse`, `medium`, `fine` (`Undefined` -> `undefined`). |
| `scale` | int | `View.Scale`; absent when Revit gives none (0 or less, as for schedules and some view types). |
| `entourageVisible` | bool | true when the `OST_Entourage` or `OST_Planting` category is not hidden in the view (`GetCategoryHidden`, which honours the template) and a `FilteredElementCollector(doc, view.Id)` of that category returns at least one element. |

### 2.4 `elements`: one record per element of the listed categories; `id` = element id

Categories: model `OST_Walls`, `OST_Doors`, `OST_Windows`, `OST_Floors`, `OST_Ceilings`, `OST_Roofs`, `OST_Rooms`,
`OST_MEPSpaces`, `OST_Furniture`, `OST_FurnitureSystems`, `OST_Casework`, `OST_GenericModel`,
`OST_SpecialityEquipment`, `OST_PlumbingFixtures`, `OST_Columns`, `OST_StructuralColumns`, `OST_Mass`,
`OST_Stairs`, `OST_Railings` and `OST_StairsRailing` (both are in `BuiltInCategory`; `Railing` elements carry
`OST_StairsRailing`, so collect both and write each record's own); datums `OST_Levels`, `OST_Grids`; annotation `OST_TextNotes` and family instances of
`OST_DetailComponents`. Element types and elements in links are left out; elements inside groups are included.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `category` | string | `BuiltInCategory` name of `Category.Id`. |
| `familyName`, `typeName` | string | From the element's type (`ElementType.FamilyName`, `Name`); absent for rooms, spaces, levels, grids. |
| `levelName` | string | Name of `Element.LevelId` (rooms: `Room.Level`); absent when none. |
| `mark` | string | `ALL_MODEL_MARK`, trimmed; absent when empty. |
| `number` | string | Rooms and spaces: `SpatialElement.Number`, trimmed; absent when empty. |
| `isPinned` | bool | `Element.Pinned`. |
| `isDatum` | bool | true for levels and grids. |
| `isWorkshared` | bool | `Document.IsWorkshared` (same value on every record, so checks can filter with `where`). |
| `worksetName` | string | Name of `WorksetTable.GetWorkset(element.WorksetId)`; absent when not workshared. |
| `isViewSpecific` | bool | `Element.ViewSpecific`. |
| `ownerViewKind` | string | View-specific elements only: `sheet` when `OwnerViewId` is a `ViewSheet`, else `view`. |
| `text` | string | Text notes only: `TextNote.Text` with line breaks replaced by a space, trimmed. |
| `designOption` | string | Name of `Element.DesignOption`; absent for main model. |
| `levelOffsetAbsMm` | number | Absolute offset from the host level in mm. Walls `WALL_BASE_OFFSET`; floors `FLOOR_HEIGHTABOVELEVEL_PARAM`; roofs `ROOF_LEVEL_OFFSET_PARAM`; non-hosted level-based family instances of furniture, furniture systems, casework, generic models, specialty equipment and columns `INSTANCE_ELEVATION_PARAM`; structural columns `FAMILY_BASE_LEVEL_OFFSET_PARAM`. Absent for doors, windows, any instance hosted on a wall, face or other element, ceilings, rooms, spaces, stairs, railings, datums and annotation. |
| `levelSpan` | int | Walls only. Sort the document's levels by elevation. `levelSpan` = (index of the highest level whose elevation <= wall top elevation + 1 mm) - (index of the wall's base level), where the top elevation comes from `WALL_HEIGHT_TYPE` level + `WALL_TOP_OFFSET`, or base level + `WALL_BASE_OFFSET` + `WALL_USER_HEIGHT_PARAM` when unconnected. A wall from Level 1 to Level 2 is 1; Level 1 to Level 4 is 3. |
| `isAttached` | bool | Walls only: `WALL_TOP_IS_ATTACHED` or `WALL_BOTTOM_IS_ATTACHED` is 1†. |
| `roomState` | string | Rooms only, first rule that applies: `unplaced` when `Room.Location` is null; `redundant` when the room is named in a warning "Multiple Rooms are in the same enclosed region" (match the description text case-insensitively on `multiple rooms` and `same enclosed region`) and is not the room with the lowest element id in that warning; `not_enclosed` when placed and `Area` is 0; else `ok`. |
| `roomId` | string | Spaces only: id of `Space.Room` for the space's phase; absent when none. |

### 2.5 `types`: one record per element type of the listed kinds; `id` = type element id

Kinds: every `FamilySymbol` (loadable and in-place), `WallType`, `FloorType`, `CeilingType`, `RoofType`, `DimensionType`.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `name` | string | `ElementType.Name`. |
| `familyName` | string | `ElementType.FamilyName`. |
| `category` | string | `BuiltInCategory` name of the type's category; for `DimensionType` `OST_Dimensions`. |
| `isInPlace` | bool | `FamilySymbol.Family.IsInPlace`; false for system types. |
| `isLoadable` | bool | `FamilySymbol` and not in-place. |
| `instanceCount` | int | Placed instances of the type. |
| `ifcExportAs` | string | Type parameter `IFC_EXPORT_ELEMENT_TYPE_AS` ("Export Type to IFC As"), or a type parameter named `IfcExportAs` when that is filled instead†; absent when blank. |
| `dimensionStyle` | string | `DimensionType` only: `DimensionType.StyleType` lower case (`linear`, `angular`, `radial`, `diameter`, `arclength`, `spotelevation`...). |
| `stepMm` | number | `DimensionType` of a length unit only (absent for angular and other non-length types): the rounding the type displays, in mm. `FormatOptions fo = type.GetUnitsFormatOptions()`; if `fo.UseDefault`, use `Document.GetUnits().GetFormatOptions(SpecTypeId.Length)`. `stepMm = UnitUtils.ConvertToInternalUnits(fo.Accuracy, fo.GetUnitTypeId()) x 304.8`†. 1/256 in is 0.0992 mm. |
| `unitSystem` | string | `DimensionType` of a length unit only, like `stepMm`: `imperial` when the unit type is feet, inches, feet-and-fractional-inches or fractional inches; else `metric`. |

### 2.6 `links`: one record per `RevitLinkInstance` and per `ImportInstance`; `id` = instance id

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `kind` | string | `revit` or `cad`. |
| `name` | string | Name of the link or import type. |
| `isLinked` | bool | Revit links: true. CAD: `ImportInstance.IsLinked`. |
| `isPinned` | bool | `Element.Pinned`. |
| `pathType` | string | Linked only: `ExternalFileUtils.GetExternalFileReference(doc, typeId).PathType` lower case: `absolute`, `relative`, `server`; cloud models `cloud`. Absent for imports. |
| `isWorkshared` | bool | `Document.IsWorkshared`. |
| `worksetName` | string | Workset of the instance; absent when not workshared. |
| `isViewSpecific` | bool | `ImportInstance.ViewSpecific` (current view only); false for Revit links. |

### 2.7 `annotations`: one record per text note, tag, room tag and dimension text in the views read; `id` = element id

Views read: when verifying a job, the owner views of the view-specific elements the job created or modified; in an
audit, every view placed on a sheet that is not a template or a schedule (at most 20000 views; past that the table
is marked cut and no view is read). Elements: `OwnedByView`, classes `TextNote`, `IndependentTag`,
`SpatialElementTag`, `Dimension`; hidden elements (`IsHidden(view)`) and hidden categories are skipped. Boxes are in
millimetres on the printed sheet, in the view plane (`u` along `View.RightDirection`, `v` along `View.UpDirection`,
scaled by 1 / `View.Scale`), so they compare across views of any scale.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `category` | string | `BuiltInCategory` name (`OST_TextNotes`, `OST_DoorTags`...). |
| `kind` | string | `text`, `tag`, `roomTag` or `dimension`. |
| `viewId`, `viewName` | string | Owner view. |
| `minU`, `minV`, `maxU`, `maxV` | number | Text, tags: `get_BoundingBox(view)` corners projected into the view plane (includes the leader for tags, see `hasLeader`). Dimensions: estimated per segment (record id `<id>:<n>`) from `TextPosition`, the length of `ValueString` and the type's `TEXT_SIZE` and `TEXT_WIDTH_SCALE`; turned for dimension lines that are mostly vertical. |
| `hasLeader` | bool | Text: `LeaderCount > 0`; tags: `HasLeader`. A leader widens the box, so the bundled overlap check leaves these out. |
| `estimated` | bool | True for dimension text boxes. |
| `byJob` | bool | Only with job scope: the job created or modified the element. |

### 2.8 `jobPlacement`: one record per model element the job created or modified; `id` = element id

Collected only when verifying a job; in an audit the table is empty and marked cut ("no job"). The views compared
are floor, ceiling, area and engineering plans placed on a sheet with an active crop, at the element's level
(`GenLevel` of the view). The element's `get_BoundingBox(null)` is compared with each view's crop box.

| Field | Type | Meaning and how to obtain |
|---|---|---|
| `category` | string | `BuiltInCategory` name. |
| `viewSpecific` | bool | `Element.ViewSpecific`. |
| `shown` | string | `full` (inside the crop of at least one candidate view), `partial` (cut by every crop that touches it), `none` (outside every crop), or `notChecked` with `reason` (annotation categories, elements drawn on a sheet or in a view without a crop, no level, no candidate view, hidden in all candidates). |
| `reason` | string | Only when `shown` is `notChecked`. |
| `candidateViews` | int | Cropped plans on sheets at its level. |
| `viewsFull`, `viewsPartial` | int | Candidate views showing it whole, and cut. |
| `views` | string[] | Names of the candidate views that show any of it. |

---

## 3. New evaluators

All of them are neutral (they read only facts and the standard), so implement each once, preferably in SA.Core
beside `NeutralEvaluators`, and register it in both engines. Every one accepts `table` and the optional `where`
object with the same meaning as the neutral evaluators, and every finding carries `field` and `value` in its data so
fix templates work. A missing table means not_implemented (throw `FactsMissingException`); bad params are an error
(`CheckParamException`).

### `conditional_field_equals`
`{ table, where?, field?, equals | in, negate? = false, ifField, ifPattern | ifStandardPattern, ifNegate? = false, ignoreCase? = false }`

For each record passing `where`: the **condition** holds when `ifField` is a string that matches the whole regular
expression (`^(?:pattern)$`; `ifStandardPattern` names an entry of the standard's `patterns`), XOR `ifNegate`. An
absent or non-string `ifField` does not match (so with `ifNegate` true it satisfies the condition). For records
where the condition holds: when `field` is given, a finding when (`field` equals `equals` or one of `in`) XOR
`negate`, with JSON equality and "absent never equals" as in `field_equals`; when `field` is omitted, every such
record is a finding. Give exactly one of `equals`/`in` when `field` is given, and exactly one of
`ifPattern`/`ifStandardPattern`. `ignoreCase` applies to the pattern.
Finding: message `"<field> is <value> on <ifField> '<ifValue>' [id]"`; data `{ field, value, ifField, ifValue }`
(`field` = `ifField` when `field` is omitted).

### `field_within`
`{ table, where?, field, min?, max?, minExclusive? = false, maxExclusive? = false }`

The inverse of `field_range`: a finding for each record whose field is a number inside the interval (bounds
inclusive unless the exclusive flag is set). Non-numbers and absent values are ignored. At least one of `min`,
`max`. Data `{ field, value, min, max }`.

### `distinct_count_min`
`{ table, where?, field, min }`

Counts the distinct values of `field` (JSON equality; absent values are not counted) over the records passing
`where`. One finding when the count is below `min`, none otherwise; no finding when no record passes `where`.
Data `{ field, value: [distinct values], count, min }`.

### `scale_in_standard`
`{ table, where?, field, tolerance? = 0.001, skipMissing? = false }`

`field` is a scale ratio (model length per paper length). A finding for each record whose value is not within
`tolerance` (relative) of the `ratio` of some entry of the standard's `scales`, and for each record whose value is
absent, not a number or not positive. With `skipMissing`, records without the field are skipped instead (views
that have no scale, such as 3D views or legends). Requires the standard. Data `{ field, value, nearest: <name of the nearest
standard scale>, nearestRatio }`.

### `units_match_standard`
`{ table, where?, field }`

`field` holds a unit symbol (`mm`, `cm`, `m`, `in`, `ft`, `unitless` or another word). Requires the standard. When
the standard has `units.length`, a finding for each record whose value differs from it; when it has none, a finding
only for `unitless`. Data `{ field, value, expected: <standard units.length or null> }` (the `fix.units` template
uses `{{expected}}`).

### `dim_precision_min`
`{ table, where?, field? = "stepMm", systemField? = "unitSystem", imperialMinIn, metricMinMm, tolerance? = 1e-6 }`

For each record: the minimum is `imperialMinIn x 25.4` mm when `systemField` is `imperial`, `metricMinMm` when it is
`metric`. A finding when `field` < minimum x (1 - tolerance), and when either field is absent or unusable ("cannot
judge precision"). Data `{ field, value, minMm, system }`.

### `matches_standard_entry`
`{ table, where?, list, nameField? = "name", properties, ignoreCase? = true }`

`list` is `layers`, `textStyles` or `dimStyles`. For each record whose `nameField` value names an entry of that
list (compared without case when `ignoreCase`), compare each pair of `properties` (`{ "<record field>": "<standard
property>" }`): standard properties are `color`, `linetype`, `lineweight`, `plot`, `transparency` for layers;
`font`, `height`, `widthFactor`, `obliqueAngle`, `annotative` for text styles; `annotative` and `settings.<NAME>`
for dimension styles. A property the standard entry does not give is not compared. Comparison: numbers within
1e-6 relative; strings without case; colours normalised (ACI number and `"7"` are equal; `#RRGGBB` and `"r,g,b"`
are equal); an absent record field differs. Records not named in the list are skipped (other checks report them).
One finding per record with at least one difference; data `{ field, value, expected, mismatches: [ { field, value,
expected } ] }` where `field`, `value`, `expected` are the first difference.

### `group_count_over_threshold`
`{ table, where?, key, labelField?, max }`

Groups the records passing `where` by the value of `key` (records without it form no group). One finding per group
whose size is more than `max`, largest group first. Message `"<count> x <label>"` where label is the `labelField`
value of the group's first record, else the key value. Data `{ field: key, value: <key value>, count, max, label }`.

### `manual_review`
`{ instructions? }`

Reads no facts and never needs a table. Always returns exactly one finding with no record id: message
`"Review by eye: <check title>"`, data `{ field: "manual", value: "review", review: true, instructions:
<instructions or the fix's instructions> }`. The check status is therefore `fail`; report writers must list checks
whose evaluator is `manual_review` in a separate "review by eye" section and leave them out of pass/fail counts
(they also carry the group `manual`). A future report format may give them their own status (see README).

---

## 4. Layout evaluators

Neutral, in SA.Core `LayoutEvaluators`, registered in both engines.

### `extents_overlap`
`{ table, where?, whereNot?, groupBy? = [], box? = ["minX", "minY", "maxX", "maxY"], minOverlap? = 0.1, jobOnly? = true, labelField? }`

Compares the axis-aligned boxes of the records passing `where`/`whereNot`, within each group of equal `groupBy`
values (records missing a box field are skipped; a missing group field is its own value). A pair overlaps when the
shared area divided by the area of the smaller box is more than `minOverlap` (0 to 1; boxes of zero area never
overlap). With `jobOnly` and facts collected for a job (`meta.jobScope` true), only pairs where at least one record
has `byJob: true` count. One finding per pair, on the record with the smaller min x: message `"<label> [id] and
<label> [otherId] overlap by <percent>% of the smaller (<groupBy values>)"`; data `{ field: "overlap", value:
<fraction>, otherId, ids: [id, otherId], box, otherBox, group }`. Past 50,000,000 comparisons the search stops with
one extra finding without a record id, data `{ review: true }`.

### `sheet_scales_uniform`
`{ table, where?, groupField, scaleField, typeField?, tolerance? = 0.001 }`

Reads the standard's optional `scaleRules`. Without it, or with `oneScalePerSheet` false, no findings. Otherwise
records are grouped by `groupField` (sheet number or layout); records whose `typeField` value is in
`exemptViewTypes` (case-insensitive) or whose group matches `exemptSheets` are left out, as are records without a
positive scale. Scales within `tolerance` (relative) are one scale. One finding per group with more than one scale,
on the first record of the least used scale: data `{ field: scaleField, value: [scales], <groupField>: <group>,
scales: [ { ratio, name, count, ids } ] }`, most used scale first, where `name` is the standard scale's name when one
matches (else `1:<ratio>`).
