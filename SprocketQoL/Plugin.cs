using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.UI;
using UnityEngine.Events;

namespace SprocketQoL;

/// Quality of Life: small editor improvements, each one a section in the game's own inspector panels.
[BepInPlugin("local.sprocket.qol", "Quality of Life", "1.7.0")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource ModLog = null!;
    internal static ConfigEntry<string>? Folded;
    internal static ConfigEntry<bool>? ShowHotkeys;
    internal static ConfigEntry<float>? ExplodeSpread, FlashlightPercent, FullbrightPercent;
    internal static ConfigEntry<int>? BackupsKept;
    public override void Load()
    {
        ModLog = Log;
        Folded = Config.Bind("Panels", "Folded sections", "", "Quality of Life sections folded away in the editor panels, separated by |");
        ShowHotkeys = Config.Bind("Panels", "Show hotkeys box", true, "The Hotkeys box beside a hand-made structure's panel (F1 in the editor shows or hides it)");
        ExplodeSpread = Config.Bind("Panels", "Exploded view spread", 0.5f, "How far apart the exploded view (F2) moves parts, in metres (F3 / F4 change it)");
        FlashlightPercent = Config.Bind("Panels", "Flashlight brightness", 80f, "How bright the flashlight (F6) is where it lands, in percent of the sun");
        FullbrightPercent = Config.Bind("Panels", "Fullbright brightness", 25f, "How bright each of fullbright's (F7) 14 lights is, in percent of the sun");
        BackupsKept = Config.Bind("Backups", "Backups kept", 50, "How many design backups (BepInEx\\SprocketQoLBackups, one per edit) to keep; the oldest go first. 0 keeps them all.");
        AddComponent<DesignEditor>();
        var harmony = new Harmony("local.sprocket.qol");
        var features = new[] { typeof(InspectorSection), typeof(ShapeTools), typeof(RestoreSection), typeof(HoleQuality), typeof(MeshTools), typeof(MergeFaces), typeof(Hotkeys), typeof(TurretCopy), typeof(ExplodedView), typeof(GunLength), typeof(GearSpeeds), typeof(PartPaint), typeof(ImageAddresses) };
        foreach (var feature in features)
        {
            try { harmony.PatchAll(feature); }
            catch (Exception ex) { Log.LogError($"{feature.Name} disabled, could not attach to the game: {ex}"); }
        }
        Log.LogInfo("Quality of Life loaded: Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Mesh tools, Hotkeys, Turret copy, Exploded view, Gun length, Speed & acceleration, Max-quality photo, Own paint.");
    }
}

internal static class Ui
{
    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        try { draw(); }
        catch (Exception ex)
        {
            // The same error again (a GUI drawn every frame) is counted, not logged again: no wall of red.
            string text = ex.ToString();
            if (lastErrors.TryGetValue(feature, out var last) && last.Text == text)
            {
                lastErrors[feature] = (text, ++last.Count);
                if (last.Count % 1000 == 0) Plugin.ModLog.LogError($"{feature}: the same error again, {last.Count} times so far");
                return;
            }
            lastErrors[feature] = (text, 1);
            Plugin.ModLog.LogError($"{feature}: {ex}");
        }
    }

    static readonly Dictionary<string, (string Text, int Count)> lastErrors = new();

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    static HashSet<string>? closed;

    /// Starts a top-level section the player can fold away (the game's own dropdown). It stays folded, across restarts
    /// too (saved in the plugin's config file).
    internal static void Section(IGUILayout layout, string title)
    {
        closed ??= (Plugin.Folded?.Value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        layout.EndAllDropdowns();
        layout.BeginDropdown(title, !closed.Contains(title), BoolCallback(open =>
        {
            if (open ? !closed.Remove(title) : !closed.Add(title)) return;
            if (Plugin.Folded != null) Plugin.Folded.Value = string.Join("|", closed);
        }));
    }
}
