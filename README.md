# Sprocket Mod Kit

Everything needed to write BepInEx mods for **Sprocket** (IL2CPP, Unity 6), with a working example:
**Quality of Life**, which adds small tools to the vehicle editor's own panels.

## What you need

1. **.NET 8 SDK**: free from Microsoft, <https://dotnet.microsoft.com/download>.
2. **Sprocket with a BepInEx 6 IL2CPP loader that runs on your Sprocket version**, started once so it creates
   `BepInEx\interop` (the game's classes as .NET files that mods compile against).
   As of 2026-09-23 (Sprocket 0.2.55.5, Unity 6000.3.21), the official BepInEx 6 bleeding-edge build (be.788)
   freezes Sprocket at the main menu, so check that your loader actually reaches the menu before building mods.

The kit doesn't include the loader, the game's `BepInEx\interop` files, or any game code. Those come from your own
game install.

## What's in the kit

| Folder | What it is |
|---|---|
| `SprocketQoL\` | The Quality of Life mod (source), one file per feature |
| `SprocketQoL.Tests\` | Offline tests: Boolean cut, hole fill, face merge, and the turret conversion on your own blueprints |
| `DataMods\Round Add-on Parts\` | Round add-on palette parts; a data mod that needs no BepInEx |
| `MyFirstMod\` | A minimal mod to copy when starting a new one |
| `SprocketTweaks\`, `HelloMelon\` | MelonLoader-style mods: gameplay tweaks, and a check that MelonLoader mods run |
| `tools\RoundAddonParts\` | Regenerates the round add-on part files |
| `tools\ApiLister\` | Lists the game's classes and methods from `BepInEx\interop` (reads files only) |
| `tools\deploy.ps1` | Builds a mod and copies it into `Sprocket\BepInEx\plugins` |
| `SprocketMod.props` | Shared build settings: target framework and where your Sprocket folder is |
| `Release\BepInEx\plugins\` | The built Quality of Life mod and its user guide, ready to use (also on the Releases page as a zip) |

## Just want the Quality of Life mod?

Download the zip from the [Releases page](https://github.com/Hans21223/Sprocket-Quality-of-Life/releases) and add it
in Sprocket Mod Manager, or copy `Release\BepInEx\plugins\SprocketQoL.dll` into `Sprocket\BepInEx\plugins\`. If you had the older
`Sprocket.TurretAddon.dll`, delete it, because Quality of Life includes it. In the vehicle editor:

- **Turret to Add-on:** select a turret ring or turret body, and its panel gets a **Convert to add-on** button.
  Select several turrets and it converts them all at once.
- **Merge add-ons:** select two or more add-ons. The panel of the one you clicked offers to merge the others into
  it, keeping their shape, position and armour. Parts attached to them move onto it. Happens in place; Ctrl+Z undoes it.
  **Ctrl+J** does the same as Blender's join: all selected add-ons merge into the last one you selected.
- **Cut with this add-on (Boolean cut):** place an add-on through a plate, and its panel offers **Cut hole** or
  **Cut pocket (with walls)** out of the structure it sits on (or out of the other selected parts). Any closed shape
  works, dents included. A pocket turns the add-on's surface inside the structure into plates with the add-on's
  armour. Around each hole you get a ring of quads hugging the rim, then rings stepping out to the face's corners, so
  the result is easy to edit (flat neighbouring faces with the same armour are rebuilt together). Rivets move onto
  the new faces. Happens in place; Ctrl+Z undoes it. A mirrored twin of the add-on cuts too; the add-on is removed
  or kept (**Keep add-on**), and **Smooth fill** adds more, evener rings.
- **Gun length:** select a cannon, and its panel shows the barrel and bore length in calibers (L/xx).
- **Speed & acceleration:** select a transmission or an engine, and its panel lists every gear's top speed (and
  reverse), capped by the tracks' speed limit, and how many seconds the vehicle takes from standing to top speed on
  flat ground. That counts the engine's power curve, the vehicle's mass, the engine and sprockets spinning up, gear
  changes (by gearbox type), drag, and the tracks' rolling resistance when the game has it set up.
- **Hole quality:** in a hand-made structure's panel, choose how many segments and how big the game's Create Hole tool
  makes a hole. Holes come out round, stay inside their face, and no longer leave faces inside out. **Hole fill:
  light rings** (default) puts one ring of points between the hole and the face's corners, the same easy-to-edit way
  as the Boolean cut, instead of the game's fan of long thin triangles; **smooth rings** adds more, evener rings and
  **game's fan** gets the game's own fill back.
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

- **Hotkeys:** while a hand-made structure is selected, a box beside the panel lists the mesh editing keys,
  including the ones the game's hint bar leaves out: **B** box select (then drag), **C** circle select, **A** select
  all / none, **E** extrude, **J** split, **M** merge points, **H** slope, **X / Y / Z** lock to an axis, and more. The
  keys are read from the game's live bindings, so rebound keys show as rebound. **×** closes the box, **F1** shows or
  hides it, and it remembers which.
- **Move or scale without height:** while moving or scaling, **Shift + the vertical-lock key (Z)** locks to the two
  flat axes, so a scale keeps the height. Shift + X or Shift + Y leaves out that axis instead. These are the game's
  own two-axis locks, which it has no key for; the axis key alone goes back to the one-axis lock.

Every Quality of Life section in a panel folds away: click its header. It stays folded, even after a restart
(saved in `BepInEx\config\local.sprocket.qol.cfg`).

Merge and Cut happen in place as one of the game's own undoable steps. Turret to Add-on saves the design, changes it
and reloads it, because it changes a part's type, which the game only does by rebuilding. Every edit backs up the design
before and after in `BepInEx\SprocketQoLBackups\`; after a reload the part it produced gets a **Restore design before
last edit** button.

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
