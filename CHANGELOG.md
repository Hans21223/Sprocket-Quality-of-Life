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
