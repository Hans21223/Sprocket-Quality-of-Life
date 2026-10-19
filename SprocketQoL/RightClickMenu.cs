using HarmonyLib;
using Sprocket.Gameplay;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;
using GameInputState = Sprocket.Controls.InputState;

namespace SprocketQoL;

/// A right-click menu in the vehicle editor, like Blender's: Ctrl (Mod Options can change it, and leave items out) + a right
/// click that doesn't drag (a drag turns the camera) opens it at the mouse with what fits the selection, the game's own
/// actions and QoL's tools, each with its key. Click an
/// item to run it; Esc, another right click or a click elsewhere closes it. The game's actions run as if their key was
/// tapped: its editor asks VehicleEditorInputHandler whether a key went down, and the hooks below answer yes for the one
/// picked, so the game does exactly what its key does.
[HarmonyPatch]
public static class RightClickMenu
{
    sealed record Item(string Label, string Key, Action Run);
    sealed record Placed(Rect Rect, Item? Item, string? Title);

    static List<Placed>? placed; // the open menu: section titles and items where they're drawn; null when closed
    static Rect box;
    static Vector2? pressedAt;
    static float pressedTime;
    static GUIStyle? labelStyle, keyStyle, titleStyle;
    const float Row = 22, Width = 290;

    internal static bool Open => placed != null;
    internal static void Close() { placed = null; pressedAt = null; }

    /// From DesignEditor.Update while the editor runs: opens on Ctrl (or what Mod Options sets) + a right click that didn't
    /// drag, runs a clicked item.
    internal static void Update()
    {
        if (Mouse.current is not { } mouse) return;
        var p = mouse.position.ReadValue();
        var at = new Vector2(p.x, Screen.height - p.y);
        if (placed != null)
        {
            InputShield.Claim(); // the game's mouse controls wait while the menu is up
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || mouse.rightButton.wasPressedThisFrame) { Close(); return; }
            if (!mouse.leftButton.wasPressedThisFrame) return;
            var item = placed.FirstOrDefault(x => x.Item != null && x.Rect.Contains(at))?.Item;
            Close();
            if (item == null) return;
            Plugin.ModLog.LogInfo($"Right-click menu: {item.Label}");
            item.Run();
            return;
        }
        if (mouse.rightButton.wasPressedThisFrame) { pressedAt = Held() ? at : null; pressedTime = Time.unscaledTime; }
        if (pressedAt is not { } from || !mouse.rightButton.wasReleasedThisFrame) return;
        pressedAt = null;
        bool click = (at - from).magnitude < 6 && Time.unscaledTime - pressedTime < 0.35f;
        // Not over the game's panels, not during a move, turn or scale (a right click may cancel it), not while typing.
        if (!click || Time.frameCount - Hotkeys.TransformFrame <= 2 || MeshTools.Typing()
            || UnityEngine.EventSystems.EventSystem.current?.IsPointerOverGameObject() == true) return;
        if (Build() is { Count: > 0 } menu) Place(menu, at); // every item left out: no menu
    }

    // ---------- what opens it, what it shows (Mod Options) ----------

    /// Held with the right click that opens the menu.
    internal static readonly string[] Holds = { "Ctrl", "Alt", "Shift", "Off" };

    static bool Held() => Keyboard.current is { } keys && (Plugin.MenuOpens?.Value ?? "Ctrl") switch
    {
        "Alt" => keys.altKey.isPressed,
        "Shift" => keys.shiftKey.isPressed,
        "Off" => false,
        _ => keys.ctrlKey.isPressed,
    };

    /// Items left out of the menu, as "Section/Item".
    static HashSet<string> Hidden() => new((Plugin.MenuHidden?.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries));
    internal static bool Shows(string section, string label) => !Hidden().Contains(section + "/" + label);
    internal static void Show(string section, string label, bool on)
    {
        if (Plugin.MenuHidden == null) return;
        var hidden = Hidden();
        if (on) hidden.Remove(section + "/" + label); else hidden.Add(section + "/" + label);
        Plugin.MenuHidden.Value = string.Join("|", hidden);
    }

    /// Every item by section, in menu order.
    internal static IEnumerable<IGrouping<string, string>> Sections => Entries.GroupBy(e => e.Section, e => e.Label);

    // ---------- what the menu offers ----------

    /// What the items act on: the selection when the menu opened. Shape: a hand-made shape whose part is selected (its
    /// mesh can be edited).
    sealed record Selection(DesignEditor Editor, List<int> Picked, int Active, List<int> Addons, List<int> Others, PlateStructureEditor? Shape);
    /// An item: its section, name, key, when it fits the selection and what it does.
    sealed record Entry(string Section, string Label, Func<string> Key, Func<Selection, bool> When, Action<Selection> Run);

    static string G(string action) => Hotkeys.GameKey(action);
    static string K(string id) => Keybinds.Shown(id) is var key && key != "(none)" ? key : "";
    static string None() => "";
    static bool Part(Selection s) => s.Picked.Count > 0;
    static bool Addon(Selection s) => s.Picked.Count > 0 && s.Addons.Contains(s.Active);
    static bool Shape(Selection s) => s.Shape != null;
    static bool Always(Selection s) => true;

    static readonly Entry[] Entries =
    {
        new("Object", "Duplicate", () => G("Core/Duplicate"), Part, _ => Tap("Duplicate")),
        new("Object", "Flip", () => G("Core/Flip"), Part, _ => Tap("TransformFlip")),
        new("Object", "Mirror (symmetry) on / off", () => G("Core/Mirror"), Part, _ => Tap("Symmetry")),
        new("Object", "Delete", () => G("MeshEdit/Delete"), Part, _ => Tap("Delete")),
        new("Object", "Parent to active (last selected)", None, s => s.Picked.Count > 1, s => s.Editor.ParentTo(s.Active, s.Others)),
        new("Object", "Scatter other add-ons over this", None, s => Part(s) && s.Addons.Any(v => v != s.Active),
            s => ShapeTools.Scatter(s.Active, s.Addons.Where(v => v != s.Active).ToList())),
        new("Object", "Set origin to the shape's middle", None, s => s.Picked.Count == 1 && Shape(s), s =>
            s.Editor.RequestLiveEdit("Setting the origin", "Origin moved to the middle of the shape.", json => AddonEdits.PlanOrigin(json, s.Active))),
        new("Add-on", "Merge selected add-ons into this", () => K("join"), s => Addon(s) && s.Addons.Count > 1, _ => Keybinds.Press("join")),
        new("Add-on", "Cut through-hole", None, Addon, s => ShapeTools.Cut(s.Active, s.Others, pocket: false)),
        new("Add-on", "Cut recess (walls and floor)", None, Addon, s => ShapeTools.Cut(s.Active, s.Others, pocket: true)),
        new("Add-on", "Keep only the overlap", None, Addon, s => ShapeTools.Cut(s.Active, s.Others, pocket: false, keepInside: true)),
        new("Add-on", "Join into one shape (union)", None, Addon, s => ShapeTools.Union(s.Active, s.Others)),
        new("Add-on", "Slice in two", None, Addon, s => ShapeTools.Slice(s.Active, s.Others)),
        new("Add-on", "Drape as cloth", None, Addon, s => ShapeTools.Drape(s.Active)),
        new("Add-on", "Make the mirror side a real add-on", None, Addon, s => ShapeTools.RealMirror(s.Active)),
        new("Mesh", "Extrude", () => G("MeshEdit/Extend"), Shape, _ => Tap("Extrude")),
        new("Mesh", "Fill", () => G("MeshEdit/Fill"), Shape, _ => Tap("Fill")),
        new("Mesh", "Merge points", () => G("MeshEdit/Merge"), Shape, _ => Tap("Merge")),
        new("Mesh", "Split", () => G("MeshEdit/Split"), Shape, _ => Tap("Split")),
        new("Mesh", "Dissolve", () => G("MeshEdit/Dissolve"), Shape, _ => Tap("Dissolve")),
        new("Mesh", "Slope", () => G("MeshEdit/Slope"), Shape, _ => Tap("Slope")),
        new("Mesh", "Flip faces", () => G("MeshEdit/Flip"), Shape, _ => Tap("Flip")),
        new("Mesh", "Duplicate", () => G("MeshEdit/Duplicate"), Shape, _ => Tap("MeshDuplicate")),
        new("Mesh", "Delete", () => G("MeshEdit/Delete"), Shape, _ => Tap("Delete")),
        new("Mesh", "Select all / none", () => G("MeshEdit/ToggleSelection"), Shape, _ => Tap("SelectAll")),
        new("Mesh", "Invert selection", () => G("MeshEdit/InvertSelection"), Shape, _ => Tap("SelectInverse")),
        new("Mesh", "Select linked", () => G("MeshEdit/SelectLinked"), Shape, _ => Tap("SelectLinked")),
        new("Mesh tools (QoL)", "Flatten", () => K("flatten"), Shape, _ => Keybinds.Press("flatten")),
        new("Mesh tools (QoL)", "Loop cut", () => K("loopCut"), Shape, _ => Keybinds.Press("loopCut")),
        new("Mesh tools (QoL)", "Inset", () => K("inset"), Shape, _ => Keybinds.Press("inset")),
        new("Mesh tools (QoL)", "Bevel", () => K("bevel"), Shape, _ => Keybinds.Press("bevel")),
        new("Mesh tools (QoL)", "Smooth edge", None, Shape, s => MeshTools.SmoothEdge(s.Shape!)),
        new("Mesh tools (QoL)", "Fillet", None, Shape, s => MeshTools.Fillet(s.Shape!)),
        new("Mesh tools (QoL)", "Bridge selected edges", None, Shape, s => MeshTools.Bridge(s.Shape!)),
        new("Mesh tools (QoL)", "Circle selected points", None, Shape, s => MeshTools.Circle(s.Shape!)),
        new("Mesh tools (QoL)", "Select linked flat faces", () => K("selectFlat"), Shape, _ => Keybinds.Press("selectFlat")),
        new("Mesh tools (QoL)", "Proportional editing on / off", () => K("proportional"), Shape, _ => Keybinds.Press("proportional")),
        new("Mesh tools (QoL)", "Fix mirror", None, Shape, s => MeshTools.FixMirror(s.Shape!)),
        new("View", "Front", () => K("front"), Always, _ => Keybinds.Press("front")),
        new("View", "Side", () => K("side"), Always, _ => Keybinds.Press("side")),
        new("View", "Top", () => K("top"), Always, _ => Keybinds.Press("top")),
        new("View", "Orthographic on / off", () => K("ortho"), Always, _ => Keybinds.Press("ortho")),
        new("View", "Exploded view", () => K("explode"), Always, _ => Keybinds.Press("explode")),
        new("View", "Shadows on / off", () => K("shadows"), Always, _ => Keybinds.Press("shadows")),
        new("View", "Fullbright on / off", () => K("fullbright"), Always, _ => Keybinds.Press("fullbright")),
        new("View", "Drawing sheet", () => K("drawing"), Always, _ => Keybinds.Press("drawing")),
        new("View", "OBJ export / import", () => K("obj"), Always, _ => Keybinds.Press("obj")),
    };

    static List<(string Title, List<Item> Items)> Build()
    {
        if (DesignEditor.Instance is not { } editor) return new();
        var picked = editor.SelectedParts();
        int active = picked.Count > 0 ? picked[^1] : -1;
        var s = new Selection(editor, picked, active, editor.SelectedParts(Conversion.AddonGuid), picked.Where(v => v != active).ToList(), Hotkeys.Current);
        var hidden = Hidden();
        return Entries.Where(e => !hidden.Contains(e.Section + "/" + e.Label) && e.When(s)).GroupBy(e => e.Section)
            .Select(g => (g.Key, g.Select(e => new Item(e.Label, e.Key(), () => e.Run(s))).ToList())).ToList();
    }

    /// Lays the sections out down from the mouse, a new column when the next section wouldn't fit, all kept on screen.
    static void Place(List<(string Title, List<Item> Items)> menu, Vector2 at)
    {
        var rows = new List<Placed>();
        float x = 0, y = 0, bottom = 0, limit = Math.Max(Row * 4, Screen.height - 16);
        foreach (var (title, items) in menu)
        {
            if (y > 0 && y + Row * (items.Count + 1) > limit) { x += Width; y = 0; }
            rows.Add(new(new Rect(x, y, Width, Row), null, title)); y += Row;
            foreach (var item in items) { rows.Add(new(new Rect(x, y, Width, Row), item, null)); y += Row; }
            bottom = Math.Max(bottom, y);
        }
        float width = x + Width, left = Math.Clamp(at.x, 0, Math.Max(0, Screen.width - width)), top = Math.Clamp(at.y, 0, Math.Max(0, Screen.height - bottom));
        box = new Rect(left, top, width, bottom);
        placed = rows.Select(r => r with { Rect = new Rect(r.Rect.x + left, r.Rect.y + top, r.Rect.width, r.Rect.height) }).ToList();
    }

    /// From DesignEditor.OnGUI.
    internal static void Draw()
    {
        if (placed == null) return;
        labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        if (keyStyle == null) { keyStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleRight }; keyStyle.normal.textColor = new Color(0.68f, 0.68f, 0.68f); }
        titleStyle ??= new GUIStyle(labelStyle) { fontStyle = FontStyle.Bold };
        GUI.Box(box, ""); GUI.Box(box, ""); // twice: the skin's box is see-through
        var m = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        var mouse = new Vector2(m.x, Screen.height - m.y);
        foreach (var row in placed)
        {
            var r = row.Rect;
            if (row.Title != null) { GUI.Label(new Rect(r.x + 8, r.y, r.width - 16, r.height), row.Title, titleStyle); continue; }
            if (r.Contains(mouse)) GUI.Box(r, "");
            GUI.Label(new Rect(r.x + 18, r.y, r.width - 28, r.height), row.Item!.Label, labelStyle);
            if (row.Item.Key.Length > 0) GUI.Label(new Rect(r.x + 18, r.y, r.width - 28, r.height), row.Item.Key, keyStyle);
        }
    }

    // ---------- the game's actions, tapped ----------

    static (string Action, int Frame)? tap;

    /// The game's action as if its key was tapped next frame: down that frame, up the one after.
    static void Tap(string action) => tap = (action, Time.frameCount + 1);

    static void Answer(string action, GameInputState state, ref bool result)
    {
        if (tap is not { } t || t.Action != action) return;
        int now = Time.frameCount;
        if (now == t.Frame && (state & GameInputState.Pressed) != 0 || now == t.Frame + 1 && (state & GameInputState.Released) != 0) result = true;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Duplicate))]
    static void Duplicate(GameInputState __0, ref bool __result) => Answer("Duplicate", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Sprocket_VehicleDesigner_Access_IMeshEditInputProvider_Duplicate))]
    static void MeshDuplicate(GameInputState __0, ref bool __result) => Answer("MeshDuplicate", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Sprocket_VehicleDesigner_Access_ITransformInputProvider_Flip))]
    static void TransformFlip(GameInputState __0, ref bool __result) => Answer("TransformFlip", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Flip))]
    static void Flip(GameInputState __0, ref bool __result) => Answer("Flip", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Delete))]
    static void Delete(GameInputState __0, ref bool __result) => Answer("Delete", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Symmetry))]
    static void Symmetry(GameInputState __0, ref bool __result) => Answer("Symmetry", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Extrude))]
    static void Extrude(GameInputState __0, ref bool __result) => Answer("Extrude", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Fill))]
    static void Fill(GameInputState __0, ref bool __result) => Answer("Fill", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Merge))]
    static void Merge(GameInputState __0, ref bool __result) => Answer("Merge", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Split))]
    static void Split(GameInputState __0, ref bool __result) => Answer("Split", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Dissolve))]
    static void Dissolve(GameInputState __0, ref bool __result) => Answer("Dissolve", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.Slope))]
    static void Slope(GameInputState __0, ref bool __result) => Answer("Slope", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.SelectAll))]
    static void SelectAll(GameInputState __0, ref bool __result) => Answer("SelectAll", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.SelectInverse))]
    static void SelectInverse(GameInputState __0, ref bool __result) => Answer("SelectInverse", __0, ref __result);
    [HarmonyPostfix, HarmonyPatch(typeof(VehicleEditorInputHandler), nameof(VehicleEditorInputHandler.SelectLinked))]
    static void SelectLinked(GameInputState __0, ref bool __result) => Answer("SelectLinked", __0, ref __result);
}
