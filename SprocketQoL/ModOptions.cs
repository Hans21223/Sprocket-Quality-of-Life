using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.SettingConfiguration;
using Sprocket.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// "Mod Options": a tab of the game's own settings screen, after General ... Controls, drawn with the game's own
/// fields. The game makes a tab for each of its settings pages, so this is one more page (a class of the game's page
/// type, registered with the game's runtime): Quality of Life's keys (click one, press the new key) and its settings
/// that have no panel of their own. Changes are saved at once, so the screen's Apply / Cancel don't touch them.
public sealed class ModOptionsMenu : SettingsSubMenu
{
    public ModOptionsMenu(IntPtr pointer) : base(pointer) { }

    public ModOptionsMenu() : base(ClassInjector.DerivedConstructorPointer<ModOptionsMenu>())
    {
        ClassInjector.DerivedConstructorBody(this);
        MenuName = "Mod Options";
    }

    public override void DisplayGUI(IGUILayout layout) => Ui.Guard("Mod Options", () => ModOptions.Draw(this, layout));
    public override void Update() => Ui.Guard("Mod Options", () => ModOptions.Tick(this));
    public override void ClearGUI() => ModOptions.Stop();
    public override void Apply() { }
    public override void Cancel() => ModOptions.Stop();
}

[HarmonyPatch]
internal static class ModOptions
{
    static bool registered;
    static readonly List<ModOptionsMenu> pages = new();          // kept: the game holds them
    static readonly List<UnityAction> keep = new();              // the page's button callbacks, while it shows
    static readonly List<Il2CppSystem.Action<bool>> keepBools = new();
    static readonly List<Il2CppSystem.Action<float>> keepFloats = new();
    static string? capturing;                                    // the action waiting for its new key

    /// The page's class made known to the game's runtime (once, before the settings screen first opens).
    internal static void Register()
    {
        try { ClassInjector.RegisterTypeInIl2Cpp<ModOptionsMenu>(); registered = true; }
        catch (Exception ex) { Plugin.ModLog.LogError($"Mod Options: couldn't add a settings tab ({ex.Message}); the keys stay in the F11 window."); }
    }

    /// Before the settings screen makes its tabs: Mod Options added to its pages (once per screen).
    [HarmonyPrefix, HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SetupMenuButtons))]
    static void AddTab(SettingsMenu __instance) => Ui.Guard("Mod Options tab", () =>
    {
        var menus = __instance.subMenus;
        if (!registered || menus == null) return;
        for (int i = 0; i < menus.Length; i++) if (menus[i]?.TryCast<ModOptionsMenu>() != null) return;
        var page = new ModOptionsMenu();
        pages.Add(page);
        var more = new Il2CppReferenceArray<SettingsSubMenu>(menus.Length + 1);
        for (int i = 0; i < menus.Length; i++) more[i] = menus[i];
        more[menus.Length] = page;
        __instance.subMenus = more;
    });

    internal static void Stop() => capturing = null;

    /// Every frame while the page shows: a waiting key takes the next one pressed.
    internal static void Tick(ModOptionsMenu page)
    {
        if (capturing == null || Keyboard.current is not { } keys) return;
        if (Keybinds.Capture(capturing, keys) != Keybinds.Captured.Nothing) { capturing = null; page.GUIRepaint = true; }
    }

    internal static void Draw(ModOptionsMenu page, IGUILayout layout)
    {
        keep.Clear(); keepBools.Clear(); keepFloats.Clear(); keepInts.Clear();
        if (layout.TryCast<IGUIElementDrawer>() is not { } ui) return;
        UnityAction Act(Action a) { var c = Ui.Callback(a); keep.Add(c); return c; }
        void Redraw() => page.GUIRepaint = true;

        // Three short pages (the settings screen doesn't scroll, so a long page runs under its Accept / Apply buttons).
        ui.Header("Quality of Life");
        var pick = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(new Action<int>(i => { part = i; capturing = null; Redraw(); }))!;
        keepInts.Add(pick);
        ui.TabGroup(pick, part, Parts);
        if (part == 2) { Settings(ui); return; }

        ui.InfoField(capturing != null
            ? $"Press the new key for {Keybinds.All.First(b => b.Id == capturing).Name} (with Ctrl, Shift or Alt if wanted). Esc cancels, Backspace leaves it without a key."
            : "Click a key to change it, then press the new key. Saved at once. Red: two actions share a key.", 2);
        var shared = Keybinds.Shared();
        foreach (var group in Keybinds.All.GroupBy(b => b.Group).Where(g => EditingGroups.Contains(g.Key) == (part == 1)))
        {
            ui.Header(group.Key);
            ui.ColumnCount = 2;
            foreach (var b in group)
            {
                string id = b.Id;
                bool clash = shared.Contains(id);
                ui.InfoField(b.Name, 1, clash ? new Color(1f, 0.5f, 0.45f) : new Color(0.85f, 0.85f, 0.82f));
                var tip = new UITooltip(b.Name, $"{b.Group}. Default: {(b.Default.Length == 0 ? "no key" : b.Default)}. Click, then press the new key.");
                ui.Button(id == capturing ? "press a key..." : Keybinds.Shown(id) + (clash ? "  (also used)" : ""), Act(() => { capturing = id; Redraw(); }), ref tip);
            }
            ui.ColumnCount = 1;
        }
        var resetTip = new UITooltip("Reset every key", "Puts every Quality of Life key back to its default.");
        ui.Button("Reset every key to its default", Act(() => { foreach (var b in Keybinds.All) Keybinds.Reset(b.Id); capturing = null; Redraw(); }), ref resetTip);
    }

    static int part;
    static readonly string[] Parts = { "Tool keys", "Editing keys", "Settings" };
    static readonly HashSet<string> EditingGroups = new() { "View", "Mesh", "Selection", "Add-ons" };
    static readonly List<Il2CppSystem.Action<int>> keepInts = new();

    static void Settings(IGUIElementDrawer ui)
    {
        Toggle(ui, Plugin.ShowHotkeys, "Hotkeys box beside hand-made structures", "The box listing the editing keys (its key toggles it too).");
        Toggle(ui, Plugin.PartMassMarkers, "Part mass markers", "Each part's blue mass marker while the editor's COM view filter is on.");
        Toggle(ui, Plugin.MirrorMerge, "Mirror merge", "With the editor's Mirror on, Merge (M) merges the mirrored points too.");
        Slider(ui, Plugin.RotationSnap, "Rotation snap (degrees, 0: the game's)", 0, 45, 0.5f);
        Slider(ui, Plugin.ExplodeSpread, "Exploded view spread (m)", 0.1f, 3, 0.05f);
        Slider(ui, Plugin.FlashlightPercent, "Flashlight brightness (%)", 5, 300, 5);
        Slider(ui, Plugin.FullbrightPercent, "Fullbright brightness (%)", 1, 100, 1);
        if (Plugin.BackupsKept is { } backups)
        {
            var change = Ui.FloatCallback(v => backups.Value = (int)MathF.Round(v));
            keepFloats.Add(change);
            ui.Slider("Design backups kept (0: all)", backups.Value, 0, 500, change);
        }
        Toggle(ui, Plugin.RecordDrives, "Record drives (diagnostics)", "Writes your vehicle's drivetrain, every physics step, to BepInEx\\SprocketQoL-drives while you drive.");
    }

    static void Toggle(IGUIElementDrawer ui, ConfigEntry<bool>? entry, string label, string tooltip)
    {
        if (entry == null) return;
        var change = Ui.BoolCallback(v => entry.Value = v);
        keepBools.Add(change);
        ui.ToggleField(label, entry.Value, change, tooltip);
    }

    static void Slider(IGUIElementDrawer ui, ConfigEntry<float>? entry, string label, float min, float max, float step)
    {
        if (entry == null) return;
        var change = Ui.FloatCallback(v => entry.Value = MathF.Round(v / step) * step);
        keepFloats.Add(change);
        ui.Slider(label, entry.Value, min, max, change);
    }
}
