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
        new[] { ("Core/Select", "select"), ("Core/ExtendSelect", "add to selection"), ("Core/GroupSelect", "group select"), ("MeshEdit/LoopSelect", "loop select") },
        new[] { ("MeshEdit/BoxSelect", "box select: drag"), ("MeshEdit/RadialSelect", "circle select: paint") },
        new[] { ("MeshEdit/ToggleSelection", "select all / none"), ("MeshEdit/InvertSelection", "invert") },
        new[] { ("Core/RotateMode", "rings: drag a ring"), ("Core/Rotate", "rotate directly") },
        new[] { ("Core/Move", "move"), ("Core/Resize", "scale"), ("Core/RotateSingleAxis", "turn on one axis") },
        new[] { ("Core/LateralConstraint", "sideways"), ("Core/LongitudinalConstraint", "lengthways"), ("Core/VerticalConstraint", "vertical") },
        new[] { ("Core/SnapModifier", "hold: snap"), ("Core/HighPrecision", "hold: fine"), ("Core/ToggleRotationSnap", "rotation snap") },
        new[] { ("MeshEdit/Extend", "extrude"), ("MeshEdit/Fill", "fill"), ("MeshEdit/Merge", "merge points"), ("MeshEdit/Split", "split") },
        new[] { ("MeshEdit/Slope", "slope"), ("MeshEdit/Duplicate", "duplicate"), ("MeshEdit/Flip", "flip faces") },
        new[] { ("MeshEdit/Delete", "delete"), ("Core/Undo", "undo"), ("Core/Redo", "redo") },
        new[] { ("Core/EnableExteriorView", "outside"), ("Core/EnableInteriorView", "inside"), ("Core/EnableArmourView", "armour view") },
        new[] { ("Core/Focus", "focus"), ("Core/CycleModule", "next tab"), ("Core/Save", "save") },
    };
    static readonly string[] GroupNames = { "Select", "Select", "Selection", "Rotate", "Transform", "Axis lock", "Snap", "Build", "Edit", "History", "View", "General" };

    static InputActionAsset? controls;
    static PlateStructureEditor? structure; // the hand-made structure whose panel was drawn last

    /// The hand-made structure being edited (its part selected), or null.
    internal static PlateStructureEditor? Current => StructureSelected() ? structure : null;
    static List<string>? lines;
    static float nextLines, nextFind;
    static int checkedFrame = -1;
    static bool visible;

    static (InputAction? Action, ConstraintMode Mode)[]? axisKeys;
    static IntPtr planeFor;          // the move or scale that Shift + axis locked to two axes
    static ConstraintMode planeMode;

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.Update))]
    static void PlaneLock(Transformation __instance) => Ui.Guard("Axis lock", () =>
    {
        EnsureControls();
        if (controls == null || __instance.input?.TryCast<WindingInput>() != null) return; // rotating: two axes mean nothing
        // The game's three axis keys, looked up once (this runs every frame of a move).
        axisKeys ??= new[] { ("Core/LateralConstraint", Constraints.Axis12), ("Core/VerticalConstraint", Constraints.Axis20), ("Core/LongitudinalConstraint", Constraints.Axis01) }
            .Select(k => ((InputAction?)controls.FindAction(k.Item1, false), k.Item2)).ToArray();
        bool shift = Keyboard.current?.shiftKey.isPressed == true;
        // Shift + an axis key: every axis but that one. The key alone: the game's own one-axis lock again.
        foreach (var (action, mode) in axisKeys)
            if (action?.WasPressedThisFrame() == true)
            {
                planeFor = shift ? __instance.Pointer : IntPtr.Zero;
                planeMode = mode;
            }
        // Kept every frame, in case the game's own handling of the same key press set its one-axis lock after us.
        if (planeFor == __instance.Pointer && __instance.ConstraintMode != planeMode) __instance.ConstraintMode = planeMode;
    });

    [HarmonyPostfix, HarmonyPatch(typeof(Transformation), nameof(Transformation.TransformEnd))]
    static void TransformDone() => planeFor = IntPtr.Zero;

    internal static void Seen(PlateStructureEditor __instance) => Ui.Guard("Hotkeys", () =>
    {
        if (__instance.TryCast<FreeformPlateStructureEditor>() == null) return; // mesh editing is freeform only
        if (structure?.Pointer != __instance.Pointer) checkedFrame = -1;
        structure = __instance;
    });

    static Rect closeRect; // where the × was drawn last (from the screen's top left); empty while the box is hidden
    static Rect boxRect, titleRect; // the whole box and its title bar (dragged to move the box), as drawn last
    static Vector2? dragAt;
    static readonly InputShield.Drag drag = new();
    static Rect previousPageRect, nextPageRect;
    static int hintPage, hintPageCount;
    static GUIStyle? hintStyle;
    static float[] lineHeights = Array.Empty<float>();
    static string[] displayedHints = Array.Empty<string>();
    static float hintWidth = -1;
    static float hintHeight = -1;

    internal static void LeftEditor()
    {
        structure = null; controls = null; axisKeys = null; lines = null;
        planeFor = IntPtr.Zero; checkedFrame = -1; visible = false;
        closeRect = default; nextFind = nextLines = 0;
        previousPageRect = nextPageRect = default; hintPage = hintPageCount = 0;
    }

    internal static void DrawBox()
    {
        if (!(Plugin.ShowHotkeys?.Value ?? true) || !StructureSelected()) { closeRect = previousPageRect = nextPageRect = boxRect = default; return; }
        bool rebuilt = lines == null || Time.unscaledTime >= nextLines;
        if (rebuilt) { lines = Lines(); nextLines = Time.unscaledTime + 1; }
        var hints = lines!;
        if (hints.Count == 0) { closeRect = previousPageRect = nextPageRect = boxRect = default; return; }
        var area = UiPresentation.PlaceHelp(Screen.width, Screen.height, Ui.InspectorBounds());
        // Dragged by its title bar: kept where it was put (on screen), not beside the panel.
        if ((dragAt ?? InputShield.Read(Plugin.ShortcutsPosition?.Value)) is { } at)
        {
            float left = Math.Clamp(at.x, 0, Math.Max(0, Screen.width - area.Width)), top = Math.Clamp(at.y, 0, Math.Max(0, Screen.height - 80));
            area = area with { X = left, Y = top, Height = Math.Max(80, Screen.height - top - 8) };
        }
        float width = area.Width;
        float contentLimit = Math.Max(18, area.Height - 58);
        var style = hintStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
        if (rebuilt || hintWidth != width || hintHeight != contentLimit)
        {
            hintWidth = width; hintHeight = contentLimit;
            float Height(string line) => Math.Max(18, style.CalcHeight(new GUIContent(line), Math.Max(1, width - 20)));
            displayedHints = hints.SelectMany(line => UiPresentation.FitHelp(line, text => Height(text) <= contentLimit)).ToArray();
            lineHeights = displayedHints.Select(Height).ToArray();
        }
        var pages = UiPresentation.HelpPages(lineHeights, contentLimit);
        hintPageCount = pages.Length;
        hintPage = Math.Clamp(hintPage, 0, hintPageCount - 1);
        var page = pages[hintPage];
        float contentHeight = 0;
        for (int i = page.Start; i < page.Start + page.Count; i++) contentHeight += lineHeights[i];
        var box = new Rect(area.X, area.Y, width, Math.Min(area.Height, (pages.Length > 1 ? 58 : 30) + contentHeight));
        GUI.Box(box, $"Shortcuts  ({Keybinds.Shown("hotkeys")} shows / hides, {Keybinds.Shown("menu")} changes keys; drag to move)");
        boxRect = box;
        titleRect = new Rect(box.x, box.y, box.width - 30, 24);
        closeRect = new Rect(box.xMax - 26, box.y + 3, 22, 20);
        GUI.Box(closeRect, "×");
        float y = box.y + 26;
        for (int i = page.Start; i < page.Start + page.Count; i++)
        {
            GUI.Label(new Rect(box.x + 10, y, Math.Max(1, width - 20), lineHeights[i]), displayedHints[i], hintStyle);
            y += lineHeights[i];
        }
        previousPageRect = nextPageRect = default;
        if (pages.Length > 1)
        {
            previousPageRect = new Rect(box.x + 10, box.yMax - 24, 24, 20);
            nextPageRect = new Rect(box.x + 38, box.yMax - 24, 24, 20);
            GUI.Box(previousPageRect, "‹"); GUI.Box(nextPageRect, "›");
            GUI.Label(new Rect(box.x + 72, box.yMax - 24, Math.Max(1, width - 82), 20), $"Page {hintPage + 1} / {pages.Length}", hintStyle);
        }
    }

    /// From DesignEditor.Update: F1 and a click on the ×, read from the keyboard and mouse themselves (the simple
    /// on-screen GUI's own key and button events don't reach mods in this game).
    internal static void Keys()
    {
        if (MeshTools.Typing() || DrawingSheet.Capturing || PhotoShot.Capturing) return;
        if (Keybinds.Pressed("hotkeys")) Show(!(Plugin.ShowHotkeys?.Value ?? true));
        // Over the box: the game's mouse controls wait. Its title bar drags it (remembered).
        if (boxRect.width > 0 && Mouse.current is { } over)
        {
            var mp = over.position.ReadValue();
            if (drag.Active || boxRect.Contains(new Vector2(mp.x, Screen.height - mp.y))) InputShield.Claim();
            dragAt = drag.Update(titleRect, boxRect.position, at => { if (Plugin.ShortcutsPosition != null) Plugin.ShortcutsPosition.Value = InputShield.Write(at); });
            if (drag.Active) return;
        }
        if (closeRect.width > 0 && Mouse.current is { } mouse && mouse.leftButton.wasPressedThisFrame)
        {
            var p = mouse.position.ReadValue();
            if (closeRect.Contains(new Vector2(p.x, Screen.height - p.y))) Show(false);
            else if (previousPageRect.Contains(new Vector2(p.x, Screen.height - p.y))) hintPage = (hintPage - 1 + hintPageCount) % hintPageCount;
            else if (nextPageRect.Contains(new Vector2(p.x, Screen.height - p.y))) hintPage = (hintPage + 1) % hintPageCount;
        }
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
        try { visible = structure != null && Selected(structure); }
        catch { visible = false; structure = null; } // its part is gone
        // No panel drawn for it (the game's panel can't be hooked): the structure editor the game has open.
        if (!visible && Time.unscaledTime >= nextActive)
        {
            nextActive = Time.unscaledTime + 0.5f;
            try { if (PanelSections.Active<FreeformPlateStructureEditor>() is { } open && Selected(open)) { structure = open; visible = true; } }
            catch { }
        }
        return visible;
    }

    static float nextActive;
    static bool Selected(PlateStructureEditor e) => DesignEditor.Instance?.SelectedParts().Contains((int)e.Component.VehicleObject.VUID) == true;

    static List<string> Lines()
    {
        var found = new List<string>();
        EnsureControls();
        if (controls == null) return found;
        for (int i=0;i<Groups.Length;i++)
        {
            var parts = new List<string>();
            foreach (var (name, does) in Groups[i])
                if (controls.FindAction(name, false) is { } action &&
                    InputActionRebindingExtensions.GetBindingDisplayString(action, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } key)
                    parts.Add($"{key.Replace("Control", "Ctrl")}  {does}");
            if (parts.Count > 0) found.Add(GroupNames[i]+": "+string.Join("   |   ", parts));
        }
        if (controls.FindAction("Core/VerticalConstraint", false) is { } vertical &&
            InputActionRebindingExtensions.GetBindingDisplayString(vertical, default(InputBinding.DisplayStringOptions), (string?)null) is { Length: > 0 } up)
            found.Insert(Math.Min(5, found.Count), $"Axis lock: Shift+{up}  move / scale at the same height");
        static string K(string id) => Keybinds.Shown(id);
        found.Add($"Add-ons: {K("join")}  merge into the last selected add-on");
        found.Add($"Mesh: {K("flatten")}  Flatten   |   {K("loopCut")}  Loop cut   |   {K("inset")}  Inset   |   {K("bevel")}  Bevel");
        found.Add($"Selection: {K("selectFlat")}  Linked flat faces   |   {K("proportional")}  Proportional editing");
        found.Add($"View: {K("ortho")}  Orthographic   |   {K("zoomIn")} / {K("zoomOut")}  Zoom");
        found.Add($"View: {K("front")} / {K("side")} / {K("top")}  Front / side / top   |   {K("opposite")}  Opposite");
        found.Add($"Spacing: {K("explode")}  Exploded view   |   {K("explodeCloser")} / {K("explodeFurther")}  Closer / further");
        found.Add($"Lighting: {K("shadows")}  Shadows   |   {K("flashlight")}  Flashlight   |   {K("fullbright")}  Fullbright");
        found.Add($"Capture: {K("photo")}  Photo (photo mode)   |   {K("drawing")}  Drawing sheet");
        found.Add($"Models: {K("obj")}  OBJ export / import menu   |   {K("menu")}  QoL settings and keys");
        return found;
    }

    /// The editor's control set (the game builds it from its DesignerControls layout).
    static void EnsureControls()
    {
        if (controls != null) return;
        axisKeys = null;
        if (Time.unscaledTime < nextFind) return;
        nextFind = Time.unscaledTime + 1;
        controls = Find();
    }

    static InputActionAsset? Find()
    {
        foreach (var o in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
            if (o.TryCast<InputActionAsset>() is { } asset && asset.FindActionMap("MeshEdit", false) != null) return asset;
        return null;
    }
}
