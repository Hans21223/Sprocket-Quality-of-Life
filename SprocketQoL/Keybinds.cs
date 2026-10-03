using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// Every Quality of Life key, changeable in the QoL settings window (F11, or the main menu's Quality of Life button)
/// and saved in the config file's [Keybinds] section as text: a key name such as F9, P or Numpad1, with Ctrl+, Shift+
/// or Alt+ in front if it needs them. Empty: no key.
internal static class Keybinds
{
    internal sealed record Bind(string Id, string Group, string Name, string Default);

    internal static readonly Bind[] All =
    {
        new("menu", "Quality of Life", "Settings and keys (this window)", "F11"),
        new("hotkeys", "Panels", "Show or hide the hotkeys box", "F1"),
        new("explode", "Spacing", "Exploded view", "F2"),
        new("explodeCloser", "Spacing", "Exploded view: closer", "F3"),
        new("explodeFurther", "Spacing", "Exploded view: further", "F4"),
        new("shadows", "Lighting", "Shadows", "F5"),
        new("flashlight", "Lighting", "Flashlight", "F6"),
        new("fullbright", "Lighting", "Fullbright", "F7"),
        new("photo", "Capture", "Max-quality photo (in photo mode)", "F8"),
        new("drawing", "Capture", "Drawing sheet", "F9"),
        new("obj", "Models", "OBJ export / import menu", "F10"),
        new("front", "View", "Front view (Ctrl: back)", "Numpad1"),
        new("side", "View", "Right side view (Ctrl: left)", "Numpad3"),
        new("top", "View", "Top view (Ctrl: from below)", "Numpad7"),
        new("opposite", "View", "The opposite view", "Numpad9"),
        new("ortho", "View", "Orthographic view", "Numpad5"),
        new("zoomIn", "View", "Orthographic zoom in", "NumpadPlus"),
        new("zoomOut", "View", "Orthographic zoom out", "NumpadMinus"),
        new("flatten", "Mesh", "Flatten", "P"),
        new("loopCut", "Mesh", "Loop cut", "T"),
        new("inset", "Mesh", "Inset", "I"),
        new("bevel", "Mesh", "Bevel", "V"),
        new("selectFlat", "Selection", "Select linked flat faces", "U"),
        new("proportional", "Selection", "Proportional editing", "O"),
        new("join", "Add-ons", "Merge into the last selected add-on", "Ctrl+J"),
    };

    static readonly Dictionary<string, ConfigEntry<string>> entries = new();
    static readonly Dictionary<string, (Key Key, bool Ctrl, bool Shift, bool Alt)> parsed = new();

    internal static void Load(ConfigFile config)
    {
        foreach (var b in All)
        {
            entries[b.Id] = config.Bind("Keybinds", b.Id, b.Default,
                $"{b.Group}: {b.Name}. A key such as F9, P or Numpad1, with Ctrl+, Shift+ or Alt+ in front if wanted; empty for none.");
            parsed[b.Id] = Parse(entries[b.Id].Value);
        }
    }

    /// "Ctrl+Shift+J" -> J with Ctrl and Shift; an unknown key name -> no key.
    internal static (Key Key, bool Ctrl, bool Shift, bool Alt) Parse(string? text)
    {
        bool ctrl = false, shift = false, alt = false;
        var key = Key.None;
        foreach (var part in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (Enum.TryParse<Key>(part, true, out var k)) key = k;
        }
        return (key, ctrl, shift, alt);
    }

    internal static string Format(Key key, bool ctrl, bool shift, bool alt) =>
        key == Key.None ? "" : $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{key}";

    /// The key as shown in panels and the hotkeys box ("Numpad 1", "Ctrl+J"), or "(none)".
    internal static string Shown(string id)
    {
        var text = entries.TryGetValue(id, out var e) ? e.Value : All.FirstOrDefault(b => b.Id == id)?.Default ?? "";
        if (string.IsNullOrWhiteSpace(text)) return "(none)";
        return text.Replace("NumpadPlus", "Numpad +").Replace("NumpadMinus", "Numpad -").Replace("Numpad", "Numpad ").Replace("Numpad  ", "Numpad ");
    }

    internal static void Set(string id, string text)
    {
        if (!entries.TryGetValue(id, out var e)) return;
        e.Value = text;
        parsed[id] = Parse(text);
    }

    internal static void Reset(string id) { if (All.FirstOrDefault(b => b.Id == id) is { } b) Set(id, b.Default); }

    /// The action's key went down this frame, with the modifiers it needs held (others may be held too: tools that
    /// read Ctrl as "the other way" check it themselves).
    internal static bool Pressed(string id)
    {
        if (!parsed.TryGetValue(id, out var k) || k.Key == Key.None || Keyboard.current is not { } keys) return false;
        try
        {
            if (!keys[k.Key].wasPressedThisFrame) return false;
        }
        catch (Exception) { return false; } // a key this keyboard doesn't have
        return (!k.Ctrl || keys.ctrlKey.isPressed) && (!k.Shift || keys.shiftKey.isPressed) && (!k.Alt || keys.altKey.isPressed);
    }

    /// Whether the action's own binding asks for Ctrl (then Ctrl can't also mean "the other way").
    internal static bool NeedsCtrl(string id) => parsed.TryGetValue(id, out var k) && k.Ctrl;

    internal enum Captured { Nothing, Cancelled, Cleared, Set }

    static readonly Key[] Modifiers = { Key.LeftCtrl, Key.RightCtrl, Key.LeftShift, Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LeftMeta, Key.RightMeta, Key.ContextMenu };

    /// Waiting for a new key for `id`: Esc cancels, Backspace leaves it without a key, any other key (with the Ctrl,
    /// Shift and Alt held) becomes its key.
    internal static Captured Capture(string id, Keyboard keys)
    {
        if (keys.escapeKey.wasPressedThisFrame) return Captured.Cancelled;
        if (keys.backspaceKey.wasPressedThisFrame) { Set(id, ""); return Captured.Cleared; }
        foreach (Key k in Enum.GetValues(typeof(Key)))
        {
            if (k == Key.None || Modifiers.Contains(k)) continue;
            bool down;
            try { down = keys[k].wasPressedThisFrame; } catch (Exception) { continue; } // a key this keyboard doesn't have
            if (!down) continue;
            Set(id, Format(k, keys.ctrlKey.isPressed, keys.shiftKey.isPressed, keys.altKey.isPressed));
            Plugin.ModLog.LogInfo($"Keys: {id} is now {Shown(id)}");
            return Captured.Set;
        }
        return Captured.Nothing;
    }

    /// The actions whose keys are also another's (none counted twice for "no key").
    internal static HashSet<string> Shared() =>
        All.GroupBy(b => Shown(b.Id)).Where(g => g.Key != "(none)" && g.Count() > 1).SelectMany(g => g.Select(b => b.Id)).ToHashSet();
}
