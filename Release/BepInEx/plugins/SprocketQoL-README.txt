Quality of Life - editor tools for Sprocket 0.2.55.5
https://github.com/Hans21223/Sprocket-Quality-of-Life

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
  around them uses the fewest points by default (light or smooth rings, or the game's own fan, to choose from).
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
- F9 drawing sheet: a technical drawing of the vehicle from above, the front, the side and the back, all at one
  scale, with its length, width and height and a 1 m ruler (antennas left out), and under it the vehicle's name,
  horsepower and top speed, its guns (name, caliber, L/xx) and its description. Saved three times in
  Documents\My Games\Sprocket\Photos: lines only (black on white), in colour (with its decals) with the lines
  over it, and see-through (the armour as glass).
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
  fewest points by default (Fill: light or smooth rings for evener faces).

INFO
- Gun length: L/xx in calibers from the muzzle to the breech face (barrel + chamber holding the whole round),
  with the barrel alone and the round's length.
- Speed & acceleration (transmission and engine panels): every gear's top speed at the engine's rev limit (by
  default upshift rpm + 50), capped by the tracks' speed limit, and the time from standing to top speed on flat
  ground, shifting like the game's automatic gearbox. Test drive once so it uses your tracks' own losses.

Merges, cuts and face edits happen in place, and Ctrl+Z undoes them. Each edit also saves a backup of the design
to BepInEx\SprocketQoLBackups\ (the newest 50 are kept; "Backups kept" in the config). Merge faces and Boolean
cut are new: save a copy of your tank before using them.

Found a bug? Open an issue on GitHub with a screenshot and your BepInEx\LogOutput.log.
