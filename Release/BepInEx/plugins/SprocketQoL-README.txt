Quality of Life - editor tools for Sprocket 0.2.55.5
https://github.com/Hans21223/Sprocket-Quality-of-Life

Adds new sections to the vehicle editor's own panels. Every section folds away (click its header) and stays
folded.

INSTALL
Needs BepInEx 6 (IL2CPP) for Sprocket. Add the zip in Sprocket Mod Manager, or copy SprocketQoL.dll into
Sprocket\BepInEx\plugins.

STRUCTURE TOOLS (hand-made structures and add-ons)
- Merge faces: select faces in Faces edit mode, press Merge selected faces. They become as few quads as possible.
  Lines that run on into neighbouring faces are taken out too, so nothing comes apart.
  With Mirror on, the mirrored faces merge too.
- Separate (Blender's P): select faces (or the points around them) in edit mode, press Separate selected into a new
  add-on. They become a new add-on in the same place, with their armour and rivets; mirrored parts give their twin one
  too. From an add-on it happens in place and Ctrl+Z undoes it; from a hull or turret the design reloads (Restore).
- Hole quality: segments and size for the Create Hole tool. Holes come out round and face the right way; the fill
  around them uses the fewest points by default (light or smooth rings, or the game's own fan, to choose from).
- Turret to Add-on: turns a turret into a fixed add-on. Guns, crew and attached parts stay where they are.
- Mesh tools (Blender-style; Mirror applies; Ctrl+Z undoes each; each is checked first and not done if it would crack
  the shape, turn or squash a face, or lay faces over each other; rivets on rebuilt faces stay, here and in Merge
  faces and Create Hole): P flatten selected points onto one plane
  (or one height / side / length), T loop cut through the ring of quads a selected edge crosses, I inset selected
  faces, V bevel selected edges into chamfer strips, U select linked flat faces, O proportional editing (moving
  points pulls their neighbours, radius in the panel). Also a 0.5 mm snap grid, and Numpad 5 for an
  orthographic view (spawn pad and ground hidden; Ortho backdrop: plain grey / white / black, no sky or map, or scene; Numpad + / - zoom it; it never cuts into the vehicle; it snaps to front / side / top,
  Numpad 1 / 3 / 7, Ctrl for back / other side / from below, Numpad 9 the opposite view).
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
- Gun length: barrel and bore length in calibers (L/xx).
- Speed & acceleration (transmission and engine panels): every gear's top speed, capped by the tracks' speed
  limit, and the time from standing to top speed on flat ground.

Merges, cuts and face edits happen in place, and Ctrl+Z undoes them. Each edit also saves a backup of the design
to BepInEx\SprocketQoLBackups\ (the newest 50 are kept; "Backups kept" in the config). Merge faces and Boolean
cut are new: save a copy of your tank before using them.

Found a bug? Open an issue on GitHub with a screenshot and your BepInEx\LogOutput.log.
