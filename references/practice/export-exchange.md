# Export and exchange

Read this before producing PDFs, DWG exports from Revit, IFC, Navisworks caches or any deliverable for others. Source tags point to `_SOURCES.md`.

Contents: 1 Before any export · 2 PDF sets · 3 DWG export from Revit · 4 IFC export · 5 Navisworks and coordination files · 6 COBie (brief) · 7 Naming and status under ISO 19650 · 8 Checks

## 1. Before any export

1. Confirm the naming rule and the status/revision for this issue (section 7).
2. Run the pre-issue review in `qa-review.md` at the depth the user asks for. At minimum: title blocks filled, revisions current, no working views on sheets.
3. Decide the coordinate basis for model exports (shared/survey or internal) and say which in the transmittal.
4. Export to a new folder per issue; never overwrite a previous issue's files.
5. After export: check every expected file exists, page counts and sizes match, and names match the rule.

## 2. PDF sets

| Setting | Rule |
|---|---|
| Paper size | The sheet's own size (A1, ARCH D…), 100%, no fit-to-page |
| Colour | Monochrome or the project plot style unless colour carries information [S09][S10] |
| Vector | Vector output; raster only for views with transparency or shadows that need it |
| Line weights | From the plot style (AutoCAD) or the line weight table (Revit); check thin lines are not lost |
| Hidden lines | Removed in 3D views as displayed |
| Hide | Crop boundaries, reference planes, scope boxes, unreferenced view tags |
| Files | One PDF per sheet named to the rule, or a combined set, as the project requires |
| Fonts | TrueType so text is searchable [S33] |

File naming: ISO 19650 projects use the container name (e.g. `ABCDE-XYZ-ZZ-02-D-A-0001`), with status and revision in metadata or, outside the CDE, appended (DfE example `…-0001-Door Schedule-S3-P01`) [S14]. US projects often use `SheetNumber_SheetName_Rev` per office rule. No public standard for PDF naming was found beyond these; take the project's rule.

AutoCAD: plot from layouts with saved page setups; use publish for sets; set `FILEDIA`/dialogs off only around the batch, and restore them [R04].

## 3. DWG export from Revit

| Setting | Guidance |
|---|---|
| Export setup | Use a named, saved setup per project so every issue is the same |
| Layer mapping | Map categories/subcategories to the recipient's layer standard (US NCS-style or ISO 13567); Revit ships mapping tables for common standards (names not verified here). Check one exported file in AutoCAD |
| Colours and line weights | Map to the recipient's CTB or send by layer |
| Coordinates | Shared coordinates if the recipient works in site coordinates; internal origin otherwise; state which |
| Units | Match the recipient (mm or inches) |
| Views on sheets | Option to export sheet views as separate xrefs or bound into one file; ask the recipient |
| Solids | Polymesh or ACIS per recipient's need |
| Text | Map fonts; check text heights and that tags exported as text read correctly |

Also purge and audit the exported DWGs before sending if the recipient expects clean files.

## 4. IFC export

### 4.1 Setup
- Revit export setups include IFC2x3 Coordination View 2.0 (default certified), IFC4 Reference View and IFC4 Design Transfer View (search excerpt) [S58]. Use the schema the client names (DfE example: IFC2x3 TC1 on that project) [S14]. Reference View is for coordination viewing; it is not meant for editing back into authoring tools.
- Revit uses an open-source IFC exporter maintained by Autodesk, updated per release [R04]; behaviour differs across exporter versions, so record the version used.

### 4.2 Mapping
- Elements map to IFC entities by category and subcategory, overridden by an IFC mapping table or by the `IFCExportAs` / `IFCExportType` parameters on family types (e.g. escalator → IfcTransportElement) [S58].
- DfE rules [S14]: correct entity for every element; IfcBuildingElementProxy only when no entity exists; never `NOTDEFINED` predefined types; use `USERDEFINED` plus an object type when no enumeration fits; project, site, building and storey names per the information standard; ISO 13567 layer names in IFC.
- Storeys come from levels marked as building storeys; only real floors should be storeys.

### 4.3 Practice
- Export from a dedicated 3D view at Fine detail, with only the categories and worksets to be exchanged visible [S59][S22].
- Include property sets the client requires (common Pset, classification, COBie fields); check them in an IFC viewer.
- Choose the coordinate base (shared/survey or project base point) agreed in the BEP.
- Name the file to the container rule with Form `M` (model) [S14].

### 4.4 Typical failures [S59, search excerpts]
| Failure | Likely cause | Fix |
|---|---|---|
| Elements missing | View detail level not Fine; elements hidden in the export view; Join Geometry in IFC4 exports | Fine view; check visibility; test without joins |
| Elements exported as proxies | Wrong category, no mapping, in-place generic models | Correct category or set IFC export class |
| NOTDEFINED types | No predefined type set | Set predefined type or USERDEFINED |
| Curtain wall parts missing | Panels or mullions in unusual categories | Use standard curtain categories |
| Model far from origin or rotated | Wrong coordinate base | Use the agreed base; check with a link or viewer |
| Storeys wrong | Every level exported as a storey | Untick building storey on non-floor levels |

## 5. Navisworks and coordination files

- NWC per model (cache), NWF holds links and clash tests (small), NWD a static snapshot for milestones and sharing [S22].
- Export from a dedicated `3D_Navisworks_Export` view at Fine, hiding lines, scope boxes, analytical lines, imports and links [S22]. Overwrite NWCs in a fixed shared location so the federated file updates [S22].
- Navisworks export from Revit requires the Navisworks exporter to be installed; otherwise it fails [R04].

## 6. COBie (brief)

- COBie worksheets (2.4-style): Contact, Facility, Floor, Space, Zone, Type, Component, System, Assembly, Connection, Spare, Resource, Job, Impact, Document, Attribute, Coordinate, Issue [S60]. NIBS COBie v3 regroups them (e.g. Level instead of Floor) [S60].
- DfE rules: every Space in at least one Zone and a Floor; every manageable Component in a System and a Type; units per the Facility sheet [S14].
- COBie depends on clean rooms, spaces, categories and type data; fix those first.

## 7. Naming and status under ISO 19650

- Container name fields: Project-Originator-Functional-Spatial-Form-Discipline-Number [S14][S17]. Form codes: D drawing, G diagram, I image, L list, M model, T text, V video/audio [S14].
- Status (suitability) and revision are metadata in the CDE, not part of the name [S14][S21]. Codes: S0-S4, S6, S7, A1…, B1…, CR [S15]. Revisions: P01.01 (WIP), P01 (shared), C01 (published) [S15].
- Model and drawing exports from one source share the originator, functional and spatial codes; only Form and Number change.
- Older projects use type codes (`DR`, `M3`) [S17][S83]; follow the project's information standard.

## 8. Checks

| Check | Signal |
|---|---|
| Expected files present | Count of files equals sheets/models planned |
| Names valid | File names failing the project pattern |
| PDF page size | Page size not equal to sheet size |
| IFC proxies and NOTDEFINED | Counts above zero (or above project limit) |
| IFC export view detail | Not Fine |
| Coordinates stated | Transmittal missing the coordinate basis |
| DWG layers mapped | Exported layers not in the recipient's standard |
