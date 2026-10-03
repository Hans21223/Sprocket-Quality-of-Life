# Sprocket Mod Kit

Everything needed to write BepInEx mods for **Sprocket** (IL2CPP, Unity 6), with a working example:
**Quality of Life**, which adds small tools to the vehicle editor's own panels.

**Quality of Life 1.8.4** works out speed and acceleration with the game's own drivetrain maths (top speed within
0.1% of a test drive), lets you change every Quality of Life key, and adds a **Mod Options** tab to the game's
Settings for its keys and settings. 1.8.3 added an **OBJ export / import** menu (F10).

Mesh edits and clipboard operations validate geometry and preserve surrounding edge settings, paint and mirror
images. Photo and drawing exports restore temporary state, prevent overlapping captures and write PNGs safely.
Acceleration predictions, paint lookups and renderer scans avoid repeated work. See [the change list](CHANGELOG.md).

## What you need

1. **.NET 8 SDK**: free from Microsoft, <https://dotnet.microsoft.com/download>.
2. **Sprocket with a BepInEx 6 IL2CPP loader that runs on your Sprocket version**, started once so it creates
   `BepInEx\interop` (the game's classes as .NET files that mods compile against).
   As of 2026-09-23 (Sprocket 0.2.55.5, Unity 6000.3.21), the official BepInEx 6 bleeding-edge build (be.788)
   freezes Sprocket at the main menu, so check that your loader actually reaches the menu before building mods.

The kit doesn't include the loader, the game's `BepInEx\interop` files, or any game code. Those come from your own
game install.

**New to modding Sprocket?** Read [Making Mods for Sprocket](docs/Making-Mods-for-Sprocket.pdf) (PDF, 22 pages): how
mods work, setting up, a first mod step by step, Harmony patches, UI, data mods, testing, sharing, and the pitfalls
found while building Quality of Life and the Battle Editor.

## What's in the kit

| Folder | What it is |
|---|---|
| `SprocketQoL\` | The Quality of Life mod (source), one file per feature |
| `SprocketQoL.Tests\` | Offline tests: Boolean cut, hole fill, face merge, and the turret conversion on your own blueprints |
| `DataMods\Round Add-on Parts\` | Round add-on palette parts; a data mod that needs no BepInEx |
| `MyFirstMod\` | A minimal mod to copy when starting a new one |
| `SprocketTweaks\`, `HelloMelon\` | Examples of the other kind: MelonLoader mods (they run through the loader's MLLoader). Quality of Life itself is a BepInEx mod |
| `tools\RoundAddonParts\` | Regenerates the round add-on part files |
| `tools\ApiLister\` | Lists the game's classes and methods from `BepInEx\interop` (reads files only) |
| `tools\deploy.ps1` | Builds a mod and copies it into `Sprocket\BepInEx\plugins` |
| `SprocketMod.props` | Shared build settings: target framework and where your Sprocket folder is |
| `docs\` | The modding guide: `Making-Mods-for-Sprocket.pdf`, and its source `.html` (open it in a browser and print to PDF to rebuild it) |
| `Release\BepInEx\plugins\` | The built Quality of Life mod and its user guide, ready to use (also on the Releases page as a zip) |

## Just want the Quality of Life mod?

**It's a BepInEx mod, not a MelonLoader one.** Sprocket has no mod loader of its own, and the official BepInEx
freezes this Sprocket version, so install the fixed loader first:

1. **Install the mod loader:** get **Sprocket Mod Loader** from <https://github.com/Hans21223/Sprocket-Mod-Loader>.
   Its Mod Manager installs it with one click (**Install mod loader**). A MelonLoader on its own can't load this mod.
2. **Download** `Quality-of-Life-<version>.zip` from the [Releases page](https://github.com/Hans21223/Sprocket-Quality-of-Life/releases).
3. **Add it:** in the Mod Manager, **Add mod**, choose the ZIP, then enable it. Or copy
   `Release\BepInEx\plugins\SprocketQoL.dll` into `Sprocket\BepInEx\plugins\` yourself.
4. **Start Sprocket** and open the vehicle editor: the part panels have new sections.

If you had the older `Sprocket.TurretAddon.dll`, delete it, because Quality of Life includes it. MelonLoader mods run
alongside it through the loader's MLLoader. In the vehicle editor:

- **Turret to Add-on:** select a turret ring or turret body, and its panel gets a **Convert to add-on** button.
  Select several turrets and it converts them all at once; a mirrored turret's twin converts with it.
- **Merge add-ons:** select two or more add-ons. The panel of the one you clicked offers to merge the others into
  it, keeping their shape, position and armour. Parts attached to them move onto it. Happens in place; Ctrl+Z undoes it.
  **Ctrl+J** does the same as Blender's join: all selected add-ons merge into the last one you selected. Add-ons can
  merge into a turret or hull too (select it last, or open its panel): they turn with it, and their inside counts as
  its inside. Mirror twins: when the target and every merged add-on have twins, the twins merge on the other side too;
  into a centre part (turret, hull), each add-on brings its twin, mirrored into place. An add-on the game shows on both
  sides from one part (placed with Mirror on) brings its other side along the same way. Unmirrored add-ons can't merge
  into a mirrored part whose twin is flipped (the flipped side would stop being mirrored and show wrong): merge them
  into an unmirrored part instead.
- **Cut with this add-on (Boolean cut):** place an add-on through a plate, and its panel offers **Cut hole** or
  **Cut pocket (with walls)** out of the structure it sits on (or out of the other selected parts). Any closed shape
  works, dents included. A pocket turns the add-on's surface inside the structure into plates with the add-on's
  armour. **Fill** chooses how the plate around the cut is rebuilt: **fewest points** (default) uses only the cut's
  own points and the face's corners, no new ones (like Blender's Boolean; triangles paired into quads where they
  fit), **rectangle box** surrounds the cut with a clean rectangular box (also toggled via the **Rectangle box** checkbox), **light fill** adds points inside the face
  until no face is long and thin (Delaunay refinement, as in Shewchuk's Triangle; for holes, notches and any outline),
  and **smooth fill** also a ring of quads along the cut's curved edge and evener faces. Flat neighbouring faces with the same armour are
  rebuilt together. Rivets move onto the new faces. Happens in place; Ctrl+Z undoes it. A mirrored twin of the add-on
  cuts too. A mirrored plate (a twin pair, or one part the game shows on both sides) shares one shape, so it's cut on
  both sides, as the game's own editing does, and stays mirrored; to cut one side only, unmirror it first. Where each
  part really shows is read from the game (parts on scaled mantlets or flipped parents included). The add-on must be a
  closed shape (an open plate has no inside to cut with, and is refused). The add-on is removed or kept
  (**Keep add-on**).
- **Gun length:** select a cannon, and its panel shows its length in calibers, **L/xx**, measured as guns are: from
  the muzzle to the face of the breech block. That's the barrel plus the chamber, which holds the whole round (in the
  game, the propellant plus a 3-caliber shell). The barrel alone and the round's length are listed below it, to 0.01 caliber; a muzzle brake isn't counted.
- **Speed & acceleration:** select a transmission or an engine, and its panel lists every gear's top speed (and
  reverse), the top speed with its rpm and track slip, the most power the engine gives below its rev limit, and the
  time from standing to 20, 40, 60... km/h on flat ground. It runs the game's own drivetrain maths step by step, as
  its physics jobs do: the engine's torque curve, rev limiter and inertia; the gearbox, its shift times and the
  game's driver (launch, upshifts, rev matching); the clutch; each track's sprocket, belt bending and drag; the
  belts' grip and slip on the ground; rolling resistance, part of which loads the belts; and the vehicle's mass. All
  of it is read from the design, so no test drive is needed. Against recorded test drives the top speed is within
  0.1% and each gear's acceleration within about 3%; the first 10-20 km/h read a little slow, as the tank's rocking at
  launch isn't modelled. A gear marked "short of the rev limit" can't reach it on flat ground.
- **Hole quality:** in a hand-made structure's panel, choose how many segments and how big the game's Create Hole tool
  makes a hole. Holes come out round, stay inside their face, and no longer leave faces inside out. **Hole fill:
  fewest points** (default) joins the hole's ring to the face's corners with no new points; **light fill** adds points
  so no face is long and thin, **smooth fill** a ring of quads round the hole and evener faces, and **game's fan** gets
  the game's own fill back. The circle sits square to the part (flat top, mirror twins match).
- **Merge faces:** in a hand-made structure, select faces in Faces edit mode and press **Merge selected faces**. Faces
  that share edges become as few faces as their outline allows: two triangles become a quad, a strip of quads one quad,
  a fan a handful of quads. Points inside, and points along straight sides, go. A side point an unselected face also
  uses is where a line runs on into that face; **Points other faces use** decides: **take out their lines** (default)
  rebuilds the faces along each line without it too, up to a real corner or the plate's edge, so everything stays
  joined; **run past them** works like the game's Delete + Fill (the other face keeps the point, not joined there, so
  moving it later opens a gap); **keep as corners** leaves them as corners. The selection splits into flat
  patches at bends over 20°, and a straight line of points two patches share across a corner goes from both. It's one
  of the game's own mesh edits, so Ctrl+Z undoes it. With the editor's **Mirror** on, the faces mirroring your
  selection merge too (matched by position, the way the game's Mirror matches points), so a mirrored hull stays even.

- **Separate** (Blender's P): in a hand-made structure (add-on, hull or turret), select faces in edit mode, or the
  points around them, and press **Separate selected into a new add-on**. The faces leave the part and become a new
  add-on in the same place, with their thickness, thickening, armour and rivets. With **Mirror** on the mirrored faces
  go too; a mirrored part's twin (or the side the game mirrors) gives up the same faces to a twin of the new add-on.
  From an add-on it happens in place as one of the game's own steps (the game copies the part, the copy keeps the
  selected faces), checked against the planned design afterwards, and **Ctrl+Z** undoes it. From a hull or turret (or
  if the check finds any difference) the design reloads instead, and **Restore** undoes it.
  **Separate picked pieces**: for a shape already in pieces that don't touch, click one face on a piece (Shift-click
  more pieces) and each whole piece becomes its own add-on. Pick every piece and the biggest stays on the part.

- **Mesh tools** (Blender-style, in a hand-made structure's **Mesh tools** section and on keys; each is one of the
  game's own mesh edits, so Ctrl+Z undoes it, and each follows the editor's Mirror). Each is checked before it changes
  anything: if the result would leave a crack, a face turned over or squashed flat, or faces laid over each other, it
  isn't done and the editor says why (Merge faces too). Rivets on the faces a tool rebuilds stay: each goes onto the new
  face under it (the game would drop them with the old faces); Create Hole keeps those round the hole too:
  - **Flatten (P):** selected points onto their best-fit plane, or to one height, side or length position.
  - **Loop cut (T):** in Edges mode, a loop through the ring of quads a selected edge crosses, to the plate's edge,
    a triangle, or all the way round.
  - **Inset (I):** the selected faces shrink inward by the width set in the panel, with a ring of faces around them.
  - **Bevel (V):** in Edges mode, the selected edges become chamfer strips; where three meet, a cap closes the corner,
    and where a bevel ends inside the plate the strip runs to the point there (no hole left).
    Recreated surrounding sides inherit their own sharp/connection settings; new chamfer edges do not copy
    unrelated or temporary edge flags. Existing edges are reused without changing their settings.
  - **Select linked flat faces (U):** grows the selection over faces lying flat with it (angle in the panel).
  - **Smooth Edge:** in Edges mode, rounds selected edges using **Smooth width (mm)** and **Smooth segments**
    (2–16, default 4). Uses a curved profile rather than dividing a flat chamfer; neighbouring faces share its
    new vertices and multi-edge corners are closed. New round strips start without sharp/connection flags
    inherited from the removed edges. Bevel (V) still makes its original single chamfer.
  - **Fillet:** in Edges mode, rounds selected edges with a circular curve tangent to both adjoining faces.
    **Fillet radius (mm)** sets the actual curve radius and **Fillet segments** (2–16, default 4) sets its
    number of strips. Uses Mirror and one undo operation, preserves surrounding edge settings and source-corner
    thickness, and rejects a radius that cannot fit instead of shrinking it. Adjacent faces must be flat;
    unsupported junctions are rejected. Smooth Edge keeps its separate cutback-width controls.
  - **Split selected faces:** in Faces mode, select the face(s) to split. **Split sections** (2–16, default 2)
    makes equal straight strips; **Split direction: A/B** chooses the pair of sides on a quad. Cuts stop at
    selected-face boundaries. Neighbours remain single faces with matching border points, without extra lines
    across them. Triangles receive an even triangular grid. Mirror applies; disable it to affect one side only.
    **Select between splits** selects all faces made inside the last split on this structure, excluding neighbours.
    A changed or undone split must be redone or split again before selection. Shared border points, edge settings
    and interpolated corner thickness are preserved. Both tools are in **Mesh tools**, use normal undo,
    and reject invalid geometry before changing the mesh.
  - **Proportional editing (O):** moving, scaling or rotating points pulls the points around them too, less the
    further away, up to the radius in the panel. Cancelling, Ctrl+Z and redo take the followers along.
  - **0.5 mm grid:** snapping (hold Ctrl while moving) uses 0.5 mm instead of the game's smallest, 1 mm.
  - **Orthographic view (Numpad 5):** no perspective, and the spawn pad (its deck and the blocks and rails beside the
    tracks) and ground under the vehicle are hidden (back when you leave it). Scroll zooms as usual (sized by the camera's orbit distance,
    never the ground), and **Numpad + / −** or the **Ortho zoom** slider zoom further, past the game's closest
    distance. With **Ortho: whole view** (on by default) the camera never cuts into the vehicle when zoomed in.
    **Ortho backdrop** (click to change): **plain grey** (default), **white** or **black** draws the vehicle alone on
    one colour, no sky and no map (the map's edge, hills and walls neither in front nor behind), or **scene** as it is.
    With **Ortho: straight views** (on by default) it snaps to front, back, sides or top, and orbiting flips between
    them; **Numpad 1 / 3 / 7** jump to front, side and top (with **Ctrl**: back, the other side, and from below), and **Numpad 9** flips to the opposite view (top to below,
    front to back). In a straight view, **Ortho: measurements** (on by default) draws the vehicle's overall size as
    dimension lines: across the screen under it, up the screen beside it, to the centimetre (antennas not counted).
    The game's fog is off in orthographic view, and the move / turn / scale arrows keep a normal size.
    Gizmo clicks also reach the pulled-back orthographic camera's full distance, so visible rotation rings can
    be dragged on selected points and edges. **D** selects the rotation rings; **R** rotates directly (default keys).
- **Bridge** (the **Bridge and circle** section; Blender's Bridge Edge Loops): in Edges mode, select the open edges of two
  plates (or two loops of edges, for a tube) and press **Bridge**: a strip of faces joins them. **Bridge cuts** sets
  how many rows of points go across it; **Bridge smooth** 0 goes straight across, 100 leaves each plate the way it runs
  and curves round into the other (a rounded corner between a wall and a floor, say). Both chains need as many points;
  the strip pairs them the shortest way round, so it doesn't twist. With Mirror on, the other side is bridged too.
- **Circle** (LoopTools' Circle): select points round a loop (a hole's rim, a ring round a turret) and press **Circle**:
  they spread evenly round a true circle, on their best plane, as far out as they are on average. For a rounder, higher-
  poly circle, loop cut first to add points, then Circle. With Mirror on, the other side follows (a loop round the
  middle stays exactly mirrored).
- **Fix mirror** (the **Mirror fixes** section): the editor's Mirror only pairs points that match to a fraction of a
  millimetre, so after a merge or split on one side only, or a few nudges, it silently stops moving both sides. Fix
  mirror makes points that are nearly each other's mirror image (within the distance set, 5 mm by default) exact again,
  puts points that near the middle onto it, and selects any point left with no mirror image, to show where the two
  sides really differ. Choose which side is right (the right, +x, or the left), or let both meet halfway. Works on the
  selected points, or the whole shape if none are selected.
- **Rotation snap angle:** **Rotation snap (°)** in Mesh tools sets the step turns snap to while the game's rotation
  snap is on, for points in a hand-made structure and for whole parts: 7.5° makes a 48-sided circle, 5° a 72-sided
  one, 3.75° a 96-sided one. 0 keeps the game's own step; the game's rotation snap key still turns snapping off.
- **Merge (M) both sides with Mirror** (on by default, in Mirror fixes): the game's Merge ignores Mirror, so you had
  to merge each side by hand and the two sides drifted apart. Now, with Mirror on, merging points also merges their
  mirror images on the other side, the game's own merge run again in the same step (Ctrl+Z undoes both), with the
  merged point put exactly opposite the first so Mirror keeps pairing them.
- **Exploded view (F2):** the running gear (tracks, road wheels, sprockets, idlers, suspension) stays on the ground and
  the hull lifts off it; every other part moves away from the part it's on, straight up or down or outward on the
  level, and parts on parts go further; **F3 / F4** bring them closer or further apart (the spread is remembered). F2 again
  puts everything back. Only what's drawn moves: whenever the design is saved or read, the parts go back first.
- **Shadows off / on (F5):** turns off every light's shadows and back on, exactly as they were. While they're off, a
  shadowless headlight (the sun's colour, 60% of its strength) points wherever the camera looks, so the sides turned
  away from the sun aren't black.
- **Flashlight (F6):** a spotlight from the camera that points wherever the mouse points, putting 80% of the sun's
  light on whatever it lands on, near or far (in orthographic view it shines from a normal viewing distance). F6
  again turns it off. **Flashlight (% of sun)** in Mesh tools sets how bright (5 to 300%, remembered).
- **Fullbright (F7):** shadows off and even light from all six sides and eight corners (14 lights; brightness in
  Mesh tools, **Fullbright (% of sun, each light)**), so every face shows clearly. F7 again puts the
  lighting back.
- **Drawing sheet (F9):** saves a technical drawing of the vehicle, laid out like a maker's drawing: from above and
  from the front on top, from the side and from the back below, all at one scale, on white, each view named. Measured
  like a drawing: the overall length and height beside the side view, the width beside the view from above and under
  the front view (to the centimetre), and a 1 m ruler with ticks every 10 cm. Antennas are left off, so the height is
  the vehicle's own. Under the drawing, a title block: the vehicle's name, its guns (the name you gave each, caliber
  and length in calibers, how many of each), its engine's horsepower and top speed forward and in reverse, and its
  description, in three columns. Three pictures: **lines only** (black on white), **colour** (the paint and decals,
  lit evenly from every side, with the same lines over it) and **see-through** (half the colour picture, half the
  vehicle with its armour off: the armour as glass). Colour uses **clean outlines** around visible parts, depth steps and sharp corners, suppressing flat
  triangle seams, shallow facets and isolated speckles. These thin dark-grey contours preserve detail throughout
  the vehicle without a mesh overlay. See-through keeps only the vehicle silhouette by default.
  In **Drawing sheet (F9)**, turn off **Colour outlines** or **Interior outline** to restore that export's wireframe, or enable
  **No wireframe** to omit all added lines from both coloured exports. These choices are remembered between
  sessions and leave the separate line drawing unchanged.
  **Intensity (%)** adjusts geometry-line strength from 0 to 100 on all sheets, without fading
  paint, dimensions or labels. **Gun elevation** adds dashed barrel positions and angle labels
  in the side view; **Gun traverse** independently shows the laying drive's left/right limits in the top
  view, useful for casemates. These use the configured laying-drive limits and the principal barrel on each mount,
  keep the vehicle intact, and leave space around the views for the movement. Missing/unreadable barrel meshes are
  skipped and logged. **Blue blueprint** saves an extra `(blueprint).png` with light lines on blue paper.
  **Turret rotation** separately draws ring-centred rotation using the traverse motor's Min and Max settings,
  including asymmetric limits. Full rotation gets one 360-degree arc; a locked turret gets none. These controls
  live in their own foldout on structure, turret-ring and cannon panels, with short labels and detailed tooltips.
  Select a cannon and turn off **This gun's limits** in **Drawing sheet (F9)** to omit only its movement-limit
  lines and angle labels from all F9 sheets. Its geometry, decals and weapon specifications stay visible.
  On shared mounts, the largest enabled gun supplies the annotations; disable all guns on that mount to hide them all.
  Choices are remembered per design name and gun ID in the mod settings. Renaming a design starts a separate selection;
  copies with the same name and gun IDs share the choices. The global movement toggles must also be enabled.
  Each gun uses only its nearest trunnions: a nested turret or gun cannot supply annotations for an outer mount.
  The vehicle's weight in tonnes appears in the same bold header row as its name on every sheet.
  **Blueprint grid** adds optional square background lines to the blue export, with **Grid strength (%)** controlling
  their visibility. Minor squares follow 0.25 m at drawing scale; every fourth line is stronger (1 m). The grid is
  drawn under the white vehicle lines and text. Enable **Blue blueprint** as well; other exports keep their backgrounds.
  The lines come from the vehicle's own shapes: where faces meet at an angle, open edges and
  the outline of curved parts, with what's hidden behind other parts left out; each view is outlined from its picture
  too, so every part has its outline. Saved as PNG in `Documents\My Games\Sprocket\Photos`. Takes a few seconds.
- **OBJ export / import (F10):** opens a separate menu in the vehicle editor, also available from a structure's
  **OBJ export / import** foldout. Choose whole categories or individual parts; search and paging keep long lists
  manageable. Defaults export the tank's exterior, tracks, weapons, fittings, baskets, antennas, external fuel tanks
  and decals. Engines, powertrain, transmissions, ammunition, internal fuel tanks, crew, gunner sights, turret
  traverse motors and laying drives are unchecked. **All**, **None**, **Defaults** and **Use editor selection**
  provide quick choices. Export writes an OBJ, material file and texture folder together; keep these files together.
  Geometry uses a shared tank origin, metres and Y-up, preserving each part's position, rotation, size and mirrors.
  Turn off exploded view with F2 first. Existing exports are never overwritten.
  **Import model** saves OBJ geometry as an editable plate-structure blueprint, with optional scale and whole-mm
  plate thickness. Choose the faction and blueprint name; the destination is
  `Documents\My Games\Sprocket\Factions\<faction>\Blueprints\Plate Structures`. OBJ objects become disconnected
  pieces of one editable structure, keeping their relative positions. Add it to a tank from the game's
  plate-structure library. Existing files
  and the open tank are preserved. Import restores geometry; it does not rebuild textures, working tracks, guns,
  engines or crew from an OBJ. Offline geometry and format checks do not replace in-game menu and reload testing.
- **Max-quality photo (F8 in photo mode):** takes a photo with every graphics quality setting at its best, without
  leaving photo mode: the settings go up and the photo mode overlay hides for about a second, then both come back as
  they were. Resolution, anti-aliasing type and looks (vignette, film grain, depth of field on or off) stay yours.
  The finished photo is saved as opaque RGB: exhaust and other transparent effects keep their rendered colour
  without leaking the render target's alpha into the PNG and being blended again by image viewers.
  Saved as PNG in `Documents\My Games\Sprocket\Photos`.
- **Own paint (per part):** a structure's panel has **Paint: vehicle**; click it to give the part (and the other
  selected parts) its own paint job, "Own paint 1", "Own paint 2" and so on (up to 9), using spare paint slots the game
  has but doesn't use. A new one starts as a copy of the Primary paint; its colour, saturation, roughness, metallic,
  condition, grime, camo and camo scale are right there in the part's panel. Ctrl+Z undoes each change (a slider drag
  is one step).
  Only the part's outside changes. It's saved with the design (which parts use it is kept in the paint job).
- **Zoom in close:** the camera can come within a few centimetres of small parts (on by default, in Mesh tools).
- **Copy and mirror turrets:** the game's turret ring part says it can't be duplicated or mirrored; the mod lets it,
  in memory. Alt copies a turret, and a copied ring brings everything on it (turret body, guns and the rest), each on
  the copy of its parent. With Mirror on, a turret gets a mirrored twin on the other side like other parts; the game
  mirrors only the ring, so once you let go the mod mirrors the turret body, guns and everything else on it onto the
  twin (the design reloads once; Restore undoes it). The twin body shares the original's shape, so later edits to it
  apply to both sides. Each mirrored ring references its own traverse motor, including nested turrets.
  For an existing pair sharing or crossing drive connections, select a turret ring or traverse motor and press
  **Fix mirrored turret drives**. It reconnects each ring to its own attached motor, reloads an unsaved result and
  keeps a backup (Restore undoes it). Missing or ambiguous motors and unrelated external connections are left alone.
- **Hotkeys:** while a hand-made structure is selected, a box beside the panel lists the mesh editing keys,
  including the ones the game's hint bar leaves out: **B** box select (then drag), **C** circle select, **A** select
  all / none, **E** extrude, **J** split, **M** merge points, **H** slope, **X / Y / Z** lock to an axis, and more. The
  keys are read from the game's live bindings, so rebound keys show as rebound. **×** closes the box, **F1** shows or
  hides it, and it remembers which.
- **Keys and settings:** every Quality of Life key can be changed in the game's **Settings > Mod Options** tab (Tool
  keys, Editing keys) or the **F11** window: click a key, press the new one. Its **Settings** page holds the options
  that have no panel of their own.
- **Move or scale without height:** while moving or scaling, **Shift + the vertical-lock key (Z)** locks to the two
  flat axes, so a scale keeps the height. Shift + X or Shift + Y leaves out that axis instead. These are the game's
  own two-axis locks, which it has no key for; the axis key alone goes back to the one-axis lock.

Every Quality of Life section in a panel folds away: click its header. It stays folded, even after a restart
(saved in `BepInEx\config\local.sprocket.qol.cfg`).

Merge and Cut happen in place as one of the game's own undoable steps. Turret to Add-on saves the design, changes it
and reloads it, because it changes a part's type, which the game only does by rebuilding. Every edit backs up the design
before and after in `BepInEx\SprocketQoLBackups\` (the newest 50 are kept; `Backups kept` in the mod's config changes
that, 0 keeps them all); after a reload the part it produced gets a **Restore design before last edit** button.

## Round add-on parts (no BepInEx needed)

`DataMods\Round Add-on Parts\` adds **Round add-on (16 sides)** and **Round add-on (32 sides)** to the editor's
**Addon Structures** palette, next to the default cube. They're the cube's size (0.25 m, 5 mm plate), placed the
same way, then reshaped with the structure tools. They're plain part files, so they work on any Sprocket without a
loader: add the folder as a mod in Mod Manager, or copy its `Sprocket_Data` folder into your Sprocket folder.
To change the shapes, edit and rerun `tools\RoundAddonParts`. Keep the GUIDs so saved tanks that use them still load.

## Build and install a mod

From the kit folder in PowerShell:

```powershell
.\tools\deploy.ps1 SprocketQoL
```

- Your Sprocket isn't in the Steam default folder: add `-GameDir "D:\Games\Sprocket"`.
- `dotnet` isn't on your PATH: add `-Dotnet "C:\path\to\dotnet.exe"`.
- If Sprocket is running, the script waits for you to close it, because the game locks mod DLLs.

## Start a new mod

1. Copy `MyFirstMod` to a new folder, e.g. `MyTankMod`, and rename the `.csproj` to `MyTankMod.csproj`.
2. In the `.csproj`, set `<AssemblyName>` to your mod's name. In `Plugin.cs`, change the id, name and version in
   `[BepInPlugin(...)]`, since every mod needs a unique id.
3. `.\tools\deploy.ps1 MyTankMod`, start Sprocket, and look for your log line in `BepInEx\LogOutput.log`.

## Find the game's classes

```powershell
dotnet run --project tools\ApiLister -- "C:\...\Sprocket\BepInEx\interop" ~Turret             # classes whose name contains "Turret"
dotnet run --project tools\ApiLister -- "C:\...\Sprocket\BepInEx\interop" TurretRingEditor    # one class's fields and methods
```

Classes appear twice, as `Sprocket.X` and `Il2CppSprocket.X`. Use the `Sprocket.X` one.

## Sprocket classes the Quality of Life mod uses

- `Sprocket.VehicleDesigner.VehicleDesignerCore`: the editor. Find it with `Object.FindObjectOfType`.
  `HasEditor` and `editorState == EditorState.Running` mean a vehicle is open. `Target` is the vehicle,
  `DesignIOPossible` and `Editor.OperationInProgress` say whether it's safe to load, and
  `Load(blueprint, CancellationToken.None)` replaces the design and returns a task.
- `Sprocket.Vehicles.VehicleBlueprintSerializer`: `ToBlueprint(vehicle)`, `SerializeToJSON(blueprint, true)`,
  `DeserializeJSON(json)`. These convert between the open design and the same JSON as `.blueprint` files.
- **Adding to the right-hand panels:** a Harmony postfix on a part editor's `OnGUI(IGUILayout layout)`, e.g.
  `Sprocket.Vehicles.Turrets.Editor.TurretRingEditor` or
  `Sprocket.Vehicles.PlateStructures.Design.PlateStructureEditor` or `Sprocket.Vehicles.Cannons.Editor.CannonEditor`
  (its `Component.Blueprint` has `Caliber`, `BarrelLength` and `BoreLength` in mm). Draw with
  `layout.TryCast<Sprocket.UI.IGUIElementDrawer>()`: `Header`, `InfoField`,
  `Button(label, UnityAction, ref UITooltip)`, `ToggleField(label, value, Action<bool>, tooltip)`. Make callbacks with
  `DelegateSupport.ConvertDelegate<UnityAction>`. `layout.BeginDropdown(title, expanded, Action<bool>)` starts a
  section the player can fold away. The panel only redraws when asked: call the editor's `RequestRedraw()` after a
  click that changes what a button says, and give `InfoField` exactly as many lines as the text has (use `\n`).
- **Parts:** every part has a `VUID` (`VehiclePart.VUID`), the same number as `vuid` in blueprint files.
  From a part editor, use `Component.VehicleObject` for the part, `Component.VehicleTransform.Parent` for its parent,
  and `VehicleObject.GUID` for its part type.

Lessons learned:
- **Keep heavy work out of `OnGUI`:** don't load or convert anything inside `OnGUI` or a panel hook. Queue it and run
  it from a MonoBehaviour's `Update`.
- **Never let an exception escape a panel hook:** wrap its body in `try/catch`, because an exception thrown back into
  the game's drawing code can crash it.
- **Clicks reach the game too:** a click on an IMGUI overlay also goes to the editor behind it. Buttons drawn in the
  game's own panels don't have that problem.

## Blueprint files in one paragraph

`.blueprint` files are JSON: `header` (name, mass, version), `objects` (parts: `guid` = part type from
`Sprocket_Data\StreamingAssets\Parts\*.json`, `vuid`/`pvuid` = id and parent id, `transform` = pos, Unity Euler
rot plus a 4th "placement spin" value, and scale), `blueprints` (settings blocks referenced by `...BlueprintVuid`) and
`meshes`. Old saves have no `objects` and need re-saving in the current game first. Turret ring part:
`99281776-6b29-4ffb-9d8b-04139ca7b6a2`. Compartment structure: `7f8a9d20-eb45-482e-b149-014c964c4e2c`. Add-on
structure: `8f8a9d20-eb45-482e-b149-014c964c4e2c`.

## License

MIT. See [LICENSE](LICENSE). You can use, change and share this code, including in your own mods, as long as you keep the copyright notice and license text.
