# Design data: defaults and flags with sources

Dimensional data for stairs, ramps, doors, routes, toilets, reach, parking, lifts, dwelling sizes, escape headlines, sheets and scales, and for interior work: furniture, room sizes, kitchens and bathrooms, workplace space, fit-out counters and seating, ceilings and area measurement. Every number is tied to a public source, an edition, a clause and a jurisdiction. Use the numbers as **starting defaults** and as **flags**. They are never a compliance certificate.

## Contents

1. [Standing caveat](#1-standing-caveat)
2. [The data files](#2-the-data-files)
3. [Entry fields and how to read them](#3-entry-fields-and-how-to-read-them)
4. [Picking a jurisdiction](#4-picking-a-jurisdiction)
5. [How to word a flag](#5-how-to-word-a-flag)
6. [Most-used figures: England](#6-most-used-figures-england)
7. [Most-used figures: United States](#7-most-used-figures-united-states)
8. [Sheets and scales](#8-sheets-and-scales)
9. [What is not in the data](#9-what-is-not-in-the-data)

## 1. Standing caveat

Say this, in your own words, the first time design data shapes a drawing in a session, and again whenever you flag something:

> These figures come from published guidance (named with each figure). The code adopted for this project, any local amendments, and the judgement of a qualified professional and the building control body or authority having jurisdiction govern compliance. I am using these figures as defaults and checks, not certifying anything.

- England's Approved Documents are guidance: following them creates a presumption of compliance, but other solutions can comply.
- US entries marked `secondary` come from city or county handouts that restate ICC model codes. Editions differ and states amend. Check the adopted edition.
- Means of escape (`escape.json`) is specialist work. Use it only to spot obvious problems early. Never size a fire strategy from it.
- Where two sources disagree (for example AD K vs AD M for external steps), say so. AD M states that where there appears to be a conflict, Part M takes precedence (AD M vol 2, notes before 1.19 and 1.27).

## 2. The data files

All files are in `data/design/` (relative to the skill root).

| File | Covers |
|---|---|
| `stairs.json` | Rise, going, 2R+G, pitch, widths, headroom, landings, risers per flight, handrails, guarding |
| `ramps.json` | Gradient by going/rise, widths, landings, kerbs/edge protection, handrails |
| `doors.json` | Clear widths, ADA manoeuvring clearances by approach, thresholds, hardware, forces, nibs |
| `routes.json` | Corridor/route widths, passing places, turning circle and T-turn, clear floor space, slopes, lobbies |
| `sanitary.json` | Accessible WC rooms and compartments, grab bars, seat and basin heights, Changing Places triggers |
| `reach.json` | Reach ranges, switches and sockets, counters and work surfaces |
| `parking.json` | Accessible bays, aisles, counts by total (ADA), standard bay |
| `lifts.json` | Car sizes, doors, landings, control heights |
| `dwellings.json` | NDSS GIA and storage by bedrooms/persons, bedroom sizes, ceiling heights, M4(2)/M4(3) items, US room minimums |
| `escape.json` | AD B vol 2 travel distances, exit widths, number of exits, final-exit formula (headline only) |
| `sheets-scales.json` | ANSI and ISO A sheet sizes, imperial and metric drawing scales, minimum text height |
| `furniture.json` | AD M vol 1 Appendix D furniture sizes, bedroom manoeuvring square, living turning circle and ellipse, M4(2)/M4(3) bed access zones, ADA guest room beds, NSW wardrobe sizes |
| `rooms.json` | M4(3) bedroom areas and widths, living/dining/kitchen areas by bedspaces, NSW Apartment Design Guide room and apartment sizes and depths, Massachusetts room minimums |
| `kitchens-bathrooms.json` | ADA kitchens, mirrors, baths and showers, compartment toe clearance; M4(3) kitchen clear zone, worktop lengths and accessible kitchen items; Appendix D sanitary fitting sizes; Massachusetts kitchen passage |
| `workplace.json` | HSE workroom space per person (Great Britain), OSHA desk guidance (advisory), ADA share of accessible work and dining surfaces |
| `fitout.json` | ADA assembly wheelchair spaces, check-out and service counters |
| `ceilings.json` | NSW and Massachusetts ceiling heights, UFGS ceiling unit sizes, border-unit rule, access panel size |
| `measurement.json` | RICS headroom and narrow-space rules for NIA, GIA, EFA, NSA, residential measuring height; Massachusetts low-ceiling exclusion |
| `materials.json` | UFGS finish unit sizes: VCT, LVT, rubber, linoleum, sheet widths, carpet and carpet tile, porcelain, gauged porcelain, quarry, mosaic, glass and wall tile, tile joint on mortar bed, gypsum board width, wood strip width, modular/utility brick, brick and CMU joints, resilient edge-tile rule |

Each file is `{ "topic", "retrieved", "entries": [...] }`. Load the file for the topic, filter by `jurisdiction` and `applies_to`, then use the entry. Do not quote a figure from memory when a file has it; read the file.

## 3. Entry fields and how to read them

| Field | Meaning |
|---|---|
| `id` | Stable key: `topic.jurisdiction.scope.quantity[.min/.max]` |
| `value`, `unit` | The figure in metric (mm, m, m2, N) or a unitless type (`1:n`, `deg`, `count`, `risers`, `persons`, `percent`, `s`, `scale factor`, `mm per person`) |
| `min`, `max` | Which side is the limit. A `min` entry: design must be at least this. A `max` entry: at most this. Both set: a range; `value` then shows the lower bound. Both null: nominal, preferred or rule entry |
| `imperial` | Same figure in inches/feet/sq ft/lbf. `converted: false` means the source itself states both units. `converted: true` means one of the two figures is arithmetic (the note says which when the source is imperial-only) |
| `formula` | A rule, with variables explained in `note` (2R+G, lobby length, final exit width, parking counts) |
| `confidence` | `read_in_source` (read in the primary text) or `secondary` (read in a government handout restating a model code) |
| `source` | Title, publisher, edition, clause, URL |

Gradients use unit `1:n` with value `n`. Steeper means smaller `n`, so "maximum gradient 1:12" is stored with `min: 12` (n must be 12 or more). Read the note when in doubt.

When reporting a converted figure, say it is converted ("220 mm, about 8.7 in"). When the source gives both units (ADA), quote the source's own pair ("36 in (915 mm)"); do not recompute, because ADA rounds its metric values.

## 4. Picking a jurisdiction

- **Ask the user** which code applies before using design data for anything beyond a rough default. Never infer it from the drawing units, the file's language, or where the user seems to be.
- Useful question: "Which building code and edition apply to this project (for example England's Approved Documents, a US state's adopted IBC/IRC plus the ADA, or another country)?"
- If the user does not know yet, offer defaults from one data set, label them clearly as provisional ("using England AD K/M figures as placeholders until you confirm the code"), and keep asking at natural points.
- Accessibility in the US usually means **both** the ADA Standards (federal civil-rights law) and the locally adopted building code (often IBC with ICC A117.1). The data has the ADA text only; A117.1 was not available.
- In England, check whether the planning permission sets optional requirements M4(2) or M4(3) and whether the local plan adopts the Nationally Described Space Standard. Without those conditions, only M4(1) and the base requirements apply.
- Dwelling vs non-dwelling matters: AD K distinguishes private, utility and general access stairs; the US splits IRC (one- and two-family dwellings) from IBC (everything else).
- Other jurisdictions (Scotland, Wales, Northern Ireland, Australia, Canada, EU states) are **not** in the data, with two narrow exceptions: apartment room and ceiling figures from the NSW Apartment Design Guide (Australia - New South Wales), and room and ceiling figures from the Massachusetts building code (a US state's adopted code). Workplace space per person is Great Britain (HSE). Say so for anything else and do not borrow England or US figures as if they applied there.
- Federal guide specifications (UFGS) and federal agency standards apply to those agencies' projects; quote them elsewhere only as a published reference, not a requirement.

## 5. How to word a flag

A flag states the measurement, the reference figure, the source and clause, the jurisdiction, and what to do next. Keep it factual and short.

Template:

> [Element] is [measured value] ([converted value]). [Source, clause] ([jurisdiction]) gives [min/max/range] [figure] for [applies_to]. Worth checking against the adopted code for this project.

Examples:

- "Corridor C-104 is 1100 mm clear (about 43.3 in). AD M vol 2 para 3.14(b) (England) gives 1200 mm minimum unobstructed width for corridors in buildings other than dwellings. Worth checking."
- "Stair S2 risers are 7.5 in (190.5 mm). The 2010 ADA Standards 504.2 (US) set risers at 4 to 7 in (100 to 180 mm) for stairs that must comply. If this stair is a required means of egress, the adopted IBC also governs."
- "Ramp R1 rises 750 mm over a 9 m going: about 1:12. AD M vol 2 Table 1 (England) allows about 1:19 at a 9 m going and no flight rising more than 500 mm."

Rules for flags:

- Flag only what you measured or computed from the model or drawing. Say how you measured (clear width between finishes, structural opening, door leaf).
- Use "worth checking", "narrower than", "steeper than". Never "non-compliant", "fails code", "approved" or "compliant".
- If the entry is `secondary`, add "(from a local government handout restating the IRC/IBC; edition may differ)".
- If the element's use is unclear (private vs general stair, public vs staff door), state the assumption.
- Group several flags into one short list rather than interrupting repeatedly.

## 6. Most-used figures: England

Sources: AD K 2013 edition; AD M vol 1 2015 (with 2016 amendments); AD M vol 2 2015 (with 2020 and 2024 amendments); NDSS March 2015 (notes May 2016); AD B vol 2 2019 (with amendments to 2026). Imperial in the files is converted.

**Stairs (AD K Table 1.1)**

| Stair category | Rise mm | Going mm | Other |
|---|---|---|---|
| Private (one dwelling) | 150-220 | 220-300 | Pitch max 42 deg |
| Utility | 150-190 | 250-400 | Max 16 risers per flight (1.18) |
| General access | 150-170 | 250-400 | Max 12 risers per flight, exceptionally 16 (1.18) |
| All | 2R + G between 550 and 700 | | Change direction after 36 risers (1.17) |

| Item | Figure | Clause |
|---|---|---|
| Headroom | 2000 mm min (loft conversion: 1900 centre, 1800 side) | AD K 1.11, 1.13 |
| Stair width, non-dwellings | 1200 mm min between walls; 1000 between handrails; divide if over 2000 | AD K 1.15 |
| Landing length, non-dwellings | 1200 mm min unobstructed; all landings at least flight width | AD K 1.23, 1.20 |
| Handrail height | 900-1000 mm above pitch line; landings 900-1100 (non-dwellings) | AD K 1.34, Diagram 1.12 |
| Handrail both sides | stair 1000 mm wide or more | AD K 1.34(c) |
| Guarding height | dwellings 900 (stairs, landings, floors), 1100 (external balconies); non-dwellings 900 on flights, 1100 elsewhere | AD K Diagram 3.1 |
| Guarding trigger | dwellings: drop over 600 mm; non-dwellings: two or more risers | AD K 1.41, 1.40 |
| External stepped access | rise 150-170, going 280-425, width 1200, 12 risers (going < 350) or 18 (going >= 350) | AD M vol 2 1.33 |

**Ramps (AD K Diagram 2.1 / AD M vol 2 Table 1)**

| Going of flight | Max gradient | Max rise |
|---|---|---|
| 2 m | 1:12 | 166 mm |
| 5 m | 1:15 | 333 mm |
| 10 m | 1:20 | 500 mm |

Interpolate between 2 m and 10 m. Width 1500 mm min; end landings 1200 long; intermediate 1500; passing landings 1800 x 1800; kerb 100 mm on open sides; steps too when rise exceeds 300 mm; 1:20 or steeper is a ramp (AD K 2.3-2.13, AD M vol 2 1.26).

**Doors, routes, sanitary, lifts (buildings other than dwellings, AD M vol 2)**

| Item | Figure | Clause |
|---|---|---|
| Door effective clear width, new | 800 straight-on; 800 off a 1500 route; 825 off a 1200 route; 1000 external public | Table 2 |
| Leading-edge space (pull side) | 300 mm | 3.10(c) |
| Opening force | 30 N (0-30 deg), 22.5 N (30-60 deg) | 3.10(a) |
| Threshold | 15 mm max total | 2.7(e) |
| Corridor | 1200 mm min; passing places 1800 x 1800 where narrower than 1800 | 3.14(b),(c) |
| Turning space | 1500 x 1500 mm | 3.28(a), Diagram 18 |
| Unisex accessible WC | 1500 wide x 2200 deep min (2000 wide if the only WC); 40 m max travel | Diagram 18, 5.10 |
| WC seat / grab rails | 480 / 680 mm | Diagram 19 |
| Ambulant cubicle | 1465 deep x 920 wide min | Diagram 21 |
| Lift car | 1100 wide x 1400 deep min; door 800; landing 1500 x 1500 | 3.34, 3.28 |
| Sockets / switches | 400-1000 / 400-1200; precise controls 750-1200 | 4.30 |
| Disabled parking bay | 2400 x 4800 plus 1200 zones | Diagram 2, 1.18 |

**Dwellings (AD M vol 1, NDSS)**

| Item | Figure | Clause |
|---|---|---|
| M4(2) hall width | 900 mm min | 2.22(a) |
| M4(2) door widths | Table 2.1 pairs: door 750 head-on with 900 corridor; 750/1200, 775/1050, 800/900 not head-on | Table 2.1 |
| M4(2) entrance door | 850 mm clear; 300 nib | 2.20 |
| M4(3) halls / doors | 1050 (1200 not head-on) / 850 | 3.24 |
| Standard parking bay | 2.4 x 4.8 m | Appendix A |
| NDSS bedrooms | single 7.5 m2 and 2.15 m wide; double 11.5 m2, one at 2.75 m wide, others 2.55 m | NDSS 10(c)-(e) |
| NDSS ceiling | 2.3 m over at least 75% of GIA | NDSS 10(i) |
| NDSS GIA examples | 1b2p flat 50 m2; 2b4p flat 70; 3b5p 2-storey house 93 | NDSS Table 1 |

**Escape headline (AD B vol 2, specialist work)**: office/shop travel 18 m one direction, 45 m more than one (Table 2.1); exit widths 750 (up to 60 people), 850 (110), 1050 (220), then 5 mm per person (Table 2.3).

## 7. Most-used figures: United States

ADA = 2010 ADA Standards (DOJ), which state both units. IRC/IBC rows are `secondary` (city/county handouts) and give inches only.

| Item | Figure | Source |
|---|---|---|
| Stair riser / tread (ADA) | 4-7 in (100-180 mm) / 11 in (280 mm) min; no open risers | ADA 504.2, 504.3 |
| Handrail height | 34-38 in (865-965 mm) | ADA 505.4 |
| Handrail extension | 12 in (305 mm) at top; one tread depth at bottom | ADA 505.10 |
| Headroom on circulation paths | 80 in (2030 mm) | ADA 307.4 |
| Ramp slope / rise per run | 1:12 max / 30 in (760 mm) | ADA 405.2, 405.6 |
| Ramp width / landing | 36 in (915 mm) / 60 in (1525 mm) long | ADA 405.5, 405.7 |
| Door clear width | 32 in (815 mm) min | ADA 404.2.3 |
| Door threshold / hardware | 1/2 in (13 mm) / 34-48 in (865-1220 mm) | ADA 404.2.5, 404.2.7 |
| Front approach, pull side | 60 in (1525) deep, 18 in (455) beyond latch | ADA Table 404.2.4.1 |
| Front approach, push side | 48 in (1220) deep, 0 (add 12 in if closer and latch) | ADA Table 404.2.4.1 |
| Route width | 36 in (915 mm); passing space 60 x 60 in every 200 ft (61 m) | ADA 403.5 |
| Turning space | 60 in (1525 mm) circle or T in 60 in square | ADA 304.3 |
| Clear floor space | 30 x 48 in (760 x 1220 mm) | ADA 305.3 |
| Reach (unobstructed) | 15-48 in (380-1220 mm) | ADA 308.2.1, 308.3.1 |
| WC compartment | 60 in wide x 56 in (wall-hung) / 59 in (floor) deep | ADA 604.8.1.1 |
| WC centreline / seat | 16-18 in (405-455) from side wall / 17-19 in (430-485) | ADA 604.2, 604.4 |
| Grab bars | 33-36 in (840-915 mm) high; side 42 in, rear 36 in long | ADA 609.4, 604.5 |
| Lavatory rim | 34 in (865 mm) max | ADA 606.3 |
| Parking | car 96 in (2440), van 132 in (3350), aisle 60 in (1525); count Table 208.2 | ADA 502, 208.2 |
| Elevator (centred door) | 80 x 51 in car, 42 in door (Table has other options) | ADA Table 407.4.1 |
| IRC stair (secondary) | riser 7 3/4 in max, tread 10 in min, width 36 in, headroom 6 ft 8 in, handrail 34-38 in, guard 36 in | Boise #437, Des Moines (2015 IRC) |
| IBC stair (secondary) | riser 4-7 in, tread 11 in, width 44 in (36 in for 50 or fewer occupants), rise 12 ft per flight, guard 42 in | Humboldt County (IBC 2018) |
| IRC rooms (secondary / Seattle) | habitable room 70 sq ft with 7 ft min dimension; ceilings 7 ft (bathrooms 6 ft 8 in) | Seattle SRC R304; Boise #434 |

ADA parking counts (Table 208.2): 1-25: 1; 26-50: 2; 51-75: 3; 76-100: 4; 101-150: 5; 151-200: 6; 201-300: 7; 301-400: 8; 401-500: 9; 501-1000: 2% of total; over 1000: 20 plus 1 per 100 or fraction over 1000. One in six accessible spaces (or fraction) is a van space.

## 8. Sheets and scales

Source: USACE A/E/C Graphics Standard Release 2.2 (2023), Table 2-1, Tables 5-4 and 5-5.

| ANSI | in | ISO | mm |
|---|---|---|---|
| A | 8.5 x 11 | A4 | 210 x 297 |
| B | 11 x 17 | A3 | 297 x 420 |
| C | 17 x 22 | A2 | 420 x 594 |
| D | 22 x 34 | A1 | 594 x 841 |
| E | 34 x 44 | A0 | 841 x 1189 |
| F | 28 x 40 | | |

- Common imperial scales (factor): 1/8" = 1'-0" (96), 1/4" (48), 1/2" (24), 1" (12), 1-1/2" (8), 3" (4); engineering 1" = 10' (120) to 1" = 2000' (24000).
- Common metric scales: 1:1, 1:2.5, 1:5, 1:10, 1:20, 1:30, 1:40, 1:50, 1:60, 1:100, 1:200, 1:400, 1:500, 1:600, 1:700, 1:1000 and larger site scales.
- Minimum plotted text: 3/32 in (2.4 mm); model text height = plotted height x scale factor.
- ARCH (architectural) sheet sizes are not in the data: no public government source was opened for them. Do not supply them from memory as sourced figures.

## 9. What is not in the data

- ICC model code text itself (IBC, IRC, ICC A117.1): copyrighted, viewer not reachable. Only handout restatements.
- Fair Housing Act design requirements, US state accessibility codes (for example California CBC 11A/11B), and US egress widths and travel distances.
- Scotland, Wales, Northern Ireland, Ireland, Australia beyond the NSW Apartment Design Guide (the NCC was not read), Canada, ISO 21542 (paywalled), BS 8300 (paywalled), Approved Document T.
- General (non-accessible) car-park geometry beyond the 2.4 x 4.8 m standard bay; Manual for Streets was not opened.
- Commercial furniture and desk sizes, office aisle widths, workplace densities, cabinet module sizes, light fitting spacing, hotel room and retail fixture sizes, planting densities: no public source; they are conventions in the practice guides or come from the brand, product or designer.
- AD M vol 1 Appendix D: the 8-bedspace dining table length and the storage-unit lengths (not legible in the extraction).
- Trade-association kitchen and bath planning guidelines, BOMA and IPMS definitions (paywalled or not read); BS 5837 tree protection; US sign mounting heights (ADA 703, not transcribed).
- If the user needs any of these, say the data does not cover it and ask them for the adopted document or a figure they want to use, and record their figure with its source in the project notes.
