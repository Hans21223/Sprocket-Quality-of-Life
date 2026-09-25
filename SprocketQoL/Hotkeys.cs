using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.Transformations;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// A "Hotkeys" box beside the panel while a hand-made structure is selected: the mesh editing keys, including the
/// ones the game's hint bar leaves out (box select, circle select, extrude...). The keys come from the game's live
/// bindings, so a rebound key shows as rebound. × closes it, F1 shows or hides it; it remembers which.
/// Also: while moving or scaling, Shift + an axis key locks to the other two axes (Shift + vertical = keep height),
/// using the game's own two-axis constraints, which it has no key for.
[HarmonyPatch]
public static class Hotkeys
{
    // One line per group: actions in the game's DesignerControls ("map/action") and what they do.
    static readonly (string Action, string Does)[][] Groups =
    {
        new[] { ("Core/Select", "select"), ("Core/ExtendSelect", "add"), ("Core/GroupSelect", "group"), ("MeshEdit/LoopSelect", "loop") },
        new[] { ("MeshEdit/BoxSelect", "box select, drag"), ("MeshEdit/RadialSelect", "circle select, paint") },
        new[] { ("MeshEdit/ToggleSelection", "select all / none"), ("MeshEdit/InvertSelection", "invert") },
        new[] { ("Core/Move", "move"), ("Core/Rotate", "rotate"), ("Core/Resize", "scale"), ("Core/RotateSingleAxis", "turn on one axis") },
        new[] { ("Core/LateralConstraint", "lock sideways"), ("Core/LongitudinalConstraint", "lengthways"), ("Core/VerticalConstraint", "vertical") },
        new[] { ("Core/SnapModifier", "hold: snap"), ("Core/HighPrecision", "hold: fine"), ("Core/ToggleRotationSnap", "rotation snap") },
        new[] { ("MeshEdit/Extend", "extrude"), ("MeshEdit/Fill", "fill"), ("MeshEdit/Merge", "merge points"), ("MeshEdit/Split", "split") },
        new[] { ("MeshEdit/Slope", "slope"), ("MeshEdit/Duplicate", "duplicate"), ("MeshEdit/Flip", "flip faces") },
        new[] { ("MeshEdit/Delete", "delete"), ("Core/Undo", "undo"), ("Core/Redo", "redo") },
        new[] { ("Core/EnableExteriorView", "outside"), ("Core/EnableInteriorView", "inside"), ("Core/EnableArmourView", "armour view") },
        new[] { ("Core/Focus", "focus"), ("Core/CycleModule", "next tab"), ("Core/Save", "save") },
    };

    static InputActionAsset? controls;
    static PlateStructureEditor? structure; // the hand-made structure whose panel was drawn last

    /// The hand-made structure being edited (its part selected), or null.
    internal static PlateStructureEditor? Current => StructureSelected() ? structure : null;
    static List<string>? lines;
    static int checkedFrame = -1;
    static bool visible;

    static IntPtr planeFor;          // the move or scale that Shift + axis locked to two axes
    static ConstraintMode planeMode;

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.Update))]
    static void PlaneLock(Transformation __instance) => Ui.Guard("Axis lock", () =>
    {
        controls ??= Find();
        if (controls == null || __instance.input?.TryCast<WindingInput>() != null) return; // rotating: two axes mean nothing
        bool shift = Keyboard.current?.shiftKey.isPressed == true;
        // Shift + an axis key: every axis but that one. The key alone: the game's own one-axis lock again.
        foreach (var (key, mode) in new[] { ("Core/LateralConstraint", Constraints.Axis12), ("Core/VerticalConstraint", Constraints.Axis20), ("Core/LongitudinalConstraint", Constraints.Axis01) })
            if (controls.FindAction(key, false)?.WasPressedThisFrame() == true)
            {
                planeFor = shift ? __instance.Pointer : IntPtr.Zero;
                planeMode = mode;
            }
        // Kept every frame, in case the game's own handling of the same key press set its one-axis lock after us.
        if (planeFor == __instance.Pointer && __instance.ConstraintMode != planeMode) __instance.ConstraintMode = planeMode;
    });

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.TransformEnd))]
    static void TransformDone() => planeFor = IntPtr.Zero;

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Seen(PlateStructureEditor __instance) => Ui.Guard("Hotkeys", () =>
    {
        if (__instance.TryCast<FreeformPlateStructureEditor>() == null) return; // mesh editing is freeform only
        structure = __instance;
        lines = null; // read the keys again, in case they were rebound
    });

    internal static void DrawBox()
    {
        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F1) { Show(!(Plugin.ShowHotkeys?.Value ?? true)); e.Use(); }
        if (!(Plugin.ShowHotkeys?.Value ?? true) || !StructureSelected()) return;
        lines ??= Lines();
        if (lines.Count == 0) return;
        // Left of the game's panel (about the right 23% of the screen), under the top bar.
        const float width = 470, lineHeight = 20;
        var box = new Rect(Screen.width * 0.765f - width - 12, 90, width, 30 + lines.Count * lineHeight);
        GUI.Box(box, "Hotkeys  (F1 shows / hides)");
        if (GUI.Button(new Rect(box.xMax - 26, box.y + 3, 22, 20), "×")) Show(false);
        for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(box.x + 10, box.y + 26 + i * lineHeight, width - 20, lineHeight), lines[i]);
    }

    static void Show(bool show)
    {
        if (Plugin.ShowHotkeys != null) Plugin.ShowHotkeys.Value = show;
    }

    /// Is that structure still selected? Checked once a frame (the GUI is drawn several times a frame).
    static bool StructureSelected()
    {
        if (checkedFrame == Time.frameCount) return visible;
        checkedFrame = Time.frameCount;
        try { visible = structure != null && DesignEditor.Instance?.SelectedParts().Contains((int)structure.Component.VehicleObject.VUID) == true; }
        catch { visible = false; structure = null; } // its part is gone
        return visible;
    }

    static List<string> Lines()
    {
        var found = new List<string>();
        controls ??= Find();
        if (controls == null) return found;
        foreach (var group in Groups)
        {
            var parts = new List<string>();
            foreach (var (name, does) in group)
                if (controls.FindAction(name, false) is { } action &&
                    InputActionRebindingExtensions.GetBindingDisplayString(action, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } key)
                    parts.Add($"{key.Replace("Control", "Ctrl")}  {does}");
            if (parts.Count > 0) found.Add(string.Join("     ", parts));
        }
        if (controls.FindAction("Core/VerticalConstraint", false) is { } vertical &&
            InputActionRebindingExtensions.GetBindingDisplayString(vertical, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } up)
            found.Insert(5, $"Shift+{up}  move / scale without height   [mod]");
        found.Add("Ctrl+J  merge selected add-ons into the last one   [mod]");
        found.Add("P  flatten     T  loop cut (an edge)     I  inset     V  bevel (edges)   [mod]");
        found.Add("O  proportional editing     U  select linked flat faces   [mod]");
        found.Add("Numpad 5  orthographic view     Numpad + / -  zoom it   [mod]");
        found.Add("Numpad 1 / 3 / 7  front / side / top     Numpad 9  opposite (below, back...)   [mod]");
        found.Add("F2  exploded view     F3 / F4  closer / further apart   [mod]");
        found.Add("F5  shadows off / on     F6  flashlight at the mouse   [mod]");
        found.Add("F8 (photo mode)  photo at the best graphics settings   [mod]");
        return found;
    }

    /// The editor's control set (the game builds it from its DesignerControls layout).
    static InputActionAsset? Find()
    {
        foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
            if (o.TryCast<InputActionAsset>() is { } asset && asset.FindActionMap("MeshEdit", false) != null) return asset;
        return null;
    }
}
