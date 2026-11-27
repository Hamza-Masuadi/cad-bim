# Families and parameters (Revit)

Read this before loading, creating, editing, naming or checking families, types and parameters. Source tags point to `_SOURCES.md`.

Contents: 1 Family kinds · 2 Category · 3 Type vs instance · 4 Parameter kinds · 5 Naming · 6 Nesting · 7 Subcategories and graphics · 8 Type catalogs · 9 File hygiene · 10 Checks

## 1. Family kinds

| Kind | Examples | Created in | Use |
|---|---|---|---|
| System families | Walls, floors, roofs, ceilings, stairs, railings, levels, grids, views | The project (types duplicated and edited there) | Building elements that are assembled in place; types transfer with Transfer Project Standards |
| Loadable families (.rfa) | Doors, windows, furniture, fixtures, equipment, annotation tags, title blocks, detail components | Family Editor | Everything reusable |
| In-place families | One-off modelled elements | The project | Last resort: avoid [S22][S23][S36] |

In-place rules: each copy is a separate definition (bloat) [S23][S36]; it cannot later be converted to a loadable family [S35]; give it the right category and shared parameters if it must exist [S22]. If you need more than one copy, make a loadable family.

## 2. Category: why it matters

Category fixes, for every family [S69][S70]:
- which parameters exist (e.g. doors have Width and Height, rooms have Number),
- which schedules and tags can see it,
- object styles, filters and visibility/graphics,
- IFC and COBie mapping,
- hosting, cutting and MEP connectors.

Consequences: a plumbing fixture made as Specialty Equipment never appears in plumbing schedules or systems [S70]; anything left as Generic Models is invisible to the right schedule and exports as a proxy (RV-002, RV-039). Changing category later can break hosting, cuts and connectors [S70]. Choose the category first, from the project's list of what will be scheduled and exported.

QA trick: keep a "Generic Models - To Reclassify" schedule and a multi-category QA schedule to catch miscategorised content [S70].

## 3. Type vs instance parameters

| Make it a type parameter when | Make it an instance parameter when |
|---|---|
| All instances with that value are the same product (size, material, fire rating, manufacturer, model) | It varies per placement (Mark, sill height, offset, room-specific comments, handing) |
| It should be scheduled by type and changed once | Each instance must be set individually |

Rules:
- Sizes of manufactured products (door leaf width, height) are type parameters: a 900 and a 1000 door are different types. Why: schedules count by type; procurement buys types.
- Do not use instance parameters to make one type "flexible" when the result is a different product. You lose type counts and schedules lie.
- Type Mark identifies the type in tags and schedules (windows, wall types); Mark identifies the instance (doors). Revit warns on duplicate Type Mark [N12][S23].

## 4. Parameter kinds

| Kind | Scope | Taggable | Schedulable | Use |
|---|---|---|---|---|
| Family parameter | Inside one family | No | No (only via built-in or shared) | Internal geometry drivers |
| Shared parameter | Definition in a shared parameter file (GUID), reused across families and projects | Yes | Yes | Anything to tag or to schedule consistently across families and projects [S68][S22] |
| Project parameter | One project, bound to categories | No (unless based on a shared parameter) | Yes | Project-only data (e.g. a QA flag) |
| Global parameter | Project-wide, not bound to a category | No | Reported values | Drive or report project-wide values (e.g. standard sill height) [S68] |

Decision rules:
- Will it appear in a tag, or in a schedule that mixes loaded families? Use a **shared** parameter, from the project's or office's shared parameter file. Never create a second shared parameter with the same name and a different GUID; schedules treat them as different fields.
- Only needed in this project's schedules? A project parameter is enough.
- Name and data type (text, length, number, yes/no) must match the standard; a length stored as text cannot be totalled.
- NY OGS template parameters are UPPERCASE with a `PREFIX_Description` pattern [S22]; follow the office pattern.
- Note the Revit API changes for parameters across 2022-2025 (spec and group identifiers); that is the engine's concern, not a modelling rule [R04].

## 5. Naming

| Item | Convention examples |
|---|---|
| Family file | NY OGS: `Originator_CategoryAbbrev_Description[_Description2].rfa`, e.g. `OGS_AN_Callout Head.rfa`; category codes such as DR doors, WI windows, CA casework, LF lighting fixtures, PF plumbing fixtures, GM generic models, TB title blocks [S22] |
| Family (NBS BIM Object Standard, archived) | `Role_Source_Type_Subtype[_Differentiator]`, underscores; source omitted for generic objects; abbreviations up to 7 uppercase characters; material names suffixed `_mtrl` [S71] |
| Type | Describe the variant, not repeat the family: `0915 x 2134`, `3'-0" x 7'-0"`, `1HR`, `¼" Head` [S22][S72] |
| Never | `Copy`, `Type 1`, `Wall 2 2 2`, spaces at the end, special characters that break exports (RV-027) [S37] |

## 6. Nesting

- A **shared** nested family can be selected, tagged and scheduled on its own; a non-shared one behaves as part of the host [S69]. Set the nested family as shared before nesting it if it must be counted (e.g. a door's vision panel or a desk's chair).
- Keep nesting shallow; each level adds regeneration cost and makes edits harder.
- Associate nested parameters to host parameters for sizes; do not duplicate logic.

## 7. Subcategories and graphics

- Use subcategories for graphic control (door panel, frame, swing; casework hidden lines), not new categories [S70]. Subcategories appear in Object Styles and Visibility/Graphics so a view template can control them.
- Use symbolic lines and masking regions for plan-only graphics (door swings, furniture plan symbols) [S36]; model geometry only what must appear in 3D.
- Detail levels: assign geometry to Coarse/Medium/Fine deliberately so small-scale views stay light [S25].
- Reference planes named and with "Is Reference" set so dimensions and alignments snap to the right faces.
- Lock constraints in the Family Editor, not in the project [S23].

## 8. Type catalogs

For families with many sizes, a type catalog (a text file with the same name as the family, in the same folder) lets the user load only the types needed instead of carrying all of them. Format (stated from product knowledge; the format was not researched first-hand): comma-separated, first row lists the parameters with name, type and units, e.g. `,Width##LENGTH##MILLIMETERS,Height##LENGTH##MILLIMETERS`, then one row per type starting with the type name. Test by loading one type and reading its parameters back.

## 9. File hygiene

- Vet manufacturer and downloaded families before loading: purge, remove advertising and junk parameters, check category, size and detail levels [S34][S35].
- Avoid unnecessary voids, arrays, formulas and families that cut hosts [S36]; avoid over-modelling small detail (skip items under about 100 mm / 4" unless needed, practitioner guidance) [S34].
- Purge unused types and nested families inside the family before saving.
- Keep the family's insertion point and orientation consistent (doors: insertion at the wall centre and the opening centre; furniture: at a corner or centre per library rule).
- Reload with "overwrite parameter values" only when you mean to reset project changes.
- Test every new family in a test project: place, flex parameters, tag, schedule, cut in plan and section, export to IFC if required.

## 10. Checks

| Check | Signal |
|---|---|
| In-place families | Count of in-place families; several with identical geometry [S23] |
| Wrong category | Generic Models instances; category vs name keywords mismatch [S70] |
| Shared parameters consistent | Same parameter name with different GUIDs; tag labels using non-shared parameters |
| Type names clean | Default or numbered-copy names [S37] |
| Nested families shared where scheduled | Sub-components missing from schedules [S69] |
| Heavy families | Large family files; many parameters; tiny solids [S34][S36] |
| Duplicate Type Marks | Revit duplicate value warning |
| Orphaned families after paste | Families or types with "1"/"2" suffixes [S75] |
