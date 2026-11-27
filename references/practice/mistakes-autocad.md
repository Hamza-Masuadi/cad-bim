# AutoCAD: junior mistakes catalogue

Read this when auditing or cleaning a DWG, or before issuing one. Each entry has a stable id (`AC-001` …) that machine-readable checks use; never renumber. Severity is professional judgment (see `qa-review.md` section 5): **C** critical, **M** major, **m** minor. Raise severity for issued sheets or widespread occurrence. Thresholds marked "project" come from the project standard. Detection: **Auto** = a check can decide; **Heur** = a heuristic flags candidates for a human; **Proc** = process, not detectable in the file.

Entries are grouped by topic, so ids are not in numeric order; there are 48 (AC-001 to AC-048), none skipped.

Contents: 1 Layers and properties · 2 Blocks, xrefs, units · 3 Dimensions and text · 4 Geometry · 5 Layouts and plotting · 6 File hygiene

## 1. Layers and properties

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-001 | Geometry or annotation on layer 0 | Loses layer control; xref'd layer-0 content cannot be managed | Entities in model or paper space on layer 0, excluding contents of block definitions | Move to the correct standard layer by object type | M | Auto | S32 S48 S10 |
| AC-002 | Objects on Defpoints | Defpoints never plots; content silently missing from prints | Entities on Defpoints other than dimension definition points | Move to the correct layer | C | Auto | S32 |
| AC-003 | Colour, linetype or lineweight set on the object, not ByLayer | Consultants cannot fade xrefs; layer edits do not propagate | Entity property not ByLayer (block contents may be ByBlock) | Set to ByLayer; move to a layer with the right properties | M | Auto | S48 S32 S33 S40 |
| AC-004 | Objects on the wrong layer (text on wall layers, doors on column layer) | Breaks visibility, plotting, filtering | Object type vs layer major group mismatch (e.g. dimensions not on a dimension layer; text on wall layers) | Move to the layer the standard maps for that object type | M | Heur | S48 S32 |
| AC-005 | No layer standard; random layer names | Recipients must translate; hand-off pain | Layer names failing the project pattern or approved list | Translate layers to the standard (map old to new) | M | Auto | S32 S41 |
| AC-035 | Colours outside the plot-style colour map | Wrong lineweights in print | Layer or entity colours not in the project's CTB mapping | Recolour layers to mapped colours | M | Auto | S09 |
| AC-042 | Layers organised by line weight ("THIN", "THICK") instead of content | Cannot freeze by system; hard to xref | Layer names describing weight, not content | Re-layer by content per standard | M | Heur | S48 |
| AC-048 | No lineweight hierarchy at all | Flat, unreadable drawings | All layers the same lineweight, or plot style maps all colours to one width | Apply the standard's weights (see `drawing-conventions.md`) | M | Auto | S44 |

## 2. Blocks, xrefs, units

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-006 | Blocks built on non-0 layers or with hard-coded properties | Inserted blocks do not take the insertion layer's properties | Block definition entities on layers other than 0 with properties not ByBlock/ByLayer (allow deliberate multi-layer blocks) | Redefine block contents on 0, ByBlock | M | Auto | S32 S33 S10 |
| AC-007 | Exploded blocks (doors, windows as loose lines) | Lose counts, attributes, updates; bloat | Repeated identical line clusters; door-swing arcs as loose arcs on door layers | Replace with block instances | M | Heur | S33 S41 S10 |
| AC-012 | Drawing units wrong or unset (`INSUNITS`) | Blocks and xrefs insert at the wrong scale | `INSUNITS` = 0 or not the project unit (1 inches, 4 mm) | Set `INSUNITS`; check existing inserts for wrong scale | C | Auto | S32 S52 R04 |
| AC-016 | Xref inserted on whatever layer was current | Xref vanishes when that layer is frozen | Xref insert layer ≠ the designated xref layer (e.g. `G-ANNO-REFR`) | Move xref insert to the xref layer | m | Auto | S48 S32 |
| AC-017 | Absolute or broken xref paths | Links break when the project moves | Path type Full; reference unresolved or file missing | Repath to relative; report missing files | M | Auto | S54 S32 S41 |
| AC-018 | Attach used where Overlay is needed | Circular or duplicated nested references | Attached xrefs that themselves contain references | Change to Overlay unless the host must carry it | m | Heur | S54 S44 |
| AC-019 | Sending xref'd drawings without packaging | Missing files at the recipient | n/a | Package with eTransmit and a transmittal | M | Proc | S32 |
| AC-038 | Nested xrefs more than one level deep | Fragile chains; slow loads | Xref nesting depth > 1 | Flatten: reference each source directly in the host | m | Auto | S44 |
| AC-041 | Not using xrefs or blocks where content repeats | Duplicate content drifts out of sync | Identical geometry repeated across files; no xrefs in multi-discipline sheets | Convert to blocks or xrefs | M | Heur | S42 |

## 3. Dimensions and text

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-008 | Exploded dimensions | Not associative; no longer measure; cannot update | Lines, solids and text on dimension layers forming arrow-plus-text patterns; no dimension objects on those layers | Redimension with real dimension objects | M | Heur | S33 S40 |
| AC-009 | Dimension text overridden with a different value | Hides geometry errors; misleads the builder | Dimension text override not empty and not containing the measured value placeholder, or numeric text ≠ measurement | Remove override; fix geometry if the value was wrong; ask before changing design | C | Auto | S32 S33 S40 S53 |
| AC-011 | Dimension precision finer than buildable | Implies unachievable tolerance | Dimension style precision finer than ¼" (imperial architectural) or decimals of a mm (metric) | Set precision per `dimensions-annotation.md` | M | Auto | S49 S42 |
| AC-013 | Non-annotative text and dimensions duplicated per scale on separate layers | Error-prone when scales change | Same string at several heights close together; text styles not annotative where the standard requires | Convert to annotative objects with the needed scales | M | Heur | S32 |
| AC-014 | Text height not matching the standard for the scale | Illegible or inconsistent sheets | Model-space text height ≠ plotted height × scale factor (or annotative text with off-standard paper height) | Set heights from the table in `drawing-conventions.md` | M | Auto | S10 S14 |
| AC-025 | Too many dimension and text styles | Inconsistent output | Style counts above project limit; styles not in the standard list | Merge to standard styles; purge | m | Auto | S32 S41 S44 |
| AC-030 | SHX or custom fonts instead of the standard TrueType font | Unsearchable PDFs; missing fonts at the recipient | Text style font not in the approved list | Remap styles to the standard font | m | Auto | S33 S41 |

## 4. Geometry

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-010 | Fudged geometry (9'-11 ⅞" instead of 10'-0") | Dimensions do not add up | Lengths within a small epsilon of round values but not equal; dimensions needing more than ¼" precision | Correct geometry to true size (ask if design intent is unclear) | C | Heur | S48 S42 |
| AC-022 | Duplicate or overlapping geometry | Wrong quantities; plotting artefacts | Collinear overlaps and coincident entities | Remove duplicates (OVERKILL-style) | M | Auto | S32 S41 S42 |
| AC-023 | Exploded or non-associative hatches | Cannot edit with the boundary | Hatches not associative; many small solids or lines on hatch layers | Re-hatch associatively | m | Auto | S32 |
| AC-024 | Non-associative arrays | Cannot edit the pattern later | Repeated identical objects at regular spacing not in an associative array | Rebuild as an associative array if edits are expected | m | Heur | S32 |
| AC-026 | Running snaps left on everything ("Nearest") | Near-miss endpoints, stacked vertices | Vertices within tiny tolerance of each other; endpoints near but not on other geometry | Snap endpoints together; delete stacked vertices | M | Heur | S32 S42 |
| AC-027 | Non-zero Z values in a 2D drawing | Snaps and areas wrong | Entities with non-zero elevation or extrusion direction not (0,0,1) | Flatten | M | Auto | S32 |
| AC-028 | Room areas from open polylines | Areas wrong or impossible | Polylines on area layers not closed | Close or redraw boundaries | C | Auto | S42 S10 |
| AC-031 | Polyline width used instead of lineweight | Ignores plot style; prints inconsistently | Polylines with width > 0 on non-graphic layers | Set width 0; use layer lineweight | m | Auto | S40 |
| AC-033 | Eyeballed ortho; off-axis lines | Joins and dimensions misbehave | Lines within ±0.5° of orthogonal but not exact | Rotate or redraw to exact angles | M | Auto | S40 S48 |
| AC-034 | Not drawn at 1:1 in model space | Dimensions and scales wrong everywhere | Dimension measurements vs overall extents implausible; `DIMLFAC` ≠ 1 | Scale geometry to true size; reset `DIMLFAC` | C | Heur | S33 S10 S14 |
| AC-040 | Splines and non-constructible curves for built work | Cannot be set out; odd dimensions | Splines on construction layers | Replace with arcs and lines | M | Auto | S42 |
| AC-045 | Real-size hatch patterns scaled | Brick and block patterns wrong size | Real-size patterns (AR-*) with scale ≠ 1 | Reset scale to 1 | m | Heur | S10 |

## 5. Layouts and plotting

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-015 | Unlocked viewports | Scale changes unnoticed | Viewport display not locked | Lock after checking scale | M | Auto | S32 S33 S41 |
| AC-032 | Exploded title blocks | Attributes lost; sheets cannot be updated consistently | Loose text on the title block layer instead of an attributed block | Reinsert the standard title block; transfer values | M | Heur | S40 S10 |
| AC-036 | Non-standard viewport scales (1:1328.4) | Drawings cannot be scaled | Viewport scale not in the approved list | Set an approved scale; re-check annotation | C | Auto | S44 |
| AC-037 | Image frames plotting | Frames print as boxes | Image frame display on with raster images present | Turn frames off or to display-only | m | Auto | S44 |
| AC-043 | Not saved ready to plot; folder structure broken | Recipient cannot plot; links break | Saved in model space with no layout current; xref folder structure not relative | Save on a layout; restore folders | m | Heur | S09 |
| AC-044 | Unused or cluttered layouts | Confusion over which sheets are live | Layouts with no viewports or not in the sheet list | Delete with the user's approval | m | Auto | S88 S44 |
| AC-046 | Linetype scale inconsistent | Dashed lines look solid or vary | `LTSCALE` / `PSLTSCALE` / `MSLTSCALE` not the project values | Set per `cad-standards.md` section 6 | m | Auto | S10 |
| AC-047 | Viewports badly placed: large blank areas or crowding the border | Poor sheet craft | Viewport bounds vs title block drawable area | Re-arrange per `sheets-sets.md` | m | Heur | S44 |

## 6. File hygiene and workflow

| Id | Mistake | Why it harms | Detection signal | Fix | Sev | Det | Src |
|---|---|---|---|---|---|---|---|
| AC-020 | Not purging | Bloat; clutter | Purgeable item count > 0 at issue (remember Regapps purge separately) | Purge all, then regapps | m | Auto | S32 S09 S10 S43 R04 |
| AC-021 | Not auditing received or outgoing files | Latent corruption propagates | Audit reports errors > 0 | Audit and fix; recover if needed | M | Heur | S43 |
| AC-029 | Pasting from other versions or software | Foreign styles and junk; bloat | Unknown style and layer names; many anonymous blocks; inflated scale list | Purge; translate layers; reset scale list | m | Heur | S32 |
| AC-039 | One "master" DWG that everyone pastes into; coordinates ignored | No traceability; misaligned dead linework | Many layouts (≥ 20), unused layouts, content far from the project origin | Split into references on a shared origin (propose, do not do unasked) | M | Heur | S88 |
