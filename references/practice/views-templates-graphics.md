# Views, view templates and graphics (Revit)

Read this before creating views, setting up view templates and filters, overriding graphics, or diagnosing "I can't see it". Behaviour statements without a source tag come from long-standing product behaviour and were not re-verified in this research; see `_SOURCES.md`.

Contents: 1 View types · 2 View templates · 3 Filters · 4 Override hierarchy · 5 Detail level · 6 View range · 7 Element not visible: diagnosis order · 8 Crop, scope boxes, dependent views · 9 Phase filters · 10 Checks

## 1. View types

| View | Use | Notes |
|---|---|---|
| Floor plan | Plans per level | Cut by the view range |
| Ceiling plan (RCP) | Ceilings, lighting | Looks up from the cut plane; Bottom not used [N11] |
| Area plan | Area schemes | One per scheme and level |
| Elevation (building, interior) | Exterior and interior elevations | Interior elevations from an elevation mark inside the room |
| Section, wall section, detail section | Cuts | Far clip controls depth |
| Callout | Enlarged part of a view | Can be detail or plan type |
| Drafting view | 2D only, no model | Standard details not tied to the model [S34] |
| Legend | Symbols, wall types, keys | Placeable on many sheets |
| Schedule | Tables | See `schedules-tags.md` |
| 3D (default, isometric, camera) | Coordination, presentation, export | Use a dedicated export 3D view for IFC/NWC [S22] |
| Sheet | Documentation | See `sheets-sets.md` |

## 2. View templates

- Every view placed on a sheet has a view template (RV-029) [S91][S22]. Why: consistent graphics and one place to change them.
- Template controls: view scale, detail level, visibility/graphics (model, annotation, imports, links, filters, worksets), view range, phase filter, discipline, underlay, crop settings, shadows. Include only the properties the template should own; uncheck the rest so users can set them per view (e.g. crop, far clip).
- Typical set (names per office): Plan - Working, Plan - GA 1:100, Plan - Setting Out 1:50, Plan - Furniture, Plan - Finishes, RCP 1:100, Elevation 1:100, Section 1:50, Detail 1:10, 3D - Export IFC, 3D - Coordination.
- Apply templates by assignment (the view stays linked), not by one-off application, for documentation views.
- Never fix a graphics problem in one view if the template is wrong; fix the template.

## 3. Filters

- Rule-based filters select elements by category and parameter value (e.g. Fire Rating = "1HR", Phase status, Workset, Type Name contains "PARTITION"). Selection filters hold a fixed set of elements; avoid them for anything lasting.
- Use filters for: fire-rating plans, demolition plans, finish zones, coordination colouring, hiding temporary work.
- Filter order matters: when two filters override the same property, the one higher in the list wins.
- Name filters for what they do: "WALL - Fire Rating 1HR", not "Filter 1".
- Prefer filters and templates over Hide in View (RV-007) [S37][S34][S47].

## 4. Override hierarchy

Most specific wins:

1. Element override in the view (Override Graphics in View → By Element).
2. Filter overrides in the view or template (higher filter beats lower).
3. Category overrides in Visibility/Graphics.
4. Object Styles (project-wide defaults for each category and subcategory).

Related controls that can also change appearance: phase graphic overrides, workset visibility, link display settings (by host view, by linked view, custom), halftone, underlay, and design option visibility. Keep element overrides rare: they are invisible to others and do not travel with templates [S47].

## 5. Detail level

- Coarse, Medium, Fine. Families and wall/floor types show different geometry or fills per level [S25].
- Typical mapping (template choice): 1:200 and smaller coarse; 1:100 coarse or medium; 1:50 medium; 1:20 and larger fine.
- Exports: IFC from views at Fine, otherwise elements visible only at Fine are missing [S59][S22] (RV-051).

## 6. View range

Top, Cut Plane and Bottom define the primary range; View Depth extends below it [N11].

| Zone | Display [N11] |
|---|---|
| At the cut plane | Cuttable elements drawn with the category's cut line weight |
| Below cut, above bottom | Projection line weight |
| Below bottom, within view depth | <Beyond> line style regardless of category |
| Above cut, below top | Not shown in plan, except windows, casework and generic models |

Exceptions [N11]: floors, structural floors, stairs and ramps use an adjusted range 4 ft (about 1.22 m) below the bottom of the primary range; walls shorter than 6 ft (2 m) are not cut even if they intersect the cut plane; non-cuttable families intersecting the cut plane use projection weight.

Typical plan settings (confirm with the template): cut plane about 1200 mm / 4'-0" above the level, top at the level above, bottom at the level, view depth at the level (or below for split levels). Use plan regions for local exceptions (a high window, a split level), but keep them few [S24][S25].

## 7. Element not visible: diagnosis order

Work from cheap and common to rare. Stop when you find the cause and report it.

1. **Is it in this view's extent?** Crop region, annotation crop, far clip (sections/elevations), scope box.
2. **View range** (plans): element above the top or below the view depth; cut plane relative to its height (section 6).
3. **Phase**: element created in a later phase than the view's phase, or demolished in an earlier one; or the phase filter hides that status (section 9).
4. **Design option**: element in a non-primary option not shown in this view.
5. **Category visibility**: category or subcategory off in Visibility/Graphics, or controlled by the view template.
6. **Filters**: a filter set to invisible matches the element.
7. **Hidden in view**: element or category hidden individually. Use Reveal Hidden Elements to see it.
8. **Workset**: workset closed, or not visible in this view.
9. **Detail level**: geometry only exists at Fine (or Coarse) in the family.
10. **Discipline setting**: e.g. a structural discipline view hides some architectural content.
11. **Link settings**: link hidden, displayed "by linked view", or the linked element's own visibility.
12. **Overrides making it look invisible**: white lines, transparency 100, halftone over a dark background.
13. **Element not hosted where expected**: wrong level, huge offset, placed in another view (view-specific detail items), or deleted.
14. **Graphics display**: model too far from origin, or a temporary hide/isolate left active.

## 8. Crop regions, scope boxes, dependent views

- Crop every documentation view to what it shows; turn the crop region frame off on sheets (a published checkset checks this) [S31].
- Scope boxes: created in the architectural model and shared with all models so crops and grid extents match [S22]. Name them by area ("SB - Zone A").
- Dependent views split one plan across sheets with matching crops; the parent holds the template and annotation shared by all [S22].
- Annotation crop keeps tags and dimensions from spilling across match lines.

## 9. Phase filters

| Filter (default names) | Shows |
|---|---|
| Show All | Existing, demolished, new; with graphic distinctions |
| Show Complete | Everything as built at the view's phase, no overrides |
| Show Demo + New | Demolished and new |
| Show New | New only |
| Show Previous + Demo | Existing and demolished |
| Show Previous + New | Existing and new (typical construction plan) |

Set the view's phase and phase filter together. Demolition plans: phase = construction phase, filter = previous + demo. Proposed plans: phase = construction phase, filter = previous + new.

## 10. Checks

| Check | Signal |
|---|---|
| Sheet views without templates | View on a sheet with no template [S91] |
| Hidden elements | Views with elements hidden by element [S91] |
| Element overrides | Count of per-element graphic overrides per view [S47] |
| Default view names | "Section 1", "Copy of" [S23] |
| Views not on sheets | Working views piling up at issue [S23][S46] |
| Plan regions | Count above project norm [S24] |
| Crop frames visible on sheets | Crop region visible = on [S31] |
| IFC export view detail level | Not Fine [S59] |
