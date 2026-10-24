using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// QoL's own windows (Workshop, tool search, part patterns) switch the editor's keys off while they're open. One window
/// holds them at a time; when it lets go, exactly the maps it switched off come back on, as long as the editor is still
/// open, even if the vehicle changed meanwhile (the maps outlive it; they used to stay off then). Leaving the editor,
/// the game sets its keys up afresh.
internal static class InputLease
{
    static readonly List<InputActionMap> off = new();
    static string? holder;

    internal static void Take(string owner)
    {
        if (holder != null && holder != owner) Release(holder); // the windows refuse to open over each other: just in case
        holder = owner;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>()))
        {
            var asset = o.TryCast<InputActionAsset>();
            if (asset?.FindActionMap("MeshEdit", false) == null) continue;
            foreach (string mapName in new[] { "Core", "MeshEdit", "Camera" })
                if (asset.FindActionMap(mapName, false) is { enabled: true } map && !off.Any(m => m.Pointer == map.Pointer)) { off.Add(map); map.Disable(); }
        }
    }

    internal static void Release(string owner)
    {
        if (holder != owner) return;
        if (DesignEditor.Instance?.IsReady == true)
            foreach (var map in off) try { map.Enable(); } catch (Exception ex) { Plugin.ModLog.LogWarning(owner + " input: " + ex.Message); }
        off.Clear();
        holder = null;
    }
}
