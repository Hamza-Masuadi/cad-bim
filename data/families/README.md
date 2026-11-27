# Family recipes

Each file is a ready `args` object for the Revit op `family.create`. Put it in a job as the only op, with
`"units": { "length": "mm" }` (every length here is in millimetres), and set `outputDir` if the job's output folder is
not where the `.rfa` should go. Recipe 3.35 in `references/revit.md` shows the job, and a second job that places the
family.

Every recipe follows the same layout:

- `Width` is centred on `Center (Left/Right)` (an equality dimension keeps it centred).
- The back is on `Center (Front/Back)` and the front is at `-Depth`, so the family's origin sits on the wall line.
  Free-standing tables and the shower tray are centred in depth instead.
- `Height` (or the named overall size) is measured from `Ref. Level`.
- Parts are boxes locked to reference planes. Those planes are driven by labelled dimensions and, for inner planes,
  by formulas (for example `Top Underside = Height - Top Thickness`).
- Basins, the vanity, the bathtub and the shower tray use a void box to cut the bowl or recess.
- Materials are type parameters, created in the family when missing.
- Plan symbolic lines outline the item.
- `flex` names the overall sizes, so every type is regenerated and measured before the family is saved.
- The text type parameter `Size Source` records where each type's sizes come from: a `data/design` id, or
  "typical, verify" when no dataset gives the size. Check those before using the family on a project.

| Item | File | Category | Types | Sizes from |
|---|---|---|---|---|
| Double bed | `hotel-double-bed.json` | Furniture | Double 1500x2000, Double 1350x1900, King 1800x2000 | `furniture.uk.adm1.double_bed_principal`, `furniture.uk.adm1.double_bed_other`; king size and heights typical, verify |
| Single bed | `hotel-single-bed.json` | Furniture | Single 900x1900, Single 900x2000, Small Double 1200x1900 | `furniture.uk.adm1.single_bed`; others typical, verify |
| Nightstand | `hotel-nightstand.json` | Furniture | 400x400, 450x400, 500x450 | `furniture.uk.adm1.bedside_table`; others typical, verify |
| Wardrobe | `hotel-wardrobe.json` | Furniture | Double 1200, Main 1800, Single 900 | `furniture.uk.adm1.double_wardrobe`, `furniture.au_nsw.adg.main_wardrobe`; single typical, verify |
| Desk | `hotel-desk.json` | Furniture | 1050x500, 1200x600, 1400x700 | `furniture.uk.adm1.desk_and_chair`; others typical, verify |
| Desk chair | `hotel-desk-chair.json` | Furniture | 500x500, 550x550, 600x600 | typical, verify |
| Armchair | `hotel-armchair.json` | Furniture | 850x850, 750x800, 950x900 | `furniture.uk.adm1.armchair`; others typical, verify |
| 2-seat sofa | `hotel-sofa-2-seat.json` | Furniture | 1300x850, 1500x850, 1600x900 | `furniture.uk.adm1.settee_2seat`; others typical, verify |
| 3-seat sofa | `hotel-sofa-3-seat.json` | Furniture | 1850x850, 2000x900, 2200x900 | `furniture.uk.adm1.settee_3seat`; others typical, verify |
| Coffee table | `hotel-coffee-table.json` | Furniture | 1050x500, 900x500, 1200x600 | `furniture.uk.adm1.coffee_table`; others typical, verify |
| Dining table | `hotel-dining-table.json` | Furniture | 4 Person 1200x800, 2 Person 800x800, 6 Person 1500x800 | `furniture.uk.adm1.dining_table` (.2p, .4p, .6p, .depth); height typical, verify |
| Dining chair | `hotel-dining-chair.json` | Furniture | 450x500, 420x480, 500x550 | typical, verify |
| TV unit | `hotel-tv-unit.json` | Furniture | 1500x450, 1200x450, 1800x500 | typical, verify |
| Luggage rack | `hotel-luggage-rack.json` | Furniture | 700x450, 600x400, 900x500 | typical, verify |
| WC | `wc-close-coupled.json` | Plumbing Fixtures | 500x700 Seat 430, 500x700 Seat 480, 450x650 Compact | `kitbath.uk.adm1.wc_cistern`, `sanitary.us.ada.wc_seat.height`, `sanitary.uk.unisex_wc.seat.height`; compact typical, verify |
| Wall-hung basin | `basin-wall-hung.json` | Plumbing Fixtures | 600x450 Rim 800, 600x450 Rim 730, 500x400 Rim 800 | `kitbath.uk.adm1.wash_basin`, `sanitary.uk.basin_standing.rim`, `sanitary.uk.basin_seated.rim`; 500x400 typical, verify |
| Vanity with basin | `vanity-unit-with-basin.json` | Plumbing Fixtures | 900x500, 600x450, 1200x500 | typical, verify (rim kept under `sanitary.us.ada.lavatory_rim.max`) |
| Shower tray | `shower-tray.json` | Plumbing Fixtures | 900x900, 915x915 Transfer, 1200x800 | `kitbath.us.ada.shower_transfer.size`; others typical, verify |
| Bathtub | `bathtub.json` | Plumbing Fixtures | 1700x700, 1600x700, 1800x800 | `kitbath.uk.adm1.bath`; others and height typical, verify |
| Kitchen base unit | `kitchen-base-unit.json` | Casework | 600, 300, 1000 | typical, verify |
| Kitchen wall unit | `kitchen-wall-unit.json` | Casework | 600, 300, 1000 | typical, verify (mounting height is an instance parameter) |
| Worktop | `kitchen-worktop.json` | Casework | 1200x600 at 900, 2400x600 at 900, 1200x600 at 865 Accessible | `kitbath.us.ada.kitchen.work_surface.height.max`; others typical, verify |

The engine has built none of these in Revit yet. Lint (and the tests in `SA.Core.Tests/FamilyCreateTests.cs`) only
proves that each recipe is self-consistent: every name it uses is declared, every face starts on its plane, and every
size is driven. The flex test in Revit is what proves each family flexes.
