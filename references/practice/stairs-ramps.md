# Stairs and ramps

Read this before designing, drawing, modelling or checking a stair or ramp. **The project's building code governs.** The values below are quoted from named codes so you can compute a plausible stair and flag likely problems. Confirming compliance is the user's responsibility; say once that you have not certified it.

Contents: 1 Vocabulary · 2 Code tables by jurisdiction · 3 Computing a stair · 4 Landings, headroom, handrails, guards · 5 Ramps · 6 Plan conventions · 7 Revit stairs · 8 Failure points and checks

## 1. Vocabulary

| Term | Meaning |
|---|---|
| Rise (R), riser height | Vertical distance nosing to nosing |
| Going (G, UK) / tread depth (US) | Horizontal distance nosing to nosing, measured on the walking line |
| Flight / run | Unbroken series of steps between landings |
| Total rise | Finished floor to finished floor (FFL to FFL), not structure to structure |
| Pitch | Angle of the pitch line through the nosings |
| Nosing projection | How far the tread edge overhangs the riser below |

Always work from **finished** floor levels. Stair maths on structural levels gives a wrong top or bottom riser once screeds and finishes go on.

## 2. Code tables

### 2.1 England: Approved Document K, 2013 edition [N02]

Table 1.1, rise and going (mm):

| Stair category | Rise min | Rise max | Going min | Going max |
|---|---|---|---|---|
| Private stair | 150 | 220 | 220 | 300 |
| Utility stair | 150 | 190 | 250 | 400 |
| General access stair | 150 | 170 | 250 | 400 |

Notes from the table: private stair maximum pitch 42°; for schools the preferred going is 280 and rise 150; **2R + G between 550 and 700 mm**.

Other ADK rules: max risers per flight for buildings other than dwellings and common access areas in buildings containing flats, utility 16, general access 12 (exceptionally 16 in small premises) (1.18); no single steps in those buildings (1.18); more than 36 risers in consecutive flights need at least one change of direction (1.17); non-dwelling flights at least 1200 mm between enclosing walls and 1000 mm between handrails, flights over 2 m wide divided (1.15); escape stairs also follow Approved Document B.

### 2.2 United States

IBC 2021, as reproduced in ICC's 2021 Group A proposed-changes document [N04]:

| Item | Value |
|---|---|
| Riser height, 1011.5.2 | 7" (178 mm) max, 4" (102 mm) min |
| Tread depth, 1011.5.2 | 11" (279 mm) min |
| Dwelling units (R-3, within R-2 units not required to be Accessible/Type A), exception 3 | Riser 7¾" (197 mm) max, tread 10" (254 mm) min |
| Width, 1011.2 | 44" (1118 mm) min, with exceptions (a secondary source gives 36" for occupant load under 50; unverified) |
| Headroom, 1011.3 | 80" (2032 mm) min, measured vertically from the line connecting nosings, over the full stair and landing width |
| Nosing projection, 1011.5.5.1 | 1¼" (32 mm) max |
| Handrail height, 1014.2 | 34"-38" (864-965 mm) above nosings |
| Guard height, 1015.3 | 42" (1067 mm) min |
| Uniformity, 1011.5.4 | Variation within a flight limited to ⅜" (secondary source; unverified) |

2010 ADA Standards (accessible stairs where required) [N01]: uniform risers 4"-7" (100-180 mm), treads 11" (280 mm) min (504.2); no open risers (504.3); nosing projection 1½" (38 mm) max (504.5); handrails 34"-38" (865-965 mm) (505.4), extending 12" (305 mm) horizontally at the top (505.10.2) and one tread depth at the slope at the bottom (505.10.3); circular grip 1¼"-2" (32-51 mm) diameter (505.7.1); 1½" (38 mm) clearance to the wall (505.5).

The IRC (one- and two-family dwellings) has its own, different limits; not verified here. Ask.

### 2.3 Other jurisdictions

Not researched first-hand (Australia NCC, Canada NBC, EU national codes, others). Use the comfort rule in 3.2 as a starting point, mark the result "code not checked", and ask the user for the governing values.

## 3. Computing a stair

### 3.1 Method

1. Get total rise H = FFL(upper) − FFL(lower).
2. Get R_max and R_min, G_min (and G_max, 2R+G range) from the governing code and stair category.
3. Number of risers N = ceiling(H / R_max).
4. Actual rise R = H / N. Check R ≥ R_min. Round only for display; keep the exact value in the model.
5. Pick G ≥ G_min. If a 2R+G rule applies, choose G so 2R + G is inside the range (aim near 600-630 mm / 24"-25" for comfort; typical practice).
6. Number of treads per straight flight = risers in the flight − 1 (the top "tread" is the landing or floor).
7. Split into flights if the code limits risers per flight; add landings.
8. Going length of a flight = treads × G. Add landing lengths. Check headroom over the whole run.

### 3.2 Worked examples

| | Metric, England general access | Imperial, IBC non-dwelling |
|---|---|---|
| H | 3000 mm | 12'-0" = 144" |
| R_max | 170 | 7" |
| N | ceiling(17.65) = 18 | ceiling(20.57) = 21 |
| R | 166.7 mm | 6.857" (6 ⅞" displayed) |
| Flights | 18 > 12 per flight, so 2 flights of 9 | not limited by riser count in this table |
| G | 280 → 2R+G = 613 (inside 550-700) | 11" |
| Treads | 8 per flight, run 2240 mm each | 20 in one straight run = 220", or split with a landing |

Comfort rule (Blondel-style 2R + G ≈ 600-650 mm, 24"-25½"): typical practice, not a code everywhere. ADK's 550-700 mm is the only code value verified here [N02].

### 3.3 Uniformity

All risers in a flight are equal and all goings are equal [N01]. The first and last riser are where mistakes happen: screed thickness, floor finish change, landing finish. Recompute when finishes change.

## 4. Landings, headroom, handrails, guards

| Item | England (ADK) [N02] | US (IBC/ADA) [N04][N01] |
|---|---|---|
| Landing size | At top and bottom of every flight, width and length at least the smallest flight width (1.20); clear of permanent obstructions (1.21) | Landing depth rules in IBC 1011.6 not verified here; ask |
| Headroom | 2.0 m min (Diagram 1.3); reduced headroom allowed for loft conversions (Diagram 1.4); escape routes 2 m except doorways (1.12) | 80" (2032 mm) min (1011.3) |
| Handrail height | 900-1000 mm above pitch line or floor (1.34); both sides if the stair is 1000 mm or wider | 34"-38" (1014.2; ADA 505.4) |
| Guard height | Diagram 3.1 table: single family dwellings 900 mm for stairs, landings, ramps, internal floor edges (1100 mm for external balconies and roof edges); residential, institutional, educational, office, public buildings 900 mm for flights, otherwise 1100 mm | 42" (1067 mm) min (1015.3) |
| Guarding needed | Flights and landings with two or more risers (1.40) | Per IBC 1015 (drop height threshold not verified here) |

## 5. Ramps

| Item | England (ADK) [N02] | US (2010 ADA) [N01] |
|---|---|---|
| When a slope is a ramp | Gradient 1:20 or steeper (2.3 note) | Running slope steeper than 1:20 on an accessible route (ADA definition; not quoted here) |
| Max gradient vs flight length | 1:20 up to 10 m going (500 mm rise); 1:15 up to 5 m (333 mm rise); 1:12 up to 2 m (166 mm rise); interpolate between (e.g. 1:14 at 4 m, 1:19 at 9 m) (Diagram 2.1) | 1:12 max (405.2); rise per run 30" (760 mm) max (405.6) |
| Cross slope | not quoted | 1:48 max (405.3) |
| Width | see ADK section 2 / ADM (not quoted) | 36" (915 mm) clear, between handrails where provided (405.5) |
| Landings | see ADK section 2 (not quoted) | Top and bottom of every run, 60" (1525 mm) long min, at least as wide as the run; 60" x 60" at changes of direction (405.7) |
| Handrails | Top of handrail 900-1000 mm above the ramp surface (section 2 provisions) | Required where rise exceeds 6" (150 mm) (405.8), 34"-38", extending 12" (305 mm) at top and bottom (505.10.1) |

Ramp computation: length = rise × slope ratio. A 450 mm rise at 1:12 needs 5.4 m of ramp, which exceeds ADK's 2 m limit at 1:12, so in England you would use a shallower gradient (about 1:15 over 5 m gives 333 mm, still short: two flights with a landing). Show this reasoning to the user rather than picking a non-compliant ramp silently.

## 6. Plan conventions

- Draw the stair as cut at the plan's cut plane (typically about 1.0-1.2 m / 3'-4' above the floor; typical practice). Show treads below the cut in projection and a diagonal break line at the cut; show the part above the cut dashed or omit it (office standard).
- Direction arrow starts at the floor of the plan being drawn and points along the walking line, labelled UP or DN, often with the riser count ("UP 18R"). On each floor, arrows point away from that floor.
- Nosing lines thin; handrails and guards drawn; landing edges shown.
- At large scale (1:50 / ¼" and up) number the risers or treads and dimension: R and G, flight widths, landing lengths, headroom in section.
- The section through the stair is where headroom and riser uniformity are proven; always produce one for a new stair.

## 7. Revit stairs: concepts

- A stair is assembled from **runs**, **landings** and **supports** (stringers or carriages), with railings added automatically or later.
- Type properties (Revit help) [N12]: Calculation Rules group with Maximum Riser Height, Minimum Tread Depth and Minimum Run Width (the last two apply to standard runs, not sketched runs); the Calculation Rules button opens the Stair Calculator; Run Type and Landing Type; Right/Left/Middle Support (stringer closed, carriage open, or none) with types and offsets; Function (interior/exterior).
- Instance properties include base and top level and offsets, desired number of risers, actual riser height and actual tread depth (the instance property page was not opened in this research; names as shown in the app may differ slightly).
- Set the type's Maximum Riser Height and Minimum Tread Depth to the code values **before** creating the stair, so Revit computes the riser count and warns when you break them.
- Multistorey stairs: Revit can repeat a stair across levels with identical floor-to-floor heights; any level with a different height breaks the repetition and needs its own stair (behaviour stated from product knowledge, not verified here).
- Railings: created with the stair on the run edges by default; check they also exist at landings and open edges, and that guard heights match the code.
- View range: floors, stairs and ramps outside the primary range use an adjusted range 4 ft (about 1.22 m) below the bottom of the primary range [N11]. A stair that "disappears" below a plan is usually a view range issue.

## 8. Failure points and checks

| Failure | Signal | Fix |
|---|---|---|
| Riser count does not reach the top level | Revit warning that actual risers differ from desired; last riser not at the upper level | Extend the run or add a run; recompute N |
| Actual riser above code maximum | Revit warning about riser height; R > R_max | Increase N |
| Uneven top or bottom riser | Total rise computed on structural levels; finishes changed later | Recompute from FFL; set level offsets |
| Headroom clash | Slab edge or beam within 2.0 m / 80" of the pitch line | Enlarge the floor opening (shaft or floor edit), move the stair |
| No floor opening | Floor cuts through the stair in 3D | Add a shaft opening or edit the floor boundary |
| Landing shorter than the flight width | Landing length < flight width (ADK 1.20) | Lengthen the landing |
| Too many risers per flight | Over the code limit (e.g. 12 general access, ADK) | Add a landing |
| Detail lines drawn as a stair | Stair absent in 3D and schedules [S34] | Model it as a stair |
| Arrow or UP/DN wrong on upper floors | Arrow not starting at the plan's own floor | Fix the annotation per floor |
| Ramp too steep for its length | Gradient vs going outside the code table | Lengthen, add landings, or change level strategy |

Payette advises reviewing stair riser and ramp slope warnings right away rather than letting them pile up [S23].
