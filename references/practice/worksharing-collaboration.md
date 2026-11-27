# Worksharing and collaboration

Read this before touching a workshared Revit model, handling links from other disciplines, running clash checks, or raising coordination issues. Also read it before any save, sync or file operation on shared files in either app.

Contents: 1 Central and local · 2 Ownership and borrowing · 3 Sync etiquette · 4 What an automated agent must never do unasked · 5 Links and coordination · 6 Clash workflow · 7 Issue tracking · 8 AutoCAD shared files

## 1. Central and local models

- The **central** model holds the authoritative state. Each user works in a **local** copy and synchronises changes to central.
- Never open the central file directly to work in it; create a local [S29][S87]. Cloud-hosted models follow the same logic through the cloud service.
- Everyone on the same Revit build [S28].
- Detaching from central creates an independent copy (with or without worksets preserved) [R04]. Only do this to make an archive or a test copy, and say so.

## 2. Ownership and borrowing

| Concept | Meaning |
|---|---|
| Workset ownership | A user has made a whole workset editable; nobody else can edit its elements |
| Element borrowing | A user is editing specific elements; others can edit the rest |
| Relinquish | Give back what you own or borrowed |

Rules:
- Prefer borrowing elements to taking whole worksets [S28].
- Before changing anything, check who owns or has borrowed every element the change will touch (hosts, hosted inserts, joined elements, rooms whose boundaries change, tags). If any belong to someone else, stop and list them with the owner; do not request or force ownership without the user's say.
- Some changes touch more than they appear to: moving a wall moves its doors and may change room boundaries and joined walls.

## 3. Sync etiquette (for the user; the agent only advises)

- Sync often enough that others see your work and you do not hold elements for hours; Autodesk advises frequent synchronisation and periodic compacting of the central model, without giving numbers [S29].
- Relinquish when you finish a task or leave; held elements block others [S87].
- Save local before long operations; reload latest before starting a major edit.
- Do not sync while others are mid-way through a coordinated change without warning them.

## 4. What an automated agent must never do without being asked at that moment

| Action | Why |
|---|---|
| Synchronise with central | Publishes changes to the whole team; in the Revit API a sync also reloads latest and always saves to central [R04] |
| Relinquish ownership | May release elements the user still needs; others can take them |
| Save over central, or save as central | Can destroy the team's model |
| Detach, discard worksets, or create a new central | Breaks the link between the team's files |
| Take ownership of worksets or elements owned by others | Blocks colleagues; may look like interference |
| Delete elements it did not create | Others' work; the user decides |
| Purge or compact the central | Affects everyone |
| Change shared coordinates, levels or grids | Moves every linked model's relationship |
| Reload or remove other people's links | Changes what the team sees |

Report what would need syncing and let the user do it, or do it only after an explicit "yes, sync now".

## 5. Links and coordination

- Each discipline models its own elements; others link them. Do not copy linked elements into your model except via Copy/Monitor for levels and grids (or coordinated elements like columns where agreed) [S22].
- Review Copy/Monitor coordination alerts after each link reload; unaddressed alerts let levels and grids drift (RV-049) [S22].
- Agree link locations (origin-to-origin or shared coordinates), phase mapping and refresh cadence in the BIM execution plan (BEP) [S22][S16].
- Each link pinned and on its own workset [S22].
- Under ISO 19650, models move through CDE states: work in progress, shared, published, archive; status codes S0-S7, A, B, CR describe suitability [S15]. Only use shared or published models from others for coordination.

## 6. Clash workflow

1. **Prepare models**: export from a dedicated 3D view at Fine, hiding lines, scope boxes, analytical lines, imports and unwanted links [S22].
2. **Define tests from a clash matrix**: which category pairs are tested, test type and tolerance per cell. Example structure (Ashghal): matrix cells such as `H-500` (hard, 500 mm) or `C-1000` (clearance, 1000 mm) or n/a; test names `AR0030_v_ST0520` [S56].
3. **Test types** (Navisworks terminology) [S55]: Hard (geometry intersects beyond tolerance), Hard (Conservative), Clearance (within a distance), Duplicates.
4. **Run, group, assign**: group clashes by location or system; assign each group to the discipline that should move.
5. **Severity** (Ashghal) [S56]: level 1 critical to function, code, cost or schedule, resolve before detailed design (e.g. beam vs main duct); level 2 resolve during detailed design (door swing vs room elements); level 3 resolve in the final stage (cable tray hangers vs pipes); level 4 construction detail items before shop drawings.
6. **Status lifecycle** [S55]: New, Active, Reviewed, Approved, Resolved (no longer found).
7. **Re-run** after each model update; tolerances and cadence are set in the BEP, not universal [S56].

Limits: Revit has no API for its Interference Check report; intersection filters approximate it and miss elements without solids such as rebar [R04]. Report clash checks run that way as approximate.

LOD caution: LOD 300 is "not necessarily clash-free"; interfaces are coordinated at LOD 350 [S20].

## 7. Issue tracking

- Raise each issue with: location (view, coordinates or grid reference), elements involved (ids and model), description, proposed responsibility, priority, due date, status, and a snapshot.
- BCF (buildingSMART) is the open exchange format for issues: topics with viewpoints and snapshots, with a REST API in BCF 3.0 [S57]. Tool support for viewpoint visibility and clipping varies [S57].
- Keep the issue list as the record; do not resolve issues by private message only.

## 8. AutoCAD shared files

- Work in your own drawing; reference others' drawings as xrefs (overlay), never edit someone else's file to change your design.
- Do not save over a file another user may have open; AutoCAD's file locking will usually block it, but a copied or renamed file escapes the lock.
- Share by transmittal with relative paths and a record (see `cad-standards.md`).
