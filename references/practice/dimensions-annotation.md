# Dimensions and annotation

Read this before dimensioning, writing notes, adding leaders or keynotes, or setting up annotation scaling. The office standard governs; the rules below are the professional default. The most-cited public source is the AIA's "The lost art of dimensioning" [S49].

Contents: 1 What to dimension to · 2 String hierarchy · 3 Placement and spacing · 4 Rounding and precision · 5 Never override values · 6 Text and leaders · 7 Keynotes and general notes · 8 Annotation scaling · 9 Checks

## 1. What to dimension, and to what

| Element | Dimension to (default) | Why |
|---|---|---|
| Structure | Grid lines (column centrelines) first [S49] | Grids are the first thing set out on site |
| Renovation work | Fixed existing faces [S49] | Existing walls are the only reliable reference |
| Masonry, concrete walls | Face of structure | That is what the builder builds |
| Stud partitions | Face of board, masonry or concrete, excluding applied finishes [S49]. Some offices use face of stud or centreline; follow the firm's standard | Builders set out from a face they can mark |
| Openings in stud walls | Centreline of opening, or to the rough opening per office standard | Centreline is independent of frame size |
| Openings in masonry | Structural/masonry opening size and jamb faces | Masonry is built to the opening |
| Clear dimensions for egress or accessibility | Face of finish, labelled "CLR" or "clear" [S49] | The code measures the finished space |
| Fixtures, casework | Face of the item or centreline, to the nearest wall face | Installation reference |

Every element must be locatable by two dimensions, one in each direction, and only once [S49]. If a dimension appears in an enlarged plan, schedule or detail, do not repeat it on the general plan [S49]. Why: repeated dimensions drift apart when one is updated.

## 2. String hierarchy

Work outside in on exterior strings and inside out within the plan ("inside in, outside out") [S49]:

| Order from the building outwards | String | Content |
|---|---|---|
| 1 (nearest the building) | Openings / wall string | Wall faces, openings (centreline or jambs), returns |
| 2 | Grid string | Grid to grid |
| 3 (outermost) | Overall | End to end of the building or of the grid |

- Put grid and exterior strings outside the plan along the top and right sides of the sheet by default, and inside dimensions inside the plan [S49]. Add left/bottom strings only when the plan needs them.
- Interior strings run from a grid or a structural face, never float between two partitions without a reference.
- Keep interior dimensions short: none longer than half a column bay [S49].
- Prefer open strings: leave the least critical segment undimensioned (or marked "±" or "VARIES" per standard) to absorb tolerance. "EQ EQ" is valid only when the overall is fixed [S49].

## 3. Placement and spacing (typical practice; confirm with the standard)

- First string about 10-15 mm (⅜"-½") plotted clear of the outermost building line; subsequent strings about 7-10 mm (¼"-⅜") apart. Keep the same spacing on every sheet of the set.
- Dimension lines must not cross each other or cross other dimension text. Extension lines may cross other extension lines.
- Text sits above the dimension line (US and ISO common practice) and reads from the bottom or right side of the sheet.
- Do not dimension to hidden lines or to hatch.
- Align strings across adjacent plans and between plan and enlarged plan so they can be read together.
- At 1:100 / ⅛" give fewer, essential strings; full setting out belongs at 1:50 / ¼" or in enlarged plans.

## 4. Rounding and precision

| Units | Display precision (typical) | Source |
|---|---|---|
| Imperial architectural | Nearest ½", occasionally ¼", never finer. Revit's 1/256" default is not a buildable precision | [S49] |
| Imperial, AUGI guidance | Round dimension precision to ¼" | [S48] |
| Metric architectural | Whole millimetres; set-out on site often to 5 mm; never decimals of a mm on building drawings | typical practice, confirm |
| Metric site/large scale | Metres to 3 decimals (= mm) on site plans and levels | typical practice, confirm |

Tolerance references cited by the AIA author: concrete columns and shear walls ±1" in plan; 10 ft precast ±⅛"; 20-40 ft precast ±¼"; gypsum partitions ±¼" [S49]. Dimensioning finer than the trade can build implies a tolerance nobody will meet.

Draw true sizes: 10'-0", not 9'-11 ⅞" [S48]. If a dimension does not round cleanly, the geometry is wrong; fix the geometry, not the display.

## 5. Never override dimension values

- Never replace a measured value with typed text to make it "read right". It hides geometry errors and misleads the builder [S32][S33]. This is a critical finding in review (AC-009).
- Allowed exceptions, labelled clearly: "EQ" in an equal string, "VARIES", "CLR", "±", "TYP", "HOLD", "VIF" (verify in field), or a suffix added to the measured value (e.g. measured value plus " CLR"). AutoCAD: keep the measured value with `<>` in the text and add the suffix around it.
- If the user asks for an override, tell them the measured value, ask whether the geometry should change instead, and only override with their explicit agreement.

## 6. Text and leaders

- Notes in sentence case or uppercase per office standard; one text style per use (notes, titles, tags).
- Left-justify notes; keep them in blocks away from the drawing, with leaders to the element.
- Leaders: straight, or one straight segment with a short horizontal shoulder at the text. No curves, no crossing other leaders or dimension lines. Arrow ends on an edge; dot ends on a surface (common convention).
- Notes say what and where: material, size, reference to spec or detail. Avoid repeating information that the tags, schedules or keynotes already carry.
- Text never overlaps lines, hatches or other text. Use background masks sparingly; move the text instead.
- In Revit, use tags (parameter-driven) for element data, text only for genuine notes [S34]. Annotate in views, not on sheets [S22].

## 7. Keynotes and general notes

| Tool | Use | Notes |
|---|---|---|
| Keynotes (Revit) | Material and element callouts keyed to a list (often organised by specification section; organisation not verified here) | A tab-delimited keynote file; element, material and user keynotes; legend can list keynotes per sheet; numbering by keynote or by sheet [S66, unverified] |
| Plan-specific (coded) notes | Notes numbered per sheet, listed beside the drawing | NY OGS puts general notes top-right, snapped to the title block, with coded notes beside them [S22] |
| General notes | Project-wide instructions (dimensions to face of X, verify on site, do not scale) | On the general notes sheet (sheet type 0) or a legend repeated on sheets |
| Text notes | One-off clarifications | Keep few; they go stale |

Rules: prefer keynotes or schedule-driven notes to typed text legends [S34]. Keep one keynote file per project; never edit codes locally in a view. In AutoCAD, keynotes are blocks with attributes (KEY) and the legend is a table built from the same list.

Standard general notes worth proposing (adapt to the project): dimensions are to face of [structure/finish] unless noted; do not scale from drawings; verify existing dimensions on site; drawings to be read with the specification and engineers' drawings.

## 8. Annotation scaling

### 8.1 AutoCAD
- Two methods; use one per project:
  - **Annotative objects** (text, dimensions, multileaders, hatches, attributed blocks): each object carries one or more annotation scales and displays at the right size in each viewport whose annotation scale matches [S51]. The current annotation scale in model space is `CANNOSCALE`; each viewport has its own annotation scale, which should equal its viewport scale.
  - **Non-annotative**: text heights and `DIMSCALE` set per scale (see `drawing-conventions.md` table). `DIMSCALE` = scale factor, set in the dimension style, not at the command line [S10]. Separate dimension styles per scale.
- Keep viewport scale and viewport annotation scale equal. If annotation disappears after a scale change, Autodesk suggests `ANNOALLVISIBLE` = 1 and `ANNOAUTOSCALE` = 4 (adds the new scale to all supporting objects) [S51, unverified first-hand].
- Do not duplicate non-annotative text per scale on separate layers; that is the outdated method and breaks when scales change [S32].
- Linetype scale: see `cad-standards.md`.

### 8.2 Revit
- Annotation (text, tags, dimensions, symbols) is defined at its plotted size and scales with the view scale automatically. Changing a view's scale resizes annotation but not the model.
- Text and dimension types are named by plotted height and use (e.g. "2.5mm Arial" or "3/32" Arial"). Do not create types for each scale.
- Detail level (coarse, medium, fine) and the view template control what model detail shows at each scale; see `views-templates-graphics.md`.
- Dimension types control rounding; set architectural dimension types to the precision in section 4, never Revit's 1/256" default [S49].

## 9. Checks

| Check | Signal |
|---|---|
| Overridden dimension text | Dimension text replaced without the measured value |
| Precision too fine | Dimension style/type rounding finer than the table in 4 |
| Strings in the wrong order | Overall string inside the grid string |
| Elements dimensioned twice | Same pair of references dimensioned in two places |
| Elements not locatable | Wall or opening with no dimension in one direction (enlarged plan included) |
| Text height off standard | Plotted height not in the standard list |
| Non-annotative duplicates | Same text at several heights near each other |
| Notes on sheets (Revit) | Text notes owned by sheets rather than views [S22] |
