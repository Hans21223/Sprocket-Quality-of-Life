using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.UI;
using UnityEngine.Events;

namespace SprocketQoL;

/// Quality of Life: small editor improvements, each one a section in the game's own inspector panels.
[BepInPlugin("local.sprocket.qol", "Quality of Life", "1.3.0")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource ModLog = null!;
    public override void Load()
    {
        ModLog = Log;
        AddComponent<DesignEditor>();
        var harmony = new Harmony("local.sprocket.qol");
        var features = new[] { typeof(InspectorSection), typeof(ShapeTools), typeof(RestoreSection), typeof(HoleQuality), typeof(MergeFaces), typeof(GunLength), typeof(GearSpeeds) };
        foreach (var feature in features)
        {
            try { harmony.PatchAll(feature); }
            catch (Exception ex) { Log.LogError($"{feature.Name} disabled, could not attach to the game: {ex}"); }
        }
        Log.LogInfo("Quality of Life loaded: Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Gun length, Speed & acceleration.");
    }
}

internal static class Ui
{
    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        try { draw(); }
        catch (Exception ex) { Plugin.ModLog.LogError($"{feature}: {ex}"); }
    }

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    static readonly HashSet<string> closed = new();

    /// Starts a top-level section the player can fold away (the game's own dropdown); it stays folded this session.
    internal static void Section(IGUILayout layout, string title)
    {
        layout.EndAllDropdowns();
        layout.BeginDropdown(title, !closed.Contains(title), BoolCallback(open => { if (open) closed.Remove(title); else closed.Add(title); }));
    }
}
