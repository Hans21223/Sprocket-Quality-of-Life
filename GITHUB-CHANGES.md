# GitHub changes and local fixes

## Latest sync, 2026-09-29

Combined `main` at `48e2bc0` with `bridge-circle-mirror` at `a376aee` locally for testing.
`main` now includes the previous local bevel, gizmo, drawing, decal and photo fixes, plus the
1.8.0 Bridge, Circle, Fix mirror, rotation-snap and mirrored point-merge features.
The latest branch adds acceleration upshift fixes, smaller simulation steps around the rev limit,
power below the rev limit, and gun-length display to 0.01 caliber with a muzzle-brake clarification.
The packaged DLL was rebuilt from the combined source to resolve its binary merge conflict.

## Previous audit snapshot (before the updates above)

Verified against https://github.com/Hans21223/Sprocket-Quality-of-Life on 2026-09-29 (Asia/Bangkok).

`origin/main` is `227e97dc060f247ce9e4be136c27b8070efdd19c`.
The latest release is **v1.7.9**, and its annotated tag resolves to that same commit.
There are **no commits on main beyond the latest release tag** at this check.
The earlier description of the decal commit as unreleased was incorrect.
All upstream source changes below are already present locally. Local customizations are preserved.

## Upstream features and fixes

| Version / change | What changed |
| --- | --- |
| 1.3.0, initial repository | Sprocket Mod Kit for 0.2.55.5: turret-to-add-on conversion, merging add-ons, Boolean cuts, hole quality controls, merging faces, gun length, speed/acceleration estimates, collapsible panels, offline tests, Round Add-on Parts data mod, starter mod and build tools. |
| Documentation cleanup | Removed the README credits section. |
| 1.4.0 | Live-binding hotkeys box; Shift + axis plane locks; Mirror-aware face merging; Ctrl+J merges selected add-ons into the last selected one; folded sections persist across restarts. |
| License | Added MIT license. |
| 1.5.0 | Flatten, loop cut, inset, bevel, select linked flat faces, proportional editing and 0.5 mm grid; Mirror support and undo; orthographic view with closer zoom; exploded view; copying a whole turret and its attachments; in-place merge/cut for freshly placed add-ons sharing a mesh; more hotkey hints; builds omit local folder paths. |
| 1.6.0 | F8 max-quality photo with settings restored afterwards; mirror-aware add-on merges; merging into hulls/turrets; flipped geometry, thickness and rivet handling; stable straight orthographic views, below/opposite views and hidden ground; F5 shadows with headlight and F6 flashlight. |
| 1.7.0 / Separate and reliability changes | Separate selected faces/points into an add-on, preserving placement and properties; native undo where possible and Restore fallback; fewest-points hole fill; mirrored Boolean cuts; live placement data; reject open cutters; preserve flipped-part mirroring; fix mantlet scale inheritance; validate mesh edits against cracks, folds and overlaps; fix bevel endpoints; retain rivets on rebuilt faces and around holes; per-part paint; Paint/Decals image address fixes; plain orthographic backdrop and hidden spawn pad; working point-light fullbright with brightness control. |
| 1.7.1 hole-size hotfix | Avoid native crash when a requested hole is larger than its face; resize safely after native creation; respect concave faces and leave the original ring where no safe placement exists; added regression tests. |
| 1.7.1 combined update | Separate picked disconnected pieces into individual add-ons with one undo; unsigned thickening-edge serialization fixes load failures; faster detailed Boolean cuts; reduced per-frame paint, bounds, flashlight, axis-key and turret-mirror work. Includes the hole-size hotfix. |
| Modding guide | Added a 22-page Making Mods for Sprocket guide in PDF and HTML, covering setup, game APIs, Harmony, IL2CPP, UI, settings, logs, tests and packaging. |
| 1.7.2 | F9 four-view drawing sheet, shared scale, dimensions, ruler, line and colour images; hidden-line removal, temporary fog/map hiding, omit antennas; orthographic dimension lines; fix huge/flickering transform gizmos; F9 hotkey hint. |
| 1.7.3 | Measure only visible renderers, excluding disabled/shadow-only geometry; fixes incorrect height and spawn-pad detection; logs which parts set measurement bounds. |
| 1.7.4 | Drawing title block with vehicle name, weapons and description; Windows text rendering for different languages; capture-layer isolation removes map grass; recognize custom antennas; decimal-point measurements in comma locales. |
| 1.7.5 | Fix acceleration estimates hunting between gears and hitting the 600-second cap; improve panel spacing; include posed crew meshes in drawing visibility and footprint. |
| 1.7.6 | Mouse hover no longer makes parts disappear during F9 capture; temporarily suppress hover and maintain capture layers; remove sky/map reflections from drawing views. |
| Hole-fill correction and 1.7.7 | Correct bridge placement when multiple holes share an outline corner; prevents flipped and overlapping faces when cutting with many-legged add-ons. |
| 1.7.8 | Third F9 see-through sheet, blending normal colour with an armour-hidden capture to show crew, engine and ammunition. |
| 1.7.9 | Engine horsepower and forward/reverse top speeds on the drawing; three-column title block with Mobility, Weapons and Description headings. |
| Latest commit 227e97d (included in v1.7.9 tag) | Vehicle decals appear on drawing sheets. |

## Local customizations (not upstream GitHub changes)

- Local 1.7.9.9: bevel traces recreated sides to their original edges, preserves authored sharp/connection settings, and avoids copying temporary flags or an unrelated edge's settings to new chamfers. Existing edges are reused. Build and offline tests verified; the reported movement/Welded symptom needs in-game confirmation.
- Drawing: remembered no-wireframe option; clean contours throughout colour drawings; silhouette-only see-through option; original wireframe remains selectable.
- Drawing cleanup: restore object layers before refreshing decal caches so decals remain visible after F9, including failure cleanup.
- F8 photo: save opaque RGB while preserving rendered RGB values, preventing particle alpha from being blended again by image viewers.
- Local 1.7.9.8: extend orthographic gizmo hit detection to the actual gizmo distance and bounds. The game's 100 m limit could stop short of the rings after the camera moved back by 100 m. Both axis-picking paths are patched; native transformations, constraints and undo remain in use. Hotkeys distinguish D rotation rings from R direct rotation and respect rebound bindings.

Validation for 1.7.9.8: Release build succeeded; 85 focused picking checks passed. In-game validation is left to the user.

## Full fetched commit history

Merge commits below are integration records; the table above consolidates their feature changes.
```text
227e97d Drawing sheet: vehicle decals on the pictures
578cbc3 Merge drawing sheet powertrain and title block into 1.7.9
469f366 Quality of Life 1.7.9: drawing sheet horsepower, top speed and 3-column title block
b34250f Merge see-through drawing sheet into 1.7.8
6b0fe72 Quality of Life 1.7.8: see-through drawing sheet
56e7ee4 Merge many-legged cut fix into 1.7.7
26a1119 Quality of Life 1.7.7: cutting with a many-legged add-on no longer folds the plate over
e104091 Fill: join a hole on its own side of a corner another hole was joined at
ff40031 Merge drawing sheet mouse-over and reflection fixes into 1.7.6
bf6fa17 Quality of Life 1.7.6: drawing sheet keeps every part while the mouse moves, no sky in the top view
686ce0c Merge gear-hunting fix and crew on the drawing sheet into 1.7.5
1b71bba Quality of Life 1.7.5: speed estimate stops gear hunting, crew on the drawing sheet
222ebea Merge drawing sheet title block and fixes into 1.7.4
fe948a6 Quality of Life 1.7.4: drawing sheet title block, no map grass, custom antennas
166172f Merge measure-only-what's-seen fix into 1.7.3
641ed59 Quality of Life 1.7.3: measure only what's seen
9a2b2cb Merge drawing sheet, ortho measurements and fixes into 1.7.2
691feea Quality of Life 1.7.2: drawing sheet (F9), ortho measurements and fixes
2f7c192 Merge pull request #4 from Hans21223/mod-guide
c6861f4 Add Making Mods for Sprocket guide (PDF and its HTML source)
c1f2abd Merge Separate picked pieces, faster cuts and fixes into 1.7.1
12ddef7 Quality of Life 1.7.1: hole size crash fix, Separate picked pieces, faster cuts
4837a75 Merge pull request #3 from Hans21223/hotfix-hole-size
63a14ed Quality of Life 1.7.1: fix Hole size crash
fb80fae Merge pull request #2 from Hans21223/separate-and-reliability
fbea65a Quality of Life 1.7.0
5a717bb Separate into add-on, safer Boolean and mesh tools, kept rivets, working fullbright
d284a17 Quality of Life 1.6.0: max-quality photos, mirror-aware merging, view and lighting keys
7426f1d Quality of Life 1.5.0: mesh tools, exploded view, orthographic view, turret copy
932c66b Merge pull request #1 from Hans21223/claude/practical-bell-jglhc4
e609f30 Add MIT license
69ed81c Quality of Life 1.4.0: hotkeys box, plane lock, Mirror merge, Ctrl+J
a2ff6f2 Remove credits section from README.md
695f997 Sprocket Mod Kit with the Quality of Life mod 1.3.0
```
