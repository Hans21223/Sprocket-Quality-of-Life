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
- Hole quality: segments and size for the Create Hole tool. Holes come out round, face the right way, and get a
  clean fill instead of long thin triangles.
- Turret to Add-on: turns a turret into a fixed add-on. Guns, crew and attached parts stay where they are.
- Mesh tools (Blender-style; Mirror applies; Ctrl+Z undoes each): P flatten selected points onto one plane
  (or one height / side / length), T loop cut through the ring of quads a selected edge crosses, I inset selected
  faces, V bevel selected edges into chamfer strips, U select linked flat faces, O proportional editing (moving
  points pulls their neighbours, radius in the panel). Also a 0.5 mm snap grid, and Numpad 5 for an
  orthographic view (Numpad + / - zoom it; it never cuts into the vehicle).
- Exploded view: F2 pulls the parts apart to see inside, tracks and wheels staying on the ground (F3 / F4 closer /
  further), F2 again puts them back.
  Only what's drawn moves; saving is never affected.
- Turrets can be copied with Alt like other parts, with everything on them (turret body, guns...).
- Hotkeys box beside the panel: the mesh editing keys, including hidden ones like B (box select) and
  C (circle select). x closes it, F1 shows or hides it.
- Move or scale without height: while moving or scaling, Shift+Z locks to the two flat axes
  (Shift+X / Shift+Y leave out that axis instead).

ADD-ON TOOLS
- Merge add-ons: folds the selected add-ons into one, keeping their shape, position and armour.
  Ctrl+J (like Blender's join) merges all selected add-ons into the last one you selected.
- Boolean cut: uses an add-on to cut a hole, or a pocket with walls and a floor, into the structure under it.
  Any closed shape works, and rivets move onto the new faces.

INFO
- Gun length: barrel and bore length in calibers (L/xx).
- Speed & acceleration (transmission and engine panels): every gear's top speed, capped by the tracks' speed
  limit, and the time from standing to top speed on flat ground.

Merges, cuts and face edits happen in place, and Ctrl+Z undoes them. Each edit also saves a backup of the design
to BepInEx\SprocketQoLBackups\. Merge faces and Boolean cut are new: save a copy of your tank before using them.

Found a bug? Open an issue on GitHub with a screenshot and your BepInEx\LogOutput.log.
