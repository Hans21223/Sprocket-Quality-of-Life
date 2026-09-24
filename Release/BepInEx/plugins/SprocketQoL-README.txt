Quality of Life - editor tools for Sprocket 0.2.55.5
https://github.com/Hans21223/Sprocket-Quality-of-Life

Adds new sections to the vehicle editor's own panels. Every section folds away (click its header).

INSTALL
Needs BepInEx 6 (IL2CPP) for Sprocket. Add the zip in Sprocket Mod Manager, or copy SprocketQoL.dll into
Sprocket\BepInEx\plugins.

STRUCTURE TOOLS (hand-made structures and add-ons)
- Merge faces: select faces in Faces edit mode, press Merge selected faces. They become as few quads as possible.
  Lines that run on into neighbouring faces are taken out too, so nothing comes apart.
- Hole quality: segments and size for the Create Hole tool. Holes come out round, face the right way, and get a
  clean fill instead of long thin triangles.
- Turret to Add-on: turns a turret into a fixed add-on. Guns, crew and attached parts stay where they are.

ADD-ON TOOLS
- Merge add-ons: folds the selected add-ons into one, keeping their shape, position and armour.
- Boolean cut: uses an add-on to cut a hole, or a pocket with walls and a floor, into the structure under it.
  Any closed shape works, and rivets move onto the new faces.

INFO
- Gun length: barrel and bore length in calibers (L/xx).
- Speed & acceleration (transmission and engine panels): every gear's top speed, capped by the tracks' speed
  limit, and the time from standing to top speed on flat ground.

Merges, cuts and face edits happen in place, and Ctrl+Z undoes them. Each edit also saves a backup of the design
to BepInEx\SprocketQoLBackups\. Merge faces and Boolean cut are new: save a copy of your tank before using them.

Found a bug? Open an issue on GitHub with a screenshot and your BepInEx\LogOutput.log.
