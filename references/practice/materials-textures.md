# Materials and textures: Revit materials, fill patterns, appearance, AutoCAD materials, exports

Read this when you create, assign, clean or export materials: setting up finish materials from a finish schedule, choosing surface and cut patterns, fixing wrong-sized tiles in a view, sorting out texture paths, attaching materials in AutoCAD, or checking what a consultant will receive in DWG, IFC or FBX. Rendering and photoreal output are out of scope; how materials look in shaded and realistic views and in exports is in scope. The engine ops that set these values are `fillpattern.ensure`, `material.ensure` and `material.assign` (specified, not yet built); finish codes and schedules are in `interiors.md`; hatch choice for 2D drawings is in `drawing-conventions.md` section 6. Figures marked "typical practice" are conventions with no public source; sourced module sizes are in `data/design/materials.json`.

Contents: 1 Decide first · 2 Revit material anatomy · 3 Fill patterns: drafting and model · 4 Appearance assets and texture maps · 5 Keeping graphics and appearance consistent · 6 Choosing patterns and textures per finish · 7 Material library management · 8 Texture files · 9 AutoCAD materials · 10 What survives export · 11 Common mistakes · 12 Checks

## 1. Decide first

| Question | Why it matters | Default if nobody says |
|---|---|---|
| Where is the finish schedule, and what are the codes? | One material per finish code (`interiors.md` section 7) | Codes from `interiors.md` section 1 (PT-01, CT-01, WD-01 …) |
| Which views must show finishes? | Patterns read in hidden-line and shaded views; textures only in realistic views | Finish plans and interior elevations show model surface patterns; textures only for 3D review views |
| Who receives the model, and in what format? | IFC takes the shading colour, not textures (section 10) | Set the shading colour to the finish colour on every finish material |
| Where do texture files live? | Revit and AutoCAD store a path, not the image (section 8) | One shared project texture folder, added to the search paths on every machine |
| Which module sizes? | Model patterns and texture sizes must match the product | The specified product; failing that, the sourced sizes in `data/design/materials.json` |

## 2. Revit material anatomy

A Revit material is one element with several parts. Each part feeds different views and exports [MT03][MT18][MT19].

| Part | Holds | Used by |
|---|---|---|
| Identity | Name, description, class, comments, keywords, manufacturer, model, cost, URL, keynote, mark, assembly and classification codes | Material tags, keynotes, schedules and material takeoffs |
| Graphics: shading | Colour, transparency, "Use Render Appearance" | Shaded and consistent-colour views; IFC colour [MT07] |
| Graphics: surface pattern | Foreground and background pattern, each with a colour | Faces seen in projection in plans, elevations and 3D |
| Graphics: cut pattern | Foreground and background pattern, each with a colour | Section poché where the element is cut (drawing-conventions [S47]) |
| Appearance asset | Render appearance: colour or image, bump, reflectivity, transparency, texture maps with real-world size | Realistic views, rendering, FBX [MT11] |
| Physical and thermal assets | Structural and thermal properties | Analysis and energy work; 2027 adds an embodied-carbon asset [MT19] |

Rules:

- Only the **surface foreground** pattern accepts a model pattern. The surface background and both cut patterns must be drafting patterns; the API refuses anything else [MT19]. Texture alignment to the surface pattern works only with a model surface foreground pattern [MT04].
- A material created from scratch (not duplicated) through the API has **no appearance asset** [MT19]. A realistic view then has nothing to show but the shading colour, so duplicate an office material or attach an appearance.
- "Use Render Appearance" makes the shaded colour an average of the appearance [MT03]. Wireframe and hidden-line views show patterns but not the shading colour [MT18].
- Assembly and classification codes are stored in `UNIFORMAT_CODE` and `OMNICLASS_CODE` in 2024-2025, and in `ASSEMBLY_CODE` and `CLASSIFICATION_CODE` in 2026-2027 [MT19]. Map them by version; never assume one name.

## 3. Fill patterns: drafting and model

| | Drafting pattern | Model pattern |
|---|---|---|
| Size | Fixed on the sheet; the same paper size at every view scale | Real size in the model; grows and shrinks with the view scale |
| Meaning | Symbolic (insulation, concrete, earth) | The real joints of the product (tile, plank, brick, panel) |
| Editing in a view | Cannot be selected | Lines can be selected, moved, rotated, aligned and dimensioned [MT01] |
| Use for | Cut patterns; symbolic surface fills | Surface patterns of real products [MT02] |
| Orientation options | To view, keep aligned with the host, align with text (API `ToView`, `ToHost`, `AsText`) [MT19] | Follows the face; aligned by moving pattern lines |

**Why model patterns must be at real size.** The lines are the joints of the product. A 600 × 600 tile pattern with 600 spacing lets you count tiles, dimension cut tiles at the walls and check the setting-out from the finish plan. A pattern drawn at "looks right" size gives wrong tile counts and wrong cut sizes. Because the pattern shrinks with the scale, a 600 grid is 6 mm apart on paper at 1:100 and 3 mm at 1:200, and it starts to read as tone. At 1:100 and smaller, hide finish surface patterns with the view template rather than scaling them.

**Spacing equals the setting-out module, which is the unit plus its joint.** Set the grid offset to the specified unit plus the joint. For rectified porcelain with a 2-3 mm joint, the difference is small per tile but adds up to 20-30 mm across ten tiles. Brick is the classic case. The US modular brick is 194 × 57 mm (7-5/8 × 2-1/4 in). The joint is the nominal size minus the specified size [MT20]. With a 10 mm (3/8 in) joint the module is about 203 × 67 mm, three courses to about 203 mm (8 in); that arithmetic is derived, not read in the source. UK brick at 215 × 65 mm with 10 mm joints gives a 225 × 75 mm module: typical practice, no source checked.

Grid recipes for real-size model patterns (units in mm, model size). The API builds patterns from grids; each grid has an angle, origin, offset (spacing between parallel lines), shift (along the line) and dash segments [MT19]:

| Layout | Grid 1 | Grid 2 | Example |
|---|---|---|---|
| Stack bond (tile, carpet tile, ceiling tile, panel) | angle 0, offset = module height | angle 90, offset = module width | 600 × 600: offsets 600 and 600 |
| Half bond (plank, brick, 300 × 600 tile laid as brick) | angle 0, offset = h, solid | angle 90, offset = L/2, shift = h, segments [h, -h] | 1200 × 200 plank: offset 600, shift 200, segments [200, -200] |
| 1/n bond | angle 0, offset = h | angle 90, offset = L/n, shift = h, segments [h, -(n-1)h] | 1/3 bond 1200 × 200: offset 400, segments [200, -400] |
| Herringbone, basket weave, random-length planks | Import a PAT file in the UI; the API has no PAT import [MT19] | | |

How the API counts grid angles (the docs say degrees) and dash signs has not been confirmed in the app. The op spec says to confirm both before relying on them. A model PAT file needs its type marked as model; the help says the file must include type definitions [MT02], and the exact header syntax is not reproduced here. `FillPattern.ExportToPAT` exists only from 2025 [MT19].

## 4. Appearance assets and texture maps

The appearance asset of a tile, timber or stone material normally uses the Generic schema. Its colour slot carries an image (a bitmap texture) [MT19]. The image settings that matter:

| Setting (UI) | API property [MT19] | Rule |
|---|---|---|
| Image | `UnifiedbitmapBitmap` (path string) | Path rules in section 8 |
| Size X / Size Y (real-world scale) | `TextureRealWorldScaleX/Y` (distance, minimum 0.01) | The real size the whole image covers |
| Offset X / Y | `TextureRealWorldOffsetX/Y` | Moves the first joint onto the setting-out point |
| Rotation | `TextureWAngle` (0-360) | Grain and plank direction |
| Repeat | `TextureURepeat` / `TextureVRepeat` | On, for tiling images |
| Lock / link | `TextureScaleLock`, `TextureLinkTextureTransforms` | Link bump and colour maps so they stay aligned |

**Size = whole units in the image × module.** An image showing 4 × 4 tiles of 600 mm at 600 mm spacing is 2400 × 2400 mm. An image of three planks of 1200 × 200 is 1200 × 600 mm. A default size (often a round metre) on a 600 tile gives tiles at the wrong size in realistic views. This is the most common texture error.

**Align the texture to the pattern.** In the UI, use the texture alignment dialog, which works only when the surface foreground is a model pattern [MT04]. In the API, the same effect comes from the offsets and rotation above.

**Appearance assets are shared.** Editing one updates every material that uses it [MT19]. Before changing the texture or colour of one finish, give that finish its own appearance asset (duplicate it). Library assets that have not been modified may report an empty rendering asset; reach them through the application asset list [MT19].

## 5. Keeping graphics and appearance consistent

One finish, one colour, one module, used three times: shading colour, surface pattern and appearance. Rules:

1. **Shading colour = the finish colour.** Take it from the schedule: manufacturer colour name, plus an RGB approximation you record. Screen RGB is not the paint chip; the schedule still governs. IFC takes this colour [MT07], so set it even if nobody uses shaded views.
2. **"Use Render Appearance": off for finish materials** (decision). An averaged photo colour is usually darker or greyer than the scheduled colour, which spoils colour finish plans and shaded elevations. Turn it on only for materials with no defined finish colour (generic stone or site materials), where the photo average is the best available.
3. **Surface pattern module = texture module.** The same 600 grid in hidden-line and realistic views, aligned on the same setting-out point.
4. **Transparency**: glass gets shading transparency in the same range as its appearance; no surface pattern.
5. **Cut pattern** follows `drawing-conventions.md` section 6, not the finish. A 10 mm tile is not hatched in section; the substrate is.
6. **Check**: list finish materials and compare shading colour with the appearance's main colour (section 12). Report large differences; do not auto-fix them.

## 6. Choosing patterns and textures per finish

Sizes come from the specified product. Where none is specified yet, use the sourced US federal option lists in `data/design/materials.json` (ids `materials.us.ufgs.*`) and the ceiling modules already in `data/design/ceilings.json` (`ceilings.us.ufgs.module.*`). Do not repeat ceiling modules elsewhere.

| Finish | Surface pattern (model, real size) | Texture | Orientation and joints | Colour |
|---|---|---|---|---|
| Porcelain and ceramic tile | Grid at tile + joint; stack or half bond as specified | Whole tiles with grout lines; 2 × 2 to 4 × 4 tiles per image | Start from the setting-out point (room centre or a stated line). Edge cuts no less than half a tile (the resilient-flooring rule [MT20], applied here as good practice; ceilings have their own rule in ceilings.json) | Tile colour from the schedule; grout colour is a separate schedule line |
| Resilient tile and plank (VCT, LVT, rubber) | Grid at tile size (butt joints) | As tile; planks with a visible bevel only if the product has one | LVT planks follow the room's long axis unless the schedule says otherwise (typical practice) | Schedule colour |
| Sheet vinyl, rubber sheet, broadloom carpet | None in plan, or seam lines only on setting-out drawings at roll width [MT20] | Fine, low-contrast, small repeat; no strong pattern | Seams parallel to the main traffic direction (typical practice) | Schedule colour |
| Carpet tile | Grid at tile size; plank tiles as half or 1/3 bond | Low contrast; avoid an obvious per-tile repeat | Install method (monolithic, quarter-turn, ashlar) from the schedule; the pattern shows the grid only | Schedule colour |
| Timber strip and plank | Lines along the boards at board width; stagger end joints as 1/3 bond (stand-in for random lengths) | Grain along the board length; image length = Size along the grain | Board direction as specified; often the long axis of the room or across joists (typical practice) | Species and stain from the schedule |
| Stone tile and slab | Joints at the slab module | One large image per slab, so repeats do not show; vein direction set by rotation | Book-matching and vein direction are design decisions; draw them in elevations, not textures | Stone name from the schedule; the shading colour is the average tone |
| Plaster and paint (PT, GWB) | None | None, or a very fine neutral image; a solid colour is honest | Board joints are not drawn for painted board | Paint colour reference from the schedule |
| Metal (panels, trims) | Panel joints at the real panel size, if paneled | Metal appearance; no image needed | Brushed grain direction by rotation | Finish (anodised, powder coat RAL or equal) from the schedule |
| Glass | None | Glazing appearance | — | Tint from the schedule; transparency set |
| Fabric and wall covering | Roll seams at roll width, if shown | Size = the pattern repeat (vertical and horizontal) from the product data | Repeat and match type (straight, drop) from the product data | Colourway from the schedule |
| Acoustic ceiling tile | Grid at the module from `ceilings.json`; in Revit the visible ceiling grid comes from this pattern (product behaviour, not re-checked) | Optional | Minimise border tiles under half a tile (`ceilings.us.ufgs.border_units.rule`) | Schedule colour |
| Brick and block (elevations) | Coursing at unit + joint (section 3) | Several units with colour variation, sized to whole courses | Bond as specified | Brick name from the schedule |

## 7. Material library management

- **One material per finish code** (`interiors.md` section 7). The code is in the name and in the Mark (decision: Mark holds the finish code so tags and schedules can read it; Keynote holds the spec keynote).
- **Name**: `<code> <product description> <module>`, for example `CT-01 Porcelain 600x600 Light Grey`. Follow an office naming standard if one exists (NBS-style `_mtrl` naming [S71]). Construction materials without a finish code (concrete, insulation) use the office standard name with no code.
- **No duplicates.** These signals mean the same finish exists twice: the same code in two names or Marks; suffixes such as "(1)", "Copy" or "2"; "Default" materials on finish elements; or materials that differ only by name (same shading colour, pattern and asset). Revit has no merge. Reassign elements to the kept material, then delete or purge the other.
- **Appearance assets duplicate silently.** Every duplicated material that gets a new appearance adds an asset. The unused ones show in Purge Unused.
- **Purge**: `Document.GetUnusedElements` returns what the Purge Unused dialog lists, including materials, appearance assets and fill patterns (2024 and later) [MT19]. In AutoCAD the existing purge op has a materials kind.
- **Families bring materials.** Loaded and downloaded families carry their own materials. Check new material names after loading content (mistakes RV-026).

## 8. Texture files

- **Revit stores only the image path.** Images are not saved in the RVT, and eTransmit does not collect them [MT06]. Revit looks first at the stored path, then at the "Additional Render Appearance Paths" in the listed order [MT05]. Do not rely on subfolders of a listed path being searched; list each folder (practitioner reports, not verified).
- **Decision: one project texture folder** next to the model, or one office library on a shared (UNC) path [MT06]. Add it to the render appearance paths on every machine. Never use paths under a user profile (Desktop, Downloads, Documents, synced personal folders): they break on every other machine.
- **Name images by finish code**: `CT-01_colour.jpg`, `CT-01_bump.jpg`. The link to the schedule then survives a folder move.
- **Seamless tiling.** The image's left and right edges, and its top and bottom edges, must continue into each other. Check it by viewing it repeated 3 × 3. A visible repeat on a large floor means the image covers too few units; use an image with more units and natural variation.
- **Size and performance.** No public source sets a limit. Typical practice is 1024-2048 px square for repeating finish textures, JPEG for photos, and a lossless format only for maps that need exact values. Report images above about 4096 px or 10 MB as "check" (typical practice), not as errors.
- **AutoCAD** looks up textures through its texture maps search path [MT16]. eTransmit copies textures with the drawing, and practitioners report the paths are rewritten in the transmitted copy [MT17].
- **FBX** carries texture paths; embedding media makes the file self-contained but larger [MT12].

## 9. AutoCAD materials

- **Where they live.** Materials are objects in the drawing's material dictionary [MT19]. `Global`, `ByLayer` and `ByBlock` are built-in names; never create or rename those.
- **Attach by layer.** The default material of new objects is BYLAYER [MT13], so attaching materials to layers is the AutoCAD equivalent of type-driven materials. Attach materials to finish layers named per `cad-standards.md`; keep objects ByLayer. Changes to xref layer associations are not saved back to the source drawing [MT13].
- **Attach by object** only for exceptions; record why. Per-face attachment is out of scope.
- **Mapping**: planar, box, cylindrical or spherical. Planar mapping is not scaled to fit the object [MT14]. Use box mapping for solids such as floor slabs, so the tile size stays real.
- **Visual styles.** Realistic and similar styles show materials, and textures only when the material display mode allows them. `VSMATERIALMODE` is 0 none, 1 materials, 2 materials and textures; it is saved in the drawing [MT15].
- **2D drawings use hatches, not materials.** Materials do not plot in 2D plans and sections. Use the hatch rules in `drawing-conventions.md` section 6: real-size patterns (AR-B816, AR-BRSTD) at scale 1 [S10], associative hatches never exploded [S32]. A finish-plan tile hatch is the AutoCAD stand-in for a Revit model pattern: draw it at real module size, with its origin on the setting-out point.

## 10. What survives export

| Export | Kept | Lost | Do this |
|---|---|---|---|
| DWG from Revit 2D views | Surface and cut patterns as hatches, either generated automatically or mapped to PAT patterns in the export setup [MT09] | Material data and appearance; very dense hatches may not draw (limited by AutoCAD's hatch line limit) [MT09] | Map the main finish patterns to PAT names in the export setup (`export-exchange.md`) |
| DWG 3D (solids) | Material names per face, readable by visualisation tools [MT10] | Textures and procedural patterns [MT10] | Hand textures over separately, with the material-to-image list |
| IFC | Material names, layered materials, colour from the Graphics shading colour [MT07] | Textures and most material parameters [MT08]; view filters and overrides [MT07] | Shading colour = finish colour (section 5); identity data in mapped property sets |
| FBX (visualisation hand-off) | Render appearances, material assignments, lights [MT11] | Texture images unless their paths resolve or media is embedded [MT12] | Collect the texture folder with the FBX; give a manifest of material → image |
| PDF and print | What the view shows | Everything else | Set the view template so finish patterns read at the plot scale |
| DWG from AutoCAD | The material dictionary, attachments and texture paths | The images themselves | eTransmit, then check the transmitted paths [MT17] |

What a viewer shows from IFC also depends on the viewer. Check one finished room in the recipient's viewer before a model-wide issue.

## 11. Common mistakes

Local ids for this guide (they are not in the mistakes catalogues).

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| MAT-01 | Drafting pattern as the surface pattern of a real product | Tile size changes with view scale; cannot align or count | Finish material (coded name) with a drafting surface foreground pattern | Model pattern at the module | M | Auto | MT01 MT02 |
| MAT-02 | Model pattern at the wrong size | Wrong tile counts and cut sizes on finish plans | Pattern grid offsets ≠ the module in the material name, description or schedule | Recreate the pattern at real size and reassign it | M | Auto | MT01 |
| MAT-03 | Texture real-world size wrong (default size) | Tiles look wrong in realistic views and in visualisation | Size X/Y not a whole multiple of the module | Set Size = units in image × module | m | Auto | MT19 |
| MAT-04 | Texture not aligned or rotated against the pattern | Grout lines and joints disagree between views | Visual check in a realistic view with the pattern visible | Texture alignment dialog, or offsets and rotation | m | Heur | MT04 |
| MAT-05 | Shading colour differs from the appearance or the schedule | Shaded views, IFC and renders disagree | Large colour difference; "Use Render Appearance" on for coded finishes | Set the shading colour from the schedule; turn the option off | m | Auto | MT03 MT07 |
| MAT-06 | Duplicate materials for one finish | Tags and takeoffs split; edits miss half the model | Same code in two materials; copy suffixes; identical graphics | Reassign, then delete or purge | M | Auto | S71 |
| MAT-07 | Shared appearance asset edited for one finish | Other materials change without notice | One asset used by more than one coded material | Duplicate the asset per material before editing | M | Auto | MT19 |
| MAT-08 | Missing texture files | Realistic views and FBX show blank or default surfaces | Stored path not found, directly or on the search paths | Move images to the project folder and re-path | M | Auto | MT05 MT06 |
| MAT-09 | Texture paths in a user profile or on a local drive | Break on every other machine and in transmittals | Path under a user folder, or a local drive letter, where the office uses UNC paths | Move to the shared folder and re-path | M | Auto | MT06 |
| MAT-10 | Oversized textures | Slow realistic views and bloated FBX | Image over the project limit (typical practice ~4096 px or 10 MB) | Resize; use a tiling image | m | Auto | (convention) |
| MAT-11 | Paint used instead of types for whole elements | Hard to audit; does not follow later type changes | Many painted faces on one type | Make a type with the finish layer; keep paint for true exceptions | m | Auto | MT19 |
| MAT-12 | Finish material on the wrong compound layer (core, or wrong side) | Wrong takeoffs and wrong face in elevations | Coded finish material on a structure or core layer; finish on the exterior side of an interior partition | Fix the layer in the type | M | Auto | (walls.md) |
| MAT-13 | Finish material with no identity data | Tags show blanks; schedules cannot list it | Coded material with an empty Mark or Description | Fill identity data from the schedule | m | Auto | S71 |
| MAT-14 | AutoCAD objects with hard materials instead of ByLayer | Layer changes do not update appearance | Entity material ≠ ByLayer on finish layers | Set ByLayer; attach to the layer | m | Auto | MT13 |
| MAT-15 | AutoCAD finish hatch at the wrong size or origin | Tiles do not match the setting-out | Hatch pattern spacing ≠ the module; origin not on the setting-out point | Re-hatch at real size with a set origin | m | Heur | S10 |

## 12. Checks

Run before issuing finish plans, elevations, IFC or a visualisation hand-off.

| Check | Pass |
|---|---|
| Every finish code in the schedule has exactly one material | One-to-one; no orphans either way |
| Finish materials have a model surface foreground pattern where the product has joints | MAT-01 clear |
| Pattern grid spacing = the module (unit + joint) | MAT-02 clear |
| Texture Size X/Y = whole multiples of the module | MAT-03 clear |
| Shading colour set from the schedule; "Use Render Appearance" off for coded finishes | MAT-05 clear |
| No appearance asset shared between coded materials | MAT-07 clear |
| Every texture path resolves; none in a user profile | MAT-08, MAT-09 clear |
| Cut patterns follow `drawing-conventions.md` section 6 | Drafting patterns only; substrate hatched, not thin finishes |
| Unused materials, appearance assets and fill patterns purged before issue | Purge list empty or justified |
| One room checked in the recipient's IFC or FBX viewer | Colours and assignments as expected |
| AutoCAD: finish layers carry the materials; objects ByLayer; material display mode set | MAT-14 clear; `VSMATERIALMODE` = 2 in 3D review drawings |

Sources: tags MT01-MT20 (to be listed in `_SOURCES.md`), plus S10, S32, S47, S71 already listed there. API statements [MT19] come from the Revit and AutoCAD reference XML documentation for 2024-2027, checked against a compile test for every year. They describe what the API allows, not tested behaviour.
