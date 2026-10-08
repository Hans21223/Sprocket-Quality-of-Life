Quality of Life 1.8.6 - editor tools for Sprocket 0.2.56.0
https://github.com/Hans21223/Sprocket-Quality-of-Life

NEW IN 1.8.5
- Create Hole rejects collapsed rings and selections across several faces before cutting. Surrounding faces
  must keep every hole edge and cover the remaining plate without overlaps. A failed mirrored cut restores
  the original shape for undo and redo. Native editor behavior still needs playtesting.
- Fixed invalid plate thickening references after Merge faces and other mesh edits. Deleted topology is fully
  unlinked, and surviving corners cannot keep edges removed from the structure.
- Mesh tools check the whole structure's connections and reject unsupported faces before committing an edit.
- Split selected faces keeps neighbouring borders connected using loadable triangles and quads.

- Settings > Mod Options > Photos: F8 saves at Screen, 2K, 4K, 6K or 8K (long edge 2560/3840/5760/7680 pixels).
  Render draws the actual photo camera at the chosen size; Upscale resizes the finished screenshot.
  The photo keeps its proportions and opaque RGB colours. The window resolution is unchanged.
  Graphics, overlay and camera output return afterward. Screen remains the default.

NEW IN 1.8.4.1
- The drawing sheet's top speed uses the same drivetrain maths as the Speed & acceleration panel.
- The Shortcuts box can be dragged by its title bar and stays there (Mod Options > Settings puts it back).

NEW IN 1.8.4
- Speed & acceleration runs the game's own drivetrain maths step by step, read from the design: top speed within
  0.1% of a test drive, each gear within about 3%. Every gear's top speed, 0-20/40/60 km/h times. No test drive needed.
- Every Quality of Life key can be changed: Settings > Mod Options (Tool keys, Editing keys), or the F11 window.
- Mod Options > Settings: hotkeys box, part mass markers, mirror merge, rotation snap, exploded spread, flashlight,
  fullbright, backups kept, and an optional drive recorder (off by default).

PREVIOUSLY IN 1.8.3
- F10 opens a menu to export selected tank parts to OBJ, or import OBJ as a saved plate-structure blueprint.
- Light and smooth fills around cuts and holes work for any outline, mirror-exact on both sides.
- Create Hole circles sit square to the part.

OBJ IMPORT AND EXPORT (F10)
F10 opens the OBJ export/import menu; a button also appears in structure panels.
Choose export categories or individual parts, with search, paging and Defaults/All/None/editor selection.
Defaults include the tank exterior, tracks, guns, fittings, baskets, antennas, external fuel tanks and decals.
Engines, powertrain, transmissions, ammunition, internal fuel tanks, crew, gunner sights, turret traverse motors
and laying drives start unchecked. You can include them individually or by category.
All geometry uses one tank coordinate frame in metres, Y-up; relative positions, rotations, scale and mirrors stay.
Export makes an OBJ, MTL and texture folder; keep them together. Choose a new filename; existing files are kept.
Press F2 to close exploded view before export.
Import model: choose an OBJ, faction, blueprint name, scale and whole-mm plate thickness. The editable structure
is saved in Documents\My Games\Sprocket\Factions\<faction>\Blueprints\Plate Structures. OBJ objects become
disconnected pieces of one editable structure, keeping their relative positions. Add it from the game's
plate-structure library.
Import preserves the open tank and existing files. OBJ import contains geometry, not textures or functioning
tank mechanisms. OBJ geometry, parser, classification and import checks run offline. Native menu/export and
loading the saved plate structure still need in-game testing.

FILLET
In Edges mode, select edges with two flat adjoining faces on a hand-made structure.
Set Fillet radius (mm) and Fillet segments (2-16, default 4), then click Fillet.
A radius that cannot fit and unsupported junctions are rejected without changing the shape.
Smooth Edge remains available with its separate cutback-width controls.

SMOOTH EDGE AND SPLITTING
Smooth Edge uses Smooth width (mm) and Smooth segments to create a curved bevel.
Split selected edges makes straight cuts through adjoining quads.
In Faces mode, Split selected faces makes straight strips only on selected faces; choose direction A/B.
Select between splits selects the new faces inside the last split, excluding neighbours.
These tools follow Mirror and use normal Undo.

Built and tested with focused offline regression suites. Native placement, Undo, paint appearance and UI rendering
still need in-game testing. Previously lost paint cannot be restored from a save that no longer contains it.

Adds new sections to the vehicle editor's own panels. Every section folds away (click its header) and stays
folded.

INSTALL
A BepInEx mod (not a MelonLoader one). Install the Sprocket Mod Loader first: the official BepInEx freezes this
Sprocket version. https://github.com/Hans21223/Sprocket-Mod-Loader (its Mod Manager: Install mod loader).
Then add this zip in the Mod Manager (Add mod, then enable it), or copy SprocketQoL.dll into
Sprocket\BepInEx\plugins.

STRUCTURE TOOLS (hand-made structures and add-ons)
- Merge faces: select faces in Faces edit mode, press Merge selected faces. They become as few quads as possible.
  Lines that run on into neighbouring faces are taken out too, so nothing comes apart.
  With Mirror on, the mirrored faces merge too.
- Separate (Blender's P): select faces (or the points around them) in edit mode, press Separate selected into a new
  add-on. They become a new add-on in the same place, with their armour and rivets; mirrored parts give their twin one
  too. From an add-on it happens in place and Ctrl+Z undoes it; from a hull or turret the design reloads (Restore).
  Separate picked pieces: for a shape already in pieces that don't touch, click one face on each piece to split off
  and each whole piece becomes its own add-on.
- Hole quality: segments and size for the Create Hole tool. Holes come out round and face the right way; the fill
  around them uses the fewest points by default (light or smooth fill, or the game's own fan, to choose from).
- Bridge (Blender's Bridge Edge Loops): in Edges mode select the open edges of two plates (or two loops) and press
  Bridge: a strip of faces joins them. Cuts: rows across it; Smooth 0 straight, 100 curves round like a fillet.
- Circle: select points round a loop and press Circle: they spread evenly round a true circle (loop cut first for a
  rounder, higher-poly one).
- Fix mirror: Mirror only pairs points that match to a fraction of a mm. Fix mirror makes near pairs exact again
  (within 5 mm by default), puts near-middle points on the middle, and selects points with no mirror image (where
  the sides differ: merged, split or filled on one side only).
- Rotation snap: Mesh tools' Rotation snap (degrees) sets the step turns snap to while the game's rotation snap is
  on (points and whole parts): 7.5 makes a 48-sided circle, 5 a 72-sided one. 0 keeps the game's step.
- Merge (M) both sides: with Mirror on, merging points merges their mirror images too, in one step (Ctrl+Z undoes
  both). Turn it off in Mirror fixes.
- Turret to Add-on: turns a turret into a fixed add-on. Guns, crew and attached parts stay where they are.
- Mesh tools (Blender-style; Mirror applies; Ctrl+Z undoes each; each is checked first and not done if it would crack
  the shape, turn or squash a face, or lay faces over each other; rivets on rebuilt faces stay, here and in Merge
  faces and Create Hole): P flatten selected points onto one plane
  (or one height / side / length), T loop cut through the ring of quads a selected edge crosses, I inset selected
  faces, V bevel selected edges into chamfer strips, U select linked flat faces, O proportional editing (moving
  points pulls their neighbours, radius in the panel). Also a 0.5 mm snap grid, and Numpad 5 for an
  orthographic view (spawn pad and ground hidden; Ortho backdrop: plain grey / white / black, no sky or map, or scene; Numpad + / - zoom it; it never cuts into the vehicle; it snaps to front / side / top,
  Numpad 1 / 3 / 7, Ctrl for back / other side / from below, Numpad 9 the opposite view). In a straight view it
  shows the vehicle's width and height as dimension lines, to the centimetre (Ortho: measurements; antennas not
  counted). The game's fog is off in orthographic view and the move / turn / scale arrows keep a normal size.
- Exploded view: F2 pulls the parts apart to see inside, tracks and wheels staying on the ground (F3 / F4 closer /
  further), F2 again puts them back.
  Only what's drawn moves; saving is never affected.
- F5 turns shadows off (with a headlight from the camera, so no side is black) and back on.
- F6 turns on a flashlight that points where the mouse points (F6 again turns it off); its brightness is
  in Mesh tools.
- F7 fullbright: even light from every side and corner (14 lights), no shadows (F7 again turns it off); its
  brightness is in Mesh tools.
- F8 in photo mode takes a photo at the best graphics settings without leaving photo mode (settings and
  overlay come back after). Saved in Documents\My Games\Sprocket\Photos.
  Local build 1.7.9.7 saves opaque RGB photos: smoke retains its rendered colour instead of becoming transparent
  or dark when an image viewer blends the render target's leftover alpha. Existing photos are unchanged.
- F9 drawing sheet: a technical drawing of the vehicle from above, the front, the side and the back, all at one
  scale, with its length, width and height and a 1 m ruler (antennas left out), and under it the vehicle's name,
  horsepower and top speed, its guns (name, caliber, L/xx) and its description. Saved three times in
  Documents\My Games\Sprocket\Photos: lines only (black on white), in colour (with its decals) with the lines
  over it, and see-through (the armour as glass).
  Colour uses thin clean outlines around visible parts and sharp corners, filtering flat
  triangle seams, shallow facets and isolated speckles. See-through keeps its silhouette-only overlay.
  In Mesh tools, turn off "Colour: clean outlines" or "See-through: outline only" to restore that export's wireframe.
  Enable "Drawing: no wireframe" to omit all added lines from both coloured exports.
  These choices are saved between sessions; lines-only stays the same.
- Own paint: a structure's panel can give the part (and the selected parts) its own paint job ("Own paint 1"
  to "Own paint 9"), with its colours, camo and wear right in the panel. Saved with the design; Ctrl+Z
  undoes each change.
- Zoom in close: the camera can come within a few centimetres of small parts (Mesh tools, on by default).
- Turrets can be copied with Alt like other parts, with everything on them (turret body, guns...), and
  mirrored with Mirror on: the twin gets the body, guns and the rest too (the design reloads once).
- Hotkeys box beside the panel: the mesh editing keys, including hidden ones like B (box select) and
  C (circle select). x closes it, F1 shows or hides it.
- Move or scale without height: while moving or scaling, Shift+Z locks to the two flat axes
  (Shift+X / Shift+Y leave out that axis instead).

ADD-ON TOOLS
- Merge add-ons: folds the selected add-ons into one, keeping their shape, position and armour.
  Ctrl+J (like Blender's join) merges all selected add-ons into the last one you selected; that can be a
  turret or hull too. Mirror twins merge together: on both sides when the target has a twin, or both
  into a centre part.
- Boolean cut: uses an add-on to cut a hole, or a pocket with walls and a floor, into the structure under it.
  Any closed shape works, and rivets move onto the new faces. Mirrored plates (twin pairs, or one part shown on
  both sides) share one shape, so they're cut on both sides and stay mirrored. The add-on must be a closed shape. The fill around the cut uses the
  fewest points by default (Fill: rectangle box to enclose the cut in a clean rectangular box, or light / smooth fill for faces without long thin slivers).

INFO
- Gun length: L/xx in calibers from the muzzle to the breech face (barrel + chamber holding the whole round),
  with the barrel alone and the round's length; a muzzle brake isn't counted.
- Speed & acceleration (transmission and engine panels): every gear's top speed, the top speed with its rpm and
  track slip, the most power below the rev limit, and the time from standing to 20, 40, 60... km/h on flat ground,
  from the game's own engine, gearbox, clutch, sprocket and track maths with the design's own parts.

Merges, cuts and face edits happen in place, and Ctrl+Z undoes them. Each edit also saves a backup of the design
to BepInEx\SprocketQoLBackups\ (the newest 50 are kept; "Backups kept" in the config). Merge faces and Boolean
cut are new: save a copy of your tank before using them.

Found a bug? Open an issue on GitHub with a screenshot and your BepInEx\LogOutput.log.

GAME PROFILE
This binary was built for Sprocket 0.2.56.0.
1.8.6 rebuilds the engine editor tools against the assembly moved in 0.2.56.0.
68 hooks and 51 native routines checked offline. Use loader 1.2.2; gameplay testing pending.
