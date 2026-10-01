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
