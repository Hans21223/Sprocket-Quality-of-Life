# Quality of Life 1.10.0

New:

- **Workshop** (`Ctrl+Shift+Space`): tool presets, saved part groups with geometry thumbnails, clipboard history, saved geometry selections, pinned checkpoints, recovery as a new vehicle file and a shareable support report.
- **Tool search** (`Ctrl+Space`): every action by name, with favourites and recent actions; **part patterns** repeat the selected parts along a line or round a circle.
- **Hover outlines:** with the mouse over a mesh tool's button (QoL's, and the game's Fill, Merge, Split and Delete), what it would make is outlined on the part (what Delete takes away, in red). Tools act at once, with no Confirm window; Ctrl+Z undoes them.
- **Hover highlight:** marks the point or edge a click would pick, and outlines the face under the mouse in Faces mode.
- **Point and edge picking:** the closest visible target within a screen radius; Alt+N cycles overlapping ones (Mod Options > Selection).
- **Precision** (structure inspector): measurements, exact lengths, sliding along edges, reference alignment, several loop cuts with an offset, connected or topology-distance falloff, mesh-health inspection and welding of selected duplicate points.
- **Smart corners** (mesh panel > Corners): each corner left on Auto gets the game's thickening way that meets the plates round it best, saved with the design.
- **Create Hole:** points at the four cardinal directions (32 segments by default; Ring switches back to Flat sides); touching flat faces get one hole through them (Hole per face: one each); hovering the button outlines the holes.
- **Split** (the game's edge cut) cuts both sides with Mirror on, in one edit.
- **Fillet:** spherical corners where three convex edges meet, smooth shading on the rounds, and Auto control edges with adjustable spacing.
- **Smooth Edge:** right-angle rounds are true cylinders with spherical corners (no dented junctions); rounds too wide for the faces are refused.
- **Drape as a net:** Holes (mm) sets the hole size, down to 2 mm.
- **Predictive build assist** (off by default, Mod Options > Build assist): movement guides to nearby points, edge middles and face centres; hold N to use one.
- **Relative snap** (Mod Options): snapped moves go in whole grid steps, so off-grid shapes keep their shape.
- **OBJ:** import streams large files with units, up axis, dimensions and a rotatable preview; export writes large files straight to disk.
- **Black box fix** (Mod Options, on by default): for graphics cards where smoke and fire show black boxes.
- **View settings** (orthographic views, backdrop, zoom in close) moved to Mod Options > Settings > View.

Fixes and safety:

- **Size limits:** Merge faces, Create Hole and OBJ import refuse results past the game's 65,535 points, edges, faces or corners per part, before anything changes.
- **Edits stay with their vehicle:** an edit, Restore or drawing started on one vehicle stops if another is opened first.
- **In-place edits** compare every part's whole shape (points, thickness, corners, weld marks, rivets) in their safety trial; anything off loads the planned design instead.
- **Align** and new faces: crossed (bow-tie) plates are refused.
- **Own paint** follows parts made by Separate, Scatter, Make mirror side real and turret twins; merged or deleted parts leave the paint lists.
- **Selection steps** (Select between splits, Select reported points, saved selections) undo with Ctrl+Z.
- **Mirror matching** picks the nearest point, so a seam with doubled points pairs the right faces.
- **Keep only the overlap** on open plates (a sawtooth of single plates) just trims them.
- **Thickening repair** before a mesh tool is put back if the tool is refused.
- **Pinned backups** are kept even when their note is damaged; saved part groups always show up in the list, and one too big to open can still be deleted.
- **Radial patterns** that would stack copies (a sweep past one turn) are refused.
- **Drawing sheet:** tall or long designs draw (the views shrink to fit the graphics card); F11 waits until a photo is done.
- **QoL windows** (Workshop, tool search, part patterns) always give the editor's keys back when they close.
- **Selection sets** skipped after a shape change load again once Ctrl+Z brings that shape back.
- **Drive recorder** (diagnostics) checks the game still keeps its drivetrain where it reads it, and stays off if not.
- **Lighter:** measurements, point and edge picking, hover outlines, the Workshop, part patterns, proportional editing and acceleration runs do much less work.

# Quality of Life 1.9.0

- **Right-click menu:** Ctrl + a right click that doesn't drag opens a Blender-style menu at the mouse with what
  fits the selection: the game's own actions (duplicate, flip, mirror, delete, the mesh edits) and QoL's tools, each
  with its key. Game actions run as if their key was tapped. Settings > Mod Options > Right-click menu chooses what
  opens it (Ctrl, Alt, Shift or Off) and which items show.
- **Drape as cloth:** an add-on's top becomes a sheet of cloth, or a camouflage net, dropped onto the vehicle until it
  settles (position-based cloth: springs, collisions, friction). Spacing, stiffness, net hole size, and Lands on:
  everything, only the selected parts, or all but them. Game parts with no shape (hatches, periscopes, wheels,
  tracks) catch it as boxes around them, open plates catch it from both sides, and after it settles every edge that
  pokes out between its points lifts the nearest spot of cloth clear of it. At most 4,000 points (it runs on the
  game's main thread).
- **Scatter add-ons over this:** copies of the other selected add-ons land at random on a part, standing on its faces
  (faces looking up only, or all), area-weighted, up to 300.
- **Boolean with this add-on:** besides the two cuts, **Keep only the overlap** (intersect), **Join into one shape**
  (union, seams welded) and **Slice in two** (the inside becomes a new add-on).
- **Make the mirror side a real add-on:** the copy the game shows on the other side becomes an add-on of its own, its
  points mirrored; a twin pair stops being a pair. Mirror turns off afterwards, as dragging with it on re-mirrors.
- **Set origin to the shape's middle** and **Parent to active** (the last selected part), from the menu.
- **Mirrored turrets:** moving one of a mirrored pair keeps its twin mirrored and centred, and each ring keeps its own
  traverse drive.
- **Create Hole:** the game's circle collapsed on faces looking straight forward or back (and shrank on faces tilted
  toward them); QoL makes the circle the game meant, so those faces get full-size holes.
- **Gear spacing:** the transmission's Smooth button keeps first and top gear and picks the spacing that reaches
  speed fastest (QoL's drivetrain maths times each), or the game's equal steps when nothing is at least 1% quicker.
- QoL's design maths mirrors only Flipped parts that have a mirror twin (Set origin moved standalone Flipped add-ons
  by twice their box's offset). A thicken edge of 0 stays "none" through merges.
- Shorter labels in QoL's panel sections and Mod Options: the game's label column holds about 14 characters, and
  longer ones overlapped the next row. Tooltips keep the full wording.
- Offline tests pass (cloth, scatter, union, slice, real mirror, turret mirror poses, the circle fix); built against
  0.2.56.2, with every game type and member checked present in 0.2.55.5 too.

# Quality of Life 1.8.8

- One build for **Sprocket 0.2.55.5 and 0.2.56**. 0.2.56 moved the engine panel into
  `Sprocket.EngineDesigner.Editor.dll`, and 1.8.6/1.8.7 named it there, so on 0.2.55.5 Speed & acceleration
  failed every frame and QoL's panel sections were gone after leaving the editor once. QoL now finds the engine
  panel while the game runs, in whichever assembly has it.
- Leaving the editor tidies up each tool on its own: one that fails no longer stops the rest.
- A tool that needs something this Sprocket version doesn't have turns itself off for the session with one
  warning instead of an error every frame. The "same error again" line names the error.
- Merge add-ons refuses a merge that would give one part more than 65,535 faces, corners, edges or points, and
  says so. The game can't load a shape that big: the merge used to fail without a word.
- Every game type and member QoL uses is checked present in both versions' interop assemblies; the offline
  tests pass. Gameplay on both versions remains unverified.

# Quality of Life 1.8.7

- Works with the **Sprocket Mod API** (furryaxw/SprocketModAPI) when it is installed, and exactly as before
  without it. QoL appears in the API's Mod menu with its name and description, its settings are on its page
  there (kept in step with the config file both ways), and its 25 keys are in the API's keybinding window.
- A key changed in QoL's own window carries over to the API's; after that a key rebound in either window is
  the same key in both. QoL still reads the keys itself, so Ctrl keeps meaning "the other way" (back, left,
  from below).
- Mod ID `hans21223.quality-of-life` (saved keys and settings in the API are kept under it).
- Offline checks pass with and without the API present; gameplay remains unverified.

# Quality of Life 1.8.6

- Rebuild for **Sprocket 0.2.56.0**. The game moved its engine editor types into
  `Sprocket.EngineDesigner.Editor.dll`; QoL now references that assembly for the engine inspector
  and its Speed & acceleration section.
- All 68 hooks match the fresh game interop, and 51 relevant native methods retain their instruction
  logic after address relocation. Offline compilation and native/API checks pass; gameplay remains
  unverified. The 1.8.5 hole, mesh and photo fixes are retained.
- Use 1.8.5 with **Sprocket 0.2.55.5**. The new engine editor assembly is absent from that build.

# Quality of Life 1.8.5

- Fix Create Hole collapsing into a point and leaving crossed or overlapping plates. Reject a collapsed ring
  or a selection spanning several faces before removing the original face. Check every hole boundary edge
  and the complete filled area, and restore the original shape if either mirrored cut fails. Undo and redo
  keep a valid mesh. Hole geometry regressions pass offline; native editor behavior still needs playtesting.
- Fix plate structures failing to load after Merge faces and other mesh edits. Removed faces and edges now use
  the game's deletion helpers, which unlink their corner and point connections before compacting the mesh.
  Surviving corners keep only thickening edges that still belong to their point.
- Check the whole mesh's connections before editing and after rebuilding, including the game's triangle/quad
  limit, rather than reporting success from the new faces alone.
- Split selected faces now rebuilds neighbouring borders as triangles and quads. The previous single expanded
  neighbour could have five or more corners and fail during the next edit or reload. The split remains local;
  neighbouring plate shape, shared borders and thickness are preserved.

- **Settings > Mod Options > Photos:** choose Screen, 2K, 4K, 6K or 8K for F8 photographs. The long edge is
  2560 / 3840 / 5760 / 7680 pixels, keeping the original aspect ratio. Screen remains the default.
- Choose **Render** to draw the actual photo camera to a target at the chosen resolution for one frame, or
  **Upscale** to resize the finished screenshot. Native render retains the game's camera-specific vehicle/effect
  path without changing the window size. Restore its output target, rect, aspect and dynamic-resolution setting.
- Keep maximum-quality capture, temporary settings/overlay restoration and opaque RGB output. Cancelled and
  timed-out captures release their render targets. Reject render dimensions above the GPU texture limit before
  changing settings. Size/aspect/colour regression tests and drawing PNG checks pass; native capture awaits testing.

# Quality of Life 1.8.4.1

- **Drawing sheet top speed** now comes from the same drivetrain maths as the Speed & acceleration panel (forward and reverse). It used to take the rev limit at the wheel's radius, capped by the track panel's speed limit, which read too fast.
- **Shortcuts box can be moved:** drag it by its title bar; it stays where you put it. While the mouse is over it, the editor's camera and clicks leave it alone. Mod Options > Settings puts it back beside the part panel.

# Quality of Life 1.8.4

- **Speed & acceleration, rebuilt on the game's own drivetrain:** the estimate now runs the game's engine, gearbox, clutch, sprocket and track maths step by step, as its own physics jobs do, with every input read from the design: the engine's torque curve, rev limiter and inertia, the gearbox and its shift times, each track's sprocket radius, belt bending and drag, the belts' grip and slip, rolling resistance (including the share that loads the belts) and the vehicle's mass. Against recorded test drives: top speed within 0.1%, each gear's acceleration within about 3%, 0-60 km/h within about 2% (the first 10-20 km/h read slower, as the tank's launch rocking isn't modelled). The panel shows every gear's own top speed (marked when the engine can't reach its rev limit in that gear), the top speed with its rpm and track slip, and 0-20 / 0-40 / 0-60 km/h times, worked out in the background. No test drive is needed first any more.
- **Every Quality of Life key can be changed:** F1-F11, the numpad views, P, T, I, V, U, O and Ctrl+J. Panels and hints show the keys as you bound them.
- **Mod Options tab in the game's Settings:** after Controls, drawn with the game's own fields, with three pages: Tool keys, Editing keys and Settings (hotkeys box, part mass markers, mirror merge, rotation snap, exploded view spread, flashlight and fullbright brightness, design backups kept). Click a key, then press the new one; Esc cancels, Backspace leaves it without a key, keys shared by two actions show red. **F11** opens the same keys in a window anywhere.
- **Section names:** "Drawing sheet" and "OBJ export / import" no longer name their key (it can be changed). Folded sections stay folded.
- **Diagnostics:** an optional drive recorder (Mod Options > Settings, off by default) writes your vehicle's drivetrain to `BepInEx\SprocketQoL-drives` while you drive, to check the speed figures against the game.

# Quality of Life 1.8.3

- **OBJ export menu (F10):** choose categories or individual parts, with search, paging and quick selection buttons. Exterior parts start selected; engines, powertrain, transmissions, ammunition, internal fuel tanks, crew, gunner sights, turret traverse motors and laying drives start unselected.
- **Preserved placement:** export geometry in a shared tank coordinate frame, in metres with Y-up, retaining relative positions, rotations, scale and mirrored geometry. Material and texture files accompany the OBJ; keep them together. Close exploded view before export.
- **OBJ import to the faction library:** save one editable plate-structure blueprint in the chosen faction's `Blueprints\Plate Structures` folder. OBJ objects become disconnected pieces with their relative positions retained; choose the blueprint name, scale and whole-mm plate thickness. Import does not change the open tank or create a complete vehicle. Existing files are preserved.
- **Real-world OBJ files import:** faces with no area (slivers, collapsed or self-crossing polygons, which most models and the game's own export contain) are left out and counted instead of rejecting the whole file. A very large import (over 50,000 faces) is flagged as possibly slow to open in the game. Scale and thickness accept a comma or a point.
- **Faces around cuts and holes, reworked:** **light fill** and **smooth fill** (formerly light/smooth rings) now use Delaunay refinement (Ruppert/Chew with Üngör's off-centres, as in Shewchuk's Triangle): points go inside the face wherever a triangle would be a long sliver, for any outline (notches through a plate's edge, several holes, holes near corners), where the old rings only handled one hole in the middle of a face and everything else fell back to fans. Points are smoothed, triangles paired into quads with augmenting chains (far fewer leftover triangles), and smooth fill adds a ring of quads along curved edges. **Fewest points** stays a constrained Delaunay triangulation: with no added points that is already the best possible (Blender gives the same). Fills are now mirror-exact: a face and its mirror twin, or a face across the centre line, get matching points and faces, so the editor's Mirror keeps pairing them. Edges shared with neighbouring faces are never split, so a hole very close to a long plate edge can still leave thin faces there.
- **Create Hole circles sit square to the part:** the circle used to start from the face's first corner, tilting it a few degrees (differently on each side). It now has a flat top and bottom, 4 segments make an upright square, and mirrored faces get mirror-image circles.
- **Drawing sheet settings in one section:** appearance, movement limits and blue blueprint options are under one **Drawing sheet (F9)** heading.
- **Fixes:** Hole quality's "rectangle box" layout was ignored by Create Hole (it made smooth rings). The drawing sheet's top speed now uses the engine's rev limit and its L/ figure the gun's total length, both matching the Speed & acceleration and Cannon panels. F10 outside the editor no longer shows a message.
- **Import scope:** OBJ import restores geometry, not textures or functioning tank mechanisms. Geometry, format and export-policy checks run offline; native menu behavior, appearance and loading the imported plate structure still need in-game testing.

# Quality of Life 1.8.2

- **Fillet:** round selected edges with a circular curve tangent to both adjoining faces. Set the radius and segments; Mirror and one-step Undo apply. A radius that cannot fit is rejected before changing the mesh.
- **Individual add-on paint:** retain extra paint presets and part assignments when saving and reloading a design, including resetting a part to vehicle paint. Previously missing paint cannot be recovered from a save that no longer contains it.
- **Mirrored turrets:** disconnect deleted rings from their traverse motors, avoid duplicate movement callbacks, repair shared or crossed drives when completing mirrored placement, and isolate nested-copy bookkeeping.
- **Mass markers:** the COM view filter hides part mass diamonds as well as the vehicle marker. The separate Part mass markers setting controls the diamonds while COM is enabled.
- **Editor UI:** shorter names, clearer tooltips, grouped foldouts, width-aware help and a shortcut panel that follows the inspector and supports paging. Existing foldout preferences are retained.
- **Geometry and clipboard reliability:** reject invalid mesh results and stale selections before applying edits; preserve surrounding edge flags, corner thickness, paint, settings and implicit mirror images. Cutting an independent attachment no longer expands to an unrelated turret.
- **Drawing and photo reliability:** prevent overlapping F8/F9 captures and conflicting edits, restore render targets and temporary settings on failure, validate PNG data and replace files only after writing a complete temporary image.
- **Performance:** cache repeated acceleration predictions, paint lookups and renderer scans; avoid repeated face merge and fill work. Acceleration estimates no longer shift into a gear that cannot pull.

Generated/Standard shape controls use the game's original tools; the experimental expansion is not included.

Validation uses the Sprocket 0.2.55.5 / Unity 6 interop wrappers and focused offline regression suites. Native placement, Undo, save/reload appearance and actual UI rendering still need in-game testing.
