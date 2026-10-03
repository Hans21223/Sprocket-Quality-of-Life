using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// Quality of Life keys in a window of its own: every QoL key, changed by clicking it and pressing the new one (Esc
/// cancels, Backspace leaves it without a key). Opened with its own key (F11 at first) anywhere; the same keys are in
/// the game's settings, Mod Options tab (ModOptions). The game's own controls are off while it's open.
internal static class QolMenu
{
    static GameObject? canvas, panel;
    static TMP_FontAsset? font;
    static readonly List<UnityAction> keep = new();
    static readonly Dictionary<string, TextMeshProUGUI> keyLabels = new();
    static TextMeshProUGUI? hint;
    static string? capturing;           // the action waiting for its new key
    static int closedAt = -10;
    static readonly List<InputAction> paused = new();
    static CursorLockMode lockBefore;
    static bool cursorBefore, rebuild;
    internal static bool IsOpen => canvas != null;

    // ---------- every frame (from DesignEditor.Update) ----------

    internal static void Update()
    {
        if (!IsOpen)
        {
            if (Time.frameCount > closedAt + 1 && Keybinds.Pressed("menu") && !MeshTools.Typing()) Open();
            return;
        }
        PauseGame();
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
        if (rebuild) { rebuild = false; Build(); }
        var keys = Keyboard.current;
        if (keys == null) return;
        if (capturing != null) { Capture(keys); return; }
        if (keys.escapeKey.wasPressedThisFrame || Keybinds.Pressed("menu")) Close();
    }

    static void Open() => Ui.Guard("QoL settings", () =>
    {
        if (IsOpen) return;
        font = NativeUi.Font();
        if (font == null) { Plugin.ModLog.LogWarning("QoL settings: the game's UI font isn't loaded yet"); return; }
        lockBefore = Cursor.lockState; cursorBefore = Cursor.visible;
        canvas = NativeUi.Canvas("QoL settings");
        capturing = null;
        PauseGame();
        Build();
    });

    static void Close()
    {
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        canvas = panel = null; keep.Clear(); keyLabels.Clear(); hint = null; capturing = null;
        foreach (var a in paused) try { a.Enable(); } catch (Exception) { }
        paused.Clear();
        Cursor.lockState = lockBefore; Cursor.visible = cursorBefore;
        closedAt = Time.frameCount; // the closing key or click must not also reach the game or reopen this
    }

    /// The game's controls off (the UI's own stay on, for the clicks); done every frame, as the game turns some back on.
    static void PauseGame()
    {
        var on = InputSystem.ListEnabledActions();
        for (int i = 0; i < on.Count; i++)
            if (on[i]?.actionMap?.name is not "UI") { on[i].Disable(); paused.Add(on[i]); }
    }

    static void Build()
    {
        if (panel != null) { panel.SetActive(false); UnityEngine.Object.Destroy(panel); }
        keep.Clear(); keyLabels.Clear();
        var rect = NativeUi.Node("QoL settings panel", canvas!.transform, 0, 0, 1000, 740);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        panel = rect.gameObject;
        panel.AddComponent<UnityEngine.UI.Image>().color = new Color(0.08f, 0.09f, 0.105f, 1);
        NativeUi.Label(font, panel.transform, "Quality of Life: keys", 22, 12, 600, 30, 24);
        NativeUi.Click(font, keep, panel.transform, "Reset all", 640, 12, 150, 32, () => { foreach (var b in Keybinds.All) Keybinds.Reset(b.Id); rebuild = true; });
        NativeUi.Click(font, keep, panel.transform, $"Close ({Keybinds.Shown("menu")} / Esc)", 800, 12, 180, 32, Close);
        hint = NativeUi.Label(font, panel.transform, "Click a key to change it, then press the new key (with Ctrl, Shift or Alt if wanted).", 22, 50, 960, 24, 15);
        float y = 80;
        foreach (var b in Keybinds.All)
        {
            NativeUi.Label(font, panel.transform, $"{b.Group}:  {b.Name}", 22, y + 3, 520, 22, 15);
            string id = b.Id;
            var key = NativeUi.Click(font, keep, panel.transform, "", 560, y, 220, 24, () => { capturing = id; Refresh(); });
            keyLabels[id] = key.GetComponentInChildren<TextMeshProUGUI>();
            NativeUi.Click(font, keep, panel.transform, "Default", 790, y, 90, 24, () => { Keybinds.Reset(id); Refresh(); });
            y += 25.5f;
        }
        Refresh();
    }

    /// Each key's text: its binding, "press a key..." while it's waiting, and a warning where two actions share one.
    static void Refresh()
    {
        var used = Keybinds.Shared();
        foreach (var (id, label) in keyLabels)
        {
            if (label == null) continue;
            label.text = id == capturing ? "press a key..." : Keybinds.Shown(id) + (used.Contains(id) ? "   (also used)" : "");
            label.color = id == capturing ? new Color(1f, 0.85f, 0.3f) : used.Contains(id) ? new Color(1f, 0.45f, 0.4f) : new Color(0.93f, 0.93f, 0.92f);
        }
        if (hint != null)
            hint.text = capturing != null ? "Press the new key (with Ctrl, Shift or Alt if wanted). Esc cancels, Backspace leaves it without a key."
                                          : "Click a key to change it, then press the new key. Saved at once (BepInEx\\config, [Keybinds]).";
    }

    /// The next key pressed becomes the waiting action's, with the modifiers held.
    static void Capture(Keyboard keys)
    {
        switch (Keybinds.Capture(capturing!, keys))
        {
            case Keybinds.Captured.Cancelled: case Keybinds.Captured.Cleared: capturing = null; Refresh(); break;
            case Keybinds.Captured.Set: capturing = null; rebuild = true; break; // the Close button names the settings key
        }
    }
}
