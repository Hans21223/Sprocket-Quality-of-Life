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
[BepInPlugin("local.sprocket.qol", "Quality of Life", "1.8.0")]
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource ModLog = null!;
    internal static ConfigEntry<string>? Folded;
    internal static ConfigEntry<bool>? ShowHotkeys;
    internal static ConfigEntry<bool>? DrawingNoWireframe;
    internal static ConfigEntry<bool>? DrawingSeeThroughOutline;
    internal static ConfigEntry<bool>? DrawingColourOutline;
    internal static ConfigEntry<float>? DrawingIntensity;
    internal static ConfigEntry<bool>? DrawingElevation, DrawingTraverse, DrawingBlue, DrawingTurretTraverse;
    internal static ConfigEntry<bool>? DrawingGrid;
    internal static ConfigEntry<float>? DrawingGridIntensity;
    internal static ConfigEntry<string>? DrawingHiddenGunLimits;
    internal static ConfigEntry<float>? ExplodeSpread, FlashlightPercent, FullbrightPercent;
    internal static ConfigEntry<int>? BackupsKept;
    internal static ConfigEntry<float>? RotationSnap;
    internal static ConfigEntry<bool>? MirrorMerge;
    public override void Load()
    {
        ModLog = Log;
        DrawingNoWireframe = Config.Bind("Drawing sheet", "No wireframe", false, "Omit the wireframe overlay from F9 colour and see-through exports. The separate lines-only drawing is unchanged.");
        DrawingSeeThroughOutline = Config.Bind("Drawing sheet", "See-through outline only", true, "Use only the vehicle silhouette over the see-through interior, instead of mesh edges. No wireframe disables this outline too.");
        DrawingColourOutline = Config.Bind("Drawing sheet", "Colour outline only", true, "Use clean visible part contours and sharp corners on the colour drawing, without mesh triangulation. No wireframe disables these outlines too.");
        DrawingIntensity = Config.Bind("Drawing sheet", "Wireframe intensity percent", 100f, "Strength of geometry lines on F9 sheets, from 0 to 100. Paint, dimensions and text keep their strength.");
        DrawingElevation = Config.Bind("Drawing sheet", "Gun elevation and depression", false, "Show dashed gun positions at the laying drive's elevation and depression limits in the side view. Keeps the vehicle intact.");
        DrawingTraverse = Config.Bind("Drawing sheet", "Gun traverse", false, "Show dashed gun positions at the laying drive's left and right traverse limits in the top view (including casemates). Keeps the vehicle intact.");
        DrawingTurretTraverse = Config.Bind("Drawing sheet", "Turret rotation", false, "Show turret rotation in the top view using the traverse motor minimum and maximum angles. Full rotation is shown as a single 360-degree arc.");
        DrawingBlue = Config.Bind("Drawing sheet", "Blue blueprint", false, "Also save a white-on-blue line drawing, with the same dimensions and gun movement options.");
        DrawingGrid = Config.Bind("Drawing sheet", "Blueprint grid", false, "Add a faint square background grid to the blue blueprint only. Minor squares are 0.25 m at the drawing scale, with a stronger line every metre.");
        DrawingGridIntensity = Config.Bind("Drawing sheet", "Grid intensity percent", 20f, "Strength of the blue blueprint background grid, from 0 to 100. Vehicle lines and text are drawn over it.");
        DrawingHiddenGunLimits = Config.Bind("Drawing sheet", "Hidden gun limits", "[]", "Guns excluded from movement-limit annotations only. Managed by This gun's limits in the cannon panel. Stored per design name and gun ID; renaming a design starts a separate selection.");
        Folded = Config.Bind("Panels", "Folded sections", "", "Quality of Life sections folded away in the editor panels, separated by |");
        ShowHotkeys = Config.Bind("Panels", "Show hotkeys box", true, "The Hotkeys box beside a hand-made structure's panel (F1 in the editor shows or hides it)");
        ExplodeSpread = Config.Bind("Panels", "Exploded view spread", 0.5f, "How far apart the exploded view (F2) moves parts, in metres (F3 / F4 change it)");
        FlashlightPercent = Config.Bind("Panels", "Flashlight brightness", 80f, "How bright the flashlight (F6) is where it lands, in percent of the sun");
        FullbrightPercent = Config.Bind("Panels", "Fullbright brightness", 25f, "How bright each of fullbright's (F7) 14 lights is, in percent of the sun");
        RotationSnap = Config.Bind("Editor", "Rotation snap (degrees)", 0f, "While the game's rotation snap is on, turns snap in steps of this many degrees instead of the game's own (7.5 makes a 48-sided circle, 5 a 72-sided one). 0 keeps the game's step.");
        MirrorMerge = Config.Bind("Editor", "Mirror merge", true, "With the editor's Mirror on, Merge (M) merges the mirrored points on the other side too.");
        BackupsKept = Config.Bind("Backups", "Backups kept", 50, "How many design backups (BepInEx\\SprocketQoLBackups, one per edit) to keep; the oldest go first. 0 keeps them all.");
        AddComponent<DesignEditor>();
        var harmony = new Harmony("local.sprocket.qol");
        var features = new[] { typeof(InspectorSection), typeof(TurretDriveRepair), typeof(ShapeTools), typeof(RestoreSection), typeof(HoleQuality), typeof(MeshTools), typeof(MergeFaces), typeof(Hotkeys), typeof(TurretCopy), typeof(ExplodedView), typeof(GunLength), typeof(GearSpeeds), typeof(PartPaint), typeof(ImageAddresses), typeof(DrawingSheet.NoHover), typeof(DrawingSettings), typeof(RotationSnap), typeof(MirrorMerge) };
        foreach (var feature in features)
        {
            try { harmony.PatchAll(feature); }
            catch (Exception ex) { Log.LogError($"{feature.Name} disabled, could not attach to the game: {ex}"); }
        }
        Log.LogInfo("Quality of Life loaded: Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Mesh tools, Hotkeys, Turret copy, Exploded view, Gun length, Speed & acceleration, Max-quality photo, Own paint, Bridge, Circle, Fix mirror, Mirror merge, Rotation snap.");
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
